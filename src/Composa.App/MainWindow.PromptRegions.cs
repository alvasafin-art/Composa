using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.Editing;
using Composa.Model;

namespace Composa.App;

public sealed partial class MainWindow
{
    private readonly StackPanel promptRegionList = new() { Spacing = 3, IsVisible = false };
    private EditorSession? regionOwner;
    private PromptRegion[] listedRegions = [];
    private readonly Dictionary<int, TextBlock> regionPromptLabels = [];
    private TextBox? dockRegionInput;
    private Action<bool>? finishDockRegionEdit;
    private Button? allRegionsVisibility;
    private readonly Canvas regionCardLayer = new() { ClipToBounds = true };
    private Action<bool>? finishRegionCard;

    private static Border RegionNumber(PromptRegion region)
    {
        var c = region.Ink.Color;
        var text = Ui.Label(region.Number.ToString(), c.Red + c.Green > 320 ? Brushes.Black : Brushes.White, 11, FontWeight.SemiBold);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        return new Border { Child = text, MinWidth = 18, Height = 18, CornerRadius = new(3),
            VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.FromRgb(c.Red,c.Green,c.Blue)) };
    }

    private void RefreshPromptRegions()
    {
        var regions = session?.Document.PromptRegions;
        if (ReferenceEquals(session,regionOwner) && (regions == null || listedRegions.SequenceEqual(regions))) return;
        if (ReferenceEquals(session, regionOwner) && regions != null && listedRegions.Length == regions.Count &&
            listedRegions.Zip(regions).All(pair => pair.First.Number == pair.Second.Number && pair.First.Enabled == pair.Second.Enabled))
        {
            // Keep other controls under the pointer in place while finishing an inline text edit.
            foreach (var region in regions) regionPromptLabels[region.Number].Text = RegionPromptLabel(region);
            listedRegions = regions.ToArray();
            return;
        }
        finishDockRegionEdit?.Invoke(ReferenceEquals(session, regionOwner));
        if (!ReferenceEquals(session, regionOwner)) finishRegionCard?.Invoke(false);
        regionOwner = session;
        listedRegions = regions?.ToArray() ?? [];
        promptRegionList.Children.Clear();
        regionPromptLabels.Clear();
        allRegionsVisibility = null;
        promptRegionList.IsVisible = listedRegions.Length > 0;
        if (session is not { } owner || !promptRegionList.IsVisible) return;
        allRegionsVisibility = RegionAction(Icons.Eye, "Hide all regions", TogglePromptRegions);
        allRegionsVisibility.Name = "ToggleAllPromptRegions";
        UpdatePromptRegionVisibility();
        var deleteAll = RegionAction(Icons.Trash, "Delete all regions", owner.DeleteAllPromptRegions);
        deleteAll.Name = "DeleteAllPromptRegions";
        var header = new Grid { ColumnDefinitions = new("*,Auto") };
        var title = Ui.Label(Locale.T("Regions"), Palette.Secondary, 12);
        title.Margin = new Thickness(5, 0, 0, 0);
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title);
        AddAt(header, Ui.Row(2, allRegionsVisibility, deleteAll), 1);
        promptRegionList.Children.Add(header);
        foreach (var region in listedRegions)
        {
            var row = new Grid { ColumnDefinitions = new("24,Auto,*,24"), ColumnSpacing = 5 };
            PromptRegion? Current() => owner.Document.PromptRegions.Find(r => r.Number == region.Number);
            var eye = Ui.IconButton(region.Enabled ? Icons.Eye : Icons.EyeOff, region.Enabled ? "Disable region" : "Enable region",
                () => { if (Current() is { } current) owner.ChangePromptRegion(current with { Enabled = !current.Enabled }); }, 14);
            row.Children.Add(eye);
            AddAt(row, RegionNumber(region), 1);
            var label = Ui.Label(RegionPromptLabel(region), size: 12);
            regionPromptLabels.Add(region.Number, label);
            label.TextWrapping = TextWrapping.Wrap;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Opacity = region.Enabled ? 1 : .6;
            var cell = new Border { Child = label, Background = Brushes.Transparent };
            cell.DoubleTapped += (_, e) =>
            {
                if (cell.Child != label || Current() is not { } current) return;
                e.Handled = true;
                finishDockRegionEdit?.Invoke(true);
                var input = RegionTextBox(current);
                cell.Child = input;
                dockRegionInput = input;
                bool settled = false;
                void Finish(bool save)
                {
                    if (settled) return;
                    settled = true;
                    dockRegionInput = null;
                    finishDockRegionEdit = null;
                    cell.Child = label;
                    if (save) SaveRegionPrompt(owner, current, input.Text);
                }
                finishDockRegionEdit = Finish;
                input.KeyDown += (_, key) =>
                {
                    if (key.Key is not (Key.Enter or Key.Escape)) return;
                    key.Handled = true;
                    Finish(key.Key == Key.Enter);
                    canvas.Focus();
                };
                input.LostFocus += (_, _) => Finish(true);
                input.Focus();
                input.SelectAll();
            };
            AddAt(row, cell, 2);
            AddAt(row, Ui.IconButton(Icons.Trash, "Delete region", () => owner.DeletePromptRegion(region.Number), 14), 3);
            promptRegionList.Children.Add(row);
        }
    }

    private static string RegionPromptLabel(PromptRegion region) =>
        region.Prompt.Length > 0 ? region.Prompt : Locale.T("Region prompt");

    private void CommitRegionPromptOutside(Visual? source)
    {
        if (dockRegionInput is { } input && source?.GetSelfAndVisualAncestors().Contains(input) != true)
            finishDockRegionEdit?.Invoke(true);
    }

    private static TextBox RegionTextBox(PromptRegion r) => new()
    {
        Name = "RegionPromptInput", Text = r.Prompt, PlaceholderText = Locale.T("Region prompt"),
        MaxLength = 8192, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(6, 3)
    };

    private void TogglePromptRegions()
    {
        canvas.ShowPromptRegions = !canvas.ShowPromptRegions;
        UpdatePromptRegionVisibility();
        if (session?.Tool == Tool.PromptRegion) RebuildOptions();
    }

    private static Button RegionAction(Icons.Icon icon, string tip, Action click)
    {
        var button = Ui.IconButton(icon, tip, click, 14);
        button.Width = 24;
        button.Height = 22;
        button.Padding = new Thickness(4, 0);
        return button;
    }

    private void UpdatePromptRegionVisibility()
    {
        if (allRegionsVisibility is not { } button) return;
        button.Content = Icons.Create(canvas.ShowPromptRegions ? Icons.Eye : Icons.EyeOff, 14);
        var label = Locale.T(canvas.ShowPromptRegions ? "Hide all regions" : "Show all regions");
        Locale.Tip(button, label);
        AutomationProperties.SetName(button, label);
    }

    private void SaveRegionPrompt(EditorSession owner, PromptRegion original, string? value)
    {
        if (ReferenceEquals(owner, session) && owner.Document.PromptRegions.FirstOrDefault(r => r.Number == original.Number) is { } current)
            owner.ChangePromptRegion(current with { Prompt = value?.Trim() ?? "" });
    }

    private void OpenRegionPrompt(PromptRegion region, Point point)
    {
        if (session is not { } owner) return;
        finishRegionCard?.Invoke(true);
        var input = RegionTextBox(region);
        var body = Ui.Column(6, Ui.Row(7, RegionNumber(region), Ui.Label(Locale.T("Region prompt"), weight: FontWeight.SemiBold)), input,
            Ui.Label(Locale.T("Enter to save · Esc to cancel"), Palette.Secondary, 11));
        var card = new Border { Background = Palette.Panel, BorderBrush = Palette.Edge, BorderThickness = new(1),
            CornerRadius = new(5), Width = Math.Min(310, canvas.Bounds.Width), Padding = new(10), Child = body };
        card.Measure(new Size(card.Width, double.PositiveInfinity));
        var size = card.DesiredSize;
        Avalonia.Controls.Canvas.SetLeft(card, Math.Clamp(point.X, 0, Math.Max(0, canvas.Bounds.Width - size.Width)));
        var top = point.Y + size.Height + 5 <= canvas.Bounds.Height ? point.Y + 5 : point.Y - size.Height - 5;
        Avalonia.Controls.Canvas.SetTop(card, Math.Clamp(top, 0, Math.Max(0, canvas.Bounds.Height - size.Height)));
        regionCardLayer.Children.Add(card);

        bool closed = false;
        void Finish(bool save)
        {
            if (closed) return;
            closed = true;
            finishRegionCard = null;
            regionCardLayer.Children.Remove(card);
            if (save) SaveRegionPrompt(owner, region, input.Text);
        }
        finishRegionCard = Finish;
        input.LostFocus += (_, _) => Finish(true);
        input.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            e.Handled = true;
            Finish(e.Key == Key.Enter);
            canvas.Focus();
        };
        Dispatcher.UIThread.Post(() => { if (!closed) { input.Focus(); input.SelectAll(); } });
    }
}
