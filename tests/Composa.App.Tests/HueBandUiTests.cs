using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.ColorManagement;
using Composa.Editing;
using Composa.Filters;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public class HueBandUiTests
{
    [AvaloniaFact]
    public async Task Circular_handles_numeric_fields_reset_and_cancelled_drag_remain_one_adjustment_edit()
    {
        var window = new MainWindow { Width = 1280, Height = 860 }; window.Show();
        var session = EditorSession.NewCanvas(400, 300, SKColors.Red); session.ChangeColorDepth(ColorDepth.Sixteen); session.AssignColorProfile(RgbProfile.DisplayP3);
        var initial = new HueSaturationAdjustment().WithShift(HueRange.Reds, new(50, 0, 0)); var layer = session.AddAdjustmentLayer(initial); window.AddSession(session); var state = session.History.CurrentId;
        try
        {
            var task = Edit(window, layer); var dialog = window.OwnedWindows.OfType<DialogWindow>().Single(); Dispatcher.UIThread.RunJobs();
            Range(dialog).SelectedItem = "Reds"; Dispatcher.UIThread.RunJobs(); var editor = Band(dialog);
            Drag(dialog, editor, 345, 330); Assert.InRange(editor.Band.FadeIn, 14.99, 15.01); Assert.InRange(editor.Band.Width, 44.99, 45.01);
            Drag(dialog, editor, 180, 210); Assert.InRange(editor.Band.Start, 344.99, 345.01);
            editor.Focus(); dialog.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); dialog.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
            Assert.InRange(editor.Band.Start, 345.89, 345.91);
            var beforeDrag = editor.Band; dialog.MouseDown(At(dialog, editor, beforeDrag.Start), MouseButton.Left);
            dialog.MouseMove(At(dialog, editor, beforeDrag.Start - 10), RawInputModifiers.LeftMouseButton); Assert.NotEqual(beforeDrag, editor.Band);
            dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); dialog.MouseUp(At(dialog, editor, beforeDrag.Start - 10), MouseButton.Left); Assert.Equal(beforeDrag, editor.Band); Assert.Contains(dialog, window.OwnedWindows);
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Reset hue range").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(HueBand.Default(HueRange.Reds), editor.Band);
            var numeric = dialog.GetVisualDescendants().OfType<SliderField>().Single(f => f.Label == "Fade in"); numeric.BeginEdit(); Dispatcher.UIThread.RunJobs();
            numeric.GetVisualDescendants().OfType<TextBox>().Single().Text = "21.5"; dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); await Tick();
            Assert.Equal(21.5, editor.Band.FadeIn); Assert.Equal(21.5, ((HueSaturationAdjustment)layer.Adjustment!).BandFor(HueRange.Reds).FadeIn); Assert.Equal(state, session.History.CurrentId);
            Assert.True(Screenshots.Save(dialog, "hsl-custom-band-rgb16")); Accept(dialog); await Finish(task); Assert.Equal("Edit Adjustment", session.History.UndoName);
            session.Undo(); Assert.True(initial.ContentEquals(session.Document.Find(layer.Id)!.Adjustment!)); Assert.Equal(state, session.History.CurrentId);
        }
        finally { session.MarkSaved("hsl-ui.cmps", session.History.CurrentId); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Colorize_returns_to_master_and_cancelling_restores_all_custom_ranges()
    {
        var window = new MainWindow { Width = 1280, Height = 860 }; window.Show(); var session = EditorSession.NewCanvas(300, 200, SKColors.Cyan);
        var initial = new HueSaturationAdjustment().WithShift(HueRange.Reds, new(20, 0, 0)).WithBand(HueRange.Reds, new(160, 10, 20, 10)); var layer = session.AddAdjustmentLayer(initial); window.AddSession(session); var state = session.History.CurrentId;
        try
        {
            var task = Edit(window, layer); var dialog = window.OwnedWindows.OfType<DialogWindow>().Single(); Range(dialog).SelectedItem = "Reds"; Dispatcher.UIThread.RunJobs();
            Assert.Equal(initial.BandFor(HueRange.Reds), Band(dialog).Band);
            var colorize = dialog.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Colorize"); colorize.IsChecked = true; Dispatcher.UIThread.RunJobs(); await Tick();
            Assert.Equal("Master", Range(dialog).SelectedItem); Assert.False(Range(dialog).IsEnabled); Assert.Empty(dialog.GetVisualDescendants().OfType<HueBandEditor>()); Assert.True(((HueSaturationAdjustment)layer.Adjustment!).Colorize);
            dialog.Close(false); await Finish(task); Assert.Equal(state, session.History.CurrentId); Assert.True(initial.ContentEquals(session.Document.Find(layer.Id)!.Adjustment!));
        }
        finally { session.MarkSaved("hsl-ui.cmps", session.History.CurrentId); window.Close(); }
    }

    private static ComboBox Range(DialogWindow dialog) => dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Items.Cast<object>().Contains("Reds"));
    private static HueBandEditor Band(DialogWindow dialog) => dialog.GetVisualDescendants().OfType<HueBandEditor>().Single();
    private static Point At(DialogWindow dialog, HueBandEditor editor, double hue) => editor.TranslatePoint(new(6 + hue / 360 * (editor.Bounds.Width - 12), 20), dialog)!.Value;
    private static void Drag(DialogWindow dialog, HueBandEditor editor, double from, double to)
    { dialog.MouseDown(At(dialog, editor, from), MouseButton.Left); dialog.MouseMove(At(dialog, editor, to), RawInputModifiers.LeftMouseButton); dialog.MouseUp(At(dialog, editor, to), MouseButton.Left); Dispatcher.UIThread.RunJobs(); }
    private static Task Edit(MainWindow window, Layer layer) => (Task)typeof(MainWindow).GetMethod("EditAdjustmentLayer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [layer, false])!;
    private static void Accept(DialogWindow dialog) => dialog.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Tick() { await Task.Delay(40); Dispatcher.UIThread.RunJobs(); }
    private static async Task Finish(Task task)
    { for (var i = 0; i < 300 && !task.IsCompleted; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); } Assert.True(task.IsCompleted); await task; }
}
