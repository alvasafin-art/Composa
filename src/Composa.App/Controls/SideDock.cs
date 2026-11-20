using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Composa.App.Controls;

/// <summary>One panel under the Layers panel: a header that collapses it and the panel itself, sized by the splitter above it.</summary>
public sealed class DockSection(string title, Control content, DockPanelState state)
{
    public const double MinHeight = 90;

    public string Title { get; } = title;
    public Control Content { get; } = content;
    public bool HasOwnHeader { get; init; }
    internal double ExtraHeight { get; set; }
    internal double ExpandedHeight => Math.Max(MinHeight, State.Height) + ExtraHeight;
    internal Control? Frame { get; set; }
    internal RowDefinition? Row { get; set; }
    public DockPanelState State { get; set; } = state;

    /// <summary>Raised when the section is shown, hidden, collapsed, expanded or resized, so the window can remember it.</summary>
    public event Action? Changed;

    internal void RaiseChanged() => Changed?.Invoke();
}

/// <summary>
/// The column on the right: the Layers panel on top, taking whatever room is left, and under it the sections the
/// Window menu shows, each with a header that collapses it and a splitter above it to size it.
/// </summary>
internal sealed class DockColumn : UserControl
{
    private double MinTopHeight => top is Border { Child: null } ? 0 : 252;

    private readonly Control top;
    private readonly Action<Control, string> dragHeader;
    private readonly IReadOnlyList<DockSection> sections;
    private readonly Grid grid = new();
    private readonly DockPanel frame = new();
    private readonly ScrollViewer sectionScroll = new()
    {
        Name = "DockSectionScroll",
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
    };

    public DockColumn(Control top, DockSection[] sections, Action<Control, string> dragHeader)
    {
        this.top = top;
        this.dragHeader = dragHeader;
        this.sections = sections;
        sectionScroll.Content = grid;
        frame.Children.Add(sectionScroll);
        DockPanel.SetDock(top, Dock.Top);
        Content = frame;
        SizeChanged += (_, _) => FitHeight();
        Layout();
    }

    // Transient content such as reference thumbnails expands a section without rewriting the saved splitter position.
    public void SetExtraHeight(DockSection section, double height)
    {
        if (section.ExtraHeight == height) return;
        section.ExtraHeight = height;
        if (section.Row != null && !section.State.Collapsed) section.Row.Height = new GridLength(section.ExpandedHeight);
        FitHeight();
    }

    private string? activeTab;
    public event Action? PresentationChanged;
    public bool Presents(DockSection section) => section.State.Visible &&
        (Tabbed ? activeTab == section.Title : !section.State.Collapsed);
    public bool Tabbed { get; private set; }
    public void SetTabbed(bool tabbed)
    {
        if (Tabbed == tabbed) return;
        Tabbed = tabbed;
        Layout();
    }

    public void SetVisible(DockSection section, bool visible)
    {
        if (section.State.Visible == visible) return;
        section.State = section.State with { Visible = visible };
        if (visible) activeTab = section.Title;
        Layout();
        section.RaiseChanged();
    }

    public void SetCollapsed(DockSection section, bool collapsed)
    {
        if (section.State.Collapsed == collapsed) return;
        section.State = section.State with { Collapsed = collapsed };
        Layout();
        section.RaiseChanged();
    }

