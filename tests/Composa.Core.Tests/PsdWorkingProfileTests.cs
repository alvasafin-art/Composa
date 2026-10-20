using Composa.ColorManagement;
using Composa.Editing;
using Composa.IO.Psd;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class PsdWorkingProfileTests
{
    [Fact]
    public void Independent_Adobe_RGB_fixture_keeps_the_original_ICC_and_16_bit_numbers_when_requested()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Precision", "rgb16-adobe-rgb.psd"));
        var raw = PsdReader.Read(bytes, DocumentLimits.DocumentPixelBudget);
        var import = PsdImport.Load(bytes, DocumentLimits.DocumentPixelBudget, preserveProfile: true);
        try
        {
            Assert.Equal(raw.ColorProfile, import.Profile.IccBytes()); Assert.Equal(import.Profile, import.ToDocument().Profile);
            Assert.DoesNotContain(import.Conversions, c => c.Message.Contains("converted to the editor's sRGB"));
            var expected = raw.Layers.First(l => l.Image != null).Image!;
            var actual = import.Layers.First(l => l.Pixels != null).Pixels!;
            Assert.Equal(ColorDepth.Sixteen, Pixels.DepthOf(actual)); Assert.Equal(expected.Bytes, actual.Bytes);
            Assert.Equal(import.Profile, Pixels.ProfileOf(actual));
        }
        finally
        {
            import.Discard(); foreach (var layer in raw.Layers) { layer.Image?.Dispose(); layer.MaskImage?.Dispose(); } raw.Composite?.Dispose();
        }
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Native_PSD_keeps_parent_profile_and_editable_live_color_numbers(ColorDepth depth)
    {
        var doc = new Document(128, 96) { Depth = depth, Profile = RgbProfile.Rgb1998 };
        var session = new EditorSession(doc); session.AddBlankLayer(); session.Fill(new(100, 150, 200));
        var shape = session.AddShape(new ShapeStyle(ShapeKind.Rectangle, 0xFF6496C8, 0), new(0, 0, 60, 40))!;
        session.AddText(new(10, 10), new() { Text = "RGB", Size = 24, Color = 0xFF6496C8 });
        using var stream = new MemoryStream(); PsdExport.Write(doc, stream);
        var import = PsdImport.Load(stream.ToArray(), DocumentLimits.DocumentPixelBudget, preserveProfile: true);
        try
        {
            Assert.Equal(doc.Profile, import.Profile); Assert.Equal(depth, import.Depth);
            Assert.Equal(0xFF6496C8u, import.Layers.Single(l => l.Shape != null).Shape!.Fill);
            Assert.Equal(0xFF6496C8u, import.Layers.Single(l => l.Text != null).Text!.Color);
            var target = EditorSession.NewCanvas(128, 96, null, depth);
            target.PlacePhotoshop(import, "Foreign PSD");
            var placed = target.Document.AllLayers().Single(l => l.Shape != null);
            var expectedColor = doc.Profile.ConvertColor(new SKColor(0xFF6496C8), RgbProfile.Srgb);
            Assert.Equal((uint)expectedColor, placed.Shape!.Fill);
            Assert.Equal(RgbProfile.Srgb, Pixels.ProfileOf(placed.Pixels!));
            target.Undo(); Assert.Single(target.Document.Layers);
        }
        finally { import.Discard(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PSD_import_retains_shared_smart_sources_profiles_and_converts_only_the_parent_preview(bool embeddedPng)
    {
        var nested = EditorSession.NewCanvas(12, 12, SKColors.Green, ColorDepth.Sixteen);
        nested.AssignColorProfile(RgbProfile.DisplayP3);
        using var stream = new MemoryStream();
        if (embeddedPng) stream.Write(ImageFiles.Encode(nested.ActiveLayer!.Pixels!, ExportFormat.Png));
        else PsdExport.Write(nested.Document, stream);
        var id = Guid.NewGuid().ToString(); var writer = new PsdWriter { ColorProfile = RgbProfile.Rgb1998.IccBytes() };
        foreach (var name in new[] { "First", "Second" })
            writer.Layers.Add(new PsdWriterLayer { Name = name }.With("SoLd", PsdSmartAndEffectsTests.Placed(id, [10, 10, 22, 10, 22, 22, 10, 22])));
        writer.GlobalExtra.Add(("lnk2", PsdSmartAndEffectsTests.Linked(id, stream.ToArray())));
        var import = PsdImport.Load(writer.Build(), DocumentLimits.DocumentPixelBudget, preserveProfile: true);
        try
        {
            Assert.Empty(import.Conversions); Assert.Equal(RgbProfile.Rgb1998, import.Profile);
            var first = import.Layers[0]; var second = import.Layers[1];
            Assert.Same(first.SmartObject, second.SmartObject); Assert.Same(first.Pixels, second.Pixels);
            Assert.Equal(RgbProfile.DisplayP3, first.SmartObject!.OpenDocument().Profile);
            Assert.Equal(import.Profile, Pixels.ProfileOf(first.Pixels!));
            using var expected = Pixels.ConvertWorking(first.SmartObject.Preview, Pixels.DepthOf(first.Pixels!), import.Profile);
            Assert.Equal(expected.Bytes, first.Pixels!.Bytes);
        }
        finally { import.Discard(); }
    }
}
