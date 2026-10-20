using System;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.GameSystems.Respawn;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Threading;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// The player's half of SE2 Online's spawn menu on SE2 4.0. Keen's client asks for a body through
/// ClientRespawnSessionComponent.RequestRespawnScreenAsync (first join without a character, and after the death screen);
/// in an SE2 Online session that asks the host instead (<see cref="HostWorld.RequestCharacter"/>), shows its menu
/// (<see cref="RespawnScreen"/>), then possesses the character the host made, the way Keen possesses its own respawns.
/// The listen host's own player takes the same path to its HostWorld without the network.
/// </summary>
internal static class KeenRespawn
{
    // A ship takes up to 30 s to spawn after the choice; the host answers everything else within a tick.
    private const long AnswerTimeoutMs = 60_000;

    private const int ResourceWaitTicks = 60;

    private static volatile SpawnMenu? _localMenu;
    private static volatile string? _localFailure;
    private static volatile bool _died;
    [ThreadStatic] private static bool _keenOwn;
    private static SpawnMenu? _logged;

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(ClientRespawnSessionComponent), nameof(ClientRespawnSessionComponent.RequestRespawnScreenAsync)), prefix: new HarmonyMethod(typeof(KeenRespawn), nameof(RespawnScreenPrefix)));
        harmony.Patch(AccessTools.Method(typeof(ClientRespawnSessionComponent), nameof(ClientRespawnSessionComponent.OnLocalPlayerDeath)), prefix: new HarmonyMethod(typeof(KeenRespawn), nameof(DeathPrefix)));
        harmony.Patch(AccessTools.Method(typeof(Keen.Game2.GameAppComponent), "GameUpdate"), postfix: new HarmonyMethod(typeof(KeenRespawn), nameof(GameUpdatePostfix)));
    }

    private static bool Local => AdminAccess.Hosting && HostWorld.Current != null;

    /// <summary>The host's spawn choices while this player waits for a body; null once chosen.</summary>
    internal static SpawnMenu? Menu => Local ? _localMenu : ClientWorld.Current?.BoundSession?.SpawnMenu;

    internal static void Choose(int index)
    {
        if (!Local)
        {
            ClientWorld.Current?.BoundSession?.ChooseSpawn(index);
            return;
        }
        var menu = _localMenu;
        if (menu == null || index < 0 || index >= menu.Options.Length || !menu.Options[index].Available) return;
        _localMenu = null;
        HostWorld.Current?.RequestLocalCharacter(index, menu.Respawn);
    }

    /// <summary>Server thread: the listen host's own menu (null once its character exists).</summary>
    internal static void ShowLocal(SpawnMenu? menu) => _localMenu = menu;

    internal static void LocalFailed(string why)
    {
        _localMenu = null;
        _localFailure = why;
    }

    private static void DeathPrefix() => _died = true;

    private static bool RespawnScreenPrefix(ClientRespawnSessionComponent __instance, ref Task __result)
    {
        if (_keenOwn || !(Local || AdminAccess.Joined)) return true;
        __result = SpawnThroughHost(__instance);
        return false;
    }

    private static async Task SpawnThroughHost(ClientRespawnSessionComponent respawn)
    {
        await Task.SetLifetime(respawn);
        bool died = _died, local = Local;
        _died = false;
        _localFailure = null;
        var controller = respawn._players.LocalPlayerController;
        var client = ClientWorld.Current?.BoundSession;
        long identity = OwnIdentity(respawn, local);
        long version = client?.Assignment.Version ?? 0;
        controller.RequestUnpossessionAll();
        if (local) HostWorld.Current?.RequestLocalCharacter(-1, died);
        else client?.RequestCharacter(died ? CharacterRequestMessage.Respawn : CharacterRequestMessage.Initial);
        GameLog.Instance.Info($"spawn: asked the host for the spawn menu ({(died ? "respawn" : "first spawn")}, IdentityId {identity}{(local ? ", listen host" : "")})");
        long since = MpClock.NowMs();
        Entity? body;
        string? failure = null;
        while (true)
        {
            await respawn._timer.Delay(TimeSpan.FromMilliseconds(200), out var timer);
            timer.Dispose();
            body = OwnLivingCharacter(identity);
            if (body != null) break;
            failure = local ? _localFailure : client?.Assignment is { Status: PlayerAssignMessage.Failed } a && a.Version != version ? a.Text : null;
            if (failure != null) break;
            // No deadline while the player picks from the menu.
            if (Menu != null) since = MpClock.NowMs();
            else if (MpClock.NowMs() - since > AnswerTimeoutMs) { failure = "the host did not answer"; break; }
        }
        if (failure != null)
        {
            GameLog.Instance.Warn($"spawn: the host gave no character ({failure}); the game's own respawn runs");
            Task own;
            _keenOwn = true;
            try { own = respawn.RequestRespawnScreenAsync(); }
            finally { _keenOwn = false; }
            await own;
            return;
        }
        // What ClientRespawnSessionComponent.RequestCharacterSpawnAsync does with the character the server made; the resource
        // wait is bounded because a hidden or minimised game never finished it around a planet lander.
        GameLog.Instance.Info("spawn: the host's character is here; waiting for its surroundings to load");
        var loading = respawn._resourceLoading.AwaitAllResourcesLoaded();
        for (int i = 0; i < ResourceWaitTicks && !loading.IsCompleted; i++)
        {
            await respawn._timer.Delay(TimeSpan.FromMilliseconds(250), out var wait);
            wait.Dispose();
        }
        if (!loading.IsCompleted) GameLog.Instance.Warn("spawn: surroundings still loading after 15 s; taking the character anyway");
        controller.RequestUnpossessionAll();
        respawn.SubscribeToCharacterDeath(body);
        controller.RequestPossession(body.Single<IControllable>());
        respawn.OnClientRespawn(body);
        if (local) HostWorld.Current?.OnLocalPossessed();
        else client?.RequestCharacter(CharacterRequestMessage.Possessed);
        var at = body.Data.GetWorldTransform().Position;
        GameLog.Instance.Info($"spawn: possessed the character the host made at {at.X:0.0}, {at.Y:0.0}, {at.Z:0.0}");
        // A respawn ship's pilot is seated by the host after this report; the cockpit takes the control over here.
        for (int i = 0; i < 40; i++)
        {
            await respawn._timer.Delay(TimeSpan.FromMilliseconds(250), out var wait);
            wait.Dispose();
            if (body.AsInterface<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.ISeatable>()?.CurrentSeat is not { } seat) continue;
            var top = controller.GetTopControllable()?.Entity;
            GameLog.Instance.Info($"spawn: seated in {seat.Entity.GetComposition()?.DebugNameShort}; {(top == seat.Entity ? "controlling it" : "NOT controlling it (top: " + top?.GetComposition()?.DebugNameShort + ")")}");
            break;
        }
#if !PULSAR
        // Rig: -y51online:spawndie:<seconds> dies once that long after the first spawn, to run the game's death flow.
        if (!_rigDied && MpPlugin.LaunchArgs.FirstOrDefault(a => a.StartsWith(MpOptions.Prefix + "spawndie:", StringComparison.OrdinalIgnoreCase)) is { } die)
        {
            _rigDied = true;
            await respawn._timer.Delay(TimeSpan.FromSeconds(int.Parse(die.Substring(MpOptions.Prefix.Length + "spawndie:".Length))), out var wait);
            wait.Dispose();
            GameLog.Instance.Info("spawn: rig asks for its character's death");
            body.Get<CharacterLifeComponent>().RequestDeath();
        }
#endif
    }

