using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Dialogs;

/// <summary>A modal window with a body and OK/Cancel. Enter accepts, Escape cancels.</summary>
public class DialogWindow : Window
{
    private readonly Button ok;
    protected readonly StackPanel Buttons;
    internal StackPanel FooterButtons => Buttons;

    public DialogWindow(string title, Control body, string okText = "OK", bool cancellable = true)
    {
        Title = Locale.T(title);
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ok = Ui.TextButton(okText, Accept, accent: true);
        ok.IsDefault = true;
        Buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        if (cancellable)
        {
            var cancel = Ui.TextButton("Cancel", Reject);
            cancel.Classes.Add("flat");
            cancel.IsCancel = true;
            Buttons.Children.Add(cancel);
        }
        Buttons.Children.Add(ok);
        Content = new StackPanel { Margin = new Thickness(20), Children = { body, Buttons } };
    }

    public void SetCancelLabel(string label) => Buttons.Children.OfType<Button>().Single(b => b.IsCancel).Content = label;
    public void AddSecondaryAction(string label, Action action) => Buttons.Children.Insert(0, Ui.TextButton(label, action));
    public Func<bool>? ValidateAccept { get; set; }

    public bool CanAccept { set => ok.IsEnabled = value; }
    internal void PlaceGenerationActions(Panel host, StackPanel variants)
    {
        var cancel = Buttons.Children.OfType<Button>().Single(b => b.IsCancel);
        Buttons.Children.Clear();
        Buttons.IsVisible = false;
        cancel.MinWidth = 48;
        cancel.Height = 28;
        variants.Bind(IsEnabledProperty, ok.GetObservable(IsEnabledProperty));
        host.Children.Add(cancel);
        host.Children.Add(AI.AiGenerationControls.RunGroup(ok, variants));
        ((StackPanel)Content!).Margin = new Thickness(8);
    }

    internal void SubmitGeneration() { if (ok.IsEnabled) Accept(); }

    public async Task<bool> Ask(Window owner) => await ShowDialog<bool?>(owner) == true;

    /// <summary>OK and Enter. A panel shown without <see cref="Ask"/> overrides these to act instead of closing with a result.</summary>
    protected virtual void Accept() { if (ValidateAccept?.Invoke() != false) Close(true); }
    /// <summary>Cancel and Escape.</summary>
    protected virtual void Reject() => Close(false);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (e.Key == Key.Escape) { Reject(); e.Handled = true; }
    }
}

public static class Prompts
{
    public static Task<bool> Confirm(Window owner, string title, string message, string okText = "OK") =>
        new DialogWindow(title, new TextBlock { Text = Locale.Message(message), MaxWidth = 420, TextWrapping = TextWrapping.Wrap }, okText).Ask(owner);

    public static Task Alert(Window owner, string title, string message) =>
        new DialogWindow(title, new TextBlock { Text = Locale.Message(message), MaxWidth = 460, TextWrapping = TextWrapping.Wrap }, cancellable: false).Ask(owner);

    /// <summary>Save / Don't Save / Cancel. Returns null for cancel.</summary>
    public static async Task<bool?> SaveChanges(Window owner, string name)
    {
        var dialog = new DialogWindow("Unsaved Changes", new TextBlock { Text = Locale.Message($"Save changes to \"{name}\" before closing?"), MaxWidth = 420, TextWrapping = TextWrapping.Wrap }, "Save");
        bool? result = null;
        var discard = Ui.TextButton("Don't Save", () => { result = false; dialog.Close(false); });
        ((StackPanel)((StackPanel)dialog.Content!).Children[1]).Children.Insert(0, discard);
        if (await dialog.Ask(owner)) return true;
        return result;
    }

    public static async Task<double?> Number(Window owner, string title, string label, double value, double min, double max, string unit = "px")
    {
        var result = value;
        var box = Ui.Number(value, min, max, v => result = v, width: 120);
        var dialog = new DialogWindow(title, Ui.Row(10, Ui.Scrub(Ui.Label(label), box), box, Ui.Label(unit, Palette.Secondary)));
        dialog.Opened += (_, _) => box.Focus();
        return await dialog.Ask(owner) ? result : null;
    }

