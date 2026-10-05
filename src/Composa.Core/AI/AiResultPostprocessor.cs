using Composa.Rendering;
using SkiaSharp;

namespace Composa.AI;

/// <summary>Small deterministic finishing passes that make generated pixels agree with their immediate surroundings.</summary>
public static class AiResultPostprocessor
{
    /// <summary>Final edit coverage, distinct from the larger context/sampling mask. Never leaks beyond its allowed support.</summary>
    public static SKBitmap EditMask(SKBitmap selection, int grow, int blend)
    {
        using var support = grow > 0 ? Composa.Selections.SelectionMask.Expand(selection, grow) : Pixels.Clone(selection);
        var result = Pixels.Clone(support);
        if (blend <= 0) return result;
        // A Gaussian clipped to the selection jumps from ~50% to zero at a hard edge.
        // Feather INWARD instead. Two chamfer passes are linear-time, and use no pixel
        // neighbourhood search. Cap the radius for tiny masks so their core stays fully editable.
        var allowed = support.GetPixelSpan(); var mask = result.GetPixelSpan();
        var w = support.Width; var h = support.Height; const int far = 1_000_000;
        var distances = new int[checked(w * h)];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
            distances[y * w + x] = allowed[y * support.RowBytes + x] == 0 ? 0 : far;
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            if (x > 0) distances[i] = Math.Min(distances[i], distances[i - 1] + 3);
            if (y > 0) {
                distances[i] = Math.Min(distances[i], distances[i - w] + 3);
                if (x > 0) distances[i] = Math.Min(distances[i], distances[i - w - 1] + 4);
                if (x + 1 < w) distances[i] = Math.Min(distances[i], distances[i - w + 1] + 4);
            }
        }
        for (var y = h - 1; y >= 0; y--) for (var x = w - 1; x >= 0; x--)
        {
            var i = y * w + x;
            if (x + 1 < w) distances[i] = Math.Min(distances[i], distances[i + 1] + 3);
            if (y + 1 < h) {
                distances[i] = Math.Min(distances[i], distances[i + w] + 3);
                if (x > 0) distances[i] = Math.Min(distances[i], distances[i + w - 1] + 4);
                if (x + 1 < w) distances[i] = Math.Min(distances[i], distances[i + w + 1] + 4);
            }
        }
        var radius = Math.Max(1, Math.Min(3 * Math.Clamp(blend, 0, 512), distances.Max()));
        for (var y = 0; y < result.Height; y++)
        for (var x = 0; x < result.Width; x++)
        {
            var t = Math.Min(1.0, (double)distances[y * w + x] / radius);
            mask[y * result.RowBytes + x] = (byte)Math.Round(allowed[y * support.RowBytes + x] * t * t * (3 - 2 * t));
        }
        Pixels.Invalidate(result); return result;
    }

    public static SKBitmap ExpansionEditMask(SKBitmap emptyMask, SKBitmap context, int blend)
    {
        // Empty pixels fill fully. A narrow overlap fades into the real source,
        // rather than abruptly stopping the feather at the transparent boundary.
        var overlap = Math.Clamp(blend, 0, Math.Max(1, Math.Min(64, Math.Min(emptyMask.Width, emptyMask.Height) / 16)));
        var mask = EditMask(emptyMask, overlap, overlap);
        var coverage = mask.GetPixelSpan(); var empty = emptyMask.GetPixelSpan(); var pixels = context.GetPixelSpan();
        // Empty canvas has no underlying color to feather into. Do not leave translucent holes
        // or a black matte there; conditioning stays soft, but fully requested empty pixels fill fully.
        for (var y = 0; y < mask.Height; y++) for (var x = 0; x < mask.Width; x++)
            if (pixels[y * context.RowBytes + x * 4 + 3] == 0)
                coverage[y * mask.RowBytes + x] = empty[y * emptyMask.RowBytes + x];
        Pixels.Invalidate(mask); return mask;
    }

    public static SKBitmap Constrain(SKBitmap generated, SKBitmap context, SKBitmap mask)
    {
        if (generated.Width != context.Width || generated.Height != context.Height || mask.Width != context.Width || mask.Height != context.Height)
            throw new ArgumentException("Final edit mask, context and result must share pixel coordinates.");
        var result = Pixels.Clone(context); var output = result.GetPixelSpan(); var source = generated.GetPixelSpan(); var cover = mask.GetPixelSpan();
        for (var y = 0; y < result.Height; y++)
        for (var x = 0; x < result.Width; x++)
        {
            var amount = cover[y * mask.RowBytes + x];
            for (var c = 0; c < 4; c++)
            {
                var i = y * result.RowBytes + x * 4 + c;
                output[i] = (byte)((output[i] * (255 - amount) + source[y * generated.RowBytes + x * 4 + c] * amount + 127) / 255);
            }
        }
        Pixels.Invalidate(result); return result;
    }
    /// <summary>
    /// A stitched image already contains the soft transition. Reveal its changed pixels at full coverage,
    /// and leave identical pixels to the original layer. Multiplying by the soft selection again darkens
    /// or weakens the transition. The editable layer mask represents coverage, not a second blend.
    /// </summary>
    public static SKBitmap CompositedMask(SKBitmap generated, SKBitmap context, SKBitmap? support = null, SKBitmap? alphaEditRegion = null)
    {
        if (generated.Width != context.Width || generated.Height != context.Height)
            throw new ArgumentException("A composited result must match the original canvas dimensions.");
        if (support != null && (support.Width != context.Width || support.Height != context.Height)) throw new ArgumentException("Mask support must match the canvas.");
        if (alphaEditRegion != null && (alphaEditRegion.Width != context.Width || alphaEditRegion.Height != context.Height)) throw new ArgumentException("Alpha edit region must match the canvas.");
        var mask = Pixels.NewMask(context.Width, context.Height);
        unsafe
        {
            var result = (byte*)mask.GetPixels(); var original = (byte*)context.GetPixels(); var output = (byte*)generated.GetPixels();
            var coverage = support == null ? null : (byte*)support.GetPixels();
            var alphaRegion = alphaEditRegion == null ? null : (byte*)alphaEditRegion.GetPixels();
            for (var y = 0; y < context.Height; y++)
                for (var x = 0; x < context.Width; x++)
                {
                    if (coverage != null && coverage[(long)y * support!.RowBytes+x] == 0) continue;
                    var a = original+(long)y*context.RowBytes+x*4; var b = output+(long)y*generated.RowBytes+x*4;
                    var alphaEditable = alphaRegion == null || alphaRegion[(long)y*alphaEditRegion!.RowBytes+x] != 0;
                    // Comfy IMAGE nodes discard alpha. An unchanged transparent black pixel
                    // or unchanged straight color must not become opaque outside the edit.
                    var sameColor = a[3] == b[3] ? (*(uint*)a & 0xFFFFFF) == (*(uint*)b & 0xFFFFFF)
                        : a[3] == 0 ? (*(uint*)b & 0xFFFFFF) == 0 : b[3] == 0 ? (*(uint*)a & 0xFFFFFF) == 0
                        : Math.Abs(a[0]*b[3]-b[0]*a[3]) <= Math.Max(a[3],b[3])
                            && Math.Abs(a[1]*b[3]-b[1]*a[3]) <= Math.Max(a[3],b[3])
                            && Math.Abs(a[2]*b[3]-b[2]*a[3]) <= Math.Max(a[3],b[3]);
                    result[(long)y*mask.RowBytes+x] = !sameColor || alphaEditable && a[3] != b[3] ? (byte)255 : (byte)0;
                }
        }
        Pixels.Invalidate(mask); return mask;
    }

    public static SKBitmap MatchRemoval(SKBitmap generated, SKBitmap context, SKBitmap mask, long seed,
        SKRectI? generatedArea = null, double strength = 1)
    {
        if (generated.Width != context.Width || generated.Height != context.Height || mask.Width != context.Width || mask.Height != context.Height)
            return Pixels.Clone(generated);

        var bounds = SelectionBounds(mask);
        if (bounds.IsEmpty) return Pixels.Clone(generated);
        var radius = Math.Clamp(Math.Max(bounds.Width, bounds.Height) / 3, 8, 96);
        var ring = new SKRectI(Math.Max(0, bounds.Left - radius), Math.Max(0, bounds.Top - radius),
            Math.Min(context.Width, bounds.Right + radius), Math.Min(context.Height, bounds.Bottom + radius));
        if (generatedArea is { } area) ring = SKRectI.Intersect(ring, area);
        // A single average cannot correct opposing tone errors on different sides of a
        // patch. Extend a low-frequency correction from the SAME unchanged surrounding
        // pixels into the edit. No source subject pixels are used to recolor a new object.
        // An explicit decoded ROI gives reliable surrounding samples. Keep the legacy
        // conservative bound when a caller cannot identify what was actually generated.
        var tone = ToneField.Build(generated,context,mask,ring,generatedArea.HasValue ? 64 : 32);
        if (tone == null) return Pixels.Clone(generated);

        var result = Pixels.Clone(generated);
        unsafe
        {
            var source = (byte*)generated.GetPixels();
            var destination = (byte*)result.GetPixels();
            var coverage = (byte*)mask.GetPixels();
            for (var y = bounds.Top; y < bounds.Bottom; y++)
            {
                var sourceRow = source + (long)y * generated.RowBytes;
                var destinationRow = destination + (long)y * result.RowBytes;
                var maskRow = coverage + (long)y * mask.RowBytes;
                for (var x = bounds.Left; x < bounds.Right; x++)
                {
                    var amount = maskRow[x];
                    if (amount == 0) continue;
                    var offset = x * 4;
                    var alpha = sourceRow[offset + 3];
                    if (alpha == 0) continue;
                    for (var channel = 0; channel < 3; channel++)
                    {
                        var shift = tone.At(x,y,channel) * Math.Clamp(strength,0,1);
                        var straight = sourceRow[offset + channel] * 255.0 / alpha;
                        var matched = straight + shift;
                        // Local edge detail is not a noise estimate: lines and texture would
                        // become artificial grain across an otherwise clean reconstructed patch.
                        var adjusted = Math.Clamp((int)Math.Round(matched), 0, 255);
                        // Coverage belongs to the editable layer mask. Applying it here too
                        // weakens the correction twice precisely where a seam would be visible.
                        destinationRow[offset + channel] = (byte)((adjusted * alpha + 127) / 255);
                    }
                }
            }
        }
        Pixels.Invalidate(result);
        return result;
    }

    /// <summary>A bounded, coarse harmonic tone field. Adds smooth bias, never texture or gain.</summary>
    private sealed class ToneField(SKRectI area,int step,int width,int height,double[] values)
    {
        internal static ToneField? Build(SKBitmap generated,SKBitmap context,SKBitmap mask,SKRectI area,int limit)
        {
            if(area.IsEmpty) return null;
            var step=Math.Max(2,(Math.Max(area.Width,area.Height)+63)/64);
            var w=(area.Width+step-1)/step; var h=(area.Height+step-1)/step;
            var counts=new int[w*h]; var values=new double[w*h*3]; var sums=new double[3]; long count=0;
            for(var y=area.Top;y<area.Bottom;y++) for(var x=area.Left;x<area.Right;x++)
            {
                if (mask.GetPixel(x, y).Alpha > 0) continue;
                var a=context.GetPixel(x,y); var b=generated.GetPixel(x,y);
                if(a.Alpha==0 || b.Alpha==0) continue;
                var i=(y-area.Top)/step*w+(x-area.Left)/step; counts[i]++; count++;
                values[i*3]+=a.Red-b.Red; values[i*3+1]+=a.Green-b.Green; values[i*3+2]+=a.Blue-b.Blue;
                sums[0]+=a.Red-b.Red; sums[1]+=a.Green-b.Green; sums[2]+=a.Blue-b.Blue;
            }
            if(count==0) return null;
            for(var i=0;i<counts.Length;i++) for(var c=0;c<3;c++)
                values[i*3+c]=Math.Clamp(counts[i]>0?values[i*3+c]/counts[i]:sums[c]/count,-limit,limit);
            // Anchored cells are immutable. Alternating sweeps avoid directional bias;
            // coarse interpolation keeps model detail and grain out of the correction.
            for(var pass=0;pass<160;pass++)
                for(var row=0;row<h;row++) for(var col=0;col<w;col++)
                {
                    var y=pass%2==0?row:h-1-row; var x=pass%2==0?col:w-1-col; var i=y*w+x;
                    if(counts[i]>0) continue;
                    var neighbors=(x>0?1:0)+(x+1<w?1:0)+(y>0?1:0)+(y+1<h?1:0);
                    if(neighbors==0) continue;
                    for(var c=0;c<3;c++)
                        values[i*3+c]=((x>0?values[(i-1)*3+c]:0)+(x+1<w?values[(i+1)*3+c]:0)
                            +(y>0?values[(i-w)*3+c]:0)+(y+1<h?values[(i+w)*3+c]:0))/neighbors;
                }
            return new(area,step,w,h,values);
        }

        internal double At(int x,int y,int channel)
        {
            var px=Math.Clamp((x-area.Left+0.5)/step-0.5,0,width-1); var py=Math.Clamp((y-area.Top+0.5)/step-0.5,0,height-1);
            var left=(int)px; var top=(int)py; var right=Math.Min(left+1,width-1); var bottom=Math.Min(top+1,height-1);
            var dx=px-left; var dy=py-top;
            return (values[(top*width+left)*3+channel]*(1-dx)+values[(top*width+right)*3+channel]*dx)*(1-dy)
                +(values[(bottom*width+left)*3+channel]*(1-dx)+values[(bottom*width+right)*3+channel]*dx)*dy;
        }
    }

    private static SKRectI SelectionBounds(SKBitmap mask)
    {
        int left = mask.Width, top = mask.Height, right = 0, bottom = 0;
        for (var y = 0; y < mask.Height; y++)
            for (var x = 0; x < mask.Width; x++)
                if (mask.GetPixel(x, y).Alpha > 0)
                {
                    left = Math.Min(left, x); top = Math.Min(top, y);
                    right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1);
                }
        return left >= right || top >= bottom ? SKRectI.Empty : new SKRectI(left, top, right, bottom);
    }

}
