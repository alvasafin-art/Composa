using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Composa.Model;

namespace Composa.App.Controls;

public sealed partial class LayersPanel
{
    private readonly TextBox search = new() { PlaceholderText = Locale.T("Find layers / #tag"), MaxLength = 256, MinWidth = 0 };
    private readonly ComboBox searchKind = new() { ItemsSource = new[] { "All kinds", "Pixels", "Text", "Shapes", "Adjustments", "Smart Objects", "Groups" }.Select(Locale.T).ToArray(), SelectedIndex = 0, MinWidth = 0, Height = HeaderFieldHeight, FontSize = 12, Padding = new Thickness(8, 0), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock searchStatus = new() { Foreground = Palette.Secondary, FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private HashSet<Guid>? filterRows;
    private bool IsFiltering => !string.IsNullOrWhiteSpace(search.Text) || searchKind.SelectedIndex > 0;

    private void AddSearchHeader(Grid header)
    {
        var queryRow = new Grid { Margin = new Thickness(0, 6, 0, 0), Height = HeaderFieldHeight };
        search.MinHeight = search.Height = HeaderFieldHeight;
        search.FontSize = 12;
        search.VerticalContentAlignment = VerticalAlignment.Center;
        search.Padding = new Thickness(8, 0, 25, 0);
        var clear = Ui.IconButton(Icons.Close, "Clear layer filters", () => { search.Text = ""; searchKind.SelectedIndex = 0; }, 11);
        clear.Name = "ClearLayerFilter";
        clear.Width = 24; clear.MinWidth = 0; clear.Height = HeaderFieldHeight;
        clear.HorizontalAlignment = HorizontalAlignment.Right;
        clear.VerticalContentAlignment = VerticalAlignment.Center;
        clear.HorizontalContentAlignment = HorizontalAlignment.Center;
        clear.Padding = new Thickness(0);
        Locale.Tip(clear, "Clear layer filters (Escape)");
        queryRow.Children.Add(search); queryRow.Children.Add(clear);
        Locale.Tip(search, "Words match layer names and tags. #tag matches an exact tag. All words must match. Escape clears. Reordering is available after clearing the filter.");
        Grid.SetRow(queryRow, 2); header.Children.Add(queryRow);
        Grid.SetRow(searchKind, 2); Grid.SetColumn(searchKind, 1);
        searchKind.Margin = new Thickness(0, 6, 0, 0); header.Children.Add(searchKind);
        Grid.SetRow(searchStatus, 3); Grid.SetColumnSpan(searchStatus, 2); searchStatus.Margin = new Thickness(0, 4, 0, 0); header.Children.Add(searchStatus);
        var openSearch = Ui.IconButton(Icons.Zoom, "Find and filter layers", () => { });
        openSearch.HorizontalAlignment = HorizontalAlignment.Right;
        openSearch.Height = 22; openSearch.MinHeight = 0; openSearch.Padding = new Thickness(3);
        Grid.SetColumn(openSearch, 1); header.Children.Add(openSearch);
        bool expanded = false;
        void FitSearch()
        {
            var compact = Bounds.Height > 0 && Bounds.Height < 305;
            queryRow.IsVisible = !compact || expanded || IsFiltering;
            searchKind.IsVisible = queryRow.IsVisible;
            openSearch.IsVisible = !queryRow.IsVisible;
            clear.IsVisible = IsFiltering;
            search.Padding = new Thickness(8, 0, clear.IsVisible ? 25 : 8, 0);
        }
        openSearch.Click += (_, _) => { expanded = true; FitSearch(); search.Focus(); };
        SizeChanged += (_, _) => FitSearch();
        clear.Click += (_, _) => { expanded = false; FitSearch(); };
        FitSearch();
        search.TextChanged += (_, _) => { FitSearch(); Rebuild(); };
        searchKind.SelectionChanged += (_, _) => { FitSearch(); Rebuild(); };
        search.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            search.Text = ""; searchKind.SelectedIndex = 0; expanded = false; FitSearch(); e.Handled = true;
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
        filterRows = [];
        var count = 0;
        bool IncludeMatches(Layer layer)
        {
            var included = Match(layer);
            if (included) count++;
            foreach (var child in layer.Children) included |= IncludeMatches(child);
            if (included) filterRows.Add(layer.Id);
            return included;
        }
        foreach (var root in session.Document.Layers) IncludeMatches(root);
        var outside = session.Document.SelectedLayerIds.Count(id => !filterRows.Contains(id));
        searchStatus.Text = Locale.Message($"{count} match{(count == 1 ? "" : "es")}" + (outside > 0 ? $" · {outside} selected outside filter" : "") + " · Clear to reorder");
        ClearDropHighlight();
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