    /// <param name="preview">Called with the working color as it changes while the dialog is open, so what it colors can follow along. The caller puts the original back on Cancel.</param>
    public static async Task<SKColor?> Color(Window owner, string title, SKColor initial, Action<SKColor>? preview = null, Composa.ColorManagement.RgbProfile? profile = null)
    {
        profile ??= Ui.WorkingProfile(owner);
        var display = Ui.DisplayProfile(owner);
        if (!profile.Equals(Composa.ColorManagement.RgbProfile.Srgb) || !display.Equals(Composa.ColorManagement.RgbProfile.Srgb))
        {
            var picker = new Controls.WorkingColorPicker(initial, profile, display);
            var after = new Controls.ColorSwatch { Width = 60, Height = 28, Background = new SolidColorBrush(initial.ToDisplay(profile, display)) };
            picker.Changed += color => { after.Background = new SolidColorBrush(color.ToDisplay(profile, display)); preview?.Invoke(color); };
            var rows = Ui.Column(10, picker, Ui.Row(10, Ui.Label(Locale.T("Before / After"), Palette.Secondary),
                new Controls.ColorSwatch { Width = 60, Height = 28, Background = new SolidColorBrush(initial.ToDisplay(profile, display)) }, after));
            return await new DialogWindow(title, rows).Ask(owner) ? picker.Color : null;
        }
        // Saturation across and brightness up in the square, hue on the strip, as Photoshop lays it out. The control's
        // default puts brightness on the strip, where a black starting color leaves it at zero and every click in the
        // square stays black.
        var view = new ColorView
        {
            Color = initial.ToAvalonia(), IsAlphaEnabled = false, IsAlphaVisible = false, IsColorPaletteVisible = true,
            IsColorModelVisible = true, IsHexInputVisible = true, Width = 360, ColorSpectrumComponents = ColorSpectrumComponents.SaturationValue
        };
        view.TemplateApplied += (_, e) =>
        {
            var tabs = e.NameScope.Find("PART_TabControl") as TabControl;
            var palette = tabs?.Items.OfType<TabItem>().Select(tab => tab.Content).OfType<ListBox>().FirstOrDefault();
            if (palette == null) return;
            palette.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Color>((color, _) =>
            {
                var sample = new Controls.ColorSwatch { Background = new SolidColorBrush(color) };
                Avalonia.Automation.AutomationProperties.SetName(sample, color.ToString());
                ToolTip.SetTip(sample, color.ToString());
                return sample;
            });
        };
        view.Loaded += (_, _) =>
        {
            foreach (var sample in view.GetVisualDescendants().OfType<ColorPreviewer>().SelectMany(previewer => previewer.GetVisualDescendants()).OfType<Border>()
                .Where(border => border.Name is "PART_AccentDecrement1Border" or "PART_AccentDecrement2Border" or "PART_AccentIncrement1Border" or "PART_AccentIncrement2Border"))
                Controls.ColorSwatch.OutlineNativeSample(sample);
        };
        if (preview != null) view.ColorChanged += (_, e) => preview(e.NewColor.ToSkia().WithAlpha(255));
        var swatches = Ui.Row(0,
            new Controls.ColorSwatch { Width = 60, Height = 28, Background = new SolidColorBrush(initial.ToAvalonia()) },
            new Controls.ColorSwatch { Width = 60, Height = 28, [!Controls.ColorSwatch.BackgroundProperty] = view.GetObservable(ColorView.ColorProperty).Select(c => (IBrush)new SolidColorBrush(c)).ToBinding() });
        var body = Ui.Column(10, view, Ui.Row(10, Ui.Label(Locale.T("Before / After"), Palette.Secondary), swatches));
        return await new DialogWindow(title, body).Ask(owner) ? view.Color.ToSkia().WithAlpha(255) : null;
    }

    private static IObservable<TResult> Select<T, TResult>(this IObservable<T> source, Func<T, TResult> map) => new MapObservable<T, TResult>(source, map);

