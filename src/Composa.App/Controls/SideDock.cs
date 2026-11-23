using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Composa.App.Controls;

/// <summary>A live panel and its placement in a stack, a column, or a group of tabs.</summary>
public sealed class DockSection(string title, Control content, DockPanelState state)
{
    public const double MinHeight = 90;
    public string Title { get; } = title;
    public Control Content { get; } = content;
    public DockPanelState State { get; set; } = state;
    internal double ExtraHeight { get; set; }
    internal Control? Frame { get; set; }
}

/// <summary>Both sides share one ordered set of panel placements. Only the active tab is attached to each group.</summary>
public sealed class SideDock : UserControl
{
    private readonly DockSection[] sections;
    private readonly Action<Control, string> dragHeader;
    private readonly Dictionary<(bool Left, int Column), DockColumn> columns = new();
    private readonly Dictionary<DockSection, DockGroup> groups = new();
    private readonly Border rightHost = new() { Background = Palette.Panel };
    public Border LeftHost { get; } = new() { Background = Palette.Panel };
    public bool Tabbed { get; private set; }
    public event Action? PresentationChanged;
    public event Action? LayoutChanged;

    public SideDock(Action<Control, string> dragHeader, params DockSection[] panels)
    {
        this.dragHeader = dragHeader;
        sections = panels;
        Content = rightHost;
        Rebuild();
    }

    public DockSection Section(string title) => sections.First(section => section.Title == title);
    public IEnumerable<DockSection> Ordered(bool onLeft) => sections.Where(section => section.State.Left == onLeft).OrderBy(section => section.State.Order);
    internal IEnumerable<(int Index, DockColumn View)> Columns(bool onLeft) =>
        columns.Where(pair => pair.Key.Left == onLeft).Select(pair => (pair.Key.Column, pair.Value));

    public bool Presents(DockSection section) => groups.TryGetValue(section, out var group) && group.Active == section && !section.State.Collapsed;

    public void Activate(DockSection section)
    {
        if (!section.State.Visible || !groups.TryGetValue(section, out var group)) return;
        if (group.Active == section && !section.State.Collapsed) return;
        section.State = section.State with { Collapsed = false };
        MarkActive(section);
        group.Display(section);
        Column(section).FitHeight();
        ChangedPresentation();
    }

    public void SetCollapsed(DockSection section, bool collapsed)
    {
        if (section.State.Collapsed == collapsed) return;
        section.State = section.State with { Collapsed = collapsed };
        if (groups.TryGetValue(section, out var group) && group.Active == section)
        {
            group.Display(section);
            Column(section).FitHeight();
        }
        ChangedPresentation();
    }

    public void SetExtraHeight(DockSection section, double height)
    {
        if (section.ExtraHeight == height) return;
        section.ExtraHeight = height;
        if (Presents(section)) Column(section).FitHeight();
    }

    public void SetVisible(DockSection section, bool visible)
    {
        if (section.State.Visible == visible) return;
        section.State = section.State with { Visible = visible };
        if (visible)
        {
            section.State = section.State with { Collapsed = false };
            MarkActive(section);
        }
        Rebuild();
        LayoutChanged?.Invoke();
    }

    // Legacy workspace preference, still applied to secondary panels the person has not explicitly placed.
    public void SetTabbed(bool value)
    {
        if (Tabbed == value) return;
        Tabbed = value;
        Rebuild();
    }

    public Dictionary<string, DockPanelState> CaptureLayout() => sections.ToDictionary(section => section.Title, section => section.State);

    public void RestoreLayout(IReadOnlyDictionary<string, DockPanelState> states, bool persist = true)
    {
        foreach (var section in sections)
            if (states.TryGetValue(section.Title, out var state) && state != null)
                section.State = state with { Height = double.IsFinite(state.Height) ? Math.Clamp(state.Height, 90, 600) : 220 };
        Rebuild();
        if (persist) LayoutChanged?.Invoke();
    }

    /// <summary>Moves a panel into a stack or next to the given column. An existing tab group stays together.</summary>
    public void Move(string title, bool onLeft, DockSection? before, int? targetColumn = null, bool insertColumn = false)
    {
        var moved = Section(title);
        if (before == moved) return;
        var column = targetColumn ?? before?.State.Column ?? 0;
        var order = insertColumn ? [] : ColumnPanels(onLeft, column).Where(panel => panel != moved).ToList();
        var key = before == null ? (Group: (Guid?)null, Solo: (string?)null) : GroupKey(before);
        int index = before == null ? -1 : order.FindIndex(panel => GroupKey(panel) == key);
        if (insertColumn)
            foreach (var panel in Ordered(onLeft).Where(panel => panel != moved && panel.State.Column >= column))
                panel.State = panel.State with { Column = panel.State.Column + 1 };
        var weight = order.FirstOrDefault()?.State.ColumnWeight ?? 1;
        moved.State = moved.State with { TabGroup = null, Standalone = true, ActiveTab = true, ColumnWeight = weight };
        order.Insert(index < 0 ? order.Count : index, moved);
        ApplyOrder(order, onLeft, column);
    }

