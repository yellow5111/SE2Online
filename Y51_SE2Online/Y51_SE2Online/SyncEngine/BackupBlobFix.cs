using System;
using System.IO;
using HarmonyLib;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Extensions;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Core.Worlds;
using Keen.VRage.Library.Filesystem.StorageManagers;
using Keen.VRage.Library.Threading;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// SE2 2.4's BlobExtensions.AddBackupBlob registers blob readers that, unlike AddContainerBlob, never set a task
/// lifetime. Loading an autosave and then streaming a voxel region whose data lives in a blob throws
/// TaskLifetimeException ("No lifetime adapter or explicit lifetime was found") from planet init and the game
/// crashes. This registers the same readers with the backup's container lifetime, as AddContainerBlob does.
/// </summary>
internal static class BackupBlobFix
{
	internal static void Install(Harmony harmony)
	{
		var method = AccessTools.Method(typeof(BlobExtensions), nameof(BlobExtensions.AddBackupBlob)).MakeGenericMethod(typeof(WorldSessionSaveGameMetaData));
		harmony.Patch(method, prefix: new HarmonyMethod(typeof(BackupBlobFix), nameof(Prefix)));
	}

	private static bool Prefix(IStreamedOutBlobs streamedOutBlobs, BlobSetIndexer indexer, Backup<WorldSessionSaveGameMetaData> backup)
	{
		foreach (Blob blob in indexer.Blobs)
		{
			streamedOutBlobs.AddKnownBlob(blob, async () =>
			{
				await Task.SetLifetime(backup.Parent.Lifetime);
				return (await backup.Parent.GetFileHandleAsync(Path.Combine(backup.LocalPath, EntityContainerAccessors.GetBlobsStorage<WorldSessionSaveGameMetaData>(), blob.ToString()))).OpenRead();
			});
		}
		return false;
	}
}
