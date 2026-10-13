using System.Text.Json.Nodes;
using Composa.AI;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.AI;

internal sealed class SceneEditingWorkflow : IDisposable
{
    internal SKBitmap Source { get; }
    internal SKBitmap Mask { get; }
    internal (int Width,int Height) ContentSize { get; }
    internal (int Width,int Height) PaddedSize { get; }
    private readonly SceneEditInput scene;
    internal List<SKBitmap> ReferenceImages { get; } = [];

    internal SceneEditingWorkflow(SceneEditInput scene,AiTaskRequest request,SKBitmap? imageOverride=null,SKBitmap? maskOverride=null)
    {
        this.scene=scene;
        var initial=imageOverride??scene.InitialImage;
        var original=Convert.ToBoolean(request.Settings.Values.GetValueOrDefault("imageOriginalSize")??true);
        var native=original ? AiDimensions.OriginalGenerationSize(scene.Before.Width,scene.Before.Height)
            : (Math.Max(64,request.Settings.Width),Math.Max(64,request.Settings.Height));
        var scale=Math.Sqrt(native.Item1*(double)native.Item2/(scene.Before.Width*(double)scene.Before.Height));
        scale=Math.Max(scale,64d/Math.Min(scene.Before.Width,scene.Before.Height));
        ContentSize=((int)Math.Round(scene.Before.Width*scale),(int)Math.Round(scene.Before.Height*scale));
        PaddedSize=((ContentSize.Width+15)/16*16,(ContentSize.Height+15)/16*16);
        if(!Composa.Model.DocumentLimits.FitsSurface(PaddedSize.Width,PaddedSize.Height))
            throw new InvalidOperationException($"Scene input exceeds the {Composa.Model.DocumentLimits.MaxSurfaceMegapixels} MP / {Composa.Model.DocumentLimits.MaxSide} px limit.");
        try
        {
        Source=Pixels.NewColor(PaddedSize.Width,PaddedSize.Height); Source.Erase(SKColors.White);
        using(var draw=new SKCanvas(Source))
        {
            draw.DrawImage(Pixels.ImageOf(initial),new SKRect(0,0,ContentSize.Width,ContentSize.Height),new SKSamplingOptions(SKCubicResampler.Mitchell));
            if(PaddedSize.Width>ContentSize.Width)
                draw.DrawImage(Pixels.ImageOf(initial),new SKRect(initial.Width-1,0,initial.Width,initial.Height),
                    new SKRect(ContentSize.Width,0,PaddedSize.Width,ContentSize.Height),new SKSamplingOptions(SKFilterMode.Nearest));
            if(PaddedSize.Height>ContentSize.Height)
                draw.DrawImage(Pixels.ImageOf(initial),new SKRect(0,initial.Height-1,initial.Width,initial.Height),
                    new SKRect(0,ContentSize.Height,PaddedSize.Width,PaddedSize.Height),new SKSamplingOptions(SKFilterMode.Nearest));
        }
        Pixels.Invalidate(Source);
        Mask=Pixels.NewMask(PaddedSize.Width,PaddedSize.Height);
        using(var draw=new SKCanvas(Mask)) draw.DrawImage(Pixels.ImageOf(maskOverride??scene.GenerationMask),new SKRect(0,0,ContentSize.Width,ContentSize.Height),new SKSamplingOptions(SKFilterMode.Linear));
        if(maskOverride==null && request.Task!=AiTaskKind.ChangeBackground && !scene.WholeImage)
        {
            // Preserve a model-space contour anchor; the model may change lighting, not the silhouette/support.
            using var subject=Pixels.NewMask(PaddedSize.Width,PaddedSize.Height);
            using(var draw=new SKCanvas(subject)) draw.DrawImage(Pixels.ImageOf(scene.ObjectAlpha),SKRect.Create(ContentSize.Width,ContentSize.Height),new SKSamplingOptions(SKFilterMode.Linear));
            Pixels.Invalidate(subject);
            using var opaque=Pixels.NewMask(PaddedSize.Width,PaddedSize.Height);
            var opaquePixels=opaque.GetPixelSpan();
            for(var y=0;y<ContentSize.Height;y++) for(var x=0;x<ContentSize.Width;x++) opaquePixels[y*opaque.RowBytes+x]=subject.GetPixel(x,y).Alpha>=scene.SupportAlpha?(byte)255:(byte)0;
            Pixels.Invalidate(opaque);
            using var interior=SelectionMask.Contract(opaque,Math.Clamp(Math.Min(ContentSize.Width,ContentSize.Height)/200,2,4))??Pixels.NewMask(PaddedSize.Width,PaddedSize.Height);
            var mask=Mask.GetPixelSpan();
            for(var y=0;y<ContentSize.Height;y++) for(var x=0;x<ContentSize.Width;x++)
                if(opaque.GetPixel(x,y).Alpha>0) mask[y*Mask.RowBytes+x]=Math.Min(mask[y*Mask.RowBytes+x],interior.GetPixel(x,y).Alpha);
        }
        Pixels.Invalidate(Mask);
        foreach(var reference in request.ReferenceImages)
        {
            ReferenceImages.Add(CopyReference(reference,request.ReferenceMegapixels));
        }
        }
        catch {Source?.Dispose();Mask?.Dispose();foreach(var reference in ReferenceImages) reference.Dispose();throw;}
    }
    internal void Bind(JsonObject graph,string source,string mask,AiTaskRequest request,int variant=0,bool cleanup=false)
    {
        graph["source"]!["inputs"]!["image"]=source;
        graph["maskImage"]!["inputs"]!["image"]=mask;
        graph["prompt"]!["inputs"]!["text"]=cleanup ? CleanupPrompt(request) : Prompt(request)+(request.Task!=AiTaskKind.ChangeBackground ? "\nLighting guidance: "+scene.LightHint.Instruction : "");
        graph["sampler"]!["inputs"]!["denoise"]=cleanup ? 1 : request.Scene.Normalized().Denoise;
        graph["sampler"]!["inputs"]!["seed"]=request.Settings.Seed<0 ? Random.Shared.NextInt64(long.MaxValue) : (request.Settings.Seed+variant)&long.MaxValue;
    }
    internal SKBitmap Finish(SKBitmap decoded)
    {
        using(decoded)
        {
            if((decoded.Width,decoded.Height)!=PaddedSize)
                throw new InvalidDataException($"Scene workflow returned {decoded.Width} × {decoded.Height}; expected {PaddedSize.Width} × {PaddedSize.Height}. No edit was applied.");
            using var content=new SKBitmap();
            if(!decoded.ExtractSubset(content,SKRectI.Create(ContentSize.Width,ContentSize.Height))) throw new InvalidDataException("Cannot remove technical scene padding.");
            var image=Pixels.NewColor(scene.Before.Width,scene.Before.Height);
            try
            {
                using(var draw=new SKCanvas(image)) draw.DrawImage(Pixels.ImageOf(content),SKRect.Create(image.Width,image.Height),new SKSamplingOptions(SKCubicResampler.Mitchell));
                Pixels.Invalidate(image); return image;
            }
            catch {image.Dispose();throw;}
        }
    }
    internal static void References(JsonObject graph,IReadOnlyDictionary<string,string> files)
    {
        var positive="positive"; var negative="negativeReference";
        for(var index=1;index<=6;index++)
        {
            if(!files.TryGetValue("referenceImage"+index,out var file)) continue;
            var load="scene_reference_"+index; var encode=load+"_encode";
            graph[load]=new JsonObject {["class_type"]="LoadImage",["inputs"]=new JsonObject {["image"]=file}};
            graph[encode]=new JsonObject {["class_type"]="VAEEncode",["inputs"]=new JsonObject {["pixels"]=new JsonArray(load,0),["vae"]=new JsonArray("vae",0)}};
            foreach(var pair in new[] {(Name:load+"_positive",Previous:positive),(Name:load+"_negative",Previous:negative)})
                graph[pair.Name]=new JsonObject {["class_type"]="ReferenceLatent",["inputs"]=new JsonObject {["conditioning"]=new JsonArray(pair.Previous,0),["latent"]=new JsonArray(encode,0)}};
            positive=load+"_positive"; negative=load+"_negative";
        }
        graph["sampler"]!["inputs"]!["positive"]=new JsonArray(positive,0);
        graph["sampler"]!["inputs"]!["negative"]=new JsonArray(negative,0);
    }
    internal static string Prompt(AiTaskRequest request)
    {
        var settings=request.Scene.Normalized();
        var operation=request.Task==AiTaskKind.ChangeBackground
            ? "Replace the background around the existing foreground subject with the requested environment. Use the subject's actual scale, material, position and support points to compose a believable scene. Keep the subject in exactly the same position and shape. Build the support surface directly against its base, with a soft grounded contact shadow."
            : request.Task==AiTaskKind.Relight
                ? "Change the lighting of "+(settings.Scope==RelightScope.WholeImage ? "the image" : "the foreground subject")+". "+settings.LightingInstruction
                : "Match the existing foreground subject's illumination to the visible background: light direction, exposure, color temperature and reflected light. Keep its exact position, dimensions and support points. Preserve the surrounding scene. Lighting changes belong on its surfaces.";
        var shadow=request.Task==AiTaskKind.ChangeBackground ? "" : settings.AddShadows && settings.MatchMode==SceneMatchMode.LightAndShadows
            ? " Generate a realistic contact and cast shadow from the subject onto the ground, consistent with scene lighting and surface perspective."
            : " Do not add a new shadow outside the subject.";
        return operation+shadow+" Preserve identity, material texture, labels and colors. Use photographic surface shading, without luminous outlines, bloom, haze or light leaking outside the silhouette. Do not move, resize or duplicate the object, add a platform, or leave a gap under its base. User guidance: "+request.Prompt+
            (string.IsNullOrWhiteSpace(request.AdditionalPrompt) ? "" : "\nWorkflow guidance: "+request.AdditionalPrompt);
    }
    internal static string CleanupPrompt(AiTaskRequest request)=>"Erase the foreground product/person inside the edit mask completely. Reconstruct the unobstructed background and continuous support surface behind it, matching the surrounding photographic texture. Keep the existing cast shadow, camera framing, perspective, lighting and every unmasked pixel unchanged. Return an empty background plate without the removed subject, a duplicate, silhouette, cutout hole, platform or luminous outline.";
    internal static SKBitmap CopyReference(SKBitmap reference,double? megapixels)
    {
        var scale=megapixels is { } mp ? Math.Min(1,Math.Sqrt(Math.Clamp(mp,.1,4)*1_000_000/(reference.Width*(double)reference.Height))) : 1;
        scale=Math.Max(scale,16d/Math.Min(reference.Width,reference.Height));
        var width=(int)Math.Round(reference.Width*scale); var height=(int)Math.Round(reference.Height*scale);
        if(!Composa.Model.DocumentLimits.FitsSurface(width,height)) throw new InvalidOperationException("The reference proportions exceed the model image limits.");
        var copy=Pixels.NewColor(width,height);
        try
        {
            copy.Erase(SKColors.White);
            using(var draw=new SKCanvas(copy)) draw.DrawImage(Pixels.ImageOf(reference),SKRect.Create(copy.Width,copy.Height),new SKSamplingOptions(SKCubicResampler.Mitchell));
            Pixels.Invalidate(copy); return copy;
        }
        catch {copy.Dispose();throw;}
    }
    public void Dispose() {Source.Dispose();Mask.Dispose();foreach(var reference in ReferenceImages) reference.Dispose();}
}