#if !PULSAR
    private static bool _rigDied;
    private static long _rigRequestAt;

    // Rig: -y51online:spawnrequest, 100 s after start in an SE2 Online session, asks for a body (a world loaded by the autohost
    // rig skips the game's own initial request) or kills the one the scenario gave, so the respawn runs.
    private static void RigRequest()
    {
        if (_rigRequestAt < 0 || !MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "spawnrequest")) return;
        long now = MpClock.NowMs();
        if (_rigRequestAt == 0)
        {
            _rigRequestAt = now + 100_000;
            GameLog.Instance.Info("spawn: rig request armed");
        }
        if (now < _rigRequestAt) return;
        _rigRequestAt = -1;
        var session = MpPlugin.App?.ClientSession;
        if (session == null || !(Local || AdminAccess.Joined) || session.TryGet<Keen.Game2.Simulation.GameSystems.Respawn.IRespawn>() is not ClientRespawnSessionComponent respawn)
        {
            GameLog.Instance.Info($"spawn: rig request skipped (client session {session != null}, hosting {AdminAccess.Hosting}, host world {HostWorld.Current != null}, joined {AdminAccess.Joined})");
            return;
        }
        if (OwnLivingCharacter(OwnIdentity(respawn, Local)) is { } body)
        {
            GameLog.Instance.Info("spawn: rig player already has a body; asks for its death (the game's death flow)");
            body.Get<CharacterLifeComponent>().RequestDeath();
            return;
        }
        GameLog.Instance.Info("spawn: rig asks for a body");
        respawn.RequestRespawnScreenAsync().SkipWait();
    }
