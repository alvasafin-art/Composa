using System.Text;
using Composa.Editing;
using Composa.IO;
using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;
using D = Composa.Core.Tests.PsdWriter.Descriptor;
using B = Composa.Core.Tests.PsdWriter.Buffer;

namespace Composa.Core.Tests;

public class PsdSmartAndEffectsTests
{
    private static byte[] Versioned(D descriptor, bool effect = false)
    { var b = new B(); if (effect) b.U32(0); b.U32(16); b.Bytes(descriptor.ToArray()); return b.ToArray(); }
    private static SKBitmap Solid(int w, int h, SKColor color) { var b = Pixels.NewColor(w, h); b.Erase(color); return b; }

    [Theory]
    [InlineData("lfx2", false)]
    [InlineData("lmfx", true)]
    public void Photoshop_shadows_are_editable_and_zero_fill_does_not_hide_the_shadow(string key, bool multi)
    {
        using var image = Solid(20, 15, SKColors.Red);
        var shadow = new D().Add("enab", D.Bool(true)).Add("Md  ", D.Enum("BlnM", "Mltp"))
            .Add("Clr ", D.Objc(PsdWriter.Rgb(SKColors.Black))).Add("Opct", D.UntF("#Prc", 70))
            .Add("lagl", D.UntF("#Ang", 90)).Add("Dstn", D.UntF("#Pxl", 8)).Add("blur", D.UntF("#Pxl", 0));
        var effects = new D().Add("Scl ", D.UntF("#Prc", 100)).Add("masterFXSwitch", D.Bool(true))
            .Add(multi ? "dropShadowMulti" : "DrSh", multi ? D.List(D.Objc(shadow)) : D.Objc(shadow));
        var writer = new PsdWriter(); writer.Layers.Add(new PsdWriterLayer { Image = image, Left = 20, Top = 20 }.With(key, Versioned(effects, true)).With("iOpa", [0]));
        var imported = PsdImport.Load(writer.Build()); Assert.Empty(imported.Conversions);
        var layer = Assert.Single(imported.Layers); var actual = Assert.IsType<ShadowEffect>(layer.Effects!.Shadow);
        Assert.Equal(8, actual.Distance); Assert.Equal(.7, actual.Opacity, 3); Assert.Equal(90, actual.Angle); Assert.Equal(0, layer.FillOpacity);
        using var rendered = DocumentRenderer.Flatten(imported.ToDocument());
        Assert.Equal(0, rendered.GetPixel(25, 23).Alpha); Assert.InRange(rendered.GetPixel(25, 39).Alpha, 175, 180);
        using var project = new MemoryStream(); ProjectFile.Write(imported.ToDocument(), project); project.Position = 0;
        var loaded = ProjectFile.Read(project).Layers[0]; Assert.Equal(layer.Effects, loaded.Effects); Assert.Equal(0, loaded.FillOpacity);
        imported.Discard();
    }

    [Fact]
    public void Exporting_live_text_with_fill_opacity_preserves_transparency_in_compatibility_pixels()
    {
        var session = EditorSession.NewCanvas(200, 100);
        var layer = session.AddText(new SKPoint(20, 20), new TextStyle { Text = "Fill", Size = 32 });
        layer.FillOpacity = .25;
        Assert.Contains(PsdExport.Conversions(session.Document), c => c.Message.Contains("fill opacity"));
        using var stream = new MemoryStream(); PsdExport.Write(session.Document, stream);
        var imported = PsdImport.Load(stream.ToArray());
        Assert.All(imported.Layers, l => Assert.Null(l.Text));
        using var before = DocumentRenderer.Flatten(session.Document);
        using var after = DocumentRenderer.Flatten(imported.ToDocument());
        var a = before.GetPixelSpan(); var b = after.GetPixelSpan();
        for (var i = 0; i < a.Length; i++) Assert.InRange(Math.Abs(a[i] - b[i]), 0, 1);
        imported.Discard();
    }

    private static byte[] Placed(string id, double[] quad)
    {
        var b = new B(); b.Ascii("soLD"); b.U32(4); b.U32(16);
        b.Bytes(new D().Add("Idnt", D.Text(id)).Add("Trnf", D.List(quad.Select(D.Doub).ToArray()))
            .Add("warp", D.Objc(new D().Add("warpStyle", D.Enum("warpStyle", "warpNone")))).ToArray());
        return b.ToArray();
    }

    private static byte[] Linked(string id, byte[] data, string type = "liFD")
    {
        var r = new B(); r.Ascii(type); r.U32(1); r.U8((byte)id.Length); r.Ascii(id);
        var name = "Contents.psb"; r.U32((uint)name.Length); r.Bytes(Encoding.BigEndianUnicode.GetBytes(name));
        r.Ascii("8BPS"); r.Ascii("8BIM"); r.U32(0); r.U32((uint)data.Length); r.U8(0); r.Bytes(data);
        var contents = r.ToArray(); var block = new B(); block.U32(0); block.U32((uint)contents.Length); block.Bytes(contents); block.Zeros((4 - contents.Length % 4) % 4);
        return block.ToArray();
    }

