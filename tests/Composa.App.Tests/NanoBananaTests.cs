using System.Text.Json.Nodes;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.AI;
using Composa.App.AI;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class NanoBananaTests
{
    [Fact]
    public void Cpu_server_null_device_index_and_memory_do_not_block_partner_connection()
    {
        using var json=JsonDocument.Parse("""{"system":{"os":"windows"},"devices":[{"name":"cpu","type":"cpu","index":null,"vram_total":null,"vram_free":null}]}""");
        var info=ComfyClient.ParseInfo(json.RootElement); Assert.Single(info.Devices); Assert.Equal(0,Assert.Single(info.Memory).Total);
    }
    private static EngineCatalog Catalog()=>new(Path.Combine(AppContext.BaseDirectory,"ai","engines"));
    private static EngineProfile Pack()=>Catalog().Find("nano-banana-2")!;
    internal static ComfyServerCapabilities Capabilities()
    {
        var definition=JsonNode.Parse("""
        {"input":{"required":{"model":["COMFY_DYNAMICCOMBO_V3",{"options":[{"key":"Nano Banana 2 (Gemini 3.1 Flash Image)","inputs":{"required":{"resolution":["COMBO",{"options":["1K","2K","4K"]}],"aspect_ratio":["COMBO",{"options":["auto","1:1","2:3","3:2","3:4","4:3","4:5","5:4","9:16","16:9","21:9","1:4","4:1","8:1","1:8"]}],"thinking_level":["COMBO",{"options":["MINIMAL","HIGH"]}]}}}]}]}},"price_badge":{"expr":"($prices := {\"1k\":0.0835,\"2k\":0.1217,\"4k\":0.1848};)"}}
        """)!.AsObject();
        var nodes=new JsonObject { ["GeminiNanoBanana2V2"]=definition,["LoadImage"]=new JsonObject(),["SaveImage"]=new JsonObject() };
        using var json=JsonDocument.Parse(nodes.ToJsonString()); return ComfyClient.ParseCapabilities(json.RootElement);
    }
    private static AiTaskService Service(PartnerImageTests.Connection connection,bool key=true)=>new(()=>"http://localhost:8188",Catalog().Root,_=>connection)
        {SelectedEngine=Pack(),ApiKey=()=>key ? "test-only-comfy-key" : null};

    [Fact]
    public async Task Live_official_schema_is_checked_without_upload_or_paid_execution_when_requested()
    {
        var url=Environment.GetEnvironmentVariable("COMPOSA_PARTNER_SCHEMA_TEST_URL"); if(string.IsNullOrEmpty(url)) return;
        using var client=new ComfyClient(url);
        var (_,schema)=await client.TestConnectionAsync(TestContext.Current.CancellationToken);
        Assert.True(EngineCompatibility.Check(Pack(),schema).IsCompatible);
        Assert.True(PartnerPricing.SupportsModel(schema,NanoBanana.Model));
        Assert.Equal(["1K","2K","4K"],PartnerPricing.Choices(schema,NanoBanana.Model,"resolution"));
        Assert.NotNull(PartnerPricing.Estimate(schema,NanoBanana.Model,"2K","auto",3,2));
    }

    [Fact]
    public void Fourteen_generation_references_are_ordered_and_no_source_or_mask_is_added()
    {
        var s=EditorSession.NewCanvas(80,60); using var reference=Pixels.NewColor(20,20);
        var request=new AiTaskRequest {Task=AiTaskKind.GenerateImage,ReferenceImages=Enumerable.Repeat(reference,14).ToArray()};
        using var inputs=AiTaskInputPreparer.Prepare(s,request,14); using var api=new PartnerImageInputs(inputs,request,NanoBanana.Model);
        var graph=api.Bind(Catalog().ReadWorkflow(Pack(),Pack().Workflows[0]),Pack(),api.Images.ToDictionary(p=>p.Key,p=>p.Key+".png"),1);
        Assert.Equal(14,graph["gpt"]!["inputs"]!.AsObject().Count(p=>p.Key.StartsWith("model.images.image_")));
        for(var i=1;i<=14;i++) Assert.Equal($"referenceImage{i}.png",graph[$"composa_api_image_{i}"]!["inputs"]!["image"]!.GetValue<string>());
        Assert.DoesNotContain("apiSource",api.Images.Keys);
    }

    [Fact]
    public void Official_provider_model_has_no_local_weights_and_prices_come_from_the_server()
    {
        var pack=Pack(); Assert.Equal(NanoBanana.Model,pack.ApiModel); Assert.True(pack.PaidApi);
        Assert.Empty(pack.RequiredAssets); Assert.Empty(Catalog().ModelSlots(pack)); Assert.True(EngineCompatibility.Check(pack,Capabilities()).IsCompatible);
        Assert.True(PartnerPricing.SupportsModel(Capabilities(),NanoBanana.Model));
        Assert.Equal(["1K","2K","4K"],PartnerPricing.Choices(Capabilities(),NanoBanana.Model,"resolution"));
        Assert.Equal(.1217*3,PartnerPricing.Estimate(Capabilities(),NanoBanana.Model,"2K","auto",8,3)!.MinimumUsd,8);
        Assert.Null(PartnerPricing.Estimate(Capabilities() with {NodeDefinitions=[]},NanoBanana.Model,"2K","auto",0,1));
    }

    [Theory]
    [InlineData(79,61)] [InlineData(160,39)] [InlineData(19,130)]
    public void Padding_roundtrip_preserves_pixels_alpha_and_proportions(int width,int height)
    {
        using var source=Pixels.NewColor(width,height);
        for(var y=0;y<height;y++) for(var x=0;x<width;x++) source.SetPixel(x,y,new((byte)(x*255/width),(byte)(y*255/height),80,(byte)(x<width/2?128:255)));
        Pixels.Invalidate(source);
        var padded=NanoBanana.Pad(source,out var content);
        using var result=NanoBanana.Unpad(padded,content,new(padded.Width,padded.Height));
        Assert.Equal((width,height),(result.Width,result.Height)); Assert.Equal(source.Bytes,result.Bytes);
    }

    [Theory]
    [InlineData(AiTaskKind.GenerateImage)] [InlineData(AiTaskKind.GenerativeFill)] [InlineData(AiTaskKind.ImageEdit)]
    [InlineData(AiTaskKind.RemoveObject)] [InlineData(AiTaskKind.ChangeBackground)] [InlineData(AiTaskKind.Harmonize)] [InlineData(AiTaskKind.Relight)]
    public async Task Operations_bind_the_official_node_and_insert_one_undoable_result(AiTaskKind task)
    {
        var s=EditorSession.NewCanvas(79,61,SKColors.White);
        if(task is not (AiTaskKind.GenerateImage or AiTaskKind.ImageEdit)) s.SelectRect(new(25,20,45,40));
        var before=s.History.Count; var connection=new PartnerImageTests.Connection {Server=Capabilities()};
        await Service(connection).RunAsync(new EditorCommandService(s),new(){Task=task,Prompt="make it blue",Settings=new(){Values=new(){["apiQuality"]="2K"}}},TestContext.Current.CancellationToken);
        var graph=Assert.Single(connection.Graphs); var node=graph["gpt"]!["inputs"]!;
        Assert.Equal("GeminiNanoBanana2V2",graph["gpt"]!["class_type"]!.GetValue<string>());
        Assert.Equal(NanoBanana.Model,node["model"]!.GetValue<string>()); Assert.Equal("2K",node["model.resolution"]!.GetValue<string>());
        Assert.Null(node["model.custom_width"]); Assert.Null(node["model.mask"]); Assert.Null(node["n"]);
        Assert.Equal(before+1,s.History.Count); Assert.Equal((79,61),(s.Document.Width,s.Document.Height));
        if(task==AiTaskKind.GenerativeFill) Assert.Equal(SKColors.White,s.Flatten().GetPixel(0,0));
        s.Undo(); Assert.Single(s.Document.Layers);
    }

    [Theory]
    [InlineData(AiVariantMode.List)] [InlineData(AiVariantMode.Batch)]
    public async Task Expansion_variants_are_separate_requests_share_uploads_and_undo_together(AiVariantMode mode)
    {
        var s=EditorSession.NewCanvas(79,61,SKColors.White); var connection=new PartnerImageTests.Connection {Server=Capabilities()};
        await Service(connection).RunAsync(new EditorCommandService(s),new(){Task=AiTaskKind.GenerativeExpand,ExpansionBounds=new(-11,-7,91,69),Settings=new(){Variants=2,VariantMode=mode}},TestContext.Current.CancellationToken);
        Assert.Equal(2,connection.Graphs.Count); Assert.Single(connection.Uploads); Assert.Equal((102,76),(s.Document.Width,s.Document.Height));
        Assert.Equal(SKColors.White,s.Flatten().GetPixel(30,30)); Assert.Equal(2,s.AiVariantGroup!.Children.Count);
        s.Undo(); Assert.Equal((79,61),(s.Document.Width,s.Document.Height)); Assert.Single(s.Document.Layers);
    }

    [Fact]
    public async Task Invalid_key_model_resolution_and_reference_count_fail_before_upload()
    {
        using var reference=Pixels.NewColor(20,20);
        foreach(var failure in new[]{"key","model","resolution","references"})
        {
            var s=EditorSession.NewCanvas(80,60);
            var c=new PartnerImageTests.Connection {Server=failure=="model" ? Capabilities() with {NodeDefinitions=[]} : Capabilities()};
            await Assert.ThrowsAsync<InvalidOperationException>(()=>Service(c,failure!="key").RunAsync(new EditorCommandService(s),new(){Task=AiTaskKind.GenerateImage,
                ReferenceImages=failure=="references" ? Enumerable.Repeat(reference,15).ToArray() : [],Settings=new(){Values=new(){["apiQuality"]=failure=="resolution"?"8K":"1K"}}},TestContext.Current.CancellationToken));
            Assert.Empty(c.Uploads); Assert.Empty(c.Graphs); Assert.Equal(0,s.History.Count);
        }
    }

    [AvaloniaFact]
    public async Task Model_switching_restores_distinct_quality_and_resolution_profiles()
    {
        var window=new MainWindow(); window.Settings.CheckForUpdates=false; window.Show();
        try
        {
            window.Settings.AiTaskEngineIds[nameof(AiTaskKind.GenerateImage)]=Pack().Id;
            window.AiTasks.SelectedEngine=window.AiTasks.Engines.Find(Pack().Id);
            window.Settings.SetOperation(Pack().Id,AiTaskKind.GenerateImage,window.Settings.OperationFor(Pack().Id,AiTaskKind.GenerateImage,true) with {ApiQuality="2K"});
            var pending=AiDialogs.Prompt(window,AiTaskKind.GenerateImage,window.Settings,1600,400,service:window.AiTasks);
            Dispatcher.UIThread.RunJobs(); var dialog=Assert.Single(window.OwnedWindows);
            var combos=dialog.GetVisualDescendants().OfType<ComboBox>().ToArray();
            var quality=Assert.Single(combos,c=>c.Items.Cast<string>().Contains("2K")); Assert.Equal("2K",quality.SelectedItem);
            var model=Assert.Single(combos,c=>c.Items.Cast<string>().Contains(Pack().DisplayName));
            model.SelectedItem=Catalog().Find("chatgpt-image-2.5")!.DisplayName; Dispatcher.UIThread.RunJobs();
            quality.SelectedItem="high";
            model.SelectedItem=Pack().DisplayName; Dispatcher.UIThread.RunJobs(); Assert.Equal("2K",quality.SelectedItem);
            quality.SelectedItem="4K"; Dispatcher.UIThread.RunJobs();
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text?.Contains("Nano Banana: 4K · 4:1")==true);
            Assert.True(Screenshots.Save(dialog,"nano-banana-2-generation-resolution"));
            model.SelectedItem=Catalog().Find("chatgpt-image-2.5")!.DisplayName; Dispatcher.UIThread.RunJobs(); Assert.Equal("high",quality.SelectedItem);
            dialog.Close(false); Assert.Null(await pending);
        }
        finally {window.Close();}
    }
}
