using Composa.ColorManagement;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    private static void HideChannelComponents(SKBitmap bitmap, RgbChannels visible)
    {
        var pixels = new PixelBuffer(bitmap);
        Parallel.For(0, bitmap.Height, y =>
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var p = pixels.Read(x, y); pixels.Write(x, y, new(visible.HasFlag(RgbChannels.Red) ? p.R : 0,
                    visible.HasFlag(RgbChannels.Green) ? p.G : 0, visible.HasFlag(RgbChannels.Blue) ? p.B : 0, p.A));
            }
        });
        Pixels.Invalidate(bitmap);
    }

    /// <summary>Display-only channel inspection. Exports, AI inputs and the composite cache retain actual RGB.</summary>
    private unsafe void RenderChannelView(SKBitmap target, SKRectI area, RenderView view)
    {
        area = Geometry.Intersect(area, new(0, 0, target.Width, target.Height)); if (area.IsEmpty) return;
        var localView = new RenderView(view.Scale, new(view.Origin.X + area.Left / view.Scale, view.Origin.Y + area.Top / view.Scale));
        using var shown = Pixels.NewWorkingColor(area.Width, area.Height, document.WorkingDepth, document.Profile);
        if (!IsEditingAlphaChannel || AlphaChannelOverlay) DocumentRenderer.Render(document, shown, new(0, 0, shown.Width, shown.Height), localView, CurrentOptions());
        if (IsEditingAlphaChannel && AlphaChannelOverlay && VisibleRgbChannels != RgbChannels.All) HideChannelComponents(shown, VisibleRgbChannels);
        using var coverage = IsEditingAlphaChannel ? Pixels.NewMask(area.Width, area.Height) : null;
        if (coverage != null)
        {
            using var canvas = new SKCanvas(coverage); canvas.Scale(view.Scale); canvas.Translate(-localView.Origin.X, -localView.Origin.Y);
            canvas.DrawImage(Pixels.ImageOf(document.AlphaChannels[EditingAlphaChannel!]), 0, 0, DocumentRenderer.SamplingFor(SKMatrix.CreateScale(view.Scale, view.Scale)));
        }
        var gray = IsEditingAlphaChannel && !AlphaChannelOverlay || !IsEditingAlphaChannel && Pixels.IsSingleChannel(VisibleRgbChannels);
        using var display = Pixels.NewWorkingColor(shown.Width, shown.Height, document.WorkingDepth, gray ? RgbProfile.Srgb : document.Profile);
        var before = new PixelBuffer(shown); var after = new PixelBuffer(display);
        var alphaData = coverage == null ? null : (byte*)coverage.GetPixels(); var alphaStride = coverage?.RowBytes ?? 0;
        Parallel.For(0, shown.Height, y =>
        {
            for (var x = 0; x < shown.Width; x++)
            {
                var p = before.Read(x, y);
                if (IsEditingAlphaChannel)
                {
                    var value = alphaData[y * alphaStride + x] / 255f;
                    if (AlphaChannelOverlay)
                    {
                        var amount = (1 - value) * .5f;
                        // A red rubylith overlay is a preview; the channel's coverage stays unchanged.
                        after.Write(x, y, new(amount + p.R * (1 - amount), p.G * (1 - amount), p.B * (1 - amount), amount + p.A * (1 - amount)));
                    }
                    else after.Write(x, y, new(value, value, value, 1));
                }
                else if (gray)
                {
                    var value = VisibleRgbChannels == RgbChannels.Red ? p.R : VisibleRgbChannels == RgbChannels.Green ? p.G : p.B;
                    after.Write(x, y, new(value, value, value, p.A));
                }
                else after.Write(x, y, new(VisibleRgbChannels.HasFlag(RgbChannels.Red) ? p.R : 0,
                    VisibleRgbChannels.HasFlag(RgbChannels.Green) ? p.G : 0, VisibleRgbChannels.HasFlag(RgbChannels.Blue) ? p.B : 0, p.A));
            }
        });
        using var output = new SKCanvas(target); output.ClipRect(new(area.Left, area.Top, area.Right, area.Bottom));
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        output.DrawImage(Pixels.ImageOf(display), area.Left, area.Top, paint); Pixels.Invalidate(target);
    }
}
