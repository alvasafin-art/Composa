using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Composa.App.Controls;

namespace Composa.App;

public sealed partial class MainWindow
{
    private readonly Grid workspaceGrid = new();
    private readonly Border toolSurface = new() { Background = Palette.Panel };
    private Control toolChrome = null!;
    private readonly ScrollViewer propertiesView = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private void PlaceTools(string position)
    {
        settings.ToolsPosition = position is "Bottom" or "Right" ? position : "Left";
        var bottom = settings.ToolsPosition == "Bottom";
        var scroll = (ScrollViewer)toolSurface.Child!;
        var rail = (StackPanel)scroll.Content!;
        rail.Orientation = bottom ? Orientation.Horizontal : Orientation.Vertical;
        rail.VerticalAlignment = VerticalAlignment.Center;
        scroll.Width = bottom ? double.NaN : 56;
        scroll.Background = null;
        scroll.VerticalScrollBarVisibility = bottom ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        scroll.HorizontalScrollBarVisibility = bottom ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        toolSurface.CornerRadius = new CornerRadius(bottom ? 14 : 0);
        toolSurface.ClipToBounds = true;
        toolSurface.Padding = bottom ? new Thickness(12, 8) : new Thickness(0, 6, 0, 0);
        toolChrome.HorizontalAlignment = bottom ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        toolChrome.VerticalAlignment = bottom ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        toolChrome.Margin = bottom ? new Thickness(10, 0, 10, 12) : default;
        rail.Margin = bottom ? default : new Thickness(0, 8);
        if (rail.Children.LastOrDefault() is StackPanel colors)
        {
            colors.Orientation = rail.Orientation;
            colors.VerticalAlignment = VerticalAlignment.Center;
            colors.Spacing = bottom ? 4 : 2;
            colors.Margin = bottom ? new Thickness(6, 0, 0, 0) : default;
            var swatches = (Panel)colors.Children[0];
            swatches.Width = swatches.Height = bottom ? 32 : 42;
            swatches.Margin = bottom ? default : new Thickness(0, 8, 0, 0);
            foregroundSwatch.Width = foregroundSwatch.Height = backgroundSwatch.Width = backgroundSwatch.Height = bottom ? 21 : 26;
            backgroundSwatch.Margin = bottom ? new Thickness(11, 11, 0, 0) : new Thickness(14, 14, 0, 0);
            var swap = (Button)colors.Children[1];
            swap.Width = bottom ? 26 : double.NaN;
            swap.VerticalAlignment = VerticalAlignment.Center;
        }
        foreach (var button in rail.Children.OfType<ToolButton>())
        {
            button.GroupPlacement = bottom ? PlacementMode.Top : settings.ToolsPosition == "Right" ? PlacementMode.LeftEdgeAlignedTop : PlacementMode.RightEdgeAlignedTop;
            if (bottom) button.Height = 32;
        }
        ToolTip.SetTip(toolSurface, "Drag the top edge to dock tools on the left, right or bottom");
        toolChrome.ZIndex = 3;
        Grid.SetColumn(toolChrome, bottom ? 3 : settings.ToolsPosition == "Right" ? 6 : 0);
    }

    private void AttachPanelDrag(Control header, string title, bool topOnly = false)
    {
        Point? start = null;
        bool moving = false;
        var hint = new Border
        {
            Background = Palette.Selected,
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            ZIndex = 5
        };
        string toolPosition = "Left";
        bool left = false;
        DockSection? before = null;
        void Clear()
        {
            start = null; moving = false;
            workspaceGrid.Children.Remove(hint);
        }
        header.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(header).Properties.IsLeftButtonPressed && (!topOnly || e.GetPosition(header).Y <= 11))
            {
                start = e.GetPosition(workspaceGrid);
                e.Pointer.Capture(header);
            }
        }, RoutingStrategies.Tunnel, true);
        header.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (start is not { } origin) return;
            if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) { Clear(); return; }
            var p = e.GetPosition(workspaceGrid);
            if (!moving && Math.Abs(p.X - origin.X) + Math.Abs(p.Y - origin.Y) < 7) return;
            if (!moving)
            {
                moving = true; e.Pointer.Capture(header);
                Grid.SetColumnSpan(hint, 7); workspaceGrid.Children.Add(hint);
            }
            left = p.X < workspaceGrid.Bounds.Width / 2;
            toolPosition = p.Y > workspaceGrid.Bounds.Height * .8 ? "Bottom" : left ? "Left" : "Right";
            before = dock.Ordered(left).FirstOrDefault(s => s.Title != title && s.State.Visible &&
                s.Content.TranslatePoint(default, workspaceGrid) is { } at && p.Y < at.Y + s.Content.Bounds.Height / 2);
            hint.Child = Ui.Label(title == "Tools" ? "Tools · " + toolPosition :
                (left ? "Left" : "Right") + (before == null ? " · Bottom" : " · Above " + before.Title));
            e.Handled = true;
        }, RoutingStrategies.Tunnel, true);
        header.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (!moving) { Clear(); if (header is not Button && e.Pointer.Captured == header) e.Pointer.Capture(null); return; }
            e.Handled = true; Clear(); e.Pointer.Capture(null);
            if (title == "Tools") { PlaceTools(toolPosition); settings.Save(); }
            else dock.Move(title, left, before);
        }, RoutingStrategies.Tunnel, true);
        header.PointerCaptureLost += (_, e) => { if (e.Source == header) Clear(); };
    }
}
