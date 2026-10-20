using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public class WorkspaceTests
{
    [AvaloniaFact]
    public void Workspace_capture_is_independent_and_apply_restores_chrome_without_committing_live_text()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show(); var session = EditorSession.NewCanvas(400, 250, SKColors.White, ColorDepth.Sixteen); window.AddSession(session);
        try
        {
            var dock = window.GetVisualDescendants().OfType<SideDock>().Single(); dock.SetVisible(dock.Section("Navigator"), true); dock.SetCollapsed(dock.Section("History"), true); window.SetCanvasOnly(true);
            var layout = window.CaptureWorkspace(); dock.SetVisible(dock.Section("Navigator"), false); dock.SetCollapsed(dock.Section("History"), false); window.SetCanvasOnly(false);
            Assert.True(layout.Panels["Navigator"].Visible); Assert.True(layout.Panels["History"].Collapsed);
            window.SelectTool(Tool.Text); var edit = session.BeginText(new SKPoint(20, 30)); edit.Insert("Uncommitted draft"); var state = session.History.CurrentId;
            window.ApplyWorkspace(layout); Pump(); Assert.True(window.Settings.CanvasOnly); Assert.True(dock.Section("Navigator").State.Visible); Assert.True(dock.Section("History").State.Collapsed); Assert.Same(edit, session.TextEdit); Assert.Equal(state, session.History.CurrentId);
            window.ApplyWorkspace(layout with { CanvasOnly = false }); Assert.False(window.Settings.CanvasOnly); Assert.Same(edit, session.TextEdit);
            var encoded = JsonSerializer.Serialize(new Settings { Workspaces = new() { ["Retouch"] = layout } }); var restored = JsonSerializer.Deserialize<Settings>(encoded)!;
            Assert.True(restored.Workspaces["Retouch"].CanvasOnly); Assert.Equal(layout.Panels["Navigator"], restored.Workspaces["Retouch"].Panels["Navigator"]); Assert.Empty(JsonSerializer.Deserialize<Settings>("{}")!.Workspaces);
        }
        finally { session.FinishText(); session.MarkSaved("workspace.cmps", session.History.CurrentId); window.Close(); }
    }

    [AvaloniaFact]
    public void Workspace_dialog_saves_applies_replaces_and_deletes_explicitly_without_losing_other_layouts()
    {
        var owner = new MainWindow { Width = 1280, Height = 900 }; owner.Show(); var dock = owner.GetVisualDescendants().OfType<SideDock>().Single();
        var dialog = new WorkspacesWindow(owner.Settings, owner.CaptureWorkspace, owner.ApplyWorkspace); dialog.Show(owner); Pump();
        try
        {
            var name = dialog.GetVisualDescendants().OfType<TextBox>().Single(); var list = dialog.GetVisualDescendants().OfType<ListBox>().Single();
            name.Text = "Retouch"; Click(dialog, "Save Current"); Assert.Single(owner.Settings.Workspaces);
            dock.SetVisible(dock.Section("Navigator"), true); name.Text = "Compositing"; Click(dialog, "Save Current"); Assert.Equal(2, owner.Settings.Workspaces.Count);
            list.SelectedItem = "Retouch"; Click(dialog, "Apply"); Assert.False(dock.Section("Navigator").State.Visible);
            dock.SetCollapsed(dock.Section("History"), true); Click(dialog, "Save Current"); Assert.True(owner.Settings.Workspaces["Retouch"].Panels["History"].Collapsed);
            list.SelectedItem = "Compositing"; Click(dialog, "Apply"); Assert.True(dock.Section("Navigator").State.Visible); Assert.False(dock.Section("History").State.Collapsed);
            Click(dialog, "Delete"); Assert.Single(owner.Settings.Workspaces); Assert.Contains("Retouch", owner.Settings.Workspaces.Keys);
            name.Text = "RETouCH"; Click(dialog, "Save Current"); Assert.Single(owner.Settings.Workspaces); Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.StartsWith("Select that workspace") == true);
            Click(dialog, "Default Layout"); Assert.False(dock.Section("Navigator").State.Visible); Assert.True(dock.Section("History").State.Visible); Assert.False(dock.Section("History").State.Collapsed); Assert.Single(owner.Settings.Workspaces);
            Pump(); Assert.True(Screenshots.Save(dialog, "workspaces"));
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [AvaloniaFact]
    public void Invalid_saved_height_and_unknown_panel_do_not_break_the_dock_or_retain_mutable_snapshot_state()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show();
        try
        {
            window.ApplyWorkspace(new(new() { ["History"] = new(true, false, double.NaN), ["Removed Plugin"] = new() }, false)); Pump();
            var layout = window.CaptureWorkspace(); Assert.Equal(220, layout.Panels["History"].Height); Assert.DoesNotContain("Removed Plugin", layout.Panels.Keys);
            layout.Panels["History"] = new(false); Assert.True(window.CaptureWorkspace().Panels["History"].Visible);
        }
        finally { window.Close(); }
    }
    private static void Click(Window window, string title) { window.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == title).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    private static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
}
