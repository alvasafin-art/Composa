using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    private string? editingAlphaChannel;
    private readonly Layer alphaChannelProxy = Layer.Group("Alpha Channel");
    public string? EditingAlphaChannel => editingAlphaChannel != null && document.AlphaChannels.ContainsKey(editingAlphaChannel) ? editingAlphaChannel : null;
    public bool IsEditingAlphaChannel => EditingAlphaChannel != null;
    public RgbChannels EditingRgbChannels { get; private set; } = RgbChannels.All;
    public RgbChannels VisibleRgbChannels { get; private set; } = RgbChannels.All;
    public bool AlphaChannelOverlay { get; private set; }
    public bool IsEditingComponent => !IsEditingMask && EditingRgbChannels != RgbChannels.All;
    public Layer? PixelEditLayer => IsEditingAlphaChannel ? alphaChannelProxy : ActiveLayer;
    public event Action? ChannelsChanged;

    private void ResetChannelTarget()
    {
        if (editingAlphaChannel == null && EditingRgbChannels == RgbChannels.All && VisibleRgbChannels == RgbChannels.All) return;
        editingAlphaChannel = null; EditingRgbChannels = VisibleRgbChannels = RgbChannels.All; AlphaChannelOverlay = false;
        CanvasChanged?.Invoke(null); ChannelsChanged?.Invoke();
    }

    public void EditRgbChannels(RgbChannels channels = RgbChannels.All, bool changeView = true)
    {
        if (channels == RgbChannels.None || (channels & ~RgbChannels.All) != 0) throw new ArgumentException("Select at least one RGB channel.");
        FinishInteraction();
        if (editingAlphaChannel == null && !IsEditingMask && EditingRgbChannels == channels && (!changeView || VisibleRgbChannels == channels) && !AlphaChannelOverlay) return;
        editingAlphaChannel = null; EditingMask = false; EditingRgbChannels = channels;
        if (changeView) VisibleRgbChannels = channels;
        AlphaChannelOverlay = false; CanvasChanged?.Invoke(null); ChannelsChanged?.Invoke(); LayersChanged?.Invoke();
    }

    public void EditAlphaChannel(string name, bool overlay = false)
    {
        if (!document.AlphaChannels.ContainsKey(name)) throw new ArgumentException("This alpha channel does not exist.");
        FinishInteraction();
        if (EditingAlphaChannel?.Equals(name, StringComparison.OrdinalIgnoreCase) == true && AlphaChannelOverlay == overlay) return;
        EditingMask = false; editingAlphaChannel = name; EditingRgbChannels = RgbChannels.All;
        AlphaChannelOverlay = overlay; CanvasChanged?.Invoke(null); ChannelsChanged?.Invoke(); LayersChanged?.Invoke();
    }

    public void SetChannelView(RgbChannels visible, bool alphaOverlay = false)
    {
        if ((visible & ~RgbChannels.All) != 0) throw new ArgumentException("Invalid RGB channel visibility.");
        var overlay = IsEditingAlphaChannel && alphaOverlay;
        if (VisibleRgbChannels == visible && AlphaChannelOverlay == overlay) return;
        VisibleRgbChannels = visible; AlphaChannelOverlay = overlay;
        CanvasChanged?.Invoke(null); ChannelsChanged?.Invoke();
    }

    public void AddAlphaChannel(string name, bool fromSelection = false, bool revealAll = false)
    {
        FinishText(); name = ChannelName(name);
        if (document.AlphaChannels.ContainsKey(name)) throw new ArgumentException("An alpha channel already has that name.");
        if (document.AlphaChannels.Count >= 64) throw new InvalidOperationException("The project already has 64 alpha channels.");
        if (document.RasterPixels() + ((long)document.Width * document.Height + 3) / 4 > DocumentLimits.DocumentPixelBudget)
            throw new InvalidOperationException("This alpha channel exceeds the document raster budget.");
        var mask = fromSelection && document.Selection != null ? Pixels.Clone(document.Selection) : Pixels.NewMask(document.Width, document.Height);
        if (!fromSelection && revealAll) mask.Erase(SKColors.White);
        Apply("New Alpha Channel", () => document.AlphaChannels.Add(name, mask)); EditAlphaChannel(name); LayersChanged?.Invoke();
    }

    public void RenameAlphaChannel(string name, string replacement)
    {
        replacement = ChannelName(replacement); if (!document.AlphaChannels.TryGetValue(name, out var pixels)) return;
        if (name == replacement) return;
        if (!name.Equals(replacement, StringComparison.OrdinalIgnoreCase) && document.AlphaChannels.ContainsKey(replacement)) throw new ArgumentException("An alpha channel already has that name.");
        Apply("Rename Alpha Channel", () =>
        {
            var channels = document.AlphaChannels.Select(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase) ? new KeyValuePair<string, SKBitmap>(replacement, pixels) : pair).ToArray();
            document.AlphaChannels.Clear(); foreach (var pair in channels) document.AlphaChannels.Add(pair.Key, pair.Value);
        });
        if (editingAlphaChannel?.Equals(name, StringComparison.OrdinalIgnoreCase) == true) editingAlphaChannel = replacement;
        ChannelsChanged?.Invoke(); LayersChanged?.Invoke();
    }

    private static string ChannelName(string name)
    {
        name = name.Trim(); if (name.Length is 0 or > 200 || name.Contains('\0')) throw new ArgumentException("An alpha channel name needs 1–200 characters.");
        return name;
    }

    public void DuplicateAlphaChannel(string name)
    {
        if (!document.AlphaChannels.TryGetValue(name, out var mask)) return;
        if (document.AlphaChannels.Count >= 64) throw new InvalidOperationException("The project already has 64 alpha channels.");
        var stem = name.Length > 180 ? name[..180] : name; var copy = stem + " copy";
        for (var n = 2; document.AlphaChannels.ContainsKey(copy); n++) copy = stem + " copy " + n;
        Apply("Duplicate Alpha Channel", () => document.AlphaChannels.Add(copy, mask)); EditAlphaChannel(copy);
    }

    public void MoveAlphaChannel(string name, int direction)
    {
        var channels = document.AlphaChannels.ToList(); var index = channels.FindIndex(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return; var to = Math.Clamp(index + Math.Sign(direction), 0, channels.Count - 1); if (index == to) return;
        (channels[index], channels[to]) = (channels[to], channels[index]);
        Apply("Reorder Alpha Channels", () => { document.AlphaChannels.Clear(); foreach (var pair in channels) document.AlphaChannels.Add(pair.Key, pair.Value); });
        ChannelsChanged?.Invoke(); LayersChanged?.Invoke();
    }

    private SKColor ChannelPaintColor(SKColor color)
    {
        if (!IsEditingComponent) return color;
        var gray = (byte)((color.Red * 54 + color.Green * 183 + color.Blue * 19) >> 8);
        return new(gray, gray, gray, color.Alpha);
    }

    private unsafe Layer? PasteChannelPixels(ClipboardImage? image, bool inPlace)
    {
        if (image == null || EditableLayer is not { } layer) return null;
        Apply("Paste to Channel", () =>
        {
            var original = Target(layer); using var converted = PastePixels(image.Pixels);
            using var gray = Pixels.Grayscale(converted); using var mask = IsEditingAlphaChannel ? Pixels.NewMask(gray.Width, gray.Height) : null;
            if (mask != null)
            {
                var pixels = new PixelBuffer(gray); var output = (byte*)mask.GetPixels();
                Parallel.For(0, mask.Height, y =>
                {
                    for (var x = 0; x < mask.Width; x++) output[y * mask.RowBytes + x] = (byte)Math.Clamp(Math.Round(pixels.Read(x, y).R * 255), 0, 255);
                });
            }
            var placed = new SKRectI(image.Origin.X, image.Origin.Y, image.Origin.X + gray.Width, image.Origin.Y + gray.Height);
            var fits = inPlace ? document.Bounds.IntersectsWith(placed) : document.Bounds.Contains(placed);
            var selectionBounds = !inPlace && document.Selection != null ? SelectionMask.Bounds(document.Selection) : document.Bounds;
            var x = !inPlace && document.Selection != null || !fits ? (selectionBounds.Left + selectionBounds.Right - gray.Width) / 2f : image.Origin.X;
            var y = !inPlace && document.Selection != null || !fits ? (selectionBounds.Top + selectionBounds.Bottom - gray.Height) / 2f : image.Origin.Y;
            var pasted = Pixels.Clone(original);
            if (TargetMatrix(layer).TryInvert(out var inverse))
            {
                using var canvas = new SKCanvas(pasted); canvas.SetMatrix(in inverse);
                using var paint = new SKPaint { BlendMode = mask != null ? SKBlendMode.Src : SKBlendMode.SrcOver };
                canvas.DrawBitmap(mask ?? gray, x, y, paint);
            }
            var selection = SelectionInTargetSpace(layer);
            SetTarget(layer, MixBySelection(original, pasted, selection));
            if (selection != document.Selection) selection?.Dispose();
        });
        InvalidateAll(); ChannelsChanged?.Invoke(); LayersChanged?.Invoke(); return layer;
    }

    public void SaveSelection(string name)
    {
        FinishText(); name = name.Trim();
        if (document.Selection == null || name.Length is 0 or > 200) return;
        if (document.AlphaChannels.Count >= 64 && !document.AlphaChannels.ContainsKey(name)) throw new InvalidOperationException("The project already has 64 saved selections.");
        if (!document.AlphaChannels.ContainsKey(name) && document.RasterPixels() + (long)document.Width * document.Height > DocumentLimits.DocumentPixelBudget)
            throw new InvalidOperationException("Saved selection exceeds the document raster budget.");
        Apply("Save Selection", () => document.AlphaChannels[name] = document.Selection);
        LayersChanged?.Invoke(); ChannelsChanged?.Invoke();
    }
    public void LoadSelection(string name, SelectionMode mode = SelectionMode.Replace)
    {
        if (!document.AlphaChannels.TryGetValue(name, out var mask)) return;
        Select(mask.Width == document.Width && mask.Height == document.Height ? mask : Resample(mask, document.Width, document.Height), mode, "Load Selection");
    }

    public unsafe void LoadRgbChannel(RgbChannels channel, SelectionMode mode = SelectionMode.Replace)
    {
        if (!Pixels.IsSingleChannel(channel)) throw new ArgumentException("Choose one RGB channel.");
        using var composite = CompositePixels(); var pixels = new PixelBuffer(composite); var mask = Pixels.NewMask(document.Width, document.Height);
        var output = (byte*)mask.GetPixels();
        Parallel.For(0, mask.Height, y =>
        {
            for (var x = 0; x < mask.Width; x++)
            {
                var p = pixels.Read(x, y); var value = channel == RgbChannels.Red ? p.R : channel == RgbChannels.Green ? p.G : p.B;
                output[y * mask.RowBytes + x] = (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
            }
        });
        Select(mask, mode, "Load Channel Selection");
    }
    public void DeleteSelectionChannel(string name)
    {
        if (!document.AlphaChannels.ContainsKey(name)) return;
        Apply("Delete Selection Channel", () => document.AlphaChannels.Remove(name)); LayersChanged?.Invoke();
        if (editingAlphaChannel?.Equals(name, StringComparison.OrdinalIgnoreCase) == true) EditRgbChannels();
        ChannelsChanged?.Invoke();
    }
}
