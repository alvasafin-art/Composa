using Composa.Editing;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.AI;

/// <summary>Extracts lighting on the original silhouette and only model-created darkening on the permitted floor.</summary>
public sealed record SceneEditResult(IReadOnlyList<AiOutput> Layers, int ShadowPixels,int ContactCorrectionX=0,int ContactCorrectionY=0)
{
    public static SceneEditResult Extract(SceneEditInput input, SKBitmap generated, AiTaskRequest request, CancellationToken token = default,SKBitmap? reconstruction = null,double modelToDocument=1)
    {
        if(generated.Width!=input.Before.Width || generated.Height!=input.Before.Height)
            throw new InvalidDataException("The scene result must have the original document dimensions.");
        var settings=request.Scene.Normalized();
        var baseline=reconstruction??input.Before;
        if(baseline.Width!=generated.Width || baseline.Height!=generated.Height) throw new InvalidDataException("Scene reconstruction dimensions differ from the result.");
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
            var ratios=settings.PreserveDetails ? new SceneLightingMap(input,generated,baseline,token) : null;
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
                    var ratio=ratios?.At(x,y) ?? (1d,1d,1d);
                    var background=input.Background.GetPixel(x,y);
                    byte Channel(byte source,byte model,byte backdrop,double light)
                    {
                        var a=alpha/255d;
                        var edited=settings.PreserveDetails ? Linear(source)*light
                            : input.IsCutout ? Linear((byte)Math.Round(Math.Clamp((model-(1-a)*backdrop)/Math.Max(.01,a),0,255))) : Linear(model);
                        return Encode(Linear(source)*(1-amount)+edited*amount);
                    }
                    objectPixels.SetPixel(x,y,new(Channel(original.Red,desired.Red,background.Red,ratio.Item1),
                        Channel(original.Green,desired.Green,background.Green,ratio.Item2),Channel(original.Blue,desired.Blue,background.Blue,ratio.Item3),alpha));
                }
            }
            Pixels.Invalidate(objectPixels);
            if(input.WholeImage) return new(layers,0);
            using var rawShadow=ExtractShadow(input,generated,baseline,settings,ratios,modelToDocument,token,out var count);
            var shadow=SceneShadowContact.Anchor(rawShadow,input,modelToDocument,token,out count,out var correction);
            if(count>0) layers.Insert(0,new("Contact and Cast Shadow",shadow,Tags:["scene-shadow","editable"]));
            else shadow.Dispose();
            return new(layers,count,correction.X,correction.Y);
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
    private static SKBitmap ExtractShadow(SceneEditInput input,SKBitmap generated,SKBitmap baseline,SceneEditSettings settings,SceneLightingMap? lighting,double modelToDocument,CancellationToken token,out int count)
    {
        count=0;
        if(Composa.Selections.SelectionMask.IsEmpty(input.ShadowMask))return Pixels.NewColor(generated.Width,generated.Height);
        var calibration=new SceneBackgroundCalibration(input,generated,baseline,token);
        var w=generated.Width; var h=generated.Height;
        var coverage=new byte[checked(w*h)];
        var differences=new float[checked(w*h)];var weights=new float[differences.Length];var energy=new float[differences.Length];
        for(var y=0;y<h;y++)
        {
            token.ThrowIfCancellationRequested();
            for(var x=0;x<w;x++)
            {
                var permission=input.ShadowMask.GetPixel(x,y).Alpha;
                if(permission==0) continue;
                var source=baseline.GetPixel(x,y); var background=input.Background.GetPixel(x,y);
                if(source.Alpha<250 || background.Alpha<250) continue;
                var l=Luma(background); if(l<.01) continue;
                var model=generated.GetPixel(x,y); var foreground=input.Foreground.GetPixel(x,y); var a=input.ObjectAlpha.GetPixel(x,y).Alpha/255d;
                var gains=lighting?.At(x,y)??(1d,1d,1d);
                byte Background(byte observed,byte rebuilt,byte backdrop,byte original,double gain,int channel)
                {
                    // Recover the ground change from an encoded-RGB composite; PNG alpha applies exactly once later.
                    var calibrated=Encode(calibration.Correct(observed,channel));
                    var relit=Encode(calibration.Correct(Encode(Linear(original)*gain),channel));
                    var difference=(calibrated-rebuilt-a*(relit-original))/Math.Max(.06,1-a);
                    return (byte)Math.Round(Math.Clamp(backdrop+difference,0,255));
                }
                var desiredBackground=new SKColor(Background(model.Red,source.Red,background.Red,foreground.Red,gains.Item1,0),
                    Background(model.Green,source.Green,background.Green,foreground.Green,gains.Item2,1),Background(model.Blue,source.Blue,background.Blue,foreground.Blue,gains.Item3,2));
                differences[y*w+x]=(float)(l-Luma(desiredBackground));weights[y*w+x]=(float)l;energy[y*w+x]=Math.Abs(differences[y*w+x]);
            }
        }
        // Filter SIGNED differences before clamping to a shadow. Rectifying first gives every
        // zero-mean texture/codec fluctuation a dark bias and joins it to the real cast shadow.
        var radius=Math.Clamp((int)Math.Ceiling(2*modelToDocument),1,32);
        differences=Composa.Vision.GuidedFilter.Box(differences,w,h,radius);
        weights=Composa.Vision.GuidedFilter.Box(weights,w,h,radius);
        energy=Composa.Vision.GuidedFilter.Box(energy,w,h,radius);
        differences=Composa.Vision.GuidedFilter.Box(differences,w,h,radius);
        weights=Composa.Vision.GuidedFilter.Box(weights,w,h,radius);
        energy=Composa.Vision.GuidedFilter.Box(energy,w,h,radius);
        for(var y=0;y<h;y++)
        {
            token.ThrowIfCancellationRequested();
            for(var x=0;x<w;x++)
            {
                var permission=input.ShadowMask.GetPixel(x,y).Alpha;if(permission==0||weights[y*w+x]<.001)continue;
                var darkness=differences[y*w+x]/weights[y*w+x];
                // A cast is predominantly darkening; alternating bright/dark repaint edges are not.
                if(darkness<.04||differences[y*w+x]<energy[y*w+x]*.5f)continue;
                var l=Luma(input.Background.GetPixel(x,y));if(l<.01)continue;
                // Normal-over compositing attenuates encoded RGB, whereas the estimate is linear-light.
                // Using linear darkness directly as alpha would make a legitimate shadow nearly black.
                var attenuation=1-Srgb(l*(1-Math.Clamp(darkness,0,.9)))/Srgb(l);
                coverage[y*w+x]=(byte)Math.Round(Math.Clamp(attenuation,0,1)*permission*settings.Strength);
            }
        }
        // Component significance and contact are evaluated after smoothing, against the actual opaque support.
        var result=Pixels.NewColor(w,h);
        for(var y=0;y<h;y++) for(var x=0;x<w;x++) if(coverage[y*w+x]>0) {result.SetPixel(x,y,new(0,0,0,coverage[y*w+x]));count++;}
        Pixels.Invalidate(result);
        if(count==0) return result;
        using var soft=Pixels.NewColor(w,h);
        var sigma=.8f*(float)Math.Clamp(modelToDocument,.5,16);
        using(var draw=new SKCanvas(soft)) using(var blur=SKImageFilter.CreateBlur(sigma,sigma)) using(var paint=new SKPaint {ImageFilter=blur}) draw.DrawImage(Pixels.ImageOf(result),0,0,paint);
        Pixels.Invalidate(soft); count=0;
        for(var y=0;y<h;y++) for(var x=0;x<w;x++)
        {
            var alpha=Math.Min(soft.GetPixel(x,y).Alpha,input.ShadowMask.GetPixel(x,y).Alpha);
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
