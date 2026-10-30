using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Keen.Game2.Game.SessionComponents;
using Y51_SE2Online.Core;
using Y51_SE2Online.KeenNetcode;
using Y51_SE2Online.NetHost;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// The Rules page: custom rules for the world you run (or, from the main menu, for the next new world), the co-op twin of
/// the dedicated runner's CUSTOM RULES tab. Every session setting and definition field of the running game, searchable, with
/// presets and shareable rules files (which also carry SE2 Online's own gameplay options). Saved per world; Apply now
/// changes the running world's definitions at once and sends them to the players in it.
/// </summary>
public sealed partial class OnlineScreen
{
    private const int RulesShown = 80, RulesPageIndex = 5;
    private static readonly IBrush RuleOk = new SolidColorBrush(Color.FromRgb(0x72, 0xD3, 0x8A));
    private static readonly IBrush RuleWarn = new SolidColorBrush(Color.FromRgb(0xE5, 0xA2, 0x3A));
    private static readonly IBrush RuleBad = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4D));
    private static readonly IBrush RuleRowBack = new SolidColorBrush(Color.FromArgb(0xB8, 0x06, 0x0C, 0x12));

    private sealed class RuleRow
    {
        public bool Session, Single;
        public string Key, Field, Type, Title, Subtitle, Game, Category, Search;
        public JsonElement? GameValue;
    }

    private readonly TextBox _ruleSearch = OnlineUi.Input("Search: inventory, jetpack, weld, thrust, a block name...", 100);
    private readonly ComboBox _ruleCategory = new() { MinHeight = 48, FontSize = 22, MinWidth = 300 };
    private readonly CheckBox _ruleChanged = OnlineUi.Check("Changed only");
    private readonly CheckBox _ruleSingle = OnlineUi.Check("Single definitions");
    private readonly ComboBox _rulePreset = new() { MinHeight = 48, FontSize = 22, MinWidth = 360 };
    private readonly ComboBox _ruleFile = new() { MinHeight = 48, FontSize = 22, MinWidth = 420 };
    private readonly TextBlock _ruleWorld = OnlineUi.Text("", "SubTitle", 20);
    private readonly TextBlock _ruleStatus = OnlineUi.Text("", "SubtleNotice", 19);
    private readonly TextBlock _ruleBanner = OnlineUi.Warning("");
    private readonly TextBlock _ruleCount = OnlineUi.Text("", "SubtleNotice", 18);
    private readonly StackPanel _ruleList = new() { Spacing = 3 };
    private Button _ruleRetry, _ruleSave, _ruleApplyNow;
    private RulesCatalog _ruleCatalog;
    private List<RuleRow> _ruleRows = new();
    private RulesDocument _ruleDoc = new(), _ruleBaseline = new();
    private string _ruleWorldId;
    private bool _ruleLoading;
    private DispatcherTimer _ruleTimer;

    private static string RulesFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SE2 Online", "Rules");

    private Control BuildRulesPage()
    {
        _rulePreset.ItemsSource = RulePresets.All.Select(p => p.Name).ToList();
        _rulePreset.PlaceholderText = "Presets";
        _ruleFile.PlaceholderText = "Rules files in Documents\\SE2 Online\\Rules";
        _ruleRetry = OnlineUi.Button("Try rules again", () => { if (_ruleWorldId != null) WorldRulesStore.Retry(_ruleWorldId); RulesRefreshBanner(); MpMenu.Report("The rules are used again at this world's next load."); }, "RulesRetry");
        _ruleSave = OnlineUi.Button("Save rules", RulesSave, "RulesSave", true);
        _ruleApplyNow = OnlineUi.Button("Apply now", RulesApplyNow, "RulesApplyNow", true);
        var apply = OnlineUi.Button("Apply preset", RulesApplyPreset, "RulesPreset");
        var import = OnlineUi.Button("Import", RulesImport, "RulesImport");
        var export = OnlineUi.Button("Export", RulesExport, "RulesExport");
        var folder = OnlineUi.Button("Open folder", () => { Directory.CreateDirectory(RulesFolder); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RulesFolder) { UseShellExecute = true })?.Dispose(); }, "RulesFolder");
        var check = OnlineUi.Button("Check", () => RulesValidate(true), "RulesCheck");
        var reset = OnlineUi.Button("Reset all", () => { _ruleDoc = new RulesDocument(); RulesFilter(); MpMenu.Report("Every override removed. Save rules writes it."); }, "RulesReset");
        _ruleTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => { _ruleTimer.Stop(); RulesFilter(); });
        _ruleSearch.TextChanged += (_, _) => { _ruleTimer.Stop(); _ruleTimer.Start(); };
        _ruleCategory.SelectionChanged += (_, _) => RulesFilter();
        _ruleChanged.IsCheckedChanged += (_, _) => RulesFilter();
        _ruleSingle.IsCheckedChanged += (_, _) => RulesFilter();
        _ruleSearch.MinWidth = 560;
        _ruleBanner.IsVisible = false;
        _ruleRetry.IsVisible = false;

        var intro = OnlineUi.Text("Change anything the game has: blocks, tools, inventories, character, jetpack, thrust, power, production, weapons, AI, weather... Type a value, x10 for ten times the game's, durations as 90s, 10m or 1h30m. "
            + "Unsafe values are clamped or refused with the reason. Players who join get your rules; Apply now changes the running world at once.", "SubtleNotice", 18);
        intro.HorizontalAlignment = HorizontalAlignment.Left; intro.TextAlignment = TextAlignment.Left;
        foreach (var t in new[] { _ruleStatus, _ruleCount, _ruleWorld }) { t.HorizontalAlignment = HorizontalAlignment.Left; t.TextAlignment = TextAlignment.Left; }
        var top = new StackPanel { Spacing = 8 };
        foreach (var c in new Control[] { intro, _ruleWorld, _ruleBanner, _ruleRetry,
            OnlineUi.Row(_ruleSearch, _ruleCategory, _ruleChanged, _ruleSingle, _ruleCount),
            OnlineUi.Row(_rulePreset, apply, _ruleFile, import, export, folder),
            OnlineUi.Row(check, reset, _ruleSave, _ruleApplyNow), _ruleStatus })
            top.Children.Add(c);
        var header = OnlineUi.Panel(top, new Thickness(24, 16));
        var columns = OnlineUi.CellRow("3*,1.3*,1.4*,2.3*,Auto", OnlineUi.Text("SETTING", "SubtleNotice", 18), OnlineUi.Text("GAME VALUE", "SubtleNotice", 18),
            OnlineUi.Text("YOUR VALUE", "SubtleNotice", 18), OnlineUi.Text("RESULT", "SubtleNotice", 18), new Border { Width = 96 });
        columns.MinHeight = 30;
        var table = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        table.Children.Add(columns);
        var list = OnlineUi.Scroll(_ruleList);
        Grid.SetRow(list, 1); table.Children.Add(list);
        var tablePanel = OnlineUi.Panel(table, new Thickness(12, 8));
        tablePanel.Margin = new Thickness(0, 10, 0, 0);
        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        page.Children.Add(header);
        Grid.SetRow(tablePanel, 1); page.Children.Add(tablePanel);
        return page;
    }

    /// <summary>Test screenshots: type into the Rules page's search.</summary>
    internal void SearchRules(string text) { _ruleSearch.Text = text; RulesFilter(); }

    private static bool RunningOwnWorld => MpPlugin.App?.Server != null;

    // The rules file this page edits: the running world's (a new world's are the new-world rules until its first save), or
    // from the main menu the rules for the next new world.
    private static string RulesTarget(out string label)
    {
        var app = MpPlugin.App;
        if (app?.Server == null)
        {
            label = app?.Client != null ? null : "Rules for new worlds: the next world you create or host gets these, and keeps them when it is saved.";
            return app?.Client != null ? null : WorldRulesStore.NewWorld;
        }
        string id = app.Server.Entity.Get<WorldSessionComponent>()?.SaveContainerId?.ToString();
        if (string.IsNullOrEmpty(id) || CustomRulesHost.Unsaved && WorldRulesStore.IsNew(CustomRulesHost.WorldLabel))
        {
            label = "This new world uses the rules for new worlds; they stay with it when you save it.";
            return string.IsNullOrEmpty(id) || WorldRulesStore.IsNew(id) ? WorldRulesStore.NewWorld : id;
        }
        label = $"World '{id}'.";
        return id;
    }

    /// <summary>Opening the Rules page: reads the rules and (in the background) every setting of the game.</summary>
    private void RulesOpened()
    {
        RulesFiles();
        string id = RulesTarget(out string label);
        if (id == null)
        {
            _ruleWorldId = null;
            _ruleWorld.Text = "You are in someone else's world: its host sets the rules, and your game uses them while you play there.";
            _ruleList.Children.Clear();
            _ruleSave.IsEnabled = _ruleApplyNow.IsEnabled = false;
            return;
        }
        if (id != _ruleWorldId)
        {
            _ruleWorldId = id;
            try { _ruleBaseline = RulesDocument.Load(WorldRulesStore.FileFor(id)); }
            catch (Exception e) when (e is IOException or JsonException or FormatException or UnauthorizedAccessException)
            {
                _ruleBaseline = new RulesDocument();
                MpMenu.Report("These rules cannot be read: " + e.Message);
            }
            _ruleDoc = _ruleBaseline.Clone();
        }
        _ruleApplyNow.IsVisible = RunningOwnWorld;
        _ruleWorld.Text = label + $" {_ruleBaseline.Count} saved override(s). In force now: "
            + (RunningOwnWorld && CustomRulesHost.Current is { } now ? $"{now.Count} override(s), {HostRules.Offer?.Count ?? 0} definition value(s) for players" : "none")
            + (RunningOwnWorld && CustomRulesHost.Problems.Count > 0 ? $"; {CustomRulesHost.Problems.Count} clamped or refused (see the log)." : ".");
        RulesRefreshBanner();
        if (_ruleCatalog is { DefinitionCount: > 0 } || _ruleLoading) { RulesFilter(); return; }
        _ruleLoading = true;
        _ruleStatus.Text = "Reading the game's settings...";
        var session = CustomRulesHost.SessionComponents();
        System.Threading.Tasks.Task.Run(() => CustomRulesEngine.LiveCatalog(session)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _ruleLoading = false;
            if (t.Exception != null) { _ruleStatus.Text = "The game's settings cannot be read: " + t.Exception.GetBaseException().Message; return; }
            _ruleCatalog = t.Result;
            _ruleStatus.Text = _ruleCatalog.DefinitionCount == 0 ? "The game has not loaded its settings yet: load a world once to browse them. Rules in this file are listed below."
                : $"{_ruleCatalog.DefinitionCount:N0} definitions of {_ruleCatalog.Definitions.Count:N0} types and {_ruleCatalog.Session.Count} session components.";
            RulesBuild();
        }));
    }

    private void RulesRefreshBanner()
    {
        var check = _ruleWorldId == null ? null : WorldRulesStore.Check(_ruleWorldId);
        bool failed = check != null && (check.Skipped || !string.IsNullOrEmpty(check.Error));
        _ruleBanner.Text = !failed ? "" : (check.Skipped ? "This world did not finish loading with its rules last time, so it was loaded WITHOUT them. " : "The world failed to load with these rules; the next load skips them. ")
            + (string.IsNullOrEmpty(check.Error) ? "" : check.Error + " ") + "Change the overrides it names, or try them again.";
        _ruleBanner.IsVisible = _ruleRetry.IsVisible = failed;
    }

    private static string FieldLabel(string field, string type)
    {
        string unit = RuleText.Unit(field, type);
        return string.Join(" / ", field.Split('.').Select(RuleText.Label)) + (string.IsNullOrEmpty(unit) ? "" : $" ({unit})");
    }

    private void RulesBuild()
    {
        var rows = new List<RuleRow>();
        foreach (var e in _ruleCatalog.Session)
            foreach (var f in e.Fields.Values.Where(f => f.Writable && RuleSafety.Locked(true, f.Name, f.Type) == null))
                rows.Add(new RuleRow { Session = true, Key = e.Type, Field = f.Name, Type = f.Type, Title = RuleText.Label(e.ShortType) + ": " + FieldLabel(f.Name, f.Type),
                    Subtitle = $"{e.ShortType}.{f.Name} | session setting ({RuleText.TypeName(f.Type)}), saved into the world", Game = RuleText.Show(f.Value, 30, f.Type), GameValue = f.Value, Category = "Session" });
        foreach (var (type, list) in _ruleCatalog.Definitions)
        {
            var first = list[0];
            string category = RulesCatalog.Category(type) + (RulesCatalog.Visual(type) ? " (visual)" : "");
            string loadTime = first.LoadTime == null ? "" : " | " + first.LoadTime;
            foreach (var f in first.Fields.Values.Where(f => f.Writable && RuleSafety.Locked(false, f.Name, f.Type) == null))
            {
                var values = list.Select(d => d.Fields.TryGetValue(f.Name, out var x) ? x.Value : null).ToList();
                string game = values.Select(v => RuleText.Show(v, 30, f.Type)).Distinct().Count() == 1 ? RuleText.Show(values[0], 30, f.Type)
                    : values.All(v => v is { ValueKind: JsonValueKind.Number }) ? $"{RuleText.Show(values.MinBy(v => v.Value.GetDouble()), 30, f.Type)} to {RuleText.Show(values.MaxBy(v => v.Value.GetDouble()), 30, f.Type)}" : "varies";
                string name = list.Count == 1 ? RuleText.DefinitionName(first.DebugName, type) : "All " + RuleText.Label(first.ShortType);
                rows.Add(new RuleRow { Key = type, Field = f.Name, Type = f.Type, Title = name + ": " + FieldLabel(f.Name, f.Type),
                    Subtitle = $"{first.ShortType}.{f.Name} | {(list.Count > 1 ? list.Count + " definitions, " : "")}{RuleText.TypeName(f.Type)} | {category}{loadTime}", Game = game, GameValue = values.FirstOrDefault(v => v != null), Category = category });
                if (list.Count > 1)
                    foreach (var d in list)
                        if (d.Fields.TryGetValue(f.Name, out var df))
                            rows.Add(new RuleRow { Single = true, Key = d.Key, Field = f.Name, Type = df.Type, Title = RuleText.DefinitionName(d.DebugName, type) + ": " + FieldLabel(f.Name, df.Type),
                                Subtitle = $"{first.ShortType}#{d.Id}.{f.Name} | one {RuleText.Label(first.ShortType)} | {category}{loadTime}", Game = RuleText.Show(df.Value, 30, df.Type), GameValue = df.Value, Category = category });
            }
        }
        // Overrides the game does not list (a mod that is not loaded) stay visible so they can be removed.
        var known = rows.Select(r => (r.Session, r.Key, r.Field)).ToHashSet();
        foreach (var (session, section) in new[] { (true, _ruleDoc.SessionSettings), (false, _ruleDoc.DefinitionSettings) })
            foreach (var (key, fields) in section)
                foreach (var field in fields.Keys.Where(f => !known.Contains((session, key, f))))
                    rows.Add(new RuleRow { Session = session, Key = key, Field = field, Type = null, Title = RuleText.Label(key.Split('#')[0].Split('.').Last()) + ": " + FieldLabel(field, null),
                        Subtitle = $"{key}.{field} | not in this game's list", Game = "?", Category = "Not in this game", Single = key.Contains('#') });
        foreach (var r in rows) r.Search = (r.Title + " " + r.Subtitle + " " + r.Key).ToLowerInvariant();
        _ruleRows = rows;
        _ruleCategory.ItemsSource = new[] { "All categories" }.Concat(rows.Select(r => r.Category).Distinct().OrderBy(c => c)).ToList();
        _ruleCategory.SelectedIndex = 0;
        RulesFilter();
    }

    private void RulesFilter()
    {
        _ruleList.Children.Clear();
        if (_ruleCatalog == null) return;
        string[] words = (_ruleSearch.Text ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string category = _ruleCategory.SelectedIndex > 0 ? _ruleCategory.SelectedItem as string : null;
        bool changedOnly = _ruleChanged.IsChecked == true, single = _ruleSingle.IsChecked == true;
        bool Has(RuleRow r) => _ruleDoc.Get(r.Session, r.Key, r.Field) != null;
        var shown = _ruleRows.Where(r => (!changedOnly || Has(r)) && (category == null || r.Category == category) && (!r.Single || Has(r) || single && words.Length > 0)
            && words.All(w => r.Search.Contains(w, StringComparison.Ordinal))).ToList();
        foreach (var r in shown.OrderByDescending(Has).Take(RulesShown)) _ruleList.Children.Add(RuleView(r));
        int online = _ruleDoc.Online?.Count ?? 0;
        _ruleCount.Text = (shown.Count > RulesShown ? $"{RulesShown} of {shown.Count:N0} shown; search to narrow." : $"{shown.Count:N0} shown.")
            + $" {_ruleDoc.Count} override(s){(online > 0 ? $" + {online} SE2 Online option(s)" : "")}{(RulesChanged ? ", not saved yet" : "")}.";
        _ruleSave.IsEnabled = _ruleWorldId != null && RulesChanged;
    }

    private bool RulesChanged => _ruleDoc.Hash() != _ruleBaseline.Hash() || (_ruleDoc.Online?.Count ?? 0) > 0;

    private Control RuleView(RuleRow r)
    {
        var name = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        var title = OnlineUi.Text(r.Title, "Bright", 20); title.HorizontalAlignment = HorizontalAlignment.Left;
        var sub = OnlineUi.Text(r.Subtitle, "SubtleNotice", 15); sub.HorizontalAlignment = HorizontalAlignment.Left; sub.TextAlignment = TextAlignment.Left;
        name.Children.Add(title); name.Children.Add(sub);
        var game = OnlineUi.Text(r.Game, null, 18);
        bool duration = RuleText.IsDuration(r.Type);
        var box = new TextBox { Text = RuleText.Edit(_ruleDoc.Get(r.Session, r.Key, r.Field), r.Type), Watermark = duration ? "e.g. 90s, 10m, 1h30m" : "game's value", FontSize = 20, MinHeight = 44, MaxLength = 4096, VerticalAlignment = VerticalAlignment.Center };
        var result = new TextBlock { FontSize = 17, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        void Show()
        {
            var spec = _ruleDoc.Get(r.Session, r.Key, r.Field) ?? (r.Single ? _ruleDoc.Get(false, r.Key.Split('#')[0], r.Field) : null);
            if (spec == null) { result.Text = ""; return; }
            bool typeWide = !r.Session && !r.Key.Contains('#') && _ruleCatalog.Definitions.TryGetValue(r.Key, out var list) && list.Count > 1;
            if (typeWide)
            {
                var worst = RulesCheck.Validate(new RulesDocument { DefinitionSettings = { [r.Key] = new() { [r.Field] = spec.Value } } }, _ruleCatalog).OrderByDescending(i => i.Severity).FirstOrDefault();
                result.Text = worst == null ? "OK on every one" : worst.Severity + ": " + worst.Message;
                result.Foreground = worst == null ? RuleOk : worst.Severity == RuleSeverity.Error ? RuleBad : RuleWarn;
                return;
            }
            var (text, severity) = RuleText.Outcome(r.Session, r.Field, r.Type, r.GameValue, spec.Value);
            result.Text = text;
            result.Foreground = severity == RuleSeverity.Error ? RuleBad : severity == RuleSeverity.Clamped ? RuleWarn : RuleOk;
        }
        void Commit()
        {
            _ruleDoc.Set(r.Session, r.Key, r.Field, RuleText.Parse(box.Text, r.Type));
            Show();
            _ruleSave.IsEnabled = _ruleWorldId != null && RulesChanged;
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        var reset = OnlineUi.SmallButton("Reset", () => { box.Text = ""; Commit(); });
        Show();
        var row = OnlineUi.CellRow("3*,1.3*,1.4*,2.3*,Auto", name, game, box, result, reset);
        row.MinHeight = 56;
        return new Border { Child = row, Background = RuleRowBack, Padding = new Thickness(0, 2) };
    }

    private void RulesApplyPreset()
    {
        if (_rulePreset.SelectedIndex < 0) { MpMenu.Report("Pick a preset first."); return; }
        var preset = RulePresets.All[_rulePreset.SelectedIndex];
        _ruleDoc = _ruleDoc.Merge(preset.Build());
        RulesFilter();
        MpMenu.Report($"Preset '{preset.Name}' added: {preset.Description} Save rules writes it.");
    }

    private bool RulesValidate(bool report)
    {
        var issues = RulesCheck.Validate(_ruleDoc, _ruleCatalog is { DefinitionCount: > 0 } ? _ruleCatalog : null);
        var online = new List<string>();
        if (_ruleDoc.Online is { Count: > 0 }) OnlineRules.Apply(MpMenu.Settings, _ruleDoc.Online, online);
        int errors = issues.Count(i => i.Severity == RuleSeverity.Error) + online.Count;
        if (report || errors > 0)
            _ruleStatus.Text = issues.Count + online.Count == 0 ? $"All {_ruleDoc.Count} override(s){(_ruleDoc.Online is { Count: > 0 } ? $" and {_ruleDoc.Online.Count} SE2 Online option(s)" : "")} check out."
                : $"{errors} error(s), {issues.Count(i => i.Severity == RuleSeverity.Clamped)} clamped, {issues.Count(i => i.Severity == RuleSeverity.Warning)} warning(s). First: {(online.Count > 0 ? online[0] : issues.OrderByDescending(i => i.Severity).First().ToString())}";
        return errors == 0;
    }

    private void RulesSave()
    {
        if (_ruleWorldId == null) return;
        if (!RulesValidate(false)) { MpMenu.Report("Not saved: fix or remove the overrides with errors (they would be refused)."); return; }
        try
        {
            if (_ruleDoc.Online is { Count: > 0 } online)
            {
                var problems = new List<string>();
                var settings = OnlineRules.Apply(MpMenu.Settings, online, problems);
                MpMenu.SaveSettings(settings);
                ReadSettings();
                _ruleDoc.Online = null;
            }
            var rules = _ruleDoc.Clone();
            rules.Online = null;
            string path = WorldRulesStore.FileFor(_ruleWorldId);
            if (rules.IsEmpty) { if (File.Exists(path)) File.Delete(path); }
            else rules.Save(path);
            WorldRulesStore.Retry(_ruleWorldId);
            _ruleBaseline = rules.Clone();
            RulesFilter();
            RulesRefreshBanner();
            MpMenu.Report($"Saved {rules.Count} override(s)." + (RunningOwnWorld ? " Apply now changes the running world; otherwise they apply at its next load." : " The next new world gets them."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { MpMenu.Report("Could not save the rules: " + e.Message); }
    }

    private void RulesApplyNow()
    {
        if (!RunningOwnWorld) return;
        if (RulesChanged) RulesSave();
        var host = NetSession.Current?.Host;
        string report = CustomRulesHost.ApplyLive(_ruleBaseline, offer => host?.BroadcastRules(offer));
        _ruleStatus.Text = "Applied now: " + report + (_ruleBaseline.SessionSettings.Count > 0 ? " Session settings are read when the world loads: they apply at its next load." : "");
        RulesOpened();
    }

    private void RulesFiles()
    {
        try
        {
            Directory.CreateDirectory(RulesFolder);
            _ruleFile.ItemsSource = new DirectoryInfo(RulesFolder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.Name).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _ruleFile.ItemsSource = Array.Empty<string>(); }
    }

    private void RulesImport()
    {
        if (_ruleFile.SelectedItem is not string name) { MpMenu.Report("Put a rules file (.se2rules.json) in Documents\\SE2 Online\\Rules and pick it in the list."); return; }
        try
        {
            var imported = RulesDocument.Load(Path.Combine(RulesFolder, name));
            _ruleDoc = imported;
            if (_ruleCatalog != null) RulesBuild(); else RulesFilter();
            RulesValidate(true);
            MpMenu.Report($"Imported '{(imported.Name.Length > 0 ? imported.Name : name)}': {imported.Count} override(s)"
                + (imported.Online is { Count: > 0 } ? $" and {imported.Online.Count} SE2 Online option(s) (PvP, multipliers, hacking...)" : "") + ". Check them, then Save rules.");
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException or UnauthorizedAccessException) { MpMenu.Report("Cannot import " + name + ": " + e.Message); }
    }

    private void RulesExport()
    {
        try
        {
            var doc = _ruleDoc.Clone();
            doc.Online = OnlineRules.From(MpMenu.Settings);
            doc.Game = Core.HookTargets.GameBuild;
            string baseName = new string((_ruleWorldId is { Length: > 0 } ? _ruleWorldId : "new-worlds").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            if (doc.Name.Length == 0) doc.Name = baseName;
            string path = Path.Combine(RulesFolder, $"{baseName}-{DateTime.Now:yyyyMMdd-HHmm}.se2rules.json");
            doc.Save(path);
            RulesFiles();
            MpMenu.Report($"Exported {doc.Count} override(s) and {doc.Online.Count} SE2 Online option(s) to Documents\\SE2 Online\\Rules\\{Path.GetFileName(path)}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { MpMenu.Report("Cannot export: " + e.Message); }
    }
}
