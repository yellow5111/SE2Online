using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.Contracts;
using Keen.Game2.Client.GameSystems.FastTravel;
using Keen.Game2.Client.GameSystems.GPS;
using Keen.Game2.Client.UI.HUD.Notification;
using Keen.Game2.Client.UI.InGame;
using Keen.Game2.Client.UI.Library;
using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.VRage.Library.Collections;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.UI.Screens;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using Vector3D = Keen.VRage.Library.Mathematics.Vector3D;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Design 10.2: the marker filter. Our own markers follow the mode and categories in <see cref="PingMarkers"/>; vanilla
/// markers are only filtered by category (and the server's per-category range), never restyled, through a transpiler on
/// <c>GPSMarkerRenderSessionComponent.DrawHUDMarkers</c> that passes each <c>DrawBatch(span)</c> through <see cref="Pass"/>.
/// If the method's shape is not the one this build knows, the transpiler leaves it alone: every vanilla marker shows.
/// </summary>
internal static class MarkerFilter
{
	// DrawBatch call sites in DrawHUDMarkers, in order: antennas, IGPSMarkersProvider components, ore detectors, own GPS, GPS groups.
	private const int Sites = 5, ProviderSite = 1;
	private static readonly MarkerCategory?[] SiteCategory = { MarkerCategory.Signals, null, MarkerCategory.Ore, MarkerCategory.Gps, MarkerCategory.Gps };
	private static readonly int Categories = Enum.GetValues<MarkerCategory>().Length;

	/// <summary>Null outside a multiplayer world: every marker passes.</summary>
	private static volatile MarkerChoice _active;
	private static double[] _ranges = new double[Categories];
	private static volatile object _anchor;
	[ThreadStatic] private static MarkerCategory? _providerCategory;
	[ThreadStatic] private static GPSMarker[][] _buffers;

	public static MarkerChoice Choice { get; private set; } = MarkerChoice.Default;
	public static MarkerDefaults Defaults { get; private set; } = new();
	public static bool Transpiled { get; private set; }

	private static readonly HoldGesture Gesture = new();
	private static IObservableDisposable _panel;
	private static long _nextRefreshMs;

