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
    private readonly ComboBox _player = OnlineUi.PlayerPicker("Choose a player");
    private readonly TextBlock _status = OnlineUi.Text("", "SubtleNotice", 17);
    private readonly Button _whitelist, _blacklist, _remove;
    private readonly Control _lists, _picker;
    private readonly CheckBox _showTag = new() { Content = "Show the owner's faction tag on this antenna's signal", FontSize = 20, MinHeight = 36, Name = "AntennaShowTag" };
    private bool _syncingTag;
    private Guid _block;
    private string _signature;
    private long _askedMs;

    internal Control Panel { get; }

    public BlockAccessView()
    {
        _allow.ItemTemplate = _deny.ItemTemplate = new FuncDataTemplate<SocialPlayer>((p, _) => p == null ? null : OnlineUi.ListCells("*", p.Name));
        // The panel opens over the block's own controls, so the lists stay short (they scroll).
        _allow.MaxHeight = _deny.MaxHeight = 140;
        _allow.SelectionChanged += (_, _) => { if (_allow.SelectedItem != null) _deny.SelectedItem = null; Refresh(); };
        _deny.SelectionChanged += (_, _) => { if (_deny.SelectedItem != null) _allow.SelectedItem = null; Refresh(); };
        _player.SelectionChanged += (_, _) => Refresh();
        _whitelist = OnlineUi.SmallButton("Whitelist", () => Send(SocialAction.BlockAllow, (_player.SelectedItem as SocialPlayer)?.Identity, true), "BlockWhitelistAdd");
        _blacklist = OnlineUi.SmallButton("Blacklist", () => Send(SocialAction.BlockDeny, (_player.SelectedItem as SocialPlayer)?.Identity, true), "BlockBlacklistAdd");
        _remove = OnlineUi.SmallButton("Remove", () =>
        {
            if (_allow.SelectedItem is SocialPlayer a) Send(SocialAction.BlockAllow, a.Identity, false);
            else if (_deny.SelectedItem is SocialPlayer d) Send(SocialAction.BlockDeny, d.Identity, false);
        }, "BlockAccessRemove");
        var lists = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        lists.Children.Add(OnlineUi.Text("Whitelist: may use it whatever the sharing", "SubtleNotice", 17));
        var blackTitle = OnlineUi.Text("Blacklist: may never use it", "SubtleNotice", 17); Grid.SetColumn(blackTitle, 2); lists.Children.Add(blackTitle);
        foreach (var title in lists.Children.OfType<TextBlock>()) { title.HorizontalAlignment = HorizontalAlignment.Left; title.TextAlignment = Avalonia.Media.TextAlignment.Left; }
        Grid.SetRow(_allow, 1); lists.Children.Add(_allow);
        Grid.SetRow(_deny, 1); Grid.SetColumn(_deny, 2); lists.Children.Add(_deny);
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(_lists = lists);
        stack.Children.Add(_picker = OnlineUi.Row(_player, _whitelist, _blacklist, _remove));
        _showTag.IsVisible = false;
        _showTag.IsCheckedChanged += (_, _) => { if (!_syncingTag && _block != Guid.Empty) SocialUi.Send(new SocialRequest(SocialAction.AntennaTag, Offset: _showTag.IsChecked == true ? 1 : 0, Secure: _block)); };
        stack.Children.Add(_showTag);
        stack.Children.Add(_status);
        _status.HorizontalAlignment = HorizontalAlignment.Left; _status.TextAlignment = Avalonia.Media.TextAlignment.Left;
        Panel = OnlineUi.Panel(stack, new Thickness(16, 12));
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
        // The host drops requests over five a second, so a lost ask is repeated until the block's lists arrive.
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
        bool lists = focus?.Lists != false;
        _lists.IsVisible = _picker.IsVisible = lists;
        _whitelist.IsEnabled = _blacklist.IsEnabled = owner && lists && _player.SelectedItem != null;
        _remove.IsEnabled = owner && lists && (_allow.SelectedItem != null || _deny.SelectedItem != null);
        if (focus == null) _status.Text = _block == Guid.Empty ? "" : "Loading this block's access...";
        else if (!lists && focus.Antenna) _status.Text = snapshot.Message ?? "";
        else if (!lists) _status.Text = "SE2 has no per-block access for this block type; it follows the grid's owner and sharing.";
        else if (!owner) _status.Text = "Only the grid's owner can change these lists.";
        else if (!string.IsNullOrEmpty(snapshot.Message)) _status.Text = snapshot.Message;
        else _status.Text = "";
    }
}
