using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.AI;
using Composa.App.Controls;
using Composa.AI;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public class GenerationDockTests
{
    [AvaloniaTheory]
    [InlineData(AiTaskKind.GenerateImage, 1)]
    [InlineData(AiTaskKind.ImageEdit, 2)]
    [InlineData(AiTaskKind.GenerativeFill, 3)]
    public async Task Dock_run_submits_the_prompt_and_correct_source_inputs(AiTaskKind task, int count)
    {
        var server = new PartnerImageTests.Connection();
        var window = new MainWindow(null, _ => server) { Width = 1100, Height = 800 };
        window.Settings.ComfyServerUrl = server.Address.ToString();
        window.AiTasks.ApiKey = () => "test-only";
        window.Settings.AiTaskEngineIds[task.ToString()] = "flux2-klein-intel-xpu";
        window.Settings.AiTaskModelKeys.Remove(task.ToString());
        window.Show();
        var session = EditorSession.NewCanvas(160, 120, task == AiTaskKind.GenerateImage ? null : SKColors.White);
        window.AddSession(session);
        if (task == AiTaskKind.GenerativeFill) session.SelectRect(new(20, 20, 70, 65));
        try
        {
            var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
            var section = dock.Section("AI Generation"); dock.SetVisible(section, true);
            var reference = new SKBitmap(60, 40); reference.Erase(SKColors.Coral); window.AddAiReferenceForTests(reference); Pump();
            var prompt = section.Content.GetVisualDescendants().OfType<TextBox>().Single(b => b.AcceptsReturn);
            var picker = section.Content.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible);
            picker.SelectedItem = picker.Items.OfType<ImageModelChoice>().Single(model => model.Engine.Id == "chatgpt-image-2.5");
            prompt.Text = "Keep the ceramic texture."; Pump();
            var before = session.History.CurrentId;
            var run = section.Content.GetVisualDescendants().OfType<Button>().Single(b => b.Name == (count == 1 ? "AiRun" : $"AiVariants{count}"));
            Assert.True(run.IsEffectivelyEnabled);
            run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var timeout = DateTime.UtcNow.AddSeconds(15);
            while (window.AiTasks.Operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running && DateTime.UtcNow < timeout)
            { await Task.Delay(10, TestContext.Current.CancellationToken); Pump(); }
            Assert.True(window.AiTasks.Operation?.Status == AiOperationStatus.Completed, window.AiTasks.Operation?.Error);
            Assert.Equal(count, server.Graphs.Count);
            Assert.Equal("chatgpt-image-2.5", Assert.IsType<ImageModelChoice>(picker.SelectedItem).Engine.Id);
            var graph = server.Graphs[0];
            Assert.Contains("Keep the ceramic texture.", graph["gpt"]!["inputs"]!["prompt"]!.GetValue<string>());
            Assert.Equal(task == AiTaskKind.GenerateImage ? new[] { "referenceImage1" } : new[] { "apiSource", "referenceImage1" }, server.Uploads);
            Assert.NotEqual(before, session.History.CurrentId);
            session.Undo(); Assert.Equal(before, session.History.CurrentId);
        }
        finally { session.MarkSaved("run.cmps", session.History.CurrentId); window.Close(); }
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static string Operation(Control view) =>
        AutomationProperties.GetName(view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AiDockOperation"))!;

    internal static void CheckReferenceSpacing(Control host, Window window)
    {
        var model = host.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible);
        var thumbnails = host.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible &&
            (ToolTip.GetTip(b)?.ToString()?.StartsWith("Preview reference:") == true ||
             Equals(ToolTip.GetTip(b), "Add another reference image"))).ToArray();
        var first = thumbnails.First().TranslatePoint(default, window)!.Value;
        var modelBottom = model.TranslatePoint(new Point(0, model.Bounds.Height), window)!.Value;
        Assert.Equal(8, first.Y - modelBottom.Y, 2);
        for (int i = 1; i < thumbnails.Length; i++)
        {
            var previous = thumbnails[i - 1];
            var from = previous.TranslatePoint(default, window)!.Value;
            var to = thumbnails[i].TranslatePoint(default, window)!.Value;
            if (from.Y == to.Y) Assert.Equal(8, to.X - from.X - previous.Bounds.Width, 2);
            else
            {
                Assert.Equal(first.X, to.X, 2);
                Assert.Equal(8, to.Y - from.Y - previous.Bounds.Height, 2);
            }
        }
    }

    [AvaloniaFact]
    public void Dock_follows_document_content_selection_history_and_tab_switches()
    {
        var window = new MainWindow { Width = 1120, Height = 760 }; window.Show();
        var session = EditorSession.NewCanvas(480, 320, null, ColorDepth.Sixteen);
        window.AddSession(session);
        var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
        var section = dock.Section("AI Generation");
        Assert.False(section.State.Visible);
        dock.SetVisible(section, true);
        void Is(string name) { Pump(); Assert.Equal("Choose AI operation · " + name, Operation(section.Content)); }
        try
        {
            Is("Generate Image");
            var chooser = section.Content.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AiDockOperation");
            var menu = Assert.IsType<MenuFlyout>(chooser.Flyout);
            menu.ShowAt(chooser); Pump();
            Assert.False(menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Gen Fill")).IsEnabled);
            menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Edit Image")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.Hide(); Is("Edit Image");
            session.SelectRect(new(5, 5, 40, 40)); Is("Gen Fill");
            session.Deselect(); Is("Generate Image");
            session.Fill(SKColors.Orange); Is("Edit Image");
            session.Undo(); Is("Generate Image");
            session.Redo(); Is("Edit Image");
            session.Undo();
            session.AddShape(new(10, 10, 50, 80)); Is("Edit Image");
            session.AddBlankLayer(); Is("Edit Image");
            session.SelectRect(new(3, 4, 150, 80)); Is("Gen Fill");
            session.Deselect(); Is("Edit Image");
            window.AddSession(EditorSession.NewCanvas(64, 64)); Is("Generate Image");
        }
        finally { session.MarkSaved("dock.cmps", session.History.CurrentId); window.Close(); }
    }

    [AvaloniaFact]
    public void Floating_and_docked_editors_keep_the_same_prompt_and_ordered_reference_controls()
    {
        var window = new MainWindow { Width = 1120, Height = 760 }; window.Show();
        var session = EditorSession.NewCanvas(600, 400, SKColors.LightSlateGray);
        window.AddSession(session); window.AiTasks.SetConnectedForTests();
        session.SelectRect(new(130, 85, 270, 260)); Pump();
        var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
        var section = dock.Section("AI Generation");
        TextBox Prompt(Control host) => host.GetVisualDescendants().OfType<TextBox>().Single(b => b.AcceptsReturn);
        Image[] Thumbnails(Control host) => host.GetVisualDescendants().OfType<Button>()
            .Where(b => ToolTip.GetTip(b)?.ToString()?.StartsWith("Preview reference:") == true).Select(b => (Image)b.Content!).ToArray();
        void CheckBadge(Control host)
        {
            var remove = host.GetVisualDescendants().OfType<Button>().First(b => Equals(ToolTip.GetTip(b), "Remove reference"));
            var cell = Assert.IsType<Grid>(remove.Parent);
            var image = Assert.IsType<Image>(cell.Children.OfType<Button>().First().Content);
            var tile = image.TranslatePoint(default, window)!.Value;
            var badge = remove.TranslatePoint(default, window)!.Value;
            Assert.Equal(3, badge.Y - tile.Y, 2);
            Assert.Equal(3, tile.X + image.Bounds.Width - badge.X - remove.Bounds.Width, 2);
            var glyph = Assert.IsAssignableFrom<Control>(remove.Content);
            var center = glyph.TranslatePoint(new Rect(glyph.Bounds.Size).Center, remove)!.Value;
            Assert.Equal(remove.Bounds.Width / 2, center.X, 2);
            Assert.Equal(remove.Bounds.Height / 2, center.Y, 2);
            Assert.Equal(new CornerRadius(5), remove.CornerRadius);
            Assert.Equal(6, Assert.IsType<Avalonia.Media.RectangleGeometry>(image.Clip).RadiusX);
        }
        void Add(SKColor color) { var image = new SKBitmap(20, 15); image.Erase(color); window.AddAiReferenceForTests(image); }
        try
        {
            var prompt = Prompt(window.AiFloatingPanel);
            prompt.Text = "Add a small ceramic vase beside the plant.";
            Add(SKColors.Green); Add(SKColors.Coral);
            var initial = Thumbnails(window.AiFloatingPanel);
            Assert.Equal(2, initial.Length);
            dock.SetVisible(section, true); Pump();
            Assert.False(window.AiFloatingVisible); Assert.Same(prompt, Prompt(section.Content));
            Assert.Equal(initial, Thumbnails(section.Content));
            CheckBadge(section.Content);
            CheckReferenceSpacing(section.Content, window);
            Assert.True(Screenshots.Save(window, "generation-dock-references"));
            prompt.Text = "Keep the green reference.";
            section.Content.GetVisualDescendants().OfType<Button>().Last(b => Equals(ToolTip.GetTip(b), "Remove reference"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var remaining = Thumbnails(section.Content);
            Assert.Same(initial[0].Source, Assert.Single(remaining).Source);
            dock.SetCollapsed(section, true); Pump();
            Assert.True(window.AiFloatingVisible); Assert.Same(prompt, Prompt(window.AiFloatingPanel));
            CheckBadge(window.AiFloatingPanel);
            CheckReferenceSpacing(window.AiFloatingPanel, window);
            Assert.True(Screenshots.Save(window, "generation-floating-references"));
            Assert.Equal("Keep the green reference.", prompt.Text); Assert.Equal(remaining, Thumbnails(window.AiFloatingPanel));
            dock.SetCollapsed(section, false); Pump();
            dock.SetTabbed(true); Pump();
            var tabs = dock.GetVisualDescendants().OfType<TabControl>().Single();
            var entries = tabs.Items.OfType<TabItem>().ToList();
            tabs.SelectedIndex = entries.FindIndex(t => Equals(t.Header, "History")); Pump();
            Assert.True(window.AiFloatingVisible);
            tabs.SelectedIndex = entries.FindIndex(t => Equals(t.Header, "AI Generation")); Pump();
            Assert.False(window.AiFloatingVisible); Assert.Same(prompt, Prompt(section.Content)); Assert.Equal(remaining, Thumbnails(section.Content));
            for (int i = 0; i < 5; i++) Add(SKColors.Blue);
            Pump(); CheckReferenceSpacing(section.Content, window);
            dock.SetVisible(section, false); Pump();
            Assert.True(window.AiFloatingVisible); Assert.Equal("Keep the green reference.", Prompt(window.AiFloatingPanel).Text);
        }
        finally { session.MarkSaved("handoff.cmps", session.History.CurrentId); window.Close(); }
    }
}
