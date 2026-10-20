using Composa.ColorManagement;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    private sealed class ChannelTransformState(Layer owner, SKBitmap original, Layer proxy, SKBitmap source, SKBitmap basis, SKMatrix ownerInverse, SKBitmap? selection)
    {
        public Layer Owner { get; } = owner;
        public SKBitmap Original { get; } = original;
        public Layer Proxy { get; } = proxy;
        public SKBitmap Source { get; } = source;
        public SKBitmap Basis { get; } = basis;
        public SKMatrix OwnerInverse { get; } = ownerInverse;
        public SKBitmap? Selection { get; } = selection;
        public SKBitmap? Preview, SelectionPreview;
        public bool Inspector;
        public void Dispose(bool keep)
        {
            Pixels.Invalidate(Source); Source.Dispose(); Basis.Dispose();
            if (Preview != null) { Pixels.SetLive(Preview, false); Pixels.Invalidate(Preview); }
            if (!keep) { Preview?.Dispose(); SelectionPreview?.Dispose(); }
        }
    }

    private ChannelTransformState? channelTransform;
    private Layer? channelControls;
    private static readonly Lazy<SKBitmap> ChannelControlPixel = new(() => { var pixel = Pixels.NewColor(1, 1); pixel.Erase(SKColors.White); return pixel; });
    public bool IsEditingChannel => IsEditingAlphaChannel || IsEditingComponent;

    /// <summary>A transient control frame, never a document layer or a mutable committed bitmap.</summary>
    public Layer? ChannelControlLayer
    {
        get
        {
            if (!IsEditingChannel || EditableLayer is not { } owner) return null;
            if (channelTransform != null) return channelTransform.Proxy;
            var pixels = IsEditingAlphaChannel ? EditingMaskBitmap! : owner.Pixels!;
            var bounds = Pixels.ComponentBounds(pixels, EditingRgbChannels); if (bounds.IsEmpty) bounds = new(0, 0, pixels.Width, pixels.Height);
            var matrix = SKMatrix.CreateScale(bounds.Width, bounds.Height).PostConcat(SKMatrix.CreateTranslation(bounds.Left, bounds.Top)).PostConcat(TargetMatrix(owner));
            channelControls ??= Layer.Raster("Channel", ChannelControlPixel.Value);
            channelControls.Transform = LayerTransform.FromMatrix(matrix, 1, 1);
            return channelControls;
        }
    }

    public LayerTransform ChannelControlTransform => channelTransform != null && Transform is { } edit
        ? new() { X = Math.Min(edit.Frame.Left, edit.Frame.Right), Y = Math.Min(edit.Frame.Top, edit.Frame.Bottom), Width = Math.Abs(edit.Frame.Width), Height = Math.Abs(edit.Frame.Height), Rotation = edit.Rotation }
        : document.Selection is { } selection ? FrameOf(SelectionMask.Bounds(selection)) : ChannelControlLayer?.ControlTransform ?? LayerTransform.Identity(document.Width, document.Height);

    private static LayerTransform FrameOf(SKRectI bounds) => new() { X = bounds.Left, Y = bounds.Top, Width = Math.Max(1, bounds.Width), Height = Math.Max(1, bounds.Height) };

    private unsafe TransformEdit? BeginChannelTransform(string name, bool duplicate)
    {
        FinishInteraction();
        if (EditableLayer is not { } owner) return null;
        var frame = document.Selection is { } selection ? FrameOf(SelectionMask.Bounds(selection)) : null;
        Begin(duplicate ? "Duplicate Channel Contents" : name.Contains("Channel", StringComparison.Ordinal) ? name : name + " Channel");
        SKBitmap? source = null, basis = null;
        try
        {
            var original = Target(owner); var matrix = TargetMatrix(owner);
            if (!matrix.TryInvert(out var inverse)) { Cancel(); return null; }
            var coverage = SelectionInTargetSpace(owner);
            using var ownedCoverage = coverage != document.Selection ? coverage : null;
            var alpha = original.ColorType == SKColorType.Alpha8;
            var depth = alpha ? ColorDepth.Eight : Pixels.DepthOf(original); var profile = alpha ? RgbProfile.Srgb : Pixels.ProfileOf(original);
            source = Pixels.NewWorkingColor(original.Width, original.Height, depth, profile);
            basis = Pixels.NewLike(source);
            var input = alpha ? default : new PixelBuffer(original); var picked = new PixelBuffer(source); var below = new PixelBuffer(basis);
            var mask = alpha ? (byte*)original.GetPixels() : null;
            var allowed = coverage == null ? null : (byte*)coverage.GetPixels(); var allowedStride = coverage?.RowBytes ?? 0;
            var width = original.Width; var height = original.Height; var stride = original.RowBytes; var channels = EditingRgbChannels;
            Parallel.For(0, height, y =>
            {
                for (var x = 0; x < width; x++)
                {
                    var p = alpha ? new PixelValue(mask[y * stride + x] / 255f, mask[y * stride + x] / 255f, mask[y * stride + x] / 255f, 1) : input.Read(x, y).Straight;
                    var amount = allowed == null ? 1 : allowed[y * allowedStride + x] / 255f;
                    // The plane's alpha is edit coverage, independent of channel intensity. Black must overwrite too.
                    var hasValue = alpha ? p.R > 0 : channels.HasFlag(RgbChannels.Red) && p.R > 0 || channels.HasFlag(RgbChannels.Green) && p.G > 0 || channels.HasFlag(RgbChannels.Blue) && p.B > 0;
                    var sourceAlpha = allowed != null || hasValue ? amount : 0;
                    picked.Write(x, y, new(p.R * sourceAlpha, p.G * sourceAlpha, p.B * sourceAlpha, sourceAlpha));
                    var keep = duplicate ? 1 : 1 - amount;
                    below.Write(x, y, new(p.R * keep, p.G * keep, p.B * keep, 1));
                }
            });
            var proxy = Layer.Raster("Channel", source); proxy.Transform = IsEditingAlphaChannel ? LayerTransform.Identity(source.Width, source.Height) : owner.Transform;
            channelTransform = new(owner, original, proxy, source, basis, inverse, document.Selection);
            Transform = new TransformEdit(this, [proxy], [], frameOverride: frame);
            return Transform;
        }
        catch { source?.Dispose(); basis?.Dispose(); channelTransform = null; Transform = null; Cancel(); throw; }
    }

    internal unsafe void UpdateChannelTransformPreview(TransformEdit edit)
    {
        if (Transform != edit || channelTransform is not { } state) return;
        var previous = state.Preview; var previousSelection = state.SelectionPreview;
        if (!edit.HasChanges)
        {
            SetTarget(state.Owner, state.Original, constrainChannels: false); document.Selection = state.Selection;
            state.Preview = state.SelectionPreview = null;
        }
        else
        {
            var matrix = state.Proxy.Matrix.PostConcat(state.OwnerInverse);
            var result = Pixels.Clone(state.Basis);
            using (var canvas = new SKCanvas(result))
            {
                canvas.SetMatrix(in matrix);
                canvas.DrawImage(Pixels.ImageOf(state.Source), 0, 0, DocumentRenderer.SamplingFor(matrix));
            }
            if (state.Original.ColorType == SKColorType.Alpha8)
            {
                var mask = Pixels.NewMask(result.Width, result.Height); var output = (byte*)mask.GetPixels(); var pixels = new PixelBuffer(result);
                var width = pixels.Width; var height = pixels.Height; var stride = mask.RowBytes;
                Parallel.For(0, height, y =>
                {
                    for (var x = 0; x < width; x++) output[y * stride + x] = (byte)Math.Clamp(Math.Round(pixels.Read(x, y).R * 255), 0, 255);
                });
                result.Dispose(); result = mask;
            }
            else Pixels.ConstrainChannels(result, state.Original, EditingRgbChannels);
            Pixels.SetLive(result, true); SetTarget(state.Owner, result, constrainChannels: false); state.Preview = result;
            if (state.Selection != null)
            {
                var delta = state.OwnerInverse.PostConcat(state.Proxy.Matrix);
                state.SelectionPreview = SelectionMask.Remap(state.Selection, document.Width, document.Height, delta);
                document.Selection = state.SelectionPreview;
            }
        }
        if (previous != null) { Pixels.SetLive(previous, false); Pixels.Invalidate(previous); previous.Dispose(); }
        previousSelection?.Dispose(); InvalidateAll(); SelectionChanged?.Invoke();
    }

    private bool EndChannelTransform(bool keep)
    {
        if (channelTransform is not { } state || Transform == null) return false;
        keep &= Transform.HasChanges; Transform = null; channelTransform = null; channelControls = null;
        if (keep) Commit(); else Cancel();
        state.Dispose(keep); InvalidateAll(); LayersChanged?.Invoke(); SelectionChanged?.Invoke(); return true;
    }

    public void PreviewChannelControlTransform(LayerTransform control)
    {
        if (!IsEditingChannel) throw new InvalidOperationException("Choose an alpha channel or specific RGB components before transforming channel contents.");
        if (!new[] { control.X, control.Y, control.Width, control.Height, control.Rotation }.All(double.IsFinite) || control.Width <= 0 || control.Height <= 0)
            throw new ArgumentException("A channel transform must be finite and have a positive size.");
        if (channelTransform == null && TransformEdit.Same(ChannelControlTransform, control)) return;
        var edit = channelTransform == null ? BeginChannelTransform("Transform Channel", duplicate: false) : Transform;
        if (edit == null || channelTransform == null) return;
        channelTransform.Inspector = true;
        edit.Set(SKRect.Create((float)control.X, (float)control.Y, (float)control.Width, (float)control.Height), control.Rotation);
    }

    public void CommitChannelControlEdit() { if (channelTransform?.Inspector == true) CommitTransform(); }
    public void SetChannelControlTransform(LayerTransform control) { PreviewChannelControlTransform(control); CommitChannelControlEdit(); }
}
