using System.Text.Json.Nodes;
using Composa.AI;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.AI;

/// <summary>Exports decoded pixels, not a baked stitch. The saved ROI and local layer mask share source coordinates.</summary>
internal sealed class EditableMaskedWorkflow : IDisposable
{
    internal SKRectI Bounds { get; }
    internal SKBitmap Mask { get; }
    private readonly AiTaskInputs inputs;
    private readonly AiTaskRequest request;
    private readonly int grow;
    private readonly int blend;

    internal EditableMaskedWorkflow(AiTaskInputs inputs, AiTaskRequest request)
    {
        this.inputs = inputs; this.request = request;
        grow = request.Task is AiTaskKind.GenerativeFill or AiTaskKind.GenerativeExpand ? 0 : Value("maskGrow", 16);
        blend = Value("maskBlend", 48);
        var sourceMask = request.Task == AiTaskKind.GenerativeExpand ? inputs.PreprocessedMask!
            : request.Task == AiTaskKind.ChangeBackground ? inputs.BackgroundMask! : inputs.PreprocessedMask ?? inputs.SelectionMask!;
        var area = request.Task == AiTaskKind.GenerativeExpand ? inputs.PreprocessedImage!.Info.Rect : inputs.ContextImage.Info.Rect;
        Bounds = AiContextGeometry.Flux(SelectionMask.Bounds(sourceMask, 1), area, grow, blend,
            Convert.ToDouble(request.Settings.Values.GetValueOrDefault("maskContext") ?? 2));
        if (Bounds.IsEmpty) throw new InvalidOperationException("The edit mask is empty.");
        if (request.Task == AiTaskKind.GenerativeExpand)
        {
            using var context = inputs.ExpandedContext();
            Mask = AiResultPostprocessor.ExpansionEditMask(inputs.OutputMask ?? sourceMask, context, blend);
        }
        else if (request.Task == AiTaskKind.RemoveObject && inputs.OutputMask != null) Mask = Pixels.Clone(inputs.OutputMask);
        else Mask = AiResultPostprocessor.EditMask(sourceMask, grow, blend);
    }

    private int Value(string name, int fallback) => Math.Clamp(Convert.ToInt32(request.Settings.Values.GetValueOrDefault(name) ?? fallback), 0, 64);

