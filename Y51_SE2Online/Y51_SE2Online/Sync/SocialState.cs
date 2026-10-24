using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Y51_SE2Online.Sync;

public enum GridSharing { Private, Faction, Everyone }
public enum SocialAction
{
    Refresh, CreateFaction, RequestJoin, Invite, AcceptInvitation, ApproveRequest,
    RejectRequest, LeaveFaction, RemoveMember, TransferLeadership, DisbandFaction,
    ClaimGrid, TransferGrid, ReleaseGrid, ShareGrid,
    /// <summary>Suit broadcast: Offset 1 on, 0 off.</summary>
    Broadcast,
    /// <summary>Block whitelist/blacklist for Player: Offset 1 adds, 0 removes. The block is Secure (its control panel id).</summary>
    BlockAllow, BlockDeny,
    /// <summary>Sets Player's faction role to Offset (a <see cref="FactionRole"/> below Leader).</summary>
    SetRole,
    /// <summary>Name is the faction's message of the day; empty clears it.</summary>
    SetFactionMotd,
    /// <summary>Diplomacy with FactionId (another faction), spoken for the actor's own faction.</summary>
    ProposeAlliance, ProposePeace, DeclareWar, CancelTreaty, AcceptProposal, RejectProposal,
    /// <summary>Antenna HUD marker shows its owner's faction tag: Offset 1 on, 0 off. The block is Secure.</summary>
    AntennaTag,
    /// <summary>Turns down FactionId's invitation.</summary>
    DeclineInvitation,
    /// <summary>Offset 1 starts a hack at the nearest terminal (or counter-hacks one on your own grid), 0 cancels yours, 2 says a terminal was opened.</summary>
    Hack,
    /// <summary>Freezes the hack on Grid (0: every hack on your side's grids).</summary>
    Lockdown
}

public sealed record SocialRequest(SocialAction Action, string FactionId = "", long Player = 0,
    uint Grid = 0, GridSharing Sharing = GridSharing.Private, string Name = "", string Tag = "", int Offset = 0, Guid Secure = default);
public sealed record SocialPlayer(long Identity, string Name);
/// <param name="Secure">On a snapshot's Focus: the control panel id the Refresh asked for, so a panel never shows another grid of the same name.</param>
public sealed record SocialGrid(uint NetId, string Name, long Owner, GridSharing Sharing, Guid Secure = default);
/// <summary>A saved antenna faction-tag switch: SE2 entities keep no id across a save, so the antenna is found again by its grid cell and position.</summary>
public sealed record TaggedAntenna(int X, int Y, int Z, double PX, double PY, double PZ);
/// <param name="Lists">False for block types SE2 gives no per-block access (lights, doors, thrusters...): they follow the grid.</param>
public sealed record SocialBlock(uint NetId, string Name, long Owner, long[] Allow, long[] Deny, Guid Secure, bool Antenna = false, bool ShowTag = false, bool Lists = true);
public sealed class OnlineFaction
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Tag { get; set; } = "";
    public long Leader { get; set; }
    public List<long> Members { get; set; } = new();
    public List<long> Invitations { get; set; } = new();
    public List<long> Requests { get; set; } = new();
    public List<long> Admins { get; set; } = new();
    public List<long> Moderators { get; set; } = new();
    public string Motd { get; set; } = "";

    public FactionRole RoleOf(long player) => player == Leader ? FactionRole.Leader : Admins.Contains(player) ? FactionRole.Admin : Moderators.Contains(player) ? FactionRole.Moderator : FactionRole.Member;

    internal void ForgetRole(long player) { Admins.Remove(player); Moderators.Remove(player); }
}

