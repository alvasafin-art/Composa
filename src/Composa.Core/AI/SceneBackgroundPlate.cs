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
        // Remove the object with a padded silhouette, not its entire bounding rectangle:
        // scenery between limbs/handles and beside a diagonal product is useful context.
        var pad=Math.Clamp(margin,8,64);
        using var expanded=SelectionMask.Expand(binary,pad);
        Mask=SelectionMask.Feather(expanded,Math.Clamp(pad/3,3,12));
        // Keep the actual arranged scene as the editing reference. Gray-filled silhouettes can be
        // interpreted as real scene content and survive as a color patch after masked generation.
        try
        {
            Image=Pixels.Clone(layout);
            token.ThrowIfCancellationRequested();
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
