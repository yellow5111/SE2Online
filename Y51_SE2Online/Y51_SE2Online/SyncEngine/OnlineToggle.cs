using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Y51_SE2Online.SyncEngine;

/// <summary>A setting's lamp tile (<see cref="LampToggle"/>) with its help and warning lines centred under the label. A Grid so
/// pages that set ColumnDefinitions on it keep compiling; the tile spans every column.</summary>
internal sealed class OnlineToggle : Grid
{
    private readonly LampToggle _switch;
    public event EventHandler IsCheckedChanged;
    public bool IsChecked { get => _switch.IsChecked == true; set => _switch.IsChecked = value; }

    public OnlineToggle(string label, string help = null, string warning = null)
    {
        var lines = new[] { help == null ? null : OnlineUi.Text(help, "SubtleNotice", 14), warning == null ? null : OnlineUi.Warning(warning) }
            .Where(l => l != null).ToArray();
        foreach (var line in lines) { line.HorizontalAlignment = HorizontalAlignment.Center; line.TextAlignment = TextAlignment.Center; }
        _switch = new LampToggle(label, lines) { Width = 440 };
        SetColumnSpan(_switch, 3);
        Children.Add(_switch);
        _switch.IsCheckedChanged += (_, _) => IsCheckedChanged?.Invoke(this, EventArgs.Empty);
    }
}