public sealed class SocialSnapshot
{
    public long Revision { get; set; }
    public long Actor { get; set; }
    public string Message { get; set; } = "";
    public SocialPlayer[] Players { get; set; } = Array.Empty<SocialPlayer>();
    public OnlineFaction[] Factions { get; set; } = Array.Empty<OnlineFaction>();
    public SocialGrid[] Grids { get; set; } = Array.Empty<SocialGrid>();
    public int TotalGrids { get; set; }
    public int Offset { get; set; }
    /// <summary>The grid a Refresh asked for by its control panel id, whatever the page shows.</summary>
    public SocialGrid Focus { get; set; }
    /// <summary>The single block a Refresh asked for, with its whitelist and blacklist.</summary>
    public SocialBlock FocusBlock { get; set; }
    public FactionPact[] Pacts { get; set; } = Array.Empty<FactionPact>();
    public FactionProposal[] Proposals { get; set; } = Array.Empty<FactionProposal>();
    /// <summary>
    /// Blocks (NetIds) whose own list whitelists or blacklists this snapshot's player, and blocks open to everyone on
    /// grids that are not: block access groups exist only on the server, so a joiner predicts access from these.
    /// </summary>
    public uint[] AllowedBlocks { get; set; } = Array.Empty<uint>();
    public uint[] DeniedBlocks { get; set; } = Array.Empty<uint>();
    public uint[] OpenBlocks { get; set; } = Array.Empty<uint>();
    /// <summary>Hacks this player runs or defends against.</summary>
    public HackView[] Hacks { get; set; } = Array.Empty<HackView>();

    /// <summary>The lists every player's snapshot shares; serialized once per publish, not once per player.</summary>
    public static readonly string[] Common = { nameof(Players), nameof(Factions), nameof(Pacts), nameof(Proposals), nameof(OpenBlocks) };

    /// <summary>This snapshot without the <see cref="Common"/> lists or the grid page, which players with the same view share.</summary>
    public SocialSnapshot OwnPart()
    {
        var own = (SocialSnapshot)MemberwiseClone();
        own.Players = Array.Empty<SocialPlayer>(); own.Factions = Array.Empty<OnlineFaction>(); own.Pacts = Array.Empty<FactionPact>();
        own.Proposals = Array.Empty<FactionProposal>(); own.OpenBlocks = Array.Empty<uint>(); own.Grids = Array.Empty<SocialGrid>();
        return own;
    }
}

public sealed class SocialState
{
    public int Version { get; set; } = 1;
    public long Revision { get; set; }
    public List<SocialPlayer> Players { get; set; } = new();
    public List<OnlineFaction> Factions { get; set; } = new();
    public List<FactionPact> Pacts { get; set; } = new();
    public List<FactionProposal> Proposals { get; set; } = new();
    /// <summary>Antennas whose HUD marker shows their owner's faction tag (SE2 antennas have no block permissions to keep the flag in).</summary>
    public List<TaggedAntenna> TaggedAntennas { get; set; } = new();
    /// <summary>The world's block lists carry their owner (<see cref="BlockAcl.Owner"/>); older lists were stamped once.</summary>
    public bool AclOwners { get; set; }
    /// <summary>The first load of this world since 0.18.0 has reviewed its grids' sharing (<see cref="SharingAtLoad"/>).</summary>
    public bool SharingReviewed { get; set; }
    /// <summary>Players whose suit broadcast was last off; their parked character gets no name tag while they are offline.</summary>
    public HashSet<long> Silent { get; set; } = new();
    /// <summary>
    /// The own MP identity of whoever last hosted this world as <see cref="PlayerIdentities.HotfixIdentity"/>. The game's
    /// save keeps that identity on their grids; a dedicated server, where nobody plays as it, gives the grids to this one.
    /// </summary>
    public long HostIdentity { get; set; }
    /// <summary>Dedicated: the owner for grids the save gives to <see cref="PlayerIdentities.HotfixIdentity"/>; 0 when nobody is known.</summary>
    [JsonIgnore] public long HostHeir => PlayerIdentities.IsHuman(HostIdentity) && HostIdentity != PlayerIdentities.HotfixIdentity ? HostIdentity : 0;
    public const int MaxMotd = 300;

