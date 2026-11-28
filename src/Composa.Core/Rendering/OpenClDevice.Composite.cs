using System.Diagnostics;
using System.Runtime.InteropServices;
using Composa.Model;
using SkiaSharp;

namespace Composa.Rendering;

internal readonly record struct GpuCompositeLayer(SKBitmap Pixels, SKRectI Sample, SKPointI Destination);

internal sealed unsafe partial class OpenClDevice
{
    // Keep interactive transfers bounded on both dedicated and integrated devices.
    internal const int MaxCompositeBytes = 64 * 1024 * 1024;
    private const ulong MaxCompositeInputBytes = 96 * 1024 * 1024;
    private sealed record Upload(WeakReference<SKImage> Generation, SKRectI Rect, nint Buffer, ulong Bytes);
    private readonly List<Upload> compositeInputs = [];
    private ulong compositeInputBytes;
    private int uploadGeneration;
    private bool warmingComposite;
    private void* compositeReadback;
    private nuint compositeReadbackBytes;

    internal bool CompositeInputsReady(IReadOnlyList<GpuCompositeLayer> layers)
    {
        if (!Monitor.TryEnter(gate)) return false;
        try
        {
            if (disposed) return false;
            if (FindCompositeInputs(layers) != null) return true;
            WarmCompositeInputs(layers);
            return false;
        }
        finally { Monitor.Exit(gate); }
    }

    internal bool TryComposite(SKBitmap destination, SKRectI area, IReadOnlyList<GpuCompositeLayer> layers, bool retainInputs)
    {
        var bytes = (ulong)area.Width * (ulong)area.Height * (ulong)destination.BytesPerPixel;
        if (bytes > MaxCompositeBytes || bytes > maxAllocation || bytes * 2 > workingBytes
            || !Monitor.TryEnter(gate)) return false;
        nint result = 0, input = 0;
        try
        {
            if (disposed) return false;
            Upload[]? uploads = null;
            if (retainInputs)
            {
                uploads = FindCompositeInputs(layers);
                if (uploads == null)
                {
#if CANVAS_PERFORMANCE
                    CanvasPerformanceTrace.StageCompleted("OpenCL input cache miss", Stopwatch.GetTimestamp());
#endif
                    WarmCompositeInputs(layers);
                    return false;
                }
            }
            result = Buffer((nuint)bytes, 0);
            var first = layers[0];
            var fullFirstLayer = uploads != null && first.Destination == SKPointI.Empty
                && first.Sample.Width == area.Width && first.Sample.Height == area.Height;
            if (fullFirstLayer) CopyCompositeInput(result, uploads![0], first, destination.BytesPerPixel);
            else Zero(result, (nuint)bytes);
            var floatPixels = destination.ColorType == SKColorType.RgbaF32;
            Arg(composite, 1, result);
            Arg(composite, 3, area.Width); Arg(composite, 6, floatPixels ? 1 : 0);
            for (var i = fullFirstLayer ? 1 : 0; i < layers.Count; i++)
            {
                var layer = layers[i];
                var upload = uploads?[i];
                if (upload == null)
                {
                    if (input == 0) input = Buffer((nuint)bytes, 0);
                    WriteRows(input, layer.Pixels, layer.Sample);
                }
                Arg(composite, 0, upload?.Buffer ?? input);
                Arg(composite, 2, layer.Sample.Width);
                Arg(composite, 4, layer.Destination.X); Arg(composite, 5, layer.Destination.Y);
                Arg(composite, 7, upload?.Rect.Width ?? layer.Sample.Width);
                Arg(composite, 8, upload == null ? 0 : layer.Sample.Left - upload.Rect.Left);
                Arg(composite, 9, upload == null ? 0 : layer.Sample.Top - upload.Rect.Top);
                Dispatch(composite, (nuint)layer.Sample.Width * (nuint)layer.Sample.Height);
            }
            ReadComposite(result, destination, area, (nuint)bytes);
            return true;
        }
        catch (InvalidOperationException error)
        {
            Trace.WriteLine($"OpenCL compositor fell back to CPU: {error.Message}");
            Release("clReleaseMemObject", input);
            Release("clReleaseMemObject", result);
            input = result = 0;
            Dispose();
            return false;
        }
        catch (OutOfMemoryException)
        {
            // The CPU renderer can still use its bounded bands if the full viewport staging buffer won't fit.
            return false;
        }
        finally
        {
            Release("clReleaseMemObject", input);
            Release("clReleaseMemObject", result);
            Monitor.Exit(gate);
        }
    }

    private Upload[]? FindCompositeInputs(IReadOnlyList<GpuCompositeLayer> layers)
    {
        var uploads = new Upload[layers.Count];
        for (var i = 0; i < layers.Count; i++)
        {
            if (FindUpload(layers[i]) is not { } existing) return null;
            uploads[i] = existing;
        }
        return uploads;
    }

    private Upload? FindUpload(GpuCompositeLayer layer)
    {
        var generation = Pixels.ImageOf(layer.Pixels);
        for (var i = compositeInputs.Count - 1; i >= 0; i--)
        {
            var entry = compositeInputs[i];
            if (!entry.Generation.TryGetTarget(out var source)) { RemoveUpload(i); continue; }
            if (!ReferenceEquals(generation, source) || !entry.Rect.Contains(layer.Sample)) continue;
            compositeInputs.RemoveAt(i);
            compositeInputs.Add(entry);
            return entry;
        }
        return null;
    }

    private ulong CompositeInputLimit => Math.Min(MaxCompositeInputBytes, workingBytes / 2);