    private sealed class MapObservable<T, TResult>(IObservable<T> source, Func<T, TResult> map) : IObservable<TResult>
    {
        public IDisposable Subscribe(IObserver<TResult> observer) => source.Subscribe(new Observer(observer, map));

        private sealed class Observer(IObserver<TResult> inner, Func<T, TResult> map) : IObserver<T>
        {
            public void OnCompleted() => inner.OnCompleted();
            public void OnError(Exception error) => inner.OnError(error);
            public void OnNext(T value) => inner.OnNext(map(value));
        }
    }
}

public static partial class CanvasDialogs
{
    public static async Task<(int Width, int Height, Anchor Anchor)?> CanvasSize(Window owner, int currentWidth, int currentHeight)
    {
        int width = currentWidth, height = currentHeight;
        var anchor = Anchor.Center;
        var relative = false;
        var widthBox = Ui.Number(width, -DocumentLimits.MaxSide, DocumentLimits.MaxSide, v => width = (int)v, width: 120);
        var heightBox = Ui.Number(height, -DocumentLimits.MaxSide, DocumentLimits.MaxSide, v => height = (int)v, width: 120);
        var relativeBox = Ui.Check("Relative", false, v =>
        {
            relative = v;
            widthBox.Value = v ? 0 : currentWidth;
            heightBox.Value = v ? 0 : currentHeight;
        });
        var anchors = new UniformGridPanel(3);
        var buttons = new List<ToggleButton>();
        foreach (var value in Enum.GetValues<Anchor>())
        {
            var button = new ToggleButton { Width = 26, Height = 26, IsChecked = value == anchor, Margin = new Thickness(1) };
            button.Click += (_, _) =>
            {
                anchor = value;
                foreach (var other in buttons) other.IsChecked = other == button;
            };
            buttons.Add(button);
            anchors.Children.Add(button);
        }
        var body = Ui.Column(12,
            Ui.Label($"Current size: {currentWidth} × {currentHeight} px", Palette.Secondary),
            Form(("Width", Ui.Row(6, widthBox, Ui.Label("px", Palette.Secondary))), ("Height", Ui.Row(6, heightBox, Ui.Label("px", Palette.Secondary))), ("", relativeBox), ("Anchor", anchors)));
        if (!await new DialogWindow("Canvas Size", body).Ask(owner)) return null;
        if (relative) { width += currentWidth; height += currentHeight; }
        return (Math.Clamp(width, 1, DocumentLimits.MaxSide), Math.Clamp(height, 1, DocumentLimits.MaxSide), anchor);
    }

    public static async Task<(int Width, int Height, double Resolution)?> ImageSize(Window owner, int currentWidth, int currentHeight, double resolution)
    {
        int width = currentWidth, height = currentHeight;
        var printSize = Ui.Label("", Palette.Secondary);
        void RefreshSize() => printSize.Text = $"{width * 25.4 / resolution:0.#} × {height * 25.4 / resolution:0.#} mm";
        RefreshSize();
        var constrain = true;
        var syncing = false;
        NumericUpDown widthBox = null!, heightBox = null!;
        widthBox = Ui.Number(width, 1, DocumentLimits.MaxSide, v =>
        {
            width = (int)v; RefreshSize();
            if (!constrain || syncing) return;
            syncing = true; heightBox.Value = Math.Max(1, (int)Math.Round(v * currentHeight / currentWidth)); syncing = false;
        }, width: 120);
        heightBox = Ui.Number(height, 1, DocumentLimits.MaxSide, v =>
        {
            height = (int)v; RefreshSize();
            if (!constrain || syncing) return;
            syncing = true; widthBox.Value = Math.Max(1, (int)Math.Round(v * currentWidth / currentHeight)); syncing = false;
        }, width: 120);
        var resolutionBox = Ui.Number(resolution, 1, 9600, v => { resolution = v; RefreshSize(); }, width: 120);
        var body = Ui.Column(12,
            Ui.Label($"Current size: {currentWidth} × {currentHeight} px", Palette.Secondary),
            Form(("Width", Ui.Row(6, widthBox, Ui.Label("px", Palette.Secondary))), ("Height", Ui.Row(6, heightBox, Ui.Label("px", Palette.Secondary))),
                ("", Ui.Check("Constrain proportions", true, v => constrain = v)), ("Resolution", Ui.Row(6, resolutionBox, Ui.Label("ppi", Palette.Secondary), printSize))));
        if (!await new DialogWindow("Image Size", body).Ask(owner)) return null;
        return (width, height, resolution);
    }

