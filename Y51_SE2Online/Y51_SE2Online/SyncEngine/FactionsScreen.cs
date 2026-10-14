using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Keen.VRage.UI.Screens;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>The factions controls, shown in the terminal's tab and in the pause menu's screen.</summary>
internal sealed class FactionsView
{
    private sealed record MemberRow(long Identity, string Name, string Role, FactionRole Rank);
    private readonly ListBox _factions = OnlineUi.List("FactionList"), _members = OnlineUi.List("FactionMembers");
    private readonly TextBox _search = OnlineUi.Input("Search factions", 48), _name = OnlineUi.Input("Faction name", 48), _tag = OnlineUi.Input("Tag", 5), _motd = OnlineUi.Input("Faction message of the day", SocialState.MaxMotd);
    private readonly TextBlock _status = OnlineUi.Text(""), _details = OnlineUi.Text("Select a faction.", "SubTitle", 30), _empty = OnlineUi.Text("No factions in this world yet."), _relation = OnlineUi.Text("", "SubtleNotice", 20);
    private readonly Button _create, _join, _leave, _invite, _approve, _reject, _remove, _leader, _disband, _promote, _demote, _setMotd, _ally, _peace, _war, _endTreaty, _acceptTreaty, _rejectTreaty;
    private string _motdFaction;
    private SocialSnapshot _snapshot = new();
    private string _signature, _membersSignature;
    private bool _closed;

    internal Control Navigation, Body, Actions, EmbeddedActions;
    internal TextBlock Status => _status;
    internal bool Closed { set => _closed = value; }

