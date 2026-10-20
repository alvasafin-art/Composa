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
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class ChannelMixerHighPassUiTests
{
    [AvaloniaFact]
    public async Task Mixer_presets_gray_and_RGB_rows_stay_independent_and_commit_one_edit_or_cancel()
    {
        var window = new MainWindow { Width = 1280, Height = 860 }; window.Show(); var session = Scene();
        var original = new ChannelMixerAdjustment(); var layer = session.AddAdjustmentLayer(original); window.AddSession(session); var state = session.History.CurrentId;
        try
        {
            var task = Invoke(window, "EditAdjustmentLayer", layer, false); var dialog = window.OwnedWindows.OfType<DialogWindow>().Single(); Dispatcher.UIThread.RunJobs();
            Combo(dialog, "MixerPreset").SelectedItem = "Swap Red / Blue"; await Tick(); Assert.Equal(new ChannelMix(0, 0, 100), ((ChannelMixerAdjustment)layer.Adjustment!).Red);
            var mono = dialog.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "MixerMonochrome"); mono.IsChecked = true; Dispatcher.UIThread.RunJobs();
            Assert.Equal("Gray", Combo(dialog, "MixerOutput").SelectedItem); Assert.Equal(40, Field(dialog, "Red").Value);
            Number(dialog, "Constant", "10"); await Tick(); Assert.Equal(10, ((ChannelMixerAdjustment)layer.Adjustment!).Gray.Constant); Assert.Equal("Custom", Combo(dialog, "MixerPreset").SelectedItem);
            mono.IsChecked = false; Dispatcher.UIThread.RunJobs(); Combo(dialog, "MixerOutput").SelectedItem = "Blue"; Dispatcher.UIThread.RunJobs();
            Assert.Equal(100, Field(dialog, "Red").Value); Assert.Equal(0, Field(dialog, "Blue").Value); Assert.Equal($"Total: {100:0.0}%", dialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "MixerTotal").Text);
            Assert.Equal(state, session.History.CurrentId); Assert.True(Screenshots.Save(dialog, "channel-mixer-rgb16")); Accept(dialog); await Finish(task); Assert.Equal("Edit Adjustment", session.History.UndoName);
            var accepted = Assert.IsType<ChannelMixerAdjustment>(layer.Adjustment); Assert.False(accepted.Monochrome); Assert.Equal(10, accepted.Gray.Constant); session.Undo(); Assert.True(original.ContentEquals(session.Document.Find(layer.Id)!.Adjustment!));
            layer = session.Document.Find(layer.Id)!; task = Invoke(window, "EditAdjustmentLayer", layer, false); dialog = window.OwnedWindows.OfType<DialogWindow>().Single(); Combo(dialog, "MixerPreset").SelectedItem = "Monochrome red filter"; await Tick();
            dialog.Close(false); await Finish(task); Assert.True(original.ContentEquals(session.Document.Find(layer.Id)!.Adjustment!)); Assert.Equal(state, session.History.CurrentId);
        }
        finally { session.MarkSaved("mixer-ui.cmps", session.History.CurrentId); window.Close(); }
    }

    [AvaloniaFact]
    public async Task High_pass_smart_filter_radius_is_editable_and_cancel_restores_the_committed_stack()
    {
        var window = new MainWindow { Width = 1280, Height = 860 }; window.Show(); var session = Scene(); window.AddSession(session); var layer = session.ActiveLayer!; var source = layer.Pixels!; var bytes = source.Bytes; var state = session.History.CurrentId;
        try
        {
            var task = Invoke(window, "SmartFilter", FilterKind.HighPass, null, null); var dialog = window.OwnedWindows.OfType<DialogWindow>().Single(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, Field(dialog, "Radius").Value); Number(dialog, "Radius", "3.5"); await Tick(); Assert.Equal(3.5, layer.SmartFilters[0].Settings.Radius); Assert.Equal(state, session.History.CurrentId); Assert.Same(source, layer.FilterSource); Assert.Equal(bytes, source.Bytes);
            Accept(dialog); await Finish(task); Assert.Equal("Add Smart Filter", session.History.UndoName); var filter = layer.SmartFilters[0];
            Assert.True(Screenshots.Save(window, "high-pass-rgb16"));
            task = Invoke(window, "SmartFilter", FilterKind.HighPass, layer, filter); dialog = window.OwnedWindows.OfType<DialogWindow>().Single(); Number(dialog, "Radius", "12"); await Tick(); Assert.Equal(12, layer.SmartFilters[0].Settings.Radius);
            dialog.Close(false); await Finish(task); layer = session.ActiveLayer!; Assert.Equal(3.5, layer.SmartFilters[0].Settings.Radius); Assert.Same(source, layer.FilterSource);
            session.Undo(); Assert.Empty(session.ActiveLayer!.SmartFilters); Assert.Same(source, session.ActiveLayer.Pixels); Assert.Equal(state, session.History.CurrentId);
        }
        finally { session.MarkSaved("high-pass-ui.cmps", session.History.CurrentId); window.Close(); }
    }

    private static EditorSession Scene()
    {
        var document = new Document(360, 240) { Depth = ColorDepth.Sixteen, Profile = RgbProfile.DisplayP3 };
        var image = Pixels.NewWorkingColor(360, 240, document.Depth, document.Profile);
        using (var canvas = new SKCanvas(image))
        using (var paint = new SKPaint { Color = SKColors.CornflowerBlue.WithAlpha(150) })
        { canvas.DrawRect(10, 10, 340, 220, paint); paint.Color = SKColors.Gold.WithAlpha(180); for (var x = 35; x < 330; x += 35) canvas.DrawRect(x, 35, 12, 170, paint); }
        document.InsertAboveActive(Layer.Raster("Texture", image)); return new(document);
    }
    private static SliderField Field(DialogWindow dialog, string label) => dialog.GetVisualDescendants().OfType<SliderField>().Single(f => f.Label == label);
    private static ComboBox Combo(DialogWindow dialog, string name) => dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == name);
    private static void Number(DialogWindow dialog, string label, string value)
    { var field = Field(dialog, label); field.BeginEdit(); Dispatcher.UIThread.RunJobs(); field.GetVisualDescendants().OfType<TextBox>().Single().Text = value; dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); Dispatcher.UIThread.RunJobs(); }
    private static Task Invoke(MainWindow window, string name, params object?[] args) => (Task)typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args)!;
    private static void Accept(DialogWindow dialog) => dialog.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Tick() { await Task.Delay(60); Dispatcher.UIThread.RunJobs(); }
    private static async Task Finish(Task task)
    { for (var i = 0; i < 300 && !task.IsCompleted; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); } Assert.True(task.IsCompleted); await task; }
}
