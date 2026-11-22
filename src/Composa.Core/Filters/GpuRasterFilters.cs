using System.Diagnostics;
using System.Runtime.InteropServices;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Filters;

/// <summary>Optional GPU compute for raster filters. A ready device is used only for work it can complete independently.</summary>
public static class GpuRasterFilters
{
    // Transfer and dispatch cost can exceed the CPU blur time for small images.
    internal const int MinimumPixels = 3_000_000;
    private static readonly object configuration = new();
    private static Task<OpenClFloatBlur?>? initialization;

    public static bool CanEnable => (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) && Environment.Is64BitProcess;

    internal static Task<OpenClFloatBlur?>? Initialization => Volatile.Read(ref initialization);

    public static void SetEnabled(bool enabled)
    {
        lock (configuration)
        {
            if (enabled)
            {
                if (CanEnable && initialization == null)
                    Volatile.Write(ref initialization, Task.Run(Initialize));
            }
            else if (initialization is { } previous)
            {
                Volatile.Write(ref initialization, null);
                _ = Task.Run(async () => (await previous.ConfigureAwait(false))?.Dispose());
            }
        }
    }

    internal static bool TryBlur(SKBitmap source, SKBitmap scratch, ReadOnlySpan<int> radii, bool clamp) =>
        (long)source.Width * source.Height >= MinimumPixels
        && Initialization is { IsCompletedSuccessfully: true, Result: { } backend }
        && backend.TryApply(source, scratch, radii, clamp);

    private static OpenClFloatBlur? Initialize()
    {
        OpenClFloatBlur? backend = null;
        try
        {
            backend = new OpenClFloatBlur();
            if (Validate(backend)) return backend;
            Trace.WriteLine($"GPU raster filters disabled: {backend.Device} failed the RGB16 reference check.");
        }
        catch (Exception error) when (error is ExternalException or DllNotFoundException
            or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            Trace.WriteLine($"GPU raster filters unavailable: {error.Message}");
        }
        backend?.Dispose();
        return null;
    }

    private static bool Validate(OpenClFloatBlur backend)
    {
        using var input = Pixels.NewColor(113, 83, ColorDepth.Sixteen);
        using var cpuScratch = Pixels.NewLike(input);
        using var output = Pixels.NewLike(input);
        var pixels = new PixelBuffer(input);
        for (var y = 0; y < input.Height; y++)
        for (var x = 0; x < input.Width; x++)
        {
            var alpha = x < 5 ? 0 : ((x * 23 + y * 17) % 101) / 100f;
            var scale = x % 11 == 0 ? 1e-38f : 1;
            pixels.Write(x, y, new(alpha * scale * (x / 113f), alpha * scale * (y / 83f), alpha * .37f, alpha));
        }

        ReadOnlySpan<int> radii = [8, 9, 9];
        foreach (var clamp in new[] { false, true })
        {
            using var reference = Pixels.NewLike(input);
            input.GetPixelSpan().CopyTo(reference.GetPixelSpan());
            FloatGaussian.ApplyBoxes(reference, cpuScratch, radii, clamp);
            if (!backend.TryApply(input, output, radii, clamp)
                || !reference.GetPixelSpan().SequenceEqual(output.GetPixelSpan())) return false;
        }
        return true;
    }
}
