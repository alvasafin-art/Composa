using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Composa.App.Controls;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;
using System.Text.Json.Serialization;
using static Composa.App.Controls.CanvasLayoutEditor;

namespace Composa.App.Dialogs;

public sealed record NewCanvasOptions(int Width, int Height, uint? Fill = null, ColorDepth Depth = ColorDepth.Eight,
    double Resolution = 72, CanvasColorMode ColorMode = CanvasColorMode.RGB)
{
    public CanvasLayout Layout { get; init; } = CanvasLayout.Empty;
    [JsonIgnore] public SKColor? Background => Fill is { } color ? new SKColor(color) : null;
    [JsonIgnore] public bool Fits => Enum.IsDefined(Depth) && Enum.IsDefined(ColorMode) && Resolution is >= 1 and <= 9600
        && DocumentLimits.FitsSurface(Width, Height, Depth == ColorDepth.Sixteen ? 16 : 4);

    public EditorSession CreateSession()
    {
        var session = EditorSession.NewCanvas(Width, Height, Background, Depth);
        session.Document.Resolution = Resolution;
        session.Document.ColorMode = ColorMode;
        session.Document.Layout = Layout;
        return session;
    }
}

public sealed record CanvasPreset(string Name, NewCanvasOptions Options);

public static partial class CanvasDialogs
{
    public static async Task<NewCanvasOptions?> NewCanvas(Window owner, SKColor backgroundColor,
        (int W, int H)? clipboardSize = null, Settings? settings = null)
    {
        var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
        var screenWidth = screen == null ? 1024 : screen.WorkingArea.Width / screen.Scaling;
        var screenHeight = screen == null ? 800 : screen.WorkingArea.Height / screen.Scaling;
        var design = new NewCanvasView(backgroundColor, clipboardSize, settings ?? new Settings(), screenWidth < 900);
        var dialog = new DialogWindow("New Canvas", design, "Create")
        {
            SizeToContent = SizeToContent.Manual,
            Width = Math.Min(1030, screenWidth - 26),
            Height = Math.Min(768, screenHeight - 34)
        };
        var originalBody = (StackPanel)dialog.Content!;
        var createButtons = dialog.FooterButtons;
        originalBody.Children.Clear();
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Background = Palette.Window
        };
        dialog.Content = root;
        var actions = Row("1.35*,*,*");
        actions.Name = "NewCanvasActions";
        actions.Margin = new Thickness(NewCanvasView.PanelInset, 9, Ui.OverlayScrollBarGutter, 9);
        var buttons = createButtons.Children.OfType<Button>().Prepend(
            Ui.TextButton("Save preset", () => _ = design.SavePreset(dialog))).ToArray();
        createButtons.Children.Clear();
        for (var column = 0; column < buttons.Length; column++)
        {
            var button = buttons[column];
            button.Height = 32;
            button.MinWidth = 0;
            button.Padding = new Thickness(6, 0);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.VerticalContentAlignment = VerticalAlignment.Center;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            Put(actions, button, column);
        }
        var create = buttons.Single(button => button.IsDefault);
        create.ContentTemplate = new FuncDataTemplate<string>((caption, _) => Ui.Row(8, Ui.Label(caption),
            new Border { Background = Palette.ActionHover, CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0),
                Child = Ui.Label("↵", Palette.OnActive, 11) }));
        var footer = Row("*,Auto", 0);
        footer.ColumnDefinitions[1].Width = design.ColumnDefinitions[2].Width;
        design.Status.Margin = new Thickness(12, 9, 0, 9);
        Put(footer, design.Status, 0);
        Put(footer, actions, 1);
        root.Children.Add(design);
        var footerSurface = new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Palette.Divider, Child = footer };
        Grid.SetRow(footerSurface, 1);
        root.Children.Add(footerSurface);

        void Validate() => dialog.CanAccept = design.Options.Fits;
        design.Changed += Validate;
        Validate();
        dialog.ValidateAccept = () => design.Options.Fits;
        if (!await dialog.Ask(owner)) return null;
        design.RememberRecent();
        return design.Options;
    }
}

