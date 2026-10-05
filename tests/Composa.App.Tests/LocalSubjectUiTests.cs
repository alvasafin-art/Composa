using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Rendering;
using Composa.Vision;
using SkiaSharp;
using SelectionMode = Composa.Selections.SelectionMode;

namespace Composa.App.Tests;

public class LocalSubjectUiTests
{
    private static async Task Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "Selection did not finish");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        await task;
    }

    [AvaloniaFact]
    public void Model_menu_defaults_to_local_and_follows_tools_and_document_tabs()
    {
        var window = new MainWindow(); window.Settings.CheckForUpdates = false; window.Show();
        var first = EditorSession.NewCanvas(320, 240); window.AddSession(first); window.SelectTool(Tool.ObjectSelectionAi);
        ComboBox Menu() => window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ObjectSelectionModel");
        Assert.Equal(ObjectSelectionSource.AnySubject, window.Settings.ObjectSelectionModel);
        Assert.Equal(0, Menu().SelectedIndex); Assert.True(window.Canvas.ObjectSelectionAvailable!());
        Menu().SelectedIndex = 1;
        Assert.Equal(ObjectSelectionSource.Person, window.Settings.ObjectSelectionModel); Assert.Equal(SubjectDetect.Person, first.Detect);
        var second = EditorSession.NewCanvas(400, 300); window.AddSession(second);
        Assert.Equal(1, Menu().SelectedIndex); Assert.Equal(SubjectDetect.Person, second.Detect);
        window.SelectTool(Tool.Wand); second.WandMode = WandMode.Object; window.SelectTool(Tool.Wand);
        Assert.Equal(1, Menu().SelectedIndex);
        Assert.DoesNotContain(Menu().Items.OfType<string>(), name => name.Contains("ComfyUI")); // No invented server model while disconnected.
        window.Settings.ObjectSelectionModel = ObjectSelectionSource.ComfyUI;
        window.SelectTool(Tool.ObjectSelectionAi);
        Assert.Equal(-1, Menu().SelectedIndex);
        Assert.False(window.Canvas.ObjectSelectionAvailable!());
        Menu().SelectedIndex = 0; Assert.True(window.Canvas.ObjectSelectionAvailable!());
        window.SelectTool(Tool.ObjectSelectionAi);
        Screenshots.Save(window, "local-object-selection-model-menu"); window.Close();
    }

    [AvaloniaFact]
    public async Task Local_box_runs_without_a_ComfyUI_connection_and_can_be_undone()
    {
        var window = new MainWindow(); window.Settings.CheckForUpdates = false; window.Show();
        var session = EditorSession.NewCanvas(320, 240);
        var scene = Pixels.NewColor(320, 240); var random = new Random(3);
        for (var y = 0; y < 240; y++) for (var x = 0; x < 320; x++)
        {
            var n = (byte)random.Next(24);
            scene.SetPixel(x, y, (x - 160) * (x - 160) + (y - 100) * (y - 100) < 3600
                ? new SKColor(225, 40, 30) : new SKColor((byte)(35 + n), (byte)(40 + n), (byte)(45 + n)));
        }
        session.AddImageLayer("photo", scene, new SKPoint(160, 120)); window.AddSession(session);
        window.SelectTool(Tool.ObjectSelectionAi); var history = session.History.Count;
        await Pump(window.RunObjectSelection(new SKRectI(60, 20, 260, 200), SelectionMode.Replace));
        Assert.True(session.Selection!.GetPixel(160, 100).Alpha > 200);
        Assert.Equal(0, session.Selection.GetPixel(10, 10).Alpha);
        Assert.Equal(history + 1, session.History.Count);
        session.Undo(); Assert.Null(session.Selection);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Closing_progress_cancels_without_committing_a_selection()
    {
        var window = new MainWindow(); window.Settings.CheckForUpdates = false; window.Show();
        var session = EditorSession.NewCanvas(320, 240); window.AddSession(session);
        var delay = ProgressWindow.Delay; ProgressWindow.Delay = TimeSpan.Zero;
        try
        {
            var task = ProgressWindow.Run(window, "Selecting object…", async cancellation =>
            {
                await Task.Delay(2000, cancellation);
                session.SelectAll(); return true;
            });
            Dispatcher.UIThread.RunJobs();
            Assert.Single(window.OwnedWindows).Close();
            await Pump(task); Assert.False(await task); Assert.Null(session.Selection); Assert.False(session.CanUndo);
        }
        finally { ProgressWindow.Delay = delay; window.Close(); }
    }
}
