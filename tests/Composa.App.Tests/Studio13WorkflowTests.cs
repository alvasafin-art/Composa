using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using SkiaSharp;

namespace Composa.App.Tests;

public class Studio13WorkflowTests
{
    [Theory]
    [InlineData("krea-2-turbo", "Krea 2 Turbo", "krea2", 8, 1.0, "euler")]
    [InlineData("z-image", "Z-Image", "lumina2", 25, 4.0, "res_multistep")]
    [InlineData("z-image-turbo", "Z-Image Turbo", "lumina2", 8, 1.0, "res_multistep")]
    public void Generation_packs_are_discoverable_and_bind_workflow_inputs(string id, string label,
        string encoder, int steps, double cfg, string samplerName)
    {
        var catalog = new EngineCatalog(Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
        var choices = new Dictionary<string, string>();
        var pack = Assert.Single(catalog.Profiles, profile => profile.Id == id);
        var choice = Assert.Single(catalog.ImageModels(AiTaskKind.GenerateImage, null, choices), model => model.Engine.Id == id);
        Assert.Equal(label, choice.Name);
        Assert.DoesNotContain(catalog.ImageModels(AiTaskKind.GenerativeFill, null, choices), model => model.Engine.Id == id);
        Assert.Equal(3, catalog.ModelSlots(pack).Count);
        var binding = Assert.Single(pack.Tasks);
        Assert.False(binding.SupportsReferenceImages);
        var workflow = catalog.ReadWorkflow(pack, pack.Workflow(binding.Workflow));
        var graph = WorkflowBinder.Bind(workflow, binding, new Dictionary<string, object?>
        {
            ["prompt"] = "A blue ceramic cup", ["seed"] = 42L, ["width"] = 640, ["height"] = 512
        });
        WorkflowExecution.Batch(graph, 2);
        Assert.Equal("A blue ceramic cup", graph["prompt"]!["inputs"]!["text"]!.GetValue<string>());
        Assert.Equal(encoder, graph["clip"]!["inputs"]!["type"]!.GetValue<string>());
        var sample = graph["sample"]!["inputs"]!;
        Assert.Equal((42L, steps, cfg, samplerName),
            (sample["seed"]!.GetValue<long>(), sample["steps"]!.GetValue<int>(), sample["cfg"]!.GetValue<double>(), sample["sampler_name"]!.GetValue<string>()));
        var latent = graph["latent"]!["inputs"]!;
        Assert.Equal((640, 512, 2), (latent["width"]!.GetValue<int>(), latent["height"]!.GetValue<int>(), latent["batch_size"]!.GetValue<int>()));
    }

    [Fact]
    public void All_bundled_flux3_tasks_share_the_reference_sampling_shift()
    {
        var catalog = new EngineCatalog(Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
        var pack = Assert.Single(catalog.Profiles, profile => profile.Id == Flux3Workflow.EngineId);
        Assert.Contains("ModelSamplingFlux", pack.RequiredNodeTypes);
        foreach (var task in pack.Tasks)
        {
            var graph = catalog.ReadWorkflow(pack, pack.Workflow(task.Workflow));
            var sampling = graph["sampling"]!["inputs"]!;
            Assert.Equal("unet", sampling["model"]![0]!.GetValue<string>());
            Assert.Equal("sampling", graph["sample"]!["inputs"]!["model"]![0]!.GetValue<string>());
            Assert.Equal(Math.Log(16), sampling["base_shift"]!.GetValue<double>(), 12);
            Assert.Equal(Math.Log(16), sampling["max_shift"]!.GetValue<double>(), 12);
        }
    }

    [Fact]
    public void Per_node_sampler_progress_is_scoped_to_our_prompt()
    {
        var initial = new AiOperationState { Status = AiOperationStatus.Queued };
        const string json = """{"type":"progress_state","data":{"prompt_id":"ours","nodes":{"sample":{"state":"running","value":7,"max":16}}}}""";
        Assert.Same(initial, ComfyEventParser.Parse(json, "another", initial));
        var state = ComfyEventParser.Parse(json, "ours", initial);
        Assert.Equal((AiOperationStatus.Running, "sample", 7, 16), (state.Status, state.NodeId, state.Value, state.Maximum));
    }

    public static bool Live => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COMPOSA_KLEIN3_TEST_URL"));

    [Fact(Skip = "Requires a local ComfyUI with Klein 3 installed.", SkipUnless = nameof(Live))]
    public async Task Local_flux_executes_with_and_without_a_mask_and_partner_graphs_validate_without_charges()
    {
        var url = Environment.GetEnvironmentVariable("COMPOSA_KLEIN3_TEST_URL")!;
        var token = TestContext.Current.CancellationToken;
        var service = new AiTaskService(() => url, Path.Combine(AppContext.BaseDirectory, "ai/engines"))
        {
            ConnectionTimeoutSeconds = 30
        };
        await service.TestConnectionAsync(token);
        foreach (var id in new[] { "seedream-5.5", Flux3Workflow.EngineId })
        {
            var pack = service.Engines.Find(id)!;
            Assert.Equal(id, pack.Id);
            foreach (var task in pack.Tasks)
            {
                var result = service.OperationCompatibility(pack, task.Task, new Dictionary<string,string>(), new());
                Assert.True(result.IsCompatible, string.Join(", ", result.Missing));
            }
        }
        service.SelectedEngine = service.Engines.Find(Flux3Workflow.EngineId);
        var session = EditorSession.NewCanvas(512, 512, SKColors.Gray);
        var editor = new EditorCommandService(session);
        var stages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var running = false;
        service.StateChanged += () => { stages.Enqueue($"{service.Operation?.Stage} {service.Operation?.Value}/{service.Operation?.Maximum}"); if (service.Operation is { Status: AiOperationStatus.Running, PromptId: not null }) running = true; };
        foreach (var task in new[] { AiTaskKind.GenerateImage, AiTaskKind.ImageEdit, AiTaskKind.GenerativeFill })
        {
            if (task == AiTaskKind.GenerativeFill) session.SelectRect(new(150, 150, 355, 355));
            var request = new AiTaskRequest { Task = task, EngineId = Flux3Workflow.EngineId,
                Prompt = task == AiTaskKind.GenerateImage ? "A blue ceramic cup on a pale wooden table" : "Change the cup to red",
                Settings = new() { Width = 512, Height = 512, Seed = Random.Shared.Next() } };
            await service.RunAsync(editor, request, token);
            Assert.Equal(AiOperationStatus.Completed, service.Operation!.Status);
            if (task == AiTaskKind.GenerativeFill) Assert.NotNull(session.Document.ActiveLayer!.Mask);
        }
        Assert.True(running, "Expected a server-confirmed running state: " + string.Join(" | ", stages));
    }
}