    [Theory]
    [InlineData(1, "SoLd", false)]
    [InlineData(2, "SoLE", true)]
    public void Embedded_psb_contents_rebuild_as_shared_editable_smart_objects_with_placement_and_masks(int version, string placedKey, bool perspective)
    {
        using var contents = Solid(20, 10, SKColors.Red);
        var inner = new PsdWriter { Width = 20, Height = 10, Version = 2 }; inner.Layers.Add(new PsdWriterLayer { Image = contents, Name = "Inside" });
        var id = Guid.NewGuid().ToString();
        double[] quad = perspective ? [20, 20, 60, 22, 55, 42, 22, 40] : [20, 20, 60, 20, 60, 40, 20, 40];
        var writer = new PsdWriter { Version = (ushort)version };
        using var cache = Solid(40, 20, SKColors.Blue); using var mask = Pixels.NewMask(40, 20, 255);
        writer.Layers.Add(new PsdWriterLayer { Image = cache, Name = "First", Left = 20, Top = 20, Mask = mask, MaskLeft = 20, MaskTop = 20 }.With(placedKey, Placed(id, quad)));
        writer.Layers.Add(new PsdWriterLayer { Image = cache, Name = "Second", Left = 20, Top = 20 }.With(placedKey, Placed(id, quad)));
        writer.GlobalExtra.Add(("lnk2", Linked(id, inner.Build())));
        var imported = PsdImport.Load(writer.Build()); Assert.Empty(imported.Conversions); Assert.Equal(2, imported.Layers.Count);
        var first = imported.Layers[0]; var second = imported.Layers[1];
        Assert.True(first.IsSmartObject); Assert.Same(first.SmartObject, second.SmartObject); Assert.Same(first.Pixels, second.Pixels);
        Assert.Equal(SKColors.Red, first.Pixels!.GetPixel(10, 5)); Assert.Equal(20, first.Mask!.Width); Assert.Equal(10, first.Mask.Height);
        var corners = first.Transform.Corners(first.Pixels.Width, first.Pixels.Height);
        for (var i = 0; i < 4; i++) { Assert.InRange(Math.Abs(corners[i].X - quad[i * 2]), 0, .02); Assert.InRange(Math.Abs(corners[i].Y - quad[i * 2 + 1]), 0, .02); }
        var session = new EditorSession(imported.ToDocument()); var editable = first.SmartObject!.OpenDocument();
        Assert.Equal("Inside", editable.Layers[0].Name);
        editable.Layers[0].Pixels = Solid(20, 10, SKColors.Green);
        session.UpdateSmartObject(first.SmartObject, editable);
        Assert.Equal(SKColors.Green, first.Pixels.GetPixel(10, 5)); Assert.Same(first.SmartObject, second.SmartObject);
        session.Undo(); Assert.Equal(SKColors.Red, session.Document.Layers[0].Pixels!.GetPixel(10, 5));
        using var project = new MemoryStream(); ProjectFile.Write(session.Document, project); project.Position = 0;
        var loaded = ProjectFile.Read(project); Assert.True(loaded.Layers[0].IsSmartObject); Assert.Same(loaded.Layers[0].SmartObject, loaded.Layers[1].SmartObject);
        imported.Discard();
    }

    [Fact]
    public void Embedded_editable_text_and_png_sources_are_kept_in_the_smart_document()
    {
        var inner = EditorSession.NewCanvas(80, 45);
        inner.AddText(new SKPoint(4, 4), new TextStyle { Text = "Live", Size = 12 }.WithSize(20, 1, 3));
        using var native = new MemoryStream(); PsdExport.Write(inner.Document, native);
        using var png = Solid(80, 45, SKColors.Coral);
        foreach (var data in new[] { native.ToArray(), ImageFiles.Encode(png, ExportFormat.Png) })
        {
            var id = Guid.NewGuid().ToString(); var writer = new PsdWriter();
            writer.Layers.Add(new PsdWriterLayer { Name = "Embedded" }.With("SoLd", Placed(id, [10, 10, 90, 10, 90, 55, 10, 55])));
            writer.GlobalExtra.Add(("lnkD", Linked(id, data)));
            var imported = PsdImport.Load(writer.Build()); Assert.Empty(imported.Conversions);
            var layer = Assert.Single(imported.Layers); Assert.True(layer.IsSmartObject);
            var document = layer.SmartObject!.OpenDocument();
            if (PsdImport.IsPsd(data))
            { var text = Assert.Single(document.Layers, l => l.Text != null); Assert.Equal("Live", text.Text!.Text); Assert.Equal(20, text.Text.SizeAt(1)); }
            else Assert.Equal(SKColors.Coral, document.Layers[0].Pixels!.GetPixel(20, 20));
            imported.Discard();
        }
    }

    [Fact]
    public void Missing_external_smart_content_keeps_compatibility_pixels_and_reports_the_conversion()
    {
        using var cache = Solid(12, 8, SKColors.Coral); var writer = new PsdWriter();
        writer.Layers.Add(new PsdWriterLayer { Image = cache }.With("SoLd", Placed("missing", [0, 0, 12, 0, 12, 8, 0, 8])));
        var import = PsdImport.Load(writer.Build());
        Assert.False(import.Layers[0].IsSmartObject); Assert.Equal(SKColors.Coral, import.Layers[0].Pixels!.GetPixel(2, 2));
        Assert.Contains(import.Conversions, c => c.Message.Contains("externally linked")); import.Discard();
    }
}
