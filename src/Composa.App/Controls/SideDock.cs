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
public sealed class SideDock : UserControl
{
    private const double MinTopHeight = 252;

    private readonly Control top;
    private readonly IReadOnlyList<DockSection> sections;
    private readonly Grid grid = new();
    private readonly DockPanel frame = new();
    private readonly ScrollViewer sectionScroll = new()
    {
        Name = "DockSectionScroll",
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
    };

    public SideDock(Control top, params DockSection[] sections)
    {
        this.top = top;
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

    public DockSection Section(string title) => sections.Single(s => s.Title == title);

    public Dictionary<string, DockPanelState> CaptureLayout() => sections.ToDictionary(section => section.Title, section => section.State);

    public void RestoreLayout(IReadOnlyDictionary<string, DockPanelState> states)
    {
        foreach (var section in sections)
            if (states.TryGetValue(section.Title, out var state) && state != null)
                section.State = state with { Height = double.IsFinite(state.Height) ? Math.Clamp(state.Height, DockSection.MinHeight, 600) : 220 };
        Layout();
        foreach (var section in sections) section.RaiseChanged();
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
                return new TabItem { Header = section.Title, Content = host };
            }).ToArray() };
            tabs.SelectedIndex = Math.Max(0, Array.FindIndex(visible, s => s.Title == activeTab));
            if (tabs.SelectedItem is TabItem { Header: string selected }) activeTab = selected;
            tabs.SelectionChanged += (_, e) =>
            {
                if (ReferenceEquals(e.Source, tabs) && tabs.SelectedItem is TabItem { Header: string name } && name != activeTab)
                { activeTab = name; PresentationChanged?.Invoke(); }
            };
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(TabHeight())));
            Grid.SetRow(tabs, 1); grid.Children.Add(tabs);
            FitHeight();
            PresentationChanged?.Invoke();
            return;
        }
        foreach (var section in visible)
        {
            var open = !section.State.Collapsed;
            // A collapsed section is only its header, so there is nothing to size and the splitter would only mislead.
            var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Height = 5, Background = Palette.Panel, IsEnabled = open };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(splitter, grid.RowDefinitions.Count - 1);
            grid.Children.Add(splitter);

            var row = new RowDefinition(open ? new GridLength(section.ExpandedHeight) : GridLength.Auto) { MinHeight = open ? DockSection.MinHeight : 0 };
            section.Row = row;
            grid.RowDefinitions.Add(row);
            var panel = open && section.HasOwnHeader ? section.Content : Build(section, open);
            Grid.SetRow(panel, grid.RowDefinitions.Count - 1);
            grid.Children.Add(panel);
            splitter.DragCompleted += (_, _) =>
            {
                section.State = section.State with { Height = Math.Max(DockSection.MinHeight, Math.Round(row.ActualHeight) - section.ExtraHeight) };
                section.RaiseChanged();
            };
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
        if (Tabbed && grid.RowDefinitions.Count == 2)
        {
            grid.RowDefinitions[1].Height = new GridLength(TabHeight());
            desired = MinTopHeight + TabHeight();
        }
        var compact = desired > Bounds.Height;
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
        top.Height = compact ? Math.Min(MinTopHeight, Bounds.Height * .54) : double.NaN;
        grid.RowDefinitions[0].MinHeight = compact ? 0 : MinTopHeight;
        grid.RowDefinitions[0].Height = compact ? new GridLength(0) : GridLength.Star;
        grid.Height = compact ? desired - MinTopHeight : Bounds.Height;
    }

    private Control Build(DockSection section, bool open)
    {
        var chevron = Icons.Create(open ? Icons.ChevronDown : Icons.ChevronRight, 12, Palette.Secondary);
        var title = Ui.Label(section.Title, weight: FontWeight.SemiBold);
        var header = new Button
        {
            Classes = { "flat" }, HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent, Padding = new Thickness(8, 6, 10, 6),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { chevron, title } }
        };
        var label = (open ? "Collapse " : "Expand ") + section.Title;
        ToolTip.SetTip(header, label);
        Avalonia.Automation.AutomationProperties.SetName(header, label);
        header.Click += (_, _) => SetCollapsed(section, open);
        var panel = new DockPanel { Background = Palette.Panel };
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        if (open) panel.Children.Add(section.Content);
        return panel;
    }
}
