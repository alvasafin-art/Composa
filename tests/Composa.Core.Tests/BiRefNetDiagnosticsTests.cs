using System.Diagnostics;
using Composa.IO;
using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Core.Tests;

public class BiRefNetDiagnosticsTests
{
    [Fact]
    public void Optional_official_model_runs_a_real_portrait_locally()
    {
        if (Environment.GetEnvironmentVariable("COMPOSA_TEST_BIREFNET") is not { Length: > 0 } modelPath) return;
        var sourcePath = Environment.GetEnvironmentVariable("COMPOSA_TEST_BIREFNET_IMAGE")!;
        var output = Environment.GetEnvironmentVariable("COMPOSA_TEST_BIREFNET_OUTPUT")!;
        var model = SubjectModels.BiRefNet with { File = modelPath };
        Assert.True(model.Verify()); Assert.True(ModelRunner.CanRun(model));
        using var photo = ImageFiles.Load(sourcePath);
        var clock = Stopwatch.StartNew();
        using var mask = SubjectMatting.Matte(photo, model, CancellationToken.None);
        Assert.Equal(photo.Width, mask.Width); Assert.Equal(photo.Height, mask.Height);
        Assert.False(SelectionMask.Bounds(mask, 128).IsEmpty);
        var bytes = mask.GetPixelSpan().ToArray(); Assert.Contains(bytes, b => b < 5); Assert.Contains(bytes, b => b > 250);
        using var shown = Pixels.Clone(photo); using (var canvas = new SKCanvas(shown)) using (var keep = new SKPaint { BlendMode = SKBlendMode.DstIn }) canvas.DrawBitmap(mask, 0, 0, keep);
        Directory.CreateDirectory(output); File.WriteAllBytes(Path.Combine(output, "portrait-cutout.png"), ImageFiles.Encode(shown, ExportFormat.Png));
        File.WriteAllText(Path.Combine(output, "native-validation.json"), System.Text.Json.JsonSerializer.Serialize(new { elapsedSeconds = clock.Elapsed.TotalSeconds,
            width = mask.Width, height = mask.Height, bounds = SelectionMask.Bounds(mask, 128).ToString(), peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64, model.Sha256 }));
    }
}
