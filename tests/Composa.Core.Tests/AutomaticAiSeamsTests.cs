using Composa.AI;
using Composa.Editing;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Core.Tests;

public class AutomaticAiSeamsTests
{
    [Fact]
    public void Flat_background_drift_is_corrected_without_erasing_real_relighting()
    {
        using var source = Pixels.NewColor(192, 192); source.Erase(new SKColor(200, 200, 200));
        using var core = SelectionMask.FromRect(192, 192, new SKRect(56, 56, 136, 136));
        using var mask = AutomaticAiMask.OutputMask(core, source, new(source.Info.Rect, 3, 32));
        using var generated = Pixels.Clone(source);
        for (var y = 56; y < 136; y++) for (var x = 56; x < 136; x++)
        {
            var value = (byte)(187 + (x + y) % 3); generated.SetPixel(x, y, new SKColor(value, value, value));
        }
        using var result = AiSeamlessFinisher.Match(generated, source, mask, source.Info.Rect, 3);
        using var relight = AiSeamlessFinisher.Match(generated, source, mask, source.Info.Rect, 3, matchFlatBackground: false);
        Assert.InRange(result.GetPixel(96, 96).Red, 198, 202);
        Assert.InRange(relight.GetPixel(96, 96).Red, 186, 190);
        Assert.Equal(source.GetPixel(0, 0), result.GetPixel(0, 0));
    }

    [Fact]
    public void Opacity_and_soft_skirts_do_not_weaken_the_generation_core()
    {
        using var soft = Pixels.NewMask(40, 40);
        using var faint = Pixels.NewMask(40, 40);
        for (var y = 8; y < 32; y++) for (var x = 8; x < 32; x++)
        {
            var value = x is >= 10 and < 30 && y is >= 10 and < 30 ? 255 : 64;
            soft.GetPixelSpan()[y * soft.RowBytes + x] = (byte)value;
            faint.GetPixelSpan()[y * faint.RowBytes + x] = (byte)(value / 3);
        }
        using var a = AutomaticAiMask.Normalize(soft); using var b = AutomaticAiMask.Normalize(faint);
        Assert.Equal(a.GetPixelSpan().ToArray(), b.GetPixelSpan().ToArray());
        Assert.Equal(new SKRectI(10, 10, 30, 30), SelectionMask.Bounds(a));
        Assert.Equal(85, faint.GetPixel(20, 20).Alpha);
    }

    [Fact]
    public void Legacy_controls_are_ignored_including_script_requests_and_remove_preprocessing()
    {
        var session = EditorSession.NewCanvas(160, 100, SKColors.White); session.SelectRect(new SKRect(60, 30, 100, 70));
        var request = new AiTaskRequest { Task = AiTaskKind.RemoveObject, RemoveObject = new() { Dilation = 64, Feather = 64 },
            Settings = new() { Values = new() { ["MASKBLEND"] = 64, ["maskGrow"] = 64, ["maskBlur"] = 64,
                ["maskContext"] = 8.0, ["gptContextPadding"] = 1024, ["colorMatch"] = "off", ["fluxMemory"] = "reduced" } } };
        var clean = AutomaticAiMask.IgnoreLegacyControls(request);
        Assert.Single(clean.Settings.Values); Assert.Equal("reduced", clean.Settings.Values["fluxMemory"]);
        using var a = AiTaskInputPreparer.Prepare(session, request);
        using var b = AiTaskInputPreparer.Prepare(session, clean);
        Assert.Equal(a.MaskPlan, b.MaskPlan);
        Assert.Equal(a.PreprocessedMask!.GetPixelSpan().ToArray(), b.PreprocessedMask!.GetPixelSpan().ToArray());
        Assert.Equal(a.OutputMask!.GetPixelSpan().ToArray(), b.OutputMask!.GetPixelSpan().ToArray());
        Assert.Equal(new SKRectI(60, 30, 100, 70), SelectionMask.Bounds(a.PreprocessedMask));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(-30)]
    public void Smooth_exposure_error_is_removed_at_the_seam_without_repainting_the_interior(int bias)
    {
        using var reference = Pixels.NewColor(192, 192); reference.Erase(new SKColor(110, 120, 130));
        using var generated = Pixels.Clone(reference); generated.Erase(new SKColor((byte)(110 + bias), (byte)(120 + bias), (byte)(130 + bias)));
        using (var draw = new SKCanvas(generated)) using (var paint = new SKPaint { Color = new SKColor(220, 40, 50) })
            draw.DrawRect(new SKRect(80, 80, 112, 112), paint);
        using var core = SelectionMask.FromRect(192, 192, new SKRect(40, 40, 152, 152));
        using var mask = AutomaticAiMask.OutputMask(core, reference, new(new(0, 0, 192, 192), 4, 32));
        using var finished = AiSeamlessFinisher.Match(generated, reference, mask, reference.Info.Rect, 4);
        using var plain = AiResultPostprocessor.Constrain(generated, reference, mask);
        using var composite = AiResultPostprocessor.Constrain(finished, reference, mask);
        var before = Math.Abs(plain.GetPixel(40, 96).Red - reference.GetPixel(40, 96).Red);
        var after = Math.Abs(composite.GetPixel(40, 96).Red - reference.GetPixel(40, 96).Red);
        Assert.True(after < before / 2.0, $"Seam error {before} -> {after}");
        Assert.InRange(Math.Abs(finished.GetPixel(96, 96).Red - 220), 0, 3);
        Assert.Equal(reference.GetPixel(0, 0), composite.GetPixel(0, 0));
        Assert.Equal((byte)(110 + bias), generated.GetPixel(0, 0).Red);
    }

