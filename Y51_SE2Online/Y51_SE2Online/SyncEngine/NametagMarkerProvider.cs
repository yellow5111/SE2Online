using Keen.Game2.Client.GameSystems.GPS;
using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Collections;

namespace Y51_SE2Online.SyncEngine;

internal sealed class NametagMarkerProvider : Component, IGPSMarkersProvider
{
	public volatile ObservableList<GPSMarker> Current = new ObservableList<GPSMarker>();

	ObservableList<GPSMarker> IGPSMarkersProvider.Markers => NametagRelevance.View(Current);
}
