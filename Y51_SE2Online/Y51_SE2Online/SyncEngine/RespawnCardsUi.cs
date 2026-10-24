using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Y51_SE2Online.Core;
using Y51_SE2Online.KeenNetcode;
using Y51_SE2Online.Sync;
using L = Y51_SE2Online.Sync.LoadScreenLayout;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// SE2 Online's spawn look in the loading screen's colours (amber accent, hex icons, status badges): the pieces added to
/// the game's respawn screen (<see cref="InGameRespawn"/>), and the 1920x1080 card column of the fallback overlay.
/// </summary>
internal static class RespawnCardsUi
{
    public const double Width = 1920, Height = 1080, Column = 1240;
    public const string Rows = "48,48,84,12,Auto,Auto,24,*,24,Auto,40";
    public const double CardHeight = 100, CardGap = 6, BannerHeight = 110;
    /// <summary>The overlay card's top line: icon, title, place, badge and map all start this far below the card's top.</summary>
    public const double CardTop = 18;
    /// <summary>The fallback's ship picture beside the card column (the column moves left to make room).</summary>
    public const double PanelWidth = 520, SideMargin = 60, PanelGap = 40;
    public const string CardColumns = "92,*,230,170,100";
    public const double HexSize = 64, RadarSize = 84;

    public const uint Green = 0xFF4CC38A, Red = 0xFFFF5A4D;
    public const uint CardBack = 0xD90C1118, CardEdge = 0x2EFFFFFF, CardSelected = 0xF0141B25, HomeBack = 0xE0161209;
    public const uint ShadeTop = 0xF0050810, ShadeMiddle = 0xBE050810, ShadeBottom = 0xF5050810;

    public static IBrush Brush(uint argb) => new SolidColorBrush(Color.FromUInt32(argb));

