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
[assembly: IgnoresAccessChecksTo("Game2.Simulation")]
[assembly: IgnoresAccessChecksTo("SpaceEngineers2")]
[assembly: IgnoresAccessChecksTo("VRage.Multiplayer")]
[assembly: IgnoresAccessChecksTo("VRage.Water")]
[assembly: IgnoresAccessChecksTo("VRage.Physics")]
[assembly: AssemblyCompany("Y51_SE2Online")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyFileVersion("0.15.5.0")]
[assembly: AssemblyInformationalVersion("0.15.5")]
[assembly: AssemblyProduct("Y51_SE2Online")]
[assembly: AssemblyTitle("Y51_SE2Online")]
[assembly: IndexMetadata]
[assembly: MetadataDependencies(new string[]
{
	"Game2.ContentBuilder, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "Game2.Game, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "System.Collections.Immutable, Version=9.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", "VRage.AI, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Analytics, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Animation.Client, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Animation, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Audio, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Client.ContentBuilder, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Client, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null",
	"VRage.ContentPipeline.Builder.Plugin, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Core, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Core.Game, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.DCS, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Game.Client, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Game, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Input, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Library, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Physics.Client, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Physics, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null",
	"VRage.Platform.Windows, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Render, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Render12, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Scripting, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Steam, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.UI, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.UI.Shared, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Voxels.Client, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Voxels, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Water.Client, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null",
	"VRage.Water, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "Game2.Client, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "Game2.Simulation, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "SpaceEngineers2, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null", "VRage.Multiplayer, Version=2.4.0.95, Culture=neutral, PublicKeyToken=null"
})]
[assembly: Bundle("Y51_SE2Online", "0.15.5.0")]
[assembly: AssemblyVersion("0.15.5.0")]
[module: ModuleIndexedAttributes(typeof(WithLifetimeAdapterAttribute), new Type[] { typeof(NametagMarkerProvider) }, new string[] { })]
[module: ModuleIndexedAttributes(typeof(IndexedComponentAttribute), new Type[] { typeof(NametagMarkerProvider), typeof(Y51_SE2Online.Dedicated.HeadlessEngine.HeadlessParallelInitComponent), typeof(Y51_SE2Online.Dedicated.HeadlessEngine.ServerParallelInitComponent), typeof(WorldSocial.WorldSocialSaveComponent) }, new string[] { })]

