using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Composa.ColorManagement;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Controls;

internal sealed class VectorStopsEditor : StackPanel
{
    private const string Columns = "64,*,63,24";
    private sealed class Stop(double position, SKColor color) { public double Position = position; public SKColor Color = color; }
    private readonly List<Stop> stops = [];
    private readonly StackPanel rows = new() { Spacing = 2 };
    private readonly List<Action> syncRows = [];
    private readonly GradientEditor.RampPreview strip;
    private readonly Action<GradientRamp> changed;
    private readonly RgbProfile profile, display;
    private Stop selected;
    public event Action<SKColor>? SelectedColorChanged;
    public SKColor SelectedColor => selected.Color;
    public VectorStopsEditor(GradientRamp ramp, RgbProfile profile, RgbProfile display, Action<GradientRamp> changed)
    {
        this.changed = changed; this.profile = profile; this.display = display; Spacing = 4;
        var colors = ramp.Colors; var alpha = ramp.Opacities;
        if (colors.Select(x => x.Position).SequenceEqual(alpha.Select(x => x.Position)))
            for (int i = 0; i < colors.Length; i++) stops.Add(new(colors[i].Position, new SKColor(colors[i].Color).WithAlpha((byte)Math.Round(255 * alpha[i].Opacity))));
        else foreach (var p in colors.Select(x => x.Position).Concat(alpha.Select(x => x.Position)).Distinct().Order()) stops.Add(new(p, ramp.Sample(p)));
        selected = stops[0];
        strip = new(profile, display) { PairedStops = true, Width = 312, Height = 37 };
        ToolTip.SetTip(strip, "Click to add a stop. Drag a stop to move it.");
        strip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(strip).Properties.IsLeftButtonPressed) return;
            double p = strip.Position(e.GetPosition(strip).X);
            var hit = stops.OrderBy(s => Math.Abs(s.Position - p)).First();
            if (Math.Abs(hit.Position - p) * (strip.Bounds.Width - 14) <= 7) Select(hit); else AddAt(p);
            e.Pointer.Capture(strip); e.Handled = true;
        };
        strip.PointerMoved += (_, e) =>
        {
            if (e.Pointer.Captured != strip) return;
            selected.Position = strip.Position(e.GetPosition(strip).X); Publish();
        };
        strip.PointerReleased += (_, e) => { if (e.Pointer.Captured == strip) { e.Pointer.Capture(null); BuildRows(); } };
        var add = Ui.TextButton("+", () =>
        {
            var right = stops.Where(s => s.Position > selected.Position).MinBy(s => s.Position);
            var left = stops.Where(s => s.Position < selected.Position).MaxBy(s => s.Position);
            var neighbor = right?.Position ?? left?.Position ?? (selected.Position < .5 ? 1 : 0);
            AddAt((selected.Position + neighbor) / 2);
        });
        add.MinWidth = 0; add.Width = 26; add.Padding = new Thickness(0);
        ToolTip.SetTip(add, "Add gradient stop");
        var header = new DockPanel(); DockPanel.SetDock(add, Dock.Right); header.Children.Add(add); header.Children.Add(Ui.Label("Stops", Palette.Secondary));
        var labels = PaintLayout.Columns(Columns, 4, PaintLayout.Caption("Position"), PaintLayout.Caption("Stop color"),
            PaintLayout.Caption("Opacity"), new Border());
        labels.Margin = new Thickness(4, 0);
        Children.Add(strip); Children.Add(header); Children.Add(labels);
        Children.Add(new ScrollViewer { Content = rows, MaxHeight = 112,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        BuildRows(); Refresh(false);
    }
    private GradientRamp Ramp() => new GradientRamp
    {
        Colors = stops.Select(s => new GradientColorStop(s.Position, (uint)s.Color | 0xff000000)).ToArray(),
        Opacities = stops.Select(s => new GradientOpacityStop(s.Position, s.Color.Alpha / 255d)).ToArray()
    }.Normalized();
    private void AddAt(double position)
    {
        if (stops.Count >= 64) return;
        var stop = new Stop(position, Ramp().Sample(position)); stops.Add(stop); selected = stop; BuildRows(); Publish(); SelectedColorChanged?.Invoke(stop.Color);
    }
    private void Select(Stop stop)
    {
        selected = stop; Refresh(false); SelectedColorChanged?.Invoke(stop.Color);
    }
    private void Refresh(bool publish)
    {
        var ramp = Ramp(); strip.SelectedPosition = selected.Position; strip.Ramp = ramp; strip.InvalidateVisual();
        foreach (var sync in syncRows) sync();
        if (publish) changed(ramp);
    }
    private void Publish() => Refresh(true);
    public void SetColor(SKColor color) { selected.Color = color.WithAlpha(selected.Color.Alpha); Publish(); }
    public void SetOpacity(byte opacity)
    {
        selected.Color = selected.Color.WithAlpha(opacity); Publish(); SelectedColorChanged?.Invoke(selected.Color);
    }
    private void BuildRows()
    {
        rows.Children.Clear(); syncRows.Clear();
        foreach (var stop in stops.OrderBy(s => s.Position))
        {
            var position = new PaintValueField("StopPosition", 38, text => { if (PaintValueField.TryNumber(text, 100, out var value)) { stop.Position = value / 100; Publish(); } });
            var hex = new PaintValueField("StopHex", 65, text => { if (PaintValueField.TryRgb(text, out var value)) { stop.Color = new SKColor(value | 0xff000000).WithAlpha(stop.Color.Alpha); Publish(); SelectedColorChanged?.Invoke(selected.Color); } });
            var opacity = new PaintValueField("StopOpacity", 32, text => { if (PaintValueField.TryNumber(text, 100, out var value)) { stop.Color = stop.Color.WithAlpha((byte)Math.Round(value * 255 / 100)); Publish(); SelectedColorChanged?.Invoke(selected.Color); } });
            var chip = new Button { Width = 21, Height = 19, Padding = new Thickness(0), MinHeight = 0 };
            chip.Click += (_, _) => Select(stop);
            var remove = Ui.TextButton("−", () =>
            {
                if (stops.Count <= 2) return;
                int index = stops.IndexOf(stop); stops.Remove(stop); if (selected == stop) selected = stops[Math.Min(index, stops.Count - 1)];
                BuildRows(); Publish(); SelectedColorChanged?.Invoke(selected.Color);
            });
            remove.MinWidth = 0; remove.Width = 24; remove.Padding = new Thickness(0);
            remove.IsEnabled = stops.Count > 2; ToolTip.SetTip(remove, "Remove gradient stop");
            var row = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 1),
                Child = PaintLayout.Columns(Columns, 4, Ui.Row(1, position, PaintLayout.Caption("%")), Ui.Row(4, chip, hex),
                    Ui.Row(1, opacity, PaintLayout.Caption("%")), remove) };
            row.GotFocus += (_, _) => { if (selected != stop) Select(stop); };
            row.PointerPressed += (_, _) => { if (selected != stop) Select(stop); };
            rows.Children.Add(row);
            void Sync()
            {
                position.Show((stop.Position * 100).ToString("0.#")); hex.Show($"{(uint)stop.Color & 0xffffff:X6}");
                opacity.Show(Math.Round(stop.Color.Alpha * 100d / 255).ToString("0"));
                chip.Background = new SolidColorBrush(stop.Color.ToDisplay(profile, display));
                row.Background = stop == selected ? new SolidColorBrush(Color.FromRgb(51, 67, 86)) : Palette.PanelRaised;
            }
            syncRows.Add(Sync); Sync();
        }
    }
}
