using Composa.ColorManagement;
using Composa.Editing;
using Composa.Filters;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class ColorProfileSessionTests
{
    [Fact]
    public void Assign_reinterprets_numbers_including_hidden_sources_but_preserves_independent_smart_contents()
    {
        var session = EditorSession.NewCanvas(32, 24, new SKColor(100, 150, 200), ColorDepth.Sixteen);
        var original = session.ActiveLayer!.Pixels!; var bytes = original.Bytes;
        var hidden = session.AddImageLayer("Hidden shared source", original, fit: false); hidden.Visible = false;
        session.AddMask(hidden); var mask = hidden.Mask; session.SelectRect(new(2, 3, 12, 16)); var selection = session.Document.Selection;
        session.SelectLayer(session.Document.Layers[0].Id); var smart = session.ConvertToSmartObject(); var embedded = smart.SmartObject!;
        var state = session.History.CurrentId; session.AssignColorProfile(RgbProfile.Rgb1998);
        Assert.NotEqual(state, session.History.CurrentId); Assert.Equal(RgbProfile.Rgb1998, session.Document.Profile);
        Assert.Equal(bytes, session.Document.Find(hidden.Id)!.Pixels!.Bytes); Assert.Equal(bytes, original.Bytes);
        Assert.Same(mask, session.Document.Find(hidden.Id)!.Mask); Assert.Same(selection, session.Document.Selection);
        Assert.Same(embedded, session.Document.Find(smart.Id)!.SmartObject); Assert.Equal(RgbProfile.Srgb, embedded.OpenDocument().Profile);
        using var expected = Pixels.ConvertWorking(embedded.Preview, ColorDepth.Sixteen, RgbProfile.Rgb1998);
        Assert.Equal(expected.Bytes, session.Document.Find(smart.Id)!.Pixels!.Bytes);
        var assigned = session.Document.Find(smart.Id)!.Pixels!.Bytes;
        session.UpdateSmartObject(embedded, embedded.OpenDocument()); Assert.Equal(assigned, session.Document.Find(smart.Id)!.Pixels!.Bytes);
        session.Undo(); session.Undo(); Assert.Equal(RgbProfile.Srgb, session.Document.Profile); Assert.Same(original, session.Document.Find(hidden.Id)!.Pixels);
        session.Redo(); Assert.Equal(RgbProfile.Rgb1998, session.Document.Profile);
    }

    [Fact]
    public void Flattened_conversion_keeps_composite_and_low_bits_and_undo_restores_live_layers()
    {
        var document = new Document(512, 8) { Depth = ColorDepth.Sixteen, Profile = RgbProfile.Rgb1998 };
        var image = Pixels.NewWorkingColor(512, 8, document.Depth, document.Profile); var pixels = new PixelBuffer(image);
        for (var y = 0; y < 8; y++) for (var x = 0; x < 512; x++) pixels.Write(x, y, new PixelValue((24000 + x) / 65535f, .4f, .6f, .8f).Premultiplied);
        document.InsertAboveActive(Layer.Raster("Original", image)); var session = new EditorSession(document);
        session.AddAdjustmentLayer(new ExposureAdjustment { Exposure = .25 });
        using var before = session.Flatten(); using var expected = Pixels.ConvertWorking(before, ColorDepth.Sixteen, RgbProfile.DisplayP3);
        var state = session.History.CurrentId;
        session.ConvertColorProfile(RgbProfile.DisplayP3);
        Assert.Single(session.Document.Layers); Assert.Equal(RgbProfile.DisplayP3, session.Document.Profile);
        using var after = session.Flatten(); Assert.Equal(expected.Bytes, after.Bytes);
        Assert.Equal(512, Enumerable.Range(0, 512).Select(x => new PixelBuffer(after).Read(x, 4).R).Distinct().Count());
        session.Undo(); Assert.Equal(state, session.History.CurrentId); Assert.Equal(2, session.Document.Layers.Count);
        Assert.Same(image, session.Document.Layers[0].Pixels); Assert.IsType<ExposureAdjustment>(session.Document.Layers[1].Adjustment);
    }

    [Fact]
    public void Layer_preserving_conversion_updates_live_color_runs_and_effects_so_future_edits_match()
    {
        var session = EditorSession.NewCanvas(128, 96, null, ColorDepth.Sixteen);
        var color = new SKColor(100, 150, 200);
        var shape = session.AddShape(new ShapeStyle(ShapeKind.Rectangle, (uint)color, 0) { Stroke = (uint)SKColors.Red }, new(4, 4, 80, 60))!;
        session.SetEffects(shape, new() { ColorOverlay = new() { Color = (uint)color, Opacity = .2 } });
        var text = session.AddText(new(0, 0), new() { Text = "RGB", Color = (uint)color, ColorRuns = [new(1, 1, (uint)SKColors.Red)] });
        session.ConvertColorProfile(RgbProfile.Rgb1998, flatten: false);
        var convertedShape = session.Document.Find(shape.Id)!; var convertedText = session.Document.Find(text.Id)!;
        using var swatches = new RgbColorConverter(RgbProfile.Srgb, RgbProfile.Rgb1998);
        Assert.Equal(swatches.Convert((uint)color), convertedShape.Shape!.Fill);
        Assert.Equal(swatches.Convert((uint)SKColors.Red), convertedText.Text!.ColorRuns![0].Color);
        Assert.Equal(swatches.Convert((uint)color), convertedShape.Effects!.ColorOverlay!.Color);
        var original = convertedShape.Pixels!.Bytes;
        session.ChangeShapeStyle(convertedShape, convertedShape.Shape with { CornerRadius = 10 });
        Assert.Equal(original, convertedShape.Pixels!.Bytes);
        using var stream = new MemoryStream(); ProjectFile.Write(session.Document, stream); stream.Position = 0;
        var restored = new EditorSession(ProjectFile.Read(stream));
        Assert.Equal(RgbProfile.Rgb1998, restored.Document.Profile);
        Assert.Equal(convertedText.Text, restored.Document.Find(text.Id)!.Text);
    }
}
