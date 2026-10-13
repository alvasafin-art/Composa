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
        using var expanded=SelectionMask.Expand(binary,Math.Clamp(margin,2,96));
        Mask=SelectionMask.Feather(expanded,4);
        // Removal is an actual image edit. Nearest-color hole filling would leave block/texture artifacts in the reference.
        try {Image=Pixels.Clone(layout);}
        catch {Mask.Dispose();throw;}
    }
    public SKBitmap Finish(SKBitmap generated,SKBitmap layout,SKBitmap? reconstruction=null)
    {
        if(generated.Width!=layout.Width||generated.Height!=layout.Height) throw new InvalidDataException("Background cleanup changed image dimensions.");
        var result=Pixels.Clone(layout);
        try
        {
            for(var y=0;y<result.Height;y++) for(var x=0;x<result.Width;x++)
            {
                var coverage=Mask.GetPixel(x,y).Alpha/255d; if(coverage==0) continue;
                var model=generated.GetPixel(x,y); var original=layout.GetPixel(x,y);
                var baseline=reconstruction?.GetPixel(x,y)??model; var conditioning=Image.GetPixel(x,y);
                byte Channel(byte value,byte old,byte encoded,byte source)
                {
                    var corrected=reconstruction==null?value:Math.Clamp(value+source-encoded,0,255);
                    return (byte)Math.Round(old*(1-coverage)+corrected*coverage);
                }
                result.SetPixel(x,y,new(Channel(model.Red,original.Red,baseline.Red,conditioning.Red),Channel(model.Green,original.Green,baseline.Green,conditioning.Green),Channel(model.Blue,original.Blue,baseline.Blue,conditioning.Blue),255));
            }
            Pixels.Invalidate(result); return result;
        }
        catch {result.Dispose();throw;}
    }
    public void Dispose() {Image.Dispose();Mask.Dispose();}
}
