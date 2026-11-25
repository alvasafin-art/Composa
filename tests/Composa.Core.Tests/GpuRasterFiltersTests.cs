using Composa.ColorManagement;
using Composa.Filters;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

[CollectionDefinition("GPU raster filters", DisableParallelization = true)]
public sealed class GpuRasterFiltersCollection;

[Collection("GPU raster filters")]
public sealed class GpuRasterFiltersTests
{
    [Fact]
    public void SmallCpuStrokesAndFitDoNotOpenTheGpuDevice()
    {
        GpuAcceleration.Configure(filters: false, editor: true);
        try
        {
            using var input = Pixels.NewColor(256, 256, ColorDepth.Sixteen);
            using var stroke = new BrushStroke(input, new BrushSettings { Size = 52 }, BrushMode.Paint, SKColors.CornflowerBlue);
            using var drawn = stroke.Working;
            stroke.AddPoint(new SKPoint(60, 70));
            var doc = new Document(256, 256) { Depth = ColorDepth.Sixteen };
            doc.InsertAboveActive(Layer.Raster("Paint", drawn));
            using var target = Pixels.NewColor(128, 128);
            DocumentRenderer.Render(doc, target, target.Info.Rect, new RenderView(.5f, SKPoint.Empty));
            Assert.Null(GpuAcceleration.Initialization);
        }
        finally { GpuAcceleration.Configure(filters: false, editor: false); }
    }

