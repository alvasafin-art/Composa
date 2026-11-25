using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace Composa.Rendering;

internal sealed unsafe partial class OpenClDevice : IDisposable
{
    private const int NotFound = -1;
    private const ulong GpuDevice = 1 << 2;
    private const ulong CopyHost = 1 << 5;
    private const ulong RequiredFpCapabilities = 7; // Denormals, infinities/NaNs and round-to-nearest.
    private const ulong MaxWorkingBytes = 1024 * 1024 * 1024;
    private readonly object gate = new();
    private readonly nint library;
    private nint context, queue, program, floatBlur, alphaBlur, strokeRing, paint, brushCoverage, composite;
    private readonly ulong maxAllocation, workingBytes;
    private bool disposed;
    internal string Device { get; }
    internal bool FloatBlurAvailable { get; set; }
    internal bool AlphaBlurAvailable { get; set; }
    internal bool StrokeRingAvailable { get; set; }
    internal bool PaintAvailable { get; set; }
    internal bool CoverageAvailable { get; set; }
    internal bool CompositeAvailable { get; set; }

    internal OpenClDevice()
    {
        library = NativeLibrary.Load(OperatingSystem.IsWindows()
            ? Path.Combine(Environment.SystemDirectory, "OpenCL.dll") : "libOpenCL.so.1");
        try
        {
            var platformsFn = (delegate* unmanaged<uint, nint*, uint*, int>)Api("clGetPlatformIDs");
            uint count;
            Check(platformsFn(0, null, &count));
            var platforms = new nint[count];
            fixed (nint* pointer = platforms) Check(platformsFn(count, pointer, null));
            var devicesFn = (delegate* unmanaged<nint, ulong, uint, nint*, uint*, int>)Api("clGetDeviceIDs");
            nint device = 0;
            var hasFp64 = false;
            foreach (var platform in platforms)
            {
                var status = devicesFn(platform, GpuDevice, 0, null, &count);
                if (status == NotFound) continue;
                Check(status);
                var devices = new nint[count];
                fixed (nint* pointer = devices) Check(devicesFn(platform, GpuDevice, count, pointer, null));
                foreach (var candidate in devices)
                {
                    if (device == 0) device = candidate;
                    hasFp64 = (ReadU64(candidate, 0x101B) & RequiredFpCapabilities) == RequiredFpCapabilities
                        && (ReadU64(candidate, 0x1032) & RequiredFpCapabilities) == RequiredFpCapabilities;
                    if (hasFp64) { device = candidate; break; }
                }
                if (hasFp64) break;
            }
            if (device == 0) throw new InvalidOperationException("No OpenCL GPU.");
            Device = ReadString(device, 0x102B);
            maxAllocation = ReadU64(device, 0x1010);
            workingBytes = Math.Min(MaxWorkingBytes, ReadU64(device, 0x101F) / 8);
            int error;
            context = ((delegate* unmanaged<nint*, uint, nint*, nint, nint, int*, nint>)Api("clCreateContext"))(null, 1, &device, 0, 0, &error);
            Check(error);
            queue = ((delegate* unmanaged<nint, nint, ulong, int*, nint>)Api("clCreateCommandQueue"))(context, device, 0, &error);
            Check(error);
            const ulong roundedFloatDivision = 1 << 7;
            var fp32 = ReadU64(device, 0x101B);
            var hasExactPaint = (fp32 & (RequiredFpCapabilities | roundedFloatDivision))
                == (RequiredFpCapabilities | roundedFloatDivision);
            var source = Encoding.UTF8.GetBytes((hasFp64 ? FloatBlurSource : "")
                + "\n" + EditorKernelSource + (hasExactPaint ? "\n" + PaintKernelSource : ""));
            fixed (byte* pointer = source)
            {
                var length = (nuint)source.Length;
                var text = pointer;
                program = ((delegate* unmanaged<nint, uint, byte**, nuint*, int*, nint>)Api("clCreateProgramWithSource"))(context, 1, &text, &length, &error);
                Check(error);
            }
            var options = hasExactPaint ? "-cl-std=CL1.2 -cl-fp32-correctly-rounded-divide-sqrt\0"u8 : "-cl-std=CL1.2\0"u8;
            fixed (byte* pointer = options)
            {
                var build = ((delegate* unmanaged<nint, uint, nint*, byte*, nint, nint, int>)Api("clBuildProgram"))(program, 1, &device, pointer, 0, 0);
                if (build != 0) throw new InvalidOperationException($"OpenCL program failed ({build}): {BuildLog(device)}");
            }
            if (hasFp64) floatBlur = CreateKernel("box_blur\0"u8);
            alphaBlur = CreateKernel("alpha_blur\0"u8);
            strokeRing = CreateKernel("stroke_ring_step\0"u8);
            composite = CreateKernel("composite_normal\0"u8);
            if (hasExactPaint)
            {
                paint = CreateKernel("paint_pixels\0"u8);
                brushCoverage = CreateKernel("paint_coverage\0"u8);
            }
        }
        catch { Dispose(); throw; }
    }

