using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Composa.App.Dialogs;

/// <summary>Searches the actual command registry, including scripts and rebound shortcuts. Commands are
/// returned to the window for its normal guard/execute path; looking for one is never an edit.</summary>
public sealed class CommandSearchWindow : Window
{
    private readonly TextBox query = new() { Name = "CommandQuery", PlaceholderText = "Type a command, tool or menu name…", MaxLength = 256 };
    private readonly ListBox results = new() { Name = "CommandResults", Height = 350, Background = Brushes.Transparent };
    private readonly TextBlock status = Ui.Label("", Palette.Secondary);
    private readonly IReadOnlyList<Shortcut> commands;
    private readonly Func<Shortcut, bool> canRun;

    public CommandSearchWindow(IReadOnlyList<Shortcut> commands, Func<Shortcut, bool> canRun)
    {
        this.commands = commands.Where(c => !c.Hidden).ToArray(); this.canRun = canRun;
        Title = "Find Command"; Width = 620; SizeToContent = SizeToContent.Height; CanResize = false; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children = { query, results, status, Ui.Label("↑ ↓ to choose · Enter to run · Esc to close", Palette.Secondary) } };
        query.TextChanged += (_, _) => Rebuild();
        results.SelectionChanged += (_, _) => RefreshStatus();
        results.DoubleTapped += (_, e) => { if (e.Source is Control c && (c is ListBoxItem || c.FindAncestorOfType<ListBoxItem>() != null)) RunSelected(); };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(null); e.Handled = true; }
            else if (e.Key == Key.Enter) { RunSelected(); e.Handled = true; }
            else if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown)
            {
                var move = e.Key switch { Key.Up => -1, Key.Down => 1, Key.PageUp => -8, _ => 8 };
                if (results.ItemCount > 0) { results.SelectedIndex = Math.Clamp(results.SelectedIndex + move, 0, results.ItemCount - 1); results.ScrollIntoView(results.SelectedItem!); }
                e.Handled = true;
            }
            // Printable keys remain unhandled for X11 text input.
        }, RoutingStrategies.Tunnel);
        Opened += (_, _) => query.Focus(); Rebuild();
    }

    private static string DisplayTitle(Shortcut c) => c.Id.StartsWith("automation:", StringComparison.Ordinal) ? c.Title
        : c.Id is "Undo" or "Redo" ? (c.Item?.Header as string ?? c.Title).TrimEnd('…') : c.Id;
    private static string PathFor(Shortcut c)
    {
        if (c.Item == null) return c.Group;
        var path = c.Item.GetLogicalAncestors().OfType<MenuItem>().Reverse().Select(m => (m.Header as string ?? "").Replace("_", "")).Where(s => s.Length > 0).ToArray();
        return path.Length == 0 ? c.Group : string.Join(" › ", path);
    }
    private void Rebuild()
    {
        var words = (query.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); var text = query.Text?.Trim() ?? "";
        var matching = commands.Select(c => new { Command = c, Title = DisplayTitle(c), Path = PathFor(c) })
            .Where(c => words.All(word => (c.Title + " " + c.Command.Title + " " + c.Path).Contains(word, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Title.Equals(text, StringComparison.OrdinalIgnoreCase) ? 0 : c.Title.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        results.Items.Clear();
        foreach (var row in matching)
        {
            var enabled = canRun(row.Command); var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(4, 3), Opacity = enabled ? 1 : .45 };
            var title = Ui.Label((row.Command.Item?.IsChecked == true ? "✓  " : "") + row.Title); title.TextTrimming = TextTrimming.CharacterEllipsis;
            var path = Ui.Label(row.Path, Palette.Secondary); path.FontSize = 11; path.TextTrimming = TextTrimming.CharacterEllipsis;
            body.Children.Add(new StackPanel { Spacing = 2, Children = { title, path } });
            if (row.Command.Gesture != null) { var key = Ui.Label(Shortcut.Label(row.Command.Gesture), Palette.Secondary); key.Margin = new Thickness(12, 0, 0, 0); key.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(key, 1); body.Children.Add(key); }
            results.Items.Add(new ListBoxItem { Content = body, Tag = row.Command });
        }
        results.SelectedIndex = matching.Length > 0 ? 0 : -1; RefreshStatus();
    }
    private void RefreshStatus()
    { status.Text = results.SelectedItem is ListBoxItem { Tag: Shortcut c } && !canRun(c) ? "Unavailable in the current editor state" : results.ItemCount == 0 ? "No matching commands" : $"{results.ItemCount} commands"; }
    private void RunSelected()
    { if (results.SelectedItem is not ListBoxItem { Tag: Shortcut command }) return; if (!canRun(command)) { RefreshStatus(); return; } Close(command); }
}