    public static TextBlock Text(string value, double size, uint color, FontWeight weight = FontWeight.Normal)
        => new() { Text = value, FontSize = size, Foreground = Brush(color), FontWeight = weight, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Text that wraps instead of being cut: names and descriptions are never truncated.</summary>
    public static TextBlock Wrap(string value, double size, uint color, FontWeight weight = FontWeight.Normal)
        => new() { Text = value, FontSize = size, Foreground = Brush(color), FontWeight = weight, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Everything a card says, for its tooltip.</summary>
    public static string FullText(SpawnOption o)
    {
        var lines = new System.Collections.Generic.List<string> { o.Name };
        if (!string.IsNullOrEmpty(o.Grid)) lines.Add(o.Grid);
        if (!string.IsNullOrEmpty(o.Detail)) lines.Add(o.Detail);
        string where = (string.IsNullOrEmpty(o.Place) ? "Space" : o.Place) + (string.IsNullOrEmpty(o.Tag) ? "" : "  (" + o.Tag + ")");
        string distance = SpawnCards.DistanceText(o.Distance);
        lines.Add(distance.Length > 0 ? where + ", " + distance + " from where you died" : where);
        return string.Join("\n", lines);
    }

    public static bool IsShip(SpawnOption o) => o.Kind == SpawnKind.Ship && !string.IsNullOrEmpty(o.Preview);

    /// <summary>A ship's destination (a card of its sub-list), as opposed to the ship's own top-level card.</summary>
    public static bool IsDestination(SpawnOption o) => o.Kind == SpawnKind.Ship && o.Destination != SpawnDestination.None;

    /// <summary>A ship picture cover-cropped to a strip; it hides itself (and calls <paramref name="missing"/>) when the ship has none.</summary>
    public static Border Banner(string preview, double height, Action missing = null) => Picture(ready => ShipPictures.Get(preview, ready), height, missing);

    /// <summary>A destination's planet or moon picture, as <see cref="Banner"/>: no picture (a plain card) when it has none.</summary>
    public static Border BodyBanner(string body, double height, Action missing = null) => Picture(ready => ShipPictures.GetBody(body, ready), height, missing);

    private static Border Picture(Action<Action<WriteableBitmap>> load, double height, Action missing)
    {
        var image = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var banner = new Border { Height = height, ClipToBounds = true, CornerRadius = new CornerRadius(3), Background = Brush(0xFF0A0F14), Child = image, IsHitTestVisible = false };
        load(bitmap =>
        {
            if (bitmap != null) { image.Source = bitmap; return; }
            banner.IsVisible = false;
            missing?.Invoke();
        });
        return banner;
    }

    /// <summary>A destination's facts as wrapping chips: its type as an amber pill, then gravity, atmosphere and distance.</summary>
    public static WrapPanel DestinationFacts(SpawnOption o, double size, uint color)
    {
        var facts = new WrapPanel { Orientation = Orientation.Horizontal };
        bool first = true;
        foreach (string fact in SpawnGroups.Facts(o))
        {
            Control chip = first ? Tag(fact.ToUpperInvariant(), L.Amber) : Wrap(fact, size, color);
            chip.Margin = new Thickness(0, 2, 14, 2);
            facts.Children.Add(chip);
            first = false;
        }
        return facts;
    }

    /// <summary>The enlarged picture of the ship card under the pointer or focus, or the picked one: the full 16:9 picture,
    /// name, tag, planet or space, and the whole description.</summary>
    internal sealed class ShipPanel : Border
    {
        private readonly Image _picture = new() { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly Border _frame;
        private readonly StackPanel _text = new() { Spacing = 8, Margin = new Thickness(18, 14, 18, 18) };
        private SpawnOption _shown;

        public ShipPanel(double width)
        {
            Width = width; IsHitTestVisible = false; IsVisible = false;
            Background = Brush(0xF00A0F14); BorderBrush = Brush(0xCCF0A01E); BorderThickness = new Thickness(2); CornerRadius = new CornerRadius(4);
            _frame = new Border { Height = (width - 4) * 9 / 16, ClipToBounds = true, CornerRadius = new CornerRadius(3, 3, 0, 0), Child = _picture };
            Child = new StackPanel { Children = { _frame, _text } };
        }

        /// <summary>A ship card shows its ship picture; a destination its planet or moon and all its facts; anything else hides it.</summary>
        public void Show(SpawnOption option)
        {
            bool destination = option != null && IsDestination(option);
            if (option == null || !(destination || IsShip(option))) { IsVisible = false; _shown = null; return; }
            IsVisible = true;
            if (ReferenceEquals(option, _shown)) return;
            _shown = option;
            _picture.Source = null;
            _frame.IsVisible = true;
            void Ready(WriteableBitmap bitmap) { if (!ReferenceEquals(_shown, option)) return; _picture.Source = bitmap; _frame.IsVisible = bitmap != null; }
            if (destination) ShipPictures.GetBody(option.Body, Ready);
            else ShipPictures.Get(option.Preview, Ready);
            _text.Children.Clear();
            _text.Children.Add(Wrap(option.Name, 26, L.White, FontWeight.Bold));
            if (destination)
            {
                _text.Children.Add(DestinationFacts(option, 19, L.Muted));
                if (!string.IsNullOrEmpty(option.Info)) _text.Children.Add(Wrap(option.Info, 18, L.Muted));
                var state = new WrapPanel { Orientation = Orientation.Horizontal };
                var mark = Badge(option.Status, option.Available); mark.HorizontalAlignment = HorizontalAlignment.Left; mark.Margin = new Thickness(0, 2, 10, 2);
                state.Children.Add(mark);
                if (!string.IsNullOrEmpty(option.Group)) { var ship = Wrap(option.Group, 18, L.Muted); ship.Margin = new Thickness(0, 2, 10, 2); state.Children.Add(ship); }
                _text.Children.Add(state);
                if (!string.IsNullOrEmpty(option.Detail)) _text.Children.Add(Wrap(option.Detail, 18, option.Available ? L.Muted : StatusColor(option.Status)));
                return;
            }
            var where = new WrapPanel { Orientation = Orientation.Horizontal };
            if (!string.IsNullOrEmpty(option.Tag)) { var tag = Tag(option.Tag.ToUpperInvariant(), L.Amber); tag.Margin = new Thickness(0, 0, 10, 4); where.Children.Add(tag); }
            var place = Wrap(string.IsNullOrEmpty(option.Place) ? "Space" : option.Place, 19, L.Muted); place.Margin = new Thickness(0, 0, 10, 4);
            where.Children.Add(place);
            var badge = Badge(option.Status, option.Available); badge.HorizontalAlignment = HorizontalAlignment.Left; badge.Margin = new Thickness(0, 0, 0, 4);
            where.Children.Add(badge);
            _text.Children.Add(where);
            if (!string.IsNullOrEmpty(option.Detail)) _text.Children.Add(Wrap(option.Detail, 18, option.Available ? L.Muted : StatusColor(option.Status)));
        }
    }

    public static uint StatusColor(SpawnStatus status) => status switch
    {
        SpawnStatus.Available => Green,
        SpawnStatus.Cooldown or SpawnStatus.Unpowered or SpawnStatus.TurnedOff => L.Amber,
        _ => Red
    };

    public static Control Wordmark(double size)
    {
        var wordmark = Text("", size, L.White, FontWeight.Bold);
        wordmark.Inlines!.Add(new Run("SE2") { Foreground = Brush(L.Amber) });
        wordmark.Inlines.Add(new Run(" ONLINE"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        if (Logo() is { } logo) row.Children.Add(new Image { Source = logo, Width = 44, Height = 44, Stretch = Stretch.Uniform });
        row.Children.Add(wordmark);
        return row;
    }

    public static IBrush Shade() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromUInt32(ShadeTop), 0), new GradientStop(Color.FromUInt32(ShadeMiddle), 0.45), new GradientStop(Color.FromUInt32(ShadeBottom), 1) }
    };

    // SE2's theme gives list rows and buttons its own backgrounds, padding and focus frames; the cards draw all of that themselves.
    public static void Restyle(Styles styles)
    {
        styles.Add(new Style(s => s.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<ListBoxItem>((item, _) => new ContentPresenter
                {
                    Name = "CardPresenter", [!ContentPresenter.ContentProperty] = item[!ContentControl.ContentProperty],
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent
                })),
                new Setter(ListBoxItem.PaddingProperty, new Thickness(0)),
                new Setter(ListBoxItem.MarginProperty, new Thickness(0, 0, 0, CardGap)),
                new Setter(ListBoxItem.FocusAdornerProperty, null),
            }
        });
        styles.Add(new Style(s => s.OfType<Button>().Class("Respawn"))
        {
            Setters =
            {
                new Setter(TemplatedControl.TemplateProperty, Flat()),
                new Setter(Button.BackgroundProperty, Brush(L.Amber)), new Setter(Button.ForegroundProperty, Brush(0xFF0A0D12)),
                new Setter(Button.BorderBrushProperty, Brush(L.Amber)), new Setter(Button.FocusAdornerProperty, null),
            }
        });
        styles.Add(new Style(s => s.OfType<Button>().Class("Respawn").Class(":pointerover")) { Setters = { new Setter(Button.BackgroundProperty, Brush(0xFFFFB846)) } });
        styles.Add(new Style(s => s.OfType<Button>().Class("Respawn").Class(":focus")) { Setters = { new Setter(Button.BorderBrushProperty, Brush(L.White)) } });
        styles.Add(new Style(s => s.OfType<Button>().Class("Respawn").Class(":disabled")) { Setters = { new Setter(Button.BackgroundProperty, Brush(0x33FFFFFF)), new Setter(Button.BorderBrushProperty, Brush(0x33FFFFFF)), new Setter(Button.ForegroundProperty, Brush(L.Dim)) } });
        ActionStyles(styles);
    }

