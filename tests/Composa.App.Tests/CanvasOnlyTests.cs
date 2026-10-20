using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public class CanvasOnlyTests
{
    [AvaloniaFact]
    public void Canvas_only_expands_the_view_restores_panel_states_and_keeps_pixels_zoom_history_and_export()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show(); var session = EditorSession.NewCanvas(600, 400, SKColors.CornflowerBlue, ColorDepth.Sixteen); window.AddSession(session); Pump();
        try
        {
            var dock = window.GetVisualDescendants().OfType<SideDock>().Single(); dock.SetVisible(dock.Section("Navigator"), true); dock.SetCollapsed(dock.Section("History"), true); Pump();
            window.Canvas.ZoomTo(1.5); var size = window.Canvas.Bounds.Size; var state = session.History.CurrentId; var bytes = session.ActiveLayer!.Pixels!.Bytes; using var before = session.Flatten();
            window.Canvas.Focus(); window.KeyPressQwerty(PhysicalKey.F6, RawInputModifiers.None); Pump(); Assert.True(window.Settings.CanvasOnly); Assert.False(dock.IsEffectivelyVisible); Assert.True(window.GetVisualDescendants().OfType<Menu>().Single().IsEffectivelyVisible);
            Assert.True(window.Canvas.Bounds.Width > size.Width + 250); Assert.True(window.Canvas.Bounds.Height > size.Height + 50); Assert.Equal(1.5, window.Canvas.Zoom); Assert.True(Screenshots.Save(window, "canvas-only-rgb16"));
            window.KeyPressQwerty(PhysicalKey.F6, RawInputModifiers.None); Pump(); Assert.False(window.Settings.CanvasOnly); Assert.True(dock.IsEffectivelyVisible); Assert.True(dock.Section("Navigator").State.Visible); Assert.True(dock.Section("History").State.Collapsed); Assert.Equal(size, window.Canvas.Bounds.Size);
            using var after = session.Flatten(); Assert.Equal(before.Bytes, after.Bytes); Assert.Equal(bytes, session.ActiveLayer.Pixels.Bytes); Assert.Equal(state, session.History.CurrentId); Assert.Equal(1.5, window.Canvas.Zoom);
        }
        finally { session.MarkSaved("canvas-only.cmps", session.History.CurrentId); window.Close(); }
    }

    [AvaloniaFact]
    public void Canvas_only_can_be_rebound_and_its_preference_round_trips_with_a_safe_old_settings_default()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show();
        try
        {
            window.Settings.Shortcuts["Canvas Only"] = "F8"; typeof(MainWindow).GetMethod("ApplyShortcutOverrides", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            window.KeyPressQwerty(PhysicalKey.F6, RawInputModifiers.None); Pump(); Assert.False(window.Settings.CanvasOnly);
            window.KeyPressQwerty(PhysicalKey.F8, RawInputModifiers.None); Pump(); Assert.True(window.Settings.CanvasOnly);
            var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(window.Settings))!; Assert.True(restored.CanvasOnly); Assert.Equal("F8", restored.Shortcuts["Canvas Only"]); Assert.False(JsonSerializer.Deserialize<Settings>("{}")!.CanvasOnly);
            window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header as string == "Canvas Only").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(); Assert.False(window.Settings.CanvasOnly); Assert.Null(window.Session);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Canvas_only_can_be_left_through_command_search_and_keeps_an_open_text_edit()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show(); var session = EditorSession.NewCanvas(400, 250); window.AddSession(session); window.SelectTool(Tool.Text); var edit = session.BeginText(new SKPoint(50, 50)); edit.Insert("Live draft"); var state = session.History.CurrentId;
        try
        {
            window.Canvas.Focus(); window.KeyPressQwerty(PhysicalKey.F6, RawInputModifiers.None); Pump(); Assert.True(window.Settings.CanvasOnly); Assert.Same(edit, session.TextEdit);
            window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Shift); Pump(); var palette = window.OwnedWindows.OfType<CommandSearchWindow>().Single(); palette.GetVisualDescendants().OfType<TextBox>().Single().Text = "Canvas Only"; Pump(); palette.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            for (var i = 0; i < 200 && window.Settings.CanvasOnly; i++) { await Task.Delay(10); Pump(); }
            Assert.False(window.Settings.CanvasOnly); Assert.Same(edit, session.TextEdit); Assert.Equal("Live draft", session.ActiveLayer!.Text!.Text); Assert.Equal(state, session.History.CurrentId);
        }
        finally { foreach (var owned in window.OwnedWindows.ToArray()) owned.Close(null); session.FinishText(); session.MarkSaved("canvas-only.cmps", session.History.CurrentId); window.Close(); }
    }
    private static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
}
