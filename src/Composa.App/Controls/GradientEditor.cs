using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia;
using Composa.App.Dialogs;
using Composa.Model;
using Composa.ColorManagement;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Controls;

public sealed class GradientEditor : StackPanel
{
    private GradientRamp ramp;
    private readonly Window owner;
    private readonly Action<GradientRamp> changed;
    private readonly RampPreview preview;
    private readonly Window profileOwner;
    private int movingStop = -1;
    private bool moveOpacity;
    public GradientEditor(Window owner, GradientRamp ramp, Action<GradientRamp> changed, Window? profileOwner = null)
    {
        this.owner = owner; this.profileOwner = profileOwner ?? owner; this.ramp = ramp.Normalized(); this.changed = changed;
        preview = new(Ui.WorkingProfile(profileOwner ?? owner), Ui.DisplayProfile(profileOwner ?? owner)); Spacing = 8;
        preview.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(preview).Properties.IsLeftButtonPressed) return;
            var point=e.GetPosition(preview);
            moveOpacity=point.Y>preview.Bounds.Height-12;
            double position=preview.Position(point.X);
            var positions=moveOpacity ? ramp.Opacities.Select(s=>s.Position) : ramp.Colors.Select(s=>s.Position);
            movingStop=positions.Select((p,i)=>(Index:i,Distance:Math.Abs(p-position)*(preview.Bounds.Width-14)))
                .Where(s=>s.Distance<=7).OrderBy(s=>s.Distance).Select(s=>s.Index).DefaultIfEmpty(-1).First();
            if(movingStop<0)
            {
                if (positions.Count()>=64) return;
                var sampled=ramp.Normalized().Sample(position);
                if(moveOpacity) { movingStop=ramp.Opacities.Length; Set(ramp with { Opacities=[..ramp.Opacities,new(position,sampled.Alpha/255.0)] }); }
                else { movingStop=ramp.Colors.Length; Set(ramp with { Colors=[..ramp.Colors,new(position,(uint)sampled | 0xff000000)] }); }
            }
            e.Pointer.Capture(preview); e.Handled=true;
        };
        preview.PointerMoved += (_,e) =>
        {
            if(movingStop<0 || e.Pointer.Captured!=preview) return;
            double position=preview.Position(e.GetPosition(preview).X);
            if(moveOpacity)
            {
                var stops=ramp.Opacities.ToArray(); stops[movingStop]=stops[movingStop] with { Position=position }; Set(ramp with { Opacities=stops });
            }
            else
            {
                var stops=ramp.Colors.ToArray(); stops[movingStop]=stops[movingStop] with { Position=position }; Set(ramp with { Colors=stops });
            }
        };
        preview.PointerReleased += (_,e) => { if(e.Pointer.Captured==preview) e.Pointer.Capture(null); };
        preview.PointerCaptureLost += (_,_) => { if(movingStop>=0) { movingStop=-1; Build(); } };
        Locale.Tip(preview,"Click to add a stop or drag to move it. Color above, opacity below.");
        Build();
    }
    private void Set(GradientRamp next) { ramp = next; preview.Ramp = next.Normalized(); preview.InvalidateVisual(); changed(next.Normalized()); }
    private void Build()
    {
        Children.Clear();
        var preset = GradientRamp.Presets.FirstOrDefault(p => p.Value == ramp).Key ?? "Custom";
        Children.Add(Ui.Combo(new[] { "Custom" }.Concat(GradientRamp.Presets.Keys).ToArray(), preset, s => s, s => { if (s != "Custom") { Set(GradientRamp.Presets[s]); Build(); } }, 180));
        preview.Ramp = ramp.Normalized(); Children.Add(preview);
        Children.Add(Ui.Label(Locale.T("Color stops · positions in %"), Palette.Secondary));
        for (var index = 0; index < ramp.Colors.Length; index++)
        {
            var at = index; var stop = ramp.Colors[at];
            var sample = new ColorSwatch { Width = 18, Height = 16, CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(new SKColor(stop.Color).ToDisplay(Ui.WorkingProfile(profileOwner), Ui.DisplayProfile(profileOwner))) };
            var swatch = new Button { Content = Ui.Row(5, sample, Ui.Label(Locale.T("Color"))), MinWidth = 80 };
            swatch.Click += async (_, _) =>
            {
                if (await Prompts.Color(owner, "Gradient stop", new SKColor(ramp.Colors[at].Color)) is { } color)
                { var stops = ramp.Colors.ToArray(); stops[at] = stops[at] with { Color = (uint)color }; Set(ramp with { Colors = stops }); sample.Background = new SolidColorBrush(color.ToDisplay(Ui.WorkingProfile(profileOwner), Ui.DisplayProfile(profileOwner))); }
            };
            var position = Ui.Number(stop.Position * 100, 0, 100, v => { var stops = ramp.Colors.ToArray(); stops[at] = stops[at] with { Position = v / 100 }; Set(ramp with { Colors = stops }); }, width: 70);
            Children.Add(Ui.Row(8, swatch, position, Ui.TextButton("Remove", () => { if (ramp.Colors.Length > 2) { Set(ramp with { Colors = ramp.Colors.Where((_, i) => i != at).ToArray() }); Build(); } })));
        }
        Children.Add(Ui.TextButton("Add color stop", () => { if (ramp.Colors.Length < 64) { Set(ramp with { Colors = ramp.Colors.Append(new GradientColorStop(.5, (uint)ramp.Normalized().Sample(.5))).ToArray() }); Build(); } }));
        Children.Add(Ui.Label(Locale.T("Opacity stops · positions in %"), Palette.Secondary));
        for (var index = 0; index < ramp.Opacities.Length; index++)
        {
            var at = index; var stop = ramp.Opacities[at];
            var value = Ui.SliderField("Opacity", stop.Opacity * 100, 0, 100, v => { var stops = ramp.Opacities.ToArray(); stops[at] = stops[at] with { Opacity = v / 100 }; Set(ramp with { Opacities = stops }); }, width: 120);
            var position = Ui.Number(stop.Position * 100, 0, 100, v => { var stops = ramp.Opacities.ToArray(); stops[at] = stops[at] with { Position = v / 100 }; Set(ramp with { Opacities = stops }); }, width: 70);
            Children.Add(Ui.Row(8, value, position, Ui.TextButton("Remove", () => { if (ramp.Opacities.Length > 2) { Set(ramp with { Opacities = ramp.Opacities.Where((_, i) => i != at).ToArray() }); Build(); } })));
        }
        Children.Add(Ui.TextButton("Add opacity stop", () => { if (ramp.Opacities.Length < 64) { Set(ramp with { Opacities = ramp.Opacities.Append(new GradientOpacityStop(.5, .5)).ToArray() }); Build(); } }));
    }
    internal sealed class RampPreview : Control
    {
        private readonly RgbProfile profile;
        private readonly RgbProfile displayProfile;
        private Avalonia.Media.Imaging.Bitmap? bitmap;
        internal bool PairedStops { get; init; }
        internal double? SelectedPosition { get; set; }
        private GradientRamp stops = new();
        public double Position(double x) => Math.Clamp((x-7)/Math.Max(1,Bounds.Width-14),0,1);
        public GradientRamp Ramp
        {
            set
            {
                stops=value;
                using var image = Pixels.NewWorkingColor(286, 1, ColorDepth.Eight, profile);
                var bytes = image.GetPixelSpan();
                for (var x = 0; x < image.Width; x++)
                {
                    var color = value.Sample(x / (image.Width - 1d)); var at = x * 4;
                    bytes[at] = (byte)(color.Red * color.Alpha / 255); bytes[at + 1] = (byte)(color.Green * color.Alpha / 255);
                    bytes[at + 2] = (byte)(color.Blue * color.Alpha / 255); bytes[at + 3] = color.Alpha;
                }
                var next = Ui.ToAvaloniaBitmap(image, 286, displayProfile); var old = bitmap; bitmap = next; old?.Dispose();
            }
        }
        public RampPreview(RgbProfile profile, RgbProfile displayProfile) { this.profile = profile; this.displayProfile = displayProfile; Height = 44; Width = 300; }
        public override void Render(DrawingContext context)
        {
            context.DrawRectangle(Brushes.Transparent,null,new Rect(Bounds.Size));
            var dark = new SolidColorBrush(Color.Parse("#555555")); var light = new SolidColorBrush(Color.Parse("#888888"));
            var bar=new Rect(7,10,Math.Max(1,Bounds.Width-14),24);
            for (double y=bar.Y; y<bar.Bottom; y+=8)
                for (double x=bar.X; x<bar.Right; x+=8)
                    context.DrawRectangle((((int)(x-bar.X)/8+(int)(y-bar.Y)/8)%2==0) ? dark : light,null,
                        new Rect(x,y,Math.Min(8,bar.Right-x),Math.Min(8,bar.Bottom-y)));
            if (bitmap != null) context.DrawImage(bitmap,bar);
            foreach (var stop in stops.Colors)
            {
                var color = new SKColor(stop.Color).ToDisplay(profile, displayProfile);
                context.DrawEllipse(new SolidColorBrush(color), ColorSwatch.NeedsEdge(color, Palette.Window.Color) ? ColorSwatch.ContrastPen : null,
                    new Point(bar.X+stop.Position*bar.Width,8),5,5);
            }
            if (PairedStops)
            {
                if (SelectedPosition is {} selected)
                    context.DrawEllipse(null,new Pen(Palette.Accent,2),new Point(bar.X+selected*bar.Width,8),6,6);
                return;
            }
            foreach (var stop in stops.Opacities)
            {
                byte level=(byte)Math.Round(stop.Opacity*255);
                var color = Color.FromRgb(level, level, level);
                context.DrawRectangle(new SolidColorBrush(color), ColorSwatch.NeedsEdge(color, Palette.Window.Color) ? ColorSwatch.ContrastPen : null,
                    new Rect(bar.X+stop.Position*bar.Width-4,34,8,8),1,1);
            }
        }
    }
}