    public static bool CanAccess(long owner, long actor, GridSharing sharing, IEnumerable<OnlineFaction> factions)
    {
        if (!PlayerIdentities.IsHuman(actor)) return false;
        if (owner == actor) return true;
        if (PlayerIdentities.IsNpc(owner)) return false;
        if (sharing == GridSharing.Everyone) return true;
        return sharing == GridSharing.Faction && owner != 0 && factions.Any(f => f.Members.Contains(owner) && f.Members.Contains(actor));
    }

    public OnlineFaction FactionOf(long actor) => Factions.FirstOrDefault(f => f.Members.Contains(actor));
    /// <summary>An antenna's faction tag switch: its owner, or a leader or admin of the owner's faction.</summary>
    public bool MayTag(long owner, long actor)
        => PlayerIdentities.IsHuman(actor) && PlayerIdentities.IsHuman(owner) && (owner == actor || FactionOf(owner) is { } f && f.Members.Contains(actor) && f.RoleOf(actor) >= FactionRole.Admin);
    public static bool CanManageGrid(long owner, long actor) => PlayerIdentities.IsHuman(actor) && (owner == actor || owner == 0);

    /// <summary>
    /// Host, world load: the sharing a player's grid gets, or null to keep it. <paramref name="chosen"/>: the grid carries
    /// SE2 Online's own sharing. Team grids share the grids of a world SE2 Online never hosted (an older or single-player
    /// world), once. A grid with SE2 Online's sharing always keeps it: up to 0.18.0-pre4 team grids wrote "everyone" at
    /// every load, and that value cannot be told apart from an owner's own choice (see <see cref="SharingUndecided"/>).
    /// </summary>
    public static GridSharing? SharingAtLoad(bool chosen, GridSharing current, bool teamGrids, bool hostedBefore, bool reviewed)
        => chosen ? null : teamGrids && !hostedBefore ? GridSharing.Everyone : current;

    /// <summary>The first load since 0.18.0-pre5 of a world hosted with team grids: a grid shared with everyone may be the old
    /// team grids rule or its owner's choice. It stays shared and is counted in the log.</summary>
    public static bool SharingUndecided(bool chosen, GridSharing current, bool teamGrids, bool hostedBefore, bool reviewed)
        => chosen && current == GridSharing.Everyone && teamGrids && hostedBefore && !reviewed;

    /// <summary>
    /// Host: the stable key (Steam id) of the player playing as <see cref="PlayerIdentities.HotfixIdentity"/>, the identity
    /// SE2 gives the host of every world. Saves name that player by their own MP identity instead, the one every server
    /// derives from the same key, so a world moved to a dedicated server or hosted by someone else keeps them.
    /// </summary>
    [JsonIgnore] public string HostKey { get; set; }

    /// <param name="key">The host's own stable key when <paramref name="identity"/> is the host's.</param>
    public void Register(long identity, string name, string key = null)
    {
        if (!PlayerIdentities.IsHuman(identity)) return;
        name = Clean(name, 64);
        if (identity == PlayerIdentities.HotfixIdentity && key != null && key != HostKey) TakeHostSlot(key, name);
        int index = Players.FindIndex(p => p.Identity == identity);
        if (index >= 0 && Players[index].Name == name) return;
        if (index >= 0) Players[index] = new(identity, name);
        else Players.Add(new(identity, name));
        Revision++;
    }