	internal static void Install(Harmony harmony)
	{
		harmony.Patch(EngineMembers.Method(HookTargets.GpsDrawFilter), transpiler: new HarmonyMethod(typeof(MarkerFilter), nameof(Transpiler)));
		GameLog.Instance.Info(Transpiled ? "markers: vanilla marker categories filtered in DrawHUDMarkers" : "markers: DrawHUDMarkers left as is; vanilla markers always show");
	}

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		var list = instructions.ToList();
		try
		{
			var sites = Enumerable.Range(0, list.Count).Where(i => list[i].operand is MethodInfo m && (list[i].opcode == OpCodes.Call || list[i].opcode == OpCodes.Callvirt)
				&& m.Name.StartsWith("<DrawHUDMarkers>g__DrawBatch", StringComparison.Ordinal)).ToList();
			bool spans = sites.All(i => ((MethodInfo)list[i].operand).GetParameters() is { Length: > 0 } p && p[0].ParameterType == typeof(ReadOnlySpan<GPSMarker>));
			var getters = Enumerable.Range(0, list.Count).Where(i => list[i].opcode == OpCodes.Callvirt && list[i].operand is MethodInfo m
				&& m.Name == "get_Markers" && m.DeclaringType == typeof(IGPSMarkersProvider)).ToList();
			bool shape = sites.Count == Sites && spans && getters.Count == 1 && sites.All(i => i > 0 && (list[i - 1].opcode == OpCodes.Ldloca_S || list[i - 1].opcode == OpCodes.Ldloca))
				&& getters[0] > sites[0] && getters[0] < sites[ProviderSite];
			if (!shape)
			{
				GameLog.Instance.Warn($"markers: DrawHUDMarkers has {sites.Count} DrawBatch call(s) and {getters.Count} provider read(s), not the {Sites} and 1 this build knows; the marker filter leaves vanilla markers alone");
				return list;
			}
			var provider = list[getters[0]];
			list[getters[0]] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MarkerFilter), nameof(ProviderMarkers))) { labels = provider.labels, blocks = provider.blocks };
			var pass = AccessTools.Method(typeof(MarkerFilter), nameof(Pass));
			for (int k = Sites - 1; k >= 0; k--)
			{
				// The span is on the stack; the closure reference (ldloca) and the call follow it.
				var closure = list[sites[k] - 1];
				var site = new CodeInstruction(OpCodes.Ldc_I4, k) { labels = closure.labels.ToList() };
				closure.labels.Clear();
				list.InsertRange(sites[k] - 1, new[] { site, new CodeInstruction(OpCodes.Call, pass) });
			}
			Transpiled = true;
			return list;
		}
		catch (Exception ex)
		{
			GameLog.Instance.Warn("markers: the marker filter transpiler failed, vanilla markers always show: " + ex.Message);
			return instructions;
		}
	}

	/// <summary>Render thread: the provider's markers, noting which category its batch is.</summary>
	public static ObservableList<GPSMarker> ProviderMarkers(IGPSMarkersProvider provider)
	{
		_providerCategory = provider switch
		{
			PingMarkerProvider => MarkerCategory.Pings,
			NametagMarkerProvider => MarkerCategory.Players,
			ContractModelClientSessionComponent or ContractNotificationsSessionComponent or FastTravelClientSessionComponent => MarkerCategory.Quest,
			// Blocks the player set to show on the HUD: their own choice, like a GPS marker.
			BlockMarkersClientSessionComponent => MarkerCategory.Gps,
			_ => null
		};
		return provider.Markers;
	}

	/// <summary>Render thread: one batch of markers, whole, empty (category off) or without those beyond the category's range.</summary>
	public static ReadOnlySpan<GPSMarker> Pass(ReadOnlySpan<GPSMarker> markers, int site)
	{
		var choice = _active;
		if (choice == null || markers.IsEmpty || site < 0 || site >= Sites) return markers;
		var category = site == ProviderSite ? _providerCategory : SiteCategory[site];
		if (category is not { } c) return markers;
		if (!choice.On(c)) return ReadOnlySpan<GPSMarker>.Empty;
		double range = _ranges[(int)c];
		if (range <= 0 || _anchor is not Vector3D anchor) return markers;
		_buffers ??= new GPSMarker[Categories][];
		var buffer = _buffers[(int)c];
		if (buffer == null || buffer.Length < markers.Length) _buffers[(int)c] = buffer = new GPSMarker[Math.Max(16, markers.Length * 2)];
		int n = 0;
		foreach (var m in markers)
			if (m != null && Vector3D.Distance(anchor, m.Position) <= range) buffer[n++] = m;
		return new ReadOnlySpan<GPSMarker>(buffer, 0, n);
	}

	internal static void SetAnchor(Vector3D? anchor) => _anchor = anchor.HasValue ? anchor.Value : null;

	/// <summary>Main thread: the player's saved choice over the server's defaults.</summary>
	internal static void Refresh(long now, bool force = false)
	{
		if (!force && now < _nextRefreshMs) return;
		_nextRefreshMs = now + 2000;
		Defaults = (AdminAccess.Hosting ? MpMenu.Settings.MarkerDefaults : AdminAccess.Client?.Admin.MarkerDefaults) ?? new MarkerDefaults();
		var settings = MpMenu.Settings;
		Choice = MarkerChoice.Resolve(settings.MarkerMode, settings.MarkersOff, Defaults);
		var ranges = new double[Categories];
		foreach (var c in Enum.GetValues<MarkerCategory>()) ranges[(int)c] = MarkerChoice.MaxRange(Defaults, c);
		_ranges = ranges;
		_active = Choice;
	}

	internal static void Deactivate()
	{
		_active = null;
		_anchor = null;
		Gesture.Reset();
		ClosePanel();
	}

	/// <summary>Main thread, every frame in a multiplayer world.</summary>
	internal static void Keys(SharedUIComponent shared, long now, bool usable)
	{
		bool down = usable && KeyBinding.TryParse(MpMenu.Settings.MarkerFilterKey, out var key) && key.IsDown(GameKeys.IsDown) && GameKeys.OwnWindowInFront();
		switch (Gesture.Update(down, now))
		{
		case HoldGesture.Edge.Tap:
			if (_panel != null) { ClosePanel(); break; }
			var next = MarkerChoice.Next(Choice.Mode);
			Save(PingRules.ModeText(next).Replace(" ", "").ToLowerInvariant(), Choice.ServerDefaults ? null : Choice.Off.Select(MarkerChoice.Word).ToList());
			Toast(PingLogic.ModeToast(next));
			break;
		case HoldGesture.Edge.HoldStart:
			if (_panel == null) OpenPanel(shared);
			break;
		}
	}

	private static readonly string SettingsPath = PluginPaths.Data("Y51_SE2Online.settings.json");

	/// <summary>Game thread. Written straight to the file like RememberSkippedUpdate: MpMenu.SaveSettings would re-apply every
	/// host setting and send the admin state to every player on each Alt+M tap.</summary>
	internal static void Save(string mode, List<string> off)
	{
		var settings = MpMenu.Settings;
		settings.MarkerMode = mode ?? "";
		settings.MarkersOff = off;
		try
		{
			string temporary = SettingsPath + ".tmp";
			System.IO.File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
			System.IO.File.Move(temporary, SettingsPath, true);
		}
		catch (Exception ex) { GameLog.Instance.Warn("markers: saving the marker filter failed (it still applies until the game closes): " + ex.Message); }
		Refresh(MpClock.NowMs(), force: true);
	}

	internal static void SetMode(MarkerMode mode) => Save(PingRules.ModeText(mode).Replace(" ", "").ToLowerInvariant(), Choice.ServerDefaults ? null : Choice.Off.Select(MarkerChoice.Word).ToList());

	internal static void SetCategory(MarkerCategory category, bool on)
	{
		var off = new HashSet<MarkerCategory>(Choice.Off);
		if (on) off.Remove(category); else off.Add(category);
		Save(Choice.ServerDefaults ? "" : PingRules.ModeText(Choice.Mode).Replace(" ", "").ToLowerInvariant(), off.Select(MarkerChoice.Word).ToList());
	}

	internal static void UseServerDefaults() => Save("", null);

	internal static void Toast(string text)
	{
		try
		{
			MpPlugin.App?.ClientSession?.SessionComponents.TryGet<SessionInGameUISessionComponent>()?.ShowNotification(HudNotification.CreateTextNotification(default, OnlineUi.Raw(text)));
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("marker-toast", "markers: the HUD notice failed: " + ex.Message); }
	}

	private static void OpenPanel(SharedUIComponent shared)
	{
		if (shared == null) return;
		_panel = shared.CreateScreen<MarkerFilterScreen>(new MarkerFilterViewModel(), true, ScreenLayer.Top);
		_panel.OnDisposed += _ => _panel = null;
	}

	internal static void ClosePanel() { _panel?.Dispose(); _panel = null; }

	internal static bool PanelOpen => _panel != null;
}

