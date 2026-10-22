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
    [Fact]
    public void Removed_native_model_preferences_migrate_without_losing_server_choices()
    {
        var settings = Settings.FromJson("""{"AiDefaultsRevision":1,"ObjectSelectionModel":4,"NativeBiRefNetPath":"old/model.onnx","ObjectSelectionComfyModel":"BiRefNet-HR","ComfyServerUrl":"http://192.168.1.10:8188"}""");
        Assert.Equal(ObjectSelectionSource.EfficientSamS, settings.ObjectSelectionModel);
        Assert.Equal("BiRefNet-HR", settings.ObjectSelectionComfyModel);
        Assert.Equal("http://192.168.1.10:8188", settings.ComfyServerUrl);
    }

    private static async Task Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
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
        Assert.Equal(ObjectSelectionSource.EfficientSamS, window.Settings.ObjectSelectionModel);
        Assert.Equal(0, Menu().SelectedIndex); Assert.True(window.Canvas.ObjectSelectionAvailable!());
        Assert.Equal("SAM Quality · Local", Assert.Single(Menu().Items.Cast<string>()));
        var second = EditorSession.NewCanvas(400, 300); window.AddSession(second);
        Assert.Equal(0, Menu().SelectedIndex); Assert.Equal(ObjectSelectionSource.EfficientSamS, window.Settings.ObjectSelectionModel);
        window.SelectTool(Tool.Wand); second.WandMode = WandMode.Object; window.SelectTool(Tool.Wand);
        Assert.Equal(0, Menu().SelectedIndex);
        Assert.DoesNotContain(Menu().Items.OfType<string>(), name => name.Contains("ComfyUI")); // No invented server model while disconnected.
        window.Settings.ObjectSelectionModel = ObjectSelectionSource.ComfyUI;
        window.SelectTool(Tool.ObjectSelectionAi);
        Assert.Equal(-1, Menu().SelectedIndex);
        Assert.False(window.Canvas.ObjectSelectionAvailable!());
        Menu().SelectedIndex = 0; Assert.True(window.Canvas.ObjectSelectionAvailable!());
        window.SelectTool(Tool.ObjectSelectionAi);
        Screenshots.Save(window, "local-object-selection-model-menu"); window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Local_box_runs_without_a_ComfyUI_connection_and_can_be_undone(bool precise)
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
        window.SelectTool(Tool.ObjectSelectionAi);
        window.GetVisualDescendants().OfType<CheckBox>().Single(c=>c.Name=="ObjectSelectionPreciseEdges").IsChecked=precise;
        var history = session.History.Count;
        await Pump(window.RunObjectSelection(new SKRectI(60, 20, 260, 200), SelectionMode.Replace));
        Assert.True(session.Selection!.GetPixel(160, 100).Alpha > 200);
        Assert.Equal(0, session.Selection.GetPixel(10, 10).Alpha);
        Assert.Equal(history + 1, session.History.Count);
        session.Undo(); Assert.Null(session.Selection);
        window.Close();
    }

    [AvaloniaFact]
    public void Precise_edges_is_remembered_across_tools_tabs_and_settings_and_is_disabled_for_server_models()
    {
        var window=new MainWindow {Width=1440,Height=900};window.Settings.CheckForUpdates=false;window.Show();
        try
        {
            window.AddSession(EditorSession.NewCanvas(320,240));window.SelectTool(Tool.ObjectSelectionAi);
            CheckBox Precise()=>window.GetVisualDescendants().OfType<CheckBox>().Single(c=>c.Name=="ObjectSelectionPreciseEdges");
            Assert.False(Precise().IsChecked);Assert.True(Precise().IsEnabled);Precise().IsChecked=true;
            window.SelectTool(Tool.Brush);window.AddSession(EditorSession.NewCanvas(400,300));
            window.Session!.WandMode=WandMode.Object;window.SelectTool(Tool.Wand);
            Assert.True(Precise().IsChecked);Assert.True(Precise().IsEnabled);
            Assert.True(Settings.FromJson(System.Text.Json.JsonSerializer.Serialize(window.Settings)).ObjectSelectionPreciseEdges);
            Assert.True(Screenshots.Save(window,"sam-precise-edges-options"));
            window.Settings.ObjectSelectionModel=ObjectSelectionSource.ComfyUI;window.SelectTool(Tool.ObjectSelectionAi);
            Assert.True(Precise().IsChecked);Assert.False(Precise().IsEnabled);
        }
        finally {window.Close();}
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

    public static bool HasPrecisePhoto=>Environment.GetEnvironmentVariable("COMPOSA_PRECISE_SELECTION_PHOTO") is { } path && File.Exists(path);

    [AvaloniaFact(SkipUnless=nameof(HasPrecisePhoto),Skip="Set COMPOSA_PRECISE_SELECTION_PHOTO for a photographic native-model check")]
    public async Task Photograph_compares_fast_and_precise_local_selection_and_saves_the_real_cutouts()
    {
        using var image=Composa.IO.ImageFiles.Load(Environment.GetEnvironmentVariable("COMPOSA_PRECISE_SELECTION_PHOTO")!);
        var output=Environment.GetEnvironmentVariable("COMPOSA_PRECISE_SELECTION_OUTPUT")!;Directory.CreateDirectory(output);
        var session=EditorSession.NewCanvas(image.Width,image.Height,SKColors.White,Composa.Model.ColorDepth.Sixteen);
        session.AddImageLayer("Photograph",Pixels.Clone(image),new(image.Width/2f,image.Height/2f),fit:false);
        var source=session.ActiveLayer!.Pixels!;var bytes=source.Bytes;var state=session.History.CurrentId;
        var window=new MainWindow {Width=1440,Height=1000};window.Settings.CheckForUpdates=false;window.Show();window.AddSession(session);window.SelectTool(Tool.ObjectSelectionAi);
        try
        {
            foreach(var precise in new[] {false,true})
            {
                window.GetVisualDescendants().OfType<CheckBox>().Single(c=>c.Name=="ObjectSelectionPreciseEdges").IsChecked=precise;
                await Pump(window.RunObjectSelection(null,SelectionMode.Replace,new(image.Width/2,image.Height/2)));
                Assert.NotNull(session.Selection);Assert.True(session.Selection.GetPixel(image.Width/2,image.Height/2).Alpha>200);
                Assert.Equal(0,session.Selection.GetPixel(5,5).Alpha);Assert.Equal(bytes,source.Bytes);
                using var cutout=Composa.Selections.MaskRefinement.Cutout(source,session.Selection,0);
                using var preview=Pixels.DisplayCopy(cutout);
                Composa.IO.ImageFiles.Save(preview,Path.Combine(output,precise?"precise-cutout.png":"fast-cutout.png"),Composa.IO.ExportFormat.Png);
                using var backdrop=Pixels.NewColor(image.Width,image.Height);backdrop.Erase(SKColors.White);
                using(var draw=new SKCanvas(backdrop))draw.DrawImage(Pixels.ImageOf(preview),0,0);
                Pixels.Invalidate(backdrop);Composa.IO.ImageFiles.Save(backdrop,Path.Combine(output,precise?"precise-on-white.png":"fast-on-white.png"),Composa.IO.ExportFormat.Png);
                session.Undo();Assert.Equal(state,session.History.CurrentId);Assert.Null(session.Selection);
            }
        }
        finally {session.MarkSaved("photo.cmps",session.History.CurrentId);window.Close();}
    }
}
