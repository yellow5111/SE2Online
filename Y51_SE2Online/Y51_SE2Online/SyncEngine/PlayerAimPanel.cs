using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Keen.VRage.UI.AvaloniaInterface.Utilities;
using Keen.VRage.Library.Utils;
using Keen.VRage.UI.Screens;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// The info panel beside the crosshair while aiming at another player, laid out like SE2's welder block panel
/// (BlockIntegrityScreen: 460 wide, offset 400/-72 from centre, accent bar, black HUD backdrop). It lives inside the
/// always-open chat HUD and is only shown or hidden: opening and closing a screen of its own each time the aim changed
/// disturbed SE2's HUD screen stack and left the native block panel stuck on screen.
/// </summary>
public sealed class PlayerAimView : Grid
{
    internal static readonly PlayerAimView Shared = new();
    private readonly TextBlock _name = Line(26, true), _faction = Line(21), _relation = Line(21), _ping = Line(19), _hint = Line(20, true);
    private readonly Rectangle _accent = new() { Width = 5, VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Left };

    public PlayerAimView()
    {
        IsHitTestVisible = false;
        IsVisible = false;
        Width = 460; HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center; Margin = new Thickness(400, -72, -400, 0);
        var lines = new StackPanel { Spacing = 4, Margin = new Thickness(18, 12, 14, 12) };
        foreach (var line in new[] { _name, _faction, _relation, _ping, _hint }) lines.Children.Add(line);
        _hint.Foreground = new SolidColorBrush(Color.FromRgb(255, 210, 60));
        _hint.Margin = new Thickness(0, 6, 0, 0);
        Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0xB4, 0, 0, 0)) });
        Children.Add(_accent);
        Children.Add(lines);
    }

    /// <summary>A 1920x1080 layer holding the shared panel, for a full-screen HUD overlay.</summary>
    internal static Control Layer()
    {
        if (Shared.Parent is Panel old) old.Children.Remove(Shared);
        var frame = new Grid { Width = 1920, Height = 1080, IsHitTestVisible = false };
        frame.Children.Add(Shared);
        return new Viewbox { Stretch = Stretch.Uniform, IsHitTestVisible = false, Child = frame };
    }

    private static TextBlock Line(double size, bool bold = false)
    {
        var text = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal };
        text.Bind(TextBlock.ForegroundProperty, new VRageDynamicResource("HighContrast"));
        return text;
    }

    internal void Set(PlayerAimInfo info, long identity)
    {
        _name.Text = info.Title;
        _faction.Text = info.Faction;
        _relation.Text = info.Relation;
        var c = MarkerColors.For(identity) ?? MarkerColors.Neutral;
        var colour = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        _relation.Foreground = colour;
        _accent.Fill = colour;
        _ping.Text = info.Ping;
        _ping.IsVisible = info.Ping.Length > 0;
        _hint.Text = info.Hint;
        _hint.IsVisible = info.Hint.Length > 0;
    }
}

internal static class PlayerAimPanel
{
    private static PlayerAimInfo _shown;

    internal static void Show(PlayerTarget target, string hint)
    {
        var info = PlayerAimInfo.For(target.Name, target.Identity, SocialUi.Snapshot, PingOf(target.Name));
        if (info.Ping.Length == 0 && target.ClientId == SyncProtocol.HostClientId) info = info with { Ping = "Host" };
        info = info with { Hint = hint };
        _shown = info;
        MpMenu.Queue(() =>
        {
            if (_shown != info) return;
            PlayerAimView.Shared.Set(info, target.Identity);
            PlayerAimView.Shared.IsVisible = true;
        });
    }

    internal static void Hide()
    {
        _shown = null;
        MpMenu.Queue(() => { if (_shown == null) PlayerAimView.Shared.IsVisible = false; });
    }

    private static int PingOf(string name)
    {
        // Roster names may carry the faction tag; match without it.
        string bare = FactionTag.Strip(name);
        try { return AdminAccess.State.Players?.FirstOrDefault(p => p.Connected && FactionTag.Strip(p.Name) == bare)?.PingMs ?? -1; }
        catch (Exception) { return -1; }
    }
}