    /// <summary>Picks the JPEG quality while showing what the compression does to the picture and how large the file gets.</summary>
    public static async Task<int?> JpegQuality(Window owner, int initial, SKBitmap flattened)
    {
        var display = Ui.DisplayProfile(owner);
        var quality = initial;
        var preview = new Image { Width = 520, Height = 340, Stretch = Stretch.Uniform };
        var info = Ui.Label(Locale.T("Measuring…"), Palette.Secondary);
        var generation = 0;
        var closed = false;
        Task inFlight = Task.CompletedTask;
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };

        async Task EncodeLatest()
        {
            int mine;
            do
            {
                mine = generation;
                var q = quality;
                try
                {
                    var (bytes, shown) = await Task.Run(() =>
                    {
                        var encoded = Composa.IO.ImageFiles.Encode(flattened, Composa.IO.ExportFormat.Jpeg, q);
                        using var decoded = SKBitmap.Decode(encoded);
                        return (encoded.LongLength, decoded == null ? null : Ui.ToAvaloniaBitmap(decoded, 1040, display));
                    });
                    if (closed || mine != generation) { shown?.Dispose(); continue; }
                    (preview.Source as IDisposable)?.Dispose();
                    preview.Source = shown;
                    info.Text = Locale.Message($"{flattened.Width} × {flattened.Height} px · {(bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.0} MB" : $"{bytes / 1024.0:0} KB")}");
                }
                catch (Exception) { if (!closed) info.Text = Locale.Message("Preview unavailable"); }
            } while (!closed && mine != generation);
        }

        void Refresh()
        {
            timer.Stop();
            if (!closed && inFlight.IsCompleted) inFlight = EncodeLatest();
        }
        timer.Tick += (_, _) => Refresh();
        var row = Ui.SliderField("Quality", initial, 1, 100, v =>
        {
            quality = (int)v;
            generation++;
            info.Text = Locale.Message("Measuring…");
            timer.Stop(); timer.Start();
        }, width: 380);
        var frame = new Border { Child = preview, Background = new SolidColorBrush(Color.Parse("#1A1A1A")), Padding = new Thickness(1) };
        var dialog = new DialogWindow("Export JPEG", Ui.Column(12, frame, row, info), "Export…");
        dialog.Opened += (_, _) => Refresh();
        var accepted = await dialog.Ask(owner);
        closed = true;
        timer.Stop();
        // The caller disposes the flattened bitmap next; no encode may still be reading it.
        await inFlight;
        (preview.Source as IDisposable)?.Dispose();
        return accepted ? quality : null;
    }

    /// <summary>Labelled rows. The label of a number field, or of a row that starts with one, drags the field's value.</summary>
    public static Grid Form(params (string Label, Control Field)[] rows)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,12,*") };
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = Ui.Label(Locale.T(rows[i].Label), Palette.Secondary);
            if ((rows[i].Field as NumericUpDown ?? (rows[i].Field as Panel)?.Children.FirstOrDefault() as NumericUpDown) is { } number) Ui.Scrub(label, number);
            label.Margin = new Thickness(0, 6);
            Grid.SetRow(label, i);
            grid.Children.Add(label);
            Grid.SetRow(rows[i].Field, i);
            Grid.SetColumn(rows[i].Field, 2);
            rows[i].Field.Margin = new Thickness(0, 4);
            grid.Children.Add(rows[i].Field);
        }
        return grid;
    }

    private sealed class UniformGridPanel : Avalonia.Controls.Primitives.UniformGrid
    {
        public UniformGridPanel(int columns)
        {
            Columns = columns;
            HorizontalAlignment = HorizontalAlignment.Left;
        }
    }
}
