using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.AI;
using Composa.App.AI;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Rendering;
using SkiaSharp;
using SelectionMode = Composa.Selections.SelectionMode;

namespace Composa.App.Tests;

public class AiInteractionTests
{
    [AvaloniaFact]
    public async Task Previewing_generation_area_restores_canvas_and_preserves_selection()
    {
        var window = new MainWindow();
        window.Settings.CheckForUpdates = false;
        window.Settings.AiShowContextBounds = false;
        window.Show();
        try
        {
            var editor = EditorSession.NewCanvas(400, 300, SKColors.White);
            window.AddSession(editor);
            editor.SelectRect(new SKRect(90, 80, 120, 130));
            var selection = editor.Selection;
            var history = editor.History.CurrentId;
            var area = AutomaticAiMask.Geometry(new(90, 80, 120, 130), editor.Document.Bounds).Bounds;
            var preview = window.PreviewAiContext(area);
            Assert.Equal(area, window.Canvas.AiContextBounds);
            await preview;
            Assert.Null(window.Canvas.AiContextBounds);
            Assert.Same(selection, editor.Selection);
            Assert.Equal(history, editor.History.CurrentId);
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData(SelectionMode.Replace, false, true)]
    [InlineData(SelectionMode.Add, true, true)]
    [InlineData(SelectionMode.Subtract, true, false)]
    public void Rectangle_is_a_search_region_not_a_temporary_selection_and_result_combines_once(SelectionMode mode, bool oldPart, bool newPart)
    {
        var s = EditorSession.NewCanvas(160, 100, SKColors.White); s.SelectRect(new SKRect(10, 10, 70, 70));
        var previous = s.Selection!; var history = s.History.Count;
        using var inputs = AiTaskInputPreparer.Prepare(s, new() { Task = AiTaskKind.ObjectSelection, SelectionRegion = new(50, 20, 90, 60), SelectionOperation = mode });
        Assert.Same(previous, s.Selection); Assert.Equal(history, s.History.Count);
        Assert.Equal((40, 40), (inputs.SourceImage.Width, inputs.SourceImage.Height));
        var answer = Pixels.NewColor(40, 40); answer.Erase(SKColors.White);
        AiTaskService.Insert(new EditorCommandService(s), AiTaskKind.ObjectSelection, AiOutputMode.Selection, [answer], inputs.TargetBounds, inputs);
        Assert.Equal(oldPart ? (byte)255 : (byte)0, s.Selection!.GetPixel(20, 30).Alpha);
        Assert.Equal(newPart ? (byte)255 : (byte)0, s.Selection.GetPixel(60, 30).Alpha);
        Assert.Equal(mode == SelectionMode.Subtract ? (byte)0 : (byte)255, s.Selection.GetPixel(80, 30).Alpha);
        Assert.Equal(history + 1, s.History.Count); s.Undo(); Assert.Same(previous, s.Selection); s.Redo();
        Assert.Equal(newPart ? (byte)255 : (byte)0, s.Selection!.GetPixel(60, 30).Alpha);
    }

    [AvaloniaFact]
    public void Ai_rectangle_captures_shift_add_alt_subtract_without_squaring_or_changing_old_mask()
    {
        var s = EditorSession.NewCanvas(600, 400); s.SelectRect(new SKRect(10, 10, 40, 40)); s.Tool = Tool.ObjectSelectionAi;
        var previous = s.Selection; var history = s.History.Count;
        var canvas = new CanvasView { Session = s, AiToolsAvailable = () => true };
        var w = new Window { Width = 900, Height = 650, Content = canvas }; w.Show(); canvas.Fit(); Dispatcher.UIThread.RunJobs();
        var requests = new List<(SKRectI Region, SelectionMode Mode)>(); canvas.AiObjectSelectionRequested += (r,m) => requests.Add((r,m));
        Point At(float x, float y) => canvas.TranslatePoint(canvas.ToScreen(new SKPoint(x,y)),w)!.Value;
        foreach (var modifiers in new[] { RawInputModifiers.None, RawInputModifiers.Shift, RawInputModifiers.Alt, RawInputModifiers.Shift | RawInputModifiers.Alt })
        {
            var count = requests.Count;
            w.MouseDown(At(100,100), MouseButton.Left, modifiers);
            w.MouseMove(At(160,140), RawInputModifiers.LeftMouseButton | modifiers);
            Assert.Equal(count, requests.Count); Assert.Same(previous, s.Selection);
            w.MouseUp(At(160,140), MouseButton.Left, RawInputModifiers.None); // release modifier before mouse-up
            Assert.Equal(new SKRectI(100,100,160,140), requests[^1].Region);
        }
        Assert.Equal(new[] { SelectionMode.Replace, SelectionMode.Add, SelectionMode.Subtract, SelectionMode.Subtract }, requests.Select(r=>r.Mode));
        // Fast reverse drag: the OS delivers the final position only on button-up.
        w.MouseDown(At(250, 220), MouseButton.Left);
        w.MouseMove(At(210, 190), RawInputModifiers.LeftMouseButton);
        w.MouseUp(At(110, 70), MouseButton.Left);
        Assert.Equal(new SKRectI(110, 70, 250, 220), requests[^1].Region);
        Assert.Equal(history,s.History.Count); Assert.Same(previous,s.Selection); w.Close();
    }

    [Fact]
    public async Task Tasks_route_independently_and_selection_expand_keeps_its_own_pack_after_normalization()
    {
        var connection = new PartnerImageTests.Connection(); var service = PartnerImageTests.Service(connection);
        var local = service.Engines.Profiles.First(p=>!p.PaidApi); var gpt = service.Engines.Find(service.SelectedEngine!.Id)!;
        service.SelectedEngine = local;
        var settings = new Settings { AiTaskEngineIds = new() { [nameof(AiTaskKind.GenerateImage)] = gpt.Id, [nameof(AiTaskKind.GenerativeExpand)] = gpt.Id } };
        service.EngineIdForTask = settings.EngineForTask;
        Assert.Same(gpt,service.EngineFor(AiTaskKind.GenerateImage)); Assert.Same(local,service.EngineFor(AiTaskKind.GenerativeFill));
        Assert.Same(local,service.EngineFor(AiTaskKind.RemoveObject)); Assert.Same(gpt,service.EngineFor(AiTaskKind.GenerativeFill,gpt.Id));
        var s = EditorSession.NewCanvas(150,100,SKColors.White); s.SelectRect(new SKRect(40,30,80,70));
        await service.RunAsync(new EditorCommandService(s),new(){Task=AiTaskKind.GenerativeExpand},TestContext.Current.CancellationToken);
        Assert.NotNull(connection.Graphs[0]["gpt"]); Assert.Same(local,service.SelectedEngine);
        Assert.Contains(AiPromptDefaults.Expand,connection.Graphs[0]["gpt"]!["inputs"]!["prompt"]!.GetValue<string>());
        settings.AiTaskEngineIds[nameof(AiTaskKind.GenerateImage)] = "missing-pack";
        Assert.Null(service.EngineFor(AiTaskKind.GenerateImage));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.RunAsync(new EditorCommandService(s),new(){Task=AiTaskKind.GenerateImage},TestContext.Current.CancellationToken));
        Assert.Single(connection.Graphs);
    }

