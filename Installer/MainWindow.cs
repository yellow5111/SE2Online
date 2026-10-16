using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Y51_SE2Online.Core;

namespace SE2OnlineInstaller;

public sealed class MainWindow : Window
{
    private const string Site = "https://se2-online.yellow51.dev";

    private readonly TextBlock _game = Hint("");
    private readonly TextBlock _release = Hint("Checking for the newest release...");
    private readonly TextBox _folder = new() { Text = PluginInstall.DefaultFolder() };
    private readonly TextBlock _folderHint = Hint("The plugin and its settings live here. A short path without spaces, like C:\\SE2Online, is easiest to share and type.");
    private readonly ToggleButton _launchOn = Segment("On"), _launchOff = Segment("Off");
    private readonly ComboBox _account = new();
    private readonly TextBlock _preview = Hint("");
    private readonly TextBlock _status = new() { FontSize = 20 };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Value = 0 };
    private readonly StackPanel _log = new() { Spacing = 4 };
    private readonly ScrollViewer _logScroll;
    private readonly Button _install = new() { Content = "Install", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _uninstall = new() { Content = "Uninstall" };
    private readonly CheckBox _wipe = new() { Content = "Also delete my SE2 Online settings and favorites", VerticalAlignment = VerticalAlignment.Center };

    private readonly string? _steam = Steam.Root();
    private string? _gameFolder;
    private UpdatePackage? _package;
    private bool _busy, _installed;

    public MainWindow()
    {
        Title = "Space Engineers 2 Online Installer";
        Width = 1180; Height = 880; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SE2OnlineInstaller/Assets/icon.png")));
        _install.Classes.Add("primary");
        _install.Click += async (_, _) => { if (_installed) Close(); else await Run(Install); };
        _uninstall.Click += async (_, _) => await Run(Uninstall);
        _launchOn.IsChecked = true;
        // One switch: the outer corners are round, the seam between the halves is square, as in SE2.
        _launchOff.CornerRadius = new CornerRadius(5, 0, 0, 5);
        _launchOn.CornerRadius = new CornerRadius(0, 5, 5, 0);
        _launchOn.Click += (_, _) => SetLaunch(true);
        _launchOff.Click += (_, _) => SetLaunch(false);
        _account.SelectionChanged += (_, _) => Preview();
        _folder.TextChanged += (_, _) => Preview();
        _install.IsEnabled = false;
        _logScroll = Scroll(_log);

        var browse = new Button { Content = "Browse..." };
        browse.Click += async (_, _) => await Browse();
        var folderRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,Auto") };
        folderRow.Children.Add(_folder); Grid.SetColumn(browse, 2); folderRow.Children.Add(browse);

        var launchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        launchRow.Children.Add(new TextBlock { Text = "Set my Steam launch options", FontSize = 17, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_launchOff, 1); launchRow.Children.Add(_launchOff);
        Grid.SetColumn(_launchOn, 2); launchRow.Children.Add(_launchOn);

        var left = new StackPanel { Spacing = 8 };
        left.Children.Add(Section("GAME"));
        left.Children.Add(_game);
        left.Children.Add(_release);
        left.Children.Add(Gap());
        left.Children.Add(Section("INSTALL LOCATION"));
        left.Children.Add(folderRow);
        left.Children.Add(_folderHint);
        left.Children.Add(Gap());
        left.Children.Add(Section("STEAM LAUNCH OPTIONS"));
        left.Children.Add(launchRow);
        left.Children.Add(_account);
        left.Children.Add(_preview);
        left.Children.Add(Hint("Steam saves launch options only while it is closed, so the installer closes Steam for a moment and opens it again afterwards."));
        if (PluginInstall.PulsarInstalled())
        {
            left.Children.Add(Gap());
            left.Children.Add(new TextBlock { Text = "Using Pulsar? Get SE2 Online from the Pulsar PluginHub in game instead. Don't load it both ways at once.", Foreground = (IBrush)Application.Current!.Resources["Se2Amber"]!, FontSize = 14 });
        }

        var actions = new Grid { RowDefinitions = new RowDefinitions("Auto,10,Auto"), ColumnDefinitions = new ColumnDefinitions("Auto,14,*") };
        Grid.SetColumnSpan(_install, 3); actions.Children.Add(_install);
        Grid.SetRow(_uninstall, 2); actions.Children.Add(_uninstall);
        Grid.SetRow(_wipe, 2); Grid.SetColumn(_wipe, 2); actions.Children.Add(_wipe);

        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        right.Children.Add(Section("STATUS"));
        _status.Margin = new Thickness(0, 0, 0, 12); Grid.SetRow(_status, 1); right.Children.Add(_status);
        Grid.SetRow(_progress, 2); right.Children.Add(_progress);
        _logScroll.Margin = new Thickness(0, 14); Grid.SetRow(_logScroll, 3); right.Children.Add(_logScroll);
        Grid.SetRow(actions, 4); right.Children.Add(actions);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,430") };
        body.Children.Add(Panel(Scroll(left)));
        var rightPanel = Panel(right); Grid.SetColumn(rightPanel, 2); body.Children.Add(rightPanel);

        var site = new Button { Content = "SE2 Online website" };
        site.Click += (_, _) => Open(Site);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        footer.Children.Add(site);
        var version = new TextBlock { Text = "SE2 Online Installer " + typeof(MainWindow).Assembly.GetName().Version!.ToString(3), Foreground = (IBrush)Application.Current!.Resources["Se2Amber"]!, FontSize = 14, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(version, 2); footer.Children.Add(version);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        titleRow.Children.Add(new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://SE2OnlineInstaller/Assets/icon.png"))), Width = 58, Height = 58, VerticalAlignment = VerticalAlignment.Center });
        titleRow.Children.Add(new TextBlock { Text = "Multiplayer", Classes = { "title" }, VerticalAlignment = VerticalAlignment.Center });
        var header = new StackPanel { Spacing = 10 };
        header.Children.Add(titleRow);
        header.Children.Add(new TextBlock { Text = "Space Engineers 2 Online / Install the multiplayer plugin", Classes = { "crumb" } });

        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,24,*,20,Auto"), Margin = new Thickness(56, 34, 56, 26) };
        page.Children.Add(header);
        Grid.SetRow(body, 2); page.Children.Add(body);
        Grid.SetRow(footer, 4); page.Children.Add(footer);

        var root = new Grid();
        root.Children.Add(new Starfield());
        root.Children.Add(page);
        root.Children.Add(Badge());
        Content = root;

        Detect();
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        Closed += (_, _) => { if (_installed) SelfDelete(); };
        Opened += async (_, _) => { await LoadRelease(); if (Program.ScreenshotPath is string shot) await Screenshot.TakeAsync(this, shot); };
    }

    // ---- detection ----

    private void Detect()
    {
        if (_steam == null)
        {
            _game.Text = "Steam isn't installed on this PC. You can still install the plugin, but launch options can't be set.";
            _game.Classes.Add("bad");
            SetLaunch(false);
            _launchOn.IsEnabled = false;
            return;
        }
        _gameFolder = Steam.GameFolder(_steam);
        if (_gameFolder == null) { _game.Text = "Space Engineers 2 wasn't found in your Steam libraries. Install it through Steam first."; _game.Classes.Add("bad"); }
        else { _game.Text = "Space Engineers 2 " + (Steam.GameVersion(_gameFolder) ?? "") + " found at " + _gameFolder; _game.Classes.Add("good"); }
        var accounts = Steam.Accounts(_steam);
        _account.ItemsSource = accounts;
        _account.SelectedIndex = accounts.Count > 0 ? 0 : -1;
        if (accounts.Count == 0) { SetLaunch(false); _launchOn.IsEnabled = false; }
        var saved = PluginInstall.Load();
        if (saved != null) Log($"SE2 Online {saved.Version} is installed in {saved.Folder}.");
        Preview();
    }

    private async Task LoadRelease()
    {
        try
        {
            var manifest = await PluginInstall.LatestAsync(CancellationToken.None);
            _package = manifest.Client;
            if (_package == null) { _release.Text = "The SE2 Online site has no plugin download listed right now. Try again later."; return; }
            var saved = PluginInstall.Load();
            _release.Text = "Newest release: SE2 Online " + _package.Version + $" ({_package.Size / 1048576.0:0.0} MiB, downloaded from the SE2 Online release storage and checked against its signature).";
            _install.Content = saved == null ? "Install SE2 Online " + _package.Version
                : VersionMismatch.IsNewer(_package.Version, saved.Version) ? "Update to " + _package.Version : "Reinstall " + _package.Version;
            _install.IsEnabled = true;
            Status(saved == null ? "Ready to install." : "Ready.");
        }
        catch (Exception e)
        {
            _release.Text = "Couldn't reach the SE2 Online site (" + e.Message + "). Check your connection and restart the installer.";
            _release.Classes.Add("bad");
            Status("Offline.");
        }
    }

    // ---- actions ----

    private async Task Run(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        SetEnabled(false);
        try { await action(); }
        catch (Exception e) { Status("Something went wrong."); Log(e.Message, bad: true); }
        finally { _busy = false; SetEnabled(true); }
    }

    private async Task Install()
    {
        if (_package == null) return;
        if (Steam.GameRunning()) { Status("Close Space Engineers 2 first."); Log("The game is running and holds the plugin files open. Close it, then press Install again.", bad: true); return; }
        string folder = _folder.Text?.Trim() ?? "";
        if (PluginInstall.CheckFolder(folder) is string problem) { Status("Choose another folder."); Log(problem, bad: true); return; }
        var progress = new Progress<(string Text, double Fraction)>(p => { Status(p.Text); _progress.Value = p.Fraction; });
        string version = await Task.Run(() => PluginInstall.InstallAsync(_package, folder, progress, CancellationToken.None));
        string dll = Path.Combine(Path.GetFullPath(folder), LaunchOptions.PluginFile);
        Log($"Installed SE2 Online {version} to {Path.GetFullPath(folder)}.");
        if (PluginInstall.PulsarLocalCopy()) Log("Pulsar also has its own copy of SE2 Online (Pulsar\\Modern\\Local\\Y51_SE2Online). Remove one of them so it only loads once.", bad: true);

        bool launch = _launchOn.IsChecked == true && _account.SelectedItem is SteamAccount;
        if (launch && !await SetLaunchOptions(text => LaunchOptions.With(text, dll)))
            Log("Add this to Space Engineers 2's launch options in Steam yourself: " + LaunchOptions.Token(dll));
        else if (!launch) Log("Launch options left alone. To load the plugin, add this in Steam: " + LaunchOptions.Token(dll));

        _progress.Value = 1;
        Status("Done. Start Space Engineers 2 from Steam and open Multiplayer.");
        _installed = true;
        _install.Content = "Finish";
        Log("This installer removes itself when you close it. Download it again from the SE2 Online site to update or uninstall later.");
    }

    private async Task Uninstall()
    {
        string folder = _folder.Text?.Trim() ?? "";
        if (Steam.GameRunning()) { Status("Close Space Engineers 2 first."); return; }
        if (!File.Exists(Path.Combine(folder, LaunchOptions.PluginFile))) { Status("Nothing to uninstall there."); Log("There is no SE2 Online plugin in " + folder + ". Set the install location to the folder you installed it in.", bad: true); return; }
        if (!await Ask("Uninstall SE2 Online?", "This removes the plugin from " + folder + (_wipe.IsChecked == true ? ", together with your SE2 Online settings and favorites," : "") + " and takes it out of Space Engineers 2's Steam launch options.", "Uninstall", "Cancel")) return;
        if (_steam != null && _account.ItemsSource is IEnumerable<SteamAccount> accounts && accounts.Any())
            await SetLaunchOptions(LaunchOptions.Without, all: true);
        PluginInstall.Uninstall(folder, _wipe.IsChecked == true);
        Status("SE2 Online was uninstalled.");
        Log("Removed the plugin from " + folder + ".");
        _install.Content = _package == null ? "Install" : "Install SE2 Online " + _package.Version;
    }

    /// <summary>Closes Steam if needed (asking first), rewrites the launch options, and opens Steam again.</summary>
    private async Task<bool> SetLaunchOptions(Func<string, string> change, bool all = false)
    {
        if (_steam == null) return false;
        var targets = all ? ((IEnumerable<SteamAccount>)_account.ItemsSource!).ToList() : new List<SteamAccount> { (SteamAccount)_account.SelectedItem! };
        bool wasRunning = Steam.Running();
        if (wasRunning)
        {
            if (!await Ask("Close Steam?", "Steam only saves launch options while it is closed. The installer closes Steam, saves them, and opens Steam again. Downloads in progress pause until then.", "Close Steam and continue", "Skip launch options"))
                return false;
            Status("Closing Steam...");
            if (!await Steam.ShutdownAsync(_steam, TimeSpan.FromSeconds(60))) { Log("Steam didn't close within a minute, so the launch options weren't changed.", bad: true); return false; }
        }
        foreach (var account in targets)
        {
            try
            {
                string before = Steam.ReadLaunchOptions(account);
                string after = change(before);
                if (after != before) Steam.WriteLaunchOptions(account, _ => after);
                Log($"Steam launch options for {account.Name}: {(after.Length == 0 ? "(none)" : after)}");
            }
            catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
            {
                Log($"Couldn't change {account.Name}'s launch options ({e.Message}).", bad: true);
            }
        }
        if (wasRunning) { Status("Opening Steam again..."); Steam.Start(_steam); }
        return true;
    }

    private async Task Browse()
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Where should SE2 Online be installed?", AllowMultiple = false });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is string path)
            _folder.Text = Path.GetFileName(path.TrimEnd('\\')).Equals("SE2Online", StringComparison.OrdinalIgnoreCase) ? path : Path.Combine(path, "SE2Online");
    }

    // ---- state and helpers ----

    private void SetLaunch(bool on)
    {
        _launchOn.IsChecked = on; _launchOff.IsChecked = !on;
        _account.IsEnabled = on;
        Preview();
    }

    private void Preview()
    {
        if (_launchOn.IsChecked != true || _account.SelectedItem is not SteamAccount account) { _preview.Text = ""; return; }
        string dll = Path.Combine(_folder.Text?.Trim() ?? "", LaunchOptions.PluginFile);
        try
        {
            string current = Steam.ReadLaunchOptions(account);
            _preview.Text = "New launch options: " + LaunchOptions.With(current, dll) + (current.Length > 0 ? "\nNow: " + current : "");
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException) { _preview.Text = "Launch options: " + LaunchOptions.Token(dll); }
    }

    private void SetEnabled(bool on)
    {
        _install.IsEnabled = on && (_package != null || _installed);
        _uninstall.IsEnabled = on && !_installed;
        _folder.IsEnabled = on && !_installed;
        _account.IsEnabled = on && !_installed && _launchOn.IsChecked == true;
    }

    private void Status(string text) => _status.Text = text;

    private void Log(string text, bool bad = false)
    {
        var line = Hint(text);
        if (bad) line.Classes.Add("bad");
        _log.Children.Add(line);
        Dispatcher.UIThread.Post(() => _logScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// <summary>A modal question in the same style; true for <paramref name="yes"/>.</summary>
    private async Task<bool> Ask(string title, string text, string yes, string no)
    {
        var dialog = new Window
        {
            Title = title, Width = 560, SizeToContent = SizeToContent.Height, CanResize = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Icon = Icon
        };
        var ok = new Button { Content = yes, Classes = { "primary" } };
        var cancel = new Button { Content = no };
        ok.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        var stack = new StackPanel { Spacing = 16 };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeight.Bold });
        stack.Children.Add(new TextBlock { Text = text, FontSize = 15 });
        stack.Children.Add(buttons);
        dialog.Content = new Border { Classes = { "panel" }, Child = stack, BorderThickness = new Thickness(0) };
        return await dialog.ShowDialog<bool>(this);
    }

    /// <summary>The installer is only needed once: after a successful install it deletes its own exe on exit.</summary>
    private static void SelfDelete()
    {
        string? exe = Environment.ProcessPath;
        if (exe == null || !File.Exists(exe)) return;
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{exe}\"")
            { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })?.Dispose();
        }
        catch (Exception) { }
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); } catch (Exception) { }
    }

    // The bar always shows in its own gutter beside the content, so it never covers a button or text.
    private static ScrollViewer Scroll(Control content)
    {
        content.Margin = new Thickness(0, 0, 22, 0);
        return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, AllowAutoHide = false };
    }

    private static Border Panel(Control child) => new() { Classes = { "panel" }, Child = child };
    private static TextBlock Section(string text) => new() { Text = text, Classes = { "section" } };
    private static TextBlock Hint(string text) => new() { Text = text, Classes = { "hint" } };
    private static Control Gap() => new Border { Height = 8 };
    private static ToggleButton Segment(string text) => new() { Content = text, Classes = { "segment" } };

    // SE2 marks its menus with a corner ribbon; ours says what this window is.
    private static Control Badge()
    {
        var ribbon = new Border
        {
            Background = (IBrush)Application.Current!.Resources["Se2Amber"]!, Width = 190, Height = 26,
            RenderTransform = new RotateTransform(-45), Child = new TextBlock { Text = "INSTALLER", Classes = { "badge" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        return new Canvas { Width = 120, Height = 120, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true, IsHitTestVisible = false, Children = { new Border { Child = ribbon, [Canvas.LeftProperty] = -52.0, [Canvas.TopProperty] = 24.0 } } };
    }
}
