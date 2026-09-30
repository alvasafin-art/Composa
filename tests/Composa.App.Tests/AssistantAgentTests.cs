using System.Text.Json;
using Avalonia.Headless.XUnit;
using Composa.AI;
using Composa.App.Assistant;
using Composa.App.Automation;
using Composa.Editing;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class AssistantAgentTests
{
    [AvaloniaFact]
    public async Task Oversized_native_shape_is_refused_before_allocating_pixels()
    {
        var owner=new MainWindow(); owner.AddSession(EditorSession.NewCanvas(100,80)); owner.Show();
        try
        {
            var host=new AssistantEditorTools(owner,owner.Session!,new JavaScriptRuntime(),owner.AiTasks,owner.Settings,[],false);
            await Assert.ThrowsAnyAsync<Exception>(()=>host.ExecuteAsync(Call("add_shape",new { kind="rectangle",x=0,y=0,width=Composa.Model.DocumentLimits.MaxSide,height=Composa.Model.DocumentLimits.MaxSide }),TestContext.Current.CancellationToken));
            Assert.Single(owner.Session!.Document.Layers); Assert.False(owner.Session.HasPendingEdit);
        }
        finally { owner.Close(); }
    }
    [AvaloniaFact]
    public async Task Native_editor_accepts_full_context_ids_for_duplicate_layer_names_and_refuses_other_tabs()
    {
        var owner=new MainWindow(); var session=EditorSession.NewCanvas(100,80); owner.AddSession(session); owner.Show();
        try
        {
            var first=session.AddBlankLayer(); session.Rename(first,"Duplicate");
            var second=session.AddBlankLayer(); session.Rename(second,"Duplicate");
            var other=EditorSession.NewCanvas(60,40); owner.AddSession(other);
            var host=new AssistantEditorTools(owner,session,new JavaScriptRuntime(),owner.AiTasks,owner.Settings,[],false);
            await session.RunTransactionAsync("Assistant edit",async _=>
            {
                await host.ExecuteAsync(Call("set_layer",new { layer=first.Id.ToString(),name="Correct" }),TestContext.Current.CancellationToken);
                await Assert.ThrowsAnyAsync<Exception>(()=>host.ExecuteAsync(Call("set_layer",new { layer=other.ActiveLayer!.Name,document=2,name="Wrong" }),TestContext.Current.CancellationToken));
            });
            Assert.Equal("Correct",session.Document.Find(first.Id)!.Name); Assert.Equal("Duplicate",session.Document.Find(second.Id)!.Name);
            Assert.NotEqual("Wrong",other.ActiveLayer!.Name); session.Undo(); Assert.Equal("Duplicate",session.Document.Find(first.Id)!.Name);
        }
        finally { owner.Close(); }
    }
    [AvaloniaFact]
    public async Task Local_agent_edits_existing_grouped_text_and_adds_a_mask_when_live_test_is_requested()
    {
        var executable = Environment.GetEnvironmentVariable("COMPOSA_LIVE_LLAMA_SERVER");
        var model = Environment.GetEnvironmentVariable("COMPOSA_LIVE_LLAMA_MODEL");
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(model)) return;
        var owner = new MainWindow(); owner.AddSession(EditorSession.NewCanvas(640,420,SKColors.White)); owner.Show();
        var runtime = new JavaScriptRuntime();
        runtime.Execute(owner.Session!, """
            const doc=app.activeDocument;
            const card=doc.addRectangle(40,50,300,200,'#0088CC','Card Background');
            const title=doc.addText('Скидки',70,90,{size:24,color:'#000000',name:'Title'});
            title.tags=['title']; doc.groupLayers([card.id,title.id],'Card');
            doc.addText('Не меняй меня',50,330,{size:16,color:'#000000',name:'Footer'});
            """);
        var settings = new Settings { AssistantServerExecutable=executable, AssistantModelPath=model,
            AssistantServerUrl="http://127.0.0.1:18080", AssistantContextSize=16384, AssistantMaxTokens=1536, AssistantApplyEdits=true };
        using var host = new LlamaServerHost(settings); var conversation=new AssistantConversation();
        var chat=new AssistantWindow(owner,()=>owner.Session,settings,host,runtime,owner.AiTasks,conversation); chat.Show(owner);
        try
        {
            var session=owner.Session!;
            var title=session.Document.AllLayers().Single(layer=>layer.Name=="Title"); var oldX=title.Transform.X;
            var footer=session.Document.AllLayers().Single(layer=>layer.Name=="Footer"); var footerId=footer.Id;
            var history=session.History.Count;
            await chat.SendAsync("В группе Card измени существующий текст Title на Осенняя распродажа, сделай его белым и размером 28. Сдвинь только этот текст на 20 пикселей вправо. Не создавай новые слои. Footer и остальные слои не меняй.");
            Assert.DoesNotContain(conversation.Entries,entry=>entry.Text.StartsWith("Error:"));
            title=session.Document.AllLayers().Single(layer=>layer.Name=="Title");
            Assert.Equal("Осенняя распродажа",title.Text!.Text); Assert.Equal(28,title.Text.Size);
            Assert.Equal((uint)SKColors.White,title.Text.Color); Assert.InRange(title.Transform.X,oldX+19.5,oldX+20.5);
            Assert.Equal("Не меняй меня",session.Document.Find(footerId)!.Text!.Text);
            Assert.Equal(history+1,session.History.Count); Assert.True(Screenshots.Save(owner,"assistant-live-grouped-text"));
            session.Undo(); Assert.Equal("Скидки",session.Document.Find(title.Id)!.Text!.Text); session.Redo();
            history=session.History.Count;
            await chat.SendAsync("Выдели прямоугольную область x=60, y=70, width=100, height=80. Добавь по этому выделению слой-маску к существующему Card Background. Затем сними выделение. Ничего больше не меняй, форму не растеризуй.");
            Assert.DoesNotContain(conversation.Entries,entry=>entry.Text.StartsWith("Error:"));
            var card=session.Document.AllLayers().Single(layer=>layer.Name=="Card Background");
            Assert.NotNull(card.Shape); Assert.NotNull(card.Mask); Assert.Null(session.Selection);
            Assert.Equal(255,card.Mask.GetPixel(30,30).Alpha); Assert.Equal(0,card.Mask.GetPixel(180,150).Alpha);
            Assert.Equal(history+1,session.History.Count); Assert.True(Screenshots.Save(owner,"assistant-live-layer-mask"));
            session.Undo(); Assert.Null(session.Document.Find(card.Id)!.Mask);
        }
        finally { chat.Close(); owner.Close(); }
    }

    private static AssistantToolCall Call(string name, object args) => new(Guid.NewGuid().ToString("N"), name, JsonSerializer.SerializeToElement(args));
    private static AssistantPlan Actions(params AssistantToolCall[] calls) => new("", "") { Calls = calls };
    private sealed class Provider(params AssistantPlan[] replies) : IAssistantProvider
    {
        public string Id => "native-test";
        public List<AssistantRequest> Requests { get; } = [];
        public Task<AssistantPlan> PlanAsync(AssistantRequest request, CancellationToken cancellationToken = default)
        { Requests.Add(request with { ToolMessages = request.ToolMessages.ToArray() }); return Task.FromResult(replies[Math.Min(Requests.Count - 1, replies.Length - 1)]); }
    }
    private static Task<AssistantAgentResult> Run(MainWindow owner, Provider provider, string text = "Измени документ") =>
        AssistantAgent.RunAsync(provider, new(text, "{}", JavaScriptRuntime.Reference), owner.Session!,
            new AssistantEditorTools(owner, owner.Session!, new JavaScriptRuntime(), owner.AiTasks, owner.Settings, [], false), _ => { }, TestContext.Current.CancellationToken);

    [AvaloniaFact]
    public async Task Native_commands_edit_text_groups_masks_and_canvas_with_one_undo()
    {
        var owner = new MainWindow(); owner.AddSession(EditorSession.NewCanvas(100, 80, SKColors.White)); owner.Show();
        try
        {
            var provider = new Provider(
                Actions(Call("list_operations", new { name = "add_text" })),
                Actions(Call("editor_operation", new { name = "add_text", arguments = new { text = "Old", x = 12, y = 12 } })),
                Actions(Call("editor_operation", new { name = "set_text", arguments = new { layer = "Old", text = "New", size = 14, color = "#0088FF" } })),
                Actions(Call("editor_operation", new { name = "layer_mask", arguments = new { layer = "New", action = "add" } })),
                Actions(Call("editor_operation", new { name = "group_layers", arguments = new { layers = new[] { "New" }, name = "Titles" } })),
                Actions(Call("editor_operation", new { name = "resize_document", arguments = new { width = 120, height = 100, mode = "canvas" } })),
                Actions(Call("get_document", new { })), new("Done", ""));
            var before = owner.Session!.History.Count;
            var result = await Run(owner, provider);
            Assert.True(result.Changed); Assert.Equal(7, result.ToolCount);
            Assert.Equal(before + 1, owner.Session.History.Count);
            Assert.Equal(120, owner.Session.Document.Width);
            var folder = Assert.Single(owner.Session.Document.Layers, layer => layer.IsGroup);
            var child = Assert.Single(folder.Children);
            Assert.Equal("New", child.Text!.Text); Assert.Equal(14, child.Text.Size); Assert.NotNull(child.Mask);
            Assert.Contains("inputSchema", provider.Requests[1].ToolMessages[1].Text);
            owner.Session.Undo(); Assert.Single(owner.Session.Document.Layers); Assert.Equal(100, owner.Session.Document.Width);
            owner.Session.Redo(); Assert.Contains(owner.Session.Document.AllLayers(), layer => layer.Text?.Text == "New");
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task Failed_native_command_restores_its_partial_edits_and_can_be_repaired()
    {
        var owner = new MainWindow(); owner.AddSession(EditorSession.NewCanvas(100, 80)); owner.Show();
        try
        {
            var name = owner.Session!.ActiveLayer!.Name;
            var provider = new Provider(
                Actions(Call("editor_operation", new { name = "set_layer", arguments = new { layer = name, name = "Wrong", opacity = 3 } })),
                Actions(Call("editor_operation", new { name = "set_layer", arguments = new { layer = name, name = "Repaired", opacity = 0.5 } })),
                Actions(Call("get_document", new { })), new("Done", ""));
            var result = await Run(owner, provider);
            Assert.True(result.Changed); Assert.Equal("Repaired", owner.Session.ActiveLayer!.Name);
            Assert.Contains("ERROR", provider.Requests[1].ToolMessages.Last().Text);
            Assert.Equal(0.5, owner.Session.ActiveLayer.Opacity);
            owner.Session.Undo(); Assert.Equal(name, owner.Session.ActiveLayer!.Name); Assert.Equal(1, owner.Session.ActiveLayer.Opacity);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task Final_claim_without_actions_is_never_reported_as_an_applied_edit()
    {
        var owner = new MainWindow(); owner.AddSession(EditorSession.NewCanvas(100, 80)); owner.Show();
        try
        {
            var before = owner.Session!.History.Count;
            var provider = new Provider(new AssistantPlan("I created a blue rectangle", ""));
            var result = await Run(owner, provider, "Нарисуй голубой прямоугольник");
            Assert.False(result.Changed); Assert.Equal(0, result.ToolCount);
            Assert.Equal(before, owner.Session.History.Count); Assert.Equal(2, provider.Requests.Count);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task Script_result_contains_actual_document_for_verification_and_one_undo()
    {
        var owner = new MainWindow(); owner.AddSession(EditorSession.NewCanvas(100, 80, SKColors.White)); owner.Show();
        try
        {
            var provider = new Provider(Actions(Call("execute_script", new { code = "app.activeDocument.addRectangle(10,10,20,20,'#0088FF','Box');" })),
                new("Done", ""));
            var result = await Run(owner, provider);
            Assert.True(result.Changed); Assert.Equal("Done", result.Summary);
            Assert.Contains("ACTUAL DOCUMENT", provider.Requests[1].ToolMessages.Last().Text);
            using var image = owner.Session!.Flatten(); Assert.Equal(new SKColor(0,136,255), image.GetPixel(20,20));
            owner.Session.Undo(); Assert.Single(owner.Session.Document.Layers);
        }
        finally { owner.Close(); }
    }
}
