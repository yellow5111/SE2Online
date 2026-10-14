using System;
using System.Collections.Generic;
using Keen.VRage.Core;
using Keen.VRage.Core.Input;
using Keen.VRage.Core.Platform;
using Keen.VRage.DCS.Components;
using Keen.VRage.Input;
using Keen.VRage.Input.EngineComponents;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Text boxes only get characters from the platform's input method editor (WM_CHAR on Windows). A platform layer with
/// none (SE2 natively on Linux) leaves every text box untypeable, so this editor types from the game's keyboard
/// state instead, and stays silent as soon as the platform types anything itself.
/// </summary>
internal sealed class KeyboardTextInput : IInputMethodEditor
{
    // A key's own character waits this long for the platform's text, so a working platform never gets it twice.
    private const long PlatformWaitMs = 150;

    private static KeyboardTextInput _instance;
    private static bool _failed;

    private readonly Dictionary<int, long> _nextRepeat = new();
    private readonly List<(char Char, long At)> _pending = new();
    private bool _platformTypes, _platformSilent;
    private int _typedBatches;

    public string Name => "Y51_SE2Online keyboard text";

    public event IMETextDelegate TextEntered;

    internal static void Tick()
    {
        if (_failed) return;
        try
        {
            if (_instance == null)
            {
                if (!InputDeviceManager.HasValue) return;
                var input = Singleton<VRageCore>.Instance.Engine.Get<InputEngineComponent>();
                var platform = input.Platform.DefaultInputMethodEditor;
                var instance = new KeyboardTextInput { _platformSilent = platform is NullInputMethodEditor };
                if (!instance._platformSilent) platform.TextEntered += (_, _) => instance._platformTypes = true;
                input.RegisterInputMethod(instance);
                _instance = instance;
                GameLog.Instance.Info($"keyboard text: platform input method {platform.Name}; " +
                    (instance._platformSilent ? "text boxes type from the game's keyboard" : "keyboard typing stands by until the platform types nothing"));
            }
            _instance.Update(MpClock.NowMs());
        }
        catch (Exception ex)
        {
            _failed = true;
            GameLog.Instance.Warn("keyboard text: unavailable (" + ex.GetType().Name + ": " + ex.Message + ")");
        }
    }

    private void Update(long now)
    {
        if (_platformTypes)
        {
            if (_pending.Count > 0 || _nextRepeat.Count > 0) { _pending.Clear(); _nextRepeat.Clear(); }
            return;
        }
        bool active = GameKeys.OwnWindowInFront() && !GameKeys.IsDown(0x11) && !GameKeys.IsDown(0x12);
        bool shift = GameKeys.IsDown(0x10);
        foreach (int vk in KeyText.PrintableKeys)
        {
            if (!active || !GameKeys.IsDown(vk)) { _nextRepeat.Remove(vk); continue; }
            if (_nextRepeat.TryGetValue(vk, out long next))
            {
                if (now < next) continue;
                _nextRepeat[vk] = now + RepeatMs();
            }
            else _nextRepeat[vk] = now + DelayMs();
            if (KeyText.CharOf(vk, shift) is char c) _pending.Add((c, now));
        }
        int ready = 0;
        while (ready < _pending.Count && (_platformSilent || now - _pending[ready].At >= PlatformWaitMs)) ready++;
        if (ready == 0) return;
        var text = new char[ready];
        for (int i = 0; i < ready; i++) text[i] = _pending[i].Char;
        _pending.RemoveRange(0, ready);
        if (!_platformSilent && ++_typedBatches >= 3)
        {
            _platformSilent = true;
            GameLog.Instance.Info("keyboard text: the platform typed nothing for 3 keys; text boxes now type from the game's keyboard");
        }
        TextEntered?.Invoke(this, text);
    }

    // The same delays as the game's own key repeat (KeyboardDevice.StartTimer).
    private static long DelayMs() => Window() is { } window ? 250 + 250 * window.RepeatedInputDelay : 500;

    private static long RepeatMs() => Window() is { } window ? (long)(400 - 11.83 * window.RepeatedInputSpeed) : 33;

    private static IPlatformWindow Window()
    {
        try { return Singleton<VRageCore>.Instance.Engine.Single<IPlatformWindows>().Window; }
        catch (Exception) { return null; }
    }
}
