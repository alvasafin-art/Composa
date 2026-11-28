using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.Editing;
using SkiaSharp;

namespace Composa.App.Tests;

public class GenerationMenuPointerTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ClickCurrentOperation(bool tabbed, bool arrow)
    {
        static void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var session = EditorSession.NewCanvas(400, 300, SKColors.Gray);
        try
        {
            window.AddSession(session);
            window.AiTasks.ApiKey = () => "test-only";
            window.AiTasks.SetConnectedForTests(PartnerImageTests.Capabilities());
            session.SelectRect(new SKRect(40, 40, 160, 160));
            var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
            var section = dock.Section("AI Generation");
            dock.SetVisible(section, true);
            dock.SetCollapsed(section, false);
            dock.SetTabbed(tabbed);
            Pump();
            if (tabbed)
            {
                dock.Activate(section);
                Pump();
            }
            var chooser = section.Content.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AiDockOperation");
            Assert.True(chooser.IsEffectivelyEnabled);
            var text = chooser.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Gen Fill");
            var menu = Assert.IsType<MenuFlyout>(chooser.Flyout);
            var content = Assert.IsAssignableFrom<Panel>(chooser.Content);
            Control target = arrow ? content.Children.Last() : text;
            var at = target.TranslatePoint(new Rect(target.Bounds.Size).Center, window)!.Value;
            Assert.True(chooser.IsEffectivelyVisible);
            var hit = window.InputHitTest(at);
            Assert.True(hit is Visual v && v.GetSelfAndVisualAncestors().Contains(chooser), $"Hit {hit} at {at} instead of {chooser}");
            window.MouseMove(at);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Pump();
            Assert.True(menu.IsOpen, $"The flyout stayed closed after clicking {target} at {at}");
            var edit = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Edit Image"));
            Assert.True(edit.IsEffectivelyEnabled);
            for (var frame = 0; frame < 30 && edit.Bounds.Height == 0; frame++)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
                Pump();
            }
            Assert.True(edit.IsEffectivelyVisible && edit.Bounds.Height > 0 && edit.Bounds.Width > 0,
                $"Edit Image has no visible row: {edit.Bounds}");
            var overlay = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(edit));
            var editPoint = edit.TranslatePoint(new Rect(edit.Bounds.Size).Center, overlay)!.Value;
            var rowHit = overlay.InputHitTest(editPoint);
            Assert.True(rowHit is Visual row && row.GetSelfAndVisualAncestors().Contains(edit), $"Row hit {rowHit}");
            overlay.MouseMove(editPoint);
            overlay.MouseDown(editPoint, MouseButton.Left);
            overlay.MouseUp(editPoint, MouseButton.Left);
            Pump();
            Assert.Equal("Edit Image", text.Text);
            Assert.False(menu.IsOpen);
        }
        finally
        {
            session.MarkSaved("preview.cmps", session.History.CurrentId);
            window.Close();
        }
    }
}
