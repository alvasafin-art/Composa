using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Composa.App.Dialogs;

/// <summary>Named, explicitly saved panel layouts; applying one never changes document or tool state.</summary>
public sealed class WorkspacesWindow : Window
{
    private readonly ListBox layouts = new() { Name = "WorkspaceList", Height = 190 };
    private readonly TextBox name = new() { Name = "WorkspaceName", Watermark = Locale.T("Workspace name"), MaxLength = 64 };
    private readonly TextBlock status = Ui.Label("", Palette.Secondary);
    private readonly Settings settings;
    private readonly Func<WorkspaceLayout> capture;
    private readonly Action<WorkspaceLayout> apply;

    public WorkspacesWindow(Settings settings, Func<WorkspaceLayout> capture, Action<WorkspaceLayout> apply)
    {
        this.settings = settings; this.capture = capture; this.apply = apply;
        Title = Locale.T("Workspaces"); Width = 530; SizeToContent = SizeToContent.Height; CanResize = false; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var save = Ui.TextButton("Save Current", Save);
        var use = Ui.TextButton("Apply", () => { if (SelectedKey() is { } key) { apply(settings.Workspaces[key]); status.Text = Locale.Message("Workspace applied."); } });
        var delete = Ui.TextButton("Delete", () => { if (SelectedKey() is { } key) { settings.Workspaces.Remove(key); settings.Save(); Refresh(); status.Text = Locale.Message("Workspace deleted."); } });
        var reset = Ui.TextButton("Default Layout", () =>
        {
            apply(new(new() { ["Navigator"] = new(false, false, 220), ["Channels"] = new(false, false, 250), ["AI Generation"] = new(false, false, 164), ["History"] = new(true, false, 220) }, false));
            status.Text = Locale.Message("Default panel layout restored. Saved workspaces are kept.");
        });
        Content = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children =
        {
            Ui.Label(Locale.T("Save panel visibility, collapsed sections, heights and Canvas Only."), Palette.Secondary),
            layouts, name, Ui.Row(8, save, use, delete), status, Ui.Row(8, reset, Ui.TextButton("Close", () => Close()))
        } };
        layouts.SelectionChanged += (_, _) =>
        {
            use.IsEnabled = delete.IsEnabled = SelectedKey() != null;
            if (SelectedKey() is { } key) name.Text = key;
        };
        AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) => name.Focus(); Refresh(); use.IsEnabled = delete.IsEnabled = false;
    }

    private string? SelectedKey() => layouts.SelectedItem as string;
    private void Refresh(string? select = null)
    {
        layouts.ItemsSource = settings.Workspaces.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        layouts.SelectedItem = select;
    }
    private void Save()
    {
        var requested = name.Text?.Trim() ?? "";
        if (requested.Length == 0 || requested.Length > 64 || requested.Any(char.IsControl)) { status.Text = Locale.Message("Enter a name of 1–64 characters."); return; }
        var key = settings.Workspaces.Keys.FirstOrDefault(key => key.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (key == null && settings.Workspaces.Count >= 32) { status.Text = Locale.Message("Up to 32 workspaces can be saved. Delete one first."); return; }
        // Replacing the selected workspace is explicit; a colliding typed name must first be selected.
        if (key != null && SelectedKey() != key) { status.Text = Locale.Message("Select that workspace to replace it, or enter a new name."); return; }
        var current = capture(); settings.Workspaces[key ?? requested] = current with { Panels = new(current.Panels) };
        settings.Save(); Refresh(key ?? requested); status.Text = Locale.Message(key == null ? "Workspace saved." : "Workspace replaced with the current layout.");
    }
}