    private void WarmCompositeInputs(IReadOnlyList<GpuCompositeLayer> layers)
    {
        if (warmingComposite || layers.Count > 8) return;
        var uploadBytes = layers.Sum(layer => (long)layer.Sample.Width * layer.Sample.Height * layer.Pixels.BytesPerPixel);
        if ((ulong)uploadBytes > CompositeInputLimit) return;
        warmingComposite = true;
        var generation = uploadGeneration;
        _ = Task.Run(() =>
        {
            lock (gate)
            {
                try
                {
                    if (disposed || uploadGeneration != generation) return;
                    // The scene references immutable bitmaps. A small border keeps layer moves on the uploaded tile.
                    var border = layers.Count <= 3 && (ulong)uploadBytes <= CompositeInputLimit / 2 ? 64 : 0;
                    foreach (var layer in layers)
                    {
                        if (Pixels.IsLive(layer.Pixels) || FindUpload(layer) != null) continue;
                        var rect = new SKRectI(layer.Sample.Left - border, layer.Sample.Top - border,
                            layer.Sample.Right + border, layer.Sample.Bottom + border);
                        UploadInput(layer.Pixels, Geometry.Intersect(rect, layer.Pixels.Info.Rect));
                    }
                }
                catch (InvalidOperationException error)
                {
                    Trace.WriteLine($"OpenCL compositor cache unavailable: {error.Message}");
                    Dispose();
                }
                finally { warmingComposite = false; }
            }
        });
    }

    private void UploadInput(SKBitmap pixels, SKRectI rect)
    {
        var bytes = (ulong)rect.Width * (ulong)rect.Height * (ulong)pixels.BytesPerPixel;
        if (bytes > CompositeInputLimit || bytes > maxAllocation) return;
        while (compositeInputs.Count >= 8 || compositeInputBytes + bytes > CompositeInputLimit) RemoveUpload(0);
        var generation = Pixels.ImageOf(pixels);
        var buffer = Buffer((nuint)bytes, 0);
#if CANVAS_PERFORMANCE
        var uploading = Stopwatch.GetTimestamp();
#endif
        try { WriteRows(buffer, pixels, rect); }
        catch { Release("clReleaseMemObject", buffer); throw; }
#if CANVAS_PERFORMANCE
        CanvasPerformanceTrace.StageCompleted("OpenCL input upload", uploading);
#endif
        compositeInputs.Add(new(new(generation), rect, buffer, bytes));
        compositeInputBytes += bytes;
    }

    private void RemoveUpload(int index)
    {
        var entry = compositeInputs[index];
        compositeInputs.RemoveAt(index);
        compositeInputBytes -= entry.Bytes;
        Release("clReleaseMemObject", entry.Buffer);
    }

    internal void ClearCompositeInputs()
    {
        lock (gate)
        {
            uploadGeneration++;
            for (var i = compositeInputs.Count - 1; i >= 0; i--) RemoveUpload(i);
            NativeMemory.Free(compositeReadback);
            compositeReadback = null;
            compositeReadbackBytes = 0;
        }
    }

    private void CopyCompositeInput(nint destination, Upload upload, GpuCompositeLayer layer, int pixelBytes)
    {
        var rowBytes = (nuint)(layer.Sample.Width * pixelBytes);
        nuint* sourceOrigin = stackalloc nuint[] { (nuint)((layer.Sample.Left - upload.Rect.Left) * pixelBytes),
            (nuint)(layer.Sample.Top - upload.Rect.Top), 0 };
        nuint* targetOrigin = stackalloc nuint[] { 0, 0, 0 };
        nuint* region = stackalloc nuint[] { rowBytes, (nuint)layer.Sample.Height, 1 };
        Check(((delegate* unmanaged<nint, nint, nint, nuint*, nuint*, nuint*, nuint, nuint, nuint, nuint, uint, nint*, nint*, int>)
            Api("clEnqueueCopyBufferRect"))(queue, upload.Buffer, destination, sourceOrigin, targetOrigin, region,
                (nuint)(upload.Rect.Width * pixelBytes), 0, rowBytes, 0, 0, null, null));
    }

    private void ReadComposite(nint buffer, SKBitmap destination, SKRectI area, nuint bytes)
    {
#if CANVAS_PERFORMANCE
        var reading = Stopwatch.GetTimestamp();
#endif
        var pixelBytes = destination.BytesPerPixel;
        var rowBytes = (nuint)(area.Width * pixelBytes);
        if (rowBytes == (nuint)destination.RowBytes)
            ReadBuffer(buffer, bytes, destination.GetPixels() + (nint)((long)area.Top * destination.RowBytes));
        else
        {
            // One packed transfer is cheaper than asking the driver to scatter across the viewport stride.
            if (bytes > compositeReadbackBytes)
            {
                compositeReadback = NativeMemory.Realloc(compositeReadback, bytes);
                compositeReadbackBytes = bytes;
            }
            ReadBuffer(buffer, bytes, (nint)compositeReadback);
            var output = (byte*)destination.GetPixels() + (long)area.Top * destination.RowBytes + area.Left * pixelBytes;
            for (var y = 0; y < area.Height; y++)
                System.Buffer.MemoryCopy((byte*)compositeReadback + (long)y * (long)rowBytes,
                    output + (long)y * destination.RowBytes, (long)rowBytes, (long)rowBytes);
        }
#if CANVAS_PERFORMANCE
        CanvasPerformanceTrace.StageCompleted("OpenCL blocking readback and copy", reading);
#endif
        GC.KeepAlive(destination);
    }

    private void Zero(nint buffer, nuint bytes)
    {
        uint zero = 0;
        Check(((delegate* unmanaged<nint, nint, void*, nuint, nuint, nuint, uint, nint*, nint*, int>)Api("clEnqueueFillBuffer"))
            (queue, buffer, &zero, sizeof(uint), 0, bytes, 0, null, null));
    }
}
