using System.Text.Json;
using System.Text.Encodings.Web;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.AI;

public static class RegionPrompt
{
    public static void Draw(SKBitmap source, IEnumerable<PromptRegion> regions)
    {
        using var canvas = new SKCanvas(source);
        using var pen = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(2, Math.Min(source.Width, source.Height) / 240f) };
        using var font = new SKFont(SKTypeface.Default, 5 * pen.StrokeWidth);
        foreach (var region in regions)
        {
            pen.Color = region.Ink.Color;
            canvas.DrawRect(region.Bounds, pen);
            if (region.Number <= PromptRegion.Palette.Length) continue;

            // A generated color may look close to its neighbors. The higher IDs carry a
            // number in the guide as well, so the prompt can refer to an unambiguous mark.
            var label = region.Number.ToString();
            var metrics = font.Metrics;
            var width = Math.Min(source.Width, font.MeasureText(label) + 4 * pen.StrokeWidth);
            var height = Math.Min(source.Height, metrics.Descent - metrics.Ascent + 2 * pen.StrokeWidth);
            var box = SKRect.Create(Math.Min(region.Left, source.Width - width),
                Math.Clamp(region.Top - height, 0, source.Height - height), width, height);
            pen.Style = SKPaintStyle.Fill;
            canvas.DrawRect(box, pen);
            pen.Color = region.Ink.Color.Red + region.Ink.Color.Green > 320 ? SKColors.Black : SKColors.White;
            canvas.DrawText(label, box.MidX, box.MidY - (metrics.Ascent + metrics.Descent) / 2, SKTextAlign.Center, font, pen);
            pen.Style = SKPaintStyle.Stroke;
        }
        Pixels.Invalidate(source);
    }

    public static string OverlayInstructions(AiTaskRequest request) =>
        string.Join("\n", request.Regions.Select(r => r.Number <= PromptRegion.Palette.Length
            ? $"Inside the {r.Ink.Name} rectangle: {r.Prompt}"
            : $"Inside the rectangle marked {r.Number} ({r.Ink.Name}): {r.Prompt}"))
        + "\nRemove all annotation lines from the final image.";

    // BFL's final caption (after the upsampler) uses integer YXYX coordinates on a 1000-unit grid.
    public static string Coordinates(string prompt, PromptRegion[] regions, SKRectI frame,
        double widthFraction, double heightFraction, bool editing, bool fluxCaption = true)
    {
        var items = new List<object>();
        var phrases = new List<string> { prompt.Trim() };
        foreach (var region in regions)
        {
            var rect = SKRectI.Intersect(frame, region.Bounds);
            if (rect.IsEmpty) throw new InvalidOperationException($"AI region {region.Number} is outside the input frame.");
            int X(int x) => (int)Math.Round(1000d * (x - frame.Left) / frame.Width * widthFraction);
            int Y(int y) => (int)Math.Round(1000d * (y - frame.Top) / frame.Height * heightFraction);
            int[] box = [Y(rect.Top), X(rect.Left), Y(rect.Bottom), X(rect.Right)];
            if (!fluxCaption)
            {
                items.Add(new { region = region.Number, bbox = box, instruction = region.Prompt.Trim() });
                continue;
            }
            var id = $"object_{region.Number}";
            phrases.Add($"<{id}>: {region.Prompt.Trim()}");
            if (editing) items.Add(new { id, from = "ref_image_0", src_bbox = box, tgt_bbox = box, desc = region.Prompt });
            else items.Add(new { id, bbox = box, desc = region.Prompt });
        }
        if (!fluxCaption) phrases.Add("Apply each instruction inside its bbox. Coordinates are [top, left, bottom, right] on a 0–1000 grid, measured from the top left.");
        return string.Join(" ", phrases) + " " + JsonSerializer.Serialize(items, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
