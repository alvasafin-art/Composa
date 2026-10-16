using Composa.Editing;
using Composa.Filters;
using Composa.IO;
using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class ColorDepthSessionTests
{
    [Fact]
    public void Precise_placed_sources_do_not_change_parent_mode_and_export_as_valid_PSD8()
    {
        var session = EditorSession.NewCanvas(32, 24, SKColors.White);
        var source = Pixels.NewColor(32, 24, ColorDepth.Sixteen); source.Erase(SKColors.CornflowerBlue);
        session.AddImageLayer("Precise original", source, fit: false);
        Assert.Equal(ColorDepth.Eight, session.Document.WorkingDepth);
        using var flat = session.Flatten(); Assert.Equal(SKColorType.Rgba8888, flat.ColorType);
        using var stream = new MemoryStream(); PsdExport.Write(session.Document, stream);
        var imported = PsdImport.Load(stream.ToArray());
        try { Assert.Equal(SKColors.CornflowerBlue, imported.Layers[^1].Pixels!.GetPixel(0, 0)); }
        finally { imported.Discard(); }
        var bytes = source.Bytes; session.Fill(SKColors.Red);
        Assert.Equal(SKColorType.Rgba8888, session.ActiveLayer!.Pixels!.ColorType);
        Assert.Equal(bytes, source.Bytes); session.Undo(); Assert.Same(source, session.ActiveLayer!.Pixels);
    }

    [Fact]
    public void Embedded_originals_keep_their_mode_while_parent_cache_survives_conversion_save_and_update()
    {
        var session = EditorSession.NewCanvas(32, 24, SKColors.Red, ColorDepth.Sixteen);
        var layer = session.ConvertToSmartObject(); var original = layer.SmartObject!;
        session.ChangeColorDepth(ColorDepth.Eight);
        Assert.Equal(ColorDepth.Eight, session.Document.WorkingDepth);
        Assert.Equal(ColorDepth.Sixteen, original.OpenDocument().WorkingDepth);
        using var stream = new MemoryStream(); ProjectFile.Write(session.Document, stream); stream.Position = 0;
        var loaded = new EditorSession(ProjectFile.Read(stream));
        Assert.Equal(ColorDepth.Eight, loaded.Document.WorkingDepth);
        Assert.Equal(SKColorType.Rgba8888, loaded.ActiveLayer!.Pixels!.ColorType);
        Assert.Equal(SKColorType.RgbaF32, loaded.ActiveLayer.SmartObject!.Preview.ColorType);
        var content = new EditorSession(original.OpenDocument()); content.Fill(SKColors.Blue);
        session.UpdateSmartObject(original, content.Document);
        Assert.Equal(ColorDepth.Eight, session.Document.WorkingDepth);
        Assert.Equal(SKColors.Blue, session.ActiveLayer!.Pixels!.GetPixel(0, 0));
        var independent = session.DuplicateSmartObjectIndependent(session.ActiveLayer);
        Assert.Equal(SKColorType.Rgba8888, independent.Pixels!.ColorType);
        Assert.Equal(ColorDepth.Sixteen, independent.SmartObject!.OpenDocument().WorkingDepth);
    }

    [Fact]
    public void Depth_conversion_is_undoable_and_does_not_change_shared_history_pixels()
    {
        var session = EditorSession.NewCanvas(512, 2, null, ColorDepth.Sixteen);
        var original = session.ActiveLayer!.Pixels!;
        var pixels = new PixelBuffer(original);
        for (var x = 0; x < 512; x++) pixels.Write(x, 0, new((24000 + x) / 65535f, .5f, .7f, 1));
        var bytes = original.Bytes;
        session.ChangeColorDepth(ColorDepth.Eight);
        Assert.Equal(ColorDepth.Eight, session.Document.WorkingDepth);
        Assert.Equal(SKColorType.Rgba8888, session.ActiveLayer!.Pixels!.ColorType);
        Assert.Equal(bytes, original.Bytes);
        session.Undo();
        Assert.Equal(ColorDepth.Sixteen, session.Document.WorkingDepth); Assert.Same(original, session.ActiveLayer!.Pixels);
        Assert.Equal(512, Enumerable.Range(0, 512).Select(x => new PixelBuffer(session.ActiveLayer.Pixels!).Read(x, 0).R).Distinct().Count());
        session.Redo(); session.ChangeColorDepth(ColorDepth.Sixteen);
        Assert.Equal(ColorDepth.Sixteen, session.Document.Depth);
        var state = session.History.CurrentId; session.ChangeColorDepth(ColorDepth.Sixteen); Assert.Equal(state, session.History.CurrentId);
    }

    [Fact]
    public void Conversion_shares_identical_buffers_and_preserves_filter_source_precision()
    {
        var session = EditorSession.NewCanvas(64, 48, SKColors.Gray);
        var shared = session.ActiveLayer!.Pixels!;
        session.AddImageLayer("Shared", shared, fit: false);
        session.ChangeColorDepth(ColorDepth.Sixteen);
        Assert.Same(session.Document.Layers[0].Pixels, session.Document.Layers[1].Pixels);
        var layer = session.ActiveLayer!;
        session.AddSmartFilter(layer, new() { Kind = FilterKind.AddNoise, Amount = .01 });
        Assert.Equal(SKColorType.RgbaF32, layer.FilterSource!.ColorType);
        session.ChangeColorDepth(ColorDepth.Eight);
        Assert.Equal(SKColorType.Rgba8888, session.ActiveLayer!.FilterSource!.ColorType);
        session.Undo(); Assert.Equal(SKColorType.RgbaF32, session.ActiveLayer!.FilterSource!.ColorType);
    }

    [Fact]
    public void An_8bit_import_is_promoted_before_a_16bit_document_pixel_edit()
    {
        var session = EditorSession.NewCanvas(48, 32, SKColors.White, ColorDepth.Sixteen);
        var source = Pixels.NewColor(48, 32); source.Erase(SKColors.Gray);
        var bytes = source.Bytes;
        session.AddImageLayer("Imported", source, fit: false);
        session.BeginGradient(session.ActiveLayer!);
        session.Foreground = SKColors.Black; session.Background = SKColors.White;
        session.DrawGradient(session.ActiveLayer!, session.ActiveLayer!.Pixels!, new(0, 16), new(48, 16)); session.Commit();
        Assert.Equal(SKColorType.RgbaF32, session.ActiveLayer.Pixels!.ColorType);
        Assert.Equal(bytes, source.Bytes);
        session.Undo(); Assert.Same(source, session.ActiveLayer!.Pixels);
    }

    [Fact]
    public void Oversized_high_precision_allocations_are_refused_before_allocating()
    {
        Assert.True(DocumentLimits.FitsSurface(8000, 8000));
        Assert.False(DocumentLimits.FitsSurface(8000, 8000, 16));
        Assert.Throws<InvalidOperationException>(() => EditorSession.NewCanvas(8000, 8000, null, ColorDepth.Sixteen));
        Assert.Throws<InvalidOperationException>(() => Pixels.NewColor(8000, 8000, ColorDepth.Sixteen));
        var session = new EditorSession(new Document(8000, 8000));
        var state = session.History.CurrentId;
        Assert.Throws<InvalidOperationException>(() => session.ChangeColorDepth(ColorDepth.Sixteen));
        Assert.Equal(state, session.History.CurrentId); Assert.False(session.IsInteracting);
    }
}
