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
    private Action? cancelDockDrag;
    private void PlaceTools(string position)
    {
        settings.ToolsPosition = position is "Bottom" or "Right" or "RightInner" or "LeftInner" ? position : "Left";
        var bottom = settings.ToolsPosition == "Bottom";
        var scroll = (ScrollViewer)toolSurface.Child!;
        var rail = (StackPanel)scroll.Content!;
        rail.Orientation = bottom ? Orientation.Horizontal : Orientation.Vertical;
        rail.VerticalAlignment = bottom ? VerticalAlignment.Center : VerticalAlignment.Top;
        scroll.Width = bottom ? double.NaN : 56;
        scroll.VerticalContentAlignment = bottom ? VerticalAlignment.Center : VerticalAlignment.Top;
        toolSurface.BorderBrush = Palette.Divider;
        bool right = settings.ToolsPosition.StartsWith("Right", StringComparison.Ordinal);
        bool inner = settings.ToolsPosition.EndsWith("Inner", StringComparison.Ordinal);
        toolSurface.BorderThickness = bottom ? default : right != inner ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);
        scroll.Background = null;
        scroll.VerticalScrollBarVisibility = bottom ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        scroll.HorizontalScrollBarVisibility = bottom ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        // Fluent auto-hide overlays the scroll bar over the buttons when the rail is narrow.
        scroll.AllowAutoHide = !bottom;
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
            button.GroupPlacement = bottom ? PlacementMode.Top : right ? PlacementMode.LeftEdgeAlignedTop : PlacementMode.RightEdgeAlignedTop;
            if (bottom) button.Height = 32;
        }
        ToolButton.SetPassiveTip(toolSurface, "Drag the top edge to dock tools on the left, right or bottom");
        toolChrome.ZIndex = 3;
        Grid.SetColumn(toolChrome, bottom ? 3 : right ? (inner ? 4 : 6) : (inner ? 2 : 0));
    }

    private void AttachPanelDrag(Control header, string title, bool topOnly = false)
    {
        Point? start = null;
        bool moving = false;
        IPointer? captured = null;
        var preview = new Canvas { IsHitTestVisible = false, ZIndex = 10 };
        var target = new Border
        {
            Name = "DockDropTarget", BorderBrush = Palette.Accent, BorderThickness = new Thickness(2),
            Background = Palette.DropFill,
            CornerRadius = new CornerRadius(4)
        };
        var ghost = new Border
        {
            Name = "DraggedPanel", Background = Palette.PanelRaised, BorderBrush = Palette.Edge,
            BorderThickness = new Thickness(1), Padding = new Thickness(12, 7), Opacity = .9,
            CornerRadius = new CornerRadius(5), Child = Ui.Label(Locale.T(title))
        };
        preview.Children.Add(target); preview.Children.Add(ghost);
        Grid.SetColumnSpan(preview, 7);
        bool left = false, insert = false, swapRail = false, validDrop = false;
        string toolPosition = "Left";
        int column = 0;
        DockSection? before = null;
        DockGroup? targetTabs = null;
        Rect BoundsOf(Control control) => new(control.TranslatePoint(default, workspaceGrid) ?? default, control.Bounds.Size);
        void Highlight(Rect rect)
        {
            Avalonia.Controls.Canvas.SetLeft(target, rect.X); Avalonia.Controls.Canvas.SetTop(target, rect.Y);
            target.Width = Math.Max(1, rect.Width); target.Height = Math.Max(1, rect.Height);
        }
        void Locate(Point p)
        {
            var area = workspaceGrid.Bounds.Size;
            target.IsVisible = validDrop = new Rect(area).Contains(p);
            if (!validDrop) return;
            left = p.X < area.Width / 2;
            var side = left ? "Left" : "Right";
            var railBounds = BoundsOf(toolChrome);
            var railOnSide = settings.ToolsPosition.StartsWith(side, StringComparison.Ordinal);
            var items = dock.Columns(left).OrderBy(item => item.Index).ToArray();
            var (index, view) = items.OrderBy(item => Math.Abs(p.X - BoundsOf(item.View).Center.X)).FirstOrDefault();
            var rect = view != null ? BoundsOf(view) : new Rect(left ? 0 : area.Width - 296, 0, 296, area.Height);
            double x = Math.Clamp((p.X - rect.X) / Math.Max(1, rect.Width), 0, 1);
            swapRail = false;
            if (title == "Tools")
            {
                bool bottom = p.Y >= area.Height * .8 && p.X >= area.Width * .23 && p.X <= area.Width * .77;
                bool inner = view != null && (left ? x > .5 : x < .5);
                toolPosition = bottom ? "Bottom" : side + (inner ? "Inner" : "");
                double railX = inner ? (left ? rect.Right : rect.Left - 56) : (left ? 0 : area.Width - 56);
                Highlight(bottom ? new Rect(area.Width * .25, area.Height - 56, area.Width * .5, 44)
                    : new Rect(railX, 0, 56, area.Height));
                return;
            }
            before = null;
            targetTabs = null;
            column = index;
            double edge = Math.Min(30, rect.Width * .20);
            insert = view != null && (p.X <= rect.Left + edge || p.X >= rect.Right - edge);
            if (railOnSide && railBounds.Contains(p))
            {
                swapRail = true;
                bool panelOutside = p.X < railBounds.Center.X == left;
                toolPosition = side + (panelOutside ? "Inner" : "");
                x = left == panelOutside ? 0 : 1;
                insert = view != null;
            }
            if (insert || view == null)
            {
                if (insert && x > .5) column++;
                if (view != null && !swapRail)
                {
                    Highlight(new Rect(x > .5 ? rect.Right - edge : rect.Left, rect.Top, edge, rect.Height));
                    return;
                }
                var remaining = dock.Ordered(left).Where(panel => panel.State.Visible && panel.Title != title)
                    .GroupBy(panel => panel.State.Column).OrderBy(group => group.Key).ToArray();
                int otherCount = dock.Ordered(!left).Where(panel => panel.State.Visible && panel.Title != title)
                    .Select(panel => panel.State.Column).Distinct().Count();
                double sideWidth = (remaining.Length + 1) * SideDock.ColumnWidth(area.Width, remaining.Length + 1 + otherCount);
                double Weight(IGrouping<int, DockSection> group) => Math.Clamp(group.First().State.ColumnWeight, .2, 5);
                double unit = (sideWidth - remaining.Length * 5) / (1 + remaining.Sum(Weight));
                var preceding = remaining.Where(group => group.Key < column).ToArray();
                bool outerRail = (swapRail ? toolPosition : settings.ToolsPosition) == side;
                var leftEdge = left ? (outerRail ? railBounds.Width : 0) : area.Width - sideWidth - (outerRail ? railBounds.Width : 0);
                Highlight(new Rect(leftEdge + preceding.Sum(Weight) * unit + preceding.Length * 5, 0, unit, rect.Height));
                return;
            }
            if (workspaceGrid.InputHitTest(p) is Visual hit)
            {
                var ancestors = hit.GetSelfAndVisualAncestors().ToHashSet();
                targetTabs = view.Groups.FirstOrDefault(group => ancestors.Contains(group.Header) &&
                    (group.Members.Count > 1 || group.Active.Title != title));
                if (targetTabs != null)
                {
                    foreach (var (section, tab) in targetTabs.Tabs)
                        if (section.Title != title && p.X < BoundsOf(tab).Center.X) { before = section; break; }
                    Highlight(BoundsOf(targetTabs.Header));
                    return;
                }
            }
            double top = rect.Y, bottomEdge = rect.Bottom;
            foreach (var group in view.Groups)
            {
                var anchor = group.Members.FirstOrDefault(panel => panel.Title != title);
                if (anchor == null) continue;
                var bounds = BoundsOf(group);
                if (p.Y < bounds.Center.Y) { before = anchor; bottomEdge = bounds.Center.Y; break; }
                top = bounds.Center.Y;
            }
            Highlight(new Rect(rect.X, top, rect.Width, Math.Max(16, bottomEdge - top)));
        }
        void Clear()
        {
            if (moving) cancelDockDrag = null;
            start = null; moving = false;
            captured = null;
            workspaceGrid.Children.Remove(preview);
            ghost.Child = Ui.Label(Locale.T(title));
        }
        void Cancel()
        {
            var pointer = captured;
            Clear();
            if (pointer?.Captured == header) pointer.Capture(null);
        }
        header.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(header).Properties.IsLeftButtonPressed && (!topOnly || e.GetPosition(header).Y <= 11))
            {
                start = e.GetPosition(workspaceGrid);
                captured = e.Pointer;
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
                cancelDockDrag = Cancel;
                var source = title == "Tools" ? toolSurface : dock.Section(title).Content;
                if (source.IsEffectivelyVisible && source.Bounds.Width > 0 && source.Bounds.Height > 0)
                {
                    var thumbnail = new Border
                    {
                        Width = 136, Height = 52, IsHitTestVisible = false,
                        Background = new Avalonia.Media.VisualBrush(source)
                        {
                            Stretch = Avalonia.Media.Stretch.UniformToFill,
                            AlignmentY = Avalonia.Media.AlignmentY.Top
                        }
                    };
                    ghost.Child = Ui.Column(5, Ui.Label(Locale.T(title)), thumbnail);
                }
                workspaceGrid.Children.Add(preview);
            }
            Avalonia.Controls.Canvas.SetLeft(ghost, Math.Clamp(p.X + 12, 0, Math.Max(0, workspaceGrid.Bounds.Width - ghost.DesiredSize.Width)));
            Avalonia.Controls.Canvas.SetTop(ghost, Math.Clamp(p.Y + 14, 0, Math.Max(0, workspaceGrid.Bounds.Height - ghost.DesiredSize.Height)));
            Locate(p);
            e.Handled = true;
        }, RoutingStrategies.Tunnel, true);
        header.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (!moving) { Clear(); if (header is not Button && e.Pointer.Captured == header) e.Pointer.Capture(null); return; }
            Locate(e.GetPosition(workspaceGrid));
            e.Handled = true; Clear(); e.Pointer.Capture(null);
            if (!validDrop) return;
            if (title == "Tools") { PlaceTools(toolPosition); settings.Save(); }
            else
            {
                if (swapRail) PlaceTools(toolPosition);
                if (targetTabs != null) dock.JoinTabs(title, targetTabs, before);
                else dock.Move(title, left, before, column, insert);
            }
        }, RoutingStrategies.Tunnel, true);
        header.PointerCaptureLost += (_, e) => { if (e.Source == header) Clear(); };
    }
}
