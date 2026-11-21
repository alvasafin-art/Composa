#if CANVAS_PERFORMANCE
using System.Runtime.InteropServices;

namespace Composa.App.Tests;

/// <summary>Windows address space and default heap accounting for the diagnostic replay.</summary>
internal static class CanvasNativeMemory
{
    private const uint Committed = 0x1000, Private = 0x20000, Mapped = 0x40000, Image = 0x1000000;

    internal sealed record Allocation(ulong Base, ulong CommittedBytes);
    internal sealed record Sample(ulong PrivateBytes, ulong MappedBytes, ulong ImageBytes, ulong? HeapAllocated,
        ulong? HeapCommitted, Allocation[] LargePrivateAllocations);

    internal static Sample? Read()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return null;
        var privateGroups = new Dictionary<ulong, ulong>();
        ulong privateBytes = 0, mappedBytes = 0, imageBytes = 0;
        for (nuint address = 0; VirtualQuery(address, out var region, (nuint)Marshal.SizeOf<MemoryInformation>()) > 0;)
        {
            var next = region.Base + region.Size;
            if (next <= address) break;
            address = next;
            if (region.State != Committed) continue;
            switch (region.Type)
            {
                case Private:
                    privateBytes += region.Size;
                    privateGroups[region.AllocationBase] = privateGroups.GetValueOrDefault(region.AllocationBase) + region.Size;
                    break;
                case Mapped: mappedBytes += region.Size; break;
                case Image: imageBytes += region.Size; break;
            }
        }
        var heap = new HeapInformation { Size = (uint)Marshal.SizeOf<HeapInformation>() };
        var gotHeap = HeapSummary(GetProcessHeap(), 0, ref heap);
        return new(privateBytes, mappedBytes, imageBytes, gotHeap ? heap.Allocated : null, gotHeap ? heap.Committed : null,
            privateGroups.Where(pair => pair.Value >= 16 * 1024 * 1024)
                .OrderByDescending(pair => pair.Value).Select(pair => new Allocation(pair.Key, pair.Value)).ToArray());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInformation
    {
        public nuint Base, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint Size;
        public uint State, Protect, Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HeapInformation
    {
        public uint Size;
        public nuint Allocated, Committed, Reserved, MaxReserved;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nuint VirtualQuery(nuint address, out MemoryInformation information, nuint length);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetProcessHeap();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HeapSummary(nint heap, uint flags, ref HeapInformation summary);
}
#endif