#endif

    // A same-PC alias player's game keeps the account's identity as its local player; the host's player table names the one
    // its characters belong to.
    private static long OwnIdentity(ClientRespawnSessionComponent respawn, bool local)
        => !local && ClientWorld.Current?.BoundSession?.IdentityId is long assigned and not 0 ? assigned : (long)respawn._players.LocalPlayerIdentity;

    private static Entity? OwnLivingCharacter(long identity)
    {
        var session = MpPlugin.App?.ClientSession;
        if (session == null) return null;
        foreach (Entity character in session.GetEntitiesOfType<CharacterLifeComponent>())
            if (character.Get<CharacterLifeComponent>().IsAlive && TeamPermissions.OwnerOf(character) == identity) return character;
        return null;
    }

    /// <summary>Main thread: the menu screen, a log line per menu, and the rig's automatic pick.</summary>
    private static void GameUpdatePostfix()
    {
        try { MenuTick(); }
        catch (Exception ex) { GameLog.Instance.WarnOnce("spawn-menu-tick", "spawn: menu update failed: " + ex); }
    }

    private static void MenuTick()
    {
        var menu = Menu;
        RespawnScreen.Tick();
#if !PULSAR
        RigRequest();
#endif
        if (menu == null || ReferenceEquals(menu, _logged)) return;
        _logged = menu;
        GameLog.Instance.Info($"spawn: menu{(menu.Message.Length > 0 ? " (" + menu.Message + ")" : "")}: " + string.Join("; ", menu.Options.Select((o, i) => $"{i} {o.Name} [{(o.Available ? "" : "unavailable, ")}{o.Detail}]")));
#if !PULSAR
        // Rig (hidden windows cannot be clicked): -y51online:spawnpick:<text> picks the first available entry whose name or detail contains it; '|' lists fallbacks.
        string? pick = MpPlugin.LaunchArgs.FirstOrDefault(a => a.StartsWith(MpOptions.Prefix + "spawnpick:", StringComparison.OrdinalIgnoreCase))?.Substring(MpOptions.Prefix.Length + "spawnpick:".Length);
        if (pick == null) return;
        int index = -1;
        foreach (string text in pick.Split('|'))
            if (index < 0) index = Array.FindIndex(menu.Options, o => o.Available && (o.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || o.Detail.Contains(text, StringComparison.OrdinalIgnoreCase)));
        GameLog.Instance.Info(index < 0 ? $"spawn: rig pick '{pick}' matches no available entry" : $"spawn: rig picks {index} {menu.Options[index].Name}");
        if (index >= 0) Choose(index);
#endif
    }
}
