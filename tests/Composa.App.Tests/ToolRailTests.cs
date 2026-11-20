using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>The tool rail's groups: a tool with variants opens them beside its button when held or right-clicked.</summary>
public class ToolRailTests
{
    private readonly MainWindow window;
    private readonly EditorSession session;

    public ToolRailTests()
    {
        window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        session = EditorSession.NewCanvas(600, 400, SKColors.White);
        window.AddSession(session);
        Dispatcher.UIThread.RunJobs();
    }

    private Point Center(Control control) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    /// <summary>Where a control in another top level (the flyout's popup) lies in the window's coordinates.</summary>
    private Point InWindow(Control control) => window.PointToClient(control.PointToScreen(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2)));

    private async Task Hold(Point at)
    {
        window.MouseDown(at, MouseButton.Left);
        var until = DateTime.UtcNow + ToolButton.HoldDelay + TimeSpan.FromMilliseconds(250);
        while (DateTime.UtcNow < until) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
    }

    private List<string> BarTexts() => window.GetVisualDescendants().OfType<WrapPanel>().Single(p => p.Classes.Contains("options"))
        .GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();

    [AvaloniaFact]
    public void Selection_brush_shares_magic_wand_button_and_keeps_a_distinct_selection_icon()
    {
        window.SelectTool(Tool.SelectionBrush);
        var lasso=window.RailButton(Tool.Wand);
        Assert.Same(lasso,window.RailButton(Tool.SelectionBrush)); Assert.True(lasso.IsChecked);
        Assert.Equal("Selection Brush",lasso.Current!.Name); Assert.Equal(Icons.SelectionBrush,lasso.Current.Icon);
        Assert.NotEqual(Icons.Brush,lasso.Current.Icon); Assert.True(lasso.Current.Icon.Dashed);
        Assert.StartsWith("Selection Brush (Q)",ToolTip.GetTip(lasso) as string);
        window.MouseDown(Center(lasso),MouseButton.Right); window.MouseUp(Center(lasso),MouseButton.Right); Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Freehand Lasso", "Polygonal Lasso", "Magic Wand", "Object Selection", "Selection Brush"],lasso.GroupItems.Select(item=>item.Header!.ToString()));
        Assert.True(lasso.GroupItems[3].IsEnabled); // Bundled models work without a ComfyUI connection.
        var popup=TopLevel.GetTopLevel(lasso.GroupItems[4])!;
        var selected=lasso.GroupItems[4].TranslatePoint(new Point(40,10),popup)!.Value;
        popup.MouseDown(selected,MouseButton.Left); popup.MouseUp(selected,MouseButton.Left);
        Screenshots.Save(window,"selection-brush-icon-in-wand");
        window.SelectTool(Tool.Move);
        window.MouseDown(Center(lasso),MouseButton.Left); window.MouseUp(Center(lasso),MouseButton.Left);
        Assert.Equal(Tool.SelectionBrush,session.Tool);
    }

    [AvaloniaFact]
    public void Right_clicking_the_lasso_opens_its_group_beside_it_and_a_click_picks_polygonal()
    {
        var lasso = window.RailButton(Tool.Lasso);
        window.MouseDown(Center(lasso), MouseButton.Right);
        window.MouseUp(Center(lasso), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        Assert.True(lasso.IsGroupOpen);
        var items = lasso.GroupItems;
        Assert.Equal(["Freehand Lasso", "Polygonal Lasso", "Magic Wand", "Object Selection", "Selection Brush"], items.Select(i => (string)i.Header!));
        Assert.Equal([new KeyGesture(Key.L), new KeyGesture(Key.L), new KeyGesture(Key.W), new KeyGesture(Key.W), new KeyGesture(Key.Q)], items.Select(i => i.InputGesture));
        Assert.All(items, i => Assert.NotNull(i.Icon));
        Assert.Equal([true, false, false, false, false], items.Select(i => i.IsChecked));
        // Nothing is highlighted until the pointer is over a tool.
        Assert.All(items, i => Assert.False(i.IsSelected));
        // The group opens on the button's right, level with it, as Photoshop's does.
        var button = lasso.PointToScreen(new Point(lasso.Bounds.Width, 0));
        var first = items[0].PointToScreen(new Point(0, 0));
        Assert.True(first.X >= button.X, $"the group starts at {first.X}, left of the button's edge at {button.X}");
        Assert.True(Math.Abs(first.Y - button.Y) < 20, $"the group starts at {first.Y}, the button at {button.Y}");
        Assert.Equal(Tool.Move, session.Tool);
        Screenshots.Save(window, "tool-group-rail");
        Screenshots.Save(TopLevel.GetTopLevel(items[0])!, "tool-group-flyout");

        var popup = TopLevel.GetTopLevel(items[1])!;
        var polygonal = items[1].TranslatePoint(new Point(items[1].Bounds.Width / 2, items[1].Bounds.Height / 2), popup)!.Value;
        popup.MouseDown(polygonal, MouseButton.Left);
        popup.MouseUp(polygonal, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(lasso.IsGroupOpen);
        Assert.Equal(Tool.Lasso, session.Tool);
        Assert.Equal(LassoKind.Polygonal, session.LassoKind);
        Assert.True(lasso.IsChecked);
        Assert.Equal("Polygonal Lasso", lasso.Current!.Name);
        Assert.StartsWith("Polygonal Lasso (L)", ToolTip.GetTip(lasso) as string);
        // The bar names the lasso in use and has no box to switch it.
        Assert.Contains("Polygonal Lasso", BarTexts());
        Assert.Empty(window.GetVisualDescendants().OfType<WrapPanel>().Single(p => p.Classes.Contains("options")).GetVisualDescendants().OfType<ComboBox>());
    }

    [AvaloniaFact]
    public async Task Holding_the_marquee_opens_its_group_and_letting_go_on_ellipse_picks_it()
    {
        var marquee = window.RailButton(Tool.Marquee);
        await Hold(Center(marquee));
        Assert.True(marquee.IsGroupOpen);
        Assert.Equal(Tool.Move, session.Tool);

        var ellipse = marquee.GroupItems[1];
        Assert.Equal("Ellipse Marquee", ellipse.Header);
        window.MouseMove(InWindow(ellipse), RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();
        Assert.True(ellipse.IsSelected);
        Assert.False(marquee.GroupItems[0].IsSelected);
        window.MouseUp(InWindow(ellipse), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(marquee.IsGroupOpen);
        Assert.Equal(Tool.Marquee, session.Tool);
        Assert.Equal(MarqueeKind.Ellipse, session.MarqueeKind);
        Assert.Contains("Ellipse Marquee", BarTexts());
    }

    [AvaloniaFact]
    public async Task Letting_go_of_a_hold_on_the_button_leaves_the_group_open_without_picking()
    {
        var shape = window.RailButton(Tool.Shape);
        await Hold(Center(shape));
        window.MouseUp(Center(shape), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(shape.IsGroupOpen);
        Assert.Equal(Tool.Move, session.Tool);
        Assert.Equal(["Rectangle", "Polygon", "Star", "Ellipse", "Line"], shape.GroupItems.Select(i => (string)i.Header!));
        Assert.All(shape.GroupItems, i => Assert.Equal(new KeyGesture(Key.U), i.InputGesture));
    }

    [AvaloniaFact]
    public async Task A_click_picks_the_tool_the_button_shows_without_opening_its_group()
    {
        session.LassoKind = LassoKind.Polygonal;
        var lasso = window.RailButton(Tool.Lasso);
        window.MouseDown(Center(lasso), MouseButton.Left);
        window.MouseUp(Center(lasso), MouseButton.Left);
        for (var i = 0; i < 50; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }

        Assert.Equal(Tool.Lasso, session.Tool);
        Assert.Equal(LassoKind.Polygonal, session.LassoKind);
        Assert.False(lasso.IsGroupOpen);
        // A tool on its own has no group to open.
        var crop = window.RailButton(Tool.Crop);
        Assert.False(crop.HasGroup);
        window.MouseDown(Center(crop), MouseButton.Right);
        window.MouseUp(Center(crop), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.False(crop.IsGroupOpen);
    }

    [AvaloniaFact]
    public void Brush_and_eraser_have_separate_buttons_and_shortcuts_recall_group_choices()
    {
        var brush = window.RailButton(Tool.Brush);
        var eraser = window.RailButton(Tool.Eraser);
        Assert.False(brush.HasGroup);
        Assert.False(eraser.HasGroup);
        window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
        Assert.Equal(Tool.Eraser, session.Tool);
        Assert.True(eraser.IsChecked);
        Assert.Contains("Eraser", BarTexts());
        window.MouseDown(Center(brush), MouseButton.Left); window.MouseUp(Center(brush), MouseButton.Left);
        Assert.Equal(Tool.Brush, session.Tool);
        Assert.False(session.EraserMode);
        session.SmearMode = SmearMode.Burn;
        window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.None);
        Assert.Contains("Burn", BarTexts());
        window.SelectTool(Tool.Bucket);
        window.SelectTool(Tool.Move);
        window.KeyPressQwerty(PhysicalKey.G, RawInputModifiers.None);
        Assert.Equal(Tool.Bucket, session.Tool);
        window.SelectTool(Tool.SelectionBrush);
        window.SelectTool(Tool.Move);
        window.KeyPressQwerty(PhysicalKey.L, RawInputModifiers.None);
        Assert.Equal(Tool.SelectionBrush, session.Tool);
    }

    [AvaloniaFact]
    public void Stamp_group_recalls_healing_with_S_and_preserves_the_patch_mode()
    {
        var stamp = window.RailButton(Tool.CloneStamp);
        Assert.Same(stamp, window.RailButton(Tool.HealingBrush));
        Assert.Same(stamp, window.RailButton(Tool.SpotHealing));
        void Pick(string name)
        {
            window.MouseDown(Center(stamp), MouseButton.Right);
            window.MouseUp(Center(stamp), MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["Clone Stamp", "Healing Brush", "Spot Healing Brush"], stamp.GroupItems.Select(i => (string)i.Header!));
            var item = stamp.GroupItems.Single(i => (string)i.Header! == name);
            var popup = TopLevel.GetTopLevel(item)!;
            var point = item.TranslatePoint(new Point(40, 10), popup)!.Value;
            popup.MouseDown(point, MouseButton.Left);
            popup.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }
        window.SelectTool(Tool.CloneStamp);
        window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "StampMode").SelectedIndex = 1;
        Assert.Equal(Tool.Patch, session.Tool);
        Assert.True(window.RailButton(Tool.CloneStamp).IsChecked);
        window.SelectTool(Tool.Move);
        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.None);
        Assert.Equal(Tool.Patch, session.Tool);
        Pick("Healing Brush");
        Assert.Equal(Tool.HealingBrush, session.Tool);
        Assert.Equal(Icons.HealingBrush, stamp.Current!.Icon);
        window.SelectTool(Tool.Move);
        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.None);
        Assert.Equal(Tool.HealingBrush, session.Tool);
        window.KeyPressQwerty(PhysicalKey.J, RawInputModifiers.None);
        Assert.Equal(Tool.SpotHealing, session.Tool);
        window.SelectTool(Tool.Move);
        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.None);
        Assert.Equal(Tool.SpotHealing, session.Tool);
        Pick("Clone Stamp");
        Assert.Equal(Tool.Patch, session.Tool);
        window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "StampMode").SelectedIndex = 0;
        Assert.Equal(Tool.CloneStamp, session.Tool);
    }
}
