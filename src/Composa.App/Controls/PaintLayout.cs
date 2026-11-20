using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Composa.App.Controls;

internal static class PaintLayout
{
    internal const int Width = 336;

    internal static Grid Columns(string widths, double gap, params Control[] cells)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(widths), ColumnSpacing = gap };
        for (int i = 0; i < cells.Length; ++i)
        {
            Grid.SetColumn(cells[i], i);
            grid.Children.Add(cells[i]);
        }
        return grid;
    }

    internal static Border Surface(Control contents) => new()
    {
        Background = Palette.Field, CornerRadius = new CornerRadius(4), Child = contents
    };

    internal static TextBlock Caption(string text) => new()
    {
        Text = text, FontSize = 11, Foreground = Palette.Secondary, VerticalAlignment = VerticalAlignment.Center
    };
}
