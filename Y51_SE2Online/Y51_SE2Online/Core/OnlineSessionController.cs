using System;

namespace Y51_SE2Online.Core;

public enum OnlineSessionState { Offline, Stopping, ReturningToMenu, Hosting, Joining, Failed }

public interface IOnlineRuntime
{
    bool HasWorld { get; }
    bool IsTransitioning { get; }
    bool NetworkingStopped { get; }
    void StopNetworking();
    void ReturnToMenu();
    void ResetMode();
    void StartMode(MpOptions options);
}

public sealed class OnlineSessionController
{
    private readonly IOnlineRuntime _runtime;
    private MpOptions _target;
    private bool _returnToMenu;
    private bool _worldExiting;
    private long _requestedAt;
    private long _lastTick;

    public OnlineSessionState State { get; private set; }
    public MpOptions Current { get; private set; }
    public string Error { get; private set; }
    public bool Busy => State == OnlineSessionState.Stopping || State == OnlineSessionState.ReturningToMenu;

    public OnlineSessionController(IOnlineRuntime runtime) => _runtime = runtime;

    public void Request(MpOptions target, long now)
    {
        if (Busy) throw new InvalidOperationException("A multiplayer change is already in progress.");
        _target = target;
        _worldExiting = false;
        _returnToMenu = Current?.Join == true || target?.Join == true;
        _requestedAt = _lastTick = now;
        Error = null;
        State = OnlineSessionState.Stopping;
        _runtime.StopNetworking();
    }

    public void WorldExiting(long now)
    {
        if (_worldExiting || (Current == null && !Busy)) return;
        _target = null;
        _returnToMenu = false;
        _worldExiting = true;
        _requestedAt = _lastTick = now;
        Error = null;
        State = OnlineSessionState.Stopping;
        _runtime.StopNetworking();
    }

    public void Tick(long now)
    {
        if (!Busy) return;
        // A server frame that stalls for a minute while a big world finishes loading is not the old session failing to close.
        if (now - _lastTick > 1000) _requestedAt += now - _lastTick;
        _lastTick = now;
        try
        {
            if (!_worldExiting && now - _requestedAt > 20000) throw new InvalidOperationException("The previous session is still closing. Wait for the game to finish loading or closing, then try again.");
            if (!_runtime.NetworkingStopped || _runtime.IsTransitioning) return;
            if (_worldExiting && _runtime.HasWorld) return;
            if (_returnToMenu && _runtime.HasWorld)
            {
                if (State != OnlineSessionState.ReturningToMenu)
                {
                    State = OnlineSessionState.ReturningToMenu;
                    _runtime.ReturnToMenu();
                }
                return;
            }
            _runtime.ResetMode();
            _worldExiting = false;
            Current = null;
            if (_target == null) { State = OnlineSessionState.Offline; return; }
            _runtime.StartMode(_target);
            Current = _target;
            State = Current.Host ? OnlineSessionState.Hosting : OnlineSessionState.Joining;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            State = OnlineSessionState.Failed;
        }
    }
}