    /// <summary>Rows from the top: the Layers panel, then a splitter and a section for every section shown.</summary>
    private void Layout()
    {
        foreach (var section in sections) section.Row = null;
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        // Each layout builds new section frames, so the panels leave the old ones first.
        foreach (var section in sections)
            if (section.Content.Parent is Panel parent) parent.Children.Remove(section.Content);
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star) { MinHeight = MinTopHeight });
        if (top.Parent == null) grid.Children.Add(top);
        var visible = sections.Where(s => s.State.Visible).ToArray();
        if (Tabbed && visible.Length > 0)
        {
            var tabs = new TabControl { Name = "SecondaryPanels", ItemsSource = visible.Select(section =>
            {
                var host = new Grid();
                host.Children.Add(section.Content);
                var tab = new TabItem { Header = Locale.T(section.Title), Tag = section.Title, Content = host };
                dragHeader(tab, section.Title);
                return tab;
            }).ToArray() };
            tabs.SelectedIndex = Math.Max(0, Array.FindIndex(visible, s => s.Title == activeTab));
            if (tabs.SelectedItem is TabItem { Tag: string selected }) activeTab = selected;
            tabs.SelectionChanged += (_, e) =>
            {
                if (ReferenceEquals(e.Source, tabs) && tabs.SelectedItem is TabItem { Tag: string name } && name != activeTab)
                { activeTab = name; PresentationChanged?.Invoke(); }
            };
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(TabHeight())));
            var frame = new Border { BorderThickness = new Thickness(0, MinTopHeight > 0 ? 1 : 0, 0, 0), BorderBrush = Palette.Divider, Child = tabs };
            Grid.SetRow(frame, 1); grid.Children.Add(frame);
            FitHeight();
            PresentationChanged?.Invoke();
            return;
        }
        foreach (var section in visible)
        {
            var open = !section.State.Collapsed;
            var row = new RowDefinition(open ? new GridLength(section.ExpandedHeight) : GridLength.Auto) { MinHeight = open ? DockSection.MinHeight : 0 };
            if (MinTopHeight > 0 || section != visible[0])
            {
                var splitter = new GridSplitter
                {
                    ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                    Height = 5, Background = Brushes.Transparent, IsEnabled = open
                };
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Grid.SetRow(splitter, grid.RowDefinitions.Count - 1);
                var rule = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center, Background = Palette.Divider, IsHitTestVisible = false };
                Grid.SetRow(rule, Grid.GetRow(splitter)); grid.Children.Add(rule);
                grid.Children.Add(splitter);
                splitter.DragCompleted += (_, _) =>
                {
                    section.State = section.State with { Height = Math.Max(DockSection.MinHeight, Math.Round(row.ActualHeight) - section.ExtraHeight) };
                    section.RaiseChanged();
                };
            }
            section.Row = row;
            grid.RowDefinitions.Add(row);
            var panel = Build(section, open);
            section.Frame = panel;
            Grid.SetRow(panel, grid.RowDefinitions.Count - 1);
            grid.Children.Add(panel);

        }
        FitHeight();
        PresentationChanged?.Invoke();
    }

    private double TabHeight() => Math.Clamp(Bounds.Height - MinTopHeight, 138,
        Math.Max(160, sections.Where(s => s.State.Visible).Select(s => s.ExpandedHeight).DefaultIfEmpty(220).Max()));

    private void FitHeight()
    {
        if (Bounds.Height <= 0) return;
        var desired = MinTopHeight + sections.Where(s => s.State.Visible)
            .Sum(s => s.State.Collapsed ? 35 : s.ExpandedHeight + 5);
        if (MinTopHeight == 0 && sections.Any(s => s.State.Visible)) desired -= 5;
        if (Tabbed && grid.RowDefinitions.Count == 2)
        {
            grid.RowDefinitions[1].Height = MinTopHeight == 0 ? GridLength.Star : new GridLength(TabHeight());
            desired = MinTopHeight + TabHeight();
        }
        var compact = desired > Bounds.Height;
        if (MinTopHeight == 0 && !Tabbed)
        {
            var first = sections.FirstOrDefault(s => s.State.Visible && !s.State.Collapsed);
            if (first?.Row is {} row) row.Height = compact ? new GridLength(first.ExpandedHeight) : GridLength.Star;
        }
        if (compact && top.Parent == grid)
        {
            grid.Children.Remove(top);
            frame.Children.Insert(0, top);
        }
        else if (!compact && top.Parent == frame)
        {
            frame.Children.Remove(top);
            grid.Children.Add(top);
        }
        top.Height = MinTopHeight == 0 ? 0 : compact ? Math.Min(MinTopHeight, Bounds.Height * .54) : double.NaN;
        grid.RowDefinitions[0].MinHeight = compact ? 0 : MinTopHeight;
        grid.RowDefinitions[0].Height = compact || MinTopHeight == 0 ? new GridLength(0) : GridLength.Star;
        grid.Height = compact ? desired - MinTopHeight : Bounds.Height;
    }

    private Control Build(DockSection section, bool open)
    {
        var chevron = Icons.Create(open ? Icons.ChevronDown : Icons.ChevronRight, 12, Palette.Secondary);
        var title = Ui.Label(Locale.T(section.Title), weight: FontWeight.SemiBold);
        var header = new Button
        {
            Classes = { "flat" }, HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent, Padding = new Thickness(8, 6, 10, 6),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { chevron, title } }
        };
        var label = (open ? "Collapse " : "Expand ") + section.Title;
        Locale.Tip(header, label);
        Avalonia.Automation.AutomationProperties.SetName(header, label);
        dragHeader(header, section.Title);
        header.Click += (_, _) => SetCollapsed(section, open);
        var panel = new DockPanel { Background = Palette.Panel };
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        if (open) panel.Children.Add(section.Content);
        return panel;
    }
}


