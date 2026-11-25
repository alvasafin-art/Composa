using System.Diagnostics;
using SkiaSharp;

namespace Composa.Rendering;

internal sealed unsafe partial class OpenClDevice
{
    internal bool TryStrokeRing(SKBitmap source, SKBitmap destination, int reach, bool inside)
    {
        var bytes = (ulong)source.Width * (ulong)source.Height;
        if (source.RowBytes != source.Width || destination.RowBytes != source.RowBytes
            || bytes > maxAllocation || bytes * 3 > workingBytes || !Monitor.TryEnter(gate)) return false;
        nint original = 0, first = 0, second = 0;
        try
        {
            if (disposed) return false;
            original = Buffer((nuint)bytes, source.GetPixels());
            first = Buffer((nuint)bytes, 0);
            second = Buffer((nuint)bytes, 0);
            var input = original;
            var output = first;
            Arg(strokeRing, 2, original);
            Arg(strokeRing, 3, source.Width); Arg(strokeRing, 4, source.Height);
            Arg(strokeRing, 6, inside ? 1 : 0);
            for (var axis = 0; axis < 2; axis++)
            {
                Arg(strokeRing, 7, axis);
                for (var radius = 0; radius < reach;)
                {
                    // Taking the min or max of three shifted windows extends the accumulated
                    // radius while their union stays contiguous. Each pass reads three taps.
                    var shift = Math.Min(2 * radius + 1, reach - radius);
                    radius += shift;
                    Arg(strokeRing, 0, input); Arg(strokeRing, 1, output);
                    Arg(strokeRing, 5, shift); Arg(strokeRing, 8, axis == 1 && radius == reach ? 1 : 0);
                    Dispatch(strokeRing, (nuint)bytes);
                    input = output;
                    output = output == first ? second : first;
                }
            }
            ReadBuffer(input, (nuint)bytes, destination.GetPixels());
            return true;
        }
        catch (InvalidOperationException error)
        {
            Trace.WriteLine($"OpenCL layer stroke fell back to CPU: {error.Message}");
            ReleaseBuffers();
            Dispose();
            return false;
        }
        finally
        {
            ReleaseBuffers();
            Monitor.Exit(gate);
        }

        void ReleaseBuffers()
        {
            Release("clReleaseMemObject", original);
            Release("clReleaseMemObject", first);
            Release("clReleaseMemObject", second);
            original = first = second = 0;
        }
    }

    internal bool TryAlphaBlur(SKBitmap source, SKBitmap destination, int dx, int dy, int window)
    {
        var bytes = (ulong)source.Width * (ulong)source.Height;
        if (source.ColorType != SKColorType.Alpha8 || destination.ColorType != SKColorType.Alpha8
            || source.Width != destination.Width || source.Height != destination.Height
            || source.RowBytes != source.Width || destination.RowBytes != source.RowBytes
            || source.GetPixels() == destination.GetPixels()
            || bytes > maxAllocation || bytes * 2 > workingBytes || !Monitor.TryEnter(gate)) return false;
        nint input = 0, scratch = 0;
        try
        {
            if (disposed) return false;
            input = Buffer((nuint)bytes, source.GetPixels());
            scratch = Buffer((nuint)bytes, 0);
            var last = (window & 1) == 0 ? window + 1 : window;
            var divisor = (uint)(window * window * last);
            var reciprocal = (uint)Math.Round((1ul << 32) / (double)divisor, MidpointRounding.AwayFromZero);
            AlphaPass(input, scratch, source.Width, source.Height, dx, window, last, reciprocal, (divisor + 1) / 2, true);
            AlphaPass(scratch, input, source.Width, source.Height, dy, window, last, reciprocal, (divisor + 1) / 2, false);
            ReadBuffer(input, (nuint)bytes, destination.GetPixels());
            return true;
        }
        catch (InvalidOperationException error)
        {
            Trace.WriteLine($"OpenCL effects fell back to CPU: {error.Message}");
            Release("clReleaseMemObject", scratch);
            Release("clReleaseMemObject", input);
            scratch = input = 0;
            Dispose();
            return false;
        }
        finally
        {
            Release("clReleaseMemObject", scratch);
            Release("clReleaseMemObject", input);
            Monitor.Exit(gate);
        }
    }

