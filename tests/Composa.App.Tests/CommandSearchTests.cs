using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public class CommandSearchTests
{
    [AvaloniaFact]
    public async Task Browsing_and_view_commands_preserve_an_open_channel_transform_and_its_original_for_cancel()
    {
        var window = new MainWindow(); window.Show(); var session = EditorSession.NewCanvas(160, 100, SKColors.White); window.AddSession(session); session.AddAlphaChannel("Mask"); session.SelectRect(new(20, 20, 50, 50)); session.Fill(SKColors.White); session.Deselect();
        var original = session.Document.AlphaChannels["Mask"]; session.PreviewChannelControlTransform(session.ChannelControlTransform with { X = 30 }); var transform = session.Transform; var state = session.History.CurrentId;
        try
        {
            Assert.NotNull(transform); var palette = Open(window); Query(palette).Text = "Canvas Only"; Pump(); palette.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); await Until(() => window.Settings.CanvasOnly);
            Assert.Same(transform, session.Transform); Assert.Equal(state, session.History.CurrentId); window.KeyPressQwerty(PhysicalKey.F6, RawInputModifiers.None); window.KeyPressQwerty(PhysicalKey.Digit1, RawInputModifiers.Control); Pump(); Assert.Same(transform, session.Transform); Assert.Equal(state, session.History.CurrentId);
            session.CancelTransform(); Assert.Same(original, session.Document.AlphaChannels["Mask"]); Assert.Equal(state, session.History.CurrentId);
        }
        finally { CloseOwned(window); session.CancelTransform(); session.MarkSaved("search.cmps", session.History.CurrentId); window.Close(); }
    }
    [AvaloniaFact]
    public async Task Keyboard_search_distinguishes_live_layers_shows_menu_paths_and_runs_the_real_edit_with_one_undo()
    {
        var window = new MainWindow(); window.Show(); var session = EditorSession.NewCanvas(160, 100, SKColors.CornflowerBlue); window.AddSession(session); var original = session.ActiveLayer!.Pixels!; var state = session.History.CurrentId;
        try
        {
            var dialog = Open(window); Query(dialog).Text = "  new   color lookup  layer "; Pump(); var row = Assert.Single(Rows(dialog)); Assert.Equal("New Color Lookup Layer", ((Shortcut)row.Tag!).Id);
            Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Layer") == true);
            Query(dialog).Text = "invert"; Pump(); Assert.Equal("Invert", ((Shortcut)Rows(dialog)[0].Tag!).Id); Assert.Contains(Rows(dialog)[0].GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Image") == true);
            var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.I }; Query(dialog).RaiseEvent(key); Assert.False(key.Handled);
            Assert.True(Screenshots.Save(dialog, "find-command-invert")); dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); await Until(() => session.History.CurrentId != state);
            Assert.Equal("Invert", session.History.UndoName); Assert.Equal(new SKColor(155, 106, 18), session.ActiveLayer!.Pixels!.GetPixel(30, 30)); Assert.Equal(SKColors.CornflowerBlue, original.GetPixel(30, 30)); session.Undo(); Assert.Same(original, session.ActiveLayer.Pixels); Assert.Equal(state, session.History.CurrentId);
        }
        finally { CloseOwned(window); session.MarkSaved("search.cmps", session.History.CurrentId); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Disabled_commands_and_empty_results_stay_open_without_running_and_hidden_aliases_are_not_duplicated()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            var dialog = Open(window); Query(dialog).Text = "Invert"; Pump(); dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); Pump(); Assert.True(dialog.IsVisible); Assert.Null(window.Session); Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Unavailable in the current editor state");
            Query(dialog).Text = "this command does not exist"; Pump(); Assert.Empty(Rows(dialog)); dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); Assert.True(dialog.IsVisible);
            Query(dialog).Text = "Zoom In"; Pump(); Assert.Single(Rows(dialog)); Query(dialog).Text = "Find Command"; Pump(); Assert.Empty(Rows(dialog));
            Query(dialog).Text = "New Canvas"; Pump(); dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); await Until(() => window.OwnedWindows.Any(w => w is DialogWindow));
            Assert.Equal("New Canvas", window.OwnedWindows.OfType<DialogWindow>().Single().Title); window.OwnedWindows.OfType<DialogWindow>().Single().Close(false); Assert.Null(window.Session);
        }
        finally { CloseOwned(window); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Search_reuses_rebound_tool_keys_and_arrow_navigation_and_chooses_the_registered_tool()
    {
        var window = new MainWindow(); window.Show(); var session = EditorSession.NewCanvas(160, 100); window.AddSession(session);
        try
        {
            window.Settings.Shortcuts["Brush tool"] = "F7"; window.Settings.Shortcuts["Find Command"] = "Ctrl+Alt+K"; typeof(MainWindow).GetMethod("ApplyShortcutOverrides", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.Control | RawInputModifiers.Alt); Pump(); var dialog = window.OwnedWindows.OfType<CommandSearchWindow>().Single();
            Query(dialog).Text = "tool"; Pump(); var results = dialog.GetVisualDescendants().OfType<ListBox>().Single(); var first = results.SelectedIndex; dialog.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None); Assert.Equal(first + 1, results.SelectedIndex); dialog.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None); Assert.Equal(first, results.SelectedIndex);
            Query(dialog).Text = "Brush tool"; Pump(); Assert.Equal("Brush tool", ((Shortcut)Rows(dialog)[0].Tag!).Id); Assert.Contains(Assert.Single(Rows(dialog), row => ((Shortcut)row.Tag!).Id == "Brush tool").GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "F7"); var state = session.History.CurrentId;
            dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); await Until(() => session.Tool == Tool.Brush); Assert.Equal(state, session.History.CurrentId);
        }
        finally { CloseOwned(window); session.MarkSaved("search.cmps", session.History.CurrentId); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Searching_cancelling_and_zooming_keep_a_changed_Warp_open_and_do_not_commit_it()
    {
        var window = new MainWindow(); window.Show(); var session = EditorSession.NewCanvas(300, 200, SKColors.White); window.AddSession(session); window.StartWarp(); var edit = session.WarpEdit!; var node = edit.Layer.WarpNodePosition(edit.Mesh.Points[5]); edit.MovePoint(5, node + new SKPoint(10, -5)); var mesh = edit.Mesh; var state = session.History.CurrentId;
        try
        {
            var dialog = Open(window); Assert.Same(edit, session.WarpEdit); Query(dialog).Text = "Invert"; Pump(); dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); await Until(() => !window.OwnedWindows.OfType<CommandSearchWindow>().Any()); Assert.Same(edit, session.WarpEdit); Assert.Equal(state, session.History.CurrentId);
            dialog = Open(window); Query(dialog).Text = "Zoom In"; Pump(); var before = window.Canvas.Zoom; dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); await Until(() => window.Canvas.Zoom > before); Assert.Same(edit, session.WarpEdit); Assert.Equal(mesh, edit.Mesh); Assert.Equal(state, session.History.CurrentId);
            dialog = Open(window); Query(dialog).Text = "Navigator"; Pump(); dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); await Until(() => window.Settings.Dock.GetValueOrDefault("Navigator")?.Visible == true); Assert.Same(edit, session.WarpEdit); Assert.Equal(state, session.History.CurrentId);
        }
        finally { CloseOwned(window); session.CancelWarp(); session.MarkSaved("search.cmps", session.History.CurrentId); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Browsing_commands_while_typing_keeps_the_live_text_editor_and_its_characters()
    {
        var window = new MainWindow(); window.Show(); var session = EditorSession.NewCanvas(300, 200); window.AddSession(session); window.SelectTool(Tool.Text); var editor = session.BeginText(new SKPoint(30, 30)); editor.Insert("Draft text"); var state = session.History.CurrentId;
        try
        {
            var dialog = Open(window); Assert.Same(editor, session.TextEdit); Query(dialog).Text = "color"; Pump(); dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); await Until(() => !window.OwnedWindows.OfType<CommandSearchWindow>().Any()); Assert.Same(editor, session.TextEdit); Assert.Equal("Draft text", session.ActiveLayer!.Text!.Text); Assert.Equal(state, session.History.CurrentId);
        }
        finally { CloseOwned(window); session.FinishText(); session.MarkSaved("search.cmps", session.History.CurrentId); window.Close(); }
    }

    private static CommandSearchWindow Open(MainWindow window) { window.Canvas.Focus(); window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Shift); Pump(); return window.OwnedWindows.OfType<CommandSearchWindow>().Single(); }
    private static TextBox Query(CommandSearchWindow dialog) => dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "CommandQuery");
    private static List<ListBoxItem> Rows(CommandSearchWindow dialog) => dialog.GetVisualDescendants().OfType<ListBox>().Single().Items.Cast<ListBoxItem>().ToList();
    private static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void CloseOwned(MainWindow window) { foreach (var owned in window.OwnedWindows.ToArray()) if (owned is CommandSearchWindow) owned.Close(null); else if (owned is DialogWindow) owned.Close(false); }
    private static async Task Until(Func<bool> ready) { for (var i = 0; i < 200 && !ready(); i++) { await Task.Delay(10); Pump(); } Assert.True(ready()); }
}