    /// <summary>Drops one panel into the receiving tab strip and activates it.</summary>
    internal void JoinTabs(string title, DockGroup group, DockSection? before)
    {
        var moved = Section(title);
        if (group.Members.Count == 1 && group.Members[0] == moved) return;
        var anchor = group.Members[0];
        var onLeft = anchor.State.Left;
        int column = anchor.State.Column;
        var groupId = anchor.State.TabGroup ?? Guid.NewGuid();
        var target = group.Members.Where(panel => panel != moved).ToList();
        if (target.Count == 0) return;
        int index = before == null ? -1 : target.IndexOf(before);
        target.Insert(index < 0 ? target.Count : index, moved);
        var order = ColumnPanels(onLeft, column).Where(panel => panel != moved).ToList();
        var first = order.FindIndex(panel => group.Members.Contains(panel));
        order.RemoveAll(panel => group.Members.Contains(panel));
        order.InsertRange(first, target);
        foreach (var panel in target)
            panel.State = panel.State with { TabGroup = groupId, Standalone = false, ActiveTab = panel == moved, ColumnWeight = anchor.State.ColumnWeight };
        moved.State = moved.State with { Collapsed = false };
        ApplyOrder(order, onLeft, column);
    }

    private IEnumerable<DockSection> ColumnPanels(bool onLeft, int column) =>
        Ordered(onLeft).Where(panel => panel.State.Column == column).GroupBy(GroupKey).SelectMany(group => group);

    private (Guid? Group, string? Solo) GroupKey(DockSection panel) =>
        panel.State.TabGroup is { } id ? (id, null)
        : Tabbed && !panel.State.Standalone && panel.Title is not ("Layers" or "Resources") ? (null, null) : (null, panel.Title);

    private DockColumn Column(DockSection section) => columns[(section.State.Left, section.State.Column)];

    private void MarkActive(DockSection section)
    {
        var key = GroupKey(section);
        foreach (var panel in Ordered(section.State.Left).Where(panel => panel.State.Column == section.State.Column && GroupKey(panel) == key))
            panel.State = panel.State with { ActiveTab = panel == section };
    }

    private void ApplyOrder(List<DockSection> order, bool onLeft, int column)
    {
        for (int i = 0; i < order.Count; i++)
            order[i].State = order[i].State with { Left = onLeft, Column = column, Order = i };
        NormalizeColumns(false);
        NormalizeColumns(true);
        Rebuild();
        LayoutChanged?.Invoke();
    }

    private void NormalizeColumns(bool left)
    {
        var ordered = Ordered(left).GroupBy(panel => panel.State.Column).OrderBy(group => group.Key).ToArray();
        for (int i = 0; i < ordered.Length; i++)
            foreach (var panel in ordered[i])
                panel.State = panel.State with { Column = i };
    }

    private void ChangedPresentation()
    {
        PresentationChanged?.Invoke();
        LayoutChanged?.Invoke();
    }

    private void Rebuild()
    {
        foreach (var group in groups.Values.Distinct()) group.Release();
        groups.Clear();
        columns.Clear();
        LeftHost.Child = null;
        rightHost.Child = null;
        foreach (var section in sections) section.Frame = null;
        LeftHost.Child = BuildSide(true);
        rightHost.Child = BuildSide(false);
        UpdateWidths();
        PresentationChanged?.Invoke();
    }

    private Grid BuildSide(bool onLeft)
    {
        var host = new Grid();
        foreach (var columnPanels in Ordered(onLeft).GroupBy(panel => panel.State.Column).OrderBy(group => group.Key))
        {
            var panelGroups = new List<DockGroup>();
            foreach (var members in columnPanels.GroupBy(GroupKey))
            {
                if (!members.Any(panel => panel.State.Visible)) continue;
                var group = new DockGroup(this, members.ToArray(), dragHeader);
                panelGroups.Add(group);
                foreach (var panel in members) groups.Add(panel, group);
            }
            if (panelGroups.Count == 0) continue;
            var column = new DockColumn(panelGroups.ToArray(), () => LayoutChanged?.Invoke());
            columns[(onLeft, columnPanels.Key)] = column;
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
                    var widths = Columns(onLeft).Select(item => (item.Index, Width: item.View.Bounds.Width)).ToArray();
                    var mean = widths.Average(item => item.Width);
                    if (mean <= 0) return;
                    foreach (var item in widths)
                        foreach (var section in Ordered(onLeft).Where(panel => panel.State.Column == item.Index))
                            section.State = section.State with { ColumnWeight = item.Width / mean };
                    LayoutChanged?.Invoke();
                };
                var rule = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Center, Background = Palette.Divider, IsHitTestVisible = false };
                Grid.SetColumn(rule, host.ColumnDefinitions.Count - 1); host.Children.Add(rule);
            }
            var weight = columnPanels.First().State.ColumnWeight;
            host.ColumnDefinitions.Add(new ColumnDefinition(double.IsFinite(weight) ? Math.Clamp(weight, .2, 5) : 1, GridUnitType.Star));
            Grid.SetColumn(column, host.ColumnDefinitions.Count - 1);
            host.Children.Add(column);
        }
        return host;
    }

    internal static double ColumnWidth(double available, int count) =>
        Math.Min(296, Math.Max(100, (available - 250) / Math.Max(1, count)));

    internal void UpdateWidths()
    {
        int left = Columns(true).Count(), right = Columns(false).Count();
        var available = TopLevel.GetTopLevel(this)?.ClientSize.Width ?? 1440;
        double width = ColumnWidth(available, left + right);
        LeftHost.Width = left * width;
        Width = right * width;
    }
}