internal sealed class NewCanvasView : Grid
{
    internal const int PanelInset = 15;

    private static readonly CanvasPreset[] Digital =
    [
        Screen("Portrait card", 1080, 1350),
        Screen("Square post", 1080, 1080),
        Screen("Story / Reel", 1080, 1920),
        Screen("Ad landscape", 1200, 628),
        Screen("Web banner", 1920, 640),
        Screen("YouTube thumbnail", 3840, 2160),
        Screen("YouTube channel banner", 2560, 1440),
        Screen("Full HD", 1920, 1080),
        Screen("4K", 3840, 2160)
    ];

    private static readonly CanvasPreset[] Print =
    [
        Paper("A4", 2480, 3508), Paper("A5", 1748, 2480), Paper("A3", 3508, 4961),
        Paper("US Letter", 2550, 3300), Paper("Photo 4 × 6 in", 1200, 1800)
    ];

    private readonly SKColor backgroundColor;
    private readonly Settings settings;
    private readonly CanvasPreset? clipboard;
    private readonly StackPanel presets = new() { Spacing = 4 };
    private readonly List<(Button Button, CanvasPreset Preset)> tiles = [];
    private readonly CanvasLayoutPreview preview = new() { Name = "NewCanvasPreview" };
    private readonly CanvasLayoutEditor editor;
    private readonly TextBlock orientation = Ui.Label("", Palette.Secondary, 11);
    private readonly TextBlock printSize = Ui.Label("", Palette.Secondary, 11);
    private readonly TextBlock cmykNote;
    private readonly NumericUpDown width, height, resolution;
    private readonly ComboBox colorMode, precision;
    private readonly Controls.ColorSwatch swatch = new() { Width = 13, Height = 13, CornerRadius = new CornerRadius(2) };
    private readonly TextBlock backgroundText = Ui.Label("", size: 12);
    private readonly Dictionary<string, Control> legend = [];
    private CanvasPreset? selectedPreset;
    private bool syncing;

    public NewCanvasOptions Options { get; private set; }
    public TextBlock Status { get; } = Ui.Label("", Palette.Secondary, 11);
    public event Action? Changed;

