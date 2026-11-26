using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Composa.Editing;
using Composa.Model;

namespace Composa.App;

public sealed partial class MainWindow
{
    private readonly StackPanel promptRegionList = new() { Spacing = 3, IsVisible = false };
    private EditorSession? regionOwner;
    private PromptRegion[] listedRegions = [];
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
        if (!ReferenceEquals(session, regionOwner)) finishRegionCard?.Invoke(false);
        regionOwner = session;
        listedRegions = regions?.ToArray() ?? [];
        promptRegionList.Children.Clear();
        promptRegionList.IsVisible = listedRegions.Length > 0;
        if (session is not { } owner || !promptRegionList.IsVisible) return;
        promptRegionList.Children.Add(Ui.Label($"{Locale.T("Regions")} · {listedRegions.Length}", size: 12, weight: FontWeight.SemiBold));
        foreach (var region in listedRegions)
        {
            var row = new Grid { ColumnDefinitions = new("24,Auto,*,24"), ColumnSpacing = 5 };
            var eye = Ui.IconButton(region.Enabled ? Icons.Eye : Icons.EyeOff, region.Enabled ? "Disable region" : "Enable region",
                () => owner.ChangePromptRegion(region with { Enabled = !region.Enabled }), 14);
            row.Children.Add(eye);
            AddAt(row, RegionNumber(region), 1);
            var label = Ui.Label(region.Prompt.Length > 0 ? region.Prompt : Locale.T("Region prompt"), size: 12);
            label.TextWrapping = TextWrapping.Wrap;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Opacity = region.Enabled ? 1 : .6;
            var cell = new Border { Child = label, Background = Brushes.Transparent };
            cell.DoubleTapped += (_, e) =>
            {
                if (cell.Child != label) return;
                e.Handled = true;
                var input = RegionTextBox(region);
                cell.Child = input;
                bool settled = false;
                void Finish(bool save)
                {
                    if (settled) return;
                    settled = true;
                    cell.Child = label;
                    if (save) SaveRegionPrompt(owner, region, input.Text);
                }
                input.KeyDown += (_, key) =>
                {
                    if (key.Key is not (Key.Enter or Key.Escape)) return;
                    key.Handled = true;
                    Finish(key.Key == Key.Enter);
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

    private static TextBox RegionTextBox(PromptRegion r) => new()
    {
        Name = "RegionPromptInput", Text = r.Prompt, PlaceholderText = Locale.T("Region prompt"),
        MaxLength = 8192, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(6, 3)
    };

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
