using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Composa.ColorManagement;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Controls;

/// <summary>Channel visibility and edit targets are independent; alpha channels are document data.</summary>
public sealed class ChannelsPanel : UserControl
{
    private EditorSession? session;
    private readonly StackPanel rows = new();
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly TextBlock target = Ui.Label("No document", Palette.Secondary);
    private readonly Button add, saveSelection, loadSelection, delete;
    private bool rebuilding;
    private RgbProfile displayProfile = RgbProfile.Srgb;
    public event Action<bool>? NewRequested;
    public event Action<string>? RenameRequested;
    public event Action<string>? Problem;

    public ChannelsPanel()
    {
        add = Ui.IconButton(Icons.Plus, "New alpha channel", () => NewRequested?.Invoke(false));
        saveSelection = Ui.IconButton(Icons.Mask, "Save selection as an alpha channel", () => NewRequested?.Invoke(true));
        loadSelection = Ui.TextButton("Load selection", () => { if (session?.EditingAlphaChannel is { } alpha) session.LoadSelection(alpha); });
        delete = Ui.IconButton(Icons.Trash, "Delete selected alpha channel", () => { if (session?.EditingAlphaChannel is { } alpha) session.DeleteSelectionChannel(alpha); });
        var buttons = Ui.Row(8, add, saveSelection, loadSelection, delete);
        buttons.Margin = new Thickness(8, 4); target.Margin = new Thickness(8, 4); target.TextTrimming = TextTrimming.CharacterEllipsis;
        var panel = new DockPanel(); DockPanel.SetDock(target, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(target); panel.Children.Add(buttons); panel.Children.Add(new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Content = panel;
        refreshTimer.Tick += (_, _) => { refreshTimer.Stop(); if (IsEffectivelyVisible) Rebuild(); };
        DetachedFromVisualTree += (_, _) => refreshTimer.Stop();
        AttachedToVisualTree += (_, _) => Rebuild();
    }

    public RgbProfile DisplayProfile { get => displayProfile; set { if (displayProfile.Equals(value)) return; displayProfile = value; Rebuild(); } }
    public EditorSession? Session
    {
        get => session;
        set
        {
            if (session == value) return;
            if (session != null) { session.ChannelsChanged -= Rebuild; session.LayersChanged -= Rebuild; session.CanvasChanged -= QueueRefresh; session.SelectionChanged -= Rebuild; }
            session = value;
            if (session != null) { session.ChannelsChanged += Rebuild; session.LayersChanged += Rebuild; session.CanvasChanged += QueueRefresh; session.SelectionChanged += Rebuild; }
            Rebuild();
        }
    }

    private void QueueRefresh(SKRectI? area) { if (!refreshTimer.IsEnabled) refreshTimer.Start(); }
    private void Run(Action action)
    {
        try { action(); } catch (Exception error) { Problem?.Invoke(error.Message); }
    }

    private void Rebuild()
    {
        if (rebuilding) return; rebuilding = true;
        try
        {
            rows.Children.Clear(); var current = session;
            add.IsEnabled = current != null && current.Document.AlphaChannels.Count < 64;
            saveSelection.IsEnabled = add.IsEnabled && current?.Selection != null;
            loadSelection.IsEnabled = delete.IsEnabled = current?.IsEditingAlphaChannel == true;
            if (current == null) { target.Text = "No document"; return; }
            target.Text = current.EditingAlphaChannel is { } name ? "Editing alpha · " + name
                : current.IsEditingMask ? "Editing layer mask" : "Editing · " + (current.EditingRgbChannels == RgbChannels.All ? "RGB" : current.EditingRgbChannels.ToString());
            if (TopLevel.GetTopLevel(this) == null || !IsEffectivelyVisible) return;
            var scale = Math.Min(1, 56.0 / Math.Max(current.Document.Width, current.Document.Height));
            using var sample = Pixels.NewWorkingColor(Math.Max(1, (int)Math.Round(current.Document.Width * scale)), Math.Max(1, (int)Math.Round(current.Document.Height * scale)), ColorDepth.Eight, current.Document.Profile);
            DocumentRenderer.Render(current.Document, sample, new(0, 0, sample.Width, sample.Height), new RenderView((float)scale, SKPoint.Empty));
            AddRow("RGB", sample, current.VisibleRgbChannels == RgbChannels.All && (!current.IsEditingAlphaChannel || current.AlphaChannelOverlay),
                !current.IsEditingMask && current.EditingRgbChannels == RgbChannels.All,
                () => current.EditRgbChannels(), () => current.SetChannelView(RgbChannels.All, current.IsEditingAlphaChannel));
            foreach (var channel in new[] { RgbChannels.Red, RgbChannels.Green, RgbChannels.Blue })
            {
                // Component thumbnails are neutral gray values, independent of the document's RGB primaries.
                using var gray = Pixels.ExtractChannel(sample, channel, RgbProfile.Srgb);
                AddRow(channel.ToString(), gray, (!current.IsEditingAlphaChannel || current.AlphaChannelOverlay) && current.VisibleRgbChannels.HasFlag(channel),
                    !current.IsEditingMask && current.EditingRgbChannels.HasFlag(channel), () => current.EditRgbChannels(channel),
                    () => current.SetChannelView(current.VisibleRgbChannels ^ channel, current.IsEditingAlphaChannel), channel: channel);
            }
            foreach (var pair in current.Document.AlphaChannels)
            {
                using var small = Pixels.NewColor(sample.Width, sample.Height);
                using (var canvas = new SKCanvas(small))
                {
                    canvas.Clear(SKColors.Black); using var paint = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(SKColors.White, SKBlendMode.SrcIn) };
                    canvas.DrawBitmap(pair.Value, new SKRect(0, 0, small.Width, small.Height), paint);
                }
                var alpha = pair.Key;
                AddRow(alpha, small, current.EditingAlphaChannel?.Equals(alpha, StringComparison.OrdinalIgnoreCase) == true,
                    current.EditingAlphaChannel?.Equals(alpha, StringComparison.OrdinalIgnoreCase) == true, () => current.EditAlphaChannel(alpha),
                    () => current.EditAlphaChannel(alpha, !current.AlphaChannelOverlay), alpha: alpha);
            }
        }
        finally { rebuilding = false; }
    }

    private void AddRow(string name, SKBitmap pixels, bool visible, bool selected, Action select, Action toggle, RgbChannels? channel = null, string? alpha = null)
    {
        var current = session!;
        var eye = Ui.IconButton(visible ? Icons.Eye : Icons.EyeOff, "Toggle channel visibility", () => Run(toggle)); eye.Width = 28;
        var label = Ui.Label(name); label.TextTrimming = TextTrimming.CharacterEllipsis; ToolTip.SetTip(label, name);
        var grid = new Grid { ColumnDefinitions = new("Auto,44,*"), ColumnSpacing = 6 };
        grid.Children.Add(eye);
        var image = new Image { Source = Ui.ToAvaloniaBitmap(pixels, 56, displayProfile), Width = 38, Height = 28, Stretch = Stretch.Uniform };
        Grid.SetColumn(image, 1); grid.Children.Add(image); Grid.SetColumn(label, 2); grid.Children.Add(label);
        var row = new Border { Name = alpha == null ? "Channel" + name : "AlphaChannelRow", Child = grid, Background = selected ? Palette.Selected : Brushes.Transparent,
            Padding = new Thickness(4, 2), MinHeight = 34 };
        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed || eye.IsPointerOver) return;
            Run(() =>
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    if (alpha != null) current.LoadSelection(alpha, e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? Composa.Selections.SelectionMode.Add : Composa.Selections.SelectionMode.Replace);
                    else if (channel is { } component) current.LoadRgbChannel(component);
                    return;
                }
                if (channel is { } selectedChannel && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !current.IsEditingMask)
                { var next = current.EditingRgbChannels ^ selectedChannel; if (next != RgbChannels.None) current.EditRgbChannels(next); }
                else select();
                if (e.ClickCount >= 2 && alpha != null) RenameRequested?.Invoke(alpha);
            });
            e.Handled = true;
        };
        if (alpha != null)
        {
            MenuItem Item(string title, Action action) { var item = new MenuItem { Header = title }; item.Click += (_, _) => Run(action); return item; }
            row.ContextMenu = new ContextMenu { ItemsSource = new object[]
            {
                Item("Rename…", () => RenameRequested?.Invoke(alpha)), Item("Duplicate", () => current.DuplicateAlphaChannel(alpha)),
                Item("Load Selection", () => current.LoadSelection(alpha)), Item("Move Up", () => current.MoveAlphaChannel(alpha, -1)),
                Item("Move Down", () => current.MoveAlphaChannel(alpha, 1)), Item("Delete", () => current.DeleteSelectionChannel(alpha))
            } };
        }
        rows.Children.Add(row);
    }
}
