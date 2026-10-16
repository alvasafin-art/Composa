using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.AI;

/// <summary>Immutable document snapshot and independently owned scene assets. Selection and source pixels never change.</summary>
public sealed class SceneEditInput : IDisposable
{
    private readonly List<SKBitmap> owned = [];
    private SKBitmap Own(SKBitmap bitmap) { owned.Add(bitmap); return bitmap; }
    public Guid? SourceLayerId { get; private set; }
    public IReadOnlyList<Guid> PreviousResultIds { get; private set; } = [];
    public SKMatrix SourceFrame {get;private set;}=SKMatrix.Identity;
    public bool IsCutout { get; private set; }
    public bool WholeImage { get; private set; }
    public SKBitmap Before { get; private set; } = null!;
    public SKBitmap Background { get; private set; } = null!;
    public SKBitmap Foreground { get; private set; } = null!;
    public SKBitmap ModelForeground { get; private set; } = null!;
    public SKBitmap ObjectAlpha { get; private set; } = null!;
    public SKBitmap SubjectMask { get; private set; } = null!;
    public SKBitmap ShadowMask { get; private set; } = null!;
    public SKBitmap GenerationMask { get; private set; } = null!;
    public SKBitmap InitialImage { get; private set; } = null!;
    public SKRectI SubjectBounds { get; private set; }
    public SKRectI SupportBounds {get;private set;}
    public byte SupportAlpha {get;private set;}
    public int[] SupportY {get;private set;} = [];
    public SceneLightHint LightHint { get; private set; } = null!;

    public static SceneEditInput Prepare(Document document, AiTaskRequest request, CancellationToken token = default)
    {
        var result = new SceneEditInput();
        try { result.Build(document,request,token); return result; }
        catch { result.Dispose(); throw; }
    }

