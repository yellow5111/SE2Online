using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Keen.VRage.UI.AvaloniaInterface.Utilities;
using Y51_SE2Online.Core;
using Y51_SE2Online.KeenNetcode;
using Y51_SE2Online.NetHost;

namespace Y51_SE2Online.SyncEngine;

/// <summary>The Host page: a live session card (status, join code, address, invites) above the session's settings.</summary>
public sealed partial class OnlineScreen
{
    private static readonly IBrush LiveBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C));
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xC8, 0xCC, 0xD4));
    private static readonly IBrush StartingBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3C));

    private static readonly (string Title, string Detail)[] Visibilities =
    {
        ("Public", "Listed in the session browser. Anyone with the plugin can find and join."),
        ("Invite and code", "Hidden from the browser. Players join with your join code, an invite or your address."),
        ("Friends only", "Only your Steam friends can join, from their friends list or an invite.")
    };

    private const int Public = 0, InviteAndCode = 1, FriendsOnly = 2;

    private int _visibility;
    private readonly Border[] _visibilityCards = new Border[Visibilities.Length];
    private ScrollViewer _hostScroll;
    private readonly Border _statusPill = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 4), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _statusPillText = new() { FontSize = 18, FontWeight = FontWeight.Bold, LetterSpacing = 2, Foreground = Brushes.Black };
    private readonly TextBlock _heroTitle = OnlineUi.Text("", "Header", 46);
    private readonly TextBlock _heroDetail = OnlineUi.Text("", "SubtleNotice", 22);
    private readonly TextBlock _code = OnlineUi.Text("", "Header", 64);
    private readonly TextBlock _codeHint = OnlineUi.Text("", "SubtleNotice", 18);
    private readonly TextBlock _hostAddress = OnlineUi.Text("", "SubTitle", 26);
    private readonly TextBlock _hostAddressHint = OnlineUi.Text("", "SubtleNotice", 18);
    private Button _copyCode, _copyAddress, _invite, _hostPlayers, _stopHost, _listingToggle, _advancedAddress;
    private readonly TextBlock _listing = OnlineUi.Text("", "SubTitle", 22);
    private Control _publicAddressField;
    private bool _showAddressOverride;

    private Control BuildHostPage()
    {
        _host = OnlineUi.Button("Start hosting", StartHost, "StartHosting", true);
        _load = OnlineUi.Button(OnlineUi.Localize("MenuLoadGame", "Load world"), () => MpMenu.ChooseWorld(false), "LoadWorld", true);
        _create = OnlineUi.Button(OnlineUi.Localize("MenuNewGame", "New world"), () => MpMenu.ChooseWorld(true), "CreateWorld", true);
        _invite = OnlineUi.Button("Invite friends", MpMenu.InviteFriends, "InviteFriends", true);
        _hostPlayers = OnlineUi.Button("Players", MpMenu.OpenPlayers, "HostPlayers", true);
        _stopHost = OnlineUi.Button("Stop hosting", MpMenu.Leave, "StopHosting", true);
        _hostSave = OnlineUi.Button("Apply host settings", Save, "SaveHostSettings", true);
        _copyCode = OnlineUi.SmallButton("Copy code", () => { }, "CopyJoinCode");
        _copyAddress = OnlineUi.SmallButton("Copy", () => Copy(HostAddress()), "CopyHostAddress");
        _listingToggle = OnlineUi.SmallButton("Make private", ToggleListing, "ToggleListing");
        _advancedAddress = OnlineUi.SmallButton("Advanced", () => { _showAddressOverride = !_showAddressOverride; RefreshControls(); }, "AdvancedAddress");

        // The session card.
        _statusPill.Child = _statusPillText;
        _heroTitle.TextWrapping = TextWrapping.Wrap; _heroTitle.TextTrimming = TextTrimming.None;
        _heroDetail.HorizontalAlignment = HorizontalAlignment.Left; _heroDetail.TextAlignment = TextAlignment.Left;
        var actions = OnlineUi.Row(_host, _load, _create, _invite, _hostPlayers, _stopHost);
        actions.Margin = new Thickness(0, 12, 0, 0);
        var left = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        _listing.HorizontalAlignment = HorizontalAlignment.Left; _listing.TextAlignment = TextAlignment.Left; _listing.VerticalAlignment = VerticalAlignment.Center;
        var listingRow = OnlineUi.Row(_listing, _listingToggle);
        left.Children.Add(_statusPill); left.Children.Add(_heroTitle); left.Children.Add(_heroDetail); left.Children.Add(listingRow); left.Children.Add(actions);

        _code.LetterSpacing = 8; _code.HorizontalAlignment = HorizontalAlignment.Left; _code.FontWeight = FontWeight.Bold;
        foreach (var hint in new[] { _codeHint, _hostAddressHint }) { hint.HorizontalAlignment = HorizontalAlignment.Left; hint.TextAlignment = TextAlignment.Left; }
        var codeRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        codeRow.Children.Add(_code); Grid.SetColumn(_copyCode, 1); _copyCode.VerticalAlignment = VerticalAlignment.Center; codeRow.Children.Add(_copyCode);
        var addressRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _hostAddress.TextWrapping = TextWrapping.NoWrap; _hostAddress.TextTrimming = TextTrimming.CharacterEllipsis;
        addressRow.Children.Add(_hostAddress); Grid.SetColumn(_copyAddress, 1); addressRow.Children.Add(_copyAddress);
        var card = new StackPanel { Spacing = 6 };
        card.Children.Add(Caption("JOIN CODE")); card.Children.Add(codeRow); card.Children.Add(_codeHint);
        card.Children.Add(Rule());
        card.Children.Add(Caption("DIRECT CONNECT")); card.Children.Add(addressRow); card.Children.Add(_hostAddressHint);
        var codeCard = new Border { Child = card, Padding = new Thickness(28, 20), BorderThickness = new Thickness(2), Width = 600, VerticalAlignment = VerticalAlignment.Center };
        codeCard.Bind(Border.BorderBrushProperty, new VRageDynamicResource("Primary"));
        codeCard.Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0));

        var hero = new Grid { ColumnDefinitions = new ColumnDefinitions("*,40,Auto") };
        hero.Children.Add(left); Grid.SetColumn(codeCard, 2); hero.Children.Add(codeCard);

        // Who can join.
        var choices = new UniformGrid { Columns = Visibilities.Length };
        for (int i = 0; i < Visibilities.Length; i++)
        {
            int index = i;
            var content = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
            var title = OnlineUi.Text(Visibilities[i].Title, null, 26); title.FontWeight = FontWeight.Bold;
            var detail = OnlineUi.Text(Visibilities[i].Detail, null, 18); detail.Opacity = .8;
            content.Children.Add(title); content.Children.Add(detail);
            var choice = new Border { Child = content, Padding = new Thickness(22, 16), MinHeight = 112, Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)) };
            // A Button for mouse, keyboard and gamepad, with a plain template: SE2's own button template scales its content to one line.
            var button = new Button
            {
                Name = "Visibility" + i, Margin = new Thickness(i == 0 ? 0 : 8, 0, i == Visibilities.Length - 1 ? 0 : 8, 0), Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Template = new Avalonia.Controls.Templates.FuncControlTemplate<Button>((_, _) => choice)
            };
            button.Click += (_, _) => { _visibility = index; RefreshControls(); };
            _visibilityCards[i] = choice;
            choices.Children.Add(button);
        }

        foreach (var toggle in new[] { _directIp, _waterSync, _buildGhosts, _clientMovement, _ownReplication, _onePilot }) toggle.ColumnDefinitions = new ColumnDefinitions("*,24,200");
        var session = OnlineUi.Section("SESSION",
            OnlineUi.Field("Session name", _sessionName, null, null, 420), OnlineUi.Field("Description", _description, null, null, 420),
            OnlineUi.Field("Maximum players", _limit.Row, $"{OnlineSettings.MinPlayers}-{OnlineSettings.PlayerLimit}, including you", _limitWarning, 420),
            OnlineUi.Field("Join password", _password, "Optional. Admins join without it.", null, 420),
            OnlineUi.Field("Country", _country, "auto finds it from your IP, or a 2-letter code like US", null, 420));
        var motd = OnlineUi.Section("MESSAGE OF THE DAY", OnlineUi.Text("Shown to each player after they spawn. {player}, {server}, {online} and {max} are filled in.", "SubtleNotice", 18),
            OnlineUi.Field("Title", _motdTitle, "Empty uses the session name", null, 420), OnlineUi.Field("Message", _motd, "The admin panel's message replaces it", null, 420));
        // Players join a co-op session through Steam, so an address is only for the card and the browser: an advanced
        // override, hidden until asked for (shown when one is saved, so it can be cleared).
        _publicAddressField = OnlineUi.Field("Public address override", _publicAddress, "Advanced. Shown on the card and in the browser", null, 300);
        var direct = OnlineUi.Section("DIRECT IP", _directIp,
            OnlineUi.Field("Bind address", _bind, "0.0.0.0 listens on every IPv4 network", null, 300), OnlineUi.Field("Listen port", _port, "UDP; forward it on your router", null, 300),
            _advancedAddress, _publicAddressField);
        var sync = OnlineUi.Section("SYNC", _ownReplication, _clientMovement, _onePilot, _buildGhosts, _waterSync);
        // SE2 Online's own netcode always carries the world.
        _ownReplication.IsVisible = false;
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,*") };
        var leftColumn = OnlineUi.Stack(session, motd);
        var rightColumn = OnlineUi.Stack(direct, sync, BuildCleanupSection());
        columns.Children.Add(leftColumn); Grid.SetColumn(rightColumn, 2); columns.Children.Add(rightColumn);

        var apply = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var applyHint = OnlineUi.Text("Changes apply live while hosting. A new port or direct IP choice restarts hosting, and you are asked first.", "SubtleNotice", 18);
        applyHint.HorizontalAlignment = HorizontalAlignment.Left; applyHint.TextAlignment = TextAlignment.Left;
        apply.Children.Add(applyHint); Grid.SetColumn(_hostSave, 1); apply.Children.Add(_hostSave);

        var whoCanJoin = OnlineUi.Stack(OnlineUi.Text("WHO CAN JOIN", "SubTitle", 30), choices);
        whoCanJoin.Spacing = 14;
        var page = OnlineUi.Scroll(OnlineUi.Stack(OnlineUi.Panel(hero, new Thickness(36, 28)), OnlineUi.Panel(whoCanJoin), columns, apply));
        _hostScroll = (ScrollViewer)page;
        return page;
    }

    /// <summary>Test screenshots: scroll the Host page (0 top, 1 bottom).</summary>
    internal void ScrollHost(double fraction)
    {
        if (_hostScroll != null) _hostScroll.Offset = new Vector(0, Math.Max(0, _hostScroll.Extent.Height - _hostScroll.Viewport.Height) * fraction);
    }

    private static TextBlock Caption(string text)
    {
        var caption = OnlineUi.Text(text, "SubtleNotice", 17);
        caption.LetterSpacing = 3; caption.FontWeight = FontWeight.Bold;
        caption.HorizontalAlignment = HorizontalAlignment.Left; caption.TextAlignment = TextAlignment.Left;
        return caption;
    }

    private static Control Rule()
    {
        var rule = new Border { Height = 1, Margin = new Thickness(0, 12), Opacity = .35 };
        rule.Bind(Border.BackgroundProperty, new VRageDynamicResource("Primary"));
        return rule;
    }

    private void Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
        MpMenu.Report("Copied " + text + " to the clipboard.");
    }

    private string HostAddress()
    {
        if (_directIp.IsChecked != true) return null;
        string published = MpMenu.Settings.PublishedAddress;
        return published.Length > 0 ? published : null;
    }

    private void ReadVisibility(OnlineSettings s) => _visibility = s.FriendsOnly ? FriendsOnly : s.Advertise ? Public : InviteAndCode;

    /// <summary>One click between public and private: saves only who can join, other unsaved edits on the page stay as they are.</summary>
    private void ToggleListing()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<OnlineSettings>(System.Text.Json.JsonSerializer.Serialize(MpMenu.Settings));
        _visibility = _visibility == Public ? InviteAndCode : Public;
        s.Advertise = _visibility == Public; s.FriendsOnly = false; s.AdvertiseChosen = true;
        MpMenu.SaveSettings(s);
        RefreshControls();
    }

    private void RefreshHostPage(bool busy, bool hosting)
    {
        var current = MpPlugin.Controller?.Current;
        bool live = hosting && MpMenu.HasWorld && NetSession.Current?.TransportReady == true;
        var settings = MpMenu.Settings;
        _statusPill.Background = live ? LiveBrush : hosting ? StartingBrush : IdleBrush;
        _statusPillText.Text = live ? "LIVE" : hosting ? "WAITING FOR A WORLD" : "NOT HOSTING";
        string name = (current?.SessionName ?? settings.SessionName)?.Trim();
        _heroTitle.Text = string.IsNullOrEmpty(name) ? "My SE2 session" : name;
        if (live)
        {
            int online = 1 + NetSession.Current.Players.Count(p => p.Peer != null);
            int max = current?.MaxPlayers ?? settings.MaxPlayers;
            _heroDetail.Text = $"{HostWorld.Current?.WorldName ?? "Your world"}   |   {online} / {max} players   |   {Visibilities[_visibility].Title}";
        }
        else if (hosting) _heroDetail.Text = "Hosting is on. Load or create a world and players can join it.";
        else _heroDetail.Text = "Start hosting, then load or create a world. Your current world can be hosted too.";

        _code.Text = "----  ----";
        _code.Opacity = .35;
        _codeHint.Text = "Join codes are not used by this version: players join through Steam, the server list or your address.";
        _copyCode.IsEnabled = false;

        string address = HostAddress();
        _hostAddress.Text = _directIp.IsChecked != true ? "Off" : address ?? "Port " + settings.ListenPort;
        _hostAddress.Opacity = address != null ? 1 : .6;
        _hostAddressHint.Text = _directIp.IsChecked != true ? "Turn on Direct IP below to let players connect by address."
            : address == null ? "Players join through Steam. To show an address here, set it under Advanced in Direct IP; players also need the port forwarded."
            : "Players need UDP port " + settings.ListenPort + " forwarded to this PC.";
        _copyAddress.IsEnabled = address != null;
        _listing.Text = _visibility switch
        {
            Public => "PUBLIC: listed in the session browser, anyone with SE2 Online can join",
            FriendsOnly => "FRIENDS ONLY: hidden from the session browser",
            _ => "PRIVATE: hidden from the session browser, join by invite or address"
        };
        _listing.Foreground = _visibility == Public ? LiveBrush : IdleBrush;
        _listingToggle.Content = _visibility == Public ? "Make private" : "Make public";
        _publicAddressField.IsVisible = _showAddressOverride;
        _advancedAddress.Content = _showAddressOverride ? "Hide advanced" : "Advanced";

        _host.IsVisible = !hosting;
        _load.IsVisible = _create.IsVisible = !MpMenu.HasWorld && hosting;
        _invite.IsVisible = _hostPlayers.IsVisible = _stopHost.IsVisible = hosting;
        _invite.IsEnabled = live;
        _stopHost.IsEnabled = !busy;
        _hostSave.Content = hosting ? "Apply host settings" : "Save host settings";
        for (int i = 0; i < _visibilityCards.Length; i++)
        {
            bool selected = i == _visibility;
            var card = _visibilityCards[i];
            card.BorderThickness = new Thickness(selected ? 3 : 1);
            if (selected) card.Bind(Border.BorderBrushProperty, new VRageDynamicResource("Primary"));
            else card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xC8, 0xCC, 0xD4));
            card.Child.Opacity = selected ? 1 : .7;
        }
    }
}