    public NewCanvasView(SKColor backgroundColor, (int W, int H)? clipboardSize, Settings settings, bool compact)
    {
        this.backgroundColor = backgroundColor;
        this.settings = settings;
        if (clipboardSize is { } size && DocumentLimits.FitsSurface(size.W, size.H))
            clipboard = new("Clipboard", new(size.W, size.H));
        var first = clipboard ?? Digital[0];
        Options = first.Options;
        ColumnDefinitions = new ColumnDefinitions(compact ? "179,*,299" : "217,*,318");
        ColumnSpacing = 1;
        Background = Palette.Divider;

        var sidebar = Scroll(presets, "CanvasPresets");
        Children.Add(Pane("Presets", sidebar));
        var previewFooter = new WrapPanel { ItemSpacing = 14, LineSpacing = 5, Margin = new Thickness(0, 12, 0, 1) };
        AddLegend("Canvas edge", CanvasLayoutDrawing.Edge);
        AddLegend("Safe area", CanvasLayoutDrawing.Safe, true);
        AddLegend("Columns", CanvasLayoutDrawing.Columns);
        AddLegend("Rows", CanvasLayoutDrawing.Rows);
        AddLegend("Squares", CanvasLayoutDrawing.Squares);
        AddLegend("Bleed", CanvasLayoutDrawing.Bleed, true);
        void AddLegend(string name, SKColor color, bool stroke = false)
        {
            var brush = new SolidColorBrush(color.ToAvalonia());
            var mark = new Border { Width = 9, Height = 8, BorderBrush = brush, BorderThickness = new Thickness(1),
                Background = stroke ? null : brush, VerticalAlignment = VerticalAlignment.Center };
            var entry = Ui.Row(4, mark, Ui.Label(Locale.T(name), Palette.Secondary, 11));
            previewFooter.Children.Add(entry);
            legend.Add(name, entry);
        }
        var previewBody = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        previewBody.Children.Add(preview);
        Grid.SetRow(previewFooter, 1);
        previewBody.Children.Add(previewFooter);
        Put(this, Pane("Live Preview", previewBody, orientation, Palette.Pasteboard, divider: false), 1);

        width = Number("NewCanvasWidth", Options.Width, 1, DocumentLimits.MaxSide,
            v => Edit(Options with { Width = (int)v }), live: true);
        height = Number("NewCanvasHeight", Options.Height, 1, DocumentLimits.MaxSide,
            v => Edit(Options with { Height = (int)v }), live: true);
        resolution = Number("NewCanvasResolution", Options.Resolution, 1, 9600, v => Edit(Options with { Resolution = v }));
        var swap = Ui.IconButton(Icons.Swap, "Swap width and height",
            () => Select(new CanvasPreset("Custom", Options with { Width = Options.Height, Height = Options.Width })));
        swap.Name = "SwapCanvasDimensions";
        swap.Background = Palette.Field;
        swap.VerticalAlignment = VerticalAlignment.Bottom;
        var dimensions = Dimensions(width, height, swap, scrubLabels: true);

        var backgroundMenu = new MenuFlyout();
        var editBackground = new Button
        {
            Name = "NewCanvasBackground", Content = Ui.Row(7, swatch, backgroundText), Padding = new Thickness(8, 0),
            MinWidth = 0, Height = 30, MinHeight = 0, Flyout = backgroundMenu,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(editBackground, Locale.T("Choose background color"));
        Locale.Tip(editBackground, "Choose background color");
        BackgroundOption("White", () => Edit(Options with { Fill = (uint)SKColors.White }));
        BackgroundOption("Black", () => Edit(Options with { Fill = (uint)SKColors.Black }));
        BackgroundOption("Transparent", () => Edit(Options with { Fill = null }));
        BackgroundOption("Choose color", async () =>
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            var before = Options.Fill;
            var color = await Prompts.Color(owner, "Background color", Options.Background ?? backgroundColor,
                next => Edit(Options with { Fill = (uint)next }), Composa.ColorManagement.RgbProfile.Srgb);
            var fill = color is { } chosen ? (uint)chosen : before;
            Edit(Options with { Fill = fill });
        });
        void BackgroundOption(string caption, Action action)
        {
            var item = new MenuItem { Header = Locale.T(caption) };
            item.Click += (_, _) => action();
            backgroundMenu.Items.Add(item);
        }
        colorMode = Ui.Combo(Enum.GetValues<CanvasColorMode>(), Options.ColorMode, v => v.ToString(),
            mode => Edit(Options with { ColorMode = mode }), double.NaN);
        colorMode.Name = "NewCanvasColorMode";
        cmykNote = Ui.Label(Locale.T("CMYK TIFF output · RGB editing preview · no print ICC profile"), Palette.Secondary, 11);
        cmykNote.TextWrapping = TextWrapping.Wrap;
        precision = Ui.Combo(Enum.GetValues<ColorDepth>(), Options.Depth, d => d == ColorDepth.Eight ? "8" : "16",
            depth => Edit(Options with { Depth = depth }), double.NaN);
        precision.Name = "NewCanvasDepth";
        printSize.Margin = new Thickness(0, 1, 0, 1);
        editor = new CanvasLayoutEditor(layout => Edit(Options with { Layout = layout }));
        var settingsBody = Ui.Column(13, dimensions,
            Pair(Field("Background", editBackground), Field("Color mode", colorMode)), cmykNote,
            Pair(Field("Resolution", resolution, "ppi"), Field("Bits per channel", precision)), printSize,
            Ui.Separator(vertical: false), editor);
        Put(this, Pane("Canvas Settings", Scroll(settingsBody, "NewCanvasSettings")), 2);
        BuildPresets();
        Select(first);
    }

    private static CanvasPreset Screen(string name, int width, int height) =>
        new(name, new(width, height, (uint)SKColors.White));