    private void Build(Document document, AiTaskRequest request, CancellationToken token)
    {
        // Validate lighting color transforms before a model upload or paid generation starts.
        _ = document.Profile.Transfer;
        _ = new Composa.ColorManagement.LinearRgbTransform(document.Profile, Composa.ColorManagement.RgbProfile.Srgb);
        _ = new Composa.ColorManagement.LinearRgbTransform(Composa.ColorManagement.RgbProfile.Srgb, document.Profile);
        var settings=request.Scene.Normalized();
        WholeImage=request.Task==AiTaskKind.Relight && settings.Scope==RelightScope.WholeImage;
        var selected=SceneSource.SelectedObject(document);
        var active=WholeImage?document.ActiveLayer:SceneSource.Original(document,selected);
        if (!WholeImage && (active?.Pixels==null || active.IsAdjustment || active.IsGroup || selected==null || !document.IsEffectivelyVisible(selected)))
            throw new InvalidOperationException("Select a visible image layer, a transparent PNG, or choose Whole image for Relight.");
        if (!WholeImage && active!.Blend!=BlendMode.Normal)
            throw new InvalidOperationException("Scene editing needs a Normal image layer. Keep a copy of the original blend before converting it.");
        // New full-canvas intermediates plus the eventual output are bounded independently of model resolution.
        if (document.RasterPixels()+10L*document.Width*document.Height*(document.WorkingDepth==ColorDepth.Sixteen?4:1)>DocumentLimits.DocumentPixelBudget)
            throw new InvalidOperationException($"Scene editing exceeds the document's {DocumentLimits.DocumentBudgetMegapixels} MP budget. Use a smaller document.");
        SourceLayerId=active?.Id;
        if(!WholeImage) PreviousResultIds=SceneSource.PreviousResults(document,selected!,active!);
        if(!WholeImage)SourceFrame=SceneSource.Placement(selected!,active!);
        var clean=document.Clone();
        foreach(var id in PreviousResultIds) clean.Find(id)!.Visible=false;
        if(!WholeImage&&active!=null) {var source=clean.Find(active.Id)!;source.Visible=true;source.Effects=null;source.Transform=SceneSource.Transform(SourceFrame,source.Pixels!.Width,source.Pixels.Height);}
        var workingBefore=Own(DocumentRenderer.Flatten(clean));
        Before=Own(Pixels.DisplayCopy(workingBefore));
        var backdrop=clean.Clone();
        if(active!=null) backdrop.Find(active.Id)!.Visible=false;
        using(var workingBackdrop=DocumentRenderer.Flatten(backdrop)) Background=Own(Pixels.DisplayCopy(workingBackdrop));
        IsCutout=!WholeImage && (active!.Mask!=null || active.VectorMask!=null || HasTransparency(active.Pixels!));
        if(IsCutout)
        {
            var isolated=new Document(document.Width,document.Height) {Depth=document.WorkingDepth,Profile=document.Profile};
            var copy=clean.Find(active!.Id)!.Clone(); copy.Effects=null; copy.Clipped=false; copy.Visible=true;
            isolated.Layers.Add(copy);
            Foreground=Own(DocumentRenderer.Flatten(isolated));
            ObjectAlpha=Own(Alpha(Foreground));
            SubjectMask=Own(Pixels.Clone(ObjectAlpha));
            if(document.Selection!=null) Multiply(SubjectMask,document.Selection);
        }
        else
        {
            Foreground=workingBefore;
            // In an opaque photograph the ground belongs to that same raster, not the layers underneath it.
            if(!WholeImage) Background=Before;
            if(WholeImage) SubjectMask=Own(Alpha(Before));
            else if(document.Selection!=null) SubjectMask=Own(Pixels.Clone(document.Selection));
            else
            {
                if(!ModelRunner.CanRun(PromptModels.EfficientSamS.Encoder) || !ModelRunner.CanRun(PromptModels.EfficientSamS.Decoder))
                    throw new InvalidOperationException("Select the subject first, or install SAM Quality to find it automatically.");
                SubjectMask=Own(SubjectFinder.Matte(Before,SubjectDetect.Any,token)
                    ?? throw new InvalidOperationException("No subject found. Make a selection or use a transparent PNG."));
            }
            ObjectAlpha=SubjectMask;
        }
        ModelForeground=Pixels.IsSrgb8(Foreground)?Foreground:Own(Pixels.DisplayCopy(Foreground));
        token.ThrowIfCancellationRequested();
        SubjectBounds=SelectionMask.Bounds(SubjectMask,16);
        if(SubjectBounds.IsEmpty || !WholeImage && HasNearFullCoverage(SubjectMask))
            throw new InvalidOperationException("The subject fills the image. Make a subject selection or use a transparent PNG.");
        byte maximum=0; foreach(var value in ObjectAlpha.GetPixelSpan()) maximum=Math.Max(maximum,value);
        // A glass product may have an opaque cap and a translucent base. Use the base's coverage, not its cap.
        var objectBounds=SelectionMask.Bounds(ObjectAlpha,16); byte baseMaximum=0;
        for(var y=Math.Max(objectBounds.Top,objectBounds.Bottom-objectBounds.Height/4);y<objectBounds.Bottom;y++)
            for(var x=objectBounds.Left;x<objectBounds.Right;x++) baseMaximum=Math.Max(baseMaximum,ObjectAlpha.GetPixel(x,y).Alpha);
        if(baseMaximum>16) maximum=baseMaximum;
        SupportAlpha=(byte)Math.Max(1,(int)Math.Round(maximum*.94));
        SupportBounds=SelectionMask.Bounds(ObjectAlpha,SupportAlpha);
        SupportY=Enumerable.Repeat(-1,document.Width).ToArray();
        for(var x=SupportBounds.Left;x<SupportBounds.Right;x++) for(var y=SupportBounds.Bottom-1;y>=SupportBounds.Top;y--)
            if(ObjectAlpha.GetPixel(x,y).Alpha>=SupportAlpha) {SupportY[x]=y;break;}
        LightHint=SceneLightHint.Measure(IsCutout ? Background : Before,ObjectAlpha,SubjectBounds,settings,request.Task);
        ShadowMask=Own(Pixels.NewMask(document.Width,document.Height));
        if(!WholeImage && request.Task!=AiTaskKind.ChangeBackground && settings.AddShadows && settings.MatchMode==SceneMatchMode.LightAndShadows)
        {
            var b=SupportBounds;
            // A cast shadow may extend toward the background or to a canvas edge. Do not invent a floor crop.
            var reach=Math.Max(12,(int)Math.Ceiling(Math.Max(document.Width,document.Height)*settings.ShadowReach));
            var floor=new SKRectI(Math.Max(0,b.Left-reach),Math.Max(0,b.Top-reach),
                Math.Min(document.Width,b.Right+reach),Math.Min(document.Height,b.Bottom+reach));
            var mask=ShadowMask.GetPixelSpan(); var objectMask=ObjectAlpha.GetPixelSpan();
            for(var y=floor.Top;y<floor.Bottom;y++) for(var x=floor.Left;x<floor.Right;x++)
            {
                // A smooth permission region is not a manufactured shadow. Only model darkening is extracted later.
                var alpha=objectMask[y*ObjectAlpha.RowBytes+x];
                if(alpha>=SupportAlpha) continue;
                // The foreground's alpha already attenuates an underlaid shadow. Never cut out its AA fringe twice.
                var support=SupportY[x]>=0?SupportY[x]:b.Bottom-1;
                if(alpha>0 && y<=support) continue;
                mask[y*ShadowMask.RowBytes+x]=255;
            }
            Pixels.Invalidate(ShadowMask);
        }
        GenerationMask=Own(Pixels.NewMask(document.Width,document.Height));
        var generation=GenerationMask.GetPixelSpan(); var subject=SubjectMask.GetPixelSpan(); var shadow=ShadowMask.GetPixelSpan();
        for(var y=0;y<document.Height;y++) for(var x=0;x<document.Width;x++)
            generation[y*GenerationMask.RowBytes+x]=request.Task==AiTaskKind.ChangeBackground ? (byte)(255-ObjectAlpha.GetPixel(x,y).Alpha)
                : (byte)Math.Max(subject[y*SubjectMask.RowBytes+x],shadow[y*ShadowMask.RowBytes+x]);
        Pixels.Invalidate(GenerationMask);
        // Background layout must see the actual object and its placement. A separate cleanup pass removes its cache.
        InitialImage=Before;
    }

