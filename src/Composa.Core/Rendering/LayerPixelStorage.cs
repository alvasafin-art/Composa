using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using SkiaSharp;

namespace Composa.Rendering;

/// <summary>Contiguous layer pixels with private, copy-on-write stroke views on Windows.</summary>
internal static class LayerPixelStorage
{
    private const long MinimumBytes = 16L * 1024 * 1024;
    private const int Tile = 128;
    // Replaying scattered tiles incurs private page faults on each row. Even on a huge
    // canvas, copying dozens of disjoint patches can cost more than a sequential rebase.
    private const int MaxInheritedTiles = 24;
    private static readonly ConditionalWeakTable<SKBitmap, Storage> surfaces = new();

    // One paging-file section per base image. Each native pixel reference owns a view and
    // keeps the section alive even when the original layer and its SKBitmap have gone away.
    private sealed class Section(long bytes)
    {
        internal readonly MemoryMappedFile File = MemoryMappedFile.CreateNew(null, bytes);
        private int references = 1;
        internal void Retain() => Interlocked.Increment(ref references);
        internal void Release()
        {
            if (Interlocked.Decrement(ref references) == 0) File.Dispose();
        }
    }

    private sealed class Storage
    {
        internal readonly Section Section;
        internal readonly MemoryMappedViewAccessor View;
        internal readonly nint Address;
        // Null for the base view. Private views remember tiles which differ from the base,
        // so branching from any immutable undo state needs only those tiles.
        internal readonly HashSet<int>? Changed;
        private readonly SKImageInfo format;
        internal readonly int Columns;
        internal readonly int TileCount;

        internal Storage(Section section, SKImageInfo info, HashSet<int>? changed)
        {
            format = info;
            Section = section;
            Changed = changed;
            Columns = (info.Width + Tile - 1) / Tile;
            TileCount = Columns * ((info.Height + Tile - 1) / Tile);
            View = section.File.CreateViewAccessor(0, 0, changed == null ? MemoryMappedFileAccess.ReadWrite : MemoryMappedFileAccess.CopyOnWrite);
            Address = View.SafeMemoryMappedViewHandle.DangerousGetHandle();
            section.Retain();
        }

        private int released;
        internal void Release()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            View.Dispose();
            Section.Release();
        }

        internal bool Owns(SKBitmap bitmap, SKImageInfo info) => Volatile.Read(ref released) == 0
            && format.Width == info.Width && format.Height == info.Height && format.ColorType == info.ColorType
            && bitmap.RowBytes == format.RowBytes && bitmap.GetPixels() == Address;
    }

    private static bool Eligible(SKImageInfo info) => OperatingSystem.IsWindows() && (long)info.RowBytes * info.Height >= MinimumBytes;

    // Reserve mapped storage only for persistent layer images. Scratch render tiles, previews
    // and normal public bitmap clones continue to use Skia's regular allocator.
    internal static SKBitmap Allocate(SKImageInfo info, bool clear = false)
    {
        if (Eligible(info))
        {
            try
            {
                var section = new Section((long)info.RowBytes * info.Height);
                try { return Map(section, info, null); }
                finally { section.Release(); }
            }
            catch (Exception ex) when (MappingUnavailable(ex)) { }
        }
        var bitmap = new SKBitmap(info);
        if (clear) Pixels.Fill(bitmap, 0);
        return bitmap;
    }

    private static bool MappingUnavailable(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or OutOfMemoryException or PlatformNotSupportedException;

    private static SKBitmap Map(Section section, SKImageInfo info, HashSet<int>? changed)
    {
        var storage = new Storage(section, info, changed);
        SKBitmap? bitmap = null;
        try
        {
            bitmap = new SKBitmap();
            if (!bitmap.InstallPixels(info, storage.Address, info.RowBytes, static (_, context) => ((Storage)context).Release(), storage))
                throw new IOException("Could not attach layer pixels to Skia.");
            surfaces.Add(bitmap, storage);
            return bitmap;
        }
        catch
        {
            bitmap?.Dispose();
            storage.Release();
            throw;
        }
    }

    // The editor guarantees that existing document pixels are immutable, including while
    // a background save is reading them. Only the new private view is ever written.
    internal static SKBitmap CopyForStroke(SKBitmap source)
    {
        var info = source.Info;
        if (!Eligible(info)) return Pixels.Clone(source);
        if (surfaces.TryGetValue(source, out var storage) && storage.Owns(source, info)
            && (storage.Changed == null || (storage.Changed.Count <= MaxInheritedTiles && storage.Changed.Count * 4 < storage.TileCount)))
        {
            try
            {
                var changed = storage.Changed == null ? new HashSet<int>() : new(storage.Changed);
                var copy = Map(storage.Section, info, changed);
                try
                {
                    if (changed.Count != 0) CopyTiles(source, copy, changed, storage.Columns);
                    Pixels.RetainProfile(source, copy);
                    return copy;
                }
                catch { copy.Dispose(); throw; }
            }
            catch (Exception ex) when (MappingUnavailable(ex)) { }
        }
        // Foreign or densely edited bitmap: establish a fresh base with a single independent
        // copy. Future local strokes can fork it again, without an ever-growing patch chain.
        return Pixels.CloneInto(source, Allocate(info));
    }

    private static unsafe void CopyTiles(SKBitmap source, SKBitmap copy, HashSet<int> tiles, int columns)
    {
        var input = (byte*)source.GetPixels();
        var output = (byte*)copy.GetPixels();
        int stride = source.RowBytes, width = source.Width, height = source.Height, bpp = source.BytesPerPixel;
        foreach (var index in tiles)
        {
            int x = index % columns * Tile, top = index / columns * Tile;
            int bytes = Math.Min(Tile, width - x) * bpp, bottom = Math.Min(top + Tile, height);
            for (int y = top; y < bottom; y++)
            {
                long offset = (long)y * stride + x * bpp;
                Buffer.MemoryCopy(input + offset, output + offset, bytes, bytes);
            }
        }
    }

    internal static void Changed(SKBitmap bitmap, SKRectI? area)
    {
        if (!surfaces.TryGetValue(bitmap, out var storage) || storage.Changed is not { } tiles) return;
        if (area == null)
        {
            // A writer outside the brush changed an unspecified region. Its real pixels remain
            // authoritative; the next stroke will establish a fresh base.
            surfaces.Remove(bitmap);
            return;
        }
        if (tiles.Count > MaxInheritedTiles) return;
        var rect = SKRectI.Intersect(area.Value, new SKRectI(0, 0, bitmap.Width, bitmap.Height));
        if (rect.IsEmpty) return;
        for (int y = rect.Top / Tile; y <= (rect.Bottom - 1) / Tile; y++)
        for (int x = rect.Left / Tile; x <= (rect.Right - 1) / Tile; x++)
        {
            tiles.Add(y * storage.Columns + x);
            if (tiles.Count > MaxInheritedTiles) return;
        }
    }
}
