using Composa.Editing;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.AI;

/// <summary>Extracts lighting on the original silhouette and only model-created darkening on the permitted floor.</summary>
public sealed record SceneEditResult(IReadOnlyList<AiOutput> Layers, int ShadowPixels)
{
    public static SceneEditResult Extract(SceneEditInput input, SKBitmap generated, AiTaskRequest request, CancellationToken token = default)
    {
        if(generated.Width!=input.Before.Width || generated.Height!=input.Before.Height)
            throw new InvalidDataException("The scene result must have the original document dimensions.");
        var settings=request.Scene.Normalized();
        var layers=new List<AiOutput>();
        try
        {
            if(request.Task==AiTaskKind.ChangeBackground)
            {
                layers.Add(new("New Background",Pixels.Clone(generated),Tags:["scene-background"]));
                layers.Add(new("Original Subject",Cutout(input.Foreground,input.ObjectAlpha,input.IsCutout),Tags:["scene-object","original-details"]));
                return new(layers,0);
            }
            var objectPixels=Pixels.NewColor(generated.Width,generated.Height);
            layers.Add(new(settings.PreserveDetails ? "Subject Lighting" : "Generated Subject",objectPixels,Tags:["scene-object",settings.PreserveDetails ? "original-details" : "generated-details"]));
            using var ratios=settings.PreserveDetails ? LightingRatios(input,generated,token) : null;
            for(var y=0;y<generated.Height;y++)
            {
                token.ThrowIfCancellationRequested();
                for(var x=0;x<generated.Width;x++)
                {
                    var original=input.Foreground.GetPixel(x,y); var alpha=input.ObjectAlpha.GetPixel(x,y).Alpha;
                    if(alpha==0) continue;
                    var target=input.SubjectMask.GetPixel(x,y).Alpha;
                    var amount=settings.Strength * Math.Min(1,target/(double)Math.Max(1,(int)alpha));
                    var desired=generated.GetPixel(x,y);
                    var ratio=ratios?.GetPixel(x,y) ?? default;
                    var background=input.Background.GetPixel(x,y);
                    byte Channel(byte source,byte model,byte backdrop,byte light)
                    {
                        var a=alpha/255d;
                        var edited=settings.PreserveDetails ? Linear(source)*light/63.75
                            : input.IsCutout ? Math.Clamp((Linear(model)-(1-a)*Linear(backdrop))/Math.Max(.01,a),0,1) : Linear(model);
                        return Encode(Linear(source)*(1-amount)+edited*amount);
                    }
                    objectPixels.SetPixel(x,y,new(Channel(original.Red,desired.Red,background.Red,ratio.Red),
                        Channel(original.Green,desired.Green,background.Green,ratio.Green),Channel(original.Blue,desired.Blue,background.Blue,ratio.Blue),alpha));
                }
            }
            Pixels.Invalidate(objectPixels);
            if(input.WholeImage) return new(layers,0);
            var shadow=ExtractShadow(input,generated,settings,token,out var count);
            if(count>0) layers.Insert(0,new("Contact and Cast Shadow",shadow,Tags:["scene-shadow","editable"]));
            else shadow.Dispose();
            return new(layers,count);
        }
        catch { foreach(var layer in layers) {layer.Pixels.Dispose();layer.Mask?.Dispose();} throw; }
    }

