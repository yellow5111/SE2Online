using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>Whitelist and blacklist of the block selected in the terminal control panel; only the grid's owner edits them.</summary>
internal sealed class BlockAccessView
{
    private readonly ListBox _allow = OnlineUi.List("BlockWhitelist"), _deny = OnlineUi.List("BlockBlacklist");
    private readonly ComboBox _player = new() { MinHeight = 44, FontSize = 20, MinWidth = 280, PlaceholderText = "Choose a player" };
    private readonly TextBlock _status = OnlineUi.Text("", "SubtleNotice", 18);
    private readonly Button _whitelist, _blacklist, _remove;
    private readonly OnlineToggle _showTag = new("Show the owner's faction tag on this antenna's signal");
    private bool _syncingTag;
    private Guid _block;
    private string _signature;
    private long _askedMs;

    internal Control Panel { get; }

    public BlockAccessView()
    {
        _allow.ItemTemplate = _deny.ItemTemplate = new FuncDataTemplate<SocialPlayer>((p, _) => p == null ? null : OnlineUi.ListCells("*", p.Name));
        _allow.MaxHeight = _deny.MaxHeight = 220;
        _allow.SelectionChanged += (_, _) => { if (_allow.SelectedItem != null) _deny.SelectedItem = null; Refresh(); };
        _deny.SelectionChanged += (_, _) => { if (_deny.SelectedItem != null) _allow.SelectedItem = null; Refresh(); };
        _player.SelectionChanged += (_, _) => Refresh();
        _whitelist = OnlineUi.Button("Whitelist", () => Send(SocialAction.BlockAllow, (_player.SelectedItem as SocialPlayer)?.Identity, true), "BlockWhitelistAdd");
        _blacklist = OnlineUi.Button("Blacklist", () => Send(SocialAction.BlockDeny, (_player.SelectedItem as SocialPlayer)?.Identity, true), "BlockBlacklistAdd");
        _remove = OnlineUi.Button("Remove", () =>
        {
            if (_allow.SelectedItem is SocialPlayer a) Send(SocialAction.BlockAllow, a.Identity, false);
            else if (_deny.SelectedItem is SocialPlayer d) Send(SocialAction.BlockDeny, d.Identity, false);
        }, "BlockAccessRemove");
        foreach (var button in new[] { _whitelist, _blacklist, _remove }) { button.FontSize = 20; button.MinHeight = 44; button.MinWidth = 120; button.Padding = new Thickness(12, 6); }
        var lists = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        lists.Children.Add(OnlineUi.Text("WHITELIST", "SubTitle", 20));
        var blackTitle = OnlineUi.Text("BLACKLIST", "SubTitle", 20); Grid.SetColumn(blackTitle, 2); lists.Children.Add(blackTitle);
        Grid.SetRow(_allow, 1); lists.Children.Add(_allow);
        Grid.SetRow(_deny, 1); Grid.SetColumn(_deny, 2); lists.Children.Add(_deny);
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(OnlineUi.Text("BLOCK ACCESS", "SubTitle", 22));
        stack.Children.Add(OnlineUi.Text("Whitelisted players may use this block even when the grid is not shared with them. Blacklisted players may not use it, whatever the grid's sharing.", "SubtleNotice", 18));
        stack.Children.Add(lists);
        stack.Children.Add(OnlineUi.Row(_player, _whitelist, _blacklist, _remove));
        _showTag.IsVisible = false;
        _showTag.IsCheckedChanged += (_, _) => { if (!_syncingTag && _block != Guid.Empty) SocialUi.Send(new SocialRequest(SocialAction.AntennaTag, Offset: _showTag.IsChecked ? 1 : 0, Secure: _block)); };
        stack.Children.Add(_showTag);
        stack.Children.Add(_status);
        Panel = OnlineUi.Panel(stack, new Thickness(20));
    }

    /// <summary>Selects the block by its control panel id (empty for none) and asks the host for its lists.</summary>
    internal void Select(Guid block)
    {
        if (block == _block) return;
        _block = block; _signature = null; _status.Text = "";
        _askedMs = 0;
        Refresh();
    }

    private void Send(SocialAction action, long? player, bool add)
    {
        if (player is not long id || _block == Guid.Empty) return;
        SocialUi.Send(new SocialRequest(action, Player: id, Offset: add ? 1 : 0, Secure: _block));
    }

    internal void Refresh()
    {
        var snapshot = SocialUi.Snapshot;
        var focus = snapshot.FocusBlock is { } f && f.Secure == _block && _block != Guid.Empty ? f : null;
        _showTag.IsVisible = focus?.Antenna == true;
        if (focus != null && _showTag.IsChecked != focus.ShowTag) { _syncingTag = true; _showTag.IsChecked = focus.ShowTag; _syncingTag = false; }
        var ownFaction = snapshot.Factions.FirstOrDefault(x => x.Members.Contains(snapshot.Actor));
        _showTag.IsEnabled = focus != null && snapshot.Actor != 0 && (focus.Owner == snapshot.Actor || ownFaction != null && ownFaction.Members.Contains(focus.Owner) && ownFaction.RoleOf(snapshot.Actor) >= FactionRole.Admin);
        // The host drops requests closer than 200 ms apart, so a lost ask is repeated until the block's lists arrive.
        long now = Core.MpClock.NowMs();
        if (focus == null && _block != Guid.Empty && now - _askedMs > 1000)
        {
            _askedMs = now;
            SocialUi.Send(new SocialRequest(SocialAction.Refresh, Secure: _block));
        }
        string signature = focus == null ? "" : snapshot.Revision + ":" + string.Join(",", focus.Allow) + ":" + string.Join(",", focus.Deny);
        if (signature != _signature)
        {
            _signature = signature;
            SocialPlayer Row(long id) => snapshot.Players.FirstOrDefault(p => p.Identity == id) ?? new SocialPlayer(id, "Player " + id);
            _allow.ItemsSource = focus?.Allow.Select(Row).ToArray() ?? Array.Empty<SocialPlayer>();
            _deny.ItemsSource = focus?.Deny.Select(Row).ToArray() ?? Array.Empty<SocialPlayer>();
            long selected = (_player.SelectedItem as SocialPlayer)?.Identity ?? 0;
            var players = snapshot.Players.Where(p => p.Identity != snapshot.Actor).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            _player.ItemsSource = players; _player.SelectedItem = players.FirstOrDefault(p => p.Identity == selected);
        }
        bool owner = focus != null && snapshot.Actor != 0 && focus.Owner == snapshot.Actor;
        _whitelist.IsEnabled = _blacklist.IsEnabled = owner && _player.SelectedItem != null;
        _remove.IsEnabled = owner && (_allow.SelectedItem != null || _deny.SelectedItem != null);
        if (focus == null) _status.Text = _block == Guid.Empty ? "" : "Loading this block's access...";
        else if (!owner) _status.Text = "Only the grid's owner can change these lists.";
        else if (!string.IsNullOrEmpty(snapshot.Message)) _status.Text = snapshot.Message;
        else _status.Text = "";
    }
}