public sealed class MarkerFilterViewModel : ScreenViewModel
{
	public event Action Refresh;
	private long _next;

	public MarkerFilterViewModel()
	{
		KeepsOtherScreensVisible = true; AllowsInputFromLowerScreens = false; InitializeInputContext();
		OnTick += () => { long now = MpClock.NowMs(); if (now < _next) return; _next = now + 250; Refresh?.Invoke(); };
	}

	public void Close() => SignalWantsClose();
}

/// <summary>The compact right-edge marker panel (hold the marker filter key): mode, categories, server defaults, recent pings.</summary>
public sealed class MarkerFilterScreen : ScreenView
{
	private static readonly MarkerMode[] Modes = { MarkerMode.All, MarkerMode.Relevant, MarkerMode.PingsOnly, MarkerMode.Off };
	private static readonly (MarkerCategory Category, string Label)[] Rows =
	{
		(MarkerCategory.Pings, "Pings"), (MarkerCategory.Gps, "GPS (own)"), (MarkerCategory.Players, "Players (name tags)"),
		(MarkerCategory.Signals, "Signals (antennas)"), (MarkerCategory.Quest, "Quest (contracts, fast travel)"), (MarkerCategory.Ore, "Ore"),
	};

	private readonly Button[] _modes;
	private readonly CheckBox[] _categories;
	private readonly TextBlock _source = OnlineUi.Text("", "SubtleNotice", 18);
	private readonly StackPanel _recent = new() { Spacing = 4 };
	private MarkerFilterViewModel _model;
	private bool _syncing, _closed;
	private string _recentSignature = "";