/// <summary>Both sides share one ordered set of panel states. Moving a panel reparents its live content.</summary>
public sealed class SideDock : UserControl
{
    private readonly DockSection[] sections;
    private readonly DockSection layers;
    private readonly Action<Control, string> dragHeader;
    private readonly Dictionary<(bool Left, int Column), DockColumn> columns = new();
    internal IEnumerable<(int Index, Control View)> Columns(bool onLeft) =>
        columns.Where(pair => pair.Key.Left == onLeft && pair.Value.IsVisible)
            .Select(pair => (pair.Key.Column, (Control)pair.Value));
    public Border LeftHost { get; } = new();
    public bool Tabbed { get; private set; }
    public event Action? PresentationChanged;
    private readonly DockPanel leftFrame = new() { Background = Palette.Panel }, rightFrame = new() { Background = Palette.Panel };
    public SideDock(LayersPanel layerPanel, Action<Control, string> dragHeader, params DockSection[] panels)
    {
        this.dragHeader = dragHeader;
        LeftHost.Child = leftFrame;
        Content = rightFrame;
        layers = new DockSection("Layers", layerPanel, new DockPanelState(Height: 285));
        sections = [layers, .. panels];
        dragHeader(layerPanel.PanelTitle, "Layers");
        Rebuild();
    }
    public DockSection Section(string title) => sections.First(s => s.Title == title);
    public IEnumerable<DockSection> Ordered(bool onLeft) => sections.Where(s => s.State.Left == onLeft).OrderBy(s => s.State.Order);
    public void SetCollapsed(DockSection section, bool value) => Column(section).SetCollapsed(section, value);
    private DockColumn Column(DockSection section) => columns[(section.State.Left, section.State.Column)];
    public bool Presents(DockSection section) => Column(section).Presents(section);
    public void SetExtraHeight(DockSection section, double height) => Column(section).SetExtraHeight(section, height);
    public void SetVisible(DockSection section, bool visible)
    {
        if (section.State.Visible == visible) return;
        section.State = section.State with { Visible = visible };
        section.RaiseChanged();
        Rebuild();
    }
    public void SetTabbed(bool value)
    {
        Tabbed = value;
        foreach (var column in columns.Values) column.SetTabbed(value);
    }
    public Dictionary<string,DockPanelState> CaptureLayout() => sections.ToDictionary(s => s.Title, s => s.State);
    public void RestoreLayout(IReadOnlyDictionary<string,DockPanelState> states, bool persist = true)
    {
        foreach (var panel in sections)
            if (states.TryGetValue(panel.Title, out var state) && state != null)
                panel.State = state with { Height = double.IsFinite(state.Height) ? Math.Clamp(state.Height, 90, 600) : 220 };
        Rebuild();
        if (persist) foreach (var panel in sections) panel.RaiseChanged();
    }
    public void Move(string title, bool onLeft, DockSection? before, int? targetColumn = null, bool insertColumn = false)
    {
        var moved = Section(title);
        int column = targetColumn ?? before?.State.Column ?? 0;
        var order = Ordered(onLeft).Where(panel => panel != moved && panel.State.Column == column && !insertColumn).ToList();
        if (before == moved) return;
        if (insertColumn)
        {
            foreach (var panel in Ordered(onLeft).Where(panel => panel != moved && panel.State.Column >= column).ToArray())
                panel.State = panel.State with { Column = panel.State.Column + 1 };
        }
        int index = before == null ? -1 : order.IndexOf(before);
        double weight = order.FirstOrDefault()?.State.ColumnWeight ?? 1;
        moved.State = moved.State with { ColumnWeight = weight };
        order.Insert(index < 0 ? order.Count : index, moved);
        for (int i = 0; i < order.Count; i++)
            order[i].State = order[i].State with { Left = onLeft, Column = column, Order = i };
        foreach (bool side in new[] { false, true })
        {
            var groups = Ordered(side).GroupBy(panel => panel.State.Column).OrderBy(group => group.Key).ToArray();
            for (int i = 0; i < groups.Length; i++)
                foreach (var panel in groups[i]) panel.State = panel.State with { Column = i };
        }
        Rebuild();
        foreach (var panel in sections) panel.RaiseChanged();
    }

