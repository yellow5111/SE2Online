using System;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Client.GameSystems;
using Keen.Game2.Client.GameSystems.Interaction;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.UI.Library.Controls.ControlHints;
using Keen.Game2.Client.UI.Library.Dialogs.OneOptionDialog;
using Keen.Game2.Client.UI.Library.Dialogs.TwoOptionsDialog;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Input;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Looking at another player within reach shows SE2's own control hint "More options for &lt;name&gt;" with the player's
/// bound Use key; pressing Use opens <see cref="PlayerOptionsScreen"/>. The Use press goes through a game input context
/// built from SE2's generic use action, exactly like using a block, so it follows the player's own key binding.
/// </summary>
internal static class PlayerInteraction
{
    private const double Reach = 3.5, AimRadius = 0.45, ChestHeight = 1.0;
    // SE2's generic UseAction.def; its input context and InteractPrimary action are what every block use goes through.
    private static readonly Guid UseActionId = new("f49a9b10-fe13-42f5-8bd8-dc32f98c4333");

    private static PlayerTarget _target;

    /// <summary>The player currently aimed at within reach, or null.</summary>
    internal static PlayerTarget Target => _target;

    /// <summary>Test diagnostics: treat this player as aimed at while they are in the roster.</summary>
    internal static string ForcedName;

    private static InputContext _context;
    private static IInputProcessor _processor;
    private static IDisposable _hints;
    private static InputContext _pausedBlockContext;
    private static InteractorEntityComponent _interactor;
    private static bool _failed;
    private static string _lastStage = "";
    private static long _nextStageLogMs;

    /// <summary>A new session gets a fresh try: a failure while the last world was unloading must not switch this off for good.</summary>
    internal static void Reset()
    {
        _failed = false;
        Clear();
    }

    private static long _retryAtMs;

    // A failure (typically a player leaving mid-update, while their world part unloads) pauses this for a moment; it used
    // to switch looking at players off until the host loaded another world.
    internal static void Tick()
    {
        if (_failed && MpClock.NowMs() < _retryAtMs) return;
        _failed = false;
        try { Update(); }
        catch (Exception ex)
        {
            _failed = true;
            _retryAtMs = MpClock.NowMs() + 2000;
            Clear();
            GameLog.Instance.WarnOnce("player-options-failed", "player options: an update failed, retrying: " + ex);
        }
    }

    private static void Stage(string stage, string detail = "")
    {
        long now = MpClock.NowMs();
        if (stage == _lastStage && now < _nextStageLogMs) return;
        if (stage == _lastStage && detail.Length == 0) return;
        _lastStage = stage;
        _nextStageLogMs = now + 5000;
        GameLog.Instance.Info("player options: " + stage + (detail.Length > 0 ? " (" + detail + ")" : ""));
    }

    private static void Update()
    {
        var (session, players) = NametagOverlay.Last;
        // The roster's session can be the one just closed (a client leaving, a host reloading).
        if (!AdminAccess.InSession || session == null || players == null || !ReferenceEquals(session, MpPlugin.App?.ClientSession)) { Clear(); return; }
        var interactor = session.GetEntitiesOfType<InteractorEntityComponent>().Select(e => e.Get<InteractorEntityComponent>())
            .FirstOrDefault(i => i.IsControlledByLocalPlayer);
        var controller = session.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerController;
        var camera = controller?.CameraSystem?.RenderCameraEntity;
        if (interactor == null || camera == null) { Stage(interactor == null ? "no local character with an interactor" : "no render camera"); Clear(); return; }
        var view = camera.Data.GetWorldTransform();
        var (found, nearest) = Aimed(view.Position, view.Orientation.GetForward(), players, interactor.Entity);
        if (found == null)
        {
            Stage("not aiming at a player", players.Count + " in roster" + nearest);
            Clear();
            return;
        }
        // A block use action in reach wins only when it is closer than the player.
        var block = interactor._useAction?.Entity;
        if (block != null && Vector3D.Distance(view.Position, block.Data.GetWorldTransform().Position) < found.Distance)
        {
            Stage("a closer block use action wins", block.ToString());
            Clear();
            return;
        }
        if (_target != null && _target.ClientId == found.Target.ClientId && _context != null) return;
        Clear();
        Show(session, interactor, found.Target);
    }

