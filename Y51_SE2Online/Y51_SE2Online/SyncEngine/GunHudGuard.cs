using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.UI.HUD.Weapons;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Equipping a gun or opening a ship's weapon group while the host swaps the weapon entity can hand the client a
/// weapon model whose WeaponDefinition / Projectile have not arrived yet. The weapon HUD view models then throw inside
/// async void scene callbacks (GunToolInputHandlerComponent.OnAddedToScene, the weapon group screen), which takes the
/// whole game down. Both HUDs update again from the models' own PropertyChanged once the data is there.
/// </summary>
internal static class GunHudGuard
{
	private static bool _logged;

	internal static void Install(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(ProjectileWeaponScreenViewModel), "UpdateFromModel"), prefix: new HarmonyMethod(typeof(GunHudGuard), nameof(Prefix)));
		harmony.Patch(AccessTools.Method(typeof(WeaponGroupScreenViewModel), "UpdateData"), prefix: new HarmonyMethod(typeof(GunHudGuard), nameof(GroupPrefix)));
	}

	private static bool Prefix(ProjectileWeaponScreenViewModel __instance) => Ready(__instance.Model?.WeaponDefinition != null);

	private static bool GroupPrefix(WeaponGroupScreenViewModel __instance) => Ready(__instance._models.IsDefault || __instance._models.All(m => m.Projectile != null));

	private static bool Ready(bool ready)
	{
		if (ready) return true;
		if (!_logged)
		{
			_logged = true;
			GameLog.Instance.Warn("gun hud: weapon model data has not arrived yet; the weapon display waits for it instead of crashing");
		}
		return false;
	}
}
