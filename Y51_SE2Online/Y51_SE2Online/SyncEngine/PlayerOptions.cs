using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Keen.VRage.UI.Screens;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>The player another player is looking at, for <see cref="PlayerOptions"/> entries.</summary>
public sealed record PlayerTarget(ushort ClientId, long Identity, string Name);

/// <summary>
/// Actions offered for another player when you look at them and press Use. Features add their own entries;
/// "{name}" in a label is replaced with the player's name.
/// </summary>
public static class PlayerOptions
{
    private sealed record Entry(Func<PlayerTarget, string> Label, Func<PlayerTarget, bool> Visible, Action<PlayerTarget> Run);

    private static readonly List<Entry> Entries = new();

    public static void Add(string label, Func<PlayerTarget, bool> visible, Action<PlayerTarget> run) =>
        Add(t => label.Replace("{name}", t.Name), visible, run);

    public static void Add(Func<PlayerTarget, string> label, Func<PlayerTarget, bool> visible, Action<PlayerTarget> run)
    {
        lock (Entries) Entries.Add(new Entry(label, visible, run));
    }

    internal static (string Label, Action Run)[] For(PlayerTarget target)
    {
        Entry[] entries;
        lock (Entries) entries = Entries.ToArray();
        var shown = new List<(string, Action)>();
        foreach (var e in entries)
        {
            bool visible;
            string label;
            try { visible = e.Visible(target); label = visible ? e.Label(target) : null; }
            catch (Exception ex) { GameLog.Instance.Warn("player options: an entry failed its check: " + ex.Message); continue; }
            if (visible) shown.Add((label, () => e.Run(target)));
        }
        return shown.ToArray();
    }

    static PlayerOptions()
    {
        Add("Invite {name} to my faction", t => PlayerOptionRules.CanInvite(SocialUi.Snapshot, t.Identity), t =>
        {
            var social = SocialUi.Snapshot;
            if (PlayerOptionRules.FactionOf(social.Factions, social.Actor) is { } own)
                SocialUi.Send(new SocialRequest(SocialAction.Invite, own.Id, t.Identity));
            Notice("Invitation sent to " + t.Name + ".");
        });
        Add(t => "Apply to join [" + PlayerOptionRules.FactionOf(SocialUi.Snapshot.Factions, t.Identity)?.Tag + "]", t => PlayerOptionRules.CanApply(SocialUi.Snapshot, t.Identity), t =>
        {
            var social = SocialUi.Snapshot;
            if (PlayerOptionRules.FactionOf(social.Factions, t.Identity) is { } theirs)
                SocialUi.Send(new SocialRequest(SocialAction.RequestJoin, theirs.Id));
            Notice("Application sent to [" + PlayerOptionRules.FactionOf(social.Factions, t.Identity)?.Tag + "].");
        });
        Add("Mute or unmute {name}'s voice", _ => true, t =>
        {
            bool? muted = VoiceChat.ToggleMute(t.Name);
            Notice(muted == null ? t.Name + " is not online." : muted.Value ? "You no longer hear " + t.Name + "'s voice." : "You hear " + t.Name + "'s voice again.");
        });
        Add("Player info", _ => true, t => FactionPopups.Info(t.Name, PlayerOptionRules.Describe(SocialUi.Snapshot, t.Identity, t.Name)));
    }

    internal static void Notice(string text) =>
        ChatLog.Add(new ChatLine("Server", 0, text, ChatKind.System, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), MpClock.NowMs());
}

/// <summary>The options for one player, opened by looking at them and pressing Use.</summary>
public sealed class PlayerOptionsScreen : ScreenView
{
    private static PlayerTarget _pending;
    private SocialScreenViewModel _model;

    internal static void Open(PlayerTarget target) => MpMenu.Queue(() =>
    {
        var shared = JoinStatusUi.FindShared(MpPlugin.App);
        if (shared == null) return;
        _pending = target;
        shared.CreateScreen<PlayerOptionsScreen>(new SocialScreenViewModel(), true, ScreenLayer.Top);
    });

    public PlayerOptionsScreen()
    {
        var target = _pending;
        _pending = null;
        var stack = new StackPanel { Spacing = 12 };
        var options = target == null ? Array.Empty<(string Label, Action Run)>() : PlayerOptions.For(target);
        foreach (var (label, run) in options)
        {
            var button = OnlineUi.Button(label, () => { _model?.Close(); run(); }, null, true);
            button.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            stack.Children.Add(button);
        }
        if (options.Length == 0) stack.Children.Add(OnlineUi.Text("Nothing to do with this player right now."));
        string title = "MORE OPTIONS FOR " + (target?.Name ?? "PLAYER").ToUpperInvariant();
        Content = OnlineUi.Shell(this, title, OnlineUi.Row(), OnlineUi.Text(""), OnlineUi.Panel(stack, new Thickness(28)), OnlineUi.Row());
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _model = DataContext as SocialScreenViewModel;
    }
}