    public static bool HasTransparency(SKBitmap image)
    {
        var data=new PixelBuffer(image);
        for(var y=0;y<image.Height;y++) for(var x=0;x<image.Width;x++) if(data.Read(x,y).A<1) return true;
        return false;
    }
    private static bool HasNearFullCoverage(SKBitmap mask)
    {
        long count=0; var pixels=mask.GetPixelSpan();
        for(var y=0;y<mask.Height;y++) for(var x=0;x<mask.Width;x++) if(pixels[y*mask.RowBytes+x]>=128) count++;
        return count>mask.Width*(long)mask.Height*.99;
    }
    public static SKBitmap Alpha(SKBitmap image)
    {
        var mask=Pixels.NewMask(image.Width,image.Height); var source=new PixelBuffer(image); var target=mask.GetPixelSpan();
        for(var y=0;y<image.Height;y++) for(var x=0;x<image.Width;x++) target[y*mask.RowBytes+x]=(byte)Math.Clamp(MathF.Round(source.Read(x,y).A*255),0,255);
        Pixels.Invalidate(mask); return mask;
    }
    private static void Multiply(SKBitmap target,SKBitmap other)
    {
        var a=target.GetPixelSpan(); var b=other.GetPixelSpan();
        for(var y=0;y<target.Height;y++) for(var x=0;x<target.Width;x++) a[y*target.RowBytes+x]=(byte)(a[y*target.RowBytes+x]*b[y*other.RowBytes+x]/255);
        Pixels.Invalidate(target);
    }
    public void Dispose() { foreach(var bitmap in owned) bitmap.Dispose(); owned.Clear(); }
}
