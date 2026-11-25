using System.Text.Json.Nodes;
using Composa.AI;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.AI;

internal sealed class SceneEditingWorkflow : IDisposable
{
    internal const string HarmonizePrompt="Blend the object naturally into the scene, matching the existing lighting, realistic shadow depth, contact shadows, and subtle reflections of the environment so it feels physically present and fully integrated\n";
    internal SKBitmap Source { get; }
    internal SKBitmap Mask { get; }
    internal SKBitmap? BackgroundReference { get; }
    internal (int Width,int Height) ContentSize { get; }
    internal (int Width,int Height) PaddedSize { get; }
    private readonly SceneEditInput scene;
    internal List<SKBitmap> ReferenceImages { get; } = [];

    internal SceneEditingWorkflow(SceneEditInput scene,AiTaskRequest request,SKBitmap? imageOverride=null,SKBitmap? maskOverride=null,bool useImageReferences=false)
    {
        this.scene=scene;
        var initial=imageOverride??scene.InitialImage;
        var original=Convert.ToBoolean(request.Settings.Values.GetValueOrDefault("imageOriginalSize")??true);
        var native=original ? AiDimensions.OriginalGenerationSize(scene.Before.Width,scene.Before.Height)
            : (Math.Max(64,request.Settings.Width),Math.Max(64,request.Settings.Height));
        var scale=Math.Sqrt(native.Item1*(double)native.Item2/(scene.Before.Width*(double)scene.Before.Height));
        scale=Math.Max(scale,64d/Math.Min(scene.Before.Width,scene.Before.Height));
        ContentSize=((int)Math.Round(scene.Before.Width*scale),(int)Math.Round(scene.Before.Height*scale));
        var grid=useImageReferences?32:16;
        PaddedSize=((ContentSize.Width+grid-1)/grid*grid,(ContentSize.Height+grid-1)/grid*grid);
        if(!Composa.Model.DocumentLimits.FitsSurface(PaddedSize.Width,PaddedSize.Height))
            throw new InvalidOperationException($"Scene input exceeds the {Composa.Model.DocumentLimits.MaxSurfaceMegapixels} MP / {Composa.Model.DocumentLimits.MaxSide} px limit.");
        try
        {
        Source=Pixels.NewColor(PaddedSize.Width,PaddedSize.Height); Source.Erase(SKColors.White);
        using(var draw=new SKCanvas(Source))
        {
            var sampling=ContentSize==(initial.Width,initial.Height) ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKCubicResampler.Mitchell);
            draw.DrawImage(Pixels.ImageOf(initial),new SKRect(0,0,ContentSize.Width,ContentSize.Height),sampling);
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
        // The final subject uses the original PNG alpha. Freezing a model-space contour ring
        // instead preserves the old studio lighting and leaves a moat beside the new shadow.
        Pixels.Invalidate(Mask);
        if(!useImageReferences&&maskOverride==null&&request.Task!=AiTaskKind.ChangeBackground&&!scene.WholeImage)
        {
            var backgroundScale=Math.Min(.5,512d/Math.Max(ContentSize.Width,ContentSize.Height));
            var bw=Math.Max(16,(int)Math.Round(ContentSize.Width*backgroundScale));var bh=Math.Max(16,(int)Math.Round(ContentSize.Height*backgroundScale));
            BackgroundReference=Pixels.NewColor((bw+15)/16*16,(bh+15)/16*16);BackgroundReference.Erase(SKColors.White);
            using(var draw=new SKCanvas(BackgroundReference))
            {
                draw.DrawImage(Pixels.ImageOf(scene.Background),SKRect.Create(bw,bh),new SKSamplingOptions(SKCubicResampler.Mitchell));
                if(BackgroundReference.Width>bw)draw.DrawImage(Pixels.ImageOf(scene.Background),new SKRect(scene.Background.Width-1,0,scene.Background.Width,scene.Background.Height),new SKRect(bw,0,BackgroundReference.Width,bh),new SKSamplingOptions(SKFilterMode.Nearest));
                if(BackgroundReference.Height>bh)draw.DrawImage(Pixels.ImageOf(scene.Background),new SKRect(0,scene.Background.Height-1,scene.Background.Width,scene.Background.Height),new SKRect(0,bh,BackgroundReference.Width,BackgroundReference.Height),new SKSamplingOptions(SKFilterMode.Nearest));
            }
            if(!scene.IsCutout)
            {
                using var subject=Pixels.NewMask(BackgroundReference.Width,BackgroundReference.Height);
                using(var draw=new SKCanvas(subject)) draw.DrawImage(Pixels.ImageOf(scene.ObjectAlpha),SKRect.Create(bw,bh),new SKSamplingOptions(SKFilterMode.Linear));
                for(var y=0;y<bh;y++)for(var x=0;x<bw;x++)
                    if(subject.GetPixel(x,y).Alpha>0)BackgroundReference.SetPixel(x,y,new(128,128,128));
            }
            Pixels.Invalidate(BackgroundReference);
        }
        foreach(var reference in request.ReferenceImages)
        {
            ReferenceImages.Add(CopyReference(reference,request.ReferenceMegapixels));
        }
        }
        catch {Source?.Dispose();Mask?.Dispose();BackgroundReference?.Dispose();foreach(var reference in ReferenceImages) reference.Dispose();throw;}
    }
    internal void Bind(JsonObject graph,string source,string mask,AiTaskRequest request,int variant=0,bool cleanup=false)
    {
        graph["source"]!["inputs"]!["image"]=source;
        graph["maskImage"]!["inputs"]!["image"]=mask;
        var harmonize=request.Task is AiTaskKind.MatchToScene or AiTaskKind.Harmonize;
        graph["prompt"]!["inputs"]!["text"]=cleanup ? CleanupPrompt(request) :
            (harmonize && !scene.SuspendedSubject ? HarmonizePrompt : "") + Prompt(request,scene.LightHint)+LayoutInstruction(scene)+
            (request.Task!=AiTaskKind.ChangeBackground ? "\nLighting guidance: "+scene.LightHint.Instruction : "");
        // Cleanup is an image edit: its reference identifies the actual object to remove.
        // At denoise 1 the target mask is fully resampled; the surrounding encoded latent stays context.
        var sampler=graph["sampler"]!["inputs"]!;
        sampler["denoise"]=cleanup || harmonize ? 1 : request.Scene.Normalized().Denoise;
        if(harmonize && !cleanup)
        {
            // Scene editing shares one graph; apply this preset only to Harmonize/Match to Scene.
            sampler["steps"]=4;
            sampler["cfg"]=1;
            sampler["sampler_name"]="euler";
            sampler["scheduler"]="simple";
        }
        if(BackgroundReference!=null)
        {
            var explicitLight=request.Scene.LightAngle!=null||request.Task==AiTaskKind.Relight&&request.Scene.Preset!=RelightPreset.MatchScene;
            graph["prompt"]!["inputs"]!["text"]=graph["prompt"]!["inputs"]!["text"]!.GetValue<string>()+
                (scene.SuspendedSubject
                    ? "\nReference image 1 shows the foreground placement. Reference image 2 shows the complete background and the perspective of its receiving surfaces."
                    : explicitLight?"\nReference image 1 shows the foreground placement. Reference image 2 shows the full background and support-surface perspective. Apply the requested lighting direction and preset to the foreground and its shadow.":scene.LightHint.SoftContactOnly
                    ? "\nReference image 1 shows the object placement. Reference image 2 shows the softly illuminated background alone. Match its broad ambient lighting and even support surface, with gentle localized contact shading."
                    :"\nReference image 1 shows the object placement. Reference image 2 shows the complete background alone: use its existing illumination and cast shadows as the lighting reference. Adapt the subject to that background's light, rather than imposing the subject's original studio lighting on the scene.");
        }
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
            if((content.Width,content.Height)==(scene.Before.Width,scene.Before.Height)) return Pixels.Clone(content);
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
        if(files.TryGetValue("sceneBackground",out var background))
        {
            graph["scene_background"]=new JsonObject {["class_type"]="LoadImage",["inputs"]=new JsonObject {["image"]=background}};
            graph["scene_background_encode"]=new JsonObject {["class_type"]="VAEEncode",["inputs"]=new JsonObject {["pixels"]=new JsonArray("scene_background",0),["vae"]=new JsonArray("vae",0)}};
            foreach(var pair in new[] {(Name:"scene_background_positive",Previous:positive),(Name:"scene_background_negative",Previous:negative)})
                graph[pair.Name]=new JsonObject {["class_type"]="ReferenceLatent",["inputs"]=new JsonObject {["conditioning"]=new JsonArray(pair.Previous,0),["latent"]=new JsonArray("scene_background_encode",0)}};
            positive="scene_background_positive";negative="scene_background_negative";
        }
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
    internal static string Prompt(AiTaskRequest request,SceneLightHint? light=null)
    {
        var settings=request.Scene.Normalized();
        var suspended=settings.SuspendedSubject && request.Task is AiTaskKind.Harmonize or AiTaskKind.MatchToScene;
        var operation=request.Task==AiTaskKind.ChangeBackground
            ? "Replace the background around the existing foreground subject with the requested environment. Use the subject's actual scale, material, position and support points to compose a believable scene. Keep the subject in exactly the same position and shape. Build the support surface directly against its base, with a soft grounded contact shadow."
            : request.Task==AiTaskKind.Relight
                ? "Change the lighting of "+(settings.Scope==RelightScope.WholeImage ? "the image" : "the foreground subject")+". "+settings.LightingInstruction
                : suspended
                    ? "Match the suspended subject's illumination to the visible scene. Relight its visible faces and underside using the surroundings. Preserve its position, shape, clearance and existing attachment."
                    : "Match the existing foreground subject's illumination to the visible background: light direction, exposure, color temperature and reflected light. Keep its exact position, dimensions and support points. Preserve the surrounding scene. Lighting changes belong on its surfaces.";
        var shadow=request.Task==AiTaskKind.ChangeBackground ? "" : settings.AddShadows && settings.MatchMode==SceneMatchMode.LightAndShadows
            ? suspended
                ? " " + SuspendedCastInstruction
                : light?.SoftContactOnly==true
                ? " Generate a short faint diffuse contact shadow immediately beneath the base: a small soft ambient-occlusion patch spreading less than one base-width around the support. Preserve the floor's even illumination beyond this localized patch. Match the gentle ambient shadows of the existing objects."
                : " Generate continuous contact shading and a cast shadow appropriate to "+(settings.LightAngle!=null||request.Task==AiTaskKind.Relight&&settings.Preset!=RelightPreset.MatchScene?"the requested lighting":"the background's existing illumination")+" and support-surface perspective. Match the existing shadows' density, length and softness. Start shading at the subject's support points and respect material translucency."
            : " Do not add a new shadow outside the subject.";
        return operation+shadow+" Preserve identity, material texture, labels and colors. Render ordinary photographic surface shading and a continuous natural background. Keep the object's silhouette and placement in their original image coordinates. User guidance: "+request.Prompt+
            (string.IsNullOrWhiteSpace(request.AdditionalPrompt) ? "" : "\nWorkflow guidance: "+request.AdditionalPrompt);
    }
    internal const string SuspendedCastInstruction = "Cast a shadow only where light from the scene is blocked on a visible receiving surface such as a wall, cabinet or counter. Follow that surface's perspective and the direction, softness and falloff of the scene lighting. Preserve the gap below the subject. Contact shading belongs only at actual attachments. Keep the rest of the background unchanged.";
    internal const string MaterialColorInstruction = "Keep the original material colors across all parts of the subject, including food and labels. Adjust the illumination, retaining natural highlights and restrained reflections from the environment. Avoid local recoloring or replacing details.";
    internal static string CleanupPrompt(AiTaskRequest request)=>"Remove the main foreground object. Keep the surrounding objects, background, perspective and lighting.";
    internal static string LayoutInstruction(SceneEditInput scene)
    {
        var b=scene.SubjectBounds;var w=scene.Before.Width;var h=scene.Before.Height;
        if (scene.SuspendedSubject)
            return FormattableString.Invariant($"\nThe suspended foreground occupies x={100d*b.Left/w:0.#}% to {100d*b.Right/w:0.#}%, y={100d*b.Top/h:0.#}% to {100d*b.Bottom/h:0.#}% of the complete image. Keep that placement, camera perspective and the open space underneath.");
        return FormattableString.Invariant($"\nThe foreground occupies x={100d*b.Left/w:0.#}% to {100d*b.Right/w:0.#}%, y={100d*b.Top/h:0.#}% to {100d*b.Bottom/h:0.#}% of the complete image. Its support is at y={100d*scene.SupportBounds.Bottom/h:0.#}%. Keep that placement and construct the support surface through that support point; retain the camera perspective.");
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
    public void Dispose() {Source.Dispose();Mask.Dispose();BackgroundReference?.Dispose();foreach(var reference in ReferenceImages) reference.Dispose();}
}
