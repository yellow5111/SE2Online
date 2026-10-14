using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Controls.Templates;
using HarmonyLib;
using Keen.Game2.Client.UI.TerminalScreen;
using Keen.VRage.UI.Screens;
using Keen.VRage.Library.Utils;
using Keen.VRage.Library.Localization;
using Keen.Game2.Client.UI.Library.Dialogs.TwoOptionsDialog;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal static class SocialUi
{
    private static IObservableDisposable _factions, _grids;
    internal static SocialSnapshot Snapshot => ClientWorld.Current?.BoundSession?.Social ?? HostWorld.Current?.Social?.Local ?? new();
    internal static bool Ready => Snapshot.Actor != 0;
    internal static void Send(SocialRequest request)
    {
        if (ClientWorld.Current?.BoundSession is { } client) client.SendSocial(request);
        else HostWorld.Current?.QueueSocial(request);
    }
    internal static void OpenFactions() => MpMenu.Queue(() =>
    {
        if (_factions != null) return;
        var shared = JoinStatusUi.FindShared(MpPlugin.App); if (shared == null) return;
        _factions = shared.CreateScreen<FactionsScreen>(new SocialScreenViewModel(), true, ScreenLayer.Top);
        _factions.OnDisposed += _ => _factions = null;
    });
    internal static void OpenGrids() => MpMenu.Queue(() =>
    {
        if (_grids != null) return;
        var shared = JoinStatusUi.FindShared(MpPlugin.App); if (shared == null) return;
        _grids = shared.CreateScreen<GridOwnershipScreen>(new SocialScreenViewModel(), true, ScreenLayer.Top);
        _grids.OnDisposed += _ => _grids = null;
    });

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(TerminalScreenViewModel), "BuildScreenTree"), postfix: new HarmonyMethod(typeof(SocialUi), nameof(TreeBuilt)));
        harmony.Patch(AccessTools.Constructor(typeof(TerminalScreen)), postfix: new HarmonyMethod(typeof(SocialUi), nameof(TerminalBuilt)));
        ControlPanelOwnership.Install();
    }
    internal static void Confirm(string title, string message, Action action) => MpMenu.Queue(() =>
    {
        var shared = JoinStatusUi.FindShared(MpPlugin.App); if (shared == null) return;
        shared.ShowDialog(new TwoOptionsDialogViewModel(new TwoOptionsDialogDefinition
        {
            Title = OnlineUi.Raw(title), Content = OnlineUi.Raw(message),
            ConfirmOption = OnlineUi.Raw("Confirm"), CancelOption = OnlineUi.Raw("Cancel")
        }) { ConfirmAction = () => MpMenu.Queue(action) });
    });
    private sealed record TerminalTab(bool Factions);
    // Test diagnostics: the most recently built terminal.
    internal static WeakReference<TerminalScreenViewModel> LastTerminal;
    private static readonly TerminalCategoryId FactionsCategory = new("Y51Factions");

    // Multiplayer only: Grid > Ownership, and a Factions category after Map, built like SE2's own tabs.
    private static void TreeBuilt(TerminalScreenViewModel __instance)
    {
        LastTerminal = new WeakReference<TerminalScreenViewModel>(__instance);
        if (!Ready) return;
        var categories = __instance.Categories.ToList();
        var grid = categories.FirstOrDefault(c => c.Id == TerminalCategoryId.From(TerminalScreenMode.Grid));
        if (grid == null) return;
        var factions = new TerminalCategoryTab(FactionsCategory, OnlineUi.Raw("Factions"), isVisible: true);
        int map = categories.FindIndex(c => c.Id == TerminalCategoryId.From(TerminalScreenMode.Map));
        categories.Insert(map < 0 ? categories.Count : map + 1, factions);
        var subScreens = __instance.SubScreens.ToList();
        int lastGrid = subScreens.FindLastIndex(s => s.Category == grid && s.IsVisible);
        subScreens.Insert(lastGrid + 1, new TerminalSubScreen(grid, new TerminalScreenId(grid.Id.Value + "/Ownership"), OnlineUi.Raw("Ownership"), isVisible: true, typeof(TerminalTab), _ => new TerminalTab(false)));
        subScreens.Add(new TerminalSubScreen(factions, new TerminalScreenId(FactionsCategory.Value + "/Factions"), OnlineUi.Raw("Factions"), isVisible: true, typeof(TerminalTab), _ => new TerminalTab(true)));
        __instance.Categories = categories;
        __instance.SubScreens = subScreens;
    }

    private static void TerminalBuilt(TerminalScreen __instance)
        => __instance.DataTemplates.Add(new FuncDataTemplate<TerminalTab>((tab, _) =>
        {
            if (tab == null) return null;
            if (tab.Factions)
            {
                var view = new FactionsView();
                return OnlineUi.Embedded(view.Navigation, view.Status, view.Body, view.EmbeddedActions, view.QueueRefresh);
            }
            var grids = new GridOwnershipView();
            return OnlineUi.Embedded(grids.Navigation, grids.Status, grids.Body, grids.EmbeddedActions, grids.QueueRefresh);
        }));
}

public sealed class SocialScreenViewModel : ScreenViewModel
{
    public event Action Refresh;
    private long _next;
    public SocialScreenViewModel()
    {
        KeepsOtherScreensVisible = false; AllowsInputFromLowerScreens = false; InitializeInputContext();
        OnTick += () => { if (Core.MpClock.NowMs() < _next) return; _next = Core.MpClock.NowMs() + 500; Refresh?.Invoke(); };
    }
    public void Close() => SignalWantsClose();
}
