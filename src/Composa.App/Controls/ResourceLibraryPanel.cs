using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.Editing;
using Composa.IO;

namespace Composa.App.Controls;

internal sealed partial class ResourceLibraryPanel : UserControl
{
    internal static readonly IBrush ThumbnailMatte = new SolidColorBrush(Color.FromRgb(206, 204, 198));
    private static readonly DataFormat<string> DragFormat = DataFormat.CreateStringApplicationFormat("composa-library-element");
    private static readonly Lazy<HashSet<string>> InstalledFonts = new(() => EditorSession.FontFamilies.ToHashSet(StringComparer.OrdinalIgnoreCase));
    private readonly ElementLibrary library;
    private readonly TextBox search = new()
    {
        Name = "SearchLibrary", PlaceholderText = Locale.T("Find resources"), Height = 28,
        MinHeight = 28, MinWidth = 0, FontSize = 12, Padding = new Thickness(8, 0),
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly ItemsControl cards = new() { Name = "LibraryCards" };
    private readonly TextBlock empty = Ui.Label("", Palette.Secondary, 12);
    private readonly SemaphoreSlim thumbnails = new(2);
    private readonly Dictionary<Guid, Button> realized = [];
    private LibraryEntry[] entries = [];
    private LibraryRow[] filtered = [];
    private LibraryOrganization organization = LibraryOrganization.Empty;
    private readonly HashSet<Guid> selectedIds = [];
    private sealed record LibraryRow(Guid Id, LibraryEntry? Entry, LibraryGroup? Group, bool Nested);
    private Guid? selected;
    private int refreshGeneration, unreadable;
    private bool loaded, itemsReady;
    private string? catalogError;

    internal event Action<LibraryEntry>? InsertRequested, RenameRequested, ReplaceRequested, DeleteRequested;
    internal event Action? SaveRequested, FolderRequested;
    internal event Action<string>? Problem;
    internal bool CanSave { get; set; }
    internal Func<IDataTransfer, bool>? AcceptLayerDrop { get; set; }
    internal LibraryEntry? DraggedEntry(IDataTransfer data) =>
        Guid.TryParseExact(data.TryGetValue(DragFormat), "N", out var id) ? entries.FirstOrDefault(entry => entry.Id == id) : null;
    internal static string[] MissingFonts(IEnumerable<string> fonts) => fonts.Where(font => !InstalledFonts.Value.Contains(font)).ToArray();

    internal static DataTransfer DragData(Guid id)
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(DragFormat, id.ToString("N")));
        return data;
    }

    public ResourceLibraryPanel(ElementLibrary library)
    {
        this.library = library;
        Name = "ResourceLibrary";
        var query = new Grid { Children = { search } };
        var clear = Ui.IconButton(Icons.Close, "Clear resource search", () => search.Text = "", 11);
        clear.IsVisible = false;
        clear.Width = 24;
        clear.Height = 28;
        clear.Padding = new Thickness(0);
        clear.HorizontalAlignment = HorizontalAlignment.Right;
        clear.HorizontalContentAlignment = HorizontalAlignment.Center;
        query.Children.Add(clear);
        search.TextChanged += (_, _) =>
        {
            clear.IsVisible = !string.IsNullOrEmpty(search.Text);
            search.Padding = new Thickness(8, 0, clear.IsVisible ? 24 : 8, 0);
            Filter();
        };

        cards.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel { CacheLength = 0 });
        cards.ItemTemplate = new FuncDataTemplate<LibraryRow>((row, _) =>
        {
            if (row == null) return null;
            var button = row.Entry is { } entry ? Tile(entry) : GroupRow(row.Group!);
            button.Margin = new Thickness(row.Nested ? 26 : 10, 0, 10, 2);
            return button;
        });
        var scroll = new ScrollViewer
        {
            Content = cards, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        empty.Margin = new Thickness(10, 2, 10, 0);
        empty.VerticalAlignment = VerticalAlignment.Top;
        empty.TextWrapping = TextWrapping.Wrap;
        var contents = new Panel { Children = { scroll, empty } };

        var folder = Ui.IconButton(Icons.Folder, "Open Library Folder", () => FolderRequested?.Invoke(), 15);
        folder.Name = "OpenLibraryFolder";
        folder.Classes.Remove("flat");
        folder.Width = folder.MinWidth = 30;
        folder.Height = 28;
        folder.Padding = default;
        folder.HorizontalContentAlignment = HorizontalAlignment.Center;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6,
            Margin = new Thickness(10, 4, 10, 7), Children = { query, folder } };
        Grid.SetColumn(folder, 1);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { header } };
        Grid.SetRow(contents, 1); layout.Children.Add(contents);
        Content = layout;
        SetupLibraryDrop(contents);
        AttachedToVisualTree += (_, _) => _ = Refresh();
        DetachedFromVisualTree += (_, _) => { ++refreshGeneration; itemsReady = false; };
    }

    internal async Task Refresh(Guid? select = null)
    {
        if (select is { } id) Select(id);
        if (!this.IsAttachedToVisualTree()) return;
        if (!itemsReady)
        {
            filtered = [];
            cards.ItemsSource = null;
        }
        int generation = ++refreshGeneration;
        try
        {
            var result = await Task.Run(() => (Catalog: library.List(), Order: library.ReadOrganization()));
            if (generation != refreshGeneration) return;
            catalogError = null;
            entries = result.Catalog.Entries;
            unreadable = result.Catalog.Unreadable;
            organization = result.Order.WithEntries(entries);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            if (generation != refreshGeneration) return;
            catalogError = Locale.Message($"Could not read the library: {error.Message}");
            entries = [];
        }
        loaded = true;
        itemsReady = true;
        Filter();
    }

    private void Filter()
    {
        var query = search.Text?.Trim() ?? "";
        bool Matches(string value) => value.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        var index = entries.ToDictionary(entry => entry.Id);
        var groups = organization.Groups.ToDictionary(group => group.Id);
        var matches = new List<LibraryRow>();
        foreach (var id in organization.Order)
        {
            if (index.TryGetValue(id, out var entry) && Matches(entry.Name))
                matches.Add(new(id, entry, null, false));
            else if (groups.TryGetValue(id, out var group))
            {
                var children = group.Resources.Where(index.ContainsKey).Select(child => index[child])
                    .Where(child => Matches(group.Name) || Matches(child.Name)).ToArray();
                if (children.Length == 0 && !Matches(group.Name)) continue;
                matches.Add(new(id, null, group, false));
                if (!group.Collapsed || query.Length > 0)
                    matches.AddRange(children.Select(child => new LibraryRow(child.Id, child, null, true)));
            }
        }
        if (!matches.SequenceEqual(filtered))
        {
            filtered = matches.ToArray();
            cards.ItemsSource = filtered;
        }
        selectedIds.RemoveWhere(id => !index.ContainsKey(id) && !groups.ContainsKey(id));
        if (!filtered.Any(row => row.Id == selected))
        {
            selected = filtered.FirstOrDefault(row => selectedIds.Contains(row.Id))?.Id ?? filtered.FirstOrDefault()?.Id;
            if (selectedIds.Count == 0 && selected is { } first) selectedIds.Add(first);
        }
        RefreshSelection();
        empty.IsVisible = filtered.Length == 0;
        empty.Text = catalogError ?? Locale.T(!loaded ? "Loading resources…" :
            entries.Length > 0 ? "No matching resources." : "No resources saved yet.");
        if (unreadable > 0)
        {
            var warning = Locale.Message($"{unreadable} library entries could not be read.");
            if (empty.IsVisible && catalogError == null) empty.Text = warning;
            Locale.Tip(search, warning);
        }
        else Locale.Tip(search, null);
    }

    private Button Tile(LibraryEntry entry)
    {
        var fallback = Icons.Create(Icons.Image, 18, Palette.Secondary);
        fallback.HorizontalAlignment = HorizontalAlignment.Center;
        fallback.VerticalAlignment = VerticalAlignment.Center;
        var image = new Image { Stretch = Stretch.Uniform, IsHitTestVisible = false };
        var preview = new Border
        {
            Width = 38, Height = 29, Background = ThumbnailMatte, CornerRadius = new CornerRadius(3), ClipToBounds = true,
            Child = new Panel { Children = { fallback, image } }
        };
        var title = Ui.Label(entry.Name, size: 12);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var tile = new Button
        {
            Name = "LibraryResource", Tag = entry, Content = Ui.Row(8, preview, title),
            MinWidth = 0, Padding = new Thickness(5), CornerRadius = new CornerRadius(5),
            BorderBrush = Palette.Edge, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(tile, entry.Name);
        var missing = MissingFonts(entry.Fonts);
        var message = missing.Length > 0 ? Locale.Message($"Missing fonts: {string.Join(", ", missing)}") : "";
        if (missing.Length > 0)
        {
            var badge = new Border
            {
                Background = Palette.PanelRaised, CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 2),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4), Child = Ui.Label(Locale.T("Fonts missing"), Palette.Secondary, 10)
            };
            ((Panel)preview.Child!).Children.Add(badge);
            AutomationProperties.SetHelpText(tile, message);
        }
        var details = new List<string> { entry.Name };
        if (message.Length > 0) details.Add(message);
        if (entry.EmbeddedLinkedObjects) details.Add(Locale.T("Linked smart objects are embedded in this resource."));
        Locale.Tip(tile, string.Join("\n", details));
        tile.ContextMenu = new ContextMenu();
        void Add(string key, Action action)
        {
            var item = new MenuItem { Header = Locale.T(key) };
            item.Click += (_, _) => action();
            tile.ContextMenu.Items.Add(item);
        }
        Add("Insert", () => InsertRequested?.Invoke(entry));
        Add("Rename…", () => RenameRequested?.Invoke(entry));
        Add("Update from Selection…", () => ReplaceRequested?.Invoke(entry));
        var update = (MenuItem)tile.ContextMenu.Items[2]!;
        tile.ContextMenu.Opening += (_, _) => update.IsEnabled = CanSave;
        tile.ContextMenu.Opened += (_, _) => update.IsEnabled = CanSave;
        tile.ContextMenu.Items.Add(new Separator());
        Add("Delete", () => DeleteRequested?.Invoke(entry));
        AddOrganizationMenu(tile);
        EnableResourceDrag(tile, entry.Id, DragDropEffects.Copy | DragDropEffects.Move);
        tile.DoubleTapped += (_, e) => { InsertRequested?.Invoke(entry); e.Handled = true; };

        CancellationTokenSource? pending = null;
        bool attempted = false;
        tile.AttachedToVisualTree += (_, _) =>
        {
            realized[entry.Id] = tile;
            RefreshSelection();
        };
        tile.DetachedFromVisualTree += (_, _) =>
        {
            if (realized.GetValueOrDefault(entry.Id) == tile) realized.Remove(entry.Id);
            pending?.Cancel();
            pending = null;
            if (image.Source is Bitmap bitmap) { image.Source = null; bitmap.Dispose(); }
            fallback.IsVisible = true;
            attempted = false;
        };
        tile.EffectiveViewportChanged += async (_, e) =>
        {
            if (attempted || !itemsReady || !tile.IsEffectivelyVisible || !e.EffectiveViewport.Intersects(new Rect(tile.Bounds.Size))) return;
            attempted = true;
            using var load = new CancellationTokenSource();
            pending = load;
            try
            {
                var bytes = await ReadThumbnail(entry, load.Token);
                if (load.IsCancellationRequested || !tile.IsEffectivelyVisible) { attempted = false; return; }
                using var input = new MemoryStream(bytes, writable: false);
                image.Source = new Bitmap(input);
                fallback.IsVisible = false;
            }
            catch (OperationCanceledException) when (load.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
            { Locale.Tip(preview, Locale.T("Preview unavailable")); }
            finally { if (pending == load) pending = null; }
        };
        return tile;
    }

    private async Task<byte[]> ReadThumbnail(LibraryEntry entry, CancellationToken cancellation)
    {
        await thumbnails.WaitAsync(cancellation);
        try { return await Task.Run(() => library.LoadThumbnail(entry), cancellation); }
        finally { thumbnails.Release(); }
    }

    private void Select(Guid id)
    {
        selected = id;
        selectedIds.Clear();
        selectedIds.Add(id);
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        foreach (var (id, tile) in realized)
        {
            tile.Background = selectedIds.Contains(id) ? Palette.Selected : Brushes.Transparent;
            tile.BorderBrush = selectedIds.Contains(id) ? Palette.Accent : Brushes.Transparent;
        }
    }

    private bool lastInputHere;

    private bool ContainsInput(Visual? source) => source?.GetSelfAndVisualAncestors().Contains(this) == true;

    internal void TrackInput(Visual? source) => lastInputHere = ContainsInput(source);

    internal bool HasResourceInput(object? focused) => selectedIds.Count > 0 && IsEffectivelyVisible &&
        TopLevel.GetTopLevel(this) != null && (lastInputHere || ContainsInput(focused as Visual));

    internal bool HandleLibraryKey(object? focused, KeyEventArgs e)
    {
        if (!IsEffectivelyVisible || TopLevel.GetTopLevel(this) == null) return false;
        var inPanel = ContainsInput(focused as Visual);
        if ((inPanel || lastInputHere) && e.Key == Key.G &&
            e.KeyModifiers is KeyModifiers.Control or (KeyModifiers.Control | KeyModifiers.Shift))
        {
            e.Handled = true;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _ = UngroupResources();
            else _ = GroupResources();
            return true;
        }
        if (e.KeyModifiers != KeyModifiers.None) return false;
        bool searchKey = ReferenceEquals(focused, search) && e.Key is Key.Enter or Key.Escape or Key.Down;
        bool rowKey = focused is not TextBox && (inPanel && focused is Control { Tag: LibraryEntry or LibraryGroup } ||
            lastInputHere && selected != null) &&
            e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.Enter or Key.Space or Key.Delete;
        if (!searchKey && !rowKey) return false;
        e.Handled = true;
        if (searchKey && e.Key == Key.Escape) { search.Text = ""; return true; }
        if (filtered.Length == 0) return true;
        var index = Array.FindIndex(filtered, entry => entry.Id == selected);
        if (searchKey) { FocusEntry(Math.Max(0, index)); return true; }
        if (index < 0) return true;
        var row = filtered[index];
        if (e.Key is Key.Enter or Key.Space)
        {
            if (row.Entry is { } entry) InsertRequested?.Invoke(entry);
            else if (row.Group is { } group) _ = ToggleGroup(group.Id);
        }
        else if (e.Key == Key.Delete && row.Entry is { } entry) DeleteRequested?.Invoke(entry);
        else FocusEntry(Math.Clamp(e.Key switch
        {
            Key.Left or Key.Up => index - 1, Key.Right or Key.Down => index + 1,
            Key.Home => 0, Key.End => filtered.Length - 1, _ => index
        }, 0, filtered.Length - 1));
        return true;
    }

    private void FocusEntry(int index)
    {
        var id = filtered[index].Id;
        Select(id);
        cards.ScrollIntoView(index);
        Dispatcher.UIThread.Post(() => realized.GetValueOrDefault(id)?.Focus(), DispatcherPriority.Loaded);
    }
}