    private sealed record Hit(PlayerTarget Target, double Distance);

    private static (Hit, string) Aimed(Vector3D eye, Vector3 forward, IReadOnlyList<(ushort ClientId, string Name, Entity? Target)> players, Entity self)
    {
        Hit best = null;
        string nearest = "";
        double nearestOff = double.MaxValue;
        foreach (var (clientId, name, character) in players)
        {
            // The local character is in the roster too (same Steam account in two instances included): skip it by entity.
            if (character == null || character == self || EngineCodec.IsDead(character)) continue;
            var at = character.Data.GetWorldTransform();
            Vector3D chest = at.Position + (Vector3D)at.Orientation.GetUp() * ChestHeight;
            Vector3D toChest = chest - eye;
            double along = Vector3D.Dot(toChest, (Vector3D)forward);
            double off = (toChest - (Vector3D)forward * along).Length();
            bool forced = ForcedName != null && string.Equals(name, ForcedName, StringComparison.OrdinalIgnoreCase);
            if (off < nearestOff) { nearestOff = off; nearest = $"; nearest '{name}' {toChest.Length():0.0} m away, {off:0.00} m off aim"; }
            if (!forced && (along <= 0 || along > Reach || off > AimRadius)) continue;
            // ponytail: ray to a chest point, no wall test; within 3.5 m a wall between two players is rare.
            if (best != null && along >= best.Distance) continue;
            best = new Hit(new PlayerTarget(clientId, TeamPermissions.OwnerOf(character), name), forced ? 0 : along);
        }
        return (best, nearest);
    }

    private static void Show(Session session, InteractorEntityComponent interactor, PlayerTarget target)
    {
        if (!Singleton<DefinitionManager>.Instance.TryGetDefinition(UseActionId, out UseActionDefinition use)) throw new InvalidOperationException("SE2's generic use action definition is missing");
        _target = target;
        _interactor = interactor;
        _processor = interactor._inputProcessor ?? Singleton<Keen.VRage.Core.VRageCore>.Instance.Engine.Single<IInputProcessor>();
        // While a player is the target, the block action's context (if any) is paused so Use only opens the player's options.
        if (interactor._actionInputContext is { } blockContext) { _processor?.DeactivateContext(blockContext); _pausedBlockContext = blockContext; }
        _context = new InputContext(use.InputContextDefinition);
        _context.SetHandler<bool>(use.InteractPrimary, (pressed, _) => { if (pressed && _target is { } t) PlayerOptionsScreen.Open(t); }, ControlActivationFlags.Start);
        _processor?.ActivateContext(_context);
        _hints = session.SessionComponents.TryGet<ControlHintsSessionComponent>()?.Attach(_context, OnlineUi.Raw(target.Name),
            new List<ActionHint> { new BoundActionHint(use.InteractPrimary, OnlineUi.Raw("More options")) });
        PlayerAimPanel.Show(target, "[" + UseKeyName(session, use) + "] More options for " + target.Name);
        Stage("aiming at '" + target.Name + "'", $"input processor {(_processor != null ? "found" : "MISSING")}, control hints {(_hints != null ? "attached" : "MISSING")}");
    }

    // The player's own binding for SE2's Use action, read the way the native control hints do; F is SE2's default.
    private static string UseKeyName(Session session, UseActionDefinition use)
    {
        try
        {
            // ActionControlMapping is a struct, so read it off the view model rather than through ?.
            if (session.SessionComponents.TryGet<ControlHintsSessionComponent>()?.ViewModel is { } hints && hints._actionControlMappings.TryGetActionControls(use.InteractPrimary, out var controls))
                foreach (var control in controls)
                    foreach (var id in control.Inputs)
                        if (id.GenericClass == Keen.VRage.Core.Input.GenericDeviceClass.Keyboard && KeyBinding.Format(id.Index, false, false, false) is { } name) return name;
        }
        catch (Exception) { }
        return "F";
    }

