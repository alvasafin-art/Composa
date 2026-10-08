using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.AI;

internal static class NanoBanana
{
    public const string Model = "Nano Banana 2 (Gemini 3.1 Flash Image)";
    public static bool IsModel(string? model) => model == Model;
    public static string Resolution(string? value) => value switch
    {
        null or "low" or "1K" => "1K", "2K" => "2K", "4K" => "4K",
        _ => throw new InvalidOperationException("Choose a supported Nano Banana resolution: 1K, 2K or 4K.")
    };
    private static readonly (int W,int H)[] Ratios=[(1,1),(2,3),(3,2),(3,4),(4,3),(4,5),(5,4),(9,16),(16,9),(21,9),(1,4),(4,1),(8,1),(1,8)];
    public static (int W,int H) Ratio(int width,int height) => Ratios.MinBy(r=>Math.Abs(Math.Log((double)r.W/r.H/((double)width/height))));
    public static string Aspect(int width,int height) { var r=Ratio(width,height); return $"{r.W}:{r.H}"; }

    public static SKBitmap Pad(SKBitmap source,out SKRectI content)
    {
        var r=Ratio(source.Width,source.Height); var units=(int)Math.Ceiling(Math.Max((double)source.Width/r.W,(double)source.Height/r.H));
        var width=r.W*units; var height=r.H*units;
        if(!Composa.Model.DocumentLimits.FitsSurface(width,height)) throw new InvalidOperationException("Nano Banana reference exceeds document limits.");
        var output=Pixels.NewColor(width,height); var x=(width-source.Width)/2; var y=(height-source.Height)/2;
        content=new(x,y,x+source.Width,y+source.Height);
        using var canvas=new SKCanvas(output); using var image=SKImage.FromBitmap(source);
        // Extend edge colors into technical padding; keep every source pixel unchanged.
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        var edges = new SKRect[] { new(0,0,1,source.Height),new(source.Width-1,0,source.Width,source.Height),new(0,0,source.Width,1),new(0,source.Height-1,source.Width,source.Height) };
        var bands = new SKRect[] { new(0,y,x,y+source.Height),new(x+source.Width,y,width,y+source.Height),new(0,0,width,y),new(0,y+source.Height,width,height) };
        for (var i=0;i<edges.Length;i++) if (!bands[i].IsEmpty)
            canvas.DrawImage(image,edges[i],bands[i],new SKSamplingOptions(SKFilterMode.Nearest),paint);
        canvas.DrawImage(image,x,y,paint); return output;
    }

    public static SKBitmap Unpad(SKBitmap generated,SKRectI content,SKSizeI padded)
    {
        var aspect=(double)generated.Width/generated.Height/((double)padded.Width/padded.Height);
        if(Math.Abs(aspect-1)>.03) { generated.Dispose(); throw new InvalidDataException("Nano Banana returned an unexpected aspect ratio. No result was applied."); }
        var width=Math.Max(1,(int)Math.Round(content.Width*(double)generated.Width/padded.Width));
        var height=Math.Max(1,(int)Math.Round(content.Height*(double)generated.Height/padded.Height));
        var result=Pixels.NewColor(width,height);
        using(var canvas=new SKCanvas(result))
        {
            if (generated.Width == padded.Width && generated.Height == padded.Height)
                canvas.DrawImage(Pixels.ImageOf(generated),-content.Left,-content.Top);
            else
                canvas.DrawImage(Pixels.ImageOf(generated),new SKRect(content.Left*(float)generated.Width/padded.Width,content.Top*(float)generated.Height/padded.Height,
                    content.Right*(float)generated.Width/padded.Width,content.Bottom*(float)generated.Height/padded.Height),SKRect.Create(width,height),new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        generated.Dispose(); return result;
    }
}