    private static SKBitmap Cutout(SKBitmap source,SKBitmap mask,bool alreadyMasked)
    {
        var result=Pixels.Clone(source);
        if(!alreadyMasked)
        {
            using var draw=new SKCanvas(result); using var paint=new SKPaint {BlendMode=SKBlendMode.DstIn};
            draw.DrawImage(Pixels.ImageOf(mask),0,0,paint); Pixels.Invalidate(result);
        }
        return result;
    }
    private static SKBitmap LightingRatios(SceneEditInput input,SKBitmap generated,CancellationToken token)
    {
        using var raw=Pixels.NewColor(generated.Width,generated.Height);
        for(var y=0;y<raw.Height;y++)
        {
            token.ThrowIfCancellationRequested();
            for(var x=0;x<raw.Width;x++)
            {
            var a=input.SubjectMask.GetPixel(x,y).Alpha; if(a==0) continue;
            var source=input.Before.GetPixel(x,y); var target=generated.GetPixel(x,y);
            static byte Ratio(byte s,byte t)=>(byte)Math.Round(63.75*Math.Clamp((Linear(t)+.003)/(Linear(s)+.003),.25,4));
            raw.SetPixel(x,y,new(Ratio(source.Red,target.Red),Ratio(source.Green,target.Green),Ratio(source.Blue,target.Blue),a));
            }
        }
        Pixels.Invalidate(raw);
        var smooth=Pixels.NewColor(raw.Width,raw.Height);
        var sigma=Math.Clamp(Math.Min(input.SubjectBounds.Width,input.SubjectBounds.Height)/100d,1,6);
        using(var draw=new SKCanvas(smooth)) using(var blur=SKImageFilter.CreateBlur((float)sigma,(float)sigma,SKShaderTileMode.Clamp))
        using(var paint=new SKPaint {ImageFilter=blur}) draw.DrawImage(Pixels.ImageOf(raw),0,0,paint);
        Pixels.Invalidate(smooth); return smooth;
    }
    private static SKBitmap ExtractShadow(SceneEditInput input,SKBitmap generated,SceneEditSettings settings,CancellationToken token,out int count)
    {
        count=0;
        // Estimate unchanged-background exposure drift before classifying a difference as a shadow.
        var samples=new List<double>(); var step=Math.Max(1,(int)Math.Sqrt(generated.Width*(double)generated.Height/8192));
        for(var y=0;y<generated.Height;y+=step) for(var x=0;x<generated.Width;x+=step)
        {
            if(input.ObjectAlpha.GetPixel(x,y).Alpha>0 || input.ShadowMask.GetPixel(x,y).Alpha>0) continue;
            var source=input.Before.GetPixel(x,y); var target=generated.GetPixel(x,y);
            var l=Luma(source); if(source.Alpha<250 || l<.03 || l>.85) continue;
            samples.Add(Luma(target)/l);
        }
        samples.Sort(); var exposure=samples.Count==0 ? 1 : Math.Clamp(samples[samples.Count/2],.7,1.4);
        var w=generated.Width; var h=generated.Height;
        var coverage=new byte[checked(w*h)]; var selected=new bool[coverage.Length];
        for(var y=0;y<h;y++)
        {
            token.ThrowIfCancellationRequested();
            for(var x=0;x<w;x++)
            {
                var permission=input.ShadowMask.GetPixel(x,y).Alpha;
                if(permission==0 || input.ObjectAlpha.GetPixel(x,y).Alpha>16) continue;
                var source=input.Before.GetPixel(x,y); if(source.Alpha<250) continue;
                var l=Luma(source); if(l<.01) continue;
                var darkness=1-Luma(generated.GetPixel(x,y))/exposure/l;
                if(darkness<.04) continue;
                // Normal-over compositing attenuates encoded RGB, whereas the estimate is linear-light.
                // Using linear darkness directly as alpha would make a legitimate shadow nearly black.
                var attenuation=1-Srgb(l*(1-Math.Clamp(darkness,0,.9)))/Srgb(l);
                coverage[y*w+x]=(byte)Math.Round(Math.Clamp(attenuation,0,1)*permission*settings.Strength);
            }
        }
        var b=input.SubjectBounds; var queue=new Queue<int>();
        var contact=Math.Max(8,b.Height/12);
        // Keep connected shadows near the support of the subject, rather than unrelated dark model noise.
        for(var y=Math.Max(0,b.Bottom-contact);y<Math.Min(h,b.Bottom+contact);y++)
            for(var x=Math.Max(0,b.Left-contact);x<Math.Min(w,b.Right+contact);x++)
                if(coverage[y*w+x]>0) {selected[y*w+x]=true;queue.Enqueue(y*w+x);}
        while(queue.Count>0)
        {
            var p=queue.Dequeue(); var x=p%w; var y=p/w;
            if((p&4095)==0) token.ThrowIfCancellationRequested();
            void Visit(int next) {if(next>=0 && coverage[next]>0 && !selected[next]) {selected[next]=true;queue.Enqueue(next);}}
            Visit(x>0?p-1:-1); Visit(x+1<w?p+1:-1); Visit(y>0?p-w:-1); Visit(y+1<h?p+w:-1);
        }
        var result=Pixels.NewColor(w,h);
        for(var y=0;y<h;y++) for(var x=0;x<w;x++) if(selected[y*w+x]) {result.SetPixel(x,y,new(0,0,0,coverage[y*w+x]));count++;}
        Pixels.Invalidate(result);
        if(count==0) return result;
        using var soft=Pixels.NewColor(w,h);
        using(var draw=new SKCanvas(soft)) using(var blur=SKImageFilter.CreateBlur(.8f,.8f)) using(var paint=new SKPaint {ImageFilter=blur}) draw.DrawImage(Pixels.ImageOf(result),0,0,paint);
        Pixels.Invalidate(soft); count=0;
        for(var y=0;y<h;y++) for(var x=0;x<w;x++)
        {
            var alpha=input.ObjectAlpha.GetPixel(x,y).Alpha>16 ? 0 : Math.Min(soft.GetPixel(x,y).Alpha,input.ShadowMask.GetPixel(x,y).Alpha);
            result.SetPixel(x,y,new(0,0,0,(byte)alpha)); if(alpha>0) count++;
        }
        Pixels.Invalidate(result); return result;
    }
    private static double Linear(byte value)
    {var v=value/255d;return v<=.04045 ? v/12.92 : Math.Pow((v+.055)/1.055,2.4);}
    private static double Luma(SKColor color)=>.2126*Linear(color.Red)+.7152*Linear(color.Green)+.0722*Linear(color.Blue);
    private static byte Encode(double value)
    =>(byte)Math.Round(255*Srgb(value));
    private static double Srgb(double value)
    {value=Math.Clamp(value,0,1);return value<=.0031308 ? 12.92*value : 1.055*Math.Pow(value,1/2.4)-.055;}
}
