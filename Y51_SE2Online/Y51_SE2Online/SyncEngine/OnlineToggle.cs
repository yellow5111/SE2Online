using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NativeSwitch = Keen.Game2.Client.UI.Library.Controls.ContentControls.Switch;

namespace Y51_SE2Online.SyncEngine;

internal sealed class OnlineToggle : Grid
{
    private readonly NativeSwitch _switch = new() { MinHeight = 50, HorizontalAlignment = HorizontalAlignment.Stretch };
    public event EventHandler IsCheckedChanged;
    public bool IsChecked { get => _switch.IsChecked; set => _switch.IsChecked = value; }
    /// <summary>Help and warning lines sit under the label like a field's hint; the warning is red and bold.</summary>
    public OnlineToggle(string label, string help = null, string warning = null)
    {
        ColumnDefinitions = new ColumnDefinitions("*,24,480");
        MinHeight = 54;
        var caption = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(OnlineUi.Text(label, "SubTitle"));
        foreach (string line in new[] { help, warning })
        {
            if (line == null) continue;
            // The warning skips the theme's foreground binding, which would override its red.
            var hint = OnlineUi.Text(line, "SubtleNotice", 17, inheritForeground: line == warning);
            hint.HorizontalAlignment = HorizontalAlignment.Left; hint.TextAlignment = TextAlignment.Left;
            if (line == warning) { hint.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4D)); hint.FontWeight = FontWeight.Bold; }
            caption.Children.Add(hint);
        }
        Children.Add(caption);
        _switch.Classes.Add("SubTitle");
        Grid.SetColumn(_switch, 2); Children.Add(_switch);
        _switch.PropertyChanged += (_, e) => { if (e.Property == NativeSwitch.IsCheckedProperty) IsCheckedChanged?.Invoke(this, EventArgs.Empty); };
    }
}
