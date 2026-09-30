using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Composa.App.Automation;
using Composa.Editing;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class AutomationTests
{
    [Fact]
    public void Reusable_square_script_supports_console_output_without_rolling_back_edits()
    {
        var session = EditorSession.NewCanvas(1200, 600, SKColors.White);
        var history = session.History.Count;
        var result = new JavaScriptRuntime().Execute(session, """
            const doc = app.activeDocument;
            const colors = ['#0000FF', '#00FF00', '#FF0000'];
            let x = 100;
            for (let i = 0; i < 3; i++) {
              const size = 100 * Math.pow(2, i);
              doc.addShape({kind:'rectangle', x, y:100, width:size, height:size, color:colors[i], name:`Square ${i+1}`});
              x += size + 120;
            }
            console.log('Created', 3, 'squares');
            console.info({complete:true}); console.warn('Warning'); console.error('Diagnostic');
            for (let i=0;i<100;i++) console.log('x'.repeat(100));
            """);
        Assert.Equal(4, session.Document.Layers.Count);
        Assert.Equal(new[] { 100.0, 200.0, 400.0 }, session.Document.Layers.Skip(1).Select(layer => layer.Transform.Width));
        Assert.StartsWith("Created 3 squares", result.Output);
        Assert.Contains("{\"complete\":true}", result.Output);
        Assert.InRange(result.Output.Length, 1, 4096);
        Assert.Equal(history + 1, session.History.Count);
        session.Undo(); Assert.Single(session.Document.Layers);
    }

    [Fact]
    public void Rectangle_script_creates_real_blue_pixels_and_one_undo_step()
    {
        var session = EditorSession.NewCanvas(320, 240, SKColors.White); var before = session.History.Count;
        new JavaScriptRuntime().Execute(session, "app.activeDocument.addRectangle(40,40,160,90,'#87CEEB','Blue Rectangle');");
        Assert.Equal("Blue Rectangle", session.ActiveLayer!.Name);
        Assert.NotNull(session.ActiveLayer.Shape);
        using var output = session.Flatten(); Assert.Equal(new SKColor(135, 206, 235), output.GetPixel(70, 70));
        Assert.Equal(before + 1, session.History.Count);
        session.Undo(); Assert.Single(session.Document.Layers);
        using var restored = session.Flatten(); Assert.Equal(SKColors.White, restored.GetPixel(70, 70));
    }

    [Fact]
    public void Nested_text_and_brush_commands_do_not_commit_inside_a_script_transaction()
    {
        var session = EditorSession.NewCanvas(320, 240, SKColors.White); var before = session.History.Count;
        const string script = """
        const doc = app.activeDocument;
        doc.addLayer('Painting');
        doc.paintStroke([{x:20,y:20},{x:80,y:20}], {color:'#0000FF',size:20});
        doc.addText('Hello',40,120,{size:32,name:'Title'});
        """;
        var runtime = new JavaScriptRuntime(); runtime.Execute(session, script);
        Assert.Equal(before + 1, session.History.Count); Assert.Equal(3, session.Document.Layers.Count);
        session.Undo(); Assert.Single(session.Document.Layers);
        Assert.ThrowsAny<Exception>(() => runtime.Execute(session, script + "throw new Error('stop');"));
        Assert.Single(session.Document.Layers); Assert.Equal(before, session.History.Count);
        using var restored = session.Flatten(); Assert.Equal(SKColors.White, restored.GetPixel(20, 20));
    }

    [Fact]
    public void Scripts_group_duplicate_and_blend_real_layers()
    {
        var session = EditorSession.NewCanvas(320, 240, SKColors.White);
        new JavaScriptRuntime().Execute(session, """
        const doc = app.activeDocument;
        const a = doc.addRectangle(40,40,80,90,'#87CEEB','Card');
        const b = a.duplicate(); b.blendMode = 'Multiply';
        const group = doc.groupLayers([a,b],'Cards');
        """);
        var group = Assert.Single(session.Document.Layers, layer => layer.IsGroup);
        Assert.Equal("Cards", group.Name); Assert.Equal(2, group.Children.Count);
    }

    [Fact]
    public void Untouched_stroke_preserves_the_enclosing_transaction_and_live_transform_updates()
    {
        var session = EditorSession.NewCanvas(320, 240); var before = session.History.Count;
        new JavaScriptRuntime().Execute(session, """
        const doc = app.activeDocument;
        doc.addLayer('Painting');
        doc.paintStroke([{x:500,y:500}], {color:'#0000FF',size:10});
        const shape = doc.addRectangle(40,40,80,90,'#87CEEB','Card');
        if (shape.kind !== 'shape') throw new Error('Wrong kind');
        shape.transform.x += 20;
        """);
        Assert.Equal(3, session.Document.Layers.Count); Assert.Equal(60, session.ActiveLayer!.Transform.X);
        Assert.Equal(before + 1, session.History.Count); session.Undo(); Assert.Single(session.Document.Layers);
    }

    [Theory]
    [InlineData("layer.id = 'other';")]
    [InlineData("layer.kind = 'shape';")]
    [InlineData("layer.pixels = []; ")]
    public void Unsupported_layer_assignments_fail_and_roll_back(string assignment)
    {
        var session = EditorSession.NewCanvas(80,60);
        Assert.ThrowsAny<Exception>(() => new JavaScriptRuntime().Execute(session, "const layer = app.activeDocument.addLayer('Temporary'); " + assignment));
        Assert.Single(session.Document.Layers);
    }

    [Fact]
    public void Plugin_install_disable_and_recoverable_remove_are_real_catalog_operations()
    {
        using var files = new AutomationFiles(); var catalog = new AutomationCatalog(files.Library);
        var package = files.Package(); catalog.Install(package); catalog.Reload();
        var command = Assert.Single(catalog.Commands); Assert.Equal("plugin:sample-tools:blue-card", command.Id);
        var session = EditorSession.NewCanvas(320, 240);
        new JavaScriptRuntime().Execute(session, AutomationCatalog.ReadScript(command.File));
        Assert.Equal("Blue Rectangle", session.ActiveLayer!.Name);
        catalog.Reload(["sample-tools"]); Assert.Empty(catalog.Commands); Assert.Single(catalog.Plugins);
        catalog.Reload(); catalog.RemovePlugin("sample-tools"); catalog.Reload(); Assert.Empty(catalog.Plugins);
        Assert.Single(Directory.GetDirectories(Path.Combine(files.Library, "archive")));
    }

    [Fact]
    public void Bundled_example_plugin_installs_and_each_command_runs_as_one_undo_step()
    {
        using var files = new AutomationFiles(); var catalog = new AutomationCatalog(files.Library);
        catalog.Install(Path.Combine(AppContext.BaseDirectory, "examples", "plugins", "basic-editing", "plugin.json")); catalog.Reload();
        Assert.Empty(catalog.Errors); Assert.Equal(2, catalog.Commands.Count);
        var session = EditorSession.NewCanvas(640,420,SKColors.White); var before = session.History.Count;
        var runtime = new JavaScriptRuntime();
        foreach (var command in catalog.Commands) runtime.Execute(session, AutomationCatalog.ReadScript(command.File));
        Assert.Equal(before + 2, session.History.Count);
        Assert.Equal(2, session.Document.Layers.Count(layer => layer.IsGroup));
        Assert.Equal(20, session.Document.Layers[^1].Children[0].Transform.X - session.Document.Layers[^2].Children[0].Transform.X);
        Assert.Equal(2, session.Document.Layers[^1].Children.Count);
        using var result = session.Flatten(); Assert.NotEqual(SKColors.White, result.GetPixel(350,280));
        session.Undo(); session.Undo(); Assert.Single(session.Document.Layers);
    }

    [Fact]
    public void Invalid_plugin_reports_diagnostics_without_disabling_valid_scripts()
    {
        using var files = new AutomationFiles(); var catalog = new AutomationCatalog(files.Library);
        catalog.SaveScript("Valid", "app.activeDocument.addLayer('OK');");
        var invalid = Path.Combine(files.Library,"plugins","broken"); Directory.CreateDirectory(invalid);
        File.WriteAllText(Path.Combine(invalid,"plugin.json"), "{not valid JSON}");
        catalog.Reload(); Assert.Single(catalog.Errors); Assert.Single(catalog.Commands); Assert.Empty(catalog.Plugins);
    }

    [Fact]
    public void Oversize_native_text_is_rejected_before_bitmap_allocation_and_rolls_back()
    {
        var session = EditorSession.NewCanvas(320,240); var before = session.History.Count;
        Assert.ThrowsAny<Exception>(() => new JavaScriptRuntime().Execute(session,
            "app.activeDocument.addLayer('Temporary'); app.activeDocument.addText(('X'.repeat(500)+'\\n').repeat(50),0,0,{size:2000,fitToCanvas:false});"));
        Assert.Single(session.Document.Layers); Assert.Equal(before, session.History.Count);
    }

    [Fact]
    public void Selection_bounds_and_ellipse_inversion_use_the_normal_editor_mask()
    {
        var session = EditorSession.NewCanvas(320,240);
        new JavaScriptRuntime().Execute(session, """
        const doc = app.activeDocument;
        doc.selectRect(20,30,80,60);
        if (doc.selection.x !== 20 || doc.selection.width !== 80) throw new Error('Selection bounds mismatch');
        doc.selectEllipse(20,30,80,60); doc.invertSelection();
        """);
        Assert.NotNull(session.Selection); session.Undo(); Assert.Null(session.Selection);
    }

    [Theory]
    [InlineData("../outside.js")]
    [InlineData("C:/outside.js")]
    [InlineData("/outside.js")]
    [InlineData("nested\\outside.js")]
    [InlineData("nested/../../outside.js")]
    public void Plugin_paths_cannot_escape_the_package(string script)
    {
        using var files = new AutomationFiles(); var path = files.Package(script);
        Assert.ThrowsAny<Exception>(() => AutomationCatalog.Validate(path));
        Assert.False(Directory.Exists(files.Library));
    }

    [Fact]
    public async Task Plugin_without_export_permission_cannot_write_files_and_rolls_back()
    {
        using var files = new AutomationFiles(); var session = EditorSession.NewCanvas(320, 240);
        var target = Path.Combine(files.Root, "forbidden.png").Replace('\\', '/');
        await Assert.ThrowsAnyAsync<Exception>(() => new JavaScriptRuntime().ExecuteAsync(session,
            "app.activeDocument.addLayer('Temporary'); app.activeDocument.export(" + JsonSerializer.Serialize(target) + ");",
            new NoAi(), new Settings(), cancellationToken: TestContext.Current.CancellationToken, allowExport: false));
        Assert.Single(session.Document.Layers); Assert.False(File.Exists(target));
    }

    [Fact]
    public void Script_library_import_and_save_preserve_the_previous_version()
    {
        using var files = new AutomationFiles(); var package = files.Package(); var source = Path.Combine(Path.GetDirectoryName(package)!, "blue-card.js");
        var catalog = new AutomationCatalog(files.Library); var path = catalog.ImportScript(source);
        var original = File.ReadAllText(path); catalog.SaveScript("ignored", "// revised", path);
        Assert.Equal(original, File.ReadAllText(path + ".bak")); catalog.Reload(); Assert.Single(catalog.Commands);
        Assert.ThrowsAny<Exception>(() => catalog.SaveScript("ignored", "// bad", source));
        catalog.RemoveScript(Assert.Single(catalog.Commands).Id); catalog.Reload(); Assert.Empty(catalog.Commands);
        var recovered = Assert.Single(Directory.GetDirectories(Path.Combine(files.Library, "archive")));
        Assert.Equal("// revised", File.ReadAllText(Path.Combine(recovered, Path.GetFileName(path))));
        Assert.Equal(original, File.ReadAllText(Path.Combine(recovered, Path.GetFileName(path) + ".bak")));
    }

    [AvaloniaFact]
    public void Locked_editor_does_not_accept_destructive_shortcuts_or_close_mid_transaction()
    {
        var window = new MainWindow(); window.AddSession(EditorSession.NewCanvas(80,60)); window.Show();
        var content = Assert.IsAssignableFrom<Control>(window.Content); content.IsEnabled = false;
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null); Assert.Single(window.Session!.Document.Layers);
        window.Close(); Assert.True(window.IsVisible);
        content.IsEnabled = true; window.Close();
    }

    [Fact]
    public async Task Cancellation_of_queued_AI_rolls_back_all_prior_local_edits()
    {
        var session = EditorSession.NewCanvas(80,60); var before = session.History.Count;
        using var cancellation = new CancellationTokenSource(); var ai = new WaitingAi();
        var task = new JavaScriptRuntime().ExecuteAsync(session, "app.activeDocument.addLayer('Temporary'); ai.upscale();",
            ai, new Settings(), cancellationToken: cancellation.Token);
        await ai.Started.Task; cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Single(session.Document.Layers); Assert.Equal(before, session.History.Count); Assert.False(session.IsInteracting);
    }

    [AvaloniaFact]
    public void Installed_plugin_command_appears_in_menu_and_edits_the_document()
    {
        using var files = new AutomationFiles(); var catalog = new AutomationCatalog(files.Library); catalog.Install(files.Package());
        var window = new MainWindow(files.Library); window.AddSession(EditorSession.NewCanvas(320, 240)); window.Show();
        var plugins = Assert.Single(window.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>(), item => item.Header?.ToString() == "_Plugins");
        var command = Assert.Single(plugins.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "Sample Tools · Blue Card");
        command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal("Blue Rectangle", window.Session!.ActiveLayer!.Name);
        window.Session.Undo(); Assert.Single(window.Session.Document.Layers);
        window.Settings.Shortcuts["automation:plugin:sample-tools:blue-card"] = "Ctrl+Alt+B";
        window.Settings.DisabledScriptPlugins.Add("sample-tools"); window.ReloadAutomation();
        Assert.DoesNotContain(plugins.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "Sample Tools · Blue Card");
        window.Settings.DisabledScriptPlugins.Clear(); window.ReloadAutomation();
        command = Assert.Single(plugins.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "Sample Tools · Blue Card");
        Assert.Equal("Ctrl+Alt+B", command.InputGesture!.ToString());
        window.ShowScriptEditor(); var editor = Assert.Single(window.OwnedWindows.OfType<ScriptEditorWindow>());
        Assert.True(Screenshots.Save(editor, "script-editor"));
        var run = Assert.Single(editor.GetVisualDescendants().OfType<Button>(), button => button.Content?.ToString() == "Run");
        run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal("Blue Rectangle", window.Session.ActiveLayer!.Name);
        window.Session.Undo(); Assert.Single(window.Session.Document.Layers);
        var save = Assert.Single(editor.GetVisualDescendants().OfType<Button>(), button => button.Content?.ToString() == "Save to Library");
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Contains(window.Automation.Commands, item => item.PluginId == null);
        editor.Close(); window.Close();
    }

    private sealed class NoAi : Composa.AI.IAiTaskRunner
    {
        public Task RunAsync(Composa.AI.IEditorCommandService editor, Composa.AI.AiTaskRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class WaitingAi : Composa.AI.IAiTaskRunner
    {
        public TaskCompletionSource Started { get; } = new();
        public async Task RunAsync(Composa.AI.IEditorCommandService editor, Composa.AI.AiTaskRequest request, CancellationToken cancellationToken = default)
        { Started.SetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
    }

    private sealed class AutomationFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "composa-automation-" + Guid.NewGuid().ToString("N"));
        public string Library => Path.Combine(Root, "library");
        public string Package(string scriptPath = "blue-card.js")
        {
            var source = Path.Combine(Root, "source"); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "blue-card.js"), "app.activeDocument.addRectangle(40,40,160,90,'#87CEEB','Blue Rectangle');");
            var manifest = new ScriptPluginManifest("sample-tools", "Sample Tools", "1.0.0", 1, [new("blue-card", "Blue Card", scriptPath)]);
            var path = Path.Combine(source, "plugin.json"); File.WriteAllText(path, JsonSerializer.Serialize(manifest)); return path;
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
