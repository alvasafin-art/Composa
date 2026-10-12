using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Composa.AI;
using Composa.App.Assistant;
using Composa.App.Automation;
using Composa.Editing;
using Composa.IO;
using SkiaSharp;

namespace Composa.App.Tests;

public class AssistantWorkspaceTests
{
    [Theory]
    [InlineData("new TextItem();", "photoshop", false)]
    [InlineData("// new TextItem();\nvar text='new TextItem()';", "photoshop", true)]
    [InlineData("const layer=app.activeDocument.artLayers.add();", "photoshop", false)]
    [InlineData("var layer=app.activeDocument.artLayers.add();layer.kind=LayerKind.TEXT;layer.textItem.contents='Hello';layer.textItem.size=new UnitValue(24,'pt');", "photoshop", true)]
    [InlineData("#target photoshop\nvar doc=app.activeDocument;", "photoshop", true)]
    [InlineData("const text=await files.readText('/data/input.json');", "composa", true)]
    [InlineData("const = ;", "composa", false)]
    public void Script_validation_checks_target_syntax_without_running_code(string script, string target, bool valid)
        => Assert.Equal(valid, AssistantScriptValidation.Error(script, target) == null);

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Script_author_repairs_invalid_artifact_without_executing_it(bool tool)
    {
        var w = new MainWindow(); var s = EditorSession.NewCanvas(80, 60); w.AddSession(s);
        var bad = "new TextItem();"; var good = "var layer=app.activeDocument.artLayers.add();layer.kind=LayerKind.TEXT;layer.textItem.contents='Hello';";
        var provider = new Provider(tool ? Act("return_script", new { summary = "Draft", script = bad }) : new("Draft", bad), Act("return_script", new { summary = "Fixed", script = good }));
        try
        {
            var history = s.History.CurrentId;
            var result = await AssistantScriptAuthor.RunAsync(provider, new("Write JSX", "{}", "") { ScriptTarget = "photoshop" },
                new(w, s, new JavaScriptRuntime(), w.AiTasks, w.Settings, [], false), _ => { }, TestContext.Current.CancellationToken);
            Assert.Equal(good, result.Script); Assert.Equal(2, provider.Requests.Count);
            Assert.Contains(provider.Requests[1].ToolMessages, message => message.Text.Contains("not constructors"));
            Assert.Equal(history, s.History.CurrentId); Assert.Single(s.Document.Layers);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public async Task Script_author_stops_after_two_unsuccessful_repairs()
    {
        var w = new MainWindow(); var s = EditorSession.NewCanvas(80, 60); w.AddSession(s);
        var provider = new Provider(Act("return_script", new { summary = "Draft", script = "new TextItem();" }));
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => AssistantScriptAuthor.RunAsync(provider,
                new("Write JSX", "{}", "") { ScriptTarget = "photoshop" },
                new(w, s, new JavaScriptRuntime(), w.AiTasks, w.Settings, [], false), _ => { }, TestContext.Current.CancellationToken));
            Assert.Equal(3, provider.Requests.Count); Assert.Single(s.Document.Layers);
        }
        finally { w.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Document_alias_and_explicit_declaration_work_in_both_script_modes(bool declare, bool asynchronous)
    {
        var w = new MainWindow(); var s = EditorSession.NewCanvas(80, 60); var runtime = new JavaScriptRuntime();
        try
        {
            var code = (declare ? "const doc=app.activeDocument;" : "") + "doc.addRectangle(10,20,30,30,'#0000FF','Tile');";
            var history = s.History.Count;
            if (asynchronous) await runtime.ExecuteAsync(s, code, w.AiTasks, w.Settings, cancellationToken: TestContext.Current.CancellationToken);
            else runtime.Execute(s, code);
            var tile = Assert.Single(s.Document.Layers, l => l.Name == "Tile");
            Assert.Equal((uint)SKColors.Blue, tile.Shape!.Fill); Assert.Equal(10, tile.Transform.X);
            Assert.Equal(history + 1, s.History.Count); s.Undo(); Assert.Single(s.Document.Layers);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void Capability_search_finds_creation_instead_of_looping_over_selection_modifiers()
    {
        var w = new MainWindow();
        try
        {
            var catalog = new Composa.App.Mcp.EditorOperationCatalog(new Composa.App.Mcp.ComposaTools(w));
            Assert.Equal("select_shape", catalog.Search("rectangular selection marquee", catalog.Names, 4).First().Name);
            var mask = catalog.Search("selection rectangle mask layer", catalog.Names, 4).Select(tool => tool.Name).ToArray();
            Assert.Contains("select_shape", mask); Assert.Contains("layer_mask", mask);
            Assert.Equal("smart_object", catalog.Search("smart object convert", catalog.Names, 4).First().Name);
            Assert.Equal("filter_blur", catalog.Search("filter_blur", catalog.Names, 4).First().Name);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public async Task Repeated_discovery_stops_early_without_committing_a_fake_edit()
    {
        var w = new MainWindow(); w.AddSession(EditorSession.NewCanvas(80, 60));
        try
        {
            var s = w.Session!; var state = s.History.CurrentId;
            var provider = new Provider(Act("search_operations", new { query = "mask" }));
            var error = await Assert.ThrowsAsync<AssistantAgentException>(() => AssistantAgent.RunAsync(provider, new("Add mask", "{}", ""), s,
                new(w, s, new JavaScriptRuntime(), w.AiTasks, w.Settings, [], false), _ => { }, TestContext.Current.CancellationToken));
            Assert.Contains("without progress", error.Message); Assert.Equal(4, provider.Requests.Count); Assert.Equal(state, s.History.CurrentId);
        }
        finally { w.Close(); }
    }
    private static string Fixture()
    { var path = Path.Combine(AppContext.BaseDirectory, "workspace-fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static AssistantToolCall Call(string name, object args) => new(Guid.NewGuid().ToString("N"), name, JsonSerializer.SerializeToElement(args));
    private static AssistantPlan Act(string name, object args) => new("", "") { Calls = [Call(name, args)] };
    private sealed class Provider(params AssistantPlan[] plans) : IAssistantProvider
    {
        public string Id => "workspace-test";
        public bool SupportsTools => true;
        public List<AssistantRequest> Requests { get; } = [];
        public Task<AssistantPlan> PlanAsync(AssistantRequest request, CancellationToken cancellationToken = default)
        { Requests.Add(request); return Task.FromResult(plans[Math.Min(Requests.Count - 1, plans.Length - 1)]); }
    }

    [Fact]
    public async Task Granted_folders_support_unicode_pagination_and_revoke_immediately()
    {
        var root = Fixture(); var settings = new Settings { AssistantReadFolders = [root] }; var files = new AssistantFiles(settings);
        var path = Path.Combine(root, "инструкция.txt"); await File.WriteAllTextAsync(path, "один\nдва\nтри", TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(root, "other.txt"), "data");
        using var list = JsonDocument.Parse(files.List(root, 0, 1)); Assert.True(list.RootElement.GetProperty("hasMore").GetBoolean());
        using var page = JsonDocument.Parse(await files.Read(path, 5, 3, TestContext.Current.CancellationToken));
        Assert.Equal("два", page.RootElement.GetProperty("text").GetString()); Assert.Equal(8, page.RootElement.GetProperty("nextOffset").GetInt32());
        settings.AssistantReadFolders.Clear(); Assert.Throws<UnauthorizedAccessException>(() => files.Resolve(path));
        settings.AssistantReadFolders.Add(Path.GetPathRoot(path)!);
        Assert.Equal(Path.GetFullPath(path), files.Resolve(path));
    }

    [Fact]
    public void Photoshop_execution_request_stays_in_authoring_mode()
    {
        Assert.True(AssistantIntent.RequestsScript("Напиши скрипт Photoshop и запусти его"));
        Assert.True(AssistantIntent.RequestsScript("Write a Photoshop script and run it"));
        Assert.False(AssistantIntent.RequestsScript("Напиши скрипт Composa и запусти его"));
    }

    [AvaloniaFact]
    public async Task Script_author_reads_real_source_and_rejects_attempted_mutation()
    {
        var root = Fixture(); var path = Path.Combine(root, "brief.txt"); File.WriteAllText(path, "Create five blue squares");
        var w = new MainWindow(); w.AddSession(EditorSession.NewCanvas(80, 60)); w.Settings.AssistantReadFolders = [root]; w.Show();
        var provider = new Provider(Act("read_file", new { path }), Act("add_shape", new { kind = "rectangle", x = 0, y = 0, width = 20, height = 20 }),
            Act("return_script", new { summary = "Script ready", script = "console.log('five blue squares');" }));
        using var host = new LlamaServerHost(w.Settings); var chat = new AssistantWindow(w, () => w.Session, w.Settings, host, new JavaScriptRuntime(), w.AiTasks) { ProviderFactory = () => provider }; chat.Show(w);
        try
        {
            var history = w.Session!.History.CurrentId; await chat.SendAsync("Напиши скрипт по brief.txt из разрешённой папки.");
            Assert.Contains(provider.Requests[1].ToolMessages, message => message.Text.Contains("Create five blue squares"));
            Assert.Contains(provider.Requests[2].ToolMessages, message => message.Text.Contains("file reads only"));
            Assert.Equal(history, w.Session.History.CurrentId); Assert.Single(w.Session.Document.Layers);
            Assert.All(provider.Requests, request => Assert.DoesNotContain(request.Tools, tool => tool.Name == "add_shape"));
        }
        finally { chat.Close(); w.Close(); }
    }

    [Fact]
    public async Task Siblings_parent_traversal_links_binary_and_oversized_files_are_refused()
    {
        var root = Fixture(); var granted = Path.Combine(root, "input"); Directory.CreateDirectory(granted);
        var sibling = Path.Combine(root, "input-private"); Directory.CreateDirectory(sibling); File.WriteAllText(Path.Combine(sibling, "private.txt"), "private");
        var files = new AssistantFiles(new Settings { AssistantReadFolders = [granted] });
        Assert.Throws<UnauthorizedAccessException>(() => files.Resolve(Path.Combine(sibling, "private.txt")));
        Assert.Throws<UnauthorizedAccessException>(() => files.Resolve(Path.Combine(granted, "..", "input-private", "private.txt")));
        var binary = Path.Combine(granted, "binary.txt"); File.WriteAllBytes(binary, [0, 1, 2, 0]);
        await Assert.ThrowsAsync<InvalidDataException>(() => files.Read(binary, 0, 100));
        var big = Path.Combine(granted, "big.txt"); File.WriteAllText(big, new string('x', 1_000_001));
        await Assert.ThrowsAsync<IOException>(() => files.Read(big, 0, 100));
        // Windows junction creation does not require Developer Mode; Unix uses a symlink.
        var link = Path.Combine(granted, "link");
        if (!OperatingSystem.IsWindows())
        { Directory.CreateSymbolicLink(link, sibling); Assert.Throws<UnauthorizedAccessException>(() => files.Resolve(Path.Combine(link, "private.txt"))); }
    }

    [AvaloniaFact]
    public async Task Agent_opens_allowed_image_from_empty_app_then_edits_verifies_undoes_and_redoes()
    {
        var root = Fixture(); var path = Path.Combine(root, "photo.png");
        using (var image = Composa.Rendering.Pixels.NewColor(90, 60)) { image.Erase(SKColors.Blue); ImageFiles.Save(image, path, ExportFormat.Png); }
        var w = new MainWindow(); w.Settings.AssistantReadFolders = [root]; w.Show();
        using var host = new LlamaServerHost(w.Settings); var conversation = new AssistantConversation();
        var provider = new Provider(Act("begin_task", new { intent = "edit", goal = "Open and add Tile" }), Act("list_read_folders", new { }),
            Act("list_files", new { path = root }), Act("open_document", new { path }),
            Act("open_document", new { path }),
            Act("add_shape", new { kind = "rectangle", x = 10, y = 10, width = 20, height = 20, name = "Tile", color = "#FFFF00" }),
            Act("verify_document", new { checks = new object[] { new { layer = "Tile", property = "shape.fill", expected = "#FFFF00" }, new { property = "width", expected = 90 } } }), Act("finish_task", new { summary = "Done" }));
        var chat = new AssistantWindow(w, () => w.Session, w.Settings, host, new JavaScriptRuntime(), w.AiTasks, conversation) { ProviderFactory = () => provider }; chat.Show(w);
        try
        {
            await chat.SendAsync("Открой photo.png и добавь желтый квадрат Tile.");
            Assert.DoesNotContain(conversation.Entries, e => e.Text.StartsWith("Error:")); Assert.Single(w.Sessions);
            Assert.Equal(2, w.Session!.Document.Layers.Count); Assert.Equal(90, w.Session.Document.Width);
            w.Session.Undo(); Assert.Single(w.Session.Document.Layers); w.Session.Redo(); Assert.Equal(2, w.Session.Document.Layers.Count);
            Assert.Contains(provider.Requests.Skip(4), r => r.ToolMessages.Any(m => m.Text.Contains("opened")));
            Screenshots.Save(chat, "assistant-workspace-open-edit");
        }
        finally { chat.Close(); w.Close(); }
    }

    [AvaloniaFact]
    public async Task Unverified_edit_cannot_bypass_host_checks_by_omitting_begin_task()
    {
        var w = new MainWindow(); w.AddSession(EditorSession.NewCanvas(80, 60)); w.Show();
        try
        {
            var s = w.Session!; var before = s.History.CurrentId;
            var provider = new Provider(Act("add_shape", new { kind = "rectangle", x = 0, y = 0, width = 20, height = 20 }), Act("finish_task", new { summary = "Done" }));
            await Assert.ThrowsAsync<AssistantAgentException>(() => AssistantAgent.RunAsync(provider, new("Add a shape", "{}", JavaScriptRuntime.Reference), s,
                new(w, s, new JavaScriptRuntime(), w.AiTasks, w.Settings, [], false), _ => { }, TestContext.Current.CancellationToken));
            Assert.Equal(before, s.History.CurrentId); Assert.Single(s.Document.Layers);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public async Task Photoshop_script_is_never_applied_or_saved_to_Composa_library()
    {
        var w = new MainWindow(); w.AddSession(EditorSession.NewCanvas(80, 60)); w.Show();
        using var host = new LlamaServerHost(w.Settings); var conversation = new AssistantConversation();
        var provider = new Provider(new AssistantPlan("JSX ready", "#target photoshop\nvar doc = app.activeDocument;"));
        var chat = new AssistantWindow(w, () => w.Session, w.Settings, host, new JavaScriptRuntime(), w.AiTasks, conversation) { ProviderFactory = () => provider }; chat.Show(w);
        try
        {
            var before = w.Session!.History.CurrentId; await chat.SendAsync("Напиши скрипт для Photoshop");
            Assert.Equal("photoshop", Assert.Single(provider.Requests).ScriptTarget);
            Assert.Equal(before, w.Session.History.CurrentId);
            var draft = Assert.Single(conversation.Entries, e => e.Role == "assistant"); Assert.Equal("photoshop", draft.ScriptTarget);
            Assert.DoesNotContain(chat.GetVisualDescendants().OfType<Button>(), b => b.Content?.ToString() is "Apply edit" or "Save to Library");
            Screenshots.Save(chat, "assistant-photoshop-script");
        }
        finally { chat.Close(); w.Close(); }
    }
}
