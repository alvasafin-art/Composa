using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.Editing;
using Composa.IO;

namespace Composa.App.Controls;

internal sealed class ResourceLibraryPanel : UserControl
{
    private const int ColumnCount = 2;
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
    private readonly Button save;
    private readonly SemaphoreSlim thumbnails = new(2);
    private readonly Dictionary<Guid, Button> realized = [];
    private LibraryEntry[] entries = [];
    private LibraryEntry[] filtered = [];
    private Guid? selected;
    private int refreshGeneration, unreadable;
    private bool loaded, itemsReady;
    private string? catalogError;

    internal event Action<LibraryEntry>? InsertRequested, RenameRequested, ReplaceRequested, DeleteRequested;
    internal event Action? SaveRequested, FolderRequested;
    internal bool CanSave { get => save.IsEnabled; set => save.IsEnabled = value; }
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
        var query = new Grid { Margin = new Thickness(10, 4, 10, 7), Children = { search } };
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
        cards.ItemTemplate = new FuncDataTemplate<LibraryEntry[]>((row, _) =>
        {
            if (row == null) return null;
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8,
                Margin = new Thickness(10, 0, 10, 6)
            };
            for (int i = 0; i < row.Length; i++)
            {
                var tile = Tile(row[i]);
                Grid.SetColumn(tile, i);
                grid.Children.Add(tile);
            }
            return grid;
        });
        var scroll = new ScrollViewer
        {
            Content = cards, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        empty.Margin = new Thickness(10, 8, 10, 0);
        empty.TextWrapping = TextWrapping.Wrap;
        var contents = new Panel { Children = { scroll, empty } };

        save = FooterButton("Save to Library…", "SaveLibraryElement", () => SaveRequested?.Invoke());
        var folder = FooterButton("Open Library Folder", "OpenLibraryFolder", () => FolderRequested?.Invoke());
        folder.Classes.Add("flat");
        folder.Foreground = Palette.Accent;
        folder.HorizontalContentAlignment = HorizontalAlignment.Left;
        folder.Padding = new Thickness(0, 3);
        var footer = Ui.Column(3, save, folder);
        footer.Margin = new Thickness(10, 5, 10, 5);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Children = { query } };
        Grid.SetRow(contents, 1); layout.Children.Add(contents);
        Grid.SetRow(footer, 2); layout.Children.Add(footer);
        Content = layout;
        AddHandler(KeyDownEvent, OnLibraryKey, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) => _ = Refresh();
        DetachedFromVisualTree += (_, _) => { ++refreshGeneration; itemsReady = false; };
    }

    private static Button FooterButton(string label, string name, Action action)
    {
        var button = Ui.TextButton(label, action);
        button.Name = name;
        button.MinWidth = 0;
        button.MinHeight = 27;
        button.FontSize = 12;
        button.Padding = new Thickness(5, 3);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.CornerRadius = new CornerRadius(5);
        return button;
    }

    internal async Task Refresh(Guid? select = null)
    {
        if (select != null) selected = select;
        if (!this.IsAttachedToVisualTree()) return;
        if (!itemsReady)
        {
            filtered = [];
            cards.ItemsSource = null;
        }
        int generation = ++refreshGeneration;
        try
        {
            var result = await Task.Run(library.List);
            if (generation != refreshGeneration) return;
            catalogError = null;
            entries = result.Entries;
            unreadable = result.Unreadable;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
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
        var matches = entries.Where(entry => query.Length == 0 || entry.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        if (!matches.Select(entry => entry.Revision).SequenceEqual(filtered.Select(entry => entry.Revision)))
        {
            filtered = matches;
            cards.ItemsSource = filtered.Chunk(ColumnCount).ToArray();
        }
        if (!filtered.Any(entry => entry.Id == selected)) selected = filtered.FirstOrDefault()?.Id;
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
            Height = 80, Background = ThumbnailMatte, CornerRadius = new CornerRadius(4), ClipToBounds = true,
            Child = new Panel { Children = { fallback, image } }
        };
        var title = Ui.Label(entry.Name, size: 12);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var tile = new Button
        {
            Name = "LibraryResource", Tag = entry, Content = Ui.Column(4, preview, title),
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

        PointerPressedEventArgs? press = null;
        Point origin = default;
        tile.AddHandler(PointerPressedEvent, (_, e) =>
        {
            Select(entry.Id);
            if (!e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed) return;
            press = e;
            origin = e.GetPosition(tile);
        }, RoutingStrategies.Tunnel);
        tile.PointerMoved += async (_, e) =>
        {
            if (press is not { } start || !e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed) return;
            var point = e.GetPosition(tile);
            if (Math.Abs(point.X - origin.X) + Math.Abs(point.Y - origin.Y) < 7) return;
            press = null;
            await DragDrop.DoDragDropAsync(start, DragData(entry.Id), DragDropEffects.Copy);
        };
        tile.AddHandler(PointerReleasedEvent, (_, _) => press = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        tile.PointerCaptureLost += (_, _) => press = null;
        tile.DoubleTapped += (_, e) => { InsertRequested?.Invoke(entry); e.Handled = true; };
        tile.GotFocus += (_, _) => Select(entry.Id);

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
        if (selected == id) return;
        selected = id;
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        foreach (var (id, tile) in realized)
        {
            tile.Background = id == selected ? Palette.Selected : Palette.PanelRaised;
            tile.BorderBrush = id == selected ? Palette.Accent : Palette.Edge;
        }
    }

    internal bool OwnsLibraryKey(object? focused, KeyEventArgs e) =>
        e.KeyModifiers == KeyModifiers.None && (ReferenceEquals(focused, search) && e.Key is Key.Enter or Key.Escape or Key.Down ||
            focused is Control control && control.FindAncestorOfType<ResourceLibraryPanel>() == this && control.Tag is LibraryEntry &&
            e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.Enter or Key.Space or Key.Delete);

    private void OnLibraryKey(object? sender, KeyEventArgs e)
    {
        if (!OwnsLibraryKey(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement(), e)) return;
        e.Handled = true;
        if (search.IsFocused && e.Key == Key.Escape) { search.Text = ""; return; }
        if (filtered.Length == 0) return;
        var index = Array.FindIndex(filtered, entry => entry.Id == selected);
        if (search.IsFocused) { FocusEntry(Math.Max(0, index)); return; }
        if (index < 0) return;
        if (e.Key is Key.Enter or Key.Space) InsertRequested?.Invoke(filtered[index]);
        else if (e.Key == Key.Delete) DeleteRequested?.Invoke(filtered[index]);
        else FocusEntry(Math.Clamp(e.Key switch
        {
            Key.Left => index - 1, Key.Right => index + 1, Key.Up => index - ColumnCount, Key.Down => index + ColumnCount,
            Key.Home => 0, Key.End => filtered.Length - 1, _ => index
        }, 0, filtered.Length - 1));
    }

    private void FocusEntry(int index)
    {
        var id = filtered[index].Id;
        Select(id);
        cards.ScrollIntoView(index / ColumnCount);
        Dispatcher.UIThread.Post(() => realized.GetValueOrDefault(id)?.Focus(), DispatcherPriority.Loaded);
    }
}
