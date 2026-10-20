using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Composa.Model;

namespace Composa.App.Controls;

public sealed partial class LayersPanel
{
    private readonly TextBox search = new() { Watermark = "Find layers / #tag", MaxLength = 256, MinWidth = 0 };
    private readonly ComboBox searchKind = new() { ItemsSource = new[] { "All kinds", "Pixels", "Text", "Shapes", "Adjustments", "Smart Objects", "Groups" }, SelectedIndex = 0, MinWidth = 0 };
    private readonly TextBlock searchStatus = new() { Foreground = Palette.Secondary, FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private HashSet<Guid>? filterRows;
    private bool IsFiltering => !string.IsNullOrWhiteSpace(search.Text) || searchKind.SelectedIndex > 0;

    private void AddSearchHeader(Grid header)
    {
        header.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        header.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        header.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var queryRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        var clear = Ui.TextButton("Clear", () => { search.Text = ""; searchKind.SelectedIndex = 0; });
        Grid.SetColumn(clear, 1); queryRow.Children.Add(search); queryRow.Children.Add(clear);
        ToolTip.SetTip(search, "Words match layer names and tags. #tag matches an exact tag. All words must match. Escape clears. Reordering is available after clearing the filter.");
        Grid.SetRow(queryRow, 2); Grid.SetColumnSpan(queryRow, 3); header.Children.Add(queryRow);
        Grid.SetRow(searchKind, 3); Grid.SetColumnSpan(searchKind, 3); searchKind.Margin = new Thickness(0, 4, 0, 0); header.Children.Add(searchKind);
        Grid.SetRow(searchStatus, 4); Grid.SetColumnSpan(searchStatus, 3); searchStatus.Margin = new Thickness(0, 4, 0, 0); header.Children.Add(searchStatus);
        search.TextChanged += (_, _) => Rebuild();
        searchKind.SelectionChanged += (_, _) => Rebuild();
        search.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            search.Text = ""; searchKind.SelectedIndex = 0; e.Handled = true;
        };
    }

    private void PrepareFilter()
    {
        filterRows = null; searchStatus.IsVisible = IsFiltering;
        if (!IsFiltering || session == null) { searchStatus.Text = ""; return; }
        var words = (search.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool Match(Layer layer) => KindMatches(layer) && words.All(word => word.StartsWith('#')
            ? word.Length > 1 && layer.Tags.Contains(word[1..], StringComparer.OrdinalIgnoreCase)
            : layer.Name.Contains(word, StringComparison.OrdinalIgnoreCase) || layer.Tags.Any(tag => tag.Contains(word, StringComparison.OrdinalIgnoreCase)));
        var matches = session.Document.AllLayers().Where(Match).ToArray();
        filterRows = matches.Select(layer => layer.Id).ToHashSet();
        foreach (var match in matches)
            for (var parent = session.Document.ParentOf(match.Id); parent != null; parent = session.Document.ParentOf(parent.Id)) filterRows.Add(parent.Id);
        var outside = session.Document.SelectedLayerIds.Count(id => !filterRows.Contains(id));
        searchStatus.Text = $"{matches.Length} match{(matches.Length == 1 ? "" : "es")}" + (outside > 0 ? $" · {outside} selected outside filter" : "") + " · Clear to reorder";
        pressed = null; dragging = false; dropTarget = null; dropLine.IsVisible = false;
    }

    private bool KindMatches(Layer layer) => searchKind.SelectedIndex switch
    {
        1 => !layer.IsGroup && !layer.IsAdjustment && layer.Text == null && layer.Shape == null && !layer.IsSmartObject,
        2 => layer.Text != null,
        3 => layer.Shape != null,
        4 => layer.IsAdjustment,
        5 => layer.IsSmartObject,
        6 => layer.IsGroup,
        _ => true
    };
}
