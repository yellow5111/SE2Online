using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.XamlIl.Runtime;
using Avalonia.Media;
using Avalonia.Styling;

namespace Y51_SE2Online.SyncEngine;

internal static class OnlineListStyles
{
    public static void Apply(ListBox list)
    {
        var context = new ResourceContext(list);
        list.Resources.MergedDictionaries.Add((ResourceDictionary)Load(context, "UI/Library/Styles/SharedResources.axaml"));
        foreach (string path in new[]
        {
            "UI/Library/Controls/ScrollableBar.axaml",
            "UI/Library/Styles/Avalonia/Scroll/ScrollBar.axaml",
            "UI/Library/Styles/Avalonia/Scroll/ScrollViewer.axaml",
            "UI/Menu/LoadMenu/LoadMenuStyles.axaml"
        }) list.Styles.Add((IStyle)Load(context, path));
        list.Background = Brushes.Transparent;
        list.BorderBrush = Brushes.Transparent;
        list.ClipToBounds = true;
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
    }

    /// <summary>SE2's terminal styles (inventory item tiles, their tooltips and sizes) for everything inside <paramref name="owner"/>.</summary>
    public static void ApplyTerminal(Control owner)
    {
        var context = new ResourceContext(owner);
        owner.Resources.MergedDictionaries.Add((ResourceDictionary)Load(context, "UI/Library/Styles/SharedResources.axaml"));
        owner.Styles.Add((IStyle)Load(context, "UI/TerminalScreen/Styles/TerminalStyles.axaml"));
    }

    private static object Load(IServiceProvider context, string path)
        => AvaloniaXamlLoader.Load(context, new Uri("avares://Game2.Client/" + path));

    private sealed class ResourceContext(Control owner) : IServiceProvider, IAvaloniaXamlIlParentStackProvider
    {
        private readonly IServiceProvider _parent = XamlIlRuntimeHelpers.CreateRootServiceProviderV3(null);
        public IEnumerable<object> Parents => new object[] { owner };
        public object GetService(Type type) => type == typeof(IAvaloniaXamlIlParentStackProvider) ? this : _parent.GetService(type);
    }
}