	public MarkerFilterScreen()
	{
		_modes = Modes.Select(m => OnlineUi.SmallButton(PingRules.ModeText(m), () => MpMenu.Queue(() => MarkerFilter.SetMode(m)), "MarkerMode" + m)).ToArray();
		_categories = Rows.Select(r =>
		{
			var check = OnlineUi.Check(r.Label);
			check.FontSize = 20; check.MinHeight = 36;
			check.IsCheckedChanged += (_, _) => { if (!_syncing) { bool on = check.IsChecked == true; MpMenu.Queue(() => MarkerFilter.SetCategory(r.Category, on)); } };
			return check;
		}).ToArray();
		var modeRow = new UniformGrid { Columns = 2 };
		foreach (var b in _modes) { b.Margin = new Thickness(2); b.HorizontalAlignment = HorizontalAlignment.Stretch; modeRow.Children.Add(b); }
		var defaults = OnlineUi.SmallButton("Use server defaults", () => MpMenu.Queue(MarkerFilter.UseServerDefaults), "MarkerServerDefaults");
		var close = OnlineUi.SmallButton("Close", () => _model?.Close(), "MarkerClose");
		var lines = new StackPanel { Spacing = 8, Margin = new Thickness(18, 14) };
		lines.Children.Add(OnlineUi.Text("MARKERS", "SubTitle", 24));
		lines.Children.Add(modeRow);
		foreach (var c in _categories) lines.Children.Add(c);
		lines.Children.Add(_source);
		lines.Children.Add(defaults);
		lines.Children.Add(OnlineUi.Text("Recent pings", "SubTitle", 20));
		lines.Children.Add(_recent);
		lines.Children.Add(OnlineUi.Text("Tap " + MpMenu.Settings.MarkerFilterKey + " to cycle the mode. /pings and /keep work in chat.", "SubtleNotice", 16));
		lines.Children.Add(close);
		var panel = new Grid { Width = 430, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
		panel.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0xD8, 8, 12, 16)) });
		panel.Children.Add(new Border { Width = 4, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Color.FromRgb(240, 160, 30)) });
		panel.Children.Add(lines);
		var frame = new Grid { Width = 1920, Height = 1080 };
		frame.Children.Add(panel);
		Content = new Viewbox { Stretch = Stretch.Uniform, Child = frame };
		OnDisposing += _ => { _closed = true; if (_model != null) _model.Refresh -= QueueRefresh; };
		Sync();
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (_model != null) _model.Refresh -= QueueRefresh;
		_model = DataContext as MarkerFilterViewModel;
		if (_model != null) _model.Refresh += QueueRefresh;
	}

	private void QueueRefresh() => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!_closed) Sync(); });

	private void Sync()
	{
		var choice = MarkerFilter.Choice;
		_syncing = true;
		try
		{
			for (int i = 0; i < Modes.Length; i++)
			{
				bool selected = Modes[i] == choice.Mode;
				_modes[i].FontWeight = selected ? FontWeight.Bold : FontWeight.Normal;
				_modes[i].BorderThickness = new Thickness(selected ? 3 : 1);
			}
			for (int i = 0; i < Rows.Length; i++) _categories[i].IsChecked = choice.On(Rows[i].Category);
		}
		finally { _syncing = false; }
		_source.Text = choice.ServerDefaults ? "Using the server's defaults." : "Your own choice (saved).";
		var rows = PingMarkers.Hud.Feed;
		string signature = string.Join("|", rows.Select(r => r.Id + ":" + r.Text));
		if (signature == _recentSignature) return;
		_recentSignature = signature;
		_recent.Children.Clear();
		if (rows.Length == 0) _recent.Children.Add(OnlineUi.Text("None in the last 10 seconds.", "SubtleNotice", 17));
		foreach (var row in rows)
		{
			uint id = row.Id;
			var text = OnlineUi.Text(row.Text, size: 17);
			text.TextTrimming = TextTrimming.None; text.HorizontalAlignment = HorizontalAlignment.Left;
			var keep = OnlineUi.SmallButton("Keep", () => MpMenu.Queue(() => PingClient.Keep(id)), "MarkerKeep" + id);
			var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
			line.Children.Add(text);
			Grid.SetColumn(keep, 1); line.Children.Add(keep);
			_recent.Children.Add(line);
		}
	}
}