    internal bool HasFloatBlur => floatBlur != 0;

    internal bool TryFloatBlur(SKBitmap source, SKBitmap destination, ReadOnlySpan<int> radii, bool clamp)
    {
        var bytes = (ulong)source.Width * (ulong)source.Height * 16;
        if (source.ColorType != SKColorType.RgbaF32 || destination.ColorType != source.ColorType
            || source.Width != destination.Width || source.Height != destination.Height
            || source.RowBytes != source.Width * 16 || destination.RowBytes != source.RowBytes
            || source.GetPixels() == 0 || destination.GetPixels() == 0 || source.GetPixels() == destination.GetPixels()
            || bytes > maxAllocation || bytes * 2 > workingBytes || !Monitor.TryEnter(gate))
            return false;
        try
        {
            if (disposed || !HasFloatBlur) return false;
            Apply(source, destination, (nuint)bytes, radii, clamp);
            return true;
        }
        catch (InvalidOperationException error)
        {
            Trace.WriteLine($"OpenCL fell back to CPU: {error.Message}");
            Dispose();
            return false;
        }
        finally { Monitor.Exit(gate); }
    }

    // Destination is scratch. The input remains untouched if an allocation, pass or readback fails.
    private void Apply(SKBitmap source, SKBitmap destination, nuint size, ReadOnlySpan<int> radii, bool clamp)
    {
        var input = Buffer(size, source.GetPixels());
        nint temporary = 0;
        try
        {
            temporary = Buffer(size, 0);
            foreach (var radius in radii)
            {
                Pass(input, temporary, source.Width, source.Height, radius, horizontal: true, clamp);
                Pass(temporary, input, source.Width, source.Height, radius, horizontal: false, clamp);
            }
            ReadBuffer(input, size, destination.GetPixels());
        }
        finally { Release("clReleaseMemObject", temporary); Release("clReleaseMemObject", input); }
    }

    private void Pass(nint input, nint output, int width, int height, int radius, bool horizontal, bool clamp)
    {
        Arg(floatBlur, 0, input); Arg(floatBlur, 1, output);
        Arg(floatBlur, 2, width); Arg(floatBlur, 3, height); Arg(floatBlur, 4, radius);
        Arg(floatBlur, 5, horizontal ? 1 : 0); Arg(floatBlur, 6, clamp ? 1 : 0);
        Dispatch(floatBlur, (nuint)(horizontal ? height : width));
    }

    private nint CreateKernel(ReadOnlySpan<byte> name)
    {
        int error;
        fixed (byte* pointer = name)
        {
            var handle = ((delegate* unmanaged<nint, byte*, int*, nint>)Api("clCreateKernel"))(program, pointer, &error);
            Check(error);
            return handle;
        }
    }

    private void Dispatch(nint kernel, nuint global) =>
        Check(((delegate* unmanaged<nint, nint, uint, nuint*, nuint*, nuint*, uint, nint*, nint*, int>)Api("clEnqueueNDRangeKernel"))
            (queue, kernel, 1, null, &global, null, 0, null, null));

    private void Arg<T>(nint kernel, uint index, T value) where T : unmanaged =>
        Check(((delegate* unmanaged<nint, uint, nuint, void*, int>)Api("clSetKernelArg"))(kernel, index, (nuint)sizeof(T), &value));

    private void ReadBuffer(nint buffer, nuint bytes, nint pointer) =>
        Check(((delegate* unmanaged<nint, nint, uint, nuint, nuint, nint, uint, nint*, nint*, int>)Api("clEnqueueReadBuffer"))
            (queue, buffer, 1, 0, bytes, pointer, 0, null, null));

