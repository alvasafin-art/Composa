using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.AI;
using Composa.App.Automation;
using Composa.App.Assistant;
using Composa.App.Mcp;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Text;
using SkiaSharp;

namespace Composa.App.Tests;

public class AutomationCapabilityTests
{
    [Fact]
    public void Real_guides_are_metadata_not_layers_and_undo_as_one_script()
    {
        var session = EditorSession.NewCanvas(600, 400, SKColors.White); var history = session.History.Count;
        new JavaScriptRuntime().Execute(session, """
            const doc = app.activeDocument;
            doc.addGuide('vertical', 50); doc.addGuide('vertical', doc.width - 50);
            doc.addGuide('horizontal', 50); doc.addGuide('horizontal', doc.height - 50);
            doc.addGuide('vertical', 50); // idempotent, no fifth guide
            if(doc.guides.length !== 4) throw new Error('Missing native guides');
            doc.moveGuide(doc.guides[0].id, 60);
            """);
        Assert.Single(session.Document.Layers); Assert.Equal(4, session.Guides.Count);
        Assert.Equal(60, session.Guides[0].Position); Assert.True(session.View.ShowRulers);
        using var bitmap = session.Flatten(); Assert.Equal(SKColors.White, bitmap.GetPixel(60, 100));
        Assert.Contains("guides", JavaScriptRuntime.Describe(session));
        Assert.Equal(history + 1, session.History.Count); session.Undo(); Assert.Empty(session.Guides);
    }

    [AvaloniaFact]
    public async Task Native_guides_lock_move_remove_and_agent_context_are_real()
    {
        var window = new MainWindow(); var session = EditorSession.NewCanvas(600,400); window.AddSession(session); window.Show();
        var native = new ComposaTools(window);
        await native.Guides("add", "horizontal", 100);
        var id = Assert.Single(session.Guides).Id.ToString();
        await native.Guides("move", position: 120, id: id); Assert.Equal(120, session.Guides[0].Position);
        await native.Guides("lock"); await Assert.ThrowsAnyAsync<Exception>(() => native.Guides("remove", id: id));
        await native.Guides("unlock"); await native.Guides("remove", id: id); Assert.Empty(session.Guides);
        var tools = new AssistantEditorTools(window,session,new JavaScriptRuntime(),new NoAi(),new Settings(),[],false);
        Assert.Contains(tools.Definitions, tool => tool.Name == "guides");
        Assert.Contains(tools.Definitions, tool => tool.Name == "measure_text");
        window.Close();
    }

    [Fact]
    public void Long_text_creation_and_subsequent_growth_fit_actual_canvas_without_truncation()
    {
        var session = EditorSession.NewCanvas(320,200);
        var text = string.Join(" ",Enumerable.Repeat("Длинный редактируемый текст",12));
        new JavaScriptRuntime().Execute(session, "const title=app.activeDocument.addText('Hi',0,0,{size:72}); title.text=" + JsonSerializer.Serialize(text) + ";");
        var layer = session.ActiveLayer!;
        Assert.Equal(text,layer.Text!.Text); Assert.NotNull(layer.Text.BoxWidth);
        Assert.False(new TextLayout(layer.Text).Overflows);
        Assert.InRange(layer.Bounds.Left,0,320); Assert.InRange(layer.Bounds.Top,0,200);
        Assert.InRange(layer.Bounds.Right,0,320); Assert.InRange(layer.Bounds.Bottom,0,200);
        Assert.Single(session.Document.Layers, l => l.Text != null);
    }

    [Fact]
    public void Text_outside_origin_is_rejected_but_explicit_overflow_is_supported()
    {
        var session=EditorSession.NewCanvas(320,200);
        Assert.ThrowsAny<Exception>(()=>new JavaScriptRuntime().Execute(session,"app.activeDocument.addText('Hello',400,0);"));
        Assert.Single(session.Document.Layers);
        new JavaScriptRuntime().Execute(session,"app.activeDocument.addText('Hello',400,0,{fitToCanvas:false});");
        Assert.True(session.ActiveLayer!.Bounds.Left>320);
    }

