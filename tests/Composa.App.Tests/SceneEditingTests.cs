using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.AI;
using Composa.App.AI;
using Composa.App.Dialogs;
using Composa.App.Automation;
using Composa.Editing;
using Composa.Rendering;
using SkiaSharp;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Composa.App.Tests;

public class SceneEditingTests
{
    private static EditorSession Scene()
    {
        var s=EditorSession.NewCanvas(100,80,SKColors.Gray);
        var png=Pixels.NewColor(20,30); using(var c=new SKCanvas(png)) using(var p=new SKPaint {Color=SKColors.Coral}) c.DrawRect(2,2,16,26,p);
        Pixels.Invalidate(png); s.AddImageLayer("PNG",png,new(50,40),fit:false); return s;
    }
    private static readonly string Engines=Path.Combine(AppContext.BaseDirectory,"ai/engines");
    [Fact]
    public void Scene_workflow_initializes_from_encoded_pixels_and_keeps_padding_out_of_the_noise_mask()
    {
        var s=Scene(); var request=new AiTaskRequest {Task=AiTaskKind.MatchToScene,Settings=new() {Width=81,Height=65,Seed=17,Values=new() {["imageOriginalSize"]=false}}};
        using var input=SceneEditInput.Prepare(s.Document.Clone(),request);
        using var native=new SceneEditingWorkflow(input,request);
        var catalog=new EngineCatalog(Engines); var engine=catalog.Find("flux2-klein-intel-xpu")!;
        var graph=catalog.ReadWorkflow(engine,engine.Workflow("sceneEdit")); native.Bind(graph,"source.png","mask.png",request);
        Assert.DoesNotContain(graph,entry=>entry.Value?["class_type"]?.GetValue<string>() is "EmptyFlux2LatentImage" or "InpaintModelConditioning");
        Assert.Equal("VAEEncode",graph["sourceEncode"]!["class_type"]!.GetValue<string>());
        Assert.Equal("sourceEncode",graph["noiseMask"]!["inputs"]!["samples"]![0]!.GetValue<string>());
        Assert.Equal("noiseMask",graph["latent"]!["inputs"]!["samples"]![0]!.GetValue<string>());
        Assert.Equal(.8,graph["sampler"]!["inputs"]!["denoise"]!.GetValue<double>());
        Assert.Equal("sourceEncode",graph["baselineDecode"]!["inputs"]!["samples"]![0]!.GetValue<string>());
        if(native.PaddedSize.Width>native.ContentSize.Width) Assert.Equal(0,native.Mask.GetPixel(native.PaddedSize.Width-1,native.PaddedSize.Height/2).Alpha);
    }
    [Fact]
    public void Harmonize_sends_the_complete_background_separately_and_cleanup_encodes_an_erased_reference()
    {
        var s=Scene();var request=new AiTaskRequest {Task=AiTaskKind.MatchToScene,Settings=new() {Width=100,Height=80,Values=new() {["imageOriginalSize"]=false}}};
        using var input=SceneEditInput.Prepare(s.Document.Clone(),request);using var native=new SceneEditingWorkflow(input,request);
        Assert.NotNull(native.BackgroundReference);Assert.Equal(input.Background.GetPixel(50,40),native.BackgroundReference.GetPixel(50,40));
        var catalog=new EngineCatalog(Engines);var engine=catalog.Find("flux2-klein-intel-xpu")!;var graph=catalog.ReadWorkflow(engine,engine.Workflow("sceneEdit"));
        native.Bind(graph,"source.png","mask.png",request);SceneEditingWorkflow.References(graph,new Dictionary<string,string> {{"sceneBackground","background.png"}});
        Assert.Equal("scene_background_positive",graph["sampler"]!["inputs"]!["positive"]![0]!.GetValue<string>());
        Assert.Contains("Reference image 2",graph["prompt"]!["inputs"]!["text"]!.GetValue<string>());
        using var plate=new SceneBackgroundPlate(input,input.Before,20);
        var cleanup=request with {Task=AiTaskKind.ChangeBackground};using var cleanupNative=new SceneEditingWorkflow(input,cleanup,plate.Image,plate.Mask);
        var cleanupGraph=catalog.ReadWorkflow(engine,engine.Workflow("sceneEdit"));cleanupNative.Bind(cleanupGraph,"plate.png","mask.png",cleanup,cleanup:true);
        Assert.Null(cleanupNative.BackgroundReference);Assert.Equal("VAEEncodeForInpaint",cleanupGraph["sourceEncode"]!["class_type"]!.GetValue<string>());
        Assert.Equal("removal",cleanupGraph["sourceEncode"]!["inputs"]!["mask"]![0]!.GetValue<string>());
        Assert.NotNull(cleanupNative.RemovalMask);Assert.Equal(255,cleanupNative.Mask.GetPixel(0,0).Alpha);
        Assert.Equal(1,cleanupGraph["sampler"]!["inputs"]!["denoise"]!.GetValue<double>());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_or_cancellation_does_not_hide_the_source_or_add_partial_scene_layers(bool cancel)
    {
        var s=Scene(); var source=s.ActiveLayer!; var state=s.History.CurrentId; using var before=s.Flatten();
        var connection=new Connection {Fail=!cancel,Cancel=cancel};
        var service=Service(connection);
        var task=service.RunAsync(new EditorCommandService(s),new() {Task=AiTaskKind.MatchToScene},TestContext.Current.CancellationToken);
        if(cancel) await Assert.ThrowsAsync<OperationCanceledException>(()=>task);
        else await Assert.ThrowsAsync<InvalidOperationException>(()=>task);
        Assert.Equal(state,s.History.CurrentId); Assert.True(source.Visible); Assert.Equal(2,s.Document.Layers.Count); Assert.Equal(before.Bytes,s.Flatten().Bytes);
    }
    [Fact]
    public async Task Failure_of_the_second_background_pass_cannot_commit_the_first_pass()
    {
        var s=Scene();var source=s.ActiveLayer!;var state=s.History.CurrentId;using var before=s.Flatten();
        var connection=new Connection {Fail=true,FailAt=2};var service=Service(connection);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.RunAsync(new EditorCommandService(s),new() {Task=AiTaskKind.ChangeBackground},TestContext.Current.CancellationToken));
        Assert.Equal(2,connection.Executions);Assert.True(source.Visible);Assert.Equal(state,s.History.CurrentId);Assert.Equal(before.Bytes,s.Flatten().Bytes);
    }
    [Fact]
    public async Task Scene_variants_use_one_atomic_commit_and_reuse_uploads()
    {
        var s=Scene(); var state=s.History.CurrentId; using var before=s.Flatten();
        var connection=new Connection(); var service=Service(connection);
        await service.RunAsync(new EditorCommandService(s),new() {Task=AiTaskKind.Relight,Scene=new() {Preset=RelightPreset.SoftboxLeft},Settings=new() {Variants=3,Width=100,Height=80,Values=new() {["imageOriginalSize"]=false}}},TestContext.Current.CancellationToken);
        Assert.Equal(3,connection.Executions); Assert.Equal(3,connection.Uploads); Assert.True(s.ActiveLayer!.Tags.Contains("ai-variants"));
        Assert.Equal(3,s.ActiveLayer.Children.Count); Assert.Single(s.ActiveLayer.Children,layer=>layer.Visible);
        s.Undo(); Assert.Equal(state,s.History.CurrentId); Assert.Equal(before.Bytes,s.Flatten().Bytes);
    }
    [AvaloniaTheory]
    [InlineData("matchToScene({scene:{denoise:0.7}})")]
    [InlineData("relight('soft studio',{scene:{preset:'SoftboxLeft'}})")]
    public async Task Scripted_scene_edit_participates_in_the_enclosing_transaction(string command)
    {
        var s=Scene(); var state=s.History.CurrentId; using var before=s.Flatten();
        var service=Service(new Connection());
        await new JavaScriptRuntime().ExecuteAsync(s,"ai."+command+";",service,new Settings(),cancellationToken:TestContext.Current.CancellationToken);
        Assert.Contains(s.Document.AllLayers(),layer=>layer.Tags.Contains("scene-object"));
        s.Undo(); Assert.Equal(state,s.History.CurrentId); Assert.Equal(before.Bytes,s.Flatten().Bytes);
    }
    [AvaloniaTheory]
    [InlineData(AiTaskKind.MatchToScene)]
    [InlineData(AiTaskKind.Relight)]
    [InlineData(AiTaskKind.ChangeBackground)]
    public async Task Scene_dialogs_offer_practical_controls_and_cancel_without_saving(AiTaskKind task)
    {
        var w=new MainWindow {Width=1280,Height=1000}; w.Settings.CheckForUpdates=false; w.Show();
        try
        {
            w.AddSession(Scene()); var before=JsonSerializer.Serialize(w.Settings.AiOperations);
            var pending=AiDialogs.Prompt(w,task,w.Settings,100,80,service:w.AiTasks); Dispatcher.UIThread.RunJobs();
            var dialog=Assert.Single(w.OwnedWindows);
            var labels=dialog.GetVisualDescendants().OfType<TextBlock>().Select(label=>label.Text).ToArray();
            if(task==AiTaskKind.Relight) Assert.Contains("Lighting preset",labels);
            if(task==AiTaskKind.MatchToScene) Assert.Contains("Mode",labels);
            if(task==AiTaskKind.ChangeBackground) Assert.Contains("Preserve subject",labels);
            Assert.True(Screenshots.Save(dialog,"scene-dialog-"+task)); dialog.Close(false); Assert.Null(await pending);
            Assert.Equal(before,JsonSerializer.Serialize(w.Settings.AiOperations));
        }
        finally {w.Close();}
    }
    private static AiTaskService Service(Connection connection)
    {var service=new AiTaskService(()=>"http://localhost:8188",Engines,_=>connection);service.SelectedEngine=service.Engines.Find("flux2-klein-intel-xpu");return service;}
    private sealed class Connection : IComfyConnection
    {
        public ComfyServerAddress Address=>ComfyServerAddress.Parse("http://localhost:8188");
        public TimeSpan ConnectionTimeout {get;set;}
        public bool Fail,Cancel; public int FailAt,Uploads,Executions; private SKBitmap? source;
        public Task<(ComfyServerInfo,ComfyServerCapabilities)> TestConnectionAsync(CancellationToken token=default)
        {
            var catalog=new EngineCatalog(Engines); var pack=catalog.Find("flux2-klein-intel-xpu")!;
            var graph=catalog.ReadWorkflow(pack,pack.Workflow("sceneEdit"));
            return Task.FromResult((new ComfyServerInfo("0.37.0","test","test",["CPU"]),new ComfyServerCapabilities {NodeTypes=graph.Select(node=>node.Value!["class_type"]!.GetValue<string>()).Append("VAEEncodeForInpaint").ToHashSet()}));
        }
        public Task<string> UploadPngAsync(string semantic,SKBitmap image,CancellationToken token=default)
        {Uploads++;if(semantic=="sourceImage") {source?.Dispose();source=Pixels.Clone(image);}return Task.FromResult(semantic+".png");}
        public Task<ComfyExecutionResult> ExecuteAsync(JsonObject workflow,IProgress<AiOperationState>? progress=null,CancellationToken token=default)
        {
            Executions++; if(FailAt==0||Executions==FailAt) {if(Cancel) throw new OperationCanceledException(token);if(Fail) throw new InvalidOperationException("fixture failure");}
            Assert.Equal("sourceEncode",workflow["noiseMask"]!["inputs"]!["samples"]![0]!.GetValue<string>());
            return Task.FromResult(new ComfyExecutionResult("fixture",JsonDocument.Parse("{}"),[new("fixture.png","","output","save"),new("baseline.png","","output","baseline")]));
        }
        public Task<SKBitmap> DownloadAsync(ComfyImageReference image,CancellationToken token=default)=>Task.FromResult(Pixels.Clone(source!));
        public void Dispose() {source?.Dispose();source=null;}
    }
}