    private static void Clear()
    {
        if (_target == null && _context == null) return;
        _hints?.Dispose();
        _hints = null;
        if (_context != null) _processor?.DeactivateContext(_context);
        // Only if the interactor still uses it; it may have moved on to another block meanwhile.
        if (_pausedBlockContext != null && _interactor?._actionInputContext == _pausedBlockContext) _processor?.ActivateContext(_pausedBlockContext);
        _interactor = null;
        _pausedBlockContext = null;
        _context = null;
        _processor = null;
        _target = null;
        PlayerAimPanel.Hide();
    }
}

/// <summary>
/// Native accept/decline popups for faction invitations (when you are in no faction) and join applications (when your
/// role may approve them), raised from the social snapshot the host sends every second. One popup at a time; each
/// invitation or application pops once while it stays pending.
/// </summary>
internal static class FactionPopups
{
    private static readonly HashSet<string> Shown = new();
    private static bool _open;

    internal static void Info(string title, string text) => MpMenu.Queue(() =>
        JoinStatusUi.FindShared(MpPlugin.App)?.ShowDialog(new OneOptionDialogViewModel(new OneOptionDialogDefinition
        {
            Title = OnlineUi.Raw(title), Content = OnlineUi.Raw(text), ConfirmOption = OnlineUi.Raw("OK")
        })));

    internal static void Tick()
    {
        var social = SocialUi.Snapshot;
        if (!AdminAccess.InSession || social.Actor == 0) { Shown.Clear(); return; }
        var pending = new List<(string Key, string Title, string Text, SocialRequest Yes, SocialRequest No)>();
        var own = PlayerOptionRules.FactionOf(social.Factions, social.Actor);
        if (own == null)
        {
            foreach (var f in social.Factions.Where(f => f.Invitations.Contains(social.Actor)))
                pending.Add(("invite:" + f.Id, "Faction invitation", "[" + f.Tag + "] " + f.Name + " invites you to join.",
                    new SocialRequest(SocialAction.AcceptInvitation, f.Id), new SocialRequest(SocialAction.DeclineInvitation, f.Id)));
        }
        else if (own.RoleOf(social.Actor) >= SocialState.Needed(SocialAction.ApproveRequest))
        {
            foreach (long player in own.Requests)
            {
                string name = social.Players.FirstOrDefault(p => p.Identity == player)?.Name ?? "A player";
                pending.Add(("apply:" + own.Id + ":" + player, "Faction application", name + " wants to join [" + own.Tag + "].",
                    new SocialRequest(SocialAction.ApproveRequest, own.Id, player), new SocialRequest(SocialAction.RejectRequest, own.Id, player)));
            }
        }
        Shown.IntersectWith(pending.Select(p => p.Key));
        if (_open) return;
        var next = pending.FirstOrDefault(p => !Shown.Contains(p.Key));
        if (next.Key == null) return;
        var shared = JoinStatusUi.FindShared(MpPlugin.App);
        if (shared == null) return;
        Shown.Add(next.Key);
        _open = true;
        shared.ShowDialog(new TwoOptionsDialogViewModel(new TwoOptionsDialogDefinition
        {
            Title = OnlineUi.Raw(next.Title), Content = OnlineUi.Raw(next.Text),
            ConfirmOption = OnlineUi.Raw("Accept"), CancelOption = OnlineUi.Raw("Decline")
        })
        {
            ConfirmAction = () => { _open = false; SocialUi.Send(next.Yes); },
            CancelAction = () => { _open = false; SocialUi.Send(next.No); },
            // Esc leaves it pending; the Factions screen still lists it.
            EscapeAction = () => _open = false
        });
    }
}
