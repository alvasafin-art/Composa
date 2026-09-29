using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Composa.AI;
using Composa.App.AI;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.IO;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App;

public sealed partial class MainWindow
{
    internal AiTaskService AiTasks => aiTasks;
    internal bool AiFloatingVisible => aiFloatingHost.IsVisible;
    internal int AiReferenceCount => aiReferences.Count;
    private readonly StackPanel aiActionHost = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel aiProgressHost = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly Canvas aiFloatingLayer = new();
    private readonly Border aiFloatingHost = new()
    {
        IsVisible = false, Width = 520, Padding = new Thickness(12), CornerRadius = new CornerRadius(8),
        Background = new SolidColorBrush(Color.Parse("#292929")), BorderBrush = new SolidColorBrush(Color.Parse("#4A4A4A")),
        BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
    };
    private readonly TextBox aiFloatingPrompt = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MaxHeight = 90, PlaceholderText = "Describe what you want to create or edit" };
    private readonly StackPanel aiReferenceHost = new() { Spacing = 5 };
    private readonly List<AiReferenceItem> aiReferences = [];
    private bool aiFloatingDismissed;
    private string? aiLastError;
    private Button? aiFloatingGenerate, aiFloatingRemove, aiFloatingMore;
    private readonly TextBlock aiFloatingStatus = new() { Foreground = Palette.Secondary, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 230 };

    private sealed class AiReferenceItem(SKBitmap pixels, Bitmap preview, string name) : IDisposable
    {
        public SKBitmap Pixels { get; } = pixels;
        public Bitmap Preview { get; } = preview;
        public string Name { get; } = name;
        public void Dispose() { Pixels.Dispose(); Preview.Dispose(); }
    }

    private Control BuildAiContextBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(aiActionHost);
        row.Children.Add(aiProgressHost);
        aiContextHost.Child = row;
        RefreshAiUi();
        return aiContextHost;
    }

    private bool CropExpands => session?.Tool == Tool.Crop && canvas.CropRect is { } crop &&
        (crop.Left < 0 || crop.Top < 0 || crop.Right > session.Document.Width || crop.Bottom > session.Document.Height);

    private bool CanRunAi(AiTaskKind task) =>
        AiTaskAvailability.Resolve(session, aiTasks.SelectedEngine, task, CropExpands).Available && aiTasks.Operation?.Status is not (AiOperationStatus.Queued or AiOperationStatus.Running);

    private void RefreshAiUi()
    {
        if (aiContextHost.Child == null) return;
        aiActionHost.Children.Clear();
        var contextual = new List<AiTaskKind>();
        if (session?.Selection != null) contextual.AddRange([AiTaskKind.GenerativeFill, AiTaskKind.RemoveObject]);
        else if (session?.ActiveLayer?.Pixels is { } pixels && IsEmpty(pixels)) contextual.Add(AiTaskKind.GenerateImage);
        if (CropExpands) contextual.Add(AiTaskKind.GenerativeExpand);
        foreach (var task in contextual.Distinct())
        {
            var availability = AiTaskAvailability.Resolve(session, aiTasks.SelectedEngine, task, CropExpands);
            var button = Ui.TextButton(task.DisplayName(), () => _ = RunAi(task), accent: task is AiTaskKind.GenerativeFill or AiTaskKind.GenerateImage);
            button.MinWidth = 0;
            button.IsEnabled = availability.Available && aiTasks.Operation?.Status is not (AiOperationStatus.Queued or AiOperationStatus.Running);
            if (availability.Reason != null) ToolTip.SetTip(button, availability.Reason);
            aiActionHost.Children.Add(button);
        }
        if (session != null) aiActionHost.Children.Add(BuildAiTaskMenu());

        aiProgressHost.Children.Clear();
        var operation = aiTasks.Operation;
        var active = operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running;
        if (operation != null)
        {
            var label = Ui.Label(operation.Error ?? operation.Stage ?? operation.Status.ToString(), operation.Status == AiOperationStatus.Failed ? Brushes.Orange : Palette.Secondary);
            label.MaxWidth = 190;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            aiProgressHost.Children.Add(label);
            if (active)
            {
                aiProgressHost.Children.Add(new ProgressBar
                {
                    Width = 110, Height = 6, IsIndeterminate = operation.IsIndeterminate,
                    Minimum = 0, Maximum = operation.Maximum ?? 1, Value = operation.Value ?? 0
                });
                var cancel = Ui.TextButton("Cancel", aiTasks.Cancel);
                cancel.MinWidth = 0;
                aiProgressHost.Children.Add(cancel);
            }
        }
        aiTaskText.IsVisible = active;
        aiTaskText.Text = active ? "AI: " + (operation?.Stage ?? "Working") : "";
        aiContextHost.IsVisible = session != null && (contextual.Count > 0 || active) && aiTasks.ConnectionState == ComfyConnectionState.Connected;
        RefreshAiFloatingUi();
    }

    private Button BuildAiTaskMenu()
    {
        Button? button = null;
        button = Ui.TextButton("AI ▾", () =>
        {
            var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (var task in Enum.GetValues<AiTaskKind>())
            {
                var availability = AiTaskAvailability.Resolve(session, aiTasks.SelectedEngine, task, CropExpands);
                var item = new MenuItem
                {
                    Header = task.DisplayName(),
                    IsEnabled = availability.Available && aiTasks.Operation?.Status is not (AiOperationStatus.Queued or AiOperationStatus.Running)
                };
                if (availability.Reason != null) ToolTip.SetTip(item, availability.Reason);
                item.Click += (_, _) => _ = RunAi(task);
                flyout.Items.Add(item);
            }
            flyout.ShowAt(button!);
        });
        button.MinWidth = 0;
        ToolTip.SetTip(button, "More AI tasks");
        return button;
    }

    private Control BuildAiFloatingMenu()
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        header.Children.Add(Ui.Label("Generative AI", weight: FontWeight.SemiBold));
        var advanced = Ui.TextButton("Advanced…", () => _ = ShowAiSettings());
        advanced.MinWidth = 0;
        AddAt(header, advanced, 1).Margin = new Thickness(0, 0, 5, 0);
        var close = Ui.TextButton("×", () => { aiFloatingDismissed = true; RefreshAiFloatingUi(); });
        close.MinWidth = 32;
        ToolTip.SetTip(close, "Close until the selection changes");
        AddAt(header, close, 2);

        aiFloatingGenerate = Ui.TextButton("Generate", () => _ = RunAi(AiTaskKind.GenerativeFill, aiFloatingPrompt.Text ?? "", useInlinePrompt: true), accent: true);
        aiFloatingRemove = Ui.TextButton("Remove", () => _ = RunAi(AiTaskKind.RemoveObject, useInlinePrompt: true));
        aiFloatingMore = Ui.TextButton("••• ▾", OpenAiFloatingTaskMenu);
        aiFloatingMore.MinWidth = 54;
        ToolTip.SetTip(aiFloatingMore, "More AI operations");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(aiFloatingMore);
        actions.Children.Add(aiFloatingRemove);
        actions.Children.Add(aiFloatingGenerate);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        footer.Children.Add(aiFloatingStatus);
        AddAt(footer, actions, 1);
        RefreshAiReferenceUi();
        return Ui.Column(9, header, aiFloatingPrompt, aiReferenceHost, footer);
    }

    private void OpenAiFloatingTaskMenu()
    {
        if (aiFloatingMore == null) return;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var task in Enum.GetValues<AiTaskKind>().Where(task => task is not (AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject)))
        {
            var availability = AiTaskAvailability.Resolve(session, aiTasks.SelectedEngine, task, CropExpands);
            var item = new MenuItem
            {
                Header = task.DisplayName(),
                IsEnabled = availability.Available && aiTasks.Operation?.Status is not (AiOperationStatus.Queued or AiOperationStatus.Running)
            };
            if (availability.Reason != null) ToolTip.SetTip(item, availability.Reason);
            item.Click += (_, _) => _ = RunAi(task, aiFloatingPrompt.Text ?? "", useInlinePrompt: true);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(aiFloatingMore);
    }

    private void RefreshAiFloatingUi()
    {
        var visible = session?.Selection != null && session.IsPaintingSelection == false
            && aiTasks.ConnectionState == ComfyConnectionState.Connected && !aiFloatingDismissed;
        aiFloatingHost.IsVisible = visible;
        if (!visible) return;
        var busy = aiTasks.Operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running;
        if (aiFloatingGenerate != null) aiFloatingGenerate.IsEnabled = !busy && AiTaskAvailability.Resolve(session, aiTasks.SelectedEngine, AiTaskKind.GenerativeFill, CropExpands).Available;
        if (aiFloatingRemove != null) aiFloatingRemove.IsEnabled = !busy && AiTaskAvailability.Resolve(session, aiTasks.SelectedEngine, AiTaskKind.RemoveObject, CropExpands).Available;
        if (aiFloatingMore != null) aiFloatingMore.IsEnabled = !busy && Enum.GetValues<AiTaskKind>().Any(task =>
            task is not (AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject) && AiTaskAvailability.Resolve(session, aiTasks.SelectedEngine, task, CropExpands).Available);
        var operation = aiTasks.Operation;
        aiFloatingStatus.Text = aiLastError ?? (operation?.Status switch
        {
            AiOperationStatus.Queued or AiOperationStatus.Running => operation.Stage ?? "Working…",
            AiOperationStatus.Completed => "Completed",
            AiOperationStatus.Cancelled => "Cancelled",
            AiOperationStatus.Failed => operation.Error ?? "AI operation failed",
            _ => ""
        });
        aiFloatingStatus.Foreground = aiLastError != null || operation?.Status == AiOperationStatus.Failed ? Brushes.Orange : Palette.Secondary;
        Avalonia.Threading.Dispatcher.UIThread.Post(RefreshAiFloatingPosition);
    }

    private void RefreshAiFloatingPosition()
    {
        if (!aiFloatingHost.IsVisible || session?.Selection is not { } selection) return;
        var bounds = SelectionMask.Bounds(selection);
        if (bounds.IsEmpty) return;
        var topLeft = canvas.ToScreen(new SKPoint(bounds.Left, bounds.Top));
        var bottomRight = canvas.ToScreen(new SKPoint(bounds.Right, bounds.Bottom));
        var width = Math.Max(340, Math.Min(520, canvas.Bounds.Width - 16));
        aiFloatingHost.Width = width;
        var height = Math.Max(190, aiFloatingHost.Bounds.Height);
        var maxLeft = Math.Max(8, canvas.Bounds.Width - width - 8);
        var left = Math.Clamp((topLeft.X + bottomRight.X - width) / 2, 8, maxLeft);
        var below = bottomRight.Y + 10;
        var top = below + height <= canvas.Bounds.Height - 8 ? below : topLeft.Y - height - 10;
        top = Math.Clamp(top, 8, Math.Max(8, canvas.Bounds.Height - height - 8));
        Avalonia.Controls.Canvas.SetLeft(aiFloatingHost, left);
        Avalonia.Controls.Canvas.SetTop(aiFloatingHost, top);
    }

    private async Task PickAiReferences()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Reference Images", AllowMultiple = true, FileTypeFilter = [ImageType]
        });
        AddAiReferencePaths(files.Select(file => file.TryGetLocalPath()).OfType<string>());
    }

    private void RefreshAiReferenceUi()
    {
        aiReferenceHost.Children.Clear();
        aiReferenceHost.Children.Add(Ui.Label($"References ({aiReferences.Count}/6)", Palette.Secondary));
        var items = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        foreach (var item in aiReferences.ToArray())
        {
            var preview = new Image { Source = item.Preview, Width = 52, Height = 52, Stretch = Stretch.UniformToFill };
            var remove = new Button
            {
                Content = "×", MinWidth = 22, Width = 22, Height = 22, Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.Parse("#CC292929")), Foreground = Brushes.White
            };
            remove.Click += (_, e) =>
            {
                e.Handled = true;
                aiReferences.Remove(item);
                item.Dispose();
                RefreshAiReferenceUi();
            };
            remove.SetValue(Panel.ZIndexProperty, 1);
            ToolTip.SetTip(remove, "Remove reference");
            var cell = new Grid { Width = 56, Height = 56 };
            cell.Children.Add(new Border { Child = preview, CornerRadius = new CornerRadius(6), ClipToBounds = true, BorderBrush = new SolidColorBrush(Color.Parse("#4A4A4A")), BorderThickness = new Thickness(1) });
            cell.Children.Add(remove);
            ToolTip.SetTip(cell, item.Name);
            items.Children.Add(cell);
        }
        if (aiReferences.Count < 6)
        {
            var add = Ui.TextButton("＋", () => _ = PickAiReferences());
            add.Width = 56;
            add.Height = 56;
            add.FontSize = 22;
            ToolTip.SetTip(add, aiReferences.Count == 0 ? "Add reference image · paste with Ctrl+V or drag files here" : "Add another reference image");
            items.Children.Add(add);
        }
        aiReferenceHost.Children.Add(items);
    }

    private void AddAiReferencePaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (aiReferences.Count >= 6) break;
            try { AddAiReference(ImageFiles.Load(path), Path.GetFileName(path)); }
            catch (Exception error) { ShowProblem($"Couldn't add {Path.GetFileName(path)}: {error.Message}"); }
        }
        RefreshAiReferenceUi();
    }

    private void AddAiReference(SKBitmap pixels, string name)
    {
        if (aiReferences.Count >= 6) { pixels.Dispose(); return; }
        try
        {
            using var image = SKImage.FromBitmap(pixels);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = new MemoryStream(encoded.ToArray());
            aiReferences.Add(new AiReferenceItem(pixels, new Bitmap(stream), name));
        }
        catch { pixels.Dispose(); throw; }
    }

    internal void AddAiReferenceForTests(SKBitmap pixels)
    {
        AddAiReference(pixels, "Test reference");
        RefreshAiReferenceUi();
    }

    private async Task<bool> PasteAiReference()
    {
        if (aiReferences.Count >= 6) return false;
        try
        {
            if (await ExternalClipboardBitmap() is { } bitmap)
            {
                using (bitmap)
                {
                    using var stream = new MemoryStream();
                    bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                    stream.Position = 0;
                    AddAiReference(ImageFiles.Load(stream, "clipboard"), "Clipboard image");
                }
            }
            else if (EditorSession.Clipboard is { } internalImage) AddAiReference(Pixels.Clone(internalImage.Pixels), "Clipboard image");
            else return false;
            RefreshAiReferenceUi();
            return true;
        }
        catch (Exception error) { ShowProblem("Couldn't paste the reference: " + error.Message); return false; }
    }

    private async Task PasteAiReferenceOrText(TextBox? textBox)
    {
        if (await PasteAiReference() || textBox == null || Clipboard == null) return;
        try
        {
            var text = await Clipboard.TryGetTextAsync();
            if (text == null) return;
            var current = textBox.Text ?? "";
            var start = Math.Min(textBox.SelectionStart, textBox.SelectionEnd);
            var end = Math.Max(textBox.SelectionStart, textBox.SelectionEnd);
            textBox.Text = current[..start] + text + current[end..];
            textBox.CaretIndex = start + text.Length;
        }
        catch { /* A clipboard that changed mid-paste is harmless. */ }
    }

    private void OnAiReferenceDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? [];
        AddAiReferencePaths(paths);
        e.Handled = true;
    }

    private async Task ShowAiSettings()
    {
        try
        {
            if (!await AiDialogs.SettingsDialog(this, settings, aiTasks)) return;
            aiTasks.ConnectionTimeoutSeconds = settings.ComfyConnectionTimeoutSeconds;
            aiTasks.SelectedEngine = aiTasks.Engines.Find(settings.AiEngineId);
            RefreshAiUi();
        }
        catch (Exception error) { ShowProblem(error.Message); }
    }

    private async Task EditLayerTags(Composa.Model.Layer layer)
    {
        if (session == null) return;
        if (await AiDialogs.LayerTags(this, layer, settings) is { } tags) session.SetLayerTags(layer, tags);
    }

    private async Task RunAi(AiTaskKind task, string initialPrompt = "", bool useInlinePrompt = false)
    {
        aiLastError = null;
        aiFloatingStatus.Text = "Starting…";
        var availability = AiTaskAvailability.Resolve(session, aiTasks.SelectedEngine, task, CropExpands);
        if (!availability.Available) { AiFailed(availability.Reason ?? "This AI task is unavailable."); return; }
        if (aiTasks.Operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running) { AiFailed("Another AI operation is already running."); return; }
        var aspect = AiAspect(task);
        var fallback = AiDimensions.FromMegapixels(settings.AiMegapixels, aspect.Width, aspect.Height);
        AiPromptResult? options;
        if (useInlinePrompt || task is AiTaskKind.RemoveObject or AiTaskKind.SelectSubject or AiTaskKind.ObjectSelection or AiTaskKind.Upscale or AiTaskKind.MatchToScene)
            options = new(initialPrompt, fallback.Width, fallback.Height, settings.AiSeed);
        else options = await AiDialogs.Prompt(this, task, settings, aspect.Width, aspect.Height, initialPrompt);
        if (options == null) return;
        if (session == null)
        {
            if (task != AiTaskKind.GenerateImage) return;
            AddSession(EditorSession.NewCanvas(options.Width, options.Height));
        }
        var seed = options.Seed < 0 ? Random.Shared.NextInt64(long.MaxValue) : options.Seed;
        try
        {
            var request = new AiTaskRequest
            {
                Task = task,
                Prompt = options.Prompt,
                ReferenceImages = useInlinePrompt ? aiReferences.Select(item => item.Pixels).ToList() : [],
                ReferenceMegapixels = settings.AiReferenceMegapixels,
                ExpansionBounds = task == AiTaskKind.GenerativeExpand && canvas.CropRect is { } crop
                    ? new SKRectI((int)Math.Floor(crop.Left), (int)Math.Floor(crop.Top), (int)Math.Ceiling(crop.Right), (int)Math.Ceiling(crop.Bottom)) : null,
                Settings = new AiGenerationSettings
                {
                    Width = options.Width, Height = options.Height, Seed = seed,
                    Values = new Dictionary<string, object?> { ["maskGrow"] = settings.AiMaskGrow, ["maskBlend"] = settings.AiMaskBlend,
                        ["maskContext"] = settings.AiMaskContext,
                        ["upscaleModel"] = settings.AiUpscalerModel }
                }
            };
            await aiTasks.RunAsync(new EditorCommandService(session!), request);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AiFailed(error.Message); }
    }

    private void AiFailed(string message)
    {
        aiLastError = message;
        ShowProblem(message);
        RefreshAiUi();
    }

    private (int Width, int Height) AiAspect(AiTaskKind task)
    {
        if (task == AiTaskKind.GenerativeExpand && canvas.CropRect is { Width: > 0, Height: > 0 } crop)
            return (Math.Max(1, (int)Math.Round(crop.Width)), Math.Max(1, (int)Math.Round(crop.Height)));
        if (task is AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject or AiTaskKind.ChangeBackground or AiTaskKind.Harmonize or AiTaskKind.Relight
            && session?.Selection is { } selection && SelectionMask.Bounds(selection) is { IsEmpty: false } bounds)
            return (bounds.Width, bounds.Height);
        return (session?.Document.Width ?? 1, session?.Document.Height ?? 1);
    }

    private static bool IsEmpty(SKBitmap bitmap) => bitmap.GetPixelSpan().IndexOfAnyExcept((byte)0) < 0;
}