    public string Apply(long actor, SocialRequest request)
    {
        if (!PlayerIdentities.IsHuman(actor) || !Players.Any(p => p.Identity == actor)) return "Your player identity is not ready.";
        if (!Enum.IsDefined(request.Action)) return "Unknown action.";
        var own = FactionOf(actor);
        var faction = Factions.FirstOrDefault(f => f.Id == request.FactionId);
        if (request.Action == SocialAction.CreateFaction)
        {
            if (own != null) return "Leave your current faction first.";
            string name = Clean(request.Name, 48), tag = Clean(request.Tag, 5).ToUpperInvariant();
            if (name.Length < 3 || tag.Length < 2 || !tag.All(char.IsAsciiLetterOrDigit)) return "Use a name of 3-48 characters and a tag of 2-5 letters or digits.";
            if (Factions.Count >= 256) return "This world has reached its faction limit.";
            if (Factions.Any(f => f.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase) || f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return "That faction name or tag is already in use.";
            ClearPending(actor);
            Factions.Add(new OnlineFaction { Id = Guid.NewGuid().ToString("N"), Name = name, Tag = tag, Leader = actor, Members = new() { actor } });
            Revision++; return "Faction created.";
        }
        if (request.Action == SocialAction.LeaveFaction)
        {
            if (own == null) return "You are not in a faction.";
            if (own.Leader == actor && own.Members.Count > 1) return "Transfer leadership before leaving.";
            own.Members.Remove(actor); own.ForgetRole(actor);
            if (own.Members.Count == 0) { Factions.Remove(own); FactionDiplomacy.Forget(this, own.Id); }
            Revision++; return "You left the faction.";
        }
        if (faction == null) return "That faction no longer exists.";
        if (request.Action is >= SocialAction.ProposeAlliance and <= SocialAction.RejectProposal)
        {
            if (own == null || own.RoleOf(actor) < FactionRole.Admin) return "Only your faction's leader or an admin can handle diplomacy.";
            string result = FactionDiplomacy.Apply(this, own, faction, request.Action);
            Revision++; return result;
        }
        if (request.Action == SocialAction.RequestJoin)
        {
            if (own != null) return "Leave your current faction first.";
            if (faction.Requests.Contains(actor)) return "Join request sent to the faction leader.";
            if (Recently(faction.Id, actor)) return "Wait a moment before applying to that faction again.";
            faction.Requests.Add(actor); Remember(faction.Id, actor); Revision++;
            return "Join request sent to the faction leader.";
        }
        if (request.Action == SocialAction.DeclineInvitation)
        {
            if (!faction.Invitations.Remove(actor)) return "That invitation is no longer pending.";
            Remember(faction.Id, actor); Revision++; return "Invitation declined.";
        }
        if (request.Action == SocialAction.AcceptInvitation)
        {
            if (own != null) return "Leave your current faction first.";
            if (!faction.Invitations.Contains(actor)) return "You have not been invited to that faction.";
            ClearPending(actor); faction.Members.Add(actor); Revision++; return "You joined the faction.";
        }
        if (!faction.Members.Contains(actor)) return "You are not a member of that faction.";
        var role = faction.RoleOf(actor);
        var needed = Needed(request.Action);
        if (role < needed) return needed switch
        {
            FactionRole.Leader => "Only the faction leader can do that.",
            FactionRole.Admin => "Only the faction leader or an admin can do that.",
            _ => "Only the faction leader, admins and moderators can do that."
        };
        bool known = Players.Any(p => p.Identity == request.Player);
        switch (request.Action)
        {
            case SocialAction.SetRole:
            {
                var wanted = (FactionRole)request.Offset;
                if (!Enum.IsDefined(wanted) || wanted == FactionRole.Leader) return "Use Make leader to hand over the faction.";
                if (request.Player == actor || !faction.Members.Contains(request.Player)) return "Choose another faction member.";
                // Admins run moderators and members; only the leader promotes to or removes admins.
                var current = faction.RoleOf(request.Player);
                if (current >= role || wanted >= role && role != FactionRole.Leader || wanted == FactionRole.Admin && role != FactionRole.Leader) return "You can only change the role of members below you.";
                faction.ForgetRole(request.Player);
                if (wanted == FactionRole.Admin) faction.Admins.Add(request.Player);
                else if (wanted == FactionRole.Moderator) faction.Moderators.Add(request.Player);
                break;
            }
            case SocialAction.SetFactionMotd:
                faction.Motd = Clean(request.Name, MaxMotd); break;
            case SocialAction.Invite:
                if (!known || FactionOf(request.Player) != null) return "Choose a player who is not already in a faction.";
                if (faction.Invitations.Contains(request.Player)) return "That player already has an invitation.";
                if (Recently(faction.Id, request.Player)) return "Wait a moment before inviting that player again.";
                faction.Invitations.Add(request.Player); Remember(faction.Id, request.Player);
                break;
            case SocialAction.ApproveRequest:
                if (!known || !faction.Requests.Contains(request.Player) || FactionOf(request.Player) != null) return "That player has no eligible join request.";
                ClearPending(request.Player); faction.Members.Add(request.Player); break;
            case SocialAction.RejectRequest:
                if (!faction.Requests.Remove(request.Player)) return "That join request is no longer pending.";
                Remember(faction.Id, request.Player);
                break;
            case SocialAction.RemoveMember:
                if (request.Player == actor) return "Use Leave faction to leave.";
                if (!faction.Members.Contains(request.Player)) return "Choose a faction member.";
                if (faction.RoleOf(request.Player) >= role) return "You can only remove members below your own role.";
                faction.Members.Remove(request.Player); faction.ForgetRole(request.Player);
                break;
            case SocialAction.TransferLeadership:
                if (request.Player == actor || !faction.Members.Contains(request.Player)) return "Choose another faction member.";
                faction.ForgetRole(request.Player); faction.Leader = request.Player; faction.Admins.Add(actor); break;
            case SocialAction.DisbandFaction:
                Factions.Remove(faction); FactionDiplomacy.Forget(this, faction.Id); break;
            default: return "Unknown faction action.";
        }
        Revision++; return "Faction updated.";
    }

    /// <summary>The lowest faction role that may take a membership action.</summary>
    public static FactionRole Needed(SocialAction action) => action switch
    {
        SocialAction.Invite or SocialAction.ApproveRequest or SocialAction.RejectRequest or SocialAction.RemoveMember => FactionRole.Moderator,
        SocialAction.SetRole or SocialAction.SetFactionMotd => FactionRole.Admin,
        _ => FactionRole.Leader
    };

    // ponytail: one cooldown per faction and player, kept in memory only; a restart forgets it.
    public const long AskCooldownMs = 30_000;
    private readonly Dictionary<(string Faction, long Player), long> _asked = new();
    private bool Recently(string faction, long player) => _asked.TryGetValue((faction, player), out long at) && Environment.TickCount64 - at < AskCooldownMs;
    private void Remember(string faction, long player)
    {
        if (_asked.Count > 4096) _asked.Clear();
        _asked[(faction, player)] = Environment.TickCount64;
    }

    private void ClearPending(long player)
    {
        foreach (var faction in Factions) { faction.Requests.Remove(player); faction.Invitations.Remove(player); }
    }

    private void TakeHostSlot(string key, string name)
    {
        const long host = PlayerIdentities.HotfixIdentity;
        if (HostKey != null) Merge(host, PlayerIdentities.Derive(HostKey));
        // Saves from before 0.15.4 kept the host's identity: it stays with this host unless another saved player has its name.
        else if (Players.Find(p => p.Identity == host) is { } saved && saved.Name != name && Namesake(saved) is long other) Merge(host, other);
        HostKey = key;
        Merge(PlayerIdentities.Derive(key), host);
    }

    /// <summary>
    /// Dedicated: <see cref="PlayerIdentities.HotfixIdentity"/> is a SE2 4.0 player's own identity and that player's
    /// <paramref name="derived"/> identity is the saved <see cref="HostIdentity"/>: their faction place comes back to it.
    /// </summary>
    public bool CanReclaim(long derived) => derived != 0 && HostIdentity == derived;

    /// <summary>Last step, after the grids moved: the faction place follows and the saved host slot is cleared.</summary>
    public bool ReclaimHostSlot(long derived)
    {
        if (!CanReclaim(derived)) return false;
        Merge(derived, PlayerIdentities.HotfixIdentity);
        HostIdentity = 0;
        return true;
    }

    /// <summary>
    /// Dedicated, at the world's first tick, while no SE2 4.0 player owns the host's identity: nobody plays as it there. Saves from before 0.15.4 kept it for whoever hosted;
    /// it goes to the one other saved player of the same name, or leaves its faction unless it is the faction's only member.
    /// Its grids do not follow a name match (names are chosen at join): without a saved <see cref="HostIdentity"/> they are
    /// left for anyone to claim.
    /// </summary>
    public void SettleHostSlot()
    {
        const long host = PlayerIdentities.HotfixIdentity;
        var saved = Players.Find(p => p.Identity == host);
        if (saved == null) return;
        if (Namesake(saved) is long other) Merge(host, other);
        else if (FactionOf(host) is not { Members.Count: 1 }) Forget(host);
    }

    private long? Namesake(SocialPlayer player)
    {
        var same = Players.Where(p => p.Identity != player.Identity && p.Name == player.Name).Take(2).ToList();
        return same.Count == 1 ? same[0].Identity : null;
    }

    /// <summary>
    /// <paramref name="from"/> is the same player as <paramref name="to"/>: one faction place is kept, the higher-ranked
    /// one (a leader stays leader), and invitations, requests and the name move over.
    /// </summary>
    public void Merge(long from, long to)
    {
        if (from == to || !PlayerIdentities.IsHuman(to) || !Players.Any(p => p.Identity == from)) return;
        var a = FactionOf(from); var b = FactionOf(to);
        if (a != null && (b == null || a == b || a.RoleOf(from) > b.RoleOf(to)))
        {
            var rank = b == a && a.RoleOf(to) > a.RoleOf(from) ? a.RoleOf(to) : a.RoleOf(from);
            if (b != null && b != a) Leave(b, to);
            a.ForgetRole(from); a.ForgetRole(to);
            if (a.Members.Contains(to)) a.Members.Remove(from); else a.Members[a.Members.IndexOf(from)] = to;
            if (rank == FactionRole.Leader) a.Leader = to;
            else if (rank == FactionRole.Admin) a.Admins.Add(to);
            else if (rank == FactionRole.Moderator) a.Moderators.Add(to);
        }
        else if (a != null) Leave(a, from);
        foreach (var faction in Factions)
            foreach (var list in new[] { faction.Requests, faction.Invitations })
                if (list.Remove(from) && !list.Contains(to)) list.Add(to);
        if (FactionOf(to) != null) ClearPending(to);
        string name = Players.Find(p => p.Identity == from).Name;
        Players.RemoveAll(p => p.Identity == from);
        if (!Players.Any(p => p.Identity == to)) Players.Add(new(to, name));
        Revision++;
    }

    private void Forget(long player)
    {
        if (FactionOf(player) is { } faction) Leave(faction, player);
        ClearPending(player);
        Players.RemoveAll(p => p.Identity == player);
        Revision++;
    }

    /// <summary>Takes <paramref name="player"/> out of <paramref name="faction"/>; a leader hands over to the most senior member left.</summary>
    private void Leave(OnlineFaction faction, long player)
    {
        faction.Members.Remove(player); faction.ForgetRole(player);
        if (faction.Leader != player) return;
        if (faction.Members.Count == 0) { Factions.Remove(faction); FactionDiplomacy.Forget(this, faction.Id); return; }
        faction.Leader = faction.Admins.FirstOrDefault() is long admin and not 0 ? admin : faction.Moderators.FirstOrDefault() is long moderator and not 0 ? moderator : faction.Members[0];
        faction.ForgetRole(faction.Leader);
    }

    /// <summary>The save's copy, with the host named by their own MP identity (see <see cref="HostKey"/>).</summary>
    public byte[] SerializeForSave()
    {
        if (HostKey == null) return Serialize();
        var copy = Deserialize(Serialize());
        copy.HostIdentity = PlayerIdentities.Derive(HostKey);
        copy.Merge(PlayerIdentities.HotfixIdentity, copy.HostIdentity);
        return copy.Serialize();
    }

    public static string Clean(string value, int limit) => new string((value ?? "").Where(c => !char.IsControl(c)).Take(limit).ToArray()).Trim();
    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this);
    public static SocialState Deserialize(ReadOnlySpan<byte> data)
    {
        var state = JsonSerializer.Deserialize<SocialState>(data) ?? throw new InvalidOperationException("Empty faction save.");
        state.Validate(); return state;
    }
    public void Validate()
    {
        if (Version != 1 || Players == null || Factions == null || Players.Count > 10000 || Factions.Count > 256) throw new InvalidOperationException("Invalid faction save schema.");
        var players = new HashSet<long>(); var members = new HashSet<long>(); var ids = new HashSet<string>(); var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var player in Players)
            if (player == null || !PlayerIdentities.IsHuman(player.Identity) || player.Name == null || player.Name.Length > 64 || !players.Add(player.Identity)) throw new InvalidOperationException("Invalid saved player.");
        foreach (var faction in Factions)
        {
            if (faction == null || !Guid.TryParseExact(faction.Id, "N", out _) || !ids.Add(faction.Id) || faction.Name == null || faction.Name.Length < 3 || faction.Name.Length > 48 || faction.Tag == null || faction.Tag.Length < 2 || faction.Tag.Length > 5 || !faction.Tag.All(char.IsAsciiLetterOrDigit) || !tags.Add(faction.Tag) || faction.Members == null || faction.Requests == null || faction.Invitations == null || !faction.Members.Contains(faction.Leader) || faction.Members.Count > 10000 || faction.Requests.Count > 10000 || faction.Invitations.Count > 10000) throw new InvalidOperationException("Invalid saved faction.");
            foreach (long member in faction.Members) if (!players.Contains(member) || !members.Add(member)) throw new InvalidOperationException("Invalid faction membership.");
            if (faction.Requests.Any(p => !players.Contains(p)) || faction.Invitations.Any(p => !players.Contains(p))) throw new InvalidOperationException("Invalid faction invitation or request.");
            faction.Admins ??= new(); faction.Moderators ??= new(); faction.Motd ??= "";
            if (faction.Motd.Length > MaxMotd || faction.Admins.Concat(faction.Moderators).Any(p => p == faction.Leader || !faction.Members.Contains(p)) || faction.Admins.Intersect(faction.Moderators).Any()) throw new InvalidOperationException("Invalid faction roles.");
        }
        Pacts ??= new(); Proposals ??= new(); Silent ??= new();
        TaggedAntennas = (TaggedAntennas ?? new()).Where(a => a != null).Take(65536).ToList();
        var pairs = new HashSet<(string, string)>();
        if (Pacts.Count > 65536 || Proposals.Count > 4096) throw new InvalidOperationException("Invalid faction diplomacy.");
        foreach (var pact in Pacts)
            if (pact == null || !ids.Contains(pact.A) || !ids.Contains(pact.B) || pact.A == pact.B || pact.State == Diplomacy.Neutral || !Enum.IsDefined(pact.State) || !pairs.Add(RelationTable.Key(pact.A, pact.B))) throw new InvalidOperationException("Invalid faction treaty.");
        foreach (var proposal in Proposals)
            if (proposal == null || !ids.Contains(proposal.From) || !ids.Contains(proposal.To) || proposal.From == proposal.To || proposal.State is not (Diplomacy.Peace or Diplomacy.Allied)) throw new InvalidOperationException("Invalid faction proposal.");
    }
}

public static class SocialMessage
{
    public const int MaxBytes = 1048576;
    public static byte[] Encode<T>(SyncMsg type, T value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value);
        if (json.Length >= MaxBytes) throw new ArgumentException("Faction message is too large.");
        byte[] data = new byte[json.Length + 1]; data[0] = (byte)type; json.CopyTo(data, 1); return data;
    }
    public static T Decode<T>(byte[] data)
    {
        if (data == null || data.Length < 3 || data.Length > MaxBytes || (typeof(T) == typeof(SocialRequest) && data.Length > 4096)) throw new ArgumentException("Invalid faction message size.");
        try { return JsonSerializer.Deserialize<T>(data.AsSpan(1)) ?? throw new ArgumentException("Empty faction message."); }
        catch (JsonException e) { throw new ArgumentException("Invalid faction message.", e); }
    }
}