    /// <summary>The small amber "Set as home" / "Clear home" buttons.</summary>
    public static void ActionStyles(Styles styles)
    {
        styles.Add(new Style(s => s.OfType<Button>().Class("CardAction"))
        {
            Setters =
            {
                new Setter(TemplatedControl.TemplateProperty, Flat()),
                new Setter(Button.BackgroundProperty, Brush(0x26F0A01E)), new Setter(Button.ForegroundProperty, Brush(L.Amber)),
                new Setter(Button.BorderBrushProperty, Brush(0x99F0A01E)), new Setter(Button.FocusAdornerProperty, null),
            }
        });
        styles.Add(new Style(s => s.OfType<Button>().Class("CardAction").Class(":pointerover")) { Setters = { new Setter(Button.BackgroundProperty, Brush(0x55F0A01E)) } });
        styles.Add(new Style(s => s.OfType<Button>().Class("CardAction").Class(":focus")) { Setters = { new Setter(Button.BorderBrushProperty, Brush(L.White)) } });
    }

    private static FuncControlTemplate<Button> Flat() => new((button, _) => new Border
    {
        [!Border.BackgroundProperty] = button[!Button.BackgroundProperty],
        [!Border.BorderBrushProperty] = button[!Button.BorderBrushProperty],
        BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(3),
        Child = new ContentPresenter
        {
            [!ContentPresenter.ContentProperty] = button[!ContentControl.ContentProperty],
            [!ContentPresenter.ForegroundProperty] = button[!Button.ForegroundProperty],
            Padding = new Thickness(18, 4), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
        }
    });

