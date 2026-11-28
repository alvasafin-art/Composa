using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Composa.IO;

namespace Composa.App.Controls;

internal sealed partial class ResourceLibraryPanel
{
    private bool organizing;
    private Button? highlightedDrop;
    private readonly Border saveDrop = new()
    {
        IsVisible = false, IsHitTestVisible = false, Margin = new Thickness(7, 0, 7, 7),
        BorderBrush = Palette.Accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
        Background = new SolidColorBrush(Color.FromArgb(55, 65, 155, 255)),
        Child = Ui.Label("", Palette.Foreground, 12)
    };

    private void SetupLibraryDrop(Panel contents)
    {
        if (saveDrop.Child is TextBlock tip)
        {
            tip.Text = Locale.T("Drop to save to the library");
            tip.Margin = new Thickness(6, 6, 6, 0);
            tip.VerticalAlignment = VerticalAlignment.Top;
        }
        contents.Children.Add(saveDrop);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            ClearDropCue();
            if (AcceptLayerDrop?.Invoke(e.DataTransfer) == true)
            {
                e.DragEffects = DragDropEffects.Copy;
                saveDrop.IsVisible = true;
                empty.IsVisible = false;
            }
            else if (DragDestination(e) is { } destination)
            {
                e.DragEffects = DragDropEffects.Move;
                highlightedDrop = destination.Hit;
                if (highlightedDrop != null)
                {
                    highlightedDrop.BorderBrush = Palette.Accent;
                    highlightedDrop.Background = Palette.Selected;
                }
            }
            else e.DragEffects = DragDropEffects.None;
            e.Handled = true;
        });
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropCue());
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            ClearDropCue();
            if (AcceptLayerDrop?.Invoke(e.DataTransfer) == true)
            {
                e.DragEffects = DragDropEffects.Copy;
                SaveRequested?.Invoke();
            }
            else if (DragDestination(e) is { } destination)
            {
                e.DragEffects = DragDropEffects.Move;
                _ = Organize(layout => Move(layout, destination.Id, destination.Target, destination.Into, destination.After), destination.Id);
            }
            else e.DragEffects = DragDropEffects.None;
            e.Handled = true;
        });
    }

    private void ClearDropCue()
    {
        if (saveDrop.IsVisible)
        {
            saveDrop.IsVisible = false;
            empty.IsVisible = filtered.Length == 0;
        }
        if (highlightedDrop != null)
        {
            highlightedDrop = null;
            RefreshSelection();
        }
    }

    private (Guid Id, Guid? Target, bool Into, bool After, Button? Hit)? DragDestination(DragEventArgs e)
    {
        if (organizing || !Guid.TryParseExact(e.DataTransfer.TryGetValue(DragFormat), "N", out var id) ||
            !organization.Order.Contains(id) && !organization.Groups.Any(group => group.Resources.Contains(id))) return null;
        var button = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>()
            .FirstOrDefault(item => item.Tag is LibraryEntry or LibraryGroup);
        var item = button?.Tag;
        var target = item switch { LibraryEntry entry => (Guid?)entry.Id, LibraryGroup group => group.Id, _ => null };
        if (id == target) return null;
        var sourceIsGroup = organization.Groups.Any(group => group.Id == id);
        if (sourceIsGroup && target != null && !organization.Order.Contains(target.Value)) return null;
        var fraction = button == null ? 1 : e.GetPosition(button).Y / Math.Max(1, button.Bounds.Height);
        return (id, target, !sourceIsGroup && item is LibraryGroup && fraction is >= .25 and <= .75, fraction >= .5, button);
    }

    private static LibraryOrganization Move(LibraryOrganization layout, Guid id, Guid? target, bool into, bool after)
    {
        if (target == id) return layout;
        var sourceGroup = layout.Groups.FirstOrDefault(group => group.Resources.Contains(id));
        if (sourceGroup == null && !layout.Order.Contains(id)) return layout;
        var targetGroup = layout.Groups.FirstOrDefault(group => into ? group.Id == target : group.Resources.Contains(target ?? Guid.Empty));
        if (targetGroup != null && layout.Groups.Any(group => group.Id == id)) return layout;

        Guid[] Insert(Guid[] order)
        {
            var result = order.Where(item => item != id).ToList();
            var index = target is { } neighbor ? result.IndexOf(neighbor) : -1;
            result.Insert(index < 0 ? result.Count : index + (after ? 1 : 0), id);
            return result.ToArray();
        }

        return layout with
        {
            Order = targetGroup == null ? Insert(layout.Order) : layout.Order.Where(item => item != id).ToArray(),
            Groups = layout.Groups.Select(group => group == targetGroup
                ? group with { Resources = Insert(group.Resources), Collapsed = false }
                : group == sourceGroup ? group with { Resources = group.Resources.Where(item => item != id).ToArray() } : group).ToArray()
        };
    }

    private Button GroupRow(LibraryGroup group)
    {
        var glyph = Icons.Create(Icons.ChevronDown, 10, Palette.Secondary);
        if (group.Collapsed) glyph.RenderTransform = new RotateTransform(-90);
        var text = Ui.Label(group.Name, weight: FontWeight.SemiBold, size: 12);
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        var count = Ui.Label(group.Resources.Length.ToString(), Palette.Secondary, 11);
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 7 };
        line.Children.Add(Ui.Row(5, glyph, Icons.Create(Icons.Folder, 14)));
        Grid.SetColumn(text, 1); line.Children.Add(text);
        Grid.SetColumn(count, 2); line.Children.Add(count);
        var button = new Button { Name = "LibraryGroup", Tag = group, Content = line, MinHeight = 30, MinWidth = 0,
            Padding = new Thickness(6, 1), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderThickness = new Thickness(1) };
        AutomationProperties.SetName(button, group.Name + " · " + Locale.T(group.Collapsed ? "Collapsed" : "Expanded"));
        button.Click += (_, _) => { if (selectedIds.Count == 1) _ = ToggleGroup(group.Id); };
        button.ContextMenu = new ContextMenu();
        AddOrganizationMenu(button);
        EnableResourceDrag(button, group.Id, DragDropEffects.Move);
        button.AttachedToVisualTree += (_, _) => { realized[group.Id] = button; RefreshSelection(); };
        button.DetachedFromVisualTree += (_, _) => { if (realized.GetValueOrDefault(group.Id) == button) realized.Remove(group.Id); };
        return button;
    }

    private void EnableResourceDrag(Button button, Guid id, DragDropEffects effects)
    {
        PointerPressedEventArgs? press = null;
        Point origin = default;
        button.AddHandler(PointerPressedEvent, (_, e) =>
        {
            var properties = e.GetCurrentPoint(button).Properties;
            if (properties.IsRightButtonPressed)
            {
                if (!selectedIds.Contains(id)) Select(id);
                return;
            }
            if (!properties.IsLeftButtonPressed) return;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && selected is { } anchor)
            {
                int from = Array.FindIndex(filtered, row => row.Id == anchor);
                int to = Array.FindIndex(filtered, row => row.Id == id);
                if (from >= 0 && to >= 0)
                    for (int index = Math.Min(from,to); index <= Math.Max(from,to); index++)
                        selectedIds.Add(filtered[index].Id);
            }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (!selectedIds.Add(id)) selectedIds.Remove(id);
                selected = id;
            }
            else Select(id);
            RefreshSelection();
            press = e;
            origin = e.GetPosition(button);
        }, RoutingStrategies.Tunnel);
        button.PointerMoved += async (_, e) =>
        {
            if (press is not { } start || !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
            var point = e.GetPosition(button);
            if (Math.Abs(point.X - origin.X) + Math.Abs(point.Y - origin.Y) < 7) return;
            press = null;
            await DragDrop.DoDragDropAsync(start, DragData(id), effects);
            ClearDropCue();
        };
        button.AddHandler(PointerReleasedEvent, (_, _) => press = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        button.PointerCaptureLost += (_, _) => press = null;
    }

    private void AddOrganizationMenu(Button button)
    {
        var menu = button.ContextMenu!;
        if (menu.Items.Count != 0) menu.Items.Add(new Separator());
        void Command(string title, string? gesture, Func<Task> perform, Func<bool> enabled)
        {
            var item = new MenuItem { Header = Locale.T(title), InputGesture = gesture == null ? null : KeyGesture.Parse(gesture) };
            void Update() => item.IsEnabled = !organizing && enabled();
            menu.Opening += (_, _) => Update();
            menu.Opened += (_, _) => Update();
            item.Click += async (_, _) => await perform();
            menu.Items.Add(item);
        }
        Command("Group resources", "Ctrl+G", GroupResources, CanGroup);
        Command("Ungroup resources", "Ctrl+Shift+G", UngroupResources, () => SelectedGroup() != null);
        Command("Move resource up", null, () => MoveSelected(-1), () => Neighbor(-1) != null);
        Command("Move resource down", null, () => MoveSelected(1), () => Neighbor(1) != null);
    }

    private bool CanGroup() => selectedIds.Count > 1 && selectedIds.All(id =>
        organization.Order.Contains(id) && entries.Any(entry => entry.Id == id));

    private LibraryGroup? SelectedGroup() => selectedIds.Count == 1
        ? organization.Groups.FirstOrDefault(group => selectedIds.Contains(group.Id)) : null;

    private Task GroupResources()
    {
        if (!CanGroup()) return Task.CompletedTask;
        var chosen = selectedIds.ToHashSet();
        var id = Guid.NewGuid();
        return Organize(layout =>
        {
            var resources = layout.Order.Where(chosen.Contains).ToArray();
            if (resources.Length < 2) return layout;
            var order = layout.Order.ToList();
            var at = order.IndexOf(resources[0]);
            order.RemoveAll(chosen.Contains);
            order.Insert(at, id);
            var baseName = Locale.T("Resource group");
            var name = baseName;
            for (int number = 2; layout.Groups.Any(group => group.Name == name); number++) name = $"{baseName} {number}";
            return new(order.ToArray(), [.. layout.Groups, new(id, name, false, resources)]);
        }, id, focus: true);
    }

    private Task UngroupResources()
    {
        if (SelectedGroup() is not { } group) return Task.CompletedTask;
        return Organize(layout => layout with
        {
            Order = layout.Order.SelectMany(id => id == group.Id
                ? layout.Groups.FirstOrDefault(item => item.Id == id)?.Resources ?? [] : [id]).ToArray(),
            Groups = layout.Groups.Where(item => item.Id != group.Id).ToArray()
        }, group.Resources.FirstOrDefault(), focus: true);
    }

    private Task ToggleGroup(Guid id) => Organize(layout => layout with
    {
        Groups = layout.Groups.Select(group => group.Id == id ? group with { Collapsed = !group.Collapsed } : group).ToArray()
    }, id, focus: true);

    private Guid? Neighbor(int direction)
    {
        if (selectedIds.Count != 1 || selected is not { } id) return null;
        var order = organization.Groups.FirstOrDefault(group => group.Resources.Contains(id))?.Resources ?? organization.Order;
        int index = Array.IndexOf(order, id);
        var adjacent = index + direction;
        return index >= 0 && adjacent >= 0 && adjacent < order.Length ? order[adjacent] : null;
    }

    private Task MoveSelected(int direction) => selected is { } id && Neighbor(direction) is { } neighbor
        ? Organize(layout => Move(layout, id, neighbor, false, direction > 0), id, focus: true) : Task.CompletedTask;

    private async Task Organize(Func<LibraryOrganization, LibraryOrganization> change, Guid id, bool focus = false)
    {
        if (organizing) return;
        organizing = true;
        try
        {
            organization = await Task.Run(() => library.Organize(change));
            Select(id);
            Filter();
            if (focus && Array.FindIndex(filtered, row => row.Id == id) is var index && index >= 0) FocusEntry(index);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Problem?.Invoke(Locale.Message($"Could not save library organization: {error.Message}")); }
        finally { organizing = false; }
    }
}
