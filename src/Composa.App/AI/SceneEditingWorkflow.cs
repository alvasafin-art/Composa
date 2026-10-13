using System.Text.Json.Nodes;
using Composa.AI;
using Composa.Rendering;
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

    internal SceneEditingWorkflow(SceneEditInput scene,AiTaskRequest request)
    {
        this.scene=scene;
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
            draw.DrawImage(Pixels.ImageOf(scene.InitialImage),new SKRect(0,0,ContentSize.Width,ContentSize.Height),new SKSamplingOptions(SKCubicResampler.Mitchell));
            if(PaddedSize.Width>ContentSize.Width)
                draw.DrawImage(Pixels.ImageOf(scene.InitialImage),new SKRect(scene.InitialImage.Width-1,0,scene.InitialImage.Width,scene.InitialImage.Height),
                    new SKRect(ContentSize.Width,0,PaddedSize.Width,ContentSize.Height),new SKSamplingOptions(SKFilterMode.Nearest));
            if(PaddedSize.Height>ContentSize.Height)
                draw.DrawImage(Pixels.ImageOf(scene.InitialImage),new SKRect(0,scene.InitialImage.Height-1,scene.InitialImage.Width,scene.InitialImage.Height),
                    new SKRect(0,ContentSize.Height,PaddedSize.Width,PaddedSize.Height),new SKSamplingOptions(SKFilterMode.Nearest));
        }
        Pixels.Invalidate(Source);
        Mask=Pixels.NewMask(PaddedSize.Width,PaddedSize.Height);
        using(var draw=new SKCanvas(Mask)) draw.DrawImage(Pixels.ImageOf(scene.GenerationMask),new SKRect(0,0,ContentSize.Width,ContentSize.Height),new SKSamplingOptions(SKFilterMode.Linear));
        Pixels.Invalidate(Mask);
        foreach(var reference in request.ReferenceImages)
        {
            ReferenceImages.Add(CopyReference(reference,request.ReferenceMegapixels));
        }
        }
        catch {Source?.Dispose();Mask?.Dispose();foreach(var reference in ReferenceImages) reference.Dispose();throw;}
    }
    internal void Bind(JsonObject graph,string source,string mask,AiTaskRequest request,int variant=0)
    {
        graph["source"]!["inputs"]!["image"]=source;
        graph["maskImage"]!["inputs"]!["image"]=mask;
        graph["prompt"]!["inputs"]!["text"]=Prompt(request)+(request.Task!=AiTaskKind.ChangeBackground ? "\nLighting guidance: "+scene.LightHint.Instruction : "");
        graph["sampler"]!["inputs"]!["denoise"]=request.Scene.Normalized().Denoise;
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
            ? "Produce only a clean photographic background environment for the scene described below. Remove the foreground subject completely. No person, duplicate subject, silhouette or empty cutout shape: reconstruct the environment where the subject was. The original subject will be composited separately without changes."
            : request.Task==AiTaskKind.Relight
                ? "Change the lighting of "+(settings.Scope==RelightScope.WholeImage ? "the image" : "the foreground subject")+". "+settings.LightingInstruction
                : "Replace the original studio illumination on the foreground subject with the lighting of this exact background. The subject must receive the scene's warm or cool ambient cast, dominant light direction, new highlights, reflected light and exposure. In backlit scenes use natural edge light and subdued frontal illumination. Keep the background unchanged. Integrate the subject physically with the existing support surface.";
        var shadow=request.Task==AiTaskKind.ChangeBackground ? "" : settings.AddShadows && settings.MatchMode==SceneMatchMode.LightAndShadows
            ? " Generate a realistic contact and cast shadow from the subject onto the ground, consistent with scene lighting and surface perspective."
            : " Do not add a new shadow outside the subject.";
        return operation+shadow+" Preserve the subject's exact silhouette, pose, face, identity, clothing, logos, text and material texture unless the operation explicitly removes it. No new objects, duplicated limbs, halos or fog. User guidance: "+request.Prompt+
            (string.IsNullOrWhiteSpace(request.AdditionalPrompt) ? "" : "\nWorkflow guidance: "+request.AdditionalPrompt);
    }
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