    [Fact]
    public void Different_biases_on_opposite_sides_are_matched_locally_and_texture_survives()
    {
        using var source = Pixels.NewColor(128, 128); using var generated = Pixels.NewColor(128, 128);
        for (var y = 0; y < 128; y++) for (var x = 0; x < 128; x++)
        {
            var value = 100 + x / 4 + (x % 2 == 0 ? 6 : -6); var bias = (x - 64) / 2;
            source.SetPixel(x, y, new SKColor((byte)value, (byte)value, (byte)value));
            generated.SetPixel(x, y, new SKColor((byte)(value + bias), (byte)(value + bias), (byte)(value + bias)));
        }
        using var core = SelectionMask.FromRect(128, 128, new SKRect(24, 24, 104, 104));
        using var mask = AutomaticAiMask.OutputMask(core, source, new(source.Info.Rect, 3, 24));
        using var result = AiSeamlessFinisher.Match(generated, source, mask, source.Info.Rect, 3);
        foreach (var x in new[] { 24, 25, 102, 103 }) Assert.InRange(Math.Abs(result.GetPixel(x, 64).Red - source.GetPixel(x, 64).Red), 0, 6);
        Assert.InRange(Math.Abs(result.GetPixel(64, 64).Red - result.GetPixel(65, 64).Red), 10, 14);
    }

    [Fact]
    public void Confident_small_registration_restores_texture_alignment()
    {
        using var source = Pixels.NewColor(128, 128); using var generated = Pixels.NewColor(128, 128);
        var random = new Random(17);
        for (var y = 0; y < 128; y++) for (var x = 0; x < 128; x++)
        {
            var value = (byte)random.Next(60, 180); source.SetPixel(x, y, new SKColor(value, value, value));
        }
        for (var y = 0; y < 128; y++) for (var x = 0; x < 128; x++) generated.SetPixel(x, y, source.GetPixel(Math.Clamp(x - 2, 0, 127), Math.Clamp(y + 1, 0, 127)));
        using var core = SelectionMask.FromRect(128, 128, new SKRect(48, 48, 80, 80));
        using var mask = AutomaticAiMask.OutputMask(core, source, new(source.Info.Rect, 2, 32));
        using var result = AiSeamlessFinisher.Match(generated, source, mask, source.Info.Rect, 2);
        Assert.Equal(source.GetPixel(49, 64), result.GetPixel(49, 64));
        Assert.Equal(source.GetPixel(79, 64), result.GetPixel(79, 64));
    }

    [Fact]
    public void Translucent_pixels_outside_the_request_are_protected_and_premultiplication_is_valid()
    {
        using var source = Pixels.NewColor(48, 48); source.Erase(new SKColor(120, 100, 80, 120));
        using var core = SelectionMask.FromRect(48, 48, new SKRect(16, 16, 32, 32));
        var plan = AutomaticAiMask.Analyze(core, source);
        using var mask = AutomaticAiMask.OutputMask(core, source, plan);
        Assert.Equal(0, mask.GetPixel(15, 24).Alpha);
        using var generated = Pixels.NewColor(48, 48); generated.Erase(new SKColor(200, 100, 50, 180));
        using var result = AiSeamlessFinisher.Match(generated, source, mask, source.Info.Rect, plan.SeamWidth);
        Assert.Equal(source.GetPixel(15, 24), result.GetPixel(15, 24));
        var bytes = result.GetPixelSpan();
        for (var y = 0; y < 48; y++) for (var x = 0; x < 48; x++) for (var c = 0; c < 3; c++)
            Assert.True(bytes[y * result.RowBytes + x * 4 + c] <= bytes[y * result.RowBytes + x * 4 + 3]);
    }
}
