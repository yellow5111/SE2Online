using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.BlockPlacement.BlockPlacer;
using Keen.Game2.Client.GameSystems.UI3D;
using Keen.Game2.Simulation.GameSystems.BlockPlacement;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.DCS.Definitions;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Other players' build previews. The local block placer's draw job is read for the same box SE2 draws around its own
/// placement ghost (DrawPreviewHighlight), the block it aims at (HighlightTargetBlock), and where its hologram grid
/// sits with the blocks in it. Viewers draw the boxes with SE2's UI3D outline in that player's colour and build SE2's
/// own hologram from the blocks (<see cref="BuildGhostModels"/>); the outline stays when the hologram cannot be shown.
/// </summary>
internal static class BuildGhosts
{
	private const long StaleMs = 250;

	private static readonly object Gate = new();
	private static readonly GhostThrottle Throttle = new();
	internal static readonly GhostBoard Board = new();
	private static readonly List<(ushort Builder, BuildGhost Ghost, GhostShape Shape)> Drawn = new();
	private static readonly ConditionalWeakTable<EntityBundle, GhostShape?> Shapes = new();
	private static BuildGhost _captured;
	private static long _capturedMs = long.MinValue / 2;
	private static volatile bool _inSession;
	private static bool _captureFailed, _drawFailed;

	internal static bool InSession => _inSession;

	internal static void Install(Harmony harmony)
	{
		BuildGhostMessage.HostEnabled = () => ((Dedicated.DedicatedRuntime.Requested ? Dedicated.DedicatedRuntime.Config?.Network : null) ?? MpMenu.Settings).BuildGhosts;
		BuildGhostMessage.IsBlockType = guid => Singleton<DefinitionManager>.Instance.TryGetDefinition(guid, out EntityCompositeDefinition composite) && composite.TryGetDefinition<CubeBlockComponent, CubeBlockDefinition>() != null;
		if (Dedicated.DedicatedRuntime.Headless) return;
		harmony.Patch(AccessTools.Method(typeof(BlockPlacerEntityComponent), "DrawBlockPlacement"), postfix: new HarmonyMethod(typeof(BuildGhosts), nameof(CapturePostfix)));
		harmony.Patch(AccessTools.Method(typeof(UI3DSessionComponent), nameof(UI3DSessionComponent.UpdateTransform)), postfix: new HarmonyMethod(typeof(BuildGhosts), nameof(DrawPostfix)));
		BuildGhostMessage.Received += (builder, ghost) => Board.Put(builder, ghost, MpClock.NowMs());
		try { BuildGhostModels.Install(harmony); }
		catch (Exception ex) { GameLog.Instance.Warn("build ghosts: holograms off for this game build, outlines only (" + ex.GetType().Name + ": " + ex.Message + ")"); }
	}

	/// <summary>Main thread, every frame: sends the local preview when it changed (throttled) or went away.</summary>
	internal static void Tick(bool inSession)
	{
		_inSession = inSession;
		if (!inSession)
		{
			Throttle.Reset();
			Board.Clear();
			return;
		}
		long now = MpClock.NowMs();
		BuildGhost ghost;
		lock (Gate) ghost = now - _capturedMs <= StaleMs ? _captured : default;
		if (Throttle.Next(ghost, now) is not { } send) return;
		if (AdminAccess.Hosting) AdminAccess.Host.LocalGhost(send);
		else AdminAccess.Client?.SendBuildGhost(send);
	}

	private static void CapturePostfix(BlockPlacerEntityComponent __instance)
	{
		if (!_inSession) return;
		try
		{
			var prefab = __instance.ActivePrefab;
			if (prefab == null || __instance.Controller == null || __instance.Building != null || __instance.Symmetry?.Setup != null) return;
			var v = __instance.Validation;
			GhostBox? box = null, target = null;
			GhostPose? pose = null;
			GhostShape shape = null;
			// The same condition SE2 uses for its own placement box.
			if ((__instance.NewGridMode || v.SupportedInVoxel || !v.NewGrid || __instance.IsPasteMode) && (v.CanPlace & BuildResultEnum.FailedDistance) == 0)
				box = Box(new OrientedBoundingBoxD((BoundingBoxD)CubeGridCoords.GridToLocal(prefab.AABB), (MatrixD)__instance.Preview.PreviewTransform));
			if (__instance.AlignedBlock.Target is { } aimed && __instance.Alignment.Normal != default)
				target = Box(CubeGridCoords.GridToWorldOriented(aimed.AABB, aimed.Grid.WorldTransform));
			EntityBundle bundle = prefab.Bundle.Bundle;
			if (__instance.Preview.PreviewVisible && __instance.Preview._centerHost is { } center && bundle.Roots == 1 && (shape = ShapeOf(bundle)) != null)
			{
				// Where SE2 put the hologram grid, so viewers need none of the placer's snapping maths.
				WorldTransform grid = bundle.GetWorldTransform(0, center.Entity.Data.GetWorldTransform());
				var q = grid.Orientation;
				pose = new GhostPose(shape.Id, new Vec3D(grid.Position.X, grid.Position.Y, grid.Position.Z), q.X, q.Y, q.Z, q.W);
			}
			byte flags = (byte)((__instance.IsPasteMode ? BuildGhost.Paste : 0) | (v.CanPlace != BuildResultEnum.Success ? BuildGhost.Blocked : 0));
			var ghost = new BuildGhost(flags, box, target, pose, pose != null ? shape : null);
			lock (Gate)
			{
				_captured = ghost;
				_capturedMs = MpClock.NowMs();
			}
		}
		catch (Exception ex)
		{
			if (_captureFailed) return;
			_captureFailed = true;
			GameLog.Instance.Warn("build ghosts: could not read the local block placer, previews not sent: " + ex.Message);
		}
	}