    [AvaloniaFact]
    public async Task Generation_dialogs_have_shared_references_preview_remove_and_live_counts()
    {
        var w = new MainWindow { Width=1280,Height=900 }; w.Settings.CheckForUpdates=false; w.Show();
        w.AiTasks.SetConnectedForTests(PartnerImageTests.Capabilities()); w.AiTasks.SelectedEngine=w.AiTasks.Engines.Find("chatgpt-image-2.5");
        w.AddSession(EditorSession.NewCanvas(600,400,SKColors.White));
        foreach (var task in new[]{AiTaskKind.GenerateImage,AiTaskKind.ImageEdit})
        {
            var pending=w.RunAiForTests(task); Dispatcher.UIThread.RunJobs(); var dialog=Assert.Single(w.OwnedWindows);
            var alignment = dialog.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(button => button.Name == "AiPixelAlignment");
            Assert.Equal(task == AiTaskKind.ImageEdit, alignment.IsVisible);
            var host=dialog.GetVisualDescendants().OfType<StackPanel>().Single(p=>p.Name=="AiDialogReferences");
            Assert.Equal("Reference images (0/6)", Avalonia.Automation.AutomationProperties.GetName(host));
            for(var i=0;i<6;i++){var bitmap=Pixels.NewColor(16,16);bitmap.Erase(SKColors.Blue);w.AddAiReferenceForTests(bitmap);}
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Reference images (6/6)", Avalonia.Automation.AutomationProperties.GetName(host));
            Assert.Equal(6,host.GetVisualDescendants().OfType<Image>().Count());
            Assert.DoesNotContain(host.GetVisualDescendants().OfType<Button>(),b=>ToolTip.GetTip(b)?.ToString()?.StartsWith("Add reference")==true);
            var preview=host.GetVisualDescendants().OfType<Button>().First(b=>ToolTip.GetTip(b)?.ToString()?.StartsWith("Preview reference")==true);
            preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); var child=Assert.Single(dialog.OwnedWindows); child.Close();
            Assert.True(Screenshots.Save(dialog,"ai-"+task+"-references"));
            GenerationDockTests.CheckReferenceSpacing(dialog, dialog);
            while (host.GetVisualDescendants().OfType<Button>().FirstOrDefault(b =>
                Avalonia.Automation.AutomationProperties.GetName(b)?.StartsWith("Remove reference:") == true) is { } remove)
            {
                remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
            }
            Assert.Equal(0,w.AiReferenceCount); dialog.Close(false); await pending;
        }
        w.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Task_assignment_dialog_saves_only_on_accept_and_pickers_filter_unsupported_packs(bool accept)
    {
        var w=new MainWindow{Width=1280,Height=900};w.Settings.CheckForUpdates=false;w.Show();
        var settings=new Settings();var pending=AiDialogs.SettingsDialog(w,settings,w.AiTasks);Dispatcher.UIThread.RunJobs();
        var dialog=Assert.Single(w.OwnedWindows);
        dialog.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex=1;Dispatcher.UIThread.RunJobs();
        dialog.GetVisualDescendants().OfType<Expander>().Single(e => e.Header as string == "Defaults by operation").IsExpanded = true; Dispatcher.UIThread.RunJobs();
        var pickers=dialog.GetVisualDescendants().OfType<ComboBox>().Where(c=>c.Items.Cast<object>().Contains("Use default model")).ToArray();
        Assert.Equal(Enum.GetValues<AiTaskKind>().Count(t => t is not (AiTaskKind.ObjectSelection or AiTaskKind.SelectSubject or AiTaskKind.MatchToScene)), pickers.Length); Assert.Contains(pickers,c=>!c.Items.Cast<object>().Contains("CHAT GPT 2.5"));
        var generation=pickers[0];generation.SelectedItem=generation.Items.OfType<ImageModelChoice>().Single(m => m.Engine.Id == "flux2-klein-intel-xpu");
        Assert.True(Screenshots.Save(dialog,"ai-workflow-by-task"));
        dialog.Close(accept);Assert.Equal(accept,await pending);
        if(accept) { Assert.Equal("flux2-klein-intel-xpu",settings.EngineForTask(AiTaskKind.GenerateImage));Assert.Single(settings.AiTaskEngineIds); }
        else Assert.Empty(settings.AiTaskEngineIds);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Image_edit_dialog_pastes_reference_without_pasting_a_layer()
    {
        var previous = EditorSession.Clipboard;
        using var copied = Pixels.NewColor(24, 16); copied.Erase(SKColors.Blue);
        var w = new MainWindow { Width = 1280, Height = 900 }; w.Settings.CheckForUpdates = false; w.Show();
        try
        {
            EditorSession.Clipboard = new ClipboardImage(copied, default);
            w.AiTasks.SetConnectedForTests(); var s = EditorSession.NewCanvas(600, 400); w.AddSession(s);
            var history = s.History.CurrentId;
            var pending = w.RunAiForTests(AiTaskKind.ImageEdit); Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(w.OwnedWindows);
            dialog.KeyPressQwerty(PhysicalKey.V, RawInputModifiers.Control); Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, w.AiReferenceCount); Assert.Single(s.Document.Layers); Assert.Equal(history, s.History.CurrentId);
            dialog.Close(false); await pending;
        }
        finally { EditorSession.Clipboard = previous; w.Close(); }
    }