    private void Rebuild()
    {
        leftFrame.Children.Clear(); rightFrame.Children.Clear();
        columns.Clear();
        foreach (var panel in sections)
        {
            panel.Frame = null;
            if (panel.Content.Parent is Panel parent) parent.Children.Remove(panel.Content);
        }
        Grid Build(bool onLeft)
        {
            var host = new Grid();
            foreach (var group in Ordered(onLeft).GroupBy(panel => panel.State.Column).OrderBy(group => group.Key))
            {
                var list = group.ToList();
                bool visible = list.Any(panel => panel.State.Visible);
                Control top = new Border();
                bool pinned = list.FirstOrDefault(panel => panel.State.Visible) == layers;
                if (list.Contains(layers)) ((LayersPanel)layers.Content).PanelTitle.IsVisible = pinned;
                if (pinned) { top = layers.Content; list.Remove(layers); }
                var column = new DockColumn(top, list.ToArray(), dragHeader) { IsVisible = visible };
                columns[(onLeft, group.Key)] = column;
                column.SetTabbed(Tabbed);
                column.PresentationChanged += () => PresentationChanged?.Invoke();
                if (!visible) continue;
                if (host.ColumnDefinitions.Count > 0)
                {
                    host.ColumnDefinitions.Add(new ColumnDefinition(5, GridUnitType.Pixel));
                    var splitter = new GridSplitter
                    {
                        Width = 5, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns,
                        ResizeBehavior = GridResizeBehavior.PreviousAndNext
                    };
                    Grid.SetColumn(splitter, host.ColumnDefinitions.Count - 1);
                    host.Children.Add(splitter);
                    splitter.DragCompleted += (_, _) =>
                    {
                        var widths = host.Children.OfType<DockColumn>().Select(view =>
                            (Index: columns.Single(pair => pair.Value == view).Key.Column, Width: view.Bounds.Width)).ToArray();
                        var mean = widths.Average(item => item.Width);
                        if (mean <= 0) return;
                        foreach (var item in widths)
                            foreach (var section in Ordered(onLeft).Where(s => s.State.Column == item.Index))
                            {
                                section.State = section.State with { ColumnWeight = item.Width / mean };
                                section.RaiseChanged();
                            }
                    };
                    var rule = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Center, Background = Palette.Divider, IsHitTestVisible = false };
                    Grid.SetColumn(rule, host.ColumnDefinitions.Count - 1); host.Children.Add(rule);
                }
                double weight = group.First().State.ColumnWeight;
                host.ColumnDefinitions.Add(new ColumnDefinition(double.IsFinite(weight) ? Math.Clamp(weight, .2, 5) : 1, GridUnitType.Star) { MinWidth = 120 });
                Grid.SetColumn(column, host.ColumnDefinitions.Count - 1); host.Children.Add(column);
            }
            return host;
        }
        leftFrame.Children.Add(Build(true)); rightFrame.Children.Add(Build(false));
        UpdateWidths();
        PresentationChanged?.Invoke();
    }

    internal static double ColumnWidth(double available, int count) =>
        Math.Min(296, Math.Max(100, (available - 250) / Math.Max(1, count)));

    internal void UpdateWidths()
    {
        int left = Columns(true).Count(), right = Columns(false).Count();
        double available = TopLevel.GetTopLevel(this)?.ClientSize.Width ?? 1440;
        double columnWidth = ColumnWidth(available, left + right);
        LeftHost.Width = left * columnWidth;
        Width = right * columnWidth;

    }
}
