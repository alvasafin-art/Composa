using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Composa.App.Controls;

/// <summary>One row of a dock column. The same header presents a standalone panel and a stack of tabs.</summary>
internal sealed class DockGroup : UserControl
{
    private const string SelectedClass = "dock-selected";
    private readonly SideDock dock;
    private readonly Border body = new();
    private readonly Dictionary<DockSection, Button> tabs = new();
    private readonly DockSection[] visible;
    public IReadOnlyList<DockSection> Members { get; }
    public DockSection Active { get; private set; }
    public Grid Header { get; } = new() { Name = "DockTabStrip", ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        Height = 40, Margin = new Thickness(8, 0), Background = Brushes.Transparent };
    public bool IsCompact => visible.Length == 1 && Active.CompactHeight.HasValue;
    public bool Collapsed => !IsCompact && Active.State.Collapsed;
    public bool ContainsLayers => visible.Any(section => section.Title == "Layers");
    public double PreferredHeight => IsCompact ? MinimumHeight : Math.Max(MinimumHeight, visible.Max(section => section.State.Height) + Active.ExtraHeight);
    public double MinimumHeight => IsCompact ? Active.CompactHeight!.Value : ContainsLayers ? 252 : DockSection.MinHeight;

    public DockGroup(SideDock dock, DockSection[] members, Action<Control, string> dragHeader)
    {
        this.dock = dock;
        Members = members;
        visible = members.Where(section => section.State.Visible).ToArray();
        Active = visible.FirstOrDefault(section => section.State.ActiveTab) ?? visible[0];
        if (IsCompact)
        {
            Header.Height = MinimumHeight;
            Header.Margin = default;
            Header.Children.Add(body);
            Header.Background = Palette.Panel;
            Content = Header;
            Active.Frame = this;
            AutomationProperties.SetName(Header, Locale.T(Active.Title));
            dragHeader(Header, Active.Title);
            Display(Active);
            return;
        }
        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var section in visible)
        {
            var title = Ui.Label(Locale.T(section.Title), size: 13, weight: FontWeight.SemiBold);
            title.Height = title.LineHeight = 18;
            var tab = new Button
            {
                Name = "DockPanelTab", Tag = section, Classes = { "dock-tab" },
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Height = 30, MinHeight = 0, MinWidth = 0, Padding = new Thickness(8, 0),
                BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5),
                Content = title
            };
            tab.Click += (_, _) =>
            {
                if (visible.Length == 1 && section.Title != "Layers") dock.SetCollapsed(section, !section.State.Collapsed);
                else dock.Activate(section);
            };
            tab.KeyDown += (_, e) => Navigate(section, e);
            tabs.Add(section, tab);
            strip.Children.Add(tab);
            dragHeader(tab, section.Title);
            section.Frame = this;
        }
        var stripScroll = new ScrollViewer
        {
            Content = strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false
        };
        Header.Children.Add(stripScroll);
        var more = Ui.IconButton(Icons.MoreActions, "Panel Menu", () => { }, 14);
        more.Name = "DockPanelMenu";
        more.Width = more.Height = 26;
        more.MinHeight = more.MinWidth = 0;
        more.Padding = new Thickness(0);
        more.VerticalAlignment = VerticalAlignment.Center;
        more.HorizontalContentAlignment = HorizontalAlignment.Center;
        more.VerticalContentAlignment = VerticalAlignment.Center;
        more.Click += (_, _) => OpenMenu(more);
        Grid.SetColumn(more, 1);
        Header.Children.Add(more);
        var frame = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Background = Palette.Panel, Children = { Header } };
        Grid.SetRow(body, 1); frame.Children.Add(body);
        Content = frame;
        Display(Active);
    }

    internal IEnumerable<(DockSection Section, Button Tab)> Tabs => tabs.Select(pair => (pair.Key, pair.Value));

    internal void Display(DockSection section)
    {
        Active = section;
        body.IsVisible = !Collapsed;
        body.Child = body.IsVisible ? section.Content : null;
        foreach (var (item, tab) in tabs)
        {
            var selected = visible.Length > 1 && item == section;
            tab.Classes.Set(SelectedClass, selected);
            tab.Background = selected ? Palette.ActiveTab : Brushes.Transparent;
            ((TextBlock)tab.Content!).Foreground = visible.Length == 1 || selected ? Palette.Foreground : Palette.Secondary;
            var caption = visible.Length > 1 || item.Title == "Layers" ? item.Title
                : (item.State.Collapsed ? "Expand " : "Collapse ") + item.Title;
            AutomationProperties.SetName(tab, Locale.T(caption));
            Locale.Tip(tab, caption);
        }
    }

    internal void Release() => body.Child = null;

    private void Navigate(DockSection section, KeyEventArgs e)
    {
        if (visible.Length < 2 || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
        int index = Array.IndexOf(visible, section);
        index = e.Key switch
        {
            Key.Home => 0,
            Key.End => visible.Length - 1,
            Key.Left => (index + visible.Length - 1) % visible.Length,
            _ => (index + 1) % visible.Length
        };
        ShowTab(visible[index]);
        tabs[visible[index]].Focus();
        e.Handled = true;
    }

    private void ShowTab(DockSection section)
    {
        dock.Activate(section);
        tabs[section].BringIntoView();
    }

    private void OpenMenu(Button anchor)
    {
        var menu = new ContextMenu();
        var section = Active;
        void Add(string title, Action action)
        {
            var item = new MenuItem { Header = Locale.T(title) };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        if (visible.Length > 1)
        {
            foreach (var tab in visible)
            {
                var item = new MenuItem { Header = Locale.T(tab.Title), ToggleType = MenuItemToggleType.Radio, IsChecked = tab == section };
                item.Click += (_, _) => ShowTab(tab);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
        }
        if (Members.Count > 1 || dock.Ordered(section.State.Left).Any(item => item.State.Visible && item.State.Column == section.State.Column && item != section))
            Add("Move Panel to New Column", () => dock.Move(section.Title, section.State.Left, null, section.State.Column + 1, insertColumn: true));
        Add(section.State.Collapsed ? "Expand Panel" : "Collapse Panel", () => dock.SetCollapsed(section, !section.State.Collapsed));
        Add("Close Panel", () => dock.SetVisible(section, false));
        menu.Open(anchor);
    }
}

/// <summary>Stacks groups in one column. The top group gets spare height, with splitters for the rows below it.</summary>
internal sealed class DockColumn : UserControl
{
    private readonly DockGroup[] groups;
    private readonly Grid grid = new();
    private readonly DockPanel frame = new();
    private readonly ScrollViewer scroll = new()
    {
        Name = "DockSectionScroll", HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly RowDefinition[] rows;
    private readonly GridSplitter[] splitters;
    private readonly DockGroup? footer;
    private bool fitting;

    public IReadOnlyList<DockGroup> Groups => groups;

    public DockColumn(DockGroup[] groups, Action resized)
    {
        this.groups = groups;
        footer = groups[^1].IsCompact ? groups[^1] : null;
        rows = new RowDefinition[groups.Length];
        splitters = new GridSplitter[Math.Max(0, groups.Length - 1 - (footer == null ? 0 : 1))];
        for (int i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            var row = new RowDefinition();
            if (i > 0 && group != footer)
            {
                var splitter = new GridSplitter
                {
                    Height = 5, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Rows,
                    ResizeBehavior = GridResizeBehavior.PreviousAndNext
                };
                var rule = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center, Background = Palette.Divider, IsHitTestVisible = false };
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Grid.SetRow(splitter, grid.RowDefinitions.Count - 1); grid.Children.Add(splitter);
                Grid.SetRow(rule, Grid.GetRow(splitter)); grid.Children.Add(rule);
                splitters[i - 1] = splitter;
                var above = groups[i - 1];
                var aboveRow = rows[i - 1];
                splitter.DragCompleted += (_, _) =>
                {
                    RememberHeight(above, aboveRow);
                    RememberHeight(group, row);
                    resized();
                };
            }
            rows[i] = row;
            grid.RowDefinitions.Add(row);
            if (group == footer)
            {
                DockPanel.SetDock(group, Dock.Bottom);
                frame.Children.Add(group);
            }
            else
            {
                Grid.SetRow(group, grid.RowDefinitions.Count - 1);
                grid.Children.Add(group);
            }
        }
        scroll.Content = grid;
        frame.Children.Add(scroll);
        Content = frame;
        SizeChanged += (_, _) => FitHeight();
        FitHeight();
    }

    private static void RememberHeight(DockGroup group, RowDefinition row)
    {
        if (group.IsCompact || !row.Height.IsAbsolute || row.ActualHeight <= 0) return;
        var height = Math.Clamp(Math.Round(row.ActualHeight) - group.Active.ExtraHeight, DockSection.MinHeight, 600);
        foreach (var panel in group.Members) panel.State = panel.State with { Height = height };
    }

    internal void FitHeight()
    {
        if (fitting) return;
        fitting = true;
        try
        {
            int primary = Array.FindIndex(groups, group => !group.Collapsed && !group.IsCompact);
            var top = groups[0];
            double height = Math.Max(0, Bounds.Height - (footer?.MinimumHeight ?? 0));
            double minimum = groups.Select((group, i) => group == footer ? 0 : group.Collapsed ? group.Header.Height :
                i == primary ? group.MinimumHeight : group.PreferredHeight).Sum() + splitters.Length * 5;
            bool pinned = primary == 0 && top.ContainsLayers && height > 0 && minimum > height;
            if (pinned && top.Parent == grid)
            {
                grid.Children.Remove(top);
                DockPanel.SetDock(top, Dock.Top);
                frame.Children.Insert(0, top);
            }
            else if (!pinned && top.Parent == frame)
            {
                frame.Children.Remove(top);
                grid.Children.Add(top);
            }
            top.Height = pinned ? Math.Min(top.MinimumHeight, height * .54) : double.NaN;
            for (int i = 0; i < groups.Length; i++)
            {
                var group = groups[i];
                var row = rows[i];
                if (group == footer) { row.Height = new GridLength(0); continue; }
                if (i > 0) splitters[i - 1].IsEnabled = !group.Collapsed && !groups[i - 1].Collapsed &&
                    !group.IsCompact && !groups[i - 1].IsCompact && !(pinned && i == 1);
                row.MinHeight = group.Collapsed || pinned && i == 0 ? 0 : group.MinimumHeight;
                row.Height = (pinned && i == 0) ? new GridLength(0)
                    : group.Collapsed ? GridLength.Auto
                    : i == primary ? GridLength.Star
                    : new GridLength(group.PreferredHeight);
            }
            grid.Height = pinned ? minimum - top.MinimumHeight : Math.Max(height, minimum);
        }
        finally { fitting = false; }
    }
}
