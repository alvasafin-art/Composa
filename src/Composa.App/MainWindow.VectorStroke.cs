using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;
using Geometry = Avalonia.Media.Geometry;

namespace Composa.App;

public sealed partial class MainWindow
{
    private static bool ClosedShape(ShapeStyle style) => style.Kind == ShapeKind.Path && style.Path?.IsFullyClosed == true;
    private static StrokeCap StrokeCapOf(ShapeStyle style) => style.StrokeOptions?.Cap ??
        (style.Kind == ShapeKind.Line || style.RoundCaps ? StrokeCap.Round : StrokeCap.Butt);

    private static string MarkerName(StrokeMarker marker) => marker switch
    {
        StrokeMarker.None => "No marker", StrokeMarker.Arrow => "Open arrow", StrokeMarker.SlimArrow => "Slim arrow",
        StrokeMarker.Triangle => "Triangle", StrokeMarker.Circle => "Circle", StrokeMarker.Square => "Square marker", StrokeMarker.Diamond => "Diamond",
        StrokeMarker.HollowTriangle => "Hollow triangle", StrokeMarker.HollowCircle => "Hollow circle",
        StrokeMarker.HollowSquare => "Hollow square", _ => "Hollow diamond"
    };

    private sealed record MarkerSample(StrokeMarker Marker, StrokeCap Cap, bool Start);

    private static Control MarkerPreview(MarkerSample sample)
    {
        using var center = new SKPath();
        center.MoveTo(1, 8); center.LineTo(23, 8);
        var style = new ShapeStyle(ShapeKind.Line, 0, 0) { StrokeOptions = new() { Cap = sample.Cap, End = sample.Marker } };
        using var ink = VectorStrokeGeometry.Build(center, style, 1.55f);
        if (sample.Start) ink.Transform(SKMatrix.CreateScale(-1, 1).PostConcat(SKMatrix.CreateTranslation(31, 0)));
        return Ui.Row(4, new Avalonia.Controls.Shapes.Path
        {
            Width = 25, Height = 15, Stretch = Stretch.Uniform, Fill = Palette.Foreground, Data = Geometry.Parse("F0 " + ink.ToSvgPathData()),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center
        }, Ui.Label(Locale.T(MarkerName(sample.Marker))));
    }

    private static Action AddStrokeProperties(StackPanel body, ShapeStyle initial, Func<ShapeStyle?> read,
        Action<Func<ShapeStyle, ShapeStyle>, bool> change)
    {
        var line = initial.Kind == ShapeKind.Line;
        var sampleCap = StrokeCapOf(initial);
        var lastDash = initial.Dash ?? new StrokeDash([14, 10]);
        var update = false;

        static Control Field(string name, Control control) => Ui.Column(3, Ui.Label(Locale.T(name), Palette.Secondary, 11), control);
        void ChangeOptions(Func<VectorStroke, VectorStroke> next, bool merge = false) =>
            change(style => style with { StrokeOptions = next(style.StrokeOptions ?? new VectorStroke()) }, merge);

        NumericUpDown Number(string name, double value, double min, double max, Action<double> set)
        {
            var box = Ui.Number(value, min, max, v => { if (!update) set(v); }, .5, "0.##", double.NaN);
            box.Height = 27;
            AutomationProperties.SetName(box, Locale.T(name));
            return box;
        }

        ComboBox Combo<T>(string name, T[] values, T selected, Func<T, string> label, Action<T> set, Func<T, string>? preview = null)
        {
            var box = new ComboBox { ItemsSource = values, SelectedItem = selected,
                ItemTemplate = new FuncDataTemplate<T>((item, _) =>
                {
                    var title = Ui.Label(Locale.T(label(item)));
                    if (preview == null) return title;
                    return Ui.Row(6, new PathIcon { Width = 27, Height = 13, Foreground = Palette.Foreground,
                        Data = Geometry.Parse(preview(item)) }, title);
                }) };
            box.SelectionChanged += (_, _) => { if (!update && box.SelectedIndex >= 0) set(values[box.SelectedIndex]); };
            box.MinWidth = 0;
            box.Height = 27;
            box.HorizontalAlignment = HorizontalAlignment.Stretch;
            AutomationProperties.SetName(box, Locale.T(name));
            return box;
        }

        var width = Number("Stroke width", line ? initial.LineWidth : initial.StrokeWidth, line ? 1 : 0, 5000,
            v => change(s => line ? s with { LineWidth = v } : s with { StrokeWidth = v, StrokeOptions = s.StrokeOptions ?? new() }, true));
        width.Name = "VectorStrokeWidth";
        var position = Combo("Stroke position", Enum.GetValues<StrokePosition>(), StrokePosition.Center,
            v => v switch { StrokePosition.Inside => "Inside", StrokePosition.Outside => "Outside", _ => "Center" },
            v => ChangeOptions(s => s with { Position = v }));
        body.Children.Add(PropertyPair(Field("Stroke width", width), Field("Stroke position", position)));

        var kind = Combo("Stroke pattern", [false, true], initial.Dash != null, v => v ? "Dashed" : "Solid",
            v => change(s => s with { Dash = v ? lastDash : null }, false),
            v => v ? "M1 9h6v2H1Z M11 9h6v2h-6Z M21 9h6v2h-6Z" : "M1 9h26v2H1Z");
        kind.Name = "VectorStrokePattern";
        body.Children.Add(Field("Stroke pattern", kind));
        var dashFields = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 7 };
        var dash = Number("Dash length", 14, .5, DocumentLimits.MaxSide, v => SetDash(0, v));
        var gap = Number("Dash gap", 10, .5, DocumentLimits.MaxSide, v => SetDash(1, v));
        var offset = Number("Dash offset", 0, -DocumentLimits.MaxSide, DocumentLimits.MaxSide, v => SetDash(2, v));
        dash.Name = "VectorDashLength"; gap.Name = "VectorDashGap"; offset.Name = "VectorDashOffset";
        dashFields.Children.Add(Field("Dash length", dash));
        AddAt(dashFields, Field("Dash gap", gap), 1);
        AddAt(dashFields, Field("Dash offset", offset), 2);
        body.Children.Add(dashFields);

