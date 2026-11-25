using Composa.Editing;
using Composa.Rendering;
using Composa.ColorManagement;
using SkiaSharp;

namespace Composa.AI;

/// <summary>Extracts lighting on the original silhouette and only model-created darkening on the permitted floor.</summary>
public sealed record SceneEditResult(IReadOnlyList<AiOutput> Layers, int ShadowPixels,int ContactCorrectionX=0,int ContactCorrectionY=0)
{
    public static SceneEditResult Extract(SceneEditInput input, SKBitmap generated, AiTaskRequest request, CancellationToken token = default,SKBitmap? reconstruction = null,double modelToDocument=1,bool useMaterialAwareLighting=false)
    {
        if (!Pixels.IsSrgb8(generated) || reconstruction != null && !Pixels.IsSrgb8(reconstruction))
        {
            using var model = !Pixels.IsSrgb8(generated) ? Pixels.DisplayCopy(generated) : null;
            using var baselineCopy = reconstruction != null && !Pixels.IsSrgb8(reconstruction) ? Pixels.DisplayCopy(reconstruction) : null;
            return Extract(input, model ?? generated, request, token, baselineCopy ?? reconstruction, modelToDocument, useMaterialAwareLighting);
        }
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
            var highPrecision=!Pixels.IsSrgb8(input.Foreground);
            var objectPixels=highPrecision?Pixels.NewLike(input.Foreground):Pixels.NewColor(generated.Width,generated.Height);
            var originalPixels=new PixelBuffer(input.Foreground); var highOutput=new PixelBuffer(objectPixels);
            var working=Pixels.ProfileOf(input.Foreground);
            var transfer=working.Transfer;
            var toModel=new LinearRgbTransform(working,RgbProfile.Srgb); var toWorking=new LinearRgbTransform(RgbProfile.Srgb,working);
            layers.Add(new(settings.PreserveDetails ? "Subject Lighting" : "Generated Subject",objectPixels,Tags:["scene-object",settings.PreserveDetails ? "original-details" : "generated-details"]));
            var ratios=settings.PreserveDetails ? new SceneLightingMap(input,generated,baseline,token,useMaterialAwareLighting) : null;
            for(var y=0;y<generated.Height;y++)
            {
                token.ThrowIfCancellationRequested();
                for(var x=0;x<generated.Width;x++)
                {
                    if(highPrecision)
                    {
                        var source=originalPixels.Read(x,y).Straight;
                        var a=input.IsCutout||input.WholeImage?source.A:input.ObjectAlpha.GetPixel(x,y).Alpha/255f;
                        if(a<=0)continue;
                        var highAmount=settings.Strength*Math.Min(1,input.SubjectMask.GetPixel(x,y).Alpha/255d/Math.Max(.000001,a));
                        var model=generated.GetPixel(x,y); var backdrop=input.Background.GetPixel(x,y);
                        var highRatio=ratios?.At(x,y)??(1d,1d,1d);
                        var r=transfer.Decode(source.R,0); var g=transfer.Decode(source.G,1); var b=transfer.Decode(source.B,2);
                        (double R,double G,double B) edited;
                        if(settings.PreserveDetails)
                        {
                            var rgb=toModel.Map(r,g,b);
                            edited=toWorking.Map(rgb.R*highRatio.Item1,rgb.G*highRatio.Item2,rgb.B*highRatio.Item3);
                        }
                        else
                        {
                            double Subject(byte desired,byte background)=>LinearFloat(input.IsCutout?
                                Math.Clamp((desired/255d-(1-a)*background/255d)/Math.Max(.000001,a),0,1):desired/255d);
                            edited=toWorking.Map(Subject(model.Red,backdrop.Red),Subject(model.Green,backdrop.Green),Subject(model.Blue,backdrop.Blue));
                        }
                        highOutput.Write(x,y,new PixelValue((float)Math.Clamp(transfer.Encode(r*(1-highAmount)+edited.R*highAmount,0),0,1),
                            (float)Math.Clamp(transfer.Encode(g*(1-highAmount)+edited.G*highAmount,1),0,1),
                            (float)Math.Clamp(transfer.Encode(b*(1-highAmount)+edited.B*highAmount,2),0,1),a).Premultiplied);
                        continue;
                    }
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
            var harmonize=request.Task is AiTaskKind.Harmonize or AiTaskKind.MatchToScene;
            using var rawShadow=ExtractShadow(input,generated,baseline,settings,ratios,modelToDocument,harmonize,token,out var count);
            var shadow=SceneShadowContact.Anchor(rawShadow,input,modelToDocument,token,out count,out var correction,preserveConnectedContact:harmonize);
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
    private static SKBitmap ExtractShadow(SceneEditInput input,SKBitmap generated,SKBitmap baseline,SceneEditSettings settings,SceneLightingMap? lighting,double modelToDocument,bool harmonize,CancellationToken token,out int count)
    {
        count=0;
        if(Composa.Selections.SelectionMask.IsEmpty(input.ShadowMask))return Pixels.NewColor(generated.Width,generated.Height);
        var calibration=new SceneBackgroundCalibration(input,generated,baseline,token);
        var w=generated.Width; var h=generated.Height;
        var coverage=new byte[checked(w*h)];
        var colors=new SKColor[coverage.Length];
        using var measured=Pixels.NewMask(w,h);
        var candidates=harmonize?new byte[coverage.Length]:null;
        var differences=new float[checked(w*h)];var darkness=new float[differences.Length];var energy=new float[differences.Length];
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
                var model=generated.GetPixel(x,y); var a=input.ObjectAlpha.GetPixel(x,y).Alpha/255d;
                // The model can recolor a dark subject more than the detail-preserving lighting
                // gains can represent. Measuring that mixed fringe transfers the subject's color
                // to the shadow. For Harmonize extend nearby measured ground through it instead.
                if(harmonize ? a>0 || model.Alpha<250 : a>.5)continue;
                measured.SetPixel(x,y,SKColors.White);
                var foreground=a>0?input.ModelForeground.GetPixel(x,y):default;
                var gains=a>0?lighting?.At(x,y)??(1d,1d,1d):(1d,1d,1d);
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
                differences[y*w+x]=(float)(l-Luma(desiredBackground));
                darkness[y*w+x]=differences[y*w+x]/(float)l;energy[y*w+x]=Math.Abs(differences[y*w+x]);
                // A neutral black layer loses colored ambient fill. Solve normal-over in encoded
                // RGB: choose the smallest alpha that can reproduce all three ground channels.
                static double Loss(byte before,byte after)=>before>0?1-after/(double)before:0;
                var opacity=Math.Clamp(Math.Max(Loss(background.Red,desiredBackground.Red),
                    Math.Max(Loss(background.Green,desiredBackground.Green),Loss(background.Blue,desiredBackground.Blue))),0,1);
                byte Color(byte before,byte after)=>(byte)Math.Round(Math.Clamp((after-(1-opacity)*before)/Math.Max(1e-6,opacity),0,255));
                colors[y*w+x]=new(Color(background.Red,desiredBackground.Red),Color(background.Green,desiredBackground.Green),
                    Color(background.Blue,desiredBackground.Blue),(byte)Math.Round(opacity*255));
            }
        }
        // Filter SIGNED differences before clamping to a shadow. Rectifying first gives every
        // zero-mean texture/codec fluctuation a dark bias and joins it to the real cast shadow.
        var radius=Math.Clamp((int)Math.Ceiling(2*modelToDocument),1,32);
        differences=Composa.Vision.GuidedFilter.Box(differences,w,h,radius);
        energy=Composa.Vision.GuidedFilter.Box(energy,w,h,radius);
        differences=Composa.Vision.GuidedFilter.Box(differences,w,h,radius);
        energy=Composa.Vision.GuidedFilter.Box(energy,w,h,radius);
        for(var y=0;y<h;y++)
        {
            token.ThrowIfCancellationRequested();
            for(var x=0;x<w;x++)
            {
                var permission=input.ShadowMask.GetPixel(x,y).Alpha;if(permission==0)continue;
                // A cast is predominantly darkening; alternating bright/dark repaint edges are not.
                // Use the neighborhood only to reject incoherent texture. Thresholding its
                // average erased narrow/faint contact and soft penumbrae in preview.29.
                var localDarkness=darkness[y*w+x];
                if(localDarkness<(harmonize?.01:.04)||differences[y*w+x]<energy[y*w+x]*.5f)continue;
                var l=Luma(input.Background.GetPixel(x,y));if(l<.01)continue;
                // Normal-over compositing attenuates encoded RGB, whereas the estimate is linear-light.
                // Using linear darkness directly as alpha would make a legitimate shadow nearly black.
                coverage[y*w+x]=(byte)Math.Round(colors[y*w+x].Alpha*(permission/255d)*settings.Strength);
                if(candidates!=null)candidates[y*w+x]=localDarkness>=.04?(byte)2:(byte)1;
            }
        }
        if(candidates!=null)
        {
            // Hysteresis keeps weak contact/penumbra only when connected to convincing darkening.
            // A hard per-pixel cutoff cuts holes into that transition; lowering it globally keeps noise.
            var queue=new Queue<int>();
            for(var p=0;p<candidates.Length;p++)if(candidates[p]==2)queue.Enqueue(p);
            var visited=0;
            while(queue.Count>0)
            {
                var p=queue.Dequeue();if((++visited&4095)==0)token.ThrowIfCancellationRequested();
                var x=p%w;var y=p/w;
                for(var dy=-1;dy<=1;dy++)for(var dx=-1;dx<=1;dx++)
                {
                    var nx=x+dx;var ny=y+dy;if(nx<0||ny<0||nx>=w||ny>=h)continue;
                    var next=ny*w+nx;if(candidates[next]!=1)continue;
                    candidates[next]=2;queue.Enqueue(next);
                }
            }
            for(var p=0;p<coverage.Length;p++)if(candidates[p]!=2)coverage[p]=0;
        }
        if(harmonize) ExtendShadowFringe(input,measured,colors,coverage,modelToDocument,token);
        // Component significance and contact are evaluated after smoothing, against the actual opaque support.
        var result=Pixels.NewColor(w,h);
        Pixels.Invalidate(measured);
        for(var y=0;y<h;y++) for(var x=0;x<w;x++) if(coverage[y*w+x]>0) {result.SetPixel(x,y,colors[y*w+x].WithAlpha(coverage[y*w+x]));count++;}
        Pixels.Invalidate(result);
        if(count==0) return result;
        using var soft=Pixels.NewColor(w,h);
        using var softPermission=Pixels.NewMask(w,h);
        var sigma=.8f*(float)Math.Clamp(modelToDocument,.5,16);
        using(var blur=SKImageFilter.CreateBlur(sigma,sigma)) using(var paint=new SKPaint {ImageFilter=blur})
        {
            using(var draw=new SKCanvas(soft))draw.DrawImage(Pixels.ImageOf(result),0,0,paint);
            using(var draw=new SKCanvas(softPermission))draw.DrawImage(Pixels.ImageOf(measured),0,0,paint);
            Pixels.Invalidate(softPermission);
        }
        Pixels.Invalidate(soft); count=0;
        for(var y=0;y<h;y++) for(var x=0;x<w;x++)
        {
            var color=soft.GetPixel(x,y);var alpha=(int)color.Alpha;
            // The hidden object interior is missing ground data, not a zero-strength shadow.
            // Normalize the blur so a sharp PNG edge does not acquire a lighter contact seam.
            var weight=softPermission.GetPixel(x,y).Alpha;
            alpha=weight>0?Math.Clamp((int)Math.Round(alpha*255d/weight),0,255):0;
            alpha=Math.Min(alpha,input.ShadowMask.GetPixel(x,y).Alpha);
            result.SetPixel(x,y,color.WithAlpha((byte)alpha)); if(alpha>0) count++;
        }
        Pixels.Invalidate(result); return result;
    }
    private static void ExtendShadowFringe(SceneEditInput input, SKBitmap measured, SKColor[] colors, byte[] coverage,
        double modelToDocument, CancellationToken token)
    {
        var width = measured.Width;
        var height = measured.Height;
        var reach = Math.Clamp((int)Math.Ceiling(4 * modelToDocument), 4, 24);
        var offsets = new (int X, int Y)[] { (0, 1), (-1, 0), (1, 0), (0, -1) };
        var frontier = new Queue<(int X, int Y, byte Distance)>();

        // Seed only from accepted ground. Extend the full encoded color and coverage before the
        // blur so faint premultiplied samples don't quantize the hue during edge normalization.
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
                if (coverage[y * width + x] > 0)
                    ExtendNeighbors(x, y, 1);
        }
        while (frontier.TryDequeue(out var pixel))
        {
            token.ThrowIfCancellationRequested();
            if (pixel.Distance < reach)
                ExtendNeighbors(pixel.X, pixel.Y, (byte)(pixel.Distance + 1));
        }

        void ExtendNeighbors(int x, int y, byte distance)
        {
            if (distance == 1 && input.ObjectAlpha.GetPixel(x, y).Alpha != 0) return;
            foreach (var (dx, dy) in offsets)
            {
                var nx = x + dx;
                var ny = y + dy;
                if ((uint)nx >= (uint)width || (uint)ny >= (uint)height ||
                    measured.GetPixel(nx, ny).Alpha > 0 ||
                    input.ObjectAlpha.GetPixel(nx, ny).Alpha == 0 ||
                    input.ShadowMask.GetPixel(nx, ny).Alpha == 0) continue;

                var source = y * width + x;
                var destination = ny * width + nx;
                colors[destination] = colors[source];
                coverage[destination] = coverage[source];
                measured.SetPixel(nx, ny, SKColors.White);
                frontier.Enqueue((nx, ny, distance));
            }
        }
    }

    private static double Linear(byte value)
    {var v=value/255d;return v<=.04045 ? v/12.92 : Math.Pow((v+.055)/1.055,2.4);}
    private static double LinearFloat(double value)=>value<=.04045?value/12.92:Math.Pow((value+.055)/1.055,2.4);
    private static double Luma(SKColor color)=>.2126*Linear(color.Red)+.7152*Linear(color.Green)+.0722*Linear(color.Blue);
    private static byte Encode(double value)
    =>(byte)Math.Round(255*Srgb(value));
    private static double Srgb(double value)
    {value=Math.Clamp(value,0,1);return value<=.0031308 ? 12.92*value : 1.055*Math.Pow(value,1/2.4)-.055;}
}
