using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public class BlendingUiTests
{
    [AvaloniaFact]
    public void Mask_properties_from_the_layer_menu_preview_and_cancel_without_replacing_the_source()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var session = EditorSession.NewCanvas(320, 200, SKColors.Red);
        window.AddSession(session);
        session.AddMask(session.ActiveLayer!, hideAll: true);
        var source = session.ActiveLayer!.Mask;
        Dispatcher.UIThread.RunJobs();
        var panel = window.GetVisualDescendants().OfType<LayersPanel>().Single();
        var row = panel.GetVisualDescendants().OfType<Border>().Single(b => b.Tag is Layer);
        row.ContextMenu!.Items.OfType<MenuItem>().Single(m => m.Header as string == "Mask Properties…")
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var dialog = window.OwnedWindows.Single();
        var density = dialog.GetVisualDescendants().OfType<SliderField>().Single(f => f.Label == "Density");
        density.Focus();
        dialog.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        var feather = dialog.GetVisualDescendants().OfType<SliderField>().Single(f => f.Label == "Feather");
        feather.Focus();
        dialog.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Assert.Equal(.99, session.ActiveLayer.MaskDensity, 4);
        Assert.Equal(.1, session.ActiveLayer.MaskFeather, 4);
        Assert.Same(source, session.ActiveLayer.Mask);
        Screenshots.Save(dialog, "layer-mask-properties");
        dialog.Close(false);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, session.ActiveLayer!.MaskDensity);
        Assert.Equal(0, session.ActiveLayer.MaskFeather);
        Assert.Same(source, session.ActiveLayer.Mask);
        Assert.False(session.IsInteracting);
        Assert.Equal("Add Layer Mask", session.History.UndoName);
        session.Undo();
        window.Close();
    }

    [AvaloniaFact]
    public void Blending_from_the_layer_menu_previews_and_cancel_restores_the_document()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var session = EditorSession.NewCanvas(320, 200, SKColors.Red);
        window.AddSession(session);
        Dispatcher.UIThread.RunJobs();
        var panel = window.GetVisualDescendants().OfType<LayersPanel>().Single();
        var row = panel.GetVisualDescendants().OfType<Border>().Single(b => b.Tag is Layer);
        var command = row.ContextMenu!.Items.OfType<MenuItem>().Single(m => m.Header as string == "Blending Options…");
        command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var dialog = window.OwnedWindows.Single();
        Assert.Equal("Blending Options", dialog.Title);
        var fill = dialog.GetVisualDescendants().OfType<SliderField>().Single(f => f.Label == "Fill");
        fill.Focus();
        dialog.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        dialog.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Assert.Equal(.98, session.ActiveLayer!.FillOpacity, 4);
        var black = dialog.GetVisualDescendants().OfType<SliderField>().First(f => f.Label == "Black");
        black.Focus();
        dialog.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Assert.Equal(1, session.ActiveLayer.BlendIf.Gray.ThisLayer.Black);
        Assert.Equal(1, session.ActiveLayer.BlendIf.Gray.ThisLayer.BlackEnd);
        Screenshots.Save(dialog, "blending-options-gray");
        var channels = dialog.GetVisualDescendants().OfType<ComboBox>().Single();
        channels.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, dialog.GetVisualDescendants().OfType<SliderField>().First(f => f.Label == "Black").Value);
        Assert.True(session.ActiveLayer.BlendIf.Red.IsIdentity);
        dialog.Close(false);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, session.ActiveLayer!.FillOpacity);
        Assert.True(session.ActiveLayer.BlendIf.IsIdentity);
        Assert.False(session.IsInteracting);
        Assert.False(session.IsModified);
        window.Close();
    }
}
