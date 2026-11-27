using Composa.AI;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.AI;

internal sealed record DirectImageFiles(IReadOnlyList<byte[]> Images, byte[]? Mask = null);

/// <summary>Uses the editor's context crop and finishing, with a model-sized frame shared by the source and mask.</summary>
internal sealed class DirectImageEdit : IDisposable
{
    private readonly PartnerImageInputs inputs;
    private readonly SKRectI frame;
    private readonly SKRectI content;

    internal int Width => frame.Width;
    internal int Height => frame.Height;
    internal bool ReachesCanvasEdge { get; }

    internal SKBitmap? OutputMask() => inputs.OutputMask();

    internal string RegionPromptFor(string prompt, AiTaskRequest request) =>
        RegionPrompt.MaskedPrompt(prompt, request, inputs.SourceBounds, content, new SKSizeI(Width, Height));

    internal DirectImageEdit(AiTaskInputs prepared, AiTaskRequest request, DirectImageGenerator? generator = null)
    {
        inputs = new PartnerImageInputs(prepared, request, planPartnerOutput: false);
        var source = inputs.Images["apiSource"];
        var (width, height) = generator?.GenerationSize(inputs.DesiredSize.Width, inputs.DesiredSize.Height)
            ?? DirectImageGenerator.RequestSize(source.Width, source.Height);
        frame = SKRectI.Create(width, height);
        var scale = Math.Min((double)width / source.Width, (double)height / source.Height);
        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));
        content = SKRectI.Create((width - w) / 2, (height - h) / 2, w, h);
        var canvas = prepared.ContextImage.Info.Rect;
        var selection = prepared.TargetBounds;
        ReachesCanvasEdge = request.Task == AiTaskKind.GenerativeFill && !selection.IsEmpty &&
            (selection.Left == canvas.Left || selection.Top == canvas.Top || selection.Right == canvas.Right || selection.Bottom == canvas.Bottom);
    }

    internal DirectImageFiles Encode(AiTaskInputs prepared, SKBitmap? selection, bool nativeMask, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
        using var source = Pixels.NewColor(Width, Height);
        using (var canvas = new SKCanvas(source))
        {
            canvas.Clear(SKColors.Transparent);
            DrawModelFrame(canvas, Pixels.ImageOf(inputs.Images["apiSource"]), sampling);
        }
        var images = new List<byte[]> { EncodePng(source) };
        foreach (var reference in prepared.ReferenceImages)
        {
            token.ThrowIfCancellationRequested();
            images.Add(EncodePng(reference));
        }
        if (selection == null) return new(images);

        using var coverage = Pixels.NewMask(Width, Height);
        using (var canvas = new SKCanvas(coverage))
        {
            canvas.Clear(SKColors.Transparent);
            var maskSampling = new SKSamplingOptions(SKFilterMode.Nearest);
            if (ReachesCanvasEdge && content != frame)
            {
                using var cropped = Pixels.ImageOf(selection).Subset(inputs.SourceBounds);
                DrawModelFrame(canvas, cropped, maskSampling);
            }
            else canvas.DrawImage(Pixels.ImageOf(selection), inputs.SourceBounds, content, maskSampling);
        }
        using var mask = Pixels.NewColor(Width, Height);
        using (var canvas = new SKCanvas(mask))
        {
            canvas.Clear(SKColors.Black);
            using var paint = new SKPaint { Color = SKColors.White, BlendMode = nativeMask ? SKBlendMode.DstOut : SKBlendMode.SrcOver };
            canvas.DrawImage(Pixels.ImageOf(coverage), 0, 0, paint);
        }
        var encoded = EncodePng(mask);
        if (nativeMask) return new(images, encoded);
        images.Add(encoded);
        return new(images);
    }

    private void DrawModelFrame(SKCanvas canvas, SKImage image, SKSamplingOptions sampling)
    {
        if (!ReachesCanvasEdge || content == frame)
        {
            canvas.DrawImage(image, content, sampling);
            return;
        }
        // Technical padding should continue the edge pixels and their edit coverage.
        // A protected empty band otherwise looks like an in-image boundary to GPT Image.
        var matrix = SKMatrix.CreateScale((float)content.Width / image.Width, (float)content.Height / image.Height);
        matrix.TransX = content.Left;
        matrix.TransY = content.Top;
        using var shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling, matrix);
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
        canvas.DrawRect(frame, paint);
    }

    internal SKBitmap Finish(SKBitmap generated)
    {
        using (generated)
        {
            var ratioError = Math.Abs((double)generated.Width / generated.Height / ((double)Width / Height) - 1);
            if (ratioError > 0.025)
                throw new InvalidDataException("The model returned a different aspect ratio. The edit was discarded.");
            var crop = inputs.SourceBounds;
            var fitted = Pixels.NewColor(crop.Width, crop.Height);
            try
            {
                var area = new SKRect((float)content.Left / Width * generated.Width, (float)content.Top / Height * generated.Height,
                    (float)content.Right / Width * generated.Width, (float)content.Bottom / Height * generated.Height);
                using var canvas = new SKCanvas(fitted);
                canvas.DrawImage(Pixels.ImageOf(generated), area, SKRect.Create(crop.Width, crop.Height), new SKSamplingOptions(SKCubicResampler.Mitchell));
            }
            catch { fitted.Dispose(); throw; }
            return inputs.FinishUnmasked(fitted);
        }
    }

    internal static byte[] EncodePng(SKBitmap bitmap)
    {
        using var encoded = Pixels.ImageOf(bitmap).Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException("Composa could not encode an image input.");
        return encoded.ToArray();
    }

    public void Dispose() => inputs.Dispose();
}
