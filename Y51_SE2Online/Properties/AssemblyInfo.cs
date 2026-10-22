using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Permissions;
using Keen.VRage.DCS.Internal;
using Keen.VRage.Library.Reflection;
using Keen.VRage.Library.Threading;
using Y51_SE2Online.SyncEngine;

[assembly: IgnoresAccessChecksTo("Game2.Client")]
[assembly: IgnoresAccessChecksTo("Game2.Game")]
[assembly: IgnoresAccessChecksTo("VRage.Steam")]
[assembly: IgnoresAccessChecksTo("Game2.Simulation")]
[assembly: IgnoresAccessChecksTo("SpaceEngineers2")]
[assembly: IgnoresAccessChecksTo("VRage.Multiplayer")]
[assembly: IgnoresAccessChecksTo("VRage.Water")]
[assembly: IgnoresAccessChecksTo("VRage.Physics")]
[assembly: AssemblyCompany("Y51_SE2Online")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyFileVersion("0.16.4.0")]
[assembly: AssemblyInformationalVersion("0.16.4")]
[assembly: AssemblyProduct("Y51_SE2Online")]
[assembly: AssemblyTitle("Y51_SE2Online")]
[assembly: IndexMetadata]
[assembly: MetadataDependencies(new string[]
{
	"Game2.ContentBuilder", "Game2.Game", "System.Collections.Immutable, Version=9.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", "VRage.AI", "VRage.Analytics", "VRage.Animation.Client", "VRage.Audio", "VRage.Client.ContentBuilder", "VRage.Client",
	"VRage.ContentPipeline.Builder.Plugin", "VRage.Core", "VRage.Core.Game", "VRage.DCS", "VRage.Game.Client", "VRage.Game", "VRage.Input", "VRage.Library", "VRage.Physics.Client", "VRage.Physics",
	"VRage.Platform.Windows", "VRage.Render", "VRage.Render12", "VRage.Scripting", "VRage.Steam", "VRage.UI", "VRage.UI.Shared", "VRage.Voxels.Client", "VRage.Voxels", "VRage.Water.Client",
	"VRage.Water", "Game2.Client", "Game2.Simulation", "SpaceEngineers2", "VRage.Multiplayer"
})]
[assembly: Bundle("Y51_SE2Online", "0.16.4.0")]
[assembly: AssemblyVersion("0.16.4.0")]
[module: ModuleIndexedAttributes(typeof(WithLifetimeAdapterAttribute), new Type[] { typeof(NametagMarkerProvider), typeof(Y51_SE2Online.Engine.Y51ServerTransport), typeof(Y51_SE2Online.Engine.Y51ClientTransport) }, new string[] { })]
[module: ModuleIndexedAttributes(typeof(IndexedComponentAttribute), new Type[] { typeof(NametagMarkerProvider), typeof(Y51_SE2Online.Dedicated.HeadlessEngine.HeadlessParallelInitComponent), typeof(Y51_SE2Online.Dedicated.HeadlessEngine.ServerParallelInitComponent), typeof(WorldSocial.WorldSocialSaveComponent) }, new string[] { })]

