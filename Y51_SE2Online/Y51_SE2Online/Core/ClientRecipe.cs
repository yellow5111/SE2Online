using System;

namespace Y51_SE2Online.Core;

public static class ClientRecipe
{
	public static readonly string[] SinglePlayerOnlyCoreComponents = new string[9] { "Keen.Game2.InProcess.SessionGameComponent", "Keen.Game2.InProcess.InProcessGameClientCoreComponent", "Keen.Game2.InProcessServerWorldTickComponent", "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationComponent", "Keen.Game2.AutoPauseWorldComponent", "Keen.Game2.Client.RuntimeSystems.InProcess.AssetJournalComponent", "Keen.Game2.Game.SessionComponents.ReportLostEntitiesInSaveComponent", "Keen.Game2.Simulation.GameSystems.Missions.MissionCoreComponent", "Keen.Game2.Simulation.GameSystems.PCU.PCUServerCoreComponent" };

	public static readonly string[] KeenNetworkClientAlsoLoses = new string[14]
	{
		"Keen.Game2.Client.GameSystems.PCU.PCUClientSessionComponent", "Keen.Game2.Client.GameSystems.PCU.PCUNotificationSessionComponent", "Keen.Game2.Client.GameSystems.OreDetection.OreDetectionEffectsSessionComponent", "Keen.Game2.Client.GameSystems.GPS.GPSMarkerRenderSessionComponent", "Keen.Game2.Client.GameSystems.Missions.ClientMissionCoreComponent", "Keen.Game2.Client.Debugging.DebugMenuClientCoreComponent", "Keen.Game2.Client.Debugging.DebugMenuSessionComponent", "Keen.Game2.Client.GameSystems.Missions.MissionObjectiveNotificationSessionComponent", "Keen.Game2.Client.GameSystems.Missions.MissionEndClientSessionComponent", "Keen.Game2.Client.GameSystems.Missions.MissionUISessionComponent",
		"Keen.Game2.Simulation.StreamedUI.Contracts.StreamedContractsScreenSessionComponent", "Keen.Game2.Client.GameSystems.Contracts.ContractNotificationsSessionComponent", "Keen.Game2.Client.GameSystems.Missions.MissionLogDisplaySessionComponent", "Keen.Game2.Client.RuntimeSystems.Analytics.SessionComponents.MissionAnalyticsSessionComponent"
	};

	public static readonly string[] ServerOnlyOwners = new string[2] { "Keen.Game2.InProcessServerWorldTickComponent", "Keen.Game2.Simulation.GameSystems.TrashRemoval.GridPCUTrashRemovalSessionComponent" };
}
