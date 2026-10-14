using System;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Client.UI.InGame;
using Keen.Game2.Client.UI.Library.Controls.QuickMenu;
using Keen.Game2.Client.UI.Library.Controls.RadialMenu;
using Keen.Game2.Simulation.WorldObjects.Characters.Emotes;
using Keen.VRage.Library.Collections;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Threading;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Hold the emote key for SE2's own radial quick menu (the toolbar/paint tool one) with the multiplayer emotes; click a
/// slice, or release the key over one, to play it. Emotes go through SE2's RequestPlayEmote story, so the animation
/// replicates like any other; Punch asks the host, which plays it and applies the hit under the PvP rules.
/// </summary>
internal static class EmoteMenu
{
    // The quick menu definition SE2's toolbar and paint tool radials use.
    private static readonly Guid QuickMenuId = new("183da096-9d49-4d8c-9954-7c00153ed90e");

    private static readonly Dictionary<BaseRadialMenuPanel, Emotes.Entry> Panels = new();
    private static IDisposable _open;
    private static BaseRadialMenuPanel _hovered;
    private static bool _played, _down;
    private static PlayerTarget _target;
    private static int _logs;

    internal static bool IsOpen => _open != null;

    internal static void Tick(bool usable)
    {
        bool down = KeyBinding.TryParse(MpMenu.Settings.EmoteKey, out var key) && key.IsDown(GameKeys.IsDown) && GameKeys.OwnWindowInFront();
        bool pressed = down && !_down, released = !down && _down;
        _down = down;
        try
        {
            if (pressed && usable && _open == null) Open();
            else if (released && _open != null) Close(true);
            else if (_open != null && !usable) Close(false);
        }
        catch (Exception ex)
        {
            _open = null;
            GameLog.Instance.Warn("emotes: the emote menu failed: " + ex.Message);
        }
    }

    private static void Open()
    {
        var ui = MpPlugin.App?.ClientSession?.SessionComponents.TryGet<SessionInGameUISessionComponent>();
        if (ui == null || !Singleton<DefinitionManager>.Instance.TryGetDefinition(QuickMenuId, out QuickMenuDefinition definition)) return;
        Panels.Clear();
        var slices = new MergeableList<BaseRadialMenuPanel>();
        foreach (var entry in Emotes.Menu)
        {
            var panel = new BaseRadialMenuPanel { DisplayName = entry.Label };
            Panels[panel] = entry;
            slices.Add(panel);
        }
        _hovered = null;
        _played = false;
        _target = PlayerInteraction.Target;
        var menu = new QuickMenuScreenViewModel(definition, new QuickMenuData { RootMenu = new RadialMenuData(slices, new BaseRadialMenuPanel()) },
            selected => { if (selected != null) Play(selected); }, hovered => _hovered = hovered);
        _open = ui.OpenQuickMenu(menu);
    }

    private static void Close(bool playHovered)
    {
        var open = _open;
        _open = null;
        if (playHovered && _hovered != null) Play(_hovered);
        open?.Dispose();
    }

    private static void Play(BaseRadialMenuPanel panel)
    {
        if (_played || !Panels.TryGetValue(panel, out var entry)) return;
        _played = true;
        Play(entry, _target);
    }

    internal static void Play(Emotes.Entry entry, PlayerTarget aimed)
    {
        _target = aimed;
        if (entry.Definition == Emotes.Punch)
        {
            ushort target = _target?.ClientId ?? 0;
            if (_logs++ < 40) GameLog.Instance.Info("emotes: Punch at " + (_target?.Name ?? "nobody (swing)"));
            if (AdminAccess.Hosting) HostWorld.Current?.OnPunch(SyncProtocol.HostClientId, target);
            else AdminAccess.Client?.SendPunch(target);
            return;
        }
        // The controller component lives on the server's character only; like SE2's own emote debug screen, the story
        // is started with the locally controlled character and runs on the host.
        var character = MpPlugin.App?.ClientSession?.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerController?.ControlledEntities
            .Select(c => c.Controllable.Entity).FirstOrDefault(e => e.TryGet<CharacterComponent>() != null);
        bool known = Singleton<DefinitionManager>.Instance.TryGetDefinition(entry.Definition, out EmoteDefinition emote);
        if (_logs++ < 40) GameLog.Instance.Info($"emotes: {entry.Label}: {(character == null ? "no controlled character, not played" : !known ? "definition missing, not played" : "asking " + (AdminAccess.Hosting ? "this host" : "the host") + " to play it")}");
        if (character == null || !known) return;
        EmoteControllerComponent.RequestPlayEmote(character, emote).SkipWait();
    }
}
