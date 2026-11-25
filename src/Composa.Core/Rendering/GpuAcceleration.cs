using System.Diagnostics;
using System.Runtime.InteropServices;
using Composa.Filters;
using Composa.Model;
using Composa.Painting;
using SkiaSharp;

namespace Composa.Rendering;

/// <summary>Optional GPU compute. Each operation has a CPU implementation and a device-specific reference check.</summary>
public static partial class GpuAcceleration
{
    // Transfer and dispatch cost can exceed the CPU blur time for small images.
    internal const int MinimumPixels = 3_000_000;
    private static readonly object configuration = new();
    private static Task<OpenClDevice?>? initialization;
    private static volatile bool filterEnabled, editorEnabled;

    public static bool CanEnable => (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) && Environment.Is64BitProcess;
    public static string? EditorDevice => editorEnabled ? ReadyDevice?.Device : null;
    public static string? FilterDevice => filterEnabled && ReadyDevice is { FloatBlurAvailable: true } backend ? backend.Device : null;

    internal static Task<OpenClDevice?>? Initialization => Volatile.Read(ref initialization);

    public static void Configure(bool filters, bool editor)
    {
        lock (configuration)
        {
            filterEnabled = filters && CanEnable;
            editorEnabled = editor && CanEnable;
            if (filterEnabled && initialization == null)
                Volatile.Write(ref initialization, Task.Run(Initialize));
            else if (!filterEnabled && !editorEnabled && initialization is { } previous)
            {
                Volatile.Write(ref initialization, null);
                _ = Task.Run(async () => (await previous.ConfigureAwait(false))?.Dispose());
            }
        }
    }

    internal static bool TryBlur(SKBitmap source, SKBitmap scratch, ReadOnlySpan<int> radii, bool clamp) =>
        filterEnabled && (long)source.Width * source.Height >= MinimumPixels
        && ReadyDevice is { FloatBlurAvailable: true } backend
        && backend.TryFloatBlur(source, scratch, radii, clamp);

    private static OpenClDevice? ReadyDevice =>
        Initialization is { IsCompletedSuccessfully: true } task ? task.Result : null;

    private static OpenClDevice? RequestEditorDevice()
    {
        if (Initialization == null)
        {
            lock (configuration)
                if (editorEnabled && initialization == null)
                    Volatile.Write(ref initialization, Task.Run(Initialize));
        }
        return ReadyDevice;
    }

    private static OpenClDevice? Initialize()
    {
        OpenClDevice? backend = null;
        try
        {
            backend = new OpenClDevice();
            backend.FloatBlurAvailable = backend.HasFloatBlur && ValidateFloatBlur(backend);
            backend.AlphaBlurAvailable = ValidateAlphaBlur(backend);
            backend.StrokeRingAvailable = ValidateStrokeRing(backend);
            backend.PaintAvailable = backend.HasPaint && PaintReferenceMatches(backend);
            backend.CoverageAvailable = backend.HasPaint && BrushStroke.CoverageMatchesGpu(backend);
            backend.CompositeAvailable = CompositeReferenceMatches(backend);
            if (backend.FloatBlurAvailable || backend.AlphaBlurAvailable || backend.StrokeRingAvailable ||
                backend.PaintAvailable || backend.CoverageAvailable || backend.CompositeAvailable)
                return backend;
            Trace.WriteLine($"GPU acceleration disabled: {backend.Device} failed its pixel reference checks.");
        }
        catch (Exception error) when (error is ExternalException or DllNotFoundException
            or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            Trace.WriteLine($"GPU raster filters unavailable: {error.Message}");
        }
        backend?.Dispose();
        return null;
    }

    private static bool ValidateFloatBlur(OpenClDevice backend)
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
            if (!backend.TryFloatBlur(input, output, radii, clamp)
                || !reference.GetPixelSpan().SequenceEqual(output.GetPixelSpan())) return false;
        }
        return true;
    }
}