    private void AlphaPass(nint input, nint output, int width, int height, int shift, int window, int last, uint reciprocal, uint rounding, bool horizontal)
    {
        Arg(alphaBlur, 0, input); Arg(alphaBlur, 1, output);
        Arg(alphaBlur, 2, width); Arg(alphaBlur, 3, height);
        Arg(alphaBlur, 4, shift); Arg(alphaBlur, 5, window); Arg(alphaBlur, 6, last);
        Arg(alphaBlur, 7, reciprocal); Arg(alphaBlur, 8, rounding); Arg(alphaBlur, 9, horizontal ? 1 : 0);
        var length = horizontal ? width : height;
        var lines = horizontal ? height : width;
        Dispatch(alphaBlur, (nuint)lines * (((nuint)length + 255) / 256));
    }

    private const string EditorKernelSource = """
        #pragma OPENCL FP_CONTRACT OFF

        __kernel void stroke_ring_step(__global const uchar* source, __global uchar* target,
                                       __global const uchar* original, int width, int height,
                                       int shift, int inside, int vertical, int finish)
        {
            int i = get_global_id(0);
            int x = i % width, y = i / width;
            int p = vertical ? y : x;
            int end = (vertical ? height : width) - 1;
            int stride = vertical ? width : 1;
            uint a = source[i + (max(0, p - shift) - p) * stride];
            uint b = source[i];
            uint c = source[i + (min(end, p + shift) - p) * stride];
            uint filtered = inside ? min(min(a, b), c) : max(max(a, b), c);
            // Skia erodes against transparent pixels beyond the layer's bounds.
            if (inside && (p - shift < 0 || p + shift > end)) filtered = 0;
            if (finish) {
                uint base = original[i];
                // Isolate the inside or outside ring with the same DstOut as the raster path.
                filtered = inside ? (base * (255 - filtered) + 127) / 255 : (filtered * (255 - base) + 127) / 255;
            }
            target[i] = filtered;
        }

        uint alpha_sample(__global const uchar* source, int line, int position, int begin, int width, int length, int horizontal)
        {
            if (position < begin || position >= length) return 0;
            return source[horizontal ? line * width + position : position * width + line];
        }

        // The third finite difference of three consecutive box sums is a six-tap
        // integer stencil. Integrating it gives the original sum with bounded warmup.
        __kernel void alpha_blur(__global const uchar* source, __global uchar* destination,
                                 int width, int height, int shift, int window, int last,
                                 uint reciprocal, uint rounding, int horizontal)
        {
            int length = horizontal ? width : height;
            int lines = horizontal ? height : width;
            int lane = get_global_id(0);
            int line = lane % lines;
            int start = (lane / lines) * 256;
            if (start >= length) return;
            int end = min(length, start + 256);
            int border = (2 * window + last - 3) / 2;
            int begin = max(0, start - border - shift);
            uint first = 0, second = 0, third = 0;
            for (int t = min(begin, start + border - shift); t < end + border - shift; t++) {
                uint v = alpha_sample(source, line, t, begin, width, length, horizontal)
                    - 2 * alpha_sample(source, line, t - window, begin, width, length, horizontal)
                    + alpha_sample(source, line, t - 2 * window, begin, width, length, horizontal);
                uint w = alpha_sample(source, line, t - last, begin, width, length, horizontal)
                    - 2 * alpha_sample(source, line, t - window - last, begin, width, length, horizontal)
                    + alpha_sample(source, line, t - 2 * window - last, begin, width, length, horizontal);
                first += v - w;
                second += first;
                third += second;
                int i = t - border + shift;
                if (i >= start) destination[horizontal ? line * width + i : i * width + line] = mul_hi(third + rounding, reciprocal);
            }
        }

        __kernel void composite_normal(__global const uchar* source, __global uchar* destination,
                                       int width, int stride, int x, int y, int float_pixels,
                                       int source_stride, int source_x, int source_y)
        {
            int i = get_global_id(0);
            int d = (y + i / width) * stride + x + i % width;
            int s_at = (source_y + i / width) * source_stride + source_x + i % width;
            if (float_pixels) {
                float4 s = ((__global const float4*)source)[s_at];
                __global float4* out = (__global float4*)destination;
                out[d] = s + out[d] * (1.0f - s.w);
            } else {
                uint4 s = convert_uint4(((__global const uchar4*)source)[s_at]);
                __global uchar4* out = (__global uchar4*)destination;
                uint4 b = convert_uint4(out[d]);
                out[d] = convert_uchar4_sat(s + (b * (255 - s.w) + 127) / 255);
            }
        }
        """;
}
