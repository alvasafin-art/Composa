using System.Text.Json.Nodes;
using Composa.AI;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.AI;

public sealed partial class AiTaskService
{
    private async Task RunSceneAsync(IEditorCommandService editor,AiTaskRequest request,CancellationToken token)
    {
        var engine=EngineFor(request.Task,request.EngineId);
        if(engine==null && request.Task==AiTaskKind.MatchToScene) engine=EngineFor(AiTaskKind.Harmonize,request.EngineId);
        if(engine==null) throw new InvalidOperationException("Choose a scene-editing workflow in AI Settings.");
        if(!engine.PaidApi && engine.Id!="flux2-klein-intel-xpu") throw new InvalidOperationException("Scene editing currently supports FLUX.2 Klein, GPT and Nano Banana.");
        if(request.ReferenceImages.Count==0 && request.ReferenceImage is { } legacyReference) request=request with {ReferenceImages=[legacyReference]};
        var maximumReferences=NanoBanana.IsModel(engine.ApiModel) ? 13 : 6;
        if(request.ReferenceImages.Count>maximumReferences) throw new InvalidOperationException($"This scene workflow accepts at most {maximumReferences} references in addition to the source.");
        if(request.Settings.Variants is <1 or >3) throw new ArgumentException("Choose one, two or three variants.");
        request=request with {Scene=request.Scene.Normalized(),AdditionalPrompt=AdditionalPromptForPack?.Invoke(engine.Id)??AdditionalPrompt()};
        // The generic edit default locks exposure and white balance, which would contradict relighting.
        if(request.AdditionalPrompt==AiPromptDefaults.PreserveAppearance) request=request with {AdditionalPrompt=""};
        bool LiveInteraction()=>editor.Session.IsInteracting && !editor.Session.IsInTransaction;
        if(LiveInteraction()) throw new InvalidOperationException("Finish the current interaction before scene editing.");
        var state=editor.Session.History.CurrentId; var source=editor.Session.Document.ActiveLayerId;
        var snapshot=editor.Session.Document.Clone();
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token);
        running?.Cancel(); running=linked;
        var results=new List<SceneEditResult>(); var applied=false;
        Operation=new() {Status=AiOperationStatus.Queued,Stage="Preparing subject and shadow area"}; StateChanged?.Invoke();
        try
        {
            using var scene=await Task.Run(()=>SceneEditInput.Prepare(snapshot,request,linked.Token),linked.Token);
            using var client=Client();
            if(ConnectedServerUrl!=client.Address.ToString() || ServerCapabilities==null || ConnectionState!=ComfyConnectionState.Connected)
                await TestConnectionAsync(linked.Token,client.Address.ToString());
            if(ConnectedServerUrl!=client.Address.ToString() || ServerCapabilities is not { } capabilities)
                throw new InvalidOperationException("The ComfyUI connection changed. Retry on the selected server.");
            using var native=engine.PaidApi ? null : new SceneEditingWorkflow(scene,request);
            using var apiInputs=engine.PaidApi ? SceneApiInputs(scene,request) : null;
            var editRequest=request with {Task=AiTaskKind.ImageEdit,Prompt=SceneEditingWorkflow.Prompt(request)};
            using var partner=apiInputs==null ? null : new PartnerImageInputs(apiInputs,editRequest,engine.ApiModel);
            var workflow=engine.PaidApi ? engine.Workflow(engine.Binding(AiTaskKind.ImageEdit)!.Workflow) : engine.Workflow("sceneEdit");
            var baseGraph=Engines.ReadWorkflow(engine,workflow);
            if(engine.PaidApi)
            {
                if(string.IsNullOrWhiteSpace(Credential) && RequestApiKey!=null)
                {
                    SessionApiKey=(await RequestApiKey(linked.Token))?.Trim();
                    if(string.IsNullOrWhiteSpace(SessionApiKey)) throw new OperationCanceledException("API key entry cancelled.");
                }
                if(string.IsNullOrWhiteSpace(Credential)) throw new InvalidOperationException("Add a Comfy.org API key in AI Settings.");
                if(engine.ApiModel==null || !PartnerPricing.SupportsModel(capabilities,engine.ApiModel)) throw new InvalidOperationException("The server does not offer the selected image model. Update ComfyUI.");
                var nano=NanoBanana.IsModel(engine.ApiModel);
                if(!nano && !PartnerPricing.Choices(capabilities,engine.ApiModel,"size").Contains("Custom"))
                    throw new InvalidOperationException("Update ComfyUI: scene editing needs Custom dimensions to preserve proportions.");
                var quality=request.Settings.Values.GetValueOrDefault("apiQuality")?.ToString()??"low";
                if(nano) quality=NanoBanana.Resolution(quality);
                if(!PartnerPricing.Choices(capabilities,engine.ApiModel,nano ? "resolution" : "quality").Contains(quality))
                    throw new InvalidOperationException("The server does not support this image quality or resolution.");
            }
            JsonObject Bind(IReadOnlyDictionary<string,string> files,int variant,SceneEditingWorkflow? stageNative,PartnerImageInputs? stagePartner,bool cleanup)
            {
                var graph=(JsonObject)baseGraph.DeepClone();
                WorkflowModels.ApplyChoices(graph,engine.Id,ModelSelections(client.Address.ToString()));
                if(stageNative!=null)
                {
                    stageNative.Bind(graph,files["sourceImage"],files["sceneMask"],request,variant,cleanup);
                    SceneEditingWorkflow.References(graph,files);
                    WorkflowExecution.Loras(graph,engine,request.Settings.Loras,capabilities);
                    WorkflowMemory.Apply(graph,request.Settings.Values.GetValueOrDefault("fluxMemory")?.ToString()??"auto",ServerInfo,capabilities);
                }
                else
                {
                    var seed=request.Settings.Seed<0 ? Random.Shared.NextInt64(long.MaxValue) : (request.Settings.Seed+variant)&long.MaxValue;
                    graph=stagePartner!.Bind(graph,engine,files,seed);
                }
                WorkflowModels.ResolvePaths(graph,capabilities);
                var compatibility=EngineCompatibility.CheckWorkflow(graph,capabilities);
                if(!compatibility.IsCompatible) throw new InvalidOperationException(CompatibilityMessage(engine,compatibility));
                return graph;
            }
            var images=native!=null ? new Dictionary<string,SKBitmap> {{"sourceImage",native.Source},{"sceneMask",native.Mask}} : partner!.Images;
            if(native?.BackgroundReference is { } backdrop) images["sceneBackground"]=backdrop;
            if(native!=null) for(var reference=0;reference<native.ReferenceImages.Count;reference++) images["referenceImage"+(reference+1)]=native.ReferenceImages[reference];
            if(editor.Session.History.CurrentId!=state || editor.Session.Document.ActiveLayerId!=source || LiveInteraction())
                throw new InvalidOperationException("The document changed while preparing the scene. No generation was submitted.");
            _=Bind(images.ToDictionary(item=>item.Key,_=>"scene-preflight.png"),0,native,partner,false);
            var uploaded=new Dictionary<string,string>();
            foreach(var (key,image) in images) uploaded[key]=await client.UploadPngAsync(key,image,linked.Token);
            double? credits=null;
            void EnsureUnchanged()
            {
                if(editor.Session.History.CurrentId!=state || editor.Session.Document.ActiveLayerId!=source || LiveInteraction())
                    throw new InvalidOperationException("The document changed during scene editing. No further request was submitted and no result was applied.");
            }
            async Task<SceneGeneration> Generate(SceneEditingWorkflow? stageNative,PartnerImageInputs? stagePartner,IReadOnlyDictionary<string,string> files,int variant,bool cleanup)
            {
                EnsureUnchanged();
                var progress=new Progress<AiOperationState>(update=>
                {
                    if(!ReferenceEquals(running,linked)||linked.IsCancellationRequested) return;
                    var phase=request.Task==AiTaskKind.ChangeBackground?(cleanup?"Clean background plate · pass 2/2":"Object-aware scene layout · pass 1/2"):"Lighting and contact";
                    Operation=update with {Status=AiOperationStatus.Running,Stage=$"Variant {variant+1}/{request.Settings.Variants} · {phase} · {update.Stage}"}; StateChanged?.Invoke();
                });
                var execution=await client.ExecuteAsync(Bind(files,variant,stageNative,stagePartner,cleanup),progress,linked.Token);
                using(execution.History)
                {
                    if(execution.CreditsUsed is { } cost) credits=(credits??0)+cost;
                    var output=execution.Images.Where(image=>workflow.OutputNodes.Contains(image.NodeId!) && image.NodeId!="baseline").ToArray();
                    if(output.Length!=1) throw new InvalidDataException("Scene editing expects one result per pass. No partial edit was applied.");
                    SKBitmap? image=null,baseline=null;
                    try
                    {
                        var decoded=await client.DownloadAsync(output[0],linked.Token);
                        image=await Task.Run(()=>stageNative!=null?stageNative.Finish(decoded):stagePartner!.FinishUnmasked(decoded));
                        if(stageNative!=null)
                        {
                            var reference=execution.Images.Where(candidate=>candidate.NodeId=="baseline").ToArray();
                            if(reference.Length!=1) throw new InvalidDataException("The scene workflow did not return its VAE reconstruction baseline.");
                            var reconstructed=await client.DownloadAsync(reference[0],linked.Token);
                            baseline=await Task.Run(()=>stageNative.Finish(reconstructed));
                        }
                        EnsureUnchanged();return new(image,baseline);
                    }
                    catch {image?.Dispose();baseline?.Dispose();throw;}
                }
            }
            for(var index=0;index<request.Settings.Variants;index++)
            {
                using var generated=await Generate(native,partner,uploaded,index,false);
                SceneEditResult result;
                if(request.Task==AiTaskKind.ChangeBackground)
                {
                    // First the model arranges the environment around the actual PNG. Only then remove its cache.
                    var margin=native==null?16:(int)Math.Ceiling(16*scene.Before.Width/(double)native.ContentSize.Width);
                    using var plate=await Task.Run(()=>new SceneBackgroundPlate(scene,generated.Image,margin,linked.Token),linked.Token);
                    var cleanupRequest=request with {ReferenceImages=[],ReferenceImage=null,Prompt=SceneEditingWorkflow.CleanupPrompt(request),AdditionalPrompt=""};
                    using var cleanupNative=engine.PaidApi?null:new SceneEditingWorkflow(scene,cleanupRequest,plate.Image,plate.Mask);
                    using var cleanupInputs=engine.PaidApi?new AiTaskInputs {SourceImage=Pixels.Clone(plate.Image),ContextImage=Pixels.Clone(plate.Image),CanvasWidth=scene.Before.Width,CanvasHeight=scene.Before.Height,TargetBounds=scene.Before.Info.Rect,Prompt=cleanupRequest.Prompt}:null;
                    using var cleanupPartner=cleanupInputs==null?null:new PartnerImageInputs(cleanupInputs,cleanupRequest with {Task=AiTaskKind.ImageEdit},engine.ApiModel);
                    var cleanupImages=cleanupNative==null?cleanupPartner!.Images:new Dictionary<string,SKBitmap> {{"sourceImage",cleanupNative.Source},{"sceneMask",cleanupNative.Mask}};
                    _=Bind(cleanupImages.ToDictionary(item=>item.Key,_=>"plate-preflight.png"),index,cleanupNative,cleanupPartner,true);
                    var cleanupFiles=new Dictionary<string,string>(); foreach(var (key,image) in cleanupImages) cleanupFiles[key]=await client.UploadPngAsync(key,image,linked.Token);
                    using var clean=await Generate(cleanupNative,cleanupPartner,cleanupFiles,index,true);
                    using var background=await Task.Run(()=>plate.Finish(clean.Image,generated.Image,clean.Baseline));
                    result=await Task.Run(()=>SceneEditResult.Extract(scene,background,request,linked.Token),linked.Token);
                }
                else
                {
                    result=await Task.Run(()=>SceneEditResult.Extract(scene,generated.Image,request,linked.Token,generated.Baseline,native==null?1:scene.Before.Width/(double)native.ContentSize.Width),linked.Token);
                }
                results.Add(result);
            }
            linked.Token.ThrowIfCancellationRequested();
            if(editor.Session.History.CurrentId!=state || editor.Session.Document.ActiveLayerId!=source || LiveInteraction())
                throw new InvalidOperationException("The document changed during generation. Results were not applied; retry after finishing the edit.");
            editor.Session.InsertSceneEdit(request.Task,scene.SourceLayerId,results,scene.IsCutout,scene.WholeImage,scene.PreviousResultIds,scene.SourceFrame);
            applied=true;
            var noShadow=request.Task is AiTaskKind.MatchToScene or AiTaskKind.Harmonize or AiTaskKind.Relight && !scene.WholeImage && request.Scene.AddShadows && request.Scene.MatchMode==SceneMatchMode.LightAndShadows && results.All(result=>result.ShadowPixels==0);
            Operation=new() {Status=AiOperationStatus.Completed,Stage=noShadow ? "Completed · no reliable cast shadow detected" : "Completed",CreditsUsed=credits}; StateChanged?.Invoke();
        }
        catch(OperationCanceledException) {Operation=Operation! with {Status=AiOperationStatus.Cancelled,Stage="Cancelled"};StateChanged?.Invoke();throw;}
        catch(Exception error) {Operation=Operation! with {Status=AiOperationStatus.Failed,Stage="Failed",Error=error.Message};StateChanged?.Invoke();throw;}
        finally
        {
            if(!applied) foreach(var output in results.SelectMany(result=>result.Layers)) {output.Pixels.Dispose();output.Mask?.Dispose();}
            if(ReferenceEquals(running,linked)) running=null;
        }
    }
    private sealed record SceneGeneration(SKBitmap Image,SKBitmap? Baseline):IDisposable
    {public void Dispose() {Image.Dispose();Baseline?.Dispose();}}
    private static AiTaskInputs SceneApiInputs(SceneEditInput scene,AiTaskRequest request) => new()
    {
        SourceImage=Pixels.Clone(scene.InitialImage),ContextImage=Pixels.Clone(scene.Before),
        CanvasWidth=scene.Before.Width,CanvasHeight=scene.Before.Height,TargetBounds=scene.Before.Info.Rect,
        Prompt=SceneEditingWorkflow.Prompt(request)+SceneEditingWorkflow.LayoutInstruction(scene)+(request.Task!=AiTaskKind.ChangeBackground ? "\nLighting guidance: "+scene.LightHint.Instruction : ""),Seed=request.Settings.Seed,
        ReferenceImages=request.ReferenceImages.Select(image=>SceneEditingWorkflow.CopyReference(image,request.ReferenceMegapixels)).ToArray()
    };
}
