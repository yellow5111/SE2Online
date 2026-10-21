using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Simulation.RuntimeSystems.GameData;
using Keen.VRage.Core.Project;
using Keen.VRage.Core.Worlds;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using Task = Keen.VRage.Library.Threading.Task;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Workshop mods built for older SE2 versions can hold definitions 2.4 refuses ("Pre-Init validation failed ... Non-abstract
/// definition cannot reference an abstract definition"), and one such definition fails the whole world load. Loading the
/// world's definition set now drops the mods that own refused definitions and loads again without them; they stay out
/// of every later load until the game restarts. The session's mod list (and so a save, and what joiners download) is
/// what actually loaded, as SE2 builds it from the loaded projects.
/// </summary>
internal static class BrokenMods
{
	private const int MaxAttempts = 20;

	private static readonly object Sync = new object();

	private static readonly HashSet<Guid> Broken = new HashSet<Guid>();

	private static readonly List<string> SkippedNames = new List<string>();

	private static readonly List<string> Unannounced = new List<string>();

	// Definitions whose file could not even be read (a mod referencing a definition the game no longer has: "Cannot
	// locate value for definition ..."). That exception names only the missing definition, not the file holding the reference.
	private static readonly List<string> Unreadable = new List<string>();

	[ThreadStatic] private static bool _original;

	public static IReadOnlyList<string> Skipped { get { lock (Sync) return SkippedNames.ToList(); } }

	internal static void Install(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(GameDataLoaderComponent), nameof(GameDataLoaderComponent.LoadWorldDataIfNeeded)), prefix: new HarmonyMethod(typeof(BrokenMods), nameof(LoadPrefix)));
		harmony.Patch(AccessTools.Method(typeof(GameDataLoaderComponent), nameof(GameDataLoaderComponent.SetRequiredProjects), new[] { typeof(ModList) }), postfix: new HarmonyMethod(typeof(BrokenMods), nameof(RequiredPostfix)));
		var read = new HarmonyMethod(typeof(BrokenMods), nameof(ReadFinalizer));
		harmony.Patch(AccessTools.Method(Loader, "LoadObjectBuilderThrowing"), finalizer: read);
		harmony.Patch(AccessTools.Method(Loader, "LoadPartialObjectBuilderThrowing"), finalizer: read);
	}

	private static readonly Type Loader = AccessTools.TypeByName("Keen.VRage.Library.Definitions.DefinitionLoader");

	private static readonly System.Reflection.FieldInfo Locator = AccessTools.Field(Loader, "_locator");

	private static Exception? ReadFinalizer(object __instance, Exception? __exception, object[] __args)
	{
		if (__exception != null && __args.OfType<Guid>().FirstOrDefault() is var id && id != Guid.Empty)
		{
			try
			{
				string name = (string)Locator.FieldType.GetMethod("GetDebugName")!.Invoke(Locator.GetValue(__instance), new object[] { id })!;
				lock (Sync) Unreadable.Add(name);
			}
			catch (Exception) { }
		}
		return __exception;
	}

	private static bool LoadPrefix(GameDataLoaderComponent __instance, ref Task __result)
	{
		if (_original) return true;
		__result = LoadSkippingBroken(__instance);
		return false;
	}

	private static void RequiredPostfix(GameDataLoaderComponent __instance)
	{
		lock (Sync)
		{
			if (Broken.Count > 0) __instance._requiredProjects = __instance._requiredProjects.Where(p => !Broken.Contains(p.Guid)).ToArray();
		}
	}

	private static async Task LoadSkippingBroken(GameDataLoaderComponent loader)
	{
		await Task.SetLifetime(loader);
		for (int attempt = 1; ; attempt++)
		{
			Task load;
			lock (Sync) Unreadable.Clear();
			_original = true;
			try { load = loader.LoadWorldDataIfNeeded(); }
			finally { _original = false; }
			try
			{
				await load;
				return;
			}
			catch (Exception ex) when (attempt < MaxAttempts && Drop(loader, ex))
			{
			}
		}
	}

	/// <summary>Takes the mods owning refused definitions out of the required projects; false when none of the failure is theirs.</summary>
	private static bool Drop(GameDataLoaderComponent loader, Exception ex)
	{
		try
		{
			var failed = BrokenModMatch.FailedDefinitions(ex.ToString());
			lock (Sync) failed = failed.Concat(Unreadable).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			if (failed.Count == 0) return false;
			var mods = loader._projects.LoadedProjects.Where(p => p.Data.Type == ProjectType.Mod).ToList();
			var owners = BrokenModMatch.Owners(failed, mods.Select(p => p.Data.Name).ToList()).Select(i => mods[i]).Where(p => loader._requiredProjects.Any(r => r.Guid == p.Data.ProjectID.Guid)).ToList();
			if (owners.Count == 0) return false;
			var names = owners.Select(p => WorkshopMods.From(new ModList.ModRecord { Project = p.Data.ProjectID, Version = p.Data.Version, DisplayName = p.Data.Name }).Describe()).ToList();
			lock (Sync)
			{
				foreach (var p in owners) Broken.Add(p.Data.ProjectID.Guid);
				foreach (string name in names.Where(n => !SkippedNames.Contains(n))) { SkippedNames.Add(name); Unannounced.Add(name); }
				loader._requiredProjects = loader._requiredProjects.Where(p => !Broken.Contains(p.Guid)).ToArray();
			}
			GameLog.Instance.Info($"mods: skipping {string.Join(", ", names)}: SE2 refused their definitions ({string.Join("; ", failed.Take(4))}); loading the world again without them");
			return true;
		}
		catch (Exception inner)
		{
			GameLog.Instance.Info("mods: could not work out which mod failed to load: " + inner.Message);
			return false;
		}
	}

	/// <summary>Once the player is in the world, says which mods were left out.</summary>
	internal static void Tick(Keen.Game2.GameAppComponent app, Keen.Game2.Client.UI.Library.SharedUIComponent? shared)
	{
		if (shared == null || app.Client == null || app._isTransitioning.IsSet) return;
		List<string> names;
		lock (Sync)
		{
			if (Unannounced.Count == 0) return;
			names = Unannounced.ToList();
			Unannounced.Clear();
		}
		string text = BrokenModMatch.Notice(names);
		ChatLog.Add(new ChatLine("Mods", 0, SocialState.Clean(text, ChatLog.MaxText), ChatKind.System, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), MpClock.NowMs());
		AdminAccess.Notice(shared, "Mods skipped", text);
	}
}