    /// <summary>One spawn card. Index is its place in the server's menu, which is what the choice sends back.</summary>
    internal sealed class Card : Border
    {
        public readonly SpawnOption Option;
        public readonly int Index;
        public readonly Button HomeButton;
        private bool _selected;

        /// <summary>A ship's own card (Index -1): picking it opens its destinations.</summary>
        public readonly ShipGroup Group;
        /// <summary>A destination card: shown indented under its ship.</summary>
        public bool IsDestinationCard => IsDestination(Option) && Index >= 0;

        public Card(SpawnOption option, int index, Action toggleHome, ShipGroup group = null)
        {
            Option = option; Index = index; Group = group;
            bool destination = IsDestination(option);
            if (destination) Margin = new Thickness(56, 0, 0, 0);
            // Grows with its text: names and descriptions wrap, never cut; the badge has its own column.
            MinHeight = CardHeight; CornerRadius = new CornerRadius(4); Padding = new Thickness(0, 0, 14, 0);
            ToolTip.SetTip(this, FullText(option));
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(CardColumns), RowDefinitions = new RowDefinitions("Auto,*") };
            double dim = option.Available ? 1 : 0.5;

            if (destination || IsShip(option))
            {
                var banner = destination ? BodyBanner(option.Body, BannerHeight) : Banner(option.Preview, BannerHeight);
                banner.Margin = new Thickness(6, 6, -8, 0);
                Grid.SetColumnSpan(banner, 5); grid.Children.Add(banner);
            }

            var icon = Hex(IconOf(option), option.Available);
            icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new Thickness(0, CardTop, 0, CardTop);
            icon.Opacity = dim;
            Grid.SetRow(icon, 1); grid.Children.Add(icon);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Spacing = 2, Opacity = dim, Margin = new Thickness(0, CardTop - 4, 0, CardTop) };
            var title = new WrapPanel { Orientation = Orientation.Horizontal };
            var name = Wrap(option.Name, 26, L.White, FontWeight.Bold); name.Margin = new Thickness(0, 0, 10, 0);
            title.Children.Add(name);
            if (option.Home) title.Children.Add(Tag("HOME BASE", L.Amber));
            if (!string.IsNullOrEmpty(option.Tag)) title.Children.Add(Tag(option.Tag.ToUpperInvariant(), L.Amber));
            info.Children.Add(title);
            if (!string.IsNullOrEmpty(option.Grid)) info.Children.Add(Wrap(option.Grid, 19, L.Muted));
            if (destination)
            {
                info.Children.Add(DestinationFacts(option, 18, L.Muted));
                if (!string.IsNullOrEmpty(option.Info)) info.Children.Add(Wrap(option.Info, 17, L.Dim));
            }
            if (!string.IsNullOrEmpty(option.Detail)) info.Children.Add(Wrap(option.Detail, 17, option.Available ? L.Dim : StatusColor(option.Status)));
            if (group != null)
                info.Children.Add(Wrap(group.Destinations.Count == 1 ? "1 destination" : group.Destinations.Count + " destinations: pick one below", 17, L.Amber));
            Grid.SetColumn(info, 1); Grid.SetRow(info, 1); grid.Children.Add(info);

