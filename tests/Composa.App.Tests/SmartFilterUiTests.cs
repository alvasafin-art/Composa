using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.Editing;
using Composa.Filters;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public class SmartFilterUiTests
{
    [AvaloniaFact]
    public void Blending_previews_cancel_accept_and_filter_reordering_are_undoable()
    {
        var window = new MainWindow { Width = 1280, Height = 800 }; window.Show();
        try
        {
            var session = EditorSession.NewCanvas(240, 160, new SKColor(130, 100, 190)); window.AddSession(session); var layer = session.ActiveLayer!;
            session.AddSmartFilter(layer, new() { Kind = FilterKind.AddNoise, Amount = 30, Seed = 1 });
            var noise = layer.SmartFilters.Single(); session.AddSmartFilter(layer, new() { Kind = FilterKind.GaussianBlur, Radius = 2, ClampEdges = true });
            Dispatcher.UIThread.RunJobs();
            Button BlendingButton() => window.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b)?.ToString()?.StartsWith("Smart filter blending:") == true);
            void Open() { BlendingButton().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
            Open(); var dialog = window.OwnedWindows.Single(); var combo = dialog.GetVisualDescendants().OfType<ComboBox>().Single();
            combo.SelectedIndex = BlendModeExtensions.Groups.SelectMany(g => g).Where(b => b != BlendMode.PassThrough).ToList().IndexOf(BlendMode.Luminosity);
            Dispatcher.UIThread.RunJobs(); Assert.Equal(BlendMode.Luminosity, layer.SmartFilters[^1].Blend);
            dialog.Close(false); Dispatcher.UIThread.RunJobs(); layer = session.ActiveLayer!; Assert.Equal(BlendMode.Normal, layer.SmartFilters[^1].Blend);
            Open(); dialog = window.OwnedWindows.Single(); var field = dialog.GetVisualDescendants().OfType<SliderField>().Single();
            var at = field.TranslatePoint(new Point(field.Bounds.Width * .9, field.Bounds.Height / 2), dialog)!.Value;
            var to = at - new Vector(field.Bounds.Width * .65, 0);
            dialog.MouseDown(at, MouseButton.Left); dialog.MouseMove(to, RawInputModifiers.LeftMouseButton); dialog.MouseUp(to, MouseButton.Left);
            Dispatcher.UIThread.RunJobs(); Assert.Equal(.35, layer.SmartFilters[^1].Opacity);
            Assert.True(Screenshots.Save(dialog, "smart-filter-blending")); dialog.Close(true); Dispatcher.UIThread.RunJobs();
            Assert.False(session.HasPendingEdit); Assert.Equal("Smart Filter Blending", session.History.UndoName);
            var row = BlendingButton().GetVisualAncestors().OfType<Border>().First(b => b.ContextMenu != null);
            row.ContextMenu!.Items.OfType<MenuItem>().Single(i => i.Header as string == "Move Filter Down").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs(); Assert.Equal(noise.Id, layer.SmartFilters[^1].Id);
            Assert.True(Screenshots.Save(window, "smart-filter-order"));
            session.Undo(); layer = session.ActiveLayer!; Assert.Equal(noise.Id, layer.SmartFilters[0].Id); Assert.Equal(.35, layer.SmartFilters[^1].Opacity);
            session.Undo(); Assert.Equal(1, session.ActiveLayer!.SmartFilters[^1].Opacity);
        }
        finally { window.Close(); }
    }
}
