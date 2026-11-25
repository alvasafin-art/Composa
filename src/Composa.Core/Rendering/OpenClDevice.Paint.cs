using System.Buffers;
using System.Diagnostics;
using Composa.Painting;
using SkiaSharp;

namespace Composa.Rendering;

internal readonly record struct GpuBrushPixels(SKBitmap Source, SKBitmap Destination, SKColorF Color, float Opacity);

internal sealed unsafe partial class OpenClDevice
{
    internal bool HasPaint => paint != 0;

    internal bool TryBrushCoverage(StrokeCoverage coverage, SKRectI area, SKPoint center, float radius, float hardness,
        float[] falloff, float flow, out bool changed, GpuBrushPixels? paintPixels = null)
    {
        changed = false;
        var pixels = area.Width * area.Height;
        var bytes = (ulong)pixels * sizeof(ushort);
        var paintBytes = paintPixels.HasValue ? (ulong)pixels * 4 : 0;
        if (brushCoverage == 0 || bytes > maxAllocation || paintBytes > maxAllocation
            || bytes + paintBytes + (ulong)falloff.Length * sizeof(float) > workingBytes
            || !Monitor.TryEnter(gate)) return false;
        nint mask = 0, table = 0, color = 0;
        ushort[]? staging = null;
        try
        {
            if (disposed) return false;
            staging = ArrayPool<ushort>.Shared.Rent(pixels);
            coverage.CopyTo(area, staging.AsSpan(0, pixels));
            fixed (ushort* data = staging) mask = Buffer((nuint)bytes, (nint)data);
            fixed (float* data = falloff) table = Buffer((nuint)falloff.Length * sizeof(float), (nint)data);
            if (paintPixels is { } job)
            {
                color = Buffer((nuint)paintBytes, 0);
                WriteRows(color, job.Source, area);
            }
            Arg(brushCoverage, 0, mask); Arg(brushCoverage, 1, table);
            Arg(brushCoverage, 2, area.Width); Arg(brushCoverage, 3, area.Left); Arg(brushCoverage, 4, area.Top);
            Arg(brushCoverage, 5, center.X); Arg(brushCoverage, 6, center.Y);
            Arg(brushCoverage, 7, radius); Arg(brushCoverage, 8, hardness); Arg(brushCoverage, 9, flow);
            Arg(brushCoverage, 10, falloff.Length - 1);
            Dispatch(brushCoverage, (nuint)pixels);
            if (paintPixels is { } dab) PaintPass(color, mask, dab.Color, dab.Opacity, pixels);
            fixed (ushort* data = staging) ReadBuffer(mask, (nuint)bytes, (nint)data);
            // Both results are ready before the CPU stroke is updated. On a device failure
            // the ordinary CPU dab can still start from the original coverage and pixels.
            if (paintPixels is { } output) DownloadRows(color, output.Destination, area, (int)paintBytes);
            changed = coverage.CopyFrom(area, staging.AsSpan(0, pixels));
            return true;
        }
        catch (InvalidOperationException error)
        {
            Trace.WriteLine($"OpenCL brush coverage fell back to CPU: {error.Message}");
            Release("clReleaseMemObject", table);
            Release("clReleaseMemObject", mask);
            Release("clReleaseMemObject", color);
            table = mask = color = 0;
            Dispose();
            return false;
        }
        finally
        {
            if (staging != null) ArrayPool<ushort>.Shared.Return(staging);
            Release("clReleaseMemObject", table);
            Release("clReleaseMemObject", mask);
            Release("clReleaseMemObject", color);
            Monitor.Exit(gate);
        }
    }

    internal bool TryPaint(SKBitmap source, SKBitmap destination, SKRectI area, StrokeCoverage coverage, SKColorF color, float opacity)
    {
        var pixels = area.Width * area.Height;
        var bytes = (ulong)pixels * (ulong)source.BytesPerPixel;
        var coverageBytes = (ulong)pixels * sizeof(ushort);
        if (!HasPaint || source.ColorType != SKColorType.Rgba8888 || destination.ColorType != source.ColorType
            || bytes > maxAllocation || coverageBytes > maxAllocation || bytes + coverageBytes > workingBytes
            || !Monitor.TryEnter(gate)) return false;
        nint input = 0, mask = 0;
        try
        {
            if (disposed) return false;
            input = Buffer((nuint)bytes, 0);
            var amounts = ArrayPool<ushort>.Shared.Rent(pixels);
            try
            {
                coverage.CopyTo(area, amounts.AsSpan(0, pixels));
                fixed (ushort* pointer = amounts) mask = Buffer((nuint)coverageBytes, (nint)pointer);
            }
            finally { ArrayPool<ushort>.Shared.Return(amounts); }

            WriteRows(input, source, area);
            PaintPass(input, mask, color, opacity, pixels);
            DownloadRows(input, destination, area, (int)bytes);
            GC.KeepAlive(source);
            return true;
        }
        catch (InvalidOperationException error)
        {
            Trace.WriteLine($"OpenCL brush fell back to CPU: {error.Message}");
            Release("clReleaseMemObject", mask);
            Release("clReleaseMemObject", input);
            mask = input = 0;
            Dispose();
            GC.KeepAlive(source);
            return false;
        }
        finally
        {
            Release("clReleaseMemObject", mask);
            Release("clReleaseMemObject", input);
            Monitor.Exit(gate);
        }
    }

