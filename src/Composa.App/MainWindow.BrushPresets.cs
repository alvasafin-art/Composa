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
            this.side = side;
            this.brush = ForSample(brush);
            Width = Height = side;
            IsHitTestVisible = false;
        }

        public void Update(BrushSettings settings)
        {
            settings = ForSample(settings);
            if (brush == settings) return;
            brush = settings;
            ReleaseBitmap();
            InvalidateVisual();
        }

        private BrushSettings ForSample(BrushSettings settings) => new()
        {
            Size = Math.Clamp(settings.Size, 1, side - 4),
            Hardness = settings.Hardness,
            Flow = settings.Flow,
            Opacity = settings.Opacity,
            Roundness = settings.TipRoundness,
            Angle = settings.TipAngle
        };

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

    private Button BrushLibrary(EditorSession target, out Action refresh)
    {
        bool eraser = target.Tool == Tool.Eraser;
        var custom = eraser ? settings.EraserPresets : settings.BrushPresets;
        var title = eraser ? "Eraser presets" : "Brush presets";
        var sample = new TipSample(target.Brush, 22);
        var flyout = new Flyout();
        var button = new Button
        {
            Name = "BrushPresetPicker", Flyout = flyout,
            Content = Ui.Row(6, sample, Ui.Label(Locale.T("Presets")), Icons.Create(Icons.ChevronDown, 12))
        };
        Locale.Tip(button, title);
        Action? refreshOpen = null;
        var changed = false;
        void Refresh()
        {
            sample.Update(target.Brush);
            refreshOpen?.Invoke();
        }

        flyout.Opening += (_, _) =>
        {
            changed = false;
            var list = Ui.Column(4);
            var entries = new List<(BrushSettings Settings, Button Button, TextBlock Status)>();
            var preview = new TipSample(target.Brush, 60);
            var tipFields = Ui.Column(6);
            void UpdateTip(BrushSettings brush)
            {
                target.Brush = brush;
                changed = true;
                Refresh();
                canvas.InvalidateBrushCursor();
            }

            void BuildTipFields()
            {
                const double width = 177;
                var roundness = Ui.SliderField("Roundness", target.Brush.TipRoundness * 100, BrushSettings.MinRoundness * 100, 100,
                    v => UpdateTip(target.Brush with { Roundness = v / 100 }), width: width, reset: 100);
                roundness.Name = "BrushTipRoundness";
                if (ToolTip.GetTip(roundness) is Control sliderHelp)
                    Locale.Tip(roundness, Ui.Column(6, Ui.Label(Locale.T("100% is a circle. Lower values make an oval.")), sliderHelp));
                var angle = Ui.AngleField("Tip angle", target.Brush.TipAngle, -180, 180,
                    v => UpdateTip(target.Brush with { Angle = v }), width, Controls.AngleDialStyle.Line, reset: 0);
                angle.Name = "BrushTipAngle";
                tipFields.Children.Clear();
                tipFields.Children.Add(roundness);
                tipFields.Children.Add(angle);
            }

            void Entry(BrushPreset preset, bool saved)
            {
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("32,*,Auto"), ColumnSpacing = 8 };
                grid.Children.Add(new TipSample(preset.Settings, 27));
                AddAt(grid, Ui.Label(saved ? preset.Name : Locale.T(preset.Name)), 1);
                var status = Ui.Label("", Palette.Secondary, 11);
                AddAt(grid, status, 2);
                var entry = new Button { Classes = { "flat" }, Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 4) };
                entry.Click += (_, _) =>
                {
                    target.Brush = preset.Settings;
                    BuildTipFields();
                    refreshOptions?.Invoke();
                    RememberToolSettings();
                    changed = false;
                    canvas.InvalidateBrushCursor();
                };
                entries.Add((preset.Settings, entry, status));
                list.Children.Add(entry);
            }

            list.Children.Add(Ui.Label(Locale.T("Built-in presets"), Palette.Secondary, 11));
            foreach (var preset in BrushPresets.All) Entry(preset, false);
            if (custom.Count > 0) list.Children.Add(Ui.Label(Locale.T("My presets"), Palette.Secondary, 11));
            foreach (var pair in custom.OrderBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase))
                Entry(new BrushPreset(pair.Key, pair.Value), true);

            BuildTipFields();
            refreshOpen = () =>
            {
                preview.Update(target.Brush);
                foreach (var (preset, entry, status) in entries)
                {
                    var selected = preset == target.Brush;
                    entry.Background = selected ? Palette.Selected : Brushes.Transparent;
                    status.Text = selected ? "✓" : $"{preset.Hardness:P0}";
                }
            };
            Refresh();
            var save = Ui.TextButton("Save preset…", () => { flyout.Hide(); _ = SaveBrushPreset(target.Brush, eraser); });
            flyout.Content = Ui.Column(8, Ui.Label(Locale.T(title), weight: FontWeight.SemiBold),
                new ScrollViewer { Content = list, MaxHeight = 185 },
                Ui.Separator(vertical: false), Ui.Label(Locale.T("Brush tip"), weight: FontWeight.SemiBold),
                Ui.Row(10, preview, tipFields), save);
            ((Control)flyout.Content).Width = 270;
        };
        flyout.Closed += (_, _) =>
        {
            refreshOpen = null;
            if (changed) RememberToolSettings();
        };
        refresh = Refresh;
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
