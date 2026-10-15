using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.AI;

/// <summary>Second background pass: remove the temporary object only after object-aware scene layout.</summary>
public sealed class SceneBackgroundPlate : IDisposable
{
    public SKBitmap Image {get;}
    public SKBitmap Mask {get;}
    public SceneBackgroundPlate(SceneEditInput input,SKBitmap layout,int margin,CancellationToken token=default)
    {
        using var binary=Pixels.NewMask(layout.Width,layout.Height);
        var pixels=binary.GetPixelSpan();
        for(var y=0;y<layout.Height;y++)
        {
            token.ThrowIfCancellationRequested();
            for(var x=0;x<layout.Width;x++) pixels[y*binary.RowBytes+x]=input.ObjectAlpha.GetPixel(x,y).Alpha>0?(byte)255:(byte)0;
        }
        Pixels.Invalidate(binary);
        // A rectangular removal area gives no silhouette-shaped edge for the model to copy into the plate.
        var bounds=SelectionMask.Bounds(binary,1);var pad=Math.Clamp(margin,16,128);
        using var expanded=Pixels.NewMask(layout.Width,layout.Height);
        using(var canvas=new SKCanvas(expanded)) using(var paint=new SKPaint {Color=SKColors.White})
            canvas.DrawRect(SKRectI.Intersect(new(bounds.Left-pad,bounds.Top-pad,bounds.Right+pad,bounds.Bottom+pad),layout.Info.Rect),paint);
        Pixels.Invalidate(expanded);
        Mask=SelectionMask.Feather(expanded,Math.Clamp(pad/4,4,16));
        // Explicit missing-image region: neither the latent nor reference conditioning may contain object details.
        try
        {
            Image=Pixels.Clone(layout);
            for(var y=0;y<Image.Height;y++)
            {
                token.ThrowIfCancellationRequested();
                for(var x=0;x<Image.Width;x++) if(Mask.GetPixel(x,y).Alpha>=128) Image.SetPixel(x,y,new(128,128,128,255));
            }
            Pixels.Invalidate(Image);
        }
        catch {Image?.Dispose();Mask.Dispose();throw;}
    }
    public SKBitmap Finish(SKBitmap generated,SKBitmap layout,SKBitmap? reconstruction=null)
    {
        if(generated.Width!=layout.Width||generated.Height!=layout.Height) throw new InvalidDataException("Background cleanup changed image dimensions.");
        if(reconstruction!=null&&(reconstruction.Width!=layout.Width||reconstruction.Height!=layout.Height)) throw new InvalidDataException("Background reconstruction dimensions differ.");
        // A single coherent generated plate has no ROI join and never receives an original-subject VAE residual.
        return Pixels.Clone(generated);
    }
    public void Dispose() {Image.Dispose();Mask.Dispose();}
}