    private void PaintPass(nint buffer, nint mask, SKColorF color, float opacity, int pixels)
    {
        Arg(paint, 0, buffer); Arg(paint, 1, mask);
        Arg(paint, 2, color); Arg(paint, 3, opacity);
        Dispatch(paint, (nuint)pixels);
    }

    private void WriteRows(nint buffer, SKBitmap source, SKRectI area)
    {
        var pixelBytes = source.BytesPerPixel;
        var rowBytes = (nuint)(area.Width * pixelBytes);
        nuint* bufferOrigin = stackalloc nuint[] { 0, 0, 0 };
        nuint* hostOrigin = stackalloc nuint[] { (nuint)(area.Left * pixelBytes), (nuint)area.Top, 0 };
        nuint* region = stackalloc nuint[] { rowBytes, (nuint)area.Height, 1 };
        Check(((delegate* unmanaged<nint, nint, uint, nuint*, nuint*, nuint*, nuint, nuint, nuint, nuint, nint, uint, nint*, nint*, int>)
            Api("clEnqueueWriteBufferRect"))(queue, buffer, 1, bufferOrigin, hostOrigin, region,
                rowBytes, 0, (nuint)source.RowBytes, 0, source.GetPixels(), 0, null, null));
        GC.KeepAlive(source);
    }

    private void DownloadRows(nint buffer, SKBitmap destination, SKRectI area, int bytes)
    {
        // Keep the destination untouched until the blocking read has succeeded.
        var backing = ArrayPool<byte>.Shared.Rent(bytes);
        try
        {
            fixed (byte* pointer = backing) ReadBuffer(buffer, (nuint)bytes, (nint)pointer);
            var pixelBytes = destination.BytesPerPixel;
            var rowBytes = area.Width * pixelBytes;
            var output = destination.GetPixelSpan();
            for (var y = 0; y < area.Height; y++)
                backing.AsSpan(y * rowBytes, rowBytes)
                    .CopyTo(output.Slice((area.Top + y) * destination.RowBytes + area.Left * pixelBytes, rowBytes));
        }
        finally { ArrayPool<byte>.Shared.Return(backing); }
    }

    private const string PaintKernelSource = """
        #pragma OPENCL FP_CONTRACT OFF

        __kernel void paint_coverage(__global ushort* coverage, __constant const float* table,
                                     int width, int left, int top, float center_x, float center_y,
                                     float radius, float hardness, float flow, int steps)
        {
            int i = get_global_id(0);
            float dx = (float)(left + i % width) + 0.5f - center_x;
            float dy = (float)(top + i / width) + 0.5f - center_y;
            float squared = dx * dx + dy * dy;
            float outer = radius + 0.5f;
            if (squared >= outer * outer) return;
            float amount;
            if (hardness >= 0.995f) {
                amount = clamp(radius - sqrt(squared) + 0.5f, 0.0f, 1.0f);
            } else {
                float position = min((float)steps, squared / (radius * radius) * (float)steps);
                int index = min(steps - 1, (int)position);
                amount = table[index] + (table[index + 1] - table[index]) * (position - (float)index);
                float inner = radius - 0.5f;
                if (squared > inner * inner)
                    amount *= clamp(radius - sqrt(squared) + 0.5f, 0.0f, 1.0f);
            }
            if (amount > 0.0f) {
                float old = (float)coverage[i];
                coverage[i] = convert_ushort_sat_rte(old + (65535.0f - old) * amount * flow);
            }
        }

        __kernel void paint_pixels(__global uchar4* pixels, __global const ushort* coverage, float4 color, float opacity)
        {
            int i = get_global_id(0);
            if (coverage[i] == 0 || opacity <= 0) return;
            float amount = (((float)coverage[i] / 257.0f) / 255.0f) * opacity;
            float4 value = convert_float4(pixels[i]) * (1.0f - amount) + color * amount;
            pixels[i] = convert_uchar4_sat_rtz(value + 0.5f);
        }
        """;
}