    private nint Buffer(nuint length, nint initial)
    {
        int error;
        var buffer = ((delegate* unmanaged<nint, ulong, nuint, nint, int*, nint>)Api("clCreateBuffer"))
            (context, initial != 0 ? CopyHost : 0, length, initial, &error);
        Check(error);
        return buffer;
    }

    private ulong ReadU64(nint device, uint key)
    {
        ulong value;
        var state = ((delegate* unmanaged<nint, uint, nuint, void*, nuint*, int>)Api("clGetDeviceInfo"))(device, key, 8, &value, null);
        return state == 0 ? value : 0;
    }

    private string ReadString(nint device, uint key)
    {
        var function = (delegate* unmanaged<nint, uint, nuint, byte*, nuint*, int>)Api("clGetDeviceInfo");
        nuint count;
        Check(function(device, key, 0, null, &count));
        var text = new byte[count];
        fixed (byte* pointer = text) Check(function(device, key, count, pointer, null));
        return Encoding.UTF8.GetString(text).TrimEnd('\0');
    }

    private string BuildLog(nint device)
    {
        var function = (delegate* unmanaged<nint, nint, uint, nuint, byte*, nuint*, int>)Api("clGetProgramBuildInfo");
        nuint count;
        Check(function(program, device, 0x1183, 0, null, &count));
        var text = new byte[count];
        fixed (byte* pointer = text) Check(function(program, device, 0x1183, count, pointer, null));
        return Encoding.UTF8.GetString(text).TrimEnd('\0');
    }

    private nint Api(string symbol) => NativeLibrary.GetExport(library, symbol);
    private static void Check(int code) { if (code != 0) throw new InvalidOperationException($"OpenCL call failed ({code})."); }

    private void Release(string symbol, nint value)
    {
        if (value != 0) ((delegate* unmanaged<nint, int>)Api(symbol))(value);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (queue != 0) ((delegate* unmanaged<nint, int>)Api("clFinish"))(queue);
            ClearCompositeInputs();
            Release("clReleaseKernel", paint);
            Release("clReleaseKernel", brushCoverage);
            Release("clReleaseKernel", composite);
            Release("clReleaseKernel", alphaBlur);
            Release("clReleaseKernel", strokeRing);
            Release("clReleaseKernel", floatBlur);
            Release("clReleaseProgram", program);
            Release("clReleaseCommandQueue", queue);
            Release("clReleaseContext", context);
            NativeLibrary.Free(library);
        }
    }

    private const string FloatBlurSource = """
        #pragma OPENCL EXTENSION cl_khr_fp64 : enable
        #pragma OPENCL FP_CONTRACT OFF

        double4 sample_at(__global const float4* source, int line, int index, int width,
                          int length, int horizontal, int clamp_edge)
        {
            if (clamp_edge) index = clamp(index, 0, length - 1);
            else if (index < 0 || index >= length) return (double4)(0);
            return convert_double4(source[horizontal ? line * width + index : index * width + line]);
        }

        __kernel void box_blur(__global const float4* source, __global float4* destination,
                              int width, int height, int radius, int horizontal, int clamp_edge)
        {
            int length = horizontal ? width : height;
            int line = get_global_id(0);
            double divisor = 2 * radius + 1;
            double4 sum = (double4)(0);
            // A work item follows the same accumulation order as one CPU line.
            for (int i = -radius; i <= radius; i++)
                sum += sample_at(source, line, i, width, length, horizontal, clamp_edge);
            for (int i = 0; i < length; i++) {
                float4 pixel = convert_float4_rte(sum / divisor);
                pixel.w = pixel.w < 0.0f ? 0.0f : pixel.w > 1.0f ? 1.0f : pixel.w;
                pixel.xyz = select(pixel.xyz, (float3)(0), isless(pixel.xyz, (float3)(0)));
                pixel.xyz = select(pixel.xyz, (float3)(pixel.w), isgreater(pixel.xyz, (float3)(pixel.w)));
                destination[horizontal ? line * width + i : i * width + line] = pixel;
                sum += sample_at(source, line, i + radius + 1, width, length, horizontal, clamp_edge)
                     - sample_at(source, line, i - radius, width, length, horizontal, clamp_edge);
            }
        }
        """;
}