    private static CanvasPreset Paper(string name, int width, int height) => new(name,
        new(width, height, (uint)SKColors.White, Resolution: 300));

    private static bool IsUsable(CanvasPreset? preset) => preset?.Options is { Fits: true, Layout: not null } && !string.IsNullOrWhiteSpace(preset.Name);

    private static Border Pane(string title, Control body, TextBlock? value = null, IBrush? background = null, bool divider = true)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var header = Row("*,Auto");
        Put(header, Heading(title), 0);
        if (value != null) Put(header, value, 1);
        grid.Children.Add(new Border { Padding = new Thickness(PanelInset, 12), BorderBrush = Palette.Divider,
            BorderThickness = divider ? new Thickness(0, 0, 0, 1) : default, Child = header });
        body.Margin = new Thickness(PanelInset, 12, body is ScrollViewer ? 0 : PanelInset, 12);
        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        return new Border { Background = background ?? Palette.Panel, Child = grid };
    }

    private static ScrollViewer Scroll(Control child, string name)
    {
        // The native overlay can expand on hover. Keep its full area beyond the fields.
        child.Margin = new Thickness(0, 0, Ui.OverlayScrollBarGutter, 0);
        return new ScrollViewer
        {
            Name = name, Content = child, AllowAutoHide = true, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private void BuildPresets()
    {
        tiles.Clear();
        presets.Children.Clear();
        AddGroup("Recent", settings.RecentCanvasPresets.Where(IsUsable), false);
        AddGroup("Built-in · Digital", clipboard == null ? Digital : Digital.Take(5).Append(clipboard).Concat(Digital.Skip(5)), true);
        AddGroup("Built-in · Print", Print, true);
        AddGroup("Custom", settings.UserCanvasPresets.Where(IsUsable), false);

        void AddGroup(string title, IEnumerable<CanvasPreset> items, bool translate)
        {
            var label = Ui.Label(Locale.T(title), Palette.Secondary, 11);
            label.Margin = new Thickness(2, 7, 0, 0);
            presets.Children.Add(label);
            var entries = items.ToArray();
            if (entries.Length == 0) presets.Children.Add(Ui.Label("—", Palette.Secondary, 11));
            foreach (var item in entries) AddPreset(item, translate ? Locale.T(item.Name) : item.Name);
        }
    }

    private void AddPreset(CanvasPreset preset, string label)
    {
        var (w, h) = (preset.Options.Width, preset.Options.Height);
        var scale = 17d / Math.Max(w, h);
        var shape = new Border { Width = Math.Max(5, w * scale), Height = Math.Max(5, h * scale),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(1), BorderBrush = Palette.Secondary,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var icon = new Border { Width = 27, Height = 27, Background = Palette.RaisedIconWell, CornerRadius = new CornerRadius(4), Child = shape };
        var title = Ui.Label(label, size: 12);
        title.TextWrapping = TextWrapping.Wrap;
        var meta = Ui.Label(preset.Options.Resolution == 300
            ? $"{w * 25.4 / 300:0} × {h * 25.4 / 300:0} mm" : $"{w} × {h} px", Palette.Secondary, 11);
        var content = Row("27,*", 7);
        Put(content, icon, 0);
        Put(content, Ui.Column(1, title, meta), 1);
        var tile = new Button { Content = content, Tag = preset.Name, Padding = new Thickness(6, 5), MinHeight = 41,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(tile, $"{label} {w} × {h}");
        ToolTip.SetTip(tile, label);
        tile.Click += (_, _) => Select(preset);
        presets.Children.Add(tile);
        tiles.Add((tile, preset));
    }

    private void Select(CanvasPreset preset)
    {
        Options = preset.Options with { Layout = preset.Options.Layout.Normalized() };
        selectedPreset = preset;
        syncing = true;
        width.Value = Options.Width;
        height.Value = Options.Height;
        resolution.Value = (decimal)Options.Resolution;
        colorMode.SelectedIndex = (int)Options.ColorMode;
        precision.SelectedIndex = Options.Depth == ColorDepth.Sixteen ? 1 : 0;
        syncing = false;
        Refresh();
    }

    private void Edit(NewCanvasOptions value)
    {
        if (syncing || value == Options) return;
        Options = value;
        Refresh();
    }

    private void Refresh()
    {
        var (w, h) = (Options.Width, Options.Height);
        var divisor = Gcd(w, h);
        var ratio = Math.Max(w, h) / divisor <= 32 ? $"{w / divisor}:{h / divisor}" :
            w >= h ? $"{(double)w / h:0.##}:1" : $"1:{(double)h / w:0.##}";
        orientation.Text = $"{Locale.T(w == h ? "Square" : w > h ? "Landscape" : "Portrait")} · {ratio}";
        printSize.Text = $"{w * 25.4 / Options.Resolution:0.#} × {h * 25.4 / Options.Resolution:0.#} mm";
        printSize.IsVisible = Options.Resolution >= 150 || Options.Layout.BleedMm > 0;
        cmykNote.IsVisible = Options.ColorMode == CanvasColorMode.CMYK;
        swatch.Background = Options.Background is { } color ? new SolidColorBrush(color.ToAvalonia()) : Palette.Edge;
        backgroundText.Text = Options.Background is { } fill ? $"#{(uint)fill & 0xffffff:X6}" : Locale.T("Transparent");
        preview.Show(Options);
        editor.Show(Options.Layout, w, h);
        var active = tiles.FirstOrDefault(tile => ReferenceEquals(tile.Preset, selectedPreset) && tile.Preset.Options == Options);
        if (active.Button == null) active = tiles.FirstOrDefault(tile => tile.Preset.Options == Options);
        foreach (var (tile, _) in tiles)
        {
            tile.Background = tile == active.Button ? Palette.Selected : Brushes.Transparent;
            tile.BorderThickness = default;
        }
        legend["Safe area"].IsVisible = Options.Layout.SafeArea != default;
        legend["Columns"].IsVisible = Options.Layout.Columns?.Count > 0;
        legend["Rows"].IsVisible = Options.Layout.Rows?.Count > 0;
        legend["Squares"].IsVisible = Options.Layout.SquareSize > 0;
        legend["Bleed"].IsVisible = Options.Layout.BleedMm > 0;
        Status.Text = Options.Fits
            ? $"{w} × {h} px · {Options.ColorMode} / {(int)Options.Depth}"
            : Locale.T("Choose a smaller canvas for this bit depth.");
        Changed?.Invoke();

        static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
    }

    public void RememberRecent()
    {
        var name = selectedPreset?.Options == Options ? selectedPreset.Name
            : tiles.FirstOrDefault(tile => tile.Preset.Options == Options).Preset?.Name ?? "Custom";
        settings.RecentCanvasPresets.RemoveAll(preset => !IsUsable(preset) || preset.Options == Options);
        settings.RecentCanvasPresets.Insert(0, new(name, Options));
        if (settings.RecentCanvasPresets.Count > 6) settings.RecentCanvasPresets.RemoveRange(6, settings.RecentCanvasPresets.Count - 6);
        settings.Save();
    }

    public async Task SavePreset(Window owner)
    {
        if (!Options.Fits) return;
        var name = new TextBox { PlaceholderText = Locale.T("Preset name"), Width = 260, MaxLength = 60 };
        var prompt = new DialogWindow("Save preset", Field("Preset name", name), "Save")
        {
            ValidateAccept = () => !string.IsNullOrWhiteSpace(name.Text)
        };
        prompt.Opened += (_, _) => name.Focus();
        if (!await prompt.Ask(owner)) return;
        var preset = new CanvasPreset(name.Text!.Trim(), Options);
        settings.UserCanvasPresets.RemoveAll(existing => string.Equals(existing.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        settings.UserCanvasPresets.Insert(0, preset);
        selectedPreset = preset;
        settings.Save();
        BuildPresets();
        Refresh();
        var tile = tiles.Single(entry => ReferenceEquals(entry.Preset, preset)).Button;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { tile.BringIntoView(); tile.Focus(); });
    }
}