            var where = new StackPanel { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 2, Margin = new Thickness(10, CardTop - 4, 20, CardTop), Opacity = dim };
            string distance = SpawnCards.DistanceText(option.Distance);
            if (distance.Length > 0) where.Children.Add(Right(Wrap(distance, 26, L.White, FontWeight.Bold)));
            if (group == null && !destination) where.Children.Add(Right(Wrap(string.IsNullOrEmpty(option.Place) ? "Space" : option.Place, 18, L.Muted)));
            if (distance.Length > 0) where.Children.Add(Right(Wrap("from where you died", 14, L.Dim)));
            Grid.SetColumn(where, 2); Grid.SetRow(where, 1); grid.Children.Add(where);

            var state = new StackPanel { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, CardTop, 12, CardTop) };
            var badge = Badge(option.Status, option.Available); badge.HorizontalAlignment = HorizontalAlignment.Right;
            state.Children.Add(badge);
            HomeButton = HomeAction(option, toggleHome);
            HomeButton.IsVisible = false; HomeButton.HorizontalAlignment = HorizontalAlignment.Right;
            state.Children.Add(HomeButton);
            Grid.SetColumn(state, 3); Grid.SetRow(state, 1); grid.Children.Add(state);

            var radar = Radar(option);
            radar.VerticalAlignment = VerticalAlignment.Top; radar.Margin = new Thickness(0, CardTop - 6, 0, CardTop);
            radar.Opacity = dim;
            Grid.SetColumn(radar, 4); Grid.SetRow(radar, 1); grid.Children.Add(radar);

            if (option.Home)
            {
                var bar = new Border { Width = 5, HorizontalAlignment = HorizontalAlignment.Left, Background = Brush(L.Amber), CornerRadius = new CornerRadius(4, 0, 0, 4) };
                Grid.SetRowSpan(bar, 2); grid.Children.Add(bar);
            }
            Child = grid;
            Selected = false;
        }

        public bool CanToggleHome => Option.Home || Option.Kind == SpawnKind.Home || Option.CanSetHome;

        public bool Selected
        {
            get => _selected;
            set
            {
                _selected = value;
                Background = Brush(value ? CardSelected : Option.Home ? HomeBack : CardBack);
                BorderBrush = Brush(value ? L.Amber : Option.Home ? 0x66F0A01E : CardEdge);
                BorderThickness = new Thickness(value ? 2 : 1);
                HomeButton.IsVisible = value && CanToggleHome;
            }
        }

        private static Control Right(TextBlock text) { text.HorizontalAlignment = HorizontalAlignment.Right; text.TextAlignment = TextAlignment.Right; return text; }
    }

    private static SpawnMenu _homeSentFor;
    private static long _homeSentMs;

    /// <summary>A home base change waits for the server's new menu (it reorders the cards): until it comes, or for 5 s,
    /// nothing is chosen and no other home change is sent, so a stale card index never reaches the server.</summary>
    public static bool Updating => _homeSentFor != null && ReferenceEquals(KeenRespawn.Menu, _homeSentFor) && MpClock.NowMs() - _homeSentMs < 5000;

    /// <summary>Sets (CanSetHome) or clears (the home card) the home base for card <paramref name="index"/> of <paramref name="menu"/>.</summary>
    public static void SendHome(SpawnMenu menu, SpawnOption option, int index)
    {
        if (menu == null || !ReferenceEquals(menu, KeenRespawn.Menu) || Updating) return;
        if (option.Home || option.Kind == SpawnKind.Home) AdminAccess.Send(new AdminRequest(AdminAction.SetHome, Value: false));
        else if (option.CanSetHome) AdminAccess.Send(new AdminRequest(AdminAction.SetHome, Minutes: index, Value: true));
        else return;
        _homeSentFor = menu;
        _homeSentMs = MpClock.NowMs();
    }

    public static Button HomeAction(SpawnOption option, Action toggleHome)
    {
        bool home = option.Home || option.Kind == SpawnKind.Home;
        var button = new Button { Content = home ? "Clear home" : "Set as home", FontSize = 15, Height = 30, HorizontalAlignment = HorizontalAlignment.Center, Name = home ? "SpawnClearHome" : "SpawnSetHome" };
        button.Classes.Add("CardAction");
        button.Click += (_, e) => { e.Handled = true; try { toggleHome(); } catch (Exception ex) { MpMenu.Report(ex.Message); } };
        return button;
    }

    public static Control Tag(string text, uint color) => new Border
    {
        Background = Brush(0x26000000 | (color & 0xFFFFFF)), BorderBrush = Brush(0x99000000 | (color & 0xFFFFFF)), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3), Padding = new Thickness(8, 1), VerticalAlignment = VerticalAlignment.Center,
        Child = Text(text, 13, color, FontWeight.Bold)
    };

    public static Control Badge(SpawnStatus status, bool available)
    {
        uint color = available ? Green : StatusColor(status == SpawnStatus.Available ? SpawnStatus.Unavailable : status);
        var badge = (Border)Tag(SpawnCards.StatusText(available ? SpawnStatus.Available : status == SpawnStatus.Available ? SpawnStatus.Unavailable : status).ToUpperInvariant(), color);
        badge.Padding = new Thickness(12, 3); badge.HorizontalAlignment = HorizontalAlignment.Center;
        ((TextBlock)badge.Child!).FontSize = 15;
        return badge;
    }

    public enum IconKind { Home, Medbay, Faction, Shared, Suit, Ship, Lander, Host }

    public static IconKind IconOf(SpawnOption o) => o.Home ? IconKind.Home : o.Kind switch
    {
        SpawnKind.Home => IconKind.Home,
        SpawnKind.Medbay => IconKind.Medbay,
        SpawnKind.FactionMedbay => IconKind.Faction,
        SpawnKind.SharedMedbay => IconKind.Shared,
        SpawnKind.Suit => IconKind.Suit,
        SpawnKind.NearHost => IconKind.Host,
        _ => string.IsNullOrEmpty(o.Place) ? IconKind.Ship : IconKind.Lander
    };

    // Glyphs on a 24x24 grid. Parsed when first drawn: Geometry.Parse needs Avalonia's render platform (see OnlineUi.LockShape).
    public static string GlyphPath(IconKind kind) => kind switch
    {
        IconKind.Home => "M12,3 L22,11.5 H19 V21 H14.5 V15 H9.5 V21 H5 V11.5 H2 Z",
        IconKind.Medbay => "M9,4 H15 V9 H20 V15 H15 V20 H9 V15 H4 V9 H9 Z",
        IconKind.Faction => "M5,3 H7 V21 H5 Z M8,4 H19 L16,8 L19,12 H8 Z",
        IconKind.Shared => "M5,8 A3,3 0 1 0 11,8 A3,3 0 1 0 5,8 Z M2,19 C2,13.5 14,13.5 14,19 Z M13,8 A3,3 0 1 0 19,8 A3,3 0 1 0 13,8 Z M15.2,13.9 C18.5,13.4 22,15 22,19 H15.8 C15.8,17 15.6,15.3 15.2,13.9 Z",
        // The SE2 Online engineer (logo and name tags): helmet with ear pads and visor, shoulders with a collar notch.
        IconKind.Suit => "F0 M12,3 C8.1,3 5.5,5.9 5.5,9.5 C5.5,11.6 6.5,13.4 8,14.5 H16 C17.5,13.4 18.5,11.6 18.5,9.5 C18.5,5.9 15.9,3 12,3 Z M4.1,8 H5.5 V11.5 H4.1 Z M18.5,8 H19.9 V11.5 H18.5 Z M8.6,7.6 H15.4 C16.5,7.6 17.1,8.4 17.1,9.5 C17.1,10.6 16.5,11.4 15.4,11.4 H8.6 C7.5,11.4 6.9,10.6 6.9,9.5 C6.9,8.4 7.5,7.6 8.6,7.6 Z M9.2,10.5 L10.4,8.5 L11,8.85 L9.8,10.85 Z M7,15.2 H17 C19.2,15.2 20,16.8 20,18.5 V21 H4 V18.5 C4,16.8 4.8,15.2 7,15.2 Z M10.5,17.2 H13.5 V18.3 H10.5 Z",
        IconKind.Ship => "M12,2 L16,10 L21,14 V17 L15,15.5 L14,19 H10 L9,15.5 L3,17 V14 L8,10 Z",
        IconKind.Lander => "M2,22 C6,17.5 18,17.5 22,22 Z M12,3 L15,7.5 V11.5 L17.5,14 V15.5 H6.5 V14 L9,11.5 V7.5 Z",
        _ => "M8,7 A4,4 0 1 0 16,7 A4,4 0 1 0 8,7 Z M4,21 C4,14.5 20,14.5 20,21 Z"
    };

    public static Control Hex(IconKind kind, bool available, double s = HexSize)
    {
        double h = s / 2;
        var hex = new Polygon
        {
            Points = new System.Collections.Generic.List<Point> { new Point(h, 1), new Point(s - 4, h / 2 + 1), new Point(s - 4, s - h / 2 - 1), new Point(h, s - 1), new Point(4, s - h / 2 - 1), new Point(4, h / 2 + 1) },
            Fill = Brush(available ? 0x2EF0A01Eu : 0x1AFFFFFFu), Stroke = Brush(available ? L.Amber : L.Dim), StrokeThickness = 2
        };
        var glyph = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(GlyphPath(kind)), Stretch = Stretch.Uniform, Width = s * 0.44, Height = s * 0.44, Fill = Brush(available ? L.Amber : L.Dim), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return new Grid { Width = s, Height = s, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { hex, glyph } };
    }

    /// <summary>A small map: where the player died in the middle, the card's place as a dot (north up).</summary>
    public static Control Radar(SpawnOption o)
    {
        double s = RadarSize, c = s / 2, r = c - 4;
        var canvas = new Canvas { Width = s, Height = s, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = o.HasMap ? 1 : 0.45 };
        canvas.Children.Add(Circle(c, c, r, 0x26FFFFFF, 0x66F0A01E));
        canvas.Children.Add(Circle(c, c, r / 2, 0, 0x33F0A01E));
        canvas.Children.Add(new Line { StartPoint = new Point(c, 4), EndPoint = new Point(c, s - 4), Stroke = Brush(0x22FFFFFF), StrokeThickness = 1 });
        canvas.Children.Add(new Line { StartPoint = new Point(4, c), EndPoint = new Point(s - 4, c), Stroke = Brush(0x22FFFFFF), StrokeThickness = 1 });
        if (!o.HasMap) return canvas;
        canvas.Children.Add(new Line { StartPoint = new Point(c - 4, c - 4), EndPoint = new Point(c + 4, c + 4), Stroke = Brush(Red), StrokeThickness = 2 });
        canvas.Children.Add(new Line { StartPoint = new Point(c - 4, c + 4), EndPoint = new Point(c + 4, c - 4), Stroke = Brush(Red), StrokeThickness = 2 });
        double x = c + Math.Clamp(o.MapX, -1, 1) * r, y = c - Math.Clamp(o.MapY, -1, 1) * r;
        canvas.Children.Add(Circle(x, y, 8, 0x40F0A01E));
        canvas.Children.Add(Circle(x, y, 4, o.Available ? L.Amber : L.Dim));
        return canvas;
    }

    private static Ellipse Circle(double x, double y, double r, uint fill, uint stroke = 0)
    {
        var e = new Ellipse { Width = r * 2, Height = r * 2, Fill = Brush(fill), Stroke = stroke == 0 ? null : Brush(stroke), StrokeThickness = 1.5 };
        Canvas.SetLeft(e, x - r); Canvas.SetTop(e, y - r);
        return e;
    }

    private static WriteableBitmap _logo;
    private static bool _logoFailed;

    // Same decode as the loading screen and the main menu banner: SE2's Avalonia backend only shows bitmaps written at runtime.
    private static WriteableBitmap Logo()
    {
        if (_logo != null || _logoFailed) return _logo;
        try
        {
            var (width, height, rgba) = Countries.DecodePng(File.ReadAllBytes(System.IO.Path.Combine(PluginPaths.ContentDir ?? "", "Textures/GUI/Loading", "Logo.png")));
            var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
            using (var frame = bitmap.Lock()) Marshal.Copy(rgba, 0, frame.Address, rgba.Length);
            return _logo = bitmap;
        }
        catch (Exception ex)
        {
            _logoFailed = true;
            GameLog.Instance.WarnOnce("respawn-logo", $"spawn screen: the logo could not be read ({ex.GetType().Name}: {ex.Message}); text only");
            return null;
        }
    }
}
