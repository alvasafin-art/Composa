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
    public bool IsCutout { get; private set; }
    public bool WholeImage { get; private set; }
    public SKBitmap Before { get; private set; } = null!;
    public SKBitmap Background { get; private set; } = null!;
    public SKBitmap Foreground { get; private set; } = null!;
    public SKBitmap ObjectAlpha { get; private set; } = null!;
    public SKBitmap SubjectMask { get; private set; } = null!;
    public SKBitmap ShadowMask { get; private set; } = null!;
    public SKBitmap GenerationMask { get; private set; } = null!;
    public SKBitmap InitialImage { get; private set; } = null!;
    public SKRectI SubjectBounds { get; private set; }
    public SceneLightHint LightHint { get; private set; } = null!;

    public static SceneEditInput Prepare(Document document, AiTaskRequest request, CancellationToken token = default)
    {
        var result = new SceneEditInput();
        try { result.Build(document,request,token); return result; }
        catch { result.Dispose(); throw; }
    }

    private void Build(Document document, AiTaskRequest request, CancellationToken token)
    {
        var settings=request.Scene.Normalized();
        WholeImage=request.Task==AiTaskKind.Relight && settings.Scope==RelightScope.WholeImage;
        var active=document.ActiveLayer;
        if (!WholeImage && (active?.Pixels==null || active.IsAdjustment || active.IsGroup || !document.IsEffectivelyVisible(active)))
            throw new InvalidOperationException("Select a visible image layer, a transparent PNG, or choose Whole image for Relight.");
        if (!WholeImage && active!.Blend!=BlendMode.Normal)
            throw new InvalidOperationException("Scene editing needs a Normal image layer. Keep a copy of the original blend before converting it.");
        // New full-canvas intermediates plus the eventual output are bounded independently of model resolution.
        if (document.RasterPixels()+10L*document.Width*document.Height>DocumentLimits.DocumentPixelBudget)
            throw new InvalidOperationException($"Scene editing exceeds the document's {DocumentLimits.DocumentBudgetMegapixels} MP budget. Use a smaller document.");
        SourceLayerId=active?.Id;
        Before=Own(DocumentRenderer.Flatten(document));
        var backdrop=document.Clone();
        if(active!=null) backdrop.Find(active.Id)!.Visible=false;
        Background=Own(DocumentRenderer.Flatten(backdrop));
        IsCutout=!WholeImage && (active!.Mask!=null || active.VectorMask!=null || HasTransparency(active.Pixels!));
        if(IsCutout)
        {
            var isolated=new Document(document.Width,document.Height);
            var copy=active!.Clone(); copy.Effects=null; copy.Clipped=false;
            isolated.Layers.Add(copy);
            Foreground=Own(DocumentRenderer.Flatten(isolated));
            ObjectAlpha=Own(Alpha(Foreground));
            SubjectMask=Own(Pixels.Clone(ObjectAlpha));
            if(document.Selection!=null) Multiply(SubjectMask,document.Selection);
        }
        else
        {
            Foreground=Before;
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
        token.ThrowIfCancellationRequested();
        SubjectBounds=SelectionMask.Bounds(SubjectMask,16);
        if(SubjectBounds.IsEmpty || !WholeImage && HasNearFullCoverage(SubjectMask))
            throw new InvalidOperationException("The subject fills the image. Make a subject selection or use a transparent PNG.");
        LightHint=SceneLightHint.Measure(IsCutout ? Background : Before,ObjectAlpha,SubjectBounds,settings,request.Task);
        ShadowMask=Own(Pixels.NewMask(document.Width,document.Height));
        if(!WholeImage && request.Task!=AiTaskKind.ChangeBackground && settings.AddShadows && settings.MatchMode==SceneMatchMode.LightAndShadows)
        {
            var b=SubjectBounds;
            var reach=Math.Max(12,(int)Math.Round(b.Height*settings.ShadowReach));
            var floor=new SKRectI(Math.Max(0,b.Left-reach),Math.Max(0,b.Bottom-Math.Max(8,b.Height/8)),
                Math.Min(document.Width,b.Right+reach),Math.Min(document.Height,b.Bottom+reach));
            var mask=ShadowMask.GetPixelSpan(); var objectMask=ObjectAlpha.GetPixelSpan();
            for(var y=floor.Top;y<floor.Bottom;y++) for(var x=floor.Left;x<floor.Right;x++)
            {
                // A smooth permission region is not a manufactured shadow. Only model darkening is extracted later.
                var edge=Math.Min(Math.Min(x-floor.Left,floor.Right-1-x),Math.Min(y-floor.Top,floor.Bottom-1-y));
                if(LightHint.Directional)
                {
                    var dx=x-(b.Left+b.Right)/2d; var dy=y-b.Bottom;
                    var along=dx*LightHint.Direction.X+dy*LightHint.Direction.Y;
                    var across=Math.Abs(dx*LightHint.Direction.Y-dy*LightHint.Direction.X);
                    var contact=Math.Max(8,b.Width*.4);
                    if(along < -contact*.5 || across>contact+Math.Max(0,along)*.65) continue;
                }
                mask[y*ShadowMask.RowBytes+x]=(byte)(Math.Min(1,edge/8d)*(255-objectMask[y*ObjectAlpha.RowBytes+x]));
            }
            Pixels.Invalidate(ShadowMask);
        }
        GenerationMask=Own(Pixels.NewMask(document.Width,document.Height));
        var generation=GenerationMask.GetPixelSpan(); var subject=SubjectMask.GetPixelSpan(); var shadow=ShadowMask.GetPixelSpan();
        for(var y=0;y<document.Height;y++) for(var x=0;x<document.Width;x++)
            generation[y*GenerationMask.RowBytes+x]=request.Task==AiTaskKind.ChangeBackground ? (byte)255
                : (byte)Math.Max(subject[y*SubjectMask.RowBytes+x],shadow[y*ShadowMask.RowBytes+x]);
        Pixels.Invalidate(GenerationMask);
        InitialImage=request.Task==AiTaskKind.ChangeBackground ? IsCutout ? Background : Own(AiConditioningImage.Continue(Before,ObjectAlpha)) : Before;
    }

    public static bool HasTransparency(SKBitmap image)
    {
        var data=image.GetPixelSpan();
        for(var y=0;y<image.Height;y++) for(var x=0;x<image.Width;x++) if(data[y*image.RowBytes+x*4+3]<255) return true;
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
        var mask=Pixels.NewMask(image.Width,image.Height); var source=image.GetPixelSpan(); var target=mask.GetPixelSpan();
        for(var y=0;y<image.Height;y++) for(var x=0;x<image.Width;x++) target[y*mask.RowBytes+x]=source[y*image.RowBytes+x*4+3];
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