    [AvaloniaFact]
    public async Task Making_a_default_model_updates_unassigned_operations_and_preserves_explicit_overrides()
    {
        var owner = new MainWindow();
        owner.Show();
        try
        {
            var settings = new Settings();
            settings.AiTaskEngineIds[nameof(AiTaskKind.GenerateImage)] = "chatgpt-image-2.5";
            var pending = AiDialogs.SettingsDialog(owner, settings, owner.AiTasks, page: 1);
            var dialog = Assert.Single(owner.OwnedWindows);
            Dispatcher.UIThread.RunJobs();
            var qwen = owner.AiTasks.Engines.Find("qwen-image-2.1-turbo")!;
            var picker = dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.IsEffectivelyVisible && c.Items.OfType<ImageModelChoice>().Any(m => m.Engine.Id == qwen.Id));
            picker.SelectedItem = picker.Items.OfType<ImageModelChoice>().Single(m => m.Engine.Id == qwen.Id);
            dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Make default"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog.Close(true);
            Assert.True(await pending);
            Assert.Equal(qwen.Id, settings.AiEngineId);
            Assert.Null(settings.EngineForTask(AiTaskKind.GenerativeFill));
            Assert.Equal("chatgpt-image-2.5", settings.EngineForTask(AiTaskKind.GenerateImage));
        }
        finally { owner.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(AiTaskKind.GenerateImage)]
    [InlineData(AiTaskKind.ImageEdit)]
    [InlineData(AiTaskKind.GenerativeExpand)]
    public async Task Dialog_model_choice_applies_to_the_request_and_preserves_configured_defaults(AiTaskKind task)
    {
        var w = new MainWindow { Width = 1280, Height = 900 }; w.Settings.CheckForUpdates = false; w.Show();
        var settings = new Settings(); var original = w.AiTasks.SelectedEngine;
        var configured = settings.EngineForTask(task);
        var pending = AiDialogs.Prompt(w, task, settings, 600, 400, service: w.AiTasks); Dispatcher.UIThread.RunJobs();
        var dialog = Assert.Single(w.OwnedWindows);
        var picker = Assert.Single(dialog.GetVisualDescendants().OfType<ComboBox>(),
            combo => combo.Items.OfType<ImageModelChoice>().Any());
        var gpt = w.AiTasks.Engines.Find("chatgpt-image-2.5")!;
        picker.SelectedItem = picker.Items.OfType<ImageModelChoice>().Single(m => m.Engine.Id == gpt.Id); dialog.Close(true); var result = await pending;
        Assert.Equal(gpt.Id, result!.EngineId); Assert.Equal(configured, settings.EngineForTask(task));
        Assert.Empty(settings.AiTaskEngineIds); Assert.Same(original, w.AiTasks.SelectedEngine); w.Close();
    }

    [Fact]
    public void Empty_search_region_is_rejected_without_changing_selection_or_history()
    {
        var s = EditorSession.NewCanvas(160, 100); s.SelectRect(new SKRect(10, 10, 70, 70));
        var previous = s.Selection; var history = s.History.CurrentId;
        Assert.Throws<InvalidOperationException>(() => AiTaskInputPreparer.Prepare(s,
            new() { Task = AiTaskKind.ObjectSelection, SelectionRegion = new(200, 200, 300, 300), SelectionOperation = SelectionMode.Add }));
        Assert.Same(previous, s.Selection); Assert.Equal(history, s.History.CurrentId);
    }
}
