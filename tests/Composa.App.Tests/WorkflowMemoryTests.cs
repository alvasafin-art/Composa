using System.Text.Json.Nodes;
using Composa.AI;
using Composa.App.AI;

namespace Composa.App.Tests;

public class WorkflowMemoryTests
{
    [Theory]
    [InlineData("auto", "xpu:0 Intel Arc", true)]
    [InlineData("auto", "cuda:0", false)]
    [InlineData("standard", "xpu:0 Intel Arc", false)]
    [InlineData("reduced", "cuda:0", true)]
    public void Memory_policy_preserves_models_and_links_and_only_tiles_requested_execution(string mode, string device, bool reduced)
    {
        var graph=JsonNode.Parse("""
            {"clip":{"class_type":"CLIPLoader","inputs":{"clip_name":"chosen-text.safetensors","type":"flux2","device":"default"}},
             "sourceEncode":{"class_type":"VAEEncode","inputs":{"pixels":["image",0],"vae":["vae",0]}},
             "decode":{"class_type":"VAEDecode","inputs":{"samples":["sampler",0],"vae":["vae",0]}},
             "vae":{"class_type":"VAELoader","inputs":{"vae_name":"chosen-vae.safetensors"}}}
            """)!.AsObject();
        WorkflowMemory.Apply(graph,mode,new(null,null,null,[device]),new() { NodeTypes=["VAEEncodeTiled","VAEDecodeTiled"] });
        Assert.Equal(reduced?"cpu":"default",graph["clip"]!["inputs"]!["device"]!.GetValue<string>());
        Assert.Equal(reduced?"VAEEncodeTiled":"VAEEncode",graph["sourceEncode"]!["class_type"]!.GetValue<string>());
        Assert.Equal(reduced?"VAEDecodeTiled":"VAEDecode",graph["decode"]!["class_type"]!.GetValue<string>());
        Assert.Equal("chosen-text.safetensors",graph["clip"]!["inputs"]!["clip_name"]!.GetValue<string>());
        Assert.Equal("chosen-vae.safetensors",graph["vae"]!["inputs"]!["vae_name"]!.GetValue<string>());
        Assert.Equal("[\"image\",0]",graph["sourceEncode"]!["inputs"]!["pixels"]!.ToJsonString());
        Assert.Equal("[\"sampler\",0]",graph["decode"]!["inputs"]!["samples"]!.ToJsonString());
    }

    [Fact]
    public void Reduced_mode_reports_missing_stock_nodes_before_generation()
    {
        var graph=JsonNode.Parse("""{"decode":{"class_type":"VAEDecode","inputs":{"samples":["sampler",0],"vae":["vae",0]}}}""")!.AsObject();
        var error=Assert.Throws<InvalidOperationException>(()=>WorkflowMemory.Apply(graph,"reduced",null,new()));
        Assert.Contains("VAEDecodeTiled",error.Message); Assert.Contains("Standard",error.Message);
    }
}
