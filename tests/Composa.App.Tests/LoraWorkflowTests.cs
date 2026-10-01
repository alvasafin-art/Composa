using System.Text.Json.Nodes;
using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using SkiaSharp;

namespace Composa.App.Tests;

public class LoraWorkflowTests
{
    private static EngineProfile Engine() => new EngineCatalog(Path.Combine(AppContext.BaseDirectory, "ai", "engines")).Find("flux2-klein-intel-xpu")!;
    private static ComfyServerCapabilities Server() => new()
    {
        NodeTypes = ["LoraLoaderModelOnly"], ModelChoices = new() { ["LoraLoaderModelOnly.lora_name"] = ["shared/a.safetensors", "b.safetensors", "c.safetensors"] }
    };
    private static JsonObject Graph() => JsonNode.Parse("""{"model":{"class_type":"UNETLoader","inputs":{}},"sampling":{"class_type":"ModelSamplingAuraFlow","inputs":{"model":["model",0]}},"sampler":{"class_type":"KSampler","inputs":{"model":["sampling",0]}}}""")!.AsObject();

    [Fact]
    public void Three_loras_form_one_model_only_chain_before_sampling_and_do_not_duplicate_consumers()
    {
        var graph = Graph(); WorkflowExecution.Loras(graph, Engine(), [new("a.safetensors", 0.7), new("b.safetensors", -0.5), new("c.safetensors", 1.2)], Server());
        Assert.Equal("composa_lora_2", graph["sampling"]!["inputs"]!["model"]![0]!.GetValue<string>());
        Assert.Equal("sampling", graph["sampler"]!["inputs"]!["model"]![0]!.GetValue<string>());
        var nodes = graph.Where(pair => pair.Value?["class_type"]?.GetValue<string>() == "LoraLoaderModelOnly").ToArray(); Assert.Equal(3, nodes.Length);
        Assert.Equal("model", nodes[0].Value!["inputs"]!["model"]![0]!.GetValue<string>());
        Assert.Equal("composa_lora", nodes[1].Value!["inputs"]!["model"]![0]!.GetValue<string>());
        Assert.Equal("composa_lora_1", nodes[2].Value!["inputs"]!["model"]![0]!.GetValue<string>());
        Assert.Equal("shared/a.safetensors", nodes[0].Value!["inputs"]!["lora_name"]!.GetValue<string>());
        Assert.Equal(-0.5, nodes[1].Value!["inputs"]!["strength_model"]!.GetValue<double>());
        Assert.DoesNotContain(nodes, pair => pair.Value!["inputs"]!.AsObject().ContainsKey("clip"));
    }

    [Fact]
    public void Disabled_loras_are_noop_and_missing_incompatible_or_excessive_loras_are_rejected()
    {
        var graph = Graph(); var before = graph.ToJsonString();
        WorkflowExecution.Loras(graph, Engine(), [new("missing", 9, false)], Server()); Assert.Equal(before, graph.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => WorkflowExecution.Loras(Graph(), Engine(), [new("missing")], Server()));
        Assert.Throws<ArgumentException>(() => WorkflowExecution.Loras(Graph(), Engine(), [new("b.safetensors", double.NaN)], Server()));
        Assert.Throws<ArgumentException>(() => WorkflowExecution.Loras(Graph(), Engine(), Enumerable.Repeat(new AiLora("b.safetensors"), 4).ToArray(), Server()));
        Assert.Throws<InvalidOperationException>(() => WorkflowExecution.Loras(Graph(), Engine() with { Lora = new() }, [new("b.safetensors")], Server()));
    }

    [Theory]
    [InlineData(AiTaskKind.GenerativeFill, false)]
    [InlineData(AiTaskKind.GenerativeFill, true)]
    [InlineData(AiTaskKind.RemoveObject, false)]
    [InlineData(AiTaskKind.RemoveObject, true)]
    public void Masked_edit_keeps_all_loras_when_removing_the_old_sampling_override(AiTaskKind task, bool pixaroma)
    {
        var catalog = new EngineCatalog(Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
        var engine = catalog.Find("flux2-klein-intel-xpu")!; var binding = engine.Binding(task)!;
        var session = EditorSession.NewCanvas(79, 61, SKColors.White); session.SelectRect(new SKRect(28, 22, 49, 41));
        var request = new AiTaskRequest { Task = task, Settings = new AiGenerationSettings { Width = 128, Height = 128 } };
        using var inputs = AiTaskInputPreparer.Prepare(session, request);
        var graph = JsonNode.Parse(File.ReadAllText(Path.Combine(catalog.DirectoryOf(engine), engine.Workflow(binding.Workflow).File)))!.AsObject();
        graph = WorkflowBinder.Bind(graph, binding, inputs.Values(inputs.Images().ToDictionary(pair => pair.Key, _ => "source.png")));
        var server = Server();
        if (pixaroma) { server.NodeTypes.Add("PixaromaInpaintCrop"); server.NodeTypes.Add("PixaromaInpaintStitch"); }
        // Use the same preparation order as AiTaskService.
        WorkflowExecution.Loras(graph, engine, [new("a.safetensors"), new("b.safetensors"), new("c.safetensors")], server);
        WorkflowExecution.MaskedEdit(graph, inputs, request, server);
        Assert.False(graph.ContainsKey("sampling"));
        Assert.Equal("composa_lora_2", graph["sampler"]!["inputs"]!["model"]![0]!.GetValue<string>());
        Assert.Equal(3, graph.Count(pair => pair.Value?["class_type"]?.GetValue<string>() == "LoraLoaderModelOnly"));
    }
}