    internal void Bind(JsonObject graph)
    {
        var pixaroma = graph["crop"]!["class_type"]!.GetValue<string>() == "PixaromaInpaintCrop";
        var crop = graph["crop"]!["inputs"]!.AsObject();
        var image = crop["image"]!.DeepClone(); var mask = crop["mask"]!.DeepClone();
        var original = Convert.ToBoolean(request.Settings.Values.GetValueOrDefault("imageOriginalSize") ?? false);
        var width = inputs.CanvasWidth; var height = inputs.CanvasHeight;
        if (request.Task == AiTaskKind.GenerativeExpand)
            (width, height) = AiDimensions.FromMinimumSide(request.ExpansionMinimumSide, Bounds.Width, Bounds.Height);
        else if (original) { width = Bounds.Width; height = Bounds.Height; }
        else
        {
            var scale = Math.Sqrt((double)width * height / Bounds.Width / Bounds.Height);
            width = Math.Max(1, (int)Math.Round(Bounds.Width * scale)); height = Math.Max(1, (int)Math.Round(Bounds.Height * scale));
        }
        if (Math.Min(width, height) < 64) (width,height) = AiDimensions.FromMinimumSide(64,width,height);
        width = Math.Max(16, (int)Math.Round(width / 16.0) * 16); height = Math.Max(16, (int)Math.Round(height / 16.0) * 16);
        if (!Composa.Model.DocumentLimits.FitsSurface(width,height)) throw new InvalidOperationException("The generated context exceeds the image surface limit.");
        JsonObject Node(string type, JsonObject values) => new() { ["class_type"] = type, ["inputs"] = values };
        graph["composa_edit_crop"] = Node("ImageCrop", new() { ["image"] = image, ["x"] = Bounds.Left, ["y"] = Bounds.Top, ["width"] = Bounds.Width, ["height"] = Bounds.Height });
        graph["composa_edit_size"] = Node("ImageScale", new() { ["image"] = new JsonArray("composa_edit_crop", 0), ["width"] = width, ["height"] = height, ["upscale_method"] = "lanczos", ["crop"] = "disabled" });
        graph["composa_edit_mask_grow"] = Node("GrowMask", new() { ["mask"] = mask, ["expand"] = grow, ["tapered_corners"] = true });
        graph["composa_edit_mask_crop"] = Node("CropMask", new() { ["mask"] = new JsonArray("composa_edit_mask_grow", 0), ["x"] = Bounds.Left, ["y"] = Bounds.Top, ["width"] = Bounds.Width, ["height"] = Bounds.Height });
        graph["composa_edit_mask_image"] = Node("MaskToImage", new() { ["mask"] = new JsonArray("composa_edit_mask_crop", 0) });
        graph["composa_edit_mask_size"] = Node("ImageScale", new() { ["image"] = new JsonArray("composa_edit_mask_image", 0), ["width"] = width, ["height"] = height, ["upscale_method"] = "bilinear", ["crop"] = "disabled" });
        var blur = Value("maskBlur", 16);
        var last = "composa_edit_mask_size";
        if (blur > 0)
        {
            graph["composa_edit_mask_blur"] = Node("ImageBlur", new() { ["image"] = new JsonArray(last, 0), ["blur_radius"] = Math.Clamp(blur * 3, 1, Math.Min(31, Math.Min(width, height) - 1)), ["sigma"] = Math.Min(10, Math.Max(0.1, blur / 3.0)) });
            last = "composa_edit_mask_blur";
        }
        graph["composa_edit_mask"] = Node("ImageToMask", new() { ["image"] = new JsonArray(last, 0), ["channel"] = "red" });
        foreach (var (_, node) in graph)
            if (node?["inputs"] is JsonObject fields)
                foreach (var key in fields.Select(pair => pair.Key).ToArray())
                    if (fields[key] is JsonArray link && link[0]?.GetValue<string>() == "crop")
                    {
                        var slot = link[1]!.GetValue<int>();
                        fields[key] = new JsonArray(slot == (pixaroma ? 0 : 1) ? "composa_edit_size" : "composa_edit_mask", 0);
                    }
        // Sampling is larger than final coverage. Its halo/conditioning never becomes layer alpha.
        graph["composa_sampling_mask"]!["inputs"]!["expand"] = (int)Math.Ceiling((grow + blend) * Math.Max((double)width / Bounds.Width, (double)height / Bounds.Height)) + 3 * blur;
        graph["save"]!["inputs"]!["images"] = new JsonArray("decode", 0);
        graph.Remove("stitch"); graph.Remove("crop");
    }

    internal SKBitmap Finish(SKBitmap decoded)
    {
        using (decoded)
        {
            var result = request.Task == AiTaskKind.GenerativeExpand ? inputs.ExpandedContext() : Pixels.Clone(inputs.ContextImage);
            using (var canvas = new SKCanvas(result))
            using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
            {
                if (decoded.Width == Bounds.Width && decoded.Height == Bounds.Height) canvas.DrawImage(Pixels.ImageOf(decoded),Bounds.Left,Bounds.Top,paint);
                else canvas.DrawImage(Pixels.ImageOf(decoded), new SKRect(Bounds.Left, Bounds.Top, Bounds.Right, Bounds.Bottom),
                    new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
            }
            Pixels.Invalidate(result);
            if (request.Settings.Values.GetValueOrDefault("colorMatch")?.ToString() is "subtle" or "strong"
                && request.Task is not (AiTaskKind.GenerativeExpand or AiTaskKind.ChangeBackground))
            {
                var matched = AiResultPostprocessor.MatchRemoval(result,inputs.ContextImage,Mask,inputs.Seed);
                result.Dispose(); return matched;
            }
            return result;
        }
    }
    public void Dispose() => Mask.Dispose();
}
