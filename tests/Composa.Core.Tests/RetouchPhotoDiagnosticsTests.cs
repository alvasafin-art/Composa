using System.Text.Json;
using Composa.Editing;
using Composa.IO;
using Composa.Painting;
using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Core.Tests;

/// <summary>Opt-in local photographs: private fixtures and resulting images never enter release assets.</summary>
public class RetouchPhotoDiagnosticsTests
{
    [Fact]
    public async Task Local_photo_masks_and_spot_repairs()
    {
        if (Environment.GetEnvironmentVariable("COMPOSA_RETOUCH_PHOTO_DIR") is not { Length: > 0 } input) return;
        var output = Environment.GetEnvironmentVariable("COMPOSA_RETOUCH_PHOTO_OUTPUT") ?? Path.Combine(Path.GetTempPath(), "composa-retouch-diagnostics");
        Directory.CreateDirectory(output);
        using var portrait = ImageFiles.Load(Path.Combine(input, "portrait.jpeg"));
        var metrics = new List<object>();
        foreach (var (name, fx, fy) in new[] { ("sky", .16, .15), ("skin", .69, .37), ("fabric", .43, .51) })
        {
            int x = (int)(portrait.Width * fx), y = (int)(portrait.Height * fy);
            using var dirty = Pixels.Clone(portrait); using var mask = Pixels.NewMask(portrait.Width, portrait.Height);
            using (var canvas = new SKCanvas(dirty)) using (var paint = new SKPaint { Color = new SKColor(12, 8, 4), IsAntialias = true }) canvas.DrawCircle(x, y, 4, paint);
            using (var canvas = new SKCanvas(mask)) using (var paint = new SKPaint { Color = SKColors.White, IsAntialias = true }) canvas.DrawCircle(x, y, 12, paint);
            using var old = Inpaint.Fill(dirty, mask); using var healed = Inpaint.Fill(dirty, mask, coherentSpot: true);
            int count = 0; double oldError = 0, newError = 0, sourceTexture = 0, newTexture = 0;
            for (var py = 0; py < portrait.Height; py++) for (var px = 0; px < portrait.Width; px++)
            {
                if (mask.GetPixel(px, py).Alpha == 0) { Assert.Equal(dirty.GetPixel(px, py), healed.GetPixel(px, py)); continue; }
                var a = portrait.GetPixel(px, py); var b = old.GetPixel(px, py); var c = healed.GetPixel(px, py);
                oldError += (Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue)) / 3.0;
                newError += (Math.Abs(a.Red - c.Red) + Math.Abs(a.Green - c.Green) + Math.Abs(a.Blue - c.Blue)) / 3.0;
                if (px + 1 < portrait.Width)
                { sourceTexture += Math.Abs(a.Red - portrait.GetPixel(px + 1, py).Red); newTexture += Math.Abs(c.Red - healed.GetPixel(px + 1, py).Red); }
                count++;
            }
            metrics.Add(new { name, oldError = oldError / count, newError = newError / count, textureRatio = newTexture / Math.Max(1, sourceTexture) });
            using var comparison = Pixels.NewColor(160 * 4, 160);
            using (var canvas = new SKCanvas(comparison))
                foreach (var (image, i) in new[] { (portrait, 0), (dirty, 1), (old, 2), (healed, 3) })
                    canvas.DrawBitmap(image, new SKRect(x - 40, y - 40, x + 40, y + 40), new SKRect(i * 160, 0, (i + 1) * 160, 160));
            ImageFiles.Save(comparison, Path.Combine(output, name + "-source-dirty-old-new.png"), ExportFormat.Png);
        }
        foreach (var model in new[] { PromptModels.MobileSam, PromptModels.EfficientSamTi, PromptModels.EfficientSamS })
        {
            var session = EditorSession.NewCanvas(portrait.Width, portrait.Height);
            session.AddImageLayer("Photo", Pixels.Clone(portrait), fit: false);
            var region = new SKRectI((int)(portrait.Width * .065), (int)(portrait.Height * .17), (int)(portrait.Width * .79), (int)(portrait.Height * .98));
            await session.SelectPromptObjectAsync(model, null, region, SelectionMode.Replace);
            var mask = session.Selection!; var bounds = SelectionMask.Bounds(mask, 128);
            Assert.False(bounds.IsEmpty); Assert.Equal((byte)0, mask.GetPixel(5, 5).Alpha);
            var pixels = mask.GetPixelSpan().ToArray(); session.Undo(); session.Redo(); Assert.Equal(pixels, session.Selection!.GetPixelSpan().ToArray());
            using var overlay = Pixels.Clone(portrait);
            for (var y = 0; y < overlay.Height; y++) for (var x = 0; x < overlay.Width; x++)
                if (mask.GetPixel(x, y).Alpha >= 128)
                { var color = overlay.GetPixel(x, y); overlay.SetPixel(x, y, new SKColor((byte)((color.Red + 255) / 2), (byte)(color.Green / 2), (byte)((color.Blue + 180) / 2))); }
            using var small = Pixels.NewColor(536, 960);
            using (var canvas = new SKCanvas(small)) canvas.DrawBitmap(overlay, new SKRect(0, 0, 536, 960));
            ImageFiles.Save(small, Path.Combine(output, model.Kind + "-selection.png"), ExportFormat.Png);
        }
        File.WriteAllText(Path.Combine(output, "metrics.json"), JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
    }
}
