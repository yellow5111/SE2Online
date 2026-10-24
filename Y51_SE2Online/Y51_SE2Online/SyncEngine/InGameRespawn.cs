using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using HarmonyLib;
using Keen.Game2.Client.UI.Death;
using Keen.Game2.Simulation.GameSystems.Respawn;
using Keen.Game2.Simulation.StreamedUI.Respawn;
using Keen.VRage.UI.AvaloniaInterface.Utilities;
using Y51_SE2Online.Core;
using Y51_SE2Online.KeenNetcode;
using Y51_SE2Online.Sync;
using C = Y51_SE2Online.SyncEngine.RespawnCardsUi;
using KeenScreen = Keen.Game2.Client.UI.Death.RespawnScreen;
using L = Y51_SE2Online.Sync.LoadScreenLayout;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// SE2 Online's spawn menu inside the game's own respawn screen. <see cref="KeenRespawn"/> opens that screen
/// (InGameUI.OpenRespawnScreen, as the game does) while the host's menu waits; this adds to it: the wordmark, the home base
/// first in the game's row of points, our status, distance and planet on the game's entries, and every other card of the
/// menu as more entries. The game's toggles, Refresh and Respawn buttons stay in charge: its Home, Closest and Default
/// answers and a picked extra entry are mapped back onto the menu (<see cref="Resolve"/>). If anything here fails the
/// additions are taken out again and the plain overlay (<see cref="RespawnScreen"/>) takes over.
/// </summary>
internal static class InGameRespawn
{
    // Bisect switch: -y51online:respawnscreen:off uses the plain overlay instead of the game's screen.
    private static readonly bool Off = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "respawnscreen:off");

    /// <summary>The overlay shows the menu instead of the game's screen: switched off or a hook did not install (for the
    /// session), or this spawn's screen failed (until the next spawn, <see cref="Begin"/>).</summary>
    internal static bool Fallback => Off || _installFailed || _screenFailed;

    private static volatile bool _installFailed, _screenFailed, _expecting, _cancelled;
    private static long _openedMs;
    private static volatile RespawnScreenViewModel _vm;
    private static Additions _shown;
    // The player's pick among our extra entries, kept by what it is: a new menu (home set or cleared) renumbers the cards.
    private static volatile SpawnOption _picked;
    // What ConfirmRespawn approved; the pick can still change (a double click's second release, a new menu) before Resolve.
    private static volatile SpawnOption _confirmed;

    internal static void Install(Harmony harmony)
    {
        if (Off) { GameLog.Instance.Info("spawn: the game's respawn screen is off (launch option); the plain spawn list shows the menu"); return; }
        try
        {
            harmony.Patch(AccessTools.GetDeclaredConstructors(typeof(RespawnScreenViewModel)).Single(), postfix: new HarmonyMethod(typeof(InGameRespawn), nameof(ModelPostfix)));
            harmony.Patch(AccessTools.Method(typeof(KeenScreen), nameof(KeenScreen.InitializeComponent)), postfix: new HarmonyMethod(typeof(InGameRespawn), nameof(ViewPostfix)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(RespawnScreenViewModel), nameof(RespawnScreenViewModel.HasValidSelection)), postfix: new HarmonyMethod(typeof(InGameRespawn), nameof(ValidPostfix)));
            harmony.Patch(AccessTools.Method(typeof(RespawnScreenViewModel), nameof(RespawnScreenViewModel.ConfirmRespawn)), prefix: new HarmonyMethod(typeof(InGameRespawn), nameof(ConfirmPrefix)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(RespawnScreenViewModel), nameof(RespawnScreenViewModel.HasRespawnPoints)), postfix: new HarmonyMethod(typeof(InGameRespawn), nameof(PointsPostfix)));
            // The constructor's MenuScreen trigger: the compiler-made lambda that calls InGameUI.CreateInGameMenuScreen.
            var menuKey = AccessTools.GetDeclaredMethods(typeof(RespawnScreenViewModel)).Concat(typeof(RespawnScreenViewModel).GetNestedTypes(System.Reflection.BindingFlags.NonPublic).SelectMany(AccessTools.GetDeclaredMethods))
                .FirstOrDefault(m => m.Name.Contains("ctor") && m.Name.Contains("b__") && PatchProcessor.GetOriginalInstructions(m).Any(i => i.operand is System.Reflection.MethodInfo call && call.Name == "CreateInGameMenuScreen"));
            if (menuKey != null) harmony.Patch(menuKey, prefix: new HarmonyMethod(typeof(InGameRespawn), nameof(MenuKeyPrefix)));
            else GameLog.Instance.Warn("spawn: the respawn screen's Esc binding was not found; Esc on an open ship list also opens the pause menu");
        }
        catch (Exception ex)
        {
            _installFailed = true;
            GameLog.Instance.Warn($"spawn: the game's respawn screen cannot carry the menu ({ex.GetType().Name}: {ex.Message}); the plain spawn list shows it");
        }
    }

    /// <summary>A new spawn: the game's screen gets another chance after a failed one.</summary>
    internal static void Begin() => _screenFailed = false;

    /// <summary>Game thread, just before KeenRespawn opens the game's screen for the pending menu. The previous screen's
    /// additions stop themselves once they are no longer <see cref="_shown"/>.</summary>
    internal static void Opening()
    {
        _expecting = true; _cancelled = false; _picked = null; _confirmed = null; _vm = null; _shown = null;
        _openedMs = MpClock.NowMs();
    }

    /// <summary>The screen opened but never showed our additions (no view, or a view without the expected layout).</summary>
    internal static bool Stalled => _expecting && _shown == null && MpClock.NowMs() - _openedMs > 5000;

    /// <summary>Closes the game's screen without a choice (the menu went away, or the overlay takes over).</summary>
    internal static void Cancel()
    {
        _cancelled = true;
        var vm = _vm;
        Dispatcher.UIThread.Post(() => { try { if (vm != null && !vm._optionSelectedTask.Task.IsCompleted) vm.ConfirmRespawn(); } catch (Exception ex) { GameLog.Instance.WarnOnce("respawn-cancel", "spawn: closing the game's respawn screen failed: " + ex.Message); } });
    }

    internal static void Fail(string what, Exception ex)
    {
        if (Fallback) return;
        _screenFailed = true;
        GameLog.Instance.Warn($"spawn: the game's respawn screen could not carry the menu ({what}: {ex.GetType().Name}: {ex.Message}); the plain spawn list shows it");
        Remove();
        Cancel();
    }

    /// <summary>Game thread, once the game's screen answered: the menu card to spawn at, -1 for none (it opens again).</summary>
    internal static int Resolve(RespawnRequestType type)
    {
        _expecting = false;
        var menu = KeenRespawn.Menu;
        var confirmed = _confirmed;
        if (menu == null || _cancelled || confirmed == null) return -1;
        int index = Array.FindIndex(menu.Options, x => Same(x, confirmed));
        GameLog.Instance.Info($"spawn: the game's respawn screen answered {type} ({SpawnGroups.Label(confirmed)}): {(index < 0 ? "no usable card" : index + " " + SpawnGroups.Label(menu.Options[index]))}");
        return index >= 0 && menu.Options[index].Available ? index : -1;
    }

    private static int Target(SpawnMenu menu, RespawnRequestType type, SpawnOption picked, RespawnPoint closest)
    {
        var o = menu.Options;
        if (picked != null) return Array.FindIndex(o, x => Same(x, picked));
        return type switch
        {
            RespawnRequestType.Home => HomeIndex(menu),
            RespawnRequestType.Closest => ClosestIndex(menu, closest),
            _ => Array.FindIndex(o, x => x.Kind == SpawnKind.Suit)
        };
    }

    // The same place across menus: setting a home turns a medbay card into the home card (Kind and Home change).
    private static bool Same(SpawnOption a, SpawnOption b) => a.Name == b.Name && a.Grid == b.Grid && a.Place == b.Place && a.Group == b.Group;

    /// <summary><paramref name="option"/> as the current menu has it (its status may have changed), or null.</summary>
    private static SpawnOption Current(SpawnMenu menu, SpawnOption option) => menu == null || option == null ? null : menu.Options.FirstOrDefault(o => Same(o, option));

    private static int HomeIndex(SpawnMenu menu) => Array.FindIndex(menu.Options, x => x.Home || x.Kind == SpawnKind.Home);

    // The game's nearest point is one of the player's own medbays: matched by block and grid name and distance (both measure
    // from the death point), then by name alone, else our nearest usable one.
    private static int ClosestIndex(SpawnMenu menu, RespawnPoint closest)
    {
        var o = menu.Options;
        if (closest != null)
        {
            bool Named(SpawnOption x) => x.Kind == SpawnKind.Medbay && !x.Home && x.Name == closest.Name && x.Grid == (closest.GridName ?? "");
            int near = Array.FindIndex(o, x => Named(x) && x.Distance >= 0 && Math.Abs(x.Distance - closest.Distance) <= 5);
            if (near >= 0) return near;
            int named = Array.FindIndex(o, Named);
            if (named >= 0) return named;
        }
        int best = -1;
        for (int i = 0; i < o.Length; i++)
            if (o[i].Available && (o[i].Kind == SpawnKind.Medbay || o[i].Home || o[i].Kind == SpawnKind.Home) && (best < 0 || Nearer(o[i], o[best]))) best = i;
        return best;
    }

    private static bool Nearer(SpawnOption a, SpawnOption b) => (a.Distance < 0 ? double.MaxValue : a.Distance) < (b.Distance < 0 ? double.MaxValue : b.Distance);

    private static bool Ours(RespawnScreenViewModel vm) => vm != null && ReferenceEquals(vm, _vm) && !Fallback;

    // A picked extra entry decides the Respawn button (the game's own test only knows Home and Closest).
    private static void ValidPostfix(RespawnScreenViewModel __instance, ref bool __result)
    {
        if (!Ours(__instance) || _picked is not { } picked) return;
        __result = Current(KeenRespawn.Menu, picked) is { Available: true } && !__instance.IsRefreshing && !C.Updating;
    }

    // The game enables Respawn with nothing chosen when it knows no points (first join); our menu always has cards, so the
    // button waits for a real choice (and stays off while only a ship, no destination, is picked).
    private static void PointsPostfix(RespawnScreenViewModel __instance, ref bool __result)
    {
        if (Ours(__instance)) __result = true;
    }

    /// <summary>UI thread: changes the pick and makes the Respawn button ask HasValidSelection again (it changes with IsRefreshing).</summary>
    private static void SetPicked(RespawnScreenViewModel vm, SpawnOption option)
    {
        _picked = option;
        if (vm != null && !vm.IsRefreshing) { vm.IsRefreshing = true; vm.IsRefreshing = false; }
    }

    // The game confirms Default even with nothing chosen when it knows no points; an unusable card is not confirmed.
    private static bool ConfirmPrefix(RespawnScreenViewModel __instance)
    {
        try
        {
            if (!Ours(__instance) || _cancelled) return true;
            var menu = KeenRespawn.Menu;
            if (menu == null) return true;
            if (_confirmed != null) return false;
            if (C.Updating) { _shown?.Refuse(null); return false; }
            var type = __instance.SelectedRespawnRequestType;
            // The game's own rule: with points to choose from, nothing chosen is not a choice (Default would be the suit).
            if (type == RespawnRequestType.Default && _picked == null && __instance.HasRespawnPoints) { _shown?.Refuse(null); return false; }
            int index = Target(menu, type, _picked, __instance.RespawnPointsData?.ClosestPoint);
            if (index < 0 || !menu.Options[index].Available)
            {
                _shown?.Refuse(index < 0 ? null : menu.Options[index]);
                return false;
            }
            _confirmed = menu.Options[index];
            return true;
        }
        catch (Exception ex) { GameLog.Instance.WarnOnce("respawn-confirm", "spawn: checking the game's respawn choice failed: " + ex.Message); return true; }
    }

    // The model binds the game's MenuScreen action (Esc) to opening the pause menu, apart from Avalonia's key events: while a
    // ship list is open on our screen, Esc closes the list instead.
    private static bool MenuKeyPrefix()
    {
        try
        {
            var shown = _shown;
            if (shown == null || Fallback) return true;
            if (shown.CloseOnEscape()) { Dispatcher.UIThread.Post(Guard(shown.CollapseList)); return false; }
            return !shown.RecentlyClosed;
        }
        catch (Exception ex) { GameLog.Instance.WarnOnce("respawn-menu-key", "spawn: Esc on the respawn screen: " + ex.Message); return true; }
    }

    // The screen model KeenRespawn's open makes; known before its view, so the screen can be closed even without one.
    private static void ModelPostfix(RespawnScreenViewModel __instance)
    {
        if (_expecting && _vm == null) _vm = __instance;
    }

    private static void ViewPostfix(KeenScreen __instance)
    {
        try
        {
            if (!_expecting || Fallback) return;
            void Check()
            {
                if (_shown != null || !_expecting || Fallback || __instance.DataContext is not RespawnScreenViewModel vm || !ReferenceEquals(vm, _vm)) return;
                try
                {
                    _shown = new Additions(__instance, vm);
                    GameLog.Instance.Info("spawn: SE2 Online's menu is in the game's respawn screen");
                }
                catch (Exception ex) { Fail("adding to it", ex); }
            }
            __instance.DataContextChanged += (_, _) => Check();
            Check();
        }
        catch (Exception ex) { Fail("hooking it", ex); }
    }

    private static Action Guard(Action action) => () => { try { action(); } catch (Exception ex) { Fail("updating it", ex); } };

    private static void Remove()
    {
        var shown = _shown;
        _shown = null;
        if (shown != null) Dispatcher.UIThread.Post(shown.Undo);
    }

    /// <summary>Everything added to one game screen, and how to take it out again.</summary>
    private sealed class Additions
    {
        private readonly KeenScreen _view;
        private readonly RespawnScreenViewModel _vm;
        private readonly List<Action> _undo = new();
        private readonly StackPanel _points, _gameHome, _gameClosest;
        private readonly Grid _homeContent, _closestContent;
        private StackPanel _ourHome;
        private readonly Panel _homeStrip = new StackPanel(), _closestStrip = new StackPanel();
        // Made at the first rebuild, when the column count is chosen; it stays while the screen is open.
        private CardGrid _extras;
        private ScrollViewer _scroll;
        private Grid _body;
        private readonly TextBlock _message, _hint, _extrasTitle;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private CheckBox _ourHomeBox;
        private bool _updating;
        // The enlarged ship picture: the ship under the pointer, else the focused one, else the picked one.
        private readonly C.ShipPanel _panel = new(PanelWidth) { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private SpawnOption _hover, _focus;
        // The ship whose destinations are open under it (its group name), "" for none.
        private string _expanded = "";
        private readonly List<CheckBox> _destinationBoxes = new();
        private readonly Dictionary<string, CheckBox> _shipBoxes = new();
        // The game's entries are 400x300 by its style; at least 220 holds their three facts (taller when names wrap) and
        // leaves room for two rows of ours.
        private const double GameEntryHeight = 220, PanelWidth = 420, ButtonGap = 16, RowGap = 16;
        // Every card's first text block (name and place beside the badge column) is at least this tall: the badge with the
        // home button under it fits, so the lines below start at the same height on every card.
        private const double HeadHeight = 58;
        // The game's own tiles (CheckBox.RespawnPoint is 400 wide, its RespawnPoints row spaces them 40 apart); read from the
        // screen when it opens. Our cards take exactly those columns, so their edges meet the Home and Nearest tiles' edges.
        private double _tileWidth = 400, _tileGap = 40;
        private SpawnMenu _menu;

        public Additions(KeenScreen view, RespawnScreenViewModel vm)
        {
            _view = view; _vm = vm;
            _points = view.RespawnPoints ?? throw new InvalidOperationException("no RespawnPoints panel");
            if (_points.Parent is not Grid body || body.RowDefinitions.Count != 3) throw new InvalidOperationException("the respawn points sit in an unknown layout");
            if (_points.Children.Count != 2 || _points.Children[0] is not StackPanel closest || _points.Children[1] is not StackPanel home)
                throw new InvalidOperationException("the respawn points are not the closest and home entries");
            _gameClosest = closest; _gameHome = home;
            _closestContent = EntryGrid(closest); _homeContent = EntryGrid(home);
            try
            {
                int styles = view.Styles.Count;
                C.ActionStyles(view.Styles);
                // The game's entry values (block and grid names) wrap instead of running out of the card.
                view.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<TextBlock>().Class("RespawnPointValue")) { Setters = { new Avalonia.Styling.Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap), new Avalonia.Styling.Setter(TextBlock.TextTrimmingProperty, TextTrimming.None), new Avalonia.Styling.Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right) } });
                _undo.Add(() => { while (view.Styles.Count > styles) view.Styles.RemoveAt(view.Styles.Count - 1); });

                var brand = C.Wordmark(22);
                brand.HorizontalAlignment = HorizontalAlignment.Left; brand.VerticalAlignment = VerticalAlignment.Top; brand.Margin = new Thickness(80, 44, 0, 0);
                Add(body, brand, 0);
                if (body.Children.OfType<StackPanel>().FirstOrDefault(p => Grid.GetRow(p) == 0) is { } header)
                {
                    var accent = new Border { Width = 96, Height = 4, Background = C.Brush(L.Amber), HorizontalAlignment = HorizontalAlignment.Center };
                    header.Children.Add(accent);
                    _undo.Add(() => header.Children.Remove(accent));
                }

                // The middle row: the game's points (home base first), then the rest of the menu.
                // A clear gap between the list's bottom and the game's Refresh and Respawn buttons.
                var middle = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"), Margin = new Thickness(0, 10, 0, ButtonGap) };
                body.Children.Remove(_points);
                _undo.Add(() => { middle.Children.Remove(_points); if (!body.Children.Contains(_points)) body.Children.Add(_points); Grid.SetRow(_points, 1); });
                _message = C.Wrap("", 20, L.Amber);
                _message.HorizontalAlignment = HorizontalAlignment.Center; _message.TextAlignment = TextAlignment.Center; _message.MaxWidth = 1300; _message.Margin = new Thickness(0, 0, 0, 12);
                middle.Children.Add(_message);
                Grid.SetRow(_points, 1); middle.Children.Add(_points);
                _extrasTitle = C.Text("MORE SPAWN POINTS", 18, L.Amber, FontWeight.Bold);
                _extrasTitle.HorizontalAlignment = HorizontalAlignment.Center; _extrasTitle.Margin = new Thickness(0, 6, 0, 2);
                Grid.SetRow(_extrasTitle, 2); middle.Children.Add(_extrasTitle);
                var scroll = _scroll = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalAlignment = VerticalAlignment.Top };
                Grid.SetRow(scroll, 3); middle.Children.Add(scroll);
                // The list's edges: a thin line and a short fade while more cards are past them, so cut cards end on an edge.
                _fade = Fade(false); _fadeTop = Fade(true);
                Grid.SetRow(_fade, 3); middle.Children.Add(_fade);
                Grid.SetRow(_fadeTop, 3); middle.Children.Add(_fadeTop);
                scroll.ScrollChanged += (_, _) => Edge();
                Add(body, middle, 1);
                _body = body;
                // The enlarged picture's top follows the list's top (rows 1 and 2, so a tall picture and text are not cut).
                Grid.SetRowSpan(_panel, 2);
                Add(body, _panel, 1);
                scroll.PropertyChanged += (_, e) => { if (e.Property == Visual.BoundsProperty) AlignPanel(); };
                foreach (var box in new[] { _gameHome, _gameClosest }.Select(p => p.Children.OfType<CheckBox>().First()))
                {
                    box.Height = double.NaN;
                    box.MinHeight = GameEntryHeight;
                    _undo.Add(() => { box.ClearValue(Layoutable.HeightProperty); box.ClearValue(Layoutable.MinHeightProperty); });
                }

                _points.Children.Move(1, 0);
                _undo.Add(() => { if (_points.Children.IndexOf(_gameHome) == 0 && _points.Children.Contains(_gameClosest)) _points.Children.Move(0, _points.Children.Count - 1); });
                Strip(_gameHome, _homeStrip);
                Strip(_gameClosest, _closestStrip);
                Icon(_homeContent, C.IconKind.Home);
                Icon(_closestContent, C.IconKind.Medbay);

                _hint = C.Wrap("", 18, L.Muted);
                _hint.TextAlignment = TextAlignment.Center; _hint.MaxWidth = 1300;
                _hint.HorizontalAlignment = HorizontalAlignment.Center; _hint.VerticalAlignment = VerticalAlignment.Bottom; _hint.Margin = new Thickness(0, 0, 0, 56);
                Add(body, _hint, 2);

                // Esc, or the gamepad's B (the UI's Escape key), closes an open ship list wherever the focus is in the screen.
                EventHandler<Avalonia.Input.KeyEventArgs> escape = (_, e) =>
                {
                    if (e.Key != Avalonia.Input.Key.Escape) return;
                    try { if (CloseOnEscape()) { e.Handled = true; Collapse(); } else if (RecentlyClosed) e.Handled = true; }
                    catch (Exception ex) { Fail("closing a ship", ex); }
                };
                view.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, escape, Avalonia.Interactivity.RoutingStrategies.Tunnel);
                _undo.Add(() => view.RemoveHandler(Avalonia.Input.InputElement.KeyDownEvent, escape));
                vm.PropertyChanged += OnModel;
                _undo.Add(() => vm.PropertyChanged -= OnModel);
                _timer.Tick += (_, _) => Update();
                _timer.Start();
                _undo.Add(_timer.Stop);
                view.DetachedFromVisualTree += (_, _) => _timer.Stop();
                view.AttachedToVisualTree += (_, _) => { if (_shown == this) _timer.Start(); };
                Update();
            }
            catch
            {
                Undo();
                throw;
            }
        }

        // An entry is a StackPanel holding the game's CheckBox, whose content is a Grid: the label row, then RespawnPointItem.
        private static Grid EntryGrid(StackPanel entry)
            => entry.Children.OfType<CheckBox>().FirstOrDefault()?.Content as Grid ?? throw new InvalidOperationException("a respawn entry has an unknown layout");

        private void Add(Grid grid, Control control, int row)
        {
            Grid.SetRow(control, row);
            grid.Children.Add(control);
            _undo.Add(() => grid.Children.Remove(control));
        }

        // Under the game's CheckBox, not inside it: its IsEnabled follows the point's availability, and "Clear home" must work
        // exactly when the home is unpowered or damaged.
        private void Strip(StackPanel entry, Panel strip)
        {
            entry.Children.Add(strip);
            _undo.Add(() => entry.Children.Remove(strip));
        }

        private void Icon(Grid content, C.IconKind kind)
        {
            var hex = C.Hex(kind, true, 48);
            hex.HorizontalAlignment = HorizontalAlignment.Left; hex.Margin = new Thickness(12, 0, 0, 0); hex.IsHitTestVisible = false;
            content.Children.Add(hex);
            _undo.Add(() => content.Children.Remove(hex));
        }

        public void Undo()
        {
            if (_ourHome != null) { _points.Children.Remove(_ourHome); _ourHome = null; _ourHomeBox = null; }
            for (int i = _undo.Count - 1; i >= 0; i--)
            {
                try { _undo[i](); }
                catch (Exception ex) { GameLog.Instance.WarnOnce("respawn-undo", "spawn: taking SE2 Online's additions out of the game's respawn screen failed: " + ex.Message); }
            }
            _undo.Clear();
        }

        private void OnModel(object sender, PropertyChangedEventArgs e)
        {
            // The game's own toggle was chosen: our pick gives way.
            if (e.PropertyName == nameof(RespawnScreenViewModel.SelectedRespawnRequestType) && _vm.SelectedRespawnRequestType != RespawnRequestType.Default && _picked != null)
                Dispatcher.UIThread.Post(Guard(() =>
                {
                    if (_vm.SelectedRespawnRequestType == RespawnRequestType.Default || _confirmed != null) return;
                    SetPicked(_vm, null); Checks(); Hint(null);
                }));
            else if (e.PropertyName == nameof(RespawnScreenViewModel.RespawnPointsData)) Dispatcher.UIThread.Post(Guard(Rebuild));
        }

        public void CollapseList() => Collapse();

        private void Update()
        {
            try
            {
                if (_shown != this) { _timer.Stop(); return; }
                var menu = KeenRespawn.Menu;
                if (C.Updating != _updating) Hint(null);
                if (menu == null || ReferenceEquals(menu, _menu)) return;
                _menu = menu;
                if (menu.Death.Length > 0) _vm.DeathReason = menu.Death;
                _message.Text = menu.Message;
                _message.IsVisible = menu.Message.Length > 0;
                // The pick follows its place into the new menu (a medbay made home comes back as the home card).
                if (_picked != null && _confirmed == null)
                {
                    var now = Current(menu, _picked);
                    // The game shows its own home entry for the home card: its Home toggle stands for it.
                    if (now != null && (now.Home || now.Kind == SpawnKind.Home) && _vm.RespawnPointsData?.Home != null) { SetPicked(_vm, null); _vm.SelectedRespawnRequestType = RespawnRequestType.Home; }
                    else SetPicked(_vm, now);
                }
                Rebuild();
            }
            catch (Exception ex) { Fail("updating it", ex); }
        }

        private Border _fade, _fadeTop;

        private static Border Fade(bool top) => new()
        {
            Height = 28, VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom, IsHitTestVisible = false, IsVisible = false,
            BorderBrush = C.Brush(0x40F0A01E), BorderThickness = top ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 0, 1),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, top ? 1 : 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, top ? 0 : 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, 5, 10, 16), 0), new GradientStop(Color.FromArgb(0xD0, 5, 10, 16), 1) }
            }
        };

        private void Edge()
        {
            bool scrolls = _scroll.Extent.Height > _scroll.Viewport.Height + 1;
            _fade.IsVisible = scrolls && _scroll.Offset.Y + _scroll.Viewport.Height < _scroll.Extent.Height - 1;
            _fadeTop.IsVisible = scrolls && _scroll.Offset.Y > 1;
            // As wide as the cards (the grid less its outer half-gaps), so the edge lines up with the tiles' edges.
            if (_extras != null) _fade.Width = _fadeTop.Width = _extras.Width - _tileGap;
        }

        // The picture's top on the list's top, its left one tile gap right of the cards' right edge.
        private void AlignPanel()
        {
            if (_scroll.TranslatePoint(new Point(0, 0), _body) is not { } top || _extras == null) return;
            double left = (_body.Bounds.Width > 0 ? _body.Bounds.Width : 1920) / 2 + _extras.Width / 2 - _tileGap / 2 + _tileGap;
            if (Math.Abs(_panel.Margin.Top - top.Y) > 0.5 || Math.Abs(_panel.Margin.Left - left) > 0.5) _panel.Margin = new Thickness(left, top.Y, 0, 0);
            Edge();
        }

        // Fixed equal columns, chosen once from the width the list has (the enlarged picture's space kept free when the menu
        // has ships with pictures): a few cards line up on the left instead of floating.
        private CardGrid Extras(SpawnMenu menu)
        {
            if (_extras != null) return _extras;
            var tile = _gameClosest.Children.OfType<CheckBox>().FirstOrDefault();
            if (tile != null && double.IsFinite(tile.Width) && tile.Width > 0) _tileWidth = tile.Width;
            if (_points.Spacing > 0) _tileGap = _points.Spacing;
            // Two columns, each one game tile wide with the game's gap: the block's edges are the Home and Nearest tiles' edges.
            _extras = new CardGrid(2, _tileWidth + _tileGap, _tileGap, RowGap);
            // The scroll area is as wide as the grid (and its bar's gutter), so the bar sits beside the cards, not at the screen edge.
            _scroll.Width = _extras.Width + OnlineUi.ScrollGutter;
            _scroll.HorizontalAlignment = HorizontalAlignment.Center;
            _scroll.Content = _extras;
            AlignPanel();
            return _extras;
        }

        private void Rebuild()
        {
            var menu = _menu;
            if (menu == null || _shown != this) return;
            var data = _vm.RespawnPointsData;
            int home = HomeIndex(menu);
            int closest = data?.ClosestPoint is { } point ? ClosestIndex(menu, point) : -1;

            Details(_homeStrip, home >= 0 ? menu.Options[home] : null, home);
            Details(_closestStrip, closest >= 0 ? menu.Options[closest] : null, closest);

            // The home base stays first: the game's own entry when the game knows it, else ours (a destroyed home has no point).
            if (_ourHome != null) { _points.Children.Remove(_ourHome); _ourHome = null; _ourHomeBox = null; }
            if (home >= 0 && data?.Home == null)
            {
                _ourHomeBox = Entry(menu.Options[home], home, true);
                var strip = new StackPanel();
                Details(strip, menu.Options[home], home);
                _ourHome = new StackPanel { Children = { _ourHomeBox, strip } };
                _points.Children.Insert(0, _ourHome);
            }

            var extras = Extras(menu);
            _hover = _focus = null;
            ShowPanel();
            _destinationBoxes.Clear();
            _shipBoxes.Clear();
            if (!menu.Options.Any(o => o.Kind == SpawnKind.Ship && o.Group == _expanded)) _expanded = "";
            var items = new List<(Control, bool)>();
            Control openList = null;
            foreach (var (option, index, made) in SpawnGroups.Top(menu.Options, i => i != home && !(i == closest && data?.ClosestPoint != null)))
            {
                if (made == null) { items.Add((Entry(option, index, false), false)); continue; }
                var group = Groups().FirstOrDefault(g => g.Name == made.Name) ?? made;
                var ship = Entry(group.Card, -1, false, group);
                _shipBoxes[group.Name] = ship;
                items.Add((ship, false));
                if (group.Name == _expanded) items.Add((openList = Destinations(group, extras), true));
            }
            extras.Fill(items);
            _openList = openList;
            Checks();
            _extrasTitle.IsVisible = items.Count > 0 && (data?.Home != null || data?.ClosestPoint != null || _ourHome != null);
            Hint(null);
            // A rebuild drops the focused entry; a gamepad player keeps their place.
            if (_picked != null && Box(_picked) is { } picked) picked.Focus(Avalonia.Input.NavigationMethod.Directional);
            else if (_expanded.Length > 0 && _shipBoxes.TryGetValue(_expanded, out var open)) open.Focus(Avalonia.Input.NavigationMethod.Directional);
        }

        // Our status, distance and planet under a game entry, with its home action.
        private void Details(Panel strip, SpawnOption option, int index)
        {
            strip.Children.Clear();
            if (option == null) return;
            var row = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 400, Margin = new Thickness(0, 4, 0, 0) };
            var parts = new List<Control> { C.Badge(option.Status, option.Available), Muted(Where(option)) };
            if (option.Home || option.Kind == SpawnKind.Home || option.CanSetHome) parts.Add(C.HomeAction(option, () => ToggleHome(option, index)));
            foreach (var part in parts) { part.Margin = new Thickness(5, 0, 5, 4); part.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(part); }
            strip.Children.Add(row);
        }

        private static string Where(SpawnOption o)
        {
            string distance = SpawnCards.DistanceText(o.Distance);
            string place = string.IsNullOrEmpty(o.Place) ? "Space" : o.Place;
            return distance.Length > 0 ? place + "   " + distance : place;
        }

        // SE2's TextBlock theme cuts text with an ellipsis; ours wraps and is never cut.
        private static TextBlock Whole(TextBlock text) { text.TextWrapping = TextWrapping.Wrap; text.TextTrimming = TextTrimming.None; return text; }

        private const double CardPad = 10;

        private static Control Right(Control control) { control.HorizontalAlignment = HorizontalAlignment.Right; return control; }

        private static TextBlock Muted(string text) { var t = Whole(OnlineUi.Text(text, null, 17)); t.Opacity = 0.75; return t; }

        // One of our entries, styled as the game's: a toggle card (the full-size one for a home base the game does not know).
        private CheckBox Entry(SpawnOption option, int index, bool big, ShipGroup group = null)
        {
            var box = new CheckBox { Name = group != null ? "OnlineShip" + group.Name : "OnlineSpawn" + index, IsChecked = group == null && _picked != null && Same(option, _picked) };
            box.Classes.Add("NoCheckmark");
            ToolTip.SetTip(box, C.FullText(option));
            Control content;
            if (big)
            {
                box.Classes.Add("RespawnPoint");
                box.Height = double.NaN; box.MinHeight = GameEntryHeight;
                var grid = new Grid { RowDefinitions = new RowDefinitions("80,Auto"), Margin = new Thickness(0, 0, 0, 12) };
                var label = OnlineUi.Text("HOME BASE", "SubTitle", 24);
                label.HorizontalAlignment = HorizontalAlignment.Center;
                label.Bind(TextBlock.ForegroundProperty, new VRageDynamicResource("Primary"));
                grid.Children.Add(label);
                var hex = C.Hex(C.IconKind.Home, option.Available, 48);
                hex.HorizontalAlignment = HorizontalAlignment.Left; hex.Margin = new Thickness(12, 0, 0, 0);
                grid.Children.Add(hex);
                var facts = new StackPanel { Spacing = 4, Margin = new Thickness(16, 0), Opacity = option.Available ? 1 : 0.55 };
                facts.Children.Add(Whole(OnlineUi.Text(option.Name, "Bright", 22)));
                if (option.Grid.Length > 0) facts.Children.Add(Muted(option.Grid));
                if (option.Detail.Length > 0) facts.Children.Add(Muted(option.Detail));
                Grid.SetRow(facts, 1); grid.Children.Add(facts);
                content = grid;
            }
            else
            {
                // Names and descriptions wrap, never cut; the card grid gives every card the same width and height.
                box.MinHeight = 100; box.VerticalContentAlignment = VerticalAlignment.Top; box.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                var stack = new Grid { Margin = new Thickness(CardPad), RowDefinitions = new RowDefinitions("Auto,Auto") };
                // The text column ends before the badge column, so the badge never sits on text.
                // Row 0: name and place beside the badge (and home button); row 1: grid name and description under both, full width.
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto) { MinHeight = HeadHeight });
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var hex = C.Hex(C.IconOf(option), option.Available, 48);
                hex.Opacity = option.Available ? 1 : 0.55; hex.VerticalAlignment = VerticalAlignment.Top; hex.Margin = new Thickness(0, 0, 8, 0);
                Grid.SetRowSpan(hex, 2); grid.Children.Add(hex);
                if (C.IsShip(option)) { var banner = C.Banner(option.Preview, (_tileWidth - 2 * CardPad) * 4.5 / 16); banner.Margin = new Thickness(0, 0, 0, 8); stack.Children.Add(banner); }
                var facts = new StackPanel { Spacing = 2, Opacity = option.Available ? 1 : 0.55 };
                facts.Children.Add(Whole(OnlineUi.Text(option.Name, "Bright", 20)));
                var where = new WrapPanel { Orientation = Orientation.Horizontal };
                if (option.Tag.Length > 0) { var tag = C.Tag(option.Tag.ToUpperInvariant(), L.Amber); tag.Margin = new Thickness(0, 2, 8, 2); where.Children.Add(tag); }
                if (group == null) where.Children.Add(Muted(Where(option)));
                facts.Children.Add(where);
                Grid.SetColumn(facts, 1); grid.Children.Add(facts);
                var more = new StackPanel { Spacing = 2, Opacity = option.Available ? 1 : 0.55, Margin = new Thickness(0, 2, 0, 0) };
                if (option.Grid.Length > 0) more.Children.Add(Muted(option.Grid));
                if (option.Detail.Length > 0) more.Children.Add(Muted(option.Detail));
                if (group != null)
                {
                    var count = Whole(OnlineUi.Text(group.Destinations.Count == 1 ? "1 destination" : group.Destinations.Count + " destinations: pick the ship, then where", null, 17, true));
                    count.Foreground = C.Brush(L.Amber);
                    more.Children.Add(count);
                }
                Grid.SetColumn(more, 1); Grid.SetColumnSpan(more, 2); Grid.SetRow(more, 1); grid.Children.Add(more);
                var side = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 0, 0) };
                side.Children.Add(Right(C.Badge(option.Status, option.Available)));
                if (option.CanSetHome)
                {
                    var home = Right(C.HomeAction(option, () => ToggleHome(option, index)));
                    home.Height = 26;
                    side.Children.Add(home);
                }
                Grid.SetColumn(side, 2); grid.Children.Add(side);
                Grid.SetRow(grid, 1); stack.Children.Add(grid);
                content = stack;
                if (C.IsShip(option))
                {
                    box.PointerEntered += (_, _) => { _hover = option; ShowPanel(); };
                    box.PointerExited += (_, _) => { if (ReferenceEquals(_hover, option)) { _hover = null; ShowPanel(); } };
                    box.GotFocus += (_, _) => { _focus = option; ShowPanel(); };
                    box.LostFocus += (_, _) => { if (ReferenceEquals(_focus, option)) { _focus = null; ShowPanel(); } };
                }
            }
            box.Content = content;
            if (group != null)
            {
                box.Click += (_, _) =>
                {
                    try { ToggleShip(group); }
                    catch (Exception ex) { Fail("opening a ship", ex); }
                };
                return box;
            }
            box.Click += (_, _) =>
            {
                try { Pick(_picked != null && Same(option, _picked) ? null : option); }
                catch (Exception ex) { Fail("picking an entry", ex); }
            };
            box.DoubleTapped += (_, _) =>
            {
                try { Pick(option); if (option.Available) _vm.ConfirmRespawn(); }
                catch (Exception ex) { Fail("confirming an entry", ex); }
            };
            return box;
        }

        private void ShowPanel() => _panel.Show(_hover ?? _focus ?? Current(_menu, _picked) ?? OpenShip()?.Card);

        // This menu's ships, made once per menu so their cards (and the panel's picture) stay the same objects.
        private SpawnMenu _groupsFor;
        private List<ShipGroup> _groups = new();

        private List<ShipGroup> Groups()
        {
            if (!ReferenceEquals(_groupsFor, _menu)) { _groupsFor = _menu; _groups = _menu == null ? new() : SpawnGroups.Top(_menu.Options).Where(t => t.Group != null).Select(t => t.Group).ToList(); }
            return _groups;
        }

        private ShipGroup OpenShip() => _expanded.Length == 0 ? null : Groups().FirstOrDefault(g => g.Name == _expanded);

        private const double DestinationWidth = 250, DestinationGap = 8, ListPad = 8;
        private Control _openList;

        // The open ship's destinations, on a line of their own under it: picture of the planet or moon, its type, gravity,
        // atmosphere, the plain-words info, distance and status, in equal columns like the cards. Choosing one picks that
        // flat menu card.
        private Control Destinations(ShipGroup group, CardGrid extras)
        {
            double inner = extras.Width - _tileGap - 2 * ListPad;
            var list = new CardGrid(CardGrid.ColumnsFor(inner, DestinationWidth + DestinationGap, 6), inner / CardGrid.ColumnsFor(inner, DestinationWidth + DestinationGap, 6), DestinationGap);
            var boxes = new List<(Control, bool)>();
            foreach (var (option, index) in group.Destinations) boxes.Add((Destination(option, index, inner / list.Columns - DestinationGap), false));
            list.Fill(boxes);
            var title = C.Wrap("Where should the " + group.Name + " go?", 17, L.Amber, FontWeight.Bold);
            title.Margin = new Thickness(DestinationGap / 2, 0, DestinationGap / 2, 6);
            return new Border
            {
                Padding = new Thickness(ListPad),
                Background = C.Brush(0x99050A10), BorderBrush = C.Brush(0x66F0A01E), BorderThickness = new Thickness(1, 0, 1, 1), CornerRadius = new CornerRadius(0, 0, 4, 4),
                Child = new StackPanel { Children = { title, list } }
            };
        }

        private CheckBox Destination(SpawnOption option, int index, double width)
        {
            var box = new CheckBox { Name = "OnlineSpawn" + index, MinHeight = 100, IsChecked = _picked != null && Same(option, _picked) };
            box.Classes.Add("NoCheckmark");
            box.VerticalContentAlignment = VerticalAlignment.Top; box.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            ToolTip.SetTip(box, string.Join("\n", new[] { option.Name }.Concat(SpawnGroups.Facts(option)).Append(option.Info).Append(option.Detail).Where(t => !string.IsNullOrEmpty(t))));
            var stack = new StackPanel { Margin = new Thickness(CardPad), Spacing = 6 };
            stack.Children.Add(C.BodyBanner(option.Body, (width - 2 * CardPad) * 7 / 16));
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var name = Whole(OnlineUi.Text(option.Name, "Bright", 19)); name.Opacity = option.Available ? 1 : 0.55;
            head.Children.Add(name);
            var badge = C.Badge(option.Status, option.Available); badge.VerticalAlignment = VerticalAlignment.Top; badge.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(badge, 1); head.Children.Add(badge);
            stack.Children.Add(head);
            var facts = new StackPanel { Spacing = 4, Opacity = option.Available ? 1 : 0.55 };
            facts.Children.Add(C.DestinationFacts(option, 16, L.Muted));
            if (option.Info.Length > 0) facts.Children.Add(Muted(option.Info));
            if (option.Detail.Length > 0) { var why = Muted(option.Detail); if (!option.Available) why.Foreground = C.Brush(C.StatusColor(option.Status)); facts.Children.Add(why); }
            stack.Children.Add(facts);
            box.Content = stack;
            box.PointerEntered += (_, _) => { _hover = option; ShowPanel(); };
            box.PointerExited += (_, _) => { if (ReferenceEquals(_hover, option)) { _hover = null; ShowPanel(); } };
            box.GotFocus += (_, _) => { _focus = option; ShowPanel(); };
            box.LostFocus += (_, _) => { if (ReferenceEquals(_focus, option)) { _focus = null; ShowPanel(); } };
            box.Click += (_, _) =>
            {
                try { Pick(_picked != null && Same(option, _picked) ? null : option); }
                catch (Exception ex) { Fail("picking a destination", ex); }
            };
            box.DoubleTapped += (_, _) =>
            {
                try { Pick(option); if (option.Available) _vm.ConfirmRespawn(); }
                catch (Exception ex) { Fail("confirming a destination", ex); }
            };
            _destinationBoxes.Add(box);
            return box;
        }

        // Picking a ship opens its destinations (and only picks one when it has exactly one); picking it again closes them.
        private void ToggleShip(ShipGroup group)
        {
            if (_confirmed != null) { Checks(); return; }
            if (_expanded == group.Name) { Collapse(); return; }
            _expanded = group.Name;
            _vm.SelectedRespawnRequestType = RespawnRequestType.Default;
            SetPicked(_vm, group.Destinations.Count == 1 ? group.Destinations[0].Option : null);
            Rebuild();
            if (_openList is { } open) Dispatcher.UIThread.Post(() => open.BringIntoView());
        }

        // Keyboard Esc reaches the screen twice: as the UI's key and as the game's MenuScreen action (MenuKeyPrefix). Whichever
        // comes first closes the list; the other, within a moment, is swallowed instead of opening the pause menu.
        private long _closedMs;
        public bool RecentlyClosed => MpClock.NowMs() - System.Threading.Interlocked.Read(ref _closedMs) < 400;

        /// <summary>Any thread: whether an Esc now closes an open ship list (the close itself runs on the UI thread).</summary>
        public bool CloseOnEscape()
        {
            if (_expanded.Length == 0) return false;
            System.Threading.Interlocked.Exchange(ref _closedMs, MpClock.NowMs());
            return true;
        }

        private void Collapse()
        {
            if (_expanded.Length == 0) return;
            string closing = _expanded;
            _expanded = "";
            if (_picked != null && _picked.Group == closing && _picked.Kind == SpawnKind.Ship) SetPicked(_vm, null);
            Rebuild();
            if (_shipBoxes.TryGetValue(closing, out var ship)) ship.Focus(Avalonia.Input.NavigationMethod.Directional);
        }

        private void Pick(SpawnOption option)
        {
            // Answered already: a double click's second release must not change what was confirmed.
            if (_confirmed != null) { Checks(); return; }
            if (option != null) _vm.SelectedRespawnRequestType = RespawnRequestType.Default;
            SetPicked(_vm, option);
            Checks();
            ShowPanel();
            Hint(null);
        }

        private IEnumerable<CheckBox> Boxes() => (_extras?.Children.OfType<CheckBox>() ?? Enumerable.Empty<CheckBox>()).Concat(_destinationBoxes).Append(_ourHomeBox).Where(b => b != null && !b.Name.StartsWith("OnlineShip", StringComparison.Ordinal));

        private CheckBox Box(SpawnOption option)
        {
            string name = "OnlineSpawn" + Array.FindIndex(_menu.Options, o => Same(o, option));
            return Boxes().FirstOrDefault(b => b.Name == name);
        }

        private void Checks()
        {
            var picked = _picked == null ? null : Box(_picked);
            foreach (var box in Boxes()) box.IsChecked = ReferenceEquals(box, picked);
            foreach (var (name, ship) in _shipBoxes) ship.IsChecked = name == _expanded || _picked?.Group == name && _picked.Kind == SpawnKind.Ship;
        }

        // The server answers with a new menu (the home card first); Update shows it.
        private void ToggleHome(SpawnOption option, int index)
        {
            C.SendHome(_menu, option, index);
            Hint(null);
        }

        public void Refuse(SpawnOption option) => Dispatcher.UIThread.Post(Guard(() => Hint(option ?? (C.Updating || OpenShip() != null ? null : new SpawnOption(SpawnKind.Suit, "", "Choose a spawn point first.", false, Status: SpawnStatus.Unavailable)))));

        private void Hint(SpawnOption refused)
        {
            _updating = C.Updating;
            if (_updating) { _hint.Foreground = C.Brush(L.Amber); _hint.Text = "Updating..."; return; }
            var o = refused ?? Current(_menu, _picked);
            if (o == null && OpenShip() is { } ship) { _hint.Foreground = C.Brush(L.Amber); _hint.Text = "Choose where the " + ship.Name + " goes: Down to move into its list, Esc or B to close it"; return; }
            if (o == null) { _hint.Text = ""; return; }
            if (o.Available && refused == null) { _hint.Foreground = C.Brush(L.Muted); _hint.Text = SpawnGroups.Label(o) + (o.Grid.Length > 0 ? " on " + o.Grid : ""); return; }
            var status = o.Status == SpawnStatus.Available ? SpawnStatus.Unavailable : o.Status;
            _hint.Foreground = C.Brush(C.StatusColor(status));
            _hint.Text = (o.Name.Length > 0 ? SpawnGroups.Label(o) + ": " : "") + (o.Detail.Length > 0 ? o.Detail : SpawnCards.StatusText(status));
        }
    }
}
