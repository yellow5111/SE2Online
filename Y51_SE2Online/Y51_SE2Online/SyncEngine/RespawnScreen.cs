using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Keen.VRage.Library.Utils;
using Keen.VRage.UI.Screens;
using Y51_SE2Online.KeenNetcode;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>Where a joiner spawns: their medbays, faction medbays, the suit or a respawn ship.</summary>
public sealed class RespawnScreen : ScreenView
{
    private static IObservableDisposable _open;
    private static SpawnMenu _shown;

    private readonly ListBox _options = OnlineUi.List("SpawnOptions");
    private readonly TextBlock _status = OnlineUi.Text("");
    private readonly Button _spawn;
    private SocialScreenViewModel _model;
    private SpawnMenu _menu;

    /// <summary>Opens the screen while the host waits for our choice, and closes it once we have chosen.</summary>
    internal static void Tick()
    {
        var menu = KeenRespawn.Menu;
        if (menu == null)
        {
            if (_open != null) { _open.Dispose(); _open = null; }
            _shown = null;
            return;
        }
        if (_open != null && ReferenceEquals(menu, _shown)) return;
        _shown = menu;
        if (_open != null) return;
        var shared = JoinStatusUi.FindShared(MpPlugin.App); if (shared == null) return;
        _open = shared.CreateScreen<RespawnScreen>(new SocialScreenViewModel(), true, ScreenLayer.Top);
        _open.OnDisposed += _ => _open = null;
    }

    public RespawnScreen()
    {
        _options.ItemTemplate = new FuncDataTemplate<SpawnOption>((o, _) => o == null ? null : OnlineUi.ListCells("*,720", o.Name, o.Detail));
        _options.SelectionChanged += (_, _) => _spawn.IsEnabled = _options.SelectedItem is SpawnOption { Available: true };
        _options.DoubleTapped += (_, _) => Choose();
        _spawn = OnlineUi.Button("Spawn", Choose, "SpawnChoose", true);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        body.Children.Add(OnlineUi.Cells("*,720", "SPAWN POINT", "DETAILS"));
        Grid.SetRow(_options, 1); body.Children.Add(_options);
        Content = OnlineUi.Shell(this, "CHOOSE SPAWN", OnlineUi.Row(), _status, OnlineUi.Panel(body), OnlineUi.Row(_spawn));
        Refresh();
    }

    private void Choose()
    {
        if (_menu == null || _options.SelectedItem is not SpawnOption { Available: true } option) return;
        KeenRespawn.Choose(Array.IndexOf(_menu.Options, option));
        _model?.Close();
    }

    private void Refresh()
    {
        var menu = KeenRespawn.Menu;
        if (menu == null || ReferenceEquals(menu, _menu)) return;
        _menu = menu;
        _status.Text = menu.Message.Length > 0 ? menu.Message : menu.Respawn ? "You died. Choose where to respawn." : "Choose where to spawn.";
        _options.ItemsSource = menu.Options;
        _options.SelectedItem = menu.Options.FirstOrDefault(o => o.Available);
        _spawn.IsEnabled = _options.SelectedItem != null;
    }

    private void QueueRefresh() => Dispatcher.UIThread.Post(Refresh);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model != null) _model.Refresh -= QueueRefresh;
        _model = DataContext as SocialScreenViewModel; if (_model != null) _model.Refresh += QueueRefresh;
    }
}
