using Composa.ColorManagement;
using Composa.Filters;
using Composa.Model;
using Composa.Rendering;

namespace Composa.Core.Tests;

[CollectionDefinition("GPU raster filters", DisableParallelization = true)]
public sealed class GpuRasterFiltersCollection;

[Collection("GPU raster filters")]
public sealed class GpuRasterFiltersTests
{
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

        GpuRasterFilters.SetEnabled(false);
        var settings = new FilterSettings { Kind = FilterKind.GaussianBlur, Radius = 32, ClampEdges = true };
        using var expected = ImageFilters.Run(source, settings).Result;
        GpuRasterFilters.SetEnabled(true);
        try
        {
            var backend = await GpuRasterFilters.Initialization!;
            Assert.NotNull(backend);
            using var scratch = Pixels.NewLike(source);
            Assert.True(GpuRasterFilters.TryBlur(source, scratch, new[] { 31, 32, 32 }, clamp: true));
            var (result, left, top) = ImageFilters.Run(source, settings);
            using var actual = result;
            Assert.Equal(0, left);
            Assert.Equal(0, top);
            Assert.Equal(RgbProfile.Rgb1998, Pixels.ProfileOf(actual));
            Assert.Equal(ColorDepth.Sixteen, Pixels.DepthOf(actual));
            Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()));
        }
        finally { GpuRasterFilters.SetEnabled(false); }
    }
}
