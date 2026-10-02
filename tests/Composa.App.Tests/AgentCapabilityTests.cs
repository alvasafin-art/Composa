using System.Text.Json;
using Avalonia.Headless.XUnit;
using Composa.AI;
using Composa.App.Assistant;
using Composa.App.Automation;
using Composa.App.Mcp;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public class AgentCapabilityTests
{
    [AvaloniaFact]
    public async Task Native_query_filters_before_stepping_and_batch_preserves_objects_and_undo()
    {
        var w=Window();
        try
        {
            new JavaScriptRuntime().Execute(w.Session!,"const d=app.activeDocument;for(let i=0;i<11;i++)d.addRectangle(10+i*45,30,30,30,'#0000FF','Item '+i);d.addRectangle(20,120,80,30,'#00FF00','Wide');");
            var s=w.Session!; var before=AssistantEditorTools.Fingerprint(s); var history=s.History.Count;
            var query=new {kind="shape",shapeKind="Rectangle",aspectRatio=1,order="top",start=0,step=2}; var tools=Tools(w);
            using var preview=JsonDocument.Parse(await tools.ExecuteAsync(Call("query_layers",new {query}),TestContext.Current.CancellationToken));
            Assert.Equal(6,preview.RootElement.GetProperty("matchedCount").GetInt32());
            using var edited=JsonDocument.Parse(await tools.ExecuteAsync(Call("batch_set_layers",new {query,shapeColor="#FFFF00"}),TestContext.Current.CancellationToken));
            Assert.Equal(6,edited.RootElement.GetProperty("matchedCount").GetInt32()); Assert.Equal(history+1,s.History.Count);
            var shapes=s.Document.AllLayers().Where(layer=>layer.Name.StartsWith("Item ")).Reverse().ToArray();
            for(var i=0;i<shapes.Length;i++) { Assert.NotNull(shapes[i].Shape); Assert.Equal((uint)(i%2==0?SKColors.Yellow:SKColors.Blue),shapes[i].Shape!.Fill); }
            Assert.Equal((uint)SKColors.Lime,s.Document.AllLayers().Single(layer=>layer.Name=="Wide").Shape!.Fill);
            s.Undo(); Assert.Equal(before,AssistantEditorTools.Fingerprint(s));
        }
        finally {w.Close();}
    }

    [AvaloniaFact]
    public async Task Batch_validates_all_targets_before_editing_and_rejects_unknown_query_fields()
    {
        var w=Window();
        try
        {
            w.Session!.AddShape(new ShapeStyle(ShapeKind.Rectangle,(uint)SKColors.Blue,0),SKRect.Create(20,20,30,30));
            var before=AssistantEditorTools.Fingerprint(w.Session); var tools=Tools(w);
            await Assert.ThrowsAnyAsync<Exception>(()=>tools.ExecuteAsync(Call("batch_set_layers",new {query=new {order="top"},shapeColor="#FFFF00"}),TestContext.Current.CancellationToken));
            await Assert.ThrowsAnyAsync<Exception>(()=>tools.ExecuteAsync(Call("batch_set_layers",new {query=new {kind="shape",invented=true},shapeColor="#FFFF00"}),TestContext.Current.CancellationToken));
            Assert.Equal(before,AssistantEditorTools.Fingerprint(w.Session));
        }
        finally {w.Close();}
    }

    [AvaloniaFact]
    public async Task Host_rechecks_successful_verification_if_a_later_edit_breaks_it()
    {
        var w=Window();
        try
        {
            var checks=new[] {new {layer="Tile",property="shape.fill",expected="#FFFF00"}};
            var provider=new Provider(Act(Call("begin_task",new {intent="edit",goal="Yellow tile"})),
                Act(Call("add_shape",new {kind="rectangle",x=20,y=20,width=40,height=40,name="Tile",color="#FFFF00"})),
                Act(Call("verify_document",new {checks})),Act(Call("set_shape",new {layer="Tile",color="#0000FF"})),new("Yellow tile ready",""));
            await Assert.ThrowsAsync<AssistantAgentException>(()=>Run(w,provider)); Assert.Single(w.Session!.Document.Layers);
        }
        finally {w.Close();}
    }

    [AvaloniaFact]
    public async Task Already_satisfied_verified_request_is_a_valid_noop_without_history()
    {
        var w=Window();
        try
        {
            var before=w.Session!.History.CurrentId;
            var provider=new Provider(Act(Call("begin_task",new {intent="edit",goal="Background fully opaque"})),
                Act(Call("verify_document",new {checks=new[] {new {layer="Background",property="opacity",expected=1}}})),new("Already opaque",""));
            var result=await Run(w,provider); Assert.False(result.Changed); Assert.Equal(before,w.Session.History.CurrentId);
        }
        finally {w.Close();}
    }
    [AvaloniaFact]
    public async Task Script_reference_is_complete_in_one_discovery_call()
    {
        var w=Window();
        try
        {
            var tools=Tools(w);
            using var reference=JsonDocument.Parse(await tools.ExecuteAsync(Call("get_script_api",new { }),TestContext.Current.CancellationToken));
            Assert.True(reference.RootElement.GetProperty("complete").GetBoolean());
            Assert.Equal(JavaScriptRuntime.Reference,reference.RootElement.GetProperty("api").GetString());
            Assert.Empty(tools.Definitions.Single(tool=>tool.Name=="get_script_api").Parameters.GetProperty("properties").EnumerateObject());
        }
        finally {w.Close();}
    }
    [AvaloniaFact]
    public async Task Group_bounds_describe_children_and_do_not_hide_overflow()
    {
        var w=Window();
        try
        {
            new JavaScriptRuntime().Execute(w.Session!,"const d=app.activeDocument;const a=d.addRectangle(10,20,20,20);const b=d.addRectangle(590,20,30,20);d.groupLayers([a.id,b.id],'Group');");
            using var state=JsonDocument.Parse(await Tools(w).ExecuteAsync(Call("get_document_state",new {layer="Group"}),TestContext.Current.CancellationToken));
            var group=state.RootElement.GetProperty("layers")[0]; Assert.False(group.GetProperty("insideCanvas").GetBoolean());
            Assert.Equal(610,group.GetProperty("bounds").GetProperty("width").GetDouble());
        }
        finally {w.Close();}
    }
    [AvaloniaFact]
    public async Task Native_render_returns_actual_region_pixels_only_with_vision_enabled()
    {
        var w=Window();
        try
        {
            w.Settings.AssistantVision=true; var tools=Tools(w);
            await tools.ExecuteAsync(Call("render",new { x=10,y=20,width=32,height=24,grid=0 }),TestContext.Current.CancellationToken);
            var data=Assert.IsType<string>(tools.TakeRenderedImage());
            using var png=SKBitmap.Decode(Convert.FromBase64String(data[(data.IndexOf(',')+1)..]));
            Assert.Equal((32,24),(png.Width,png.Height)); Assert.Equal(SKColors.White,png.GetPixel(0,0));
            Assert.Null(tools.TakeRenderedImage()); w.Settings.AssistantVision=false;
            await tools.ExecuteAsync(Call("render",new { }),TestContext.Current.CancellationToken); Assert.Null(tools.TakeRenderedImage());
        }
        finally {w.Close();}
    }
    [Fact]
    public void Context_budget_drops_complete_turns_and_preserves_tool_json_and_call_ids()
    {
        var call = Call("get_document_state", new { });
        var latest = Call("verify_document", new { });
        AssistantToolMessage[] messages = [new("assistant", "", Calls:[call]), new("tool", new string('x',3000),call.Id),
            new("assistant","", Calls:[latest]), new("tool","{\"passed\":true}",latest.Id)];
        var fitted=ChatCompletionAssistantProvider.FitToolMessages(messages,500);
        Assert.Equal(2,fitted.Count); Assert.Equal(latest.Id,fitted[0].Calls![0].Id); Assert.Equal(latest.Id,fitted[1].CallId);
        using var json=JsonDocument.Parse(fitted[1].Text); Assert.True(json.RootElement.GetProperty("passed").GetBoolean());
    }
    [Fact]
    public void Local_schema_adapter_preserves_contract_and_only_normalizes_schema_nodes()
    {
        var schema=JsonSerializer.SerializeToElement(new { type="object",properties=new { expected=true, enabled=new { type="boolean", @default=true } },additionalProperties=false });
        var local=ChatCompletionAssistantProvider.ToolSchema(schema,true);
        Assert.Equal("{}",local["properties"]!["expected"]!.ToJsonString());
        Assert.True(local["properties"]!["enabled"]!["default"]!.GetValue<bool>());
        Assert.False(local["additionalProperties"]!.GetValue<bool>());
        Assert.True(ChatCompletionAssistantProvider.ToolSchema(schema,false)["properties"]!["expected"]!.GetValue<bool>());
    }

    [Fact]
    public void Script_memory_budget_excludes_native_work_but_accumulates_interpreter_allocations()
    {
        var budget=new ScriptMemoryBudget(500_000); budget.Check();
        for(var i=0;i<4;i++) { var buffer=budget.Native(()=>new byte[1_000_000]); GC.KeepAlive(buffer); }
        budget.Check(); var scriptBuffer=new byte[600_000];
        Assert.Throws<InvalidOperationException>(budget.Check); GC.KeepAlive(scriptBuffer);
    }

    [AvaloniaFact]
    public void Native_script_batch_is_not_charged_as_interpreter_memory_and_remains_one_undo()
    {
        var w=Window();
        try
        {
            var history=w.Session!.History.Count;
            new JavaScriptRuntime().Execute(w.Session,"for(let i=0;i<20;i++)app.activeDocument.addRectangle(i*20,20,10,10,'#0088FF','Tile '+i);");
            Assert.Equal(21,w.Session.Document.Layers.Count); Assert.Equal(history+1,w.Session.History.Count);
            w.Session.Undo(); Assert.Single(w.Session.Document.Layers);
        }
        finally {w.Close();}
    }

    private static AssistantToolCall Call(string name, object args) => new(Guid.NewGuid().ToString("N"), name, JsonSerializer.SerializeToElement(args));
    private static AssistantPlan Act(params AssistantToolCall[] calls) => new("", "") { Calls = calls };
    private sealed class Provider(params AssistantPlan[] plans) : IAssistantProvider
    {
        public string Id => "capability-test";
        public bool SupportsTools => true;
        public List<AssistantRequest> Requests { get; } = [];
        public Task<AssistantPlan> PlanAsync(AssistantRequest request, CancellationToken cancellationToken = default)
        { Requests.Add(request with { Tools = request.Tools.ToArray(), ToolMessages = request.ToolMessages.ToArray() }); return Task.FromResult(plans[Math.Min(Requests.Count - 1, plans.Length - 1)]); }
    }
    private static MainWindow Window()
    { var w = new MainWindow(); w.Settings.CheckForUpdates = false; w.AddSession(EditorSession.NewCanvas(600, 400, SKColors.White)); w.Show(); return w; }
    private static AssistantEditorTools Tools(MainWindow w) => new(w, w.Session!, new JavaScriptRuntime(), w.AiTasks, w.Settings, [], false);
    private static Task<AssistantAgentResult> Run(MainWindow w, Provider provider) => AssistantAgent.RunAsync(provider,
        new("Выполни запрос", "{}", JavaScriptRuntime.Reference), w.Session!, Tools(w), _ => { }, TestContext.Current.CancellationToken);

    [AvaloniaFact]
    public async Task Discovery_promotes_the_exact_native_schema_and_direct_tool_is_executable()
    {
        var w = Window();
        try
        {
            var tools = Tools(w); Assert.DoesNotContain(tools.Definitions, tool => tool.Name == "filter_blur");
            using var discovered = JsonDocument.Parse(await tools.ExecuteAsync(Call("list_operations", new { name = "filter_blur" }), TestContext.Current.CancellationToken));
            var definition = Assert.Single(tools.Definitions, tool => tool.Name == "filter_blur");
            var native = new EditorOperationCatalog(new ComposaTools(w, w.Session));
            Assert.Equal(native.Schema("filter_blur").Description, definition.Description);
            Assert.Equal(native.Schema("filter_blur").InputSchema.GetRawText(), definition.Parameters.GetRawText());
            await tools.ExecuteAsync(Call("filter_blur", new { radius = 2 }), TestContext.Current.CancellationToken);
            Assert.Contains(tools.Definitions, tool => tool.Name == "verify_document");
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public async Task Search_exposes_long_tail_operations_in_the_next_model_turn_without_duplicate_schemas()
    {
        var w = Window();
        try
        {
            var provider = new Provider(Act(Call("search_operations", new { query = "group_layers" })),
                Act(Call("add_shape", new { kind = "rectangle", x = 20, y = 20, width = 40, height = 40, name = "Tile" })),
                Act(Call("group_layers", new { layers = new[] { "Tile" }, name = "Container" })), new("Done", ""));
            var result = await Run(w, provider);
            Assert.Contains(provider.Requests[1].Tools, tool => tool.Name == "group_layers");
            Assert.All(provider.Requests, request => Assert.Equal(request.Tools.Count, request.Tools.Select(tool => tool.Name).Distinct().Count()));
            Assert.True(result.Changed); Assert.Single(w.Session!.Document.Layers.Where(layer => layer.IsGroup));
            foreach (var message in provider.Requests.SelectMany(request => request.ToolMessages).Where(message => message.Role == "tool"))
                using (var json = JsonDocument.Parse(message.Text)) Assert.True(json.RootElement.TryGetProperty("ok", out _));
            w.Session.Undo(); Assert.Single(w.Session.Document.Layers);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public async Task Host_checks_declared_postconditions_and_rejects_prose_over_incorrect_results()
    {
        var w = Window();
        try
        {
            var provider = new Provider(Act(Call("begin_task", new { intent = "edit", goal = "Yellow rectangle" })),
                Act(Call("add_shape", new { kind = "rectangle", x = 20, y = 20, width = 40, height = 40, name = "Tile", color = "#0000FF" })),
                Act(Call("verify_document",new {checks=new[] {new {layer="Tile",property="shape.fill",expected="#FFFF00"}}})),new("The rectangle is yellow", ""));
            var old = w.Session!.History.CurrentId;
            var error = await Assert.ThrowsAsync<AssistantAgentException>(() => Run(w, provider));
            Assert.Contains("postconditions", error.Message); Assert.Single(w.Session.Document.Layers); Assert.Equal(old, w.Session.History.CurrentId);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public async Task Failed_check_can_be_corrected_and_rechecked_with_one_undo()
    {
        var w = Window();
        try
        {
            var check = new[] { new { layer = "Tile", property = "shape.fill", expected = "#FFFF00" } };
            var provider = new Provider(Act(Call("begin_task", new { intent = "edit", goal = "Yellow tile" })),
                Act(Call("add_shape", new { kind = "rectangle", x = 20, y = 20, width = 40, height = 40, name = "Tile", color = "#0000FF" })),
                Act(Call("verify_document", new { checks = check })), Act(Call("set_shape", new { layer = "Tile", color = "#FFFF00" })),
                Act(Call("verify_document", new { checks = check })), new("Done", ""));
            var old = w.Session!.History.Count; var result = await Run(w, provider);
            Assert.Equal((uint)SKColors.Yellow, w.Session.ActiveLayer!.Shape!.Fill); Assert.Equal(old + 1, w.Session.History.Count);
            Assert.Contains("HOST FINAL VERIFICATION", result.Log);
            Assert.DoesNotContain(provider.Requests[1].Tools,tool=>tool.Name=="begin_task");
            Assert.Contains("HOST TASK STATE",provider.Requests[4].DocumentContext);
            Assert.Contains("set_shape",provider.Requests[4].DocumentContext);
            using var receipt = JsonDocument.Parse(provider.Requests[4].ToolMessages.Last().Text);
            Assert.Single(receipt.RootElement.GetProperty("changes").GetProperty("modifiedLayerIds").EnumerateArray());
            w.Session.Undo(); Assert.Single(w.Session.Document.Layers);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public async Task Inspect_intent_is_semantic_read_only_and_creates_no_undo_entry()
    {
        var w = Window();
        try
        {
            var provider = new Provider(Act(Call("begin_task", new { intent = "inspect", goal = "Read document dimensions" })),
                Act(Call("get_document_state", new { })), new("600 × 400", ""));
            var old = w.Session!.History.CurrentId; var result = await Run(w, provider);
            Assert.False(result.Changed); Assert.Equal(old, w.Session.History.CurrentId);
            var tools = Tools(w);
            await tools.ExecuteAsync(Call("begin_task", new { intent = "inspect", goal = "Read only" }), TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() => tools.ExecuteAsync(Call("add_shape", new { kind = "rectangle", x = 10, y = 10, width = 20, height = 20 }), TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => tools.ExecuteAsync(Call("begin_task", new { intent = "edit", goal = "Escalation" }), TestContext.Current.CancellationToken));
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public async Task Paged_state_has_stable_ids_real_guides_and_no_ambiguous_name_guessing()
    {
        var w = Window();
        try
        {
            for (var i = 0; i < 17; i++) w.Session!.AddShape(new ShapeStyle(ShapeKind.Rectangle, (uint)SKColors.Blue, 0), SKRect.Create(i * 20, 10, 10, 10));
            var tools = Tools(w); var ids = new List<string>(); var offset = 0;
            while (true)
            {
                using var page = JsonDocument.Parse(await tools.ExecuteAsync(Call("get_document_state", new { offset, count = 4 }), TestContext.Current.CancellationToken));
                ids.AddRange(page.RootElement.GetProperty("layers").EnumerateArray().Select(layer => layer.GetProperty("id").GetString()!));
                if (!page.RootElement.GetProperty("hasMore").GetBoolean()) break;
                offset = page.RootElement.GetProperty("nextOffset").GetInt32();
            }
            Assert.Equal(18, ids.Count); Assert.Equal(ids.Count, ids.Distinct().Count());
            var pair = w.Session!.Document.Layers.Skip(1).Take(2).ToArray(); foreach (var layer in pair) w.Session.Rename(layer, "Duplicate");
            await Assert.ThrowsAnyAsync<Exception>(() => tools.ExecuteAsync(Call("get_document_state", new { layer = "Duplicate" }), TestContext.Current.CancellationToken));
            using var specific = JsonDocument.Parse(await tools.ExecuteAsync(Call("get_document_state", new { layer = pair[1].Id.ToString() }), TestContext.Current.CancellationToken));
            Assert.Equal(pair[1].Id.ToString(), specific.RootElement.GetProperty("layers")[0].GetProperty("id").GetString());
        }
        finally { w.Close(); }
    }
}