        void SetDash(int field, double value) => change(style =>
        {
            if (style.Dash is not { } active) return style;
            var factor = active.Relative ? line ? style.LineWidth : Math.Max(.5, style.StrokeWidth) : 1;
            var lengths = active.Intervals.Select(n => Math.Clamp(n * factor, 0, DocumentLimits.MaxSide)).ToArray();
            if (field < 2) lengths[field] = value;
            return style with { Dash = new StrokeDash(lengths, field == 2 ? value : active.Offset) };
        }, true);

        var profile = Combo("Width profile", Enum.GetValues<StrokeProfile>(), StrokeProfile.Uniform,
            v => v switch { StrokeProfile.TaperStart => "Taper at start", StrokeProfile.TaperEnd => "Taper at end",
                StrokeProfile.TaperBoth => "Taper at both ends", _ => "Uniform width" }, v => ChangeOptions(s => s with { Profile = v }),
            v => v switch { StrokeProfile.TaperStart => "M1 9.5 27 6v8L1 10.5Z", StrokeProfile.TaperEnd => "M1 6 27 9.5v1L1 14Z",
                StrokeProfile.TaperBoth => "M1 9.5 14 6 27 9.5v1L14 14 1 10.5Z", _ => "M1 8h26v4H1Z" });
        profile.Name = "VectorStrokeProfile";
        body.Children.Add(Field("Width profile", profile));

        ComboBox MarkerControl(string name, bool start)
        {
            var box = new ComboBox { MinWidth = 0, Height = 28, HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 350, ItemsSource = MarkerItems(start),
                ItemTemplate = new FuncDataTemplate<MarkerSample>((item, _) => MarkerPreview(item)) };
            AutomationProperties.SetName(box, Locale.T(name));
            box.SelectionChanged += (_, _) =>
            {
                if (!update && box.SelectedItem is MarkerSample item)
                    ChangeOptions(s => start ? s with { Start = item.Marker } : s with { End = item.Marker });
            };
            return box;
        }
        MarkerSample[] MarkerItems(bool start) => Enum.GetValues<StrokeMarker>().Select(m => new MarkerSample(m, sampleCap, start)).ToArray();
        var start = MarkerControl("Start marker", true);
        var end = MarkerControl("End marker", false);
        start.Name = "VectorStartMarker"; end.Name = "VectorEndMarker";
        var markers = PropertyPair(Field("Start marker", start), Field("End marker", end));
        var separator = Ui.Separator(false);
        body.Children.Add(separator);
        body.Children.Add(markers);

