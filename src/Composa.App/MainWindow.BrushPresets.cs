using Avalonia.Controls;
using Composa.App.Dialogs;
using Composa.Editing;
using Avalonia;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Composa.Painting;

namespace Composa.App;

public sealed partial class MainWindow
{
    private static Control BrushDynamics(EditorSession target, Action refreshCursor)
    {
        var flyout = new Flyout();
        var button = new Button
        {
            Name = "BrushDynamics", Classes = { "flat" }, Flyout = flyout,
            Content = Ui.Row(5, Ui.Label(Locale.T("Dynamics")), Icons.Create(Icons.ChevronDown, 12))
        };
        flyout.Opening += (_, _) => flyout.Content = Ui.Column(5,
            Ui.Check("Pressure size", target.Brush.PressureSize, v => target.Brush = target.Brush with { PressureSize = v }),
            Ui.Check("Pressure flow", target.Brush.PressureFlow, v => target.Brush = target.Brush with { PressureFlow = v }),
            Ui.Check("Full brush outline", target.Brush.FullCursor, v =>
            {
                target.Brush = target.Brush with { FullCursor = v };
                refreshCursor();
            }));
        return button;
    }

    private sealed class TipSample : Control
    {
        private readonly int side;
        private BrushSettings brush;
        private Bitmap? bitmap;

        public TipSample(BrushSettings brush, int side)
        {
            this.brush = brush;
            this.side = side;
            Width = Height = side;
            IsHitTestVisible = false;
        }

        public void Update(BrushSettings settings)
        {
            if (brush == settings) return;
            brush = settings;
            ReleaseBitmap();
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            if (bitmap == null)
            {
                using var preview = BrushStroke.RenderTip(brush, side);
                bitmap = Ui.ToAvaloniaBitmap(preview, side);
            }
            context.DrawImage(bitmap, new Rect(Bounds.Size));
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            ReleaseBitmap();
            base.OnDetachedFromVisualTree(e);
        }

        private void ReleaseBitmap()
        {
            bitmap?.Dispose();
            bitmap = null;
        }
    }

    private Button BrushTipPicker(EditorSession target, out Action refresh)
    {
        var sample = new TipSample(target.Brush, 22);
        var flyout = new Flyout();
        var button = new Button
        {
            Name = "BrushTipPicker", Flyout = flyout,
            Content = Ui.Row(5, sample, Ui.Label(Locale.T("Brush tip")), Icons.Create(Icons.ChevronDown, 12))
        };
        Locale.Tip(button, "Change the shape of the brush mark");
        var changed = false;
        flyout.Opening += (_, _) =>
        {
            changed = false;
            var preview = new TipSample(target.Brush, 72) { HorizontalAlignment = HorizontalAlignment.Center };
            void Update(BrushSettings brush)
            {
                target.Brush = brush;
                preview.Update(brush);
                sample.Update(brush);
                canvas.InvalidateCanvas();
                changed = true;
            }

            const double width = 190;
            var roundness = Ui.SliderField("Roundness", target.Brush.TipRoundness * 100, BrushSettings.MinRoundness * 100, 100,
                v => Update(target.Brush with { Roundness = v / 100 }), width: width, reset: 100);
            roundness.Name = "BrushTipRoundness";
            Locale.Tip(roundness, "100% is a circle. Lower values make an oval.");
            var angle = Ui.AngleField("Angle", target.Brush.TipAngle, -180, 180,
                v => Update(target.Brush with { Angle = v }), width, Controls.AngleDialStyle.Line, reset: 0);
            angle.Name = "BrushTipAngle";
            flyout.Content = Ui.Column(8,
                Ui.Label(Locale.T("Single dab at full pressure"), Palette.Secondary),
                preview, roundness, angle);
        };
        flyout.Closed += (_, _) => { if (changed) RememberToolSettings(); };
        refresh = () => sample.Update(target.Brush);
        return button;
    }

    private Button BrushLibrary(EditorSession target)
    {
        bool eraser = target.Tool == Tool.Eraser;
        var custom = eraser ? settings.EraserPresets : settings.BrushPresets;
        var title = eraser ? "Eraser presets" : "Brush presets";
        var button = new Button { Name = "BrushPresetPicker", Content = Ui.Row(6, Ui.Label(Locale.T("Presets")), Icons.Create(Icons.ChevronDown, 12)) };
        Locale.Tip(button, Locale.T(title));
        var flyout = new Flyout();
        button.Flyout = flyout;
        flyout.Opening += (_, _) =>
        {
            var list = Ui.Column(4);
            void Entry(BrushPreset preset, bool saved)
            {
                var selected = preset.Settings == target.Brush;
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("32,*,Auto"), ColumnSpacing = 8 };
                grid.Children.Add(new TipSample(preset.Settings, 27));
                AddAt(grid, Ui.Label(saved ? preset.Name : Locale.T(preset.Name)), 1);
                AddAt(grid, Ui.Label(selected ? "✓" : $"{preset.Settings.Hardness:P0}", Palette.Secondary, 11), 2);
                var entry = new Button { Classes = { "flat" }, Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 4),
                    Background = selected ? Palette.Selected : Brushes.Transparent };
                entry.Click += (_, _) => { target.Brush = preset.Settings; RememberToolSettings(); flyout.Hide(); RebuildOptions(); canvas.InvalidateCanvas(); };
                list.Children.Add(entry);
            }
            list.Children.Add(Ui.Label(Locale.T("Built-in presets"), Palette.Secondary, 11));
            foreach (var preset in BrushPresets.All) Entry(preset, false);
            if (custom.Count > 0) list.Children.Add(Ui.Label(Locale.T("My presets"), Palette.Secondary, 11));
            foreach (var pair in custom.OrderBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase))
                Entry(new BrushPreset(pair.Key, pair.Value), true);
            var save = Ui.TextButton("Save current tip…", () => { flyout.Hide(); _ = SaveBrushPreset(target.Brush, eraser); });
            flyout.Content = Ui.Column(8, Ui.Label(Locale.T(title), weight: FontWeight.SemiBold),
                new ScrollViewer { Content = list, MaxHeight = 300 }, save);
            ((Control)flyout.Content).Width = 228;
        };
        return button;
    }

    private async Task SaveBrushPreset(BrushSettings brush, bool eraser)
    {
        var presets = eraser ? settings.EraserPresets : settings.BrushPresets;
        var name = new TextBox { Text = Locale.T(eraser ? "My eraser" : "My brush"), Width = 240, MaxLength = 100 };
        var error = Ui.Label("", Palette.Secondary, 12);
        var dialog = new DialogWindow(eraser ? "Save eraser preset" : "Save brush preset", Ui.Column(5, Ui.Label(Locale.T("Name")), name, error));
        dialog.ValidateAccept = () =>
        {
            var value = name.Text?.Trim();
            error.Text = string.IsNullOrEmpty(value) ? Locale.T("Enter a name") :
                presets.Keys.Contains(value, StringComparer.OrdinalIgnoreCase)
                    ? Locale.T("This name is already in use") : "";
            return error.Text.Length == 0;
        };
        if (!await dialog.Ask(this)) return;
        presets[name.Text!.Trim()] = brush;
        settings.Save();
        RebuildOptions();
    }
}