    [AvaloniaFact]
    public async Task Script_input_shows_native_window_and_resumes_edits_on_UI_with_one_undo()
    {
        var window=new MainWindow(); var session=EditorSession.NewCanvas(600,400); window.AddSession(session); window.Show();
        var runtime=new JavaScriptRuntime(); var history=session.History.Count;
        var task=runtime.ExecuteAsync(session,"""
            const doc=app.activeDocument;
            const values=await ui.form({title:'Guide margins',fields:[{name:'margin',label:'Margin',type:'number',value:50,min:0,max:200}]});
            doc.addGuide('vertical',values.margin); doc.addGuide('vertical',doc.width-values.margin);
            console.log('Guides created');
            """,new NoAi(),new Settings(),dialogs:new ScriptDialogs(window));
        Dispatcher.UIThread.RunJobs();
        var dialog=Assert.Single(window.OwnedWindows.OfType<DialogWindow>());
        Assert.Empty(session.Guides); Assert.False(task.IsCompleted);
        Assert.True(Screenshots.Save(dialog,"script-native-input"));
        Assert.Single(dialog.GetVisualDescendants().OfType<Button>(),b=>b.Content?.ToString()=="OK").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var result=await task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("Guides created\n",result.Output); Assert.Equal(2,session.Guides.Count);
        Assert.Equal(history+1,session.History.Count); session.Undo(); Assert.Empty(session.Guides); window.Close();
    }

    [AvaloniaFact]
    public async Task Cancelled_input_rolls_back_edits_and_unawaited_input_is_not_silent()
    {
        var window=new MainWindow(); var session=EditorSession.NewCanvas(600,400); window.AddSession(session); window.Show();
        var runtime=new JavaScriptRuntime();
        var task=runtime.ExecuteAsync(session,"app.activeDocument.addGuide('vertical',40); await prompt('Title','Hello');",new NoAi(),new Settings(),dialogs:new ScriptDialogs(window));
        Dispatcher.UIThread.RunJobs(); Assert.Single(window.OwnedWindows.OfType<DialogWindow>()).Close(false);
        await Assert.ThrowsAnyAsync<Exception>(()=>task); Assert.Empty(session.Guides); Assert.False(session.IsInteracting);
        await Assert.ThrowsAnyAsync<Exception>(()=>runtime.ExecuteAsync(session,"prompt('Forgot await'); app.activeDocument.addGuide('vertical',60);",new NoAi(),new Settings(),dialogs:new ScriptDialogs(window)));
        Dispatcher.UIThread.RunJobs(); Assert.Empty(session.Guides); Assert.Empty(window.OwnedWindows.OfType<DialogWindow>()); window.Close();
    }

    [AvaloniaFact]
    public async Task Awaited_form_values_drive_real_geometry_and_waiting_does_not_consume_CPU_limit()
    {
        var session=EditorSession.NewCanvas(600,400);
        var task=new JavaScriptRuntime().ExecuteAsync(session,"const v=await ui.form({title:'Input',fields:[{name:'width',label:'Width',type:'number',value:50}]}); app.activeDocument.addRectangle(20,20,v.width,50);",
            new NoAi(),new Settings(),dialogs:new DelayedDialog());
        await task; Assert.Equal(80,session.ActiveLayer!.Transform.Width);
    }

    private sealed class DelayedDialog:IScriptDialogs
    {
        public async Task<IReadOnlyDictionary<string,object?>> ShowAsync(ScriptForm form,CancellationToken token)
        { await Task.Delay(8500,token); return new Dictionary<string,object?> { ["width"]=80 }; }
    }
    private sealed class NoAi:IAiTaskRunner
    { public Task RunAsync(IEditorCommandService editor,AiTaskRequest request,CancellationToken cancellationToken=default)=>Task.CompletedTask; }
}