        var (capPanel, selectCap) = StrokeSegments("VectorStrokeCap",
            ["Flat cap", "Round cap", "Square cap"], ["M2 6h12v8H2Z M14 3h1v14h-1Z", "M2 6h13a4 4 0 0 1 0 8H2Z", "M2 6h17v8H2Z"],
            (StrokeCap cap) => ChangeOptions(s => s with { Cap = cap }));
        var capField = Field("Stroke caps", capPanel);
        body.Children.Add(capField);

        var (joinPanel, selectJoin) = StrokeSegments("VectorStrokeJoin",
            ["Miter join", "Round join", "Bevel join"], ["M3 3h15v15h-3.6V6.6H3Z", "M3 3h7a8 8 0 0 1 8 8v7h-3.6v-7a4.4 4.4 0 0 0-4.4-4.4H3Z", "M3 3h9l6 6v9h-3.6v-7.5l-3.9-3.9H3Z"],
            (StrokeJoin join) => ChangeOptions(s => s with { Join = join }));
        var miter = Number("Miter angle", 29, 1, 180, v => ChangeOptions(s => s with { MiterAngle = v }, true));
        var angle = Field("Miter angle", Ui.Row(4, miter, Ui.Label("°", Palette.Secondary)));
        if (!line) body.Children.Add(PropertyPair(Field("Stroke joins", joinPanel), angle));

        return () =>
        {
            if (read() is not { } style) return;
            update = true;
            try
            {
                var settings = style.StrokeOptions ?? new VectorStroke();
                bool closed = ClosedShape(style);
                width.Value = (decimal)(line ? style.LineWidth : style.StrokeWidth);
                position.IsEnabled = closed;
                position.SelectedIndex = (int)(closed ? settings.Position : StrokePosition.Center);
                kind.SelectedIndex = style.Dash == null ? 0 : 1;
                dashFields.IsVisible = style.Dash != null;
                if (style.Dash is { } pattern)
                {
                    lastDash = pattern;
                    var factor = pattern.Relative ? line ? style.LineWidth : style.StrokeWidth : 1;
                    dash.Value = (decimal)Math.Min(pattern.Intervals[0] * factor, DocumentLimits.MaxSide);
                    gap.Value = (decimal)Math.Min(pattern.Intervals[1] * factor, DocumentLimits.MaxSide);
                    offset.Value = (decimal)pattern.Offset;
                }
                profile.SelectedIndex = (int)settings.Profile;
                markers.IsVisible = !closed;
                capField.IsVisible = !closed || style.Dash != null;
                var cap = StrokeCapOf(style);
                if (cap != sampleCap) { sampleCap = cap; start.ItemsSource = MarkerItems(true); end.ItemsSource = MarkerItems(false); }
                start.SelectedIndex = (int)settings.Start; end.SelectedIndex = (int)settings.End;
                ToolTip.SetTip(start, Locale.T(MarkerName(settings.Start)));
                ToolTip.SetTip(end, Locale.T(MarkerName(settings.End)));
                selectCap(cap); selectJoin(settings.Join);
                angle.IsVisible = settings.Join == StrokeJoin.Miter;
                miter.Value = (decimal)settings.MiterAngle;
            }
            finally { update = false; }
        };
    }

    private static (Border, Action<T>) StrokeSegments<T>(string name, string[] labels, string[] icons, Action<T> set) where T : struct, Enum
    {
        var values = Enum.GetValues<T>();
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 2 };
        var surfaces = new Border[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            T value = values[i];
            var button = new Button { Name = name + value, Classes = { "flat" }, MinHeight = 0, Padding = default,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new PathIcon { Data = Geometry.Parse(icons[i]), Width = 17, Height = 15, Foreground = Palette.Foreground } };
            AutomationProperties.SetName(button, Locale.T(labels[i]));
            Locale.Tip(button, labels[i]);
            button.Click += (_, _) => set(value);
            var surface = new Border { CornerRadius = new CornerRadius(3), Child = button };
            surfaces[i] = surface;
            AddAt(row, surface, i);
        }
        return (new Border { Background = Palette.Field, CornerRadius = new CornerRadius(4), Padding = new Thickness(2), Height = 27, Child = row },
            selected => { for (int i = 0; i < values.Length; i++) surfaces[i].Background = values[i].Equals(selected) ? Palette.Selected : Brushes.Transparent; });
    }
}
