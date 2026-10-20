using System.IO.Compression;
using System.Text.Json.Nodes;
using Composa.Editing;
using Composa.Filters;
using Composa.IO;
using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

public class BlendIfTests
{
    [Fact]
    public void Converting_a_layer_to_a_smart_object_keeps_its_ranges_outside_the_embedded_source()
    {
        var document = Scene(out var top);
        document.SetActive(top.Id);
        top.BlendIf = new() { Gray = new() { ThisLayer = new(0, 128, 255, 255) } };
        using var before = DocumentRenderer.Flatten(document);
        var session = new EditorSession(document);
        var smart = session.ConvertToSmartObject();
        Assert.True(smart.SmartObject!.OpenDocument().Layers[0].BlendIf.IsIdentity);
        using var after = DocumentRenderer.Flatten(session.Document);
        Assert.Equal(before.Bytes, after.Bytes);
    }

    private static Document Scene(out Layer top)
    {
        var document = new Document(256, 8);
        var background = Pixels.NewColor(256, 8);
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 256; x++) background.SetPixel(x, y, new SKColor((byte)x, (byte)x, (byte)x));
        Pixels.Invalidate(background);
        document.Layers.Add(Layer.Raster("Background", background));
        top = Layer.Raster("Red", Solid(256, 8, SKColors.Red));
        document.Layers.Add(top);
        return document;
    }

    [Fact]
    public void Underlying_gray_split_handles_reveal_both_extremes_and_fade_between_them()
    {
        var document = Scene(out var top);
        top.BlendIf = new() { Gray = new() { Underlying = new(32, 96, 160, 224) } };
        using var composite = DocumentRenderer.Flatten(document);
        AssertColor(new SKColor(16, 16, 16), composite.GetPixel(16, 4));
        AssertColor(new SKColor(160, 32, 32), composite.GetPixel(64, 4));
        AssertColor(SKColors.Red, composite.GetPixel(128, 4));
        AssertColor(new SKColor(224, 96, 96), composite.GetPixel(192, 4));
        AssertColor(new SKColor(240, 240, 240), composite.GetPixel(240, 4));
    }

    [Fact]
    public void Source_RGB_ranges_use_straight_color_and_keep_the_backdrop_unchanged()
    {
        var document = Scene(out var top);
        top.Pixels = Solid(256, 8, new SKColor(255, 0, 0, 128));
        top.BlendIf = new() { Red = new() { ThisLayer = new(0, 0, 200, 240) } };
        using var composite = DocumentRenderer.Flatten(document);
        AssertColor(new SKColor(128, 128, 128), composite.GetPixel(128, 4));
        Assert.Equal(128, top.Pixels.GetPixel(128, 4).Alpha);
    }

    [Fact]
    public void Underlying_ranges_do_not_remove_a_source_over_an_empty_backdrop()
    {
        var document = new Document(8, 8);
        var top = Layer.Raster("Red", Solid(8, 8, SKColors.Red));
        top.BlendIf = new() { Gray = new() { Underlying = new(100, 100, 255, 255) } };
        document.Layers.Add(top);
        using var composite = DocumentRenderer.Flatten(document);
        AssertColor(SKColors.Red, composite.GetPixel(4, 4));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ranges_also_limit_adjustments_and_pass_through_groups(bool group)
    {
        var document = Scene(out var top);
        document.Layers.Remove(top);
        var adjustment = Layer.ForAdjustment(new InvertAdjustment());
        var controlled = group ? Layer.Group("Group") : adjustment;
        if (group) controlled.Children.Add(adjustment);
        controlled.BlendIf = new() { Gray = new() { Underlying = new(0, 0, 127, 127) } };
        document.Layers.Add(controlled);
        using var composite = DocumentRenderer.Flatten(document);
        AssertColor(new SKColor(223, 223, 223), composite.GetPixel(32, 4));
        AssertColor(new SKColor(200, 200, 200), composite.GetPixel(200, 4));
    }

    [Fact]
    public void Settings_undo_and_round_trip_through_project_and_native_PSD_ranges()
    {
        var document = Scene(out var top);
        top.Transform = top.Transform with { Rotation = 8 }; // Exercise the PSD appearance-cache path too.
        var session = new EditorSession(document);
        var settings = new BlendIfSettings
        {
            Gray = new() { Underlying = new(32, 80, 192, 240) },
            Red = new() { ThisLayer = new(0, 0, 224, 255) }
        };
        session.SetBlending(top, 1, settings);
        using var stream = new MemoryStream();
        ProjectFile.Write(document, stream);
        stream.Position = 0;
        var project = ProjectFile.Read(stream);
        Assert.Equal(settings, project.Layers[1].BlendIf);
        Assert.Same(top.BlendIf, document.Clone().Layers[1].BlendIf);
        using var psd = new MemoryStream();
        PsdExport.Write(document, psd);
        var imported = PsdImport.Load(psd.ToArray());
        Assert.Equal(settings, imported.Layers[1].BlendIf);
        using var before = DocumentRenderer.Flatten(document);
        using var after = DocumentRenderer.Flatten(imported.ToDocument());
        AssertColor(before.GetPixel(128, 4), after.GetPixel(128, 4), 2);
        session.Undo();
        Assert.True(session.Document.Layers[1].BlendIf.IsIdentity);
        session.Redo();
        Assert.Equal(settings, session.Document.Layers[1].BlendIf);
    }

    [Fact]
    public void Version_10_groups_keep_the_old_pass_through_appearance()
    {
        var document = Scene(out var top);
        document.Layers.Remove(top);
        var group = Layer.Group("Old group");
        group.Children.Add(Layer.ForAdjustment(new InvertAdjustment()));
        document.Layers.Add(group);
        using var stream = new MemoryStream();
        ProjectFile.Write(document, stream);
        stream.Position = 0;
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry("manifest.json")!;
            JsonNode root;
            using (var input = entry.Open()) root = JsonNode.Parse(input)!;
            root["version"] = 10;
            root["layers"]![1]!["blend"] = "normal";
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            writer.Write(root.ToJsonString());
        }
        stream.Position = 0;
        var loaded = ProjectFile.Read(stream);
        Assert.Equal(BlendMode.PassThrough, loaded.Layers[1].Blend);
        using var composite = DocumentRenderer.Flatten(loaded);
        AssertColor(new SKColor(223, 223, 223), composite.GetPixel(32, 4));
    }
}
