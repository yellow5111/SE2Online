using System;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2;
using Keen.Game2.Client.GameComponents;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Game.GameComponents;
using Keen.Game2.Game.SessionComponents;
using Keen.Game2.OutOfProcess;
using Keen.Game2.Simulation.RuntimeSystems.CoreScenes;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Threading;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal static class RemoteClientGame
{
	public static async Task LoadAsync(Keen.Game2.GameAppComponent app, bool hostHasMissions, IReadOnlyList<WorldMod> mods, System.Threading.Tasks.Task<string> modsReady, IMpLog log, Action<ClientPhase, string> report, Action<bool> started, Action<Exception> failed)
	{
		await Task.SetLifetime(app.Scene);
		if (!app._isTransitioning.Set())
		{
			log.Warn("client: the game is mid-transition; the client game start is retried in a moment");
			started(obj: false);
			return;
		}
		started(obj: true);
		Exception failure = null;
		try
		{
			await BuildAndRunAsync(app, hostHasMissions, mods, modsReady, log);
		}
		catch (Exception ex)
		{
			failure = ex;
		}
		finally
		{
			app._isTransitioning.Clear();
		}
		if (failure == null)
		{
			return;
		}
		log.Error($"CLIENT GAME FAILED TO START; returning to the main menu (the game keeps running): {failure}");
		report(ClientPhase.Error, "client game failed to start: " + Describe(failure));
		try
		{
			failed(failure);
		}
		catch (Exception)
		{
		}
		try
		{
			app.NotifySessionLoadFinished(failure);
		}
		catch (Exception ex3)
		{
			log.Warn("client: NotifySessionLoadFinished threw " + ex3.GetType().Name + ": " + ex3.Message);
		}
		try
		{
			FailureResolutionComponent failureResolutionComponent = app.Scene.Entities.Select((Entity x) => x.TryGet<FailureResolutionComponent>()).FirstOrDefault((FailureResolutionComponent x) => x != null);
			if (failureResolutionComponent != null)
			{
				failureResolutionComponent.ResolveFailedLoading();
			}
			else
			{
				app.Disconnect();
			}
		}
		catch (Exception value)
		{
			log.Error($"client: returning to the main menu failed too: {value}");
		}
	}

	internal static string Describe(Exception e)
	{
		Exception ex = e;
		while (ex.InnerException != null)
		{
			ex = ex.InnerException;
		}
		if (e is AggregateException ex2 && ex2.InnerExceptions.Count > 1)
		{
			return string.Join(" | ", ex2.InnerExceptions.Select((Exception x) => x.GetType().Name + ": " + x.Message));
		}
		if (ex != e)
		{
			return $"{e.GetType().Name}: {e.Message} <- {ex.GetType().Name}: {ex.Message}";
		}
		return e.GetType().Name + ": " + e.Message;
	}

	private static async Task BuildAndRunAsync(Keen.Game2.GameAppComponent app, bool hostHasMissions, IReadOnlyList<WorldMod> mods, System.Threading.Tasks.Task<string> modsReady, IMpLog log)
	{
		await Task.SetLifetime(app.Scene);
		string modsError = modsReady == null ? null : await modsReady;
		if (modsError == WorkshopMods.Cancelled) { log.Info("client: the join was called off during the Workshop download; not loading the host's world"); return; }
		if (modsError != null) throw new InvalidOperationException(modsError);
		// Without this the client keeps whatever mods the last single-player world loaded.
		app.GameData.SetRequiredProjects(WorkshopMods.ToModList(mods));
		if (mods?.Count > 0) log.Info("client: loading the host's mods: " + WorldMods.Summary(mods));
		GameCoreScene coreScene = app.Scene;
		EntityBuilder builder = coreScene.Dynamic();
		builder.WithComponent<NetworkGameClientComponent>(default(StringId));
		builder.WithComponent<NetworkGameClientCoreComponent>(default(StringId));
		builder.WithComponent<Keen.Game2.ClientWorldTickComponent>();
		GameWorldBuilder.CreateBaseGame(ref builder, isServer: false, app._instanceIndex, hostHasMissions);
		GameClientWorldBuilder.BuildGameClient(ref builder);
		log.Info("client: building the client game with single player's client recipe (BuildGameClient non-dedicated, Missions feature " + (hostHasMissions ? "on, like the host" : "off") + ")");
		await app.TransitionToGame((EntityBuilder?)null, (EntityBuilder?)builder, string.Empty);
		(coreScene.GameClient ?? throw new InvalidOperationException("TransitionToGame finished without a client scene")).Get<WorldSessionComponent>().OwnedSession.SessionComponents.Get<ClientPlayersSessionComponent>().SetupInitialPlayer();
		log.Info("client: client game is up (TransitionToGame done, initial player set up)");
	}
}