    [Fact]
    public async Task LayerEffectBlurMatchesSkiaIncludingIntegerOffsets()
    {
        if (Environment.GetEnvironmentVariable("COMPOSA_TEST_RASTER_GPU") != "1") return;
        GpuAcceleration.Configure(filters: true, editor: true);
        try
        {
            var device = Assert.IsType<OpenClDevice>(await GpuAcceleration.Initialization!);
            Assert.True(device.PaintAvailable, "GPU paint did not pass the CPU reference check.");
            Assert.True(device.CoverageAvailable, "GPU brush coverage did not pass the CPU reference check.");
            Assert.True(device.CompositeAvailable, "GPU compositing did not pass the Skia reference check.");
            using var source = Pixels.NewMask(643, 391);
            using var expected = Pixels.NewLike(source);
            using var actual = Pixels.NewLike(source);
            new Random(489).NextBytes(source.GetPixelSpan());
            foreach (var (sigma, dx, dy) in new[] { (5f, 0, 0), (14f, 0, 0), (41f, 0, 43), (96f, 17, -21), (128f, -18, 16) })
            {
                expected.Erase(SKColors.Transparent);
                LayerEffectsRenderer.DrawShifted(source, expected, dx, dy, sigma);
                Assert.True(GpuAcceleration.TryLayerEffectBlur(source, actual, dx, dy, sigma));
                Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()), $"sigma {sigma}, offset ({dx}, {dy})");
            }
            Assert.False(GpuAcceleration.TryLayerEffectBlur(source, actual, .25f, 0, 41));
        }
        finally { GpuAcceleration.Configure(filters: false, editor: false); }
    }

    [Fact]
    public async Task LargeInsideAndOutsideLayerStrokesMatchRasterSkia()
    {
        if (Environment.GetEnvironmentVariable("COMPOSA_TEST_RASTER_GPU") != "1") return;
        GpuAcceleration.Configure(filters: true, editor: true);
        try
        {
            var device = Assert.IsType<OpenClDevice>(await GpuAcceleration.Initialization!);
            Assert.True(device.StrokeRingAvailable);
            var capabilities = GpuAcceleration.OpenCl;
            Assert.True(capabilities.Probed);
            Assert.Equal(device.Device, capabilities.Device);
            Assert.True(capabilities.LayerStroke);
            Assert.Equal(device.FloatBlurAvailable, capabilities.Rgb16Blur);
            using var source = Pixels.NewMask(563, 419);
            using var expected = Pixels.NewLike(source);
            using var actual = Pixels.NewLike(source);
            new Random(427).NextBytes(source.GetPixelSpan());
            foreach (var inside in new[] { false, true })
            foreach (var reach in new[] { 9, 31, 78 })
            {
                LayerEffectsRenderer.DrawStrokeRing(source, expected, reach, inside);
                Assert.True(GpuAcceleration.TryStrokeRing(source, actual, reach, inside));
                Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()), $"reach {reach}, inside {inside}");
            }
        }
        finally { GpuAcceleration.Configure(filters: false, editor: false); }
    }

    [Fact]
    public async Task WideMaskAndRgb16StrokesMatchEveryLiveDab()
    {
        if (Environment.GetEnvironmentVariable("COMPOSA_TEST_RASTER_GPU") != "1") return;
        GpuAcceleration.Configure(filters: true, editor: true);
        try
        {
            var device = Assert.IsType<OpenClDevice>(await GpuAcceleration.Initialization!);
            Assert.True(device.CoverageAvailable);
            using var mask = Pixels.NewMask(803, 691, 191);
            using var color = Pixels.NewWorkingColor(mask.Width, mask.Height, ColorDepth.Sixteen, RgbProfile.DisplayP3);
            color.Erase(new SKColor(157, 212, 77, 189));
            var settings = new BrushSettings { Size = 632, Hardness = .69, Flow = .23, Opacity = .57, PressureFlow = true };
            foreach (var source in new[] { mask, color })
            foreach (var mode in new[] { BrushMode.Paint, BrushMode.Erase })
            {
                using var cpu = new BrushStroke(source, settings, mode, new SKColor(194, 71, 150, 201))
                    { LockTransparency = source == color };
                using var actual = new BrushStroke(source, settings, mode, new SKColor(194, 71, 150, 201))
                    { LockTransparency = source == color };
                using var expectedImage = cpu.Working;
                using var actualImage = actual.Working;
                foreach (var (point, pressure) in new[] { (new SKPoint(140.3f, 217.7f), .98f), (new SKPoint(297.45f, 311.78f), .81f), (new SKPoint(570.71f, 451.6f), 1f) })
                {
                    GpuAcceleration.Configure(filters: true, editor: false);
                    var expectedDirty = cpu.AddPoint(point, pressure);
                    GpuAcceleration.Configure(filters: true, editor: true);
                    Assert.Equal(expectedDirty, actual.AddPoint(point, pressure));
                    Assert.True(expectedImage.GetPixelSpan().SequenceEqual(actualImage.GetPixelSpan()));
                }
            }
        }
        finally { GpuAcceleration.Configure(filters: false, editor: false); }
    }

    [Fact]
    public async Task CombinedGpuDabMatchesPaintAndEraseAcrossOverlappingDabs()
    {
        if (Environment.GetEnvironmentVariable("COMPOSA_TEST_RASTER_GPU") != "1") return;
        GpuAcceleration.Configure(filters: true, editor: true);
        try
        {
            var device = Assert.IsType<OpenClDevice>(await GpuAcceleration.Initialization!);
            using var source = Pixels.NewColor(908, 721);
            using var expected = Pixels.NewLike(source);
            using var actual = Pixels.NewLike(source);
            source.Erase(new SKColor(73, 137, 191, 201));
            var dabArea = new SKRectI(0, 0, 886, 704);
            var settings = new BrushSettings { Size = 1100, Hardness = .996, Opacity = .68, Flow = .43 };
            foreach (var mode in new[] { BrushMode.Paint, BrushMode.Erase })
            {
                using var cpu = new BrushStroke(source, settings, mode, SKColors.DarkOrange);
                using var gpu = new BrushStroke(source, settings, mode, SKColors.DarkOrange);
                using var cpuPixels = cpu.Working;
                using var gpuPixels = gpu.Working;
                foreach (var point in new[] { new SKPoint(451.375f, 352.125f), new SKPoint(571.75f, 459.25f) })
                {
                    GpuAcceleration.Configure(filters: true, editor: false);
                    var changed = cpu.AddPoint(point);
                    GpuAcceleration.Configure(filters: true, editor: true);
                    Assert.Equal(changed, gpu.AddPoint(point));
                    Assert.True(cpuPixels.GetPixelSpan().SequenceEqual(gpuPixels.GetPixelSpan()));
                }

                // Exercise the combined device call even on a CPU for which the interactive path may prefer SIMD.
                var coverage = new StrokeCoverage(source.Width, source.Height);
                var combinedCoverage = new StrokeCoverage(source.Width, source.Height);
                float[] falloff = [1, .75f, .25f, 0];
                var color = mode == BrushMode.Erase ? new SKColorF(0, 0, 0, 0) : new SKColorF(60, 110, 134, 175);
                Assert.True(device.TryBrushCoverage(coverage, dabArea, new(440, 340), 440, .75f, falloff, .43f, out _));
                Assert.True(device.TryPaint(source, expected, dabArea, coverage, color, .68f));
                Assert.True(device.TryBrushCoverage(combinedCoverage, dabArea, new(440, 340), 440, .75f, falloff, .43f,
                    out var changedOnGpu, new(source, actual, color, .68f)));
                Assert.True(changedOnGpu);
                Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()));
            }
        }
        finally { GpuAcceleration.Configure(filters: false, editor: false); }
    }

    [Fact]
    public async Task GpuCanvasPreservesProfileAndPixelRowsWithCachedInputs()
    {
        if (Environment.GetEnvironmentVariable("COMPOSA_TEST_RASTER_GPU") != "1") return;
        var profile = RgbProfile.DisplayP3;
        using var bottom = Pixels.NewWorkingColor(1361, 1050, ColorDepth.Sixteen, profile);
        using var top = Pixels.NewWorkingColor(950, 811, ColorDepth.Sixteen, profile);
        bottom.Erase(new SKColor(163, 90, 28, 245));
        top.Erase(new SKColor(17, 222, 146, 134));
        var document = new Document(bottom.Width, bottom.Height) { Depth = ColorDepth.Sixteen, Profile = profile };
        document.InsertAboveActive(Layer.Raster("Bottom", bottom));
        var movable = Layer.Raster("Top", top, 101, 72);
        document.InsertAboveActive(movable);
        using var expected = Pixels.NewLike(bottom);
        using var actual = Pixels.NewLike(bottom);
        var area = new SKRectI(31, 33, 1310, 1014);
        var view = new RenderView(1, new(8, 10));
        GpuAcceleration.Configure(filters: true, editor: false);
        try
        {
            var device = Assert.IsType<OpenClDevice>(await GpuAcceleration.Initialization!);
            Assert.True(device.CompositeAvailable);
            DocumentRenderer.Render(document, expected, area, view);
            GpuAcceleration.Configure(filters: true, editor: true);
            Assert.True(SpinWait.SpinUntil(() => DocumentRenderer.TryRenderGpu(document, actual, area, view, null), TimeSpan.FromSeconds(8)));
            Assert.Equal(profile, Pixels.ProfileOf(actual));
            Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()));

            Assert.False(DocumentRenderer.TryRenderGpu(document, actual, area, view with { Scale = .3f }, null));
            Assert.False(DocumentRenderer.TryRenderGpu(document, actual, new(31, 33, 1087, 905), view, null));
            movable.Clipped = true;
            Assert.False(DocumentRenderer.TryRenderGpu(document, actual, area, view, null));
            device.ClearCompositeInputs();
        }
        finally { GpuAcceleration.Configure(filters: false, editor: false); }
    }

    [Fact]
    public async Task SameRgb16PixelsAndProfileWithGpu()
    {
        // A release workstation can require real hardware without making it a CI prerequisite.
        if (Environment.GetEnvironmentVariable("COMPOSA_TEST_RASTER_GPU") != "1") return;

        using var source = Pixels.NewWorkingColor(2080, 1500, ColorDepth.Sixteen, RgbProfile.Rgb1998);
        var input = new PixelBuffer(source);
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var border = x == 0 || y == 0 || x == source.Width - 1 || y == source.Height - 1;
            var a = border ? 1 : ((x * 13 + y * 7) % 1009) / 1008f;
            input.Write(x, y, new(a * x / source.Width, a * y / source.Height, a / 3, a));
        }

        GpuAcceleration.Configure(filters: false, editor: false);
        var settings = new FilterSettings { Kind = FilterKind.GaussianBlur, Radius = 32, ClampEdges = true };
        using var expected = ImageFilters.Run(source, settings).Result;
        GpuAcceleration.Configure(filters: true, editor: false);
        try
        {
            var backend = await GpuAcceleration.Initialization!;
            Assert.NotNull(backend);
            using var scratch = Pixels.NewLike(source);
            Assert.True(GpuAcceleration.TryBlur(source, scratch, new[] { 31, 32, 32 }, clamp: true));
            var (result, left, top) = ImageFilters.Run(source, settings);
            using var actual = result;
            Assert.Equal(0, left);
            Assert.Equal(0, top);
            Assert.Equal(RgbProfile.Rgb1998, Pixels.ProfileOf(actual));
            Assert.Equal(ColorDepth.Sixteen, Pixels.DepthOf(actual));
            Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()));
        }
        finally { GpuAcceleration.Configure(filters: false, editor: false); }
    }
}
