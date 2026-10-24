using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// The spawn screen's card layout: a fixed number of equal columns and one height for every card line (the tallest card's),
/// so the grid is uniform whatever the count; a short last line stays left-aligned. A wide item takes a line of its own
/// at its own height.
/// </summary>
internal sealed class CardGrid : Grid
{
    public readonly int Columns;
    private readonly double _gap, _rowGap;
    private readonly HashSet<int> _wideRows = new();

    /// <param name="rowGap">The space between lines (the gap between columns when not given).</param>
    public CardGrid(int columns, double columnWidth, double gap, double rowGap = -1)
    {
        Columns = Math.Max(1, columns);
        _gap = gap;
        _rowGap = rowGap < 0 ? gap : rowGap;
        Width = Columns * columnWidth;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        for (int i = 0; i < Columns; i++) ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
    }

    /// <summary>How many columns of at least <paramref name="minWidth"/> fit in <paramref name="available"/>, at least one.</summary>
    public static int ColumnsFor(double available, double minWidth, int most) => Math.Clamp((int)Math.Floor(available / minWidth), 1, most);

    /// <summary>Lays out <paramref name="items"/> row by row; a Wide item starts a new line and fills it.</summary>
    public void Fill(IEnumerable<(Control Item, bool Wide)> items)
    {
        Children.Clear();
        RowDefinitions.Clear();
        _wideRows.Clear();
        int row = -1, column = Columns;
        foreach (var (item, wide) in items)
        {
            if (wide || column >= Columns) { row++; column = 0; RowDefinitions.Add(new RowDefinition(GridLength.Auto)); }
            if (wide) _wideRows.Add(row);
            item.Margin = new Thickness(_gap / 2, _rowGap / 2);
            item.HorizontalAlignment = HorizontalAlignment.Stretch;
            item.VerticalAlignment = VerticalAlignment.Stretch;
            SetRow(item, row);
            SetColumn(item, wide ? 0 : column);
            SetColumnSpan(item, wide ? Columns : 1);
            Children.Add(item);
            column = wide ? Columns : column + 1;
        }
    }

    // Every card line gets the tallest card's height (an Auto row only matches the cards of its own line).
    protected override Size MeasureOverride(Size availableSize)
    {
        double column = (double.IsNaN(Width) ? availableSize.Width : Width) / Columns, tallest = 0;
        foreach (var child in Children)
        {
            if (_wideRows.Contains(GetRow(child))) continue;
            child.Measure(new Size(column, double.PositiveInfinity));
            tallest = Math.Max(tallest, child.DesiredSize.Height);
        }
        for (int i = 0; i < RowDefinitions.Count; i++)
        {
            var height = _wideRows.Contains(i) ? GridLength.Auto : new GridLength(tallest, GridUnitType.Pixel);
            if (RowDefinitions[i].Height != height) RowDefinitions[i].Height = height;
        }
        return base.MeasureOverride(availableSize);
    }
}
