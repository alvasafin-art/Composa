using System.Text.Json.Nodes;
using Composa.AI;
using Composa.App.AI;

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
}