    public FactionsView()
    {
        _factions.ItemTemplate = new FuncDataTemplate<OnlineFaction>((f, _) => f == null ? null : OnlineUi.ListCells("100,*,90", f.Tag, f.Name, f.Members.Count.ToString()));
        _members.ItemTemplate = new FuncDataTemplate<MemberRow>((p, _) => p == null ? null : OnlineUi.ListCells("*,220", p.Name, p.Role));
        _search.Width = 480;
        _search.TextChanged += (_, _) => RefreshControls();
        _name.TextChanged += (_, _) => RefreshActions(); _tag.TextChanged += (_, _) => RefreshActions();
        _factions.SelectionChanged += (_, _) => RefreshMembers(); _members.SelectionChanged += (_, _) => RefreshActions();
        _create = OnlineUi.Button("Create faction", () => Send(new(SocialAction.CreateFaction, Name: _name.Text ?? "", Tag: _tag.Text ?? "")), "CreateFaction");
        _join = OnlineUi.Button("Request to join", () => Act((_factions.SelectedItem as OnlineFaction)?.Invitations.Contains(_snapshot.Actor) == true ? SocialAction.AcceptInvitation : SocialAction.RequestJoin), "JoinFaction");
        _leave = OnlineUi.Button("Leave faction", () => Confirm("Leave faction", "Leave your faction? Your private grids stay yours, and you lose access to its faction-shared grids.", new(SocialAction.LeaveFaction)), "LeaveFaction");
        _invite = OnlineUi.Button("Invite player", () => Act(SocialAction.Invite), "InviteFactionPlayer");
        _approve = OnlineUi.Button("Approve request", () => Act(SocialAction.ApproveRequest), "ApproveFactionRequest");
        _reject = OnlineUi.Button("Reject request", () => Act(SocialAction.RejectRequest), "RejectFactionRequest");
        _remove = OnlineUi.Button("Remove member", () => Confirm("Remove member", "Remove this player from the faction?", Request(SocialAction.RemoveMember)), "RemoveFactionMember");
        _leader = OnlineUi.Button("Make leader", () => Confirm("Transfer leadership", "Give this member control of faction membership and invitations?", Request(SocialAction.TransferLeadership)), "TransferFactionLeadership");
        _disband = OnlineUi.Button("Disband faction", () => Confirm("Disband faction", "Disband this faction? Members keep their grids, but faction sharing stops granting access.", Request(SocialAction.DisbandFaction)), "DisbandFaction");
        _promote = OnlineUi.Button("Promote", () => SetRole(+1), "PromoteFactionMember");
        _demote = OnlineUi.Button("Demote", () => SetRole(-1), "DemoteFactionMember");
        _setMotd = OnlineUi.Button("Set message", () => Send(new(SocialAction.SetFactionMotd, (_factions.SelectedItem as OnlineFaction)?.Id ?? "", Name: _motd.Text ?? "")), "SetFactionMotd");
        _ally = OnlineUi.Button("Propose alliance", () => Act(SocialAction.ProposeAlliance), "ProposeAlliance");
        _peace = OnlineUi.Button("Propose peace", () => Act(SocialAction.ProposePeace), "ProposePeace");
        _war = OnlineUi.Button("Declare war", () => Confirm("Declare war", "Declare war on this faction? Its members become hostile to yours while PvP is on, and turrets target them.", Request(SocialAction.DeclareWar)), "DeclareWar");
        _endTreaty = OnlineUi.Button("End treaty", () => Confirm("End treaty", "End your alliance or peace treaty with this faction?", Request(SocialAction.CancelTreaty)), "CancelTreaty");
        _acceptTreaty = OnlineUi.Button("Accept proposal", () => Act(SocialAction.AcceptProposal), "AcceptProposal");
        _rejectTreaty = OnlineUi.Button("Reject proposal", () => Act(SocialAction.RejectProposal), "RejectProposal");
        _motd.TextChanged += (_, _) => RefreshActions();
        foreach (var button in new[] { _invite, _approve, _reject, _remove, _leader, _disband, _promote, _demote, _setMotd, _ally, _peace, _war, _endTreaty, _acceptTreaty, _rejectTreaty }) { button.FontSize = 20; button.Padding = new Thickness(12, 8); }
        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };
        left.Children.Add(OnlineUi.Cells("100,*,90", "TAG", "FACTION", "SIZE"));
        Grid.SetRow(_factions, 1); left.Children.Add(_factions); Grid.SetRow(_empty, 1); _empty.IsHitTestVisible = false; left.Children.Add(_empty);
        var creation = OnlineUi.Stack(OnlineUi.Text("CREATE A FACTION", "SubTitle"), _name, OnlineUi.Row(_tag, _create));
        _tag.Width = 120; creation.Margin = new Thickness(0, 20, 0, 0); Grid.SetRow(creation, 2); left.Children.Add(creation);
        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto,Auto,Auto,Auto") };
        right.Children.Add(_details);
        var headers = OnlineUi.Cells("*,220", "PLAYER", "ROLE / REQUEST"); Grid.SetRow(headers, 1); right.Children.Add(headers);
        Grid.SetRow(_members, 2); right.Children.Add(_members);
        var actions = OnlineUi.Row(_invite, _approve, _reject); actions.Margin = new Thickness(0, 16, 0, 10); Grid.SetRow(actions, 3); right.Children.Add(actions);
        var management = OnlineUi.Row(_promote, _demote, _remove, _leader, _disband); Grid.SetRow(management, 4); right.Children.Add(management);
        var motd = OnlineUi.Row(_motd, _setMotd); _motd.Width = 520; motd.Margin = new Thickness(0, 10, 0, 0); Grid.SetRow(motd, 5); right.Children.Add(motd);
        _relation.Margin = new Thickness(0, 10, 0, 4); Grid.SetRow(_relation, 6); right.Children.Add(_relation);
        var diplomacy = OnlineUi.Row(_ally, _peace, _war, _endTreaty, _acceptTreaty, _rejectTreaty); Grid.SetRow(diplomacy, 7); right.Children.Add(diplomacy);
        Navigation = OnlineUi.Row(_search, _join);
        Body = OnlineUi.Columns(OnlineUi.Panel(left), OnlineUi.Panel(right), "600,24,*");
        Actions = OnlineUi.Row(_leave);
        EmbeddedActions = Actions;
        RefreshControls();
    }
    internal void QueueRefresh() => Dispatcher.UIThread.Post(() => { if (!_closed) RefreshControls(); });
    private void Send(SocialRequest request) => SocialUi.Send(request);
    private SocialRequest Request(SocialAction action) => new(action, (_factions.SelectedItem as OnlineFaction)?.Id ?? "", (_members.SelectedItem as MemberRow)?.Identity ?? 0);
    private void Act(SocialAction action) => Send(Request(action));
    private void SetRole(int step)
    {
        if (_members.SelectedItem is not MemberRow row) return;
        Send(Request(SocialAction.SetRole) with { Offset = (int)row.Rank + step });
    }
    private Diplomacy Between(OnlineFaction a, OnlineFaction b)
        => a == null || b == null || a == b ? Diplomacy.Neutral : RelationTable.Build(_snapshot.Factions, _snapshot.Pacts).Between(a.Id, b.Id);
    private void Confirm(string title, string text, SocialRequest request) => SocialUi.Confirm(title, text, () => Send(request));
    private void RefreshControls()
    {
        _snapshot = SocialUi.Snapshot;
        _status.Text = _snapshot.Actor == 0 ? "Start or join a multiplayer world to manage factions." : string.IsNullOrEmpty(_snapshot.Message) ? "Membership controls access to faction-shared grids. Private grids remain private." : _snapshot.Message;
        var rows = _snapshot.Factions.Where(f => string.IsNullOrWhiteSpace(_search.Text) || (f.Name + " " + f.Tag).Contains(_search.Text, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Tag).ToArray();
        string signature = _snapshot.Revision + ":" + _search.Text;
        if (_signature != signature)
        {
            string selected = (_factions.SelectedItem as OnlineFaction)?.Id;
            _signature = signature; _factions.ItemsSource = rows;
            _factions.SelectedItem = rows.FirstOrDefault(f => f.Id == selected) ?? rows.FirstOrDefault(f => f.Members.Contains(_snapshot.Actor));
        }
        _empty.IsVisible = rows.Length == 0;
        _empty.Text = _snapshot.Actor == 0 ? "No multiplayer world is active." : _snapshot.Factions.Length == 0 ? "No factions in this world yet." : "No matching factions.";
        RefreshMembers();
    }
    private void RefreshMembers()
    {
        var faction = _factions.SelectedItem as OnlineFaction;
        _details.Text = faction == null ? "Select a faction to view members and invitations." : "[" + faction.Tag + "] " + faction.Name + (faction.Motd.Length > 0 && faction.Members.Contains(_snapshot.Actor) ? "\n" + faction.Motd : "");
        if (faction?.Id != _motdFaction) { _motdFaction = faction?.Id; _motd.Text = faction?.Motd ?? ""; }
        string signature = _snapshot.Revision + ":" + faction?.Id;
        if (signature != _membersSignature)
        {
            long selected = (_members.SelectedItem as MemberRow)?.Identity ?? 0;
            _membersSignature = signature;
            var rows = faction == null ? Array.Empty<MemberRow>() : _snapshot.Players
                .Where(p => faction.Members.Contains(p.Identity) || faction.Requests.Contains(p.Identity) || faction.Invitations.Contains(p.Identity) || (faction.RoleOf(_snapshot.Actor) >= FactionRole.Moderator && faction.Members.Contains(_snapshot.Actor) && !_snapshot.Factions.Any(f => f.Members.Contains(p.Identity))))
                .Select(p => faction.Members.Contains(p.Identity)
                    ? new MemberRow(p.Identity, p.Name, faction.RoleOf(p.Identity).ToString(), faction.RoleOf(p.Identity))
                    : new MemberRow(p.Identity, p.Name, faction.Requests.Contains(p.Identity) ? "Join request" : faction.Invitations.Contains(p.Identity) ? "Invited" : "Unaffiliated", FactionRole.Member))
                .OrderBy(p => faction.Members.Contains(p.Identity) ? 3 - (int)p.Rank : 4).ThenBy(p => p.Name).ToArray();
            _members.ItemsSource = rows; _members.SelectedItem = rows.FirstOrDefault(p => p.Identity == selected);
        }
        RefreshActions();
    }
    private void RefreshActions()
    {
        if (_create == null) return;
        var own = _snapshot.Factions.FirstOrDefault(f => f.Members.Contains(_snapshot.Actor));
        var faction = _factions.SelectedItem as OnlineFaction; var player = _members.SelectedItem as MemberRow;
        bool ready = _snapshot.Actor != 0, leader = ready && faction?.Leader == _snapshot.Actor;
        var role = ready && faction != null && faction.Members.Contains(_snapshot.Actor) ? faction.RoleOf(_snapshot.Actor) : (FactionRole?)null;
        bool member = player != null && faction != null && faction.Members.Contains(player.Identity) && player.Identity != _snapshot.Actor;
        _create.IsEnabled = ready && own == null && (_name.Text?.Trim().Length ?? 0) >= 3 && (_tag.Text?.Trim().Length ?? 0) >= 2 && (_tag.Text ?? "").All(char.IsAsciiLetterOrDigit);
        _join.IsEnabled = ready && own == null && faction != null && (!faction.Requests.Contains(_snapshot.Actor) || faction.Invitations.Contains(_snapshot.Actor));
        _join.Content = faction?.Invitations.Contains(_snapshot.Actor) == true ? "Accept invitation" : faction?.Requests.Contains(_snapshot.Actor) == true ? "Join request pending" : "Request to join";
        _leave.IsEnabled = ready && own != null && (own.Leader != _snapshot.Actor || own.Members.Count == 1);
        _invite.IsEnabled = role >= FactionRole.Moderator && player != null && player.Role == "Unaffiliated";
        _approve.IsEnabled = _reject.IsEnabled = role >= FactionRole.Moderator && player?.Role == "Join request";
        _remove.IsEnabled = role >= FactionRole.Moderator && member && player.Rank < role;
        _leader.IsEnabled = leader && member;
        _disband.IsEnabled = leader;
        // Admins run moderators and members; only the leader promotes to or demotes admins.
        _promote.IsEnabled = member && player.Rank < role && (player.Rank + 1 < role || leader) && player.Rank < FactionRole.Admin;
        _demote.IsEnabled = member && player.Rank > FactionRole.Member && player.Rank < role && (player.Rank < FactionRole.Admin || leader);
        _setMotd.IsEnabled = role >= FactionRole.Admin && (_motd.Text ?? "") != (faction?.Motd ?? "");
        _motd.IsEnabled = role >= FactionRole.Admin;

        bool other = own != null && faction != null && faction != own, speak = other && own.RoleOf(_snapshot.Actor) >= FactionRole.Admin;
        var state = Between(own, faction);
        bool incoming = other && _snapshot.Proposals.Any(p => p.From == faction.Id && p.To == own.Id), outgoing = other && _snapshot.Proposals.Any(p => p.From == own.Id && p.To == faction.Id);
        _relation.Text = !other ? "" : "Relation with your faction: " + state + (incoming ? " - they proposed " + (_snapshot.Proposals.First(p => p.From == faction.Id && p.To == own.Id).State == Diplomacy.Allied ? "an alliance" : "peace") : outgoing ? " - your proposal is pending" : "");
        _ally.IsEnabled = speak && state != Diplomacy.Allied;
        _peace.IsEnabled = speak && state == Diplomacy.War;
        _war.IsEnabled = speak && state != Diplomacy.War;
        _endTreaty.IsEnabled = speak && state is Diplomacy.Allied or Diplomacy.Peace;
        _acceptTreaty.IsEnabled = _rejectTreaty.IsEnabled = speak && incoming;
    }
}

public sealed class FactionsScreen : ScreenView
{
    private readonly FactionsView _view = new();
    private SocialScreenViewModel _model;

    public FactionsScreen()
    {
        Content = OnlineUi.Shell(this, "FACTIONS", _view.Navigation, _view.Status, _view.Body, _view.Actions);
        OnDisposing += _ => { _view.Closed = true; if (_model != null) _model.Refresh -= _view.QueueRefresh; };
    }
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model != null) _model.Refresh -= _view.QueueRefresh;
        _model = DataContext as SocialScreenViewModel; if (_model != null) _model.Refresh += _view.QueueRefresh;
    }
}