	/// <summary>The blocks of the preview's one grid, once per bundle; null over the size cap or for anything but blocks.</summary>
	private static GhostShape ShapeOf(EntityBundle bundle)
	{
		if (Shapes.TryGetValue(bundle, out var known)) return known;
		var blocks = new List<(Guid, float, float, float, float, float, float, float)>();
		foreach (EntityObjectBuilder child in bundle.RootBuilders[0].OB<HierarchyComponent, HierarchyComponentObjectBuilder>().Children)
		{
			if (blocks.Count > GhostShape.MaxBlocks) break;
			if (!child.Has<CubeBlockComponent>()) continue;
			RelativeTransform t = child.GetChildTransform();
			blocks.Add((child.Definition.Guid, t.Position.X, t.Position.Y, t.Position.Z, t.Orientation.X, t.Orientation.Y, t.Orientation.Z, t.Orientation.W));
		}
		var shape = GhostShape.Create(blocks);
		Shapes.AddOrUpdate(bundle, shape);
		return shape;
	}

	private static void DrawPostfix(UI3DSessionComponent __instance)
	{
		BuildGhostModels.ClientUi3D = __instance;
		if (!_inSession) return;
		lock (Drawn)
		{
			if (Board.Live(MpClock.NowMs(), Drawn) == 0) return;
			try
			{
				foreach (var (builder, ghost, _) in Drawn)
				{
					bool blocked = (ghost.Flags & BuildGhost.Blocked) != 0;
					if (ghost.Box is { } b)
					{
						var obb = Obb(b);
						__instance.DrawPlacementPreview(obb, Tint(builder, blocked ? 0.3f : 0.6f), Thickness(__instance, obb), 0.15f, 0.15f);
					}
					if (ghost.Target is { } t)
					{
						var obb = Obb(t);
						__instance.DrawHighlightOutline(obb, Tint(builder, 0.35f), Thickness(__instance, obb), 0.15f);
					}
				}
			}
			catch (Exception ex)
			{
				if (_drawFailed) return;
				_drawFailed = true;
				GameLog.Instance.Warn("build ghosts: drawing other players' previews failed: " + ex.Message);
			}
		}
	}

	internal static GhostBox Box(OrientedBoundingBoxD o) =>
		new(new Vec3D(o.Center.X, o.Center.Y, o.Center.Z), o.Orientation.X, o.Orientation.Y, o.Orientation.Z, o.Orientation.W, (float)o.HalfExtent.X, (float)o.HalfExtent.Y, (float)o.HalfExtent.Z);

	// SE2's own HighlightInflation (0.01 m) keeps the outline off the block faces.
	private static OrientedBoundingBoxD Obb(GhostBox b) =>
		new(new Vector3D(b.Center.X, b.Center.Y, b.Center.Z), new Vector3D(b.Hx + 0.01, b.Hy + 0.01, b.Hz + 0.01), new Quaternion(b.Qx, b.Qy, b.Qz, b.Qw));

	// BlockPlacer.def: HighlightThickness 2, ThicknessScaleNear/FarDistance 5/30, ThicknessFarScale 0.6.
	private static float Thickness(UI3DSessionComponent ui3d, OrientedBoundingBoxD obb) => ui3d.ScaleThickness(2f, obb.Center, 5f, 30f, 0.6f);

	/// <summary>A steady hue per player, spread around the wheel by the golden ratio.</summary>
	internal static float Hue(ushort builder) => builder * 0.618034f % 1f;

	private static ColorSRGB Tint(ushort builder, float alpha)
	{
		float h = Hue(builder) * 6f, x = 1f - Math.Abs(h % 2f - 1f);
		var (r, g, b) = (int)h switch { 0 => (1f, x, 0f), 1 => (x, 1f, 0f), 2 => (0f, 1f, x), 3 => (0f, x, 1f), 4 => (x, 0f, 1f), _ => (1f, 0f, x) };
		const float s = 0.55f;
		return new ColorSRGB(1f - s + s * r, 1f - s + s * g, 1f - s + s * b, alpha);
	}
}
