using Composa.Editing;
using Composa.IO;
using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;
using Descriptor = Composa.Core.Tests.PsdWriter.Descriptor;

namespace Composa.Core.Tests;

public class PsdStrokeTests
{
    [Fact]
    public void PostScript_lookup_prefers_exact_spelling_and_requires_a_unique_folded_match()
    {
        var regular = new TextFace("Fixture", false, false);
        var semibold = regular with { FontStyle = new(600, 5, SKFontStyleSlant.Upright) };
        var faces = new Dictionary<string, TextFace> { ["Fixture-Semibold"] = semibold };
        Assert.Equal(semibold, PsdFonts.FindInstalled("Fixture-SemiBold", faces, out var ambiguous));
        Assert.False(ambiguous);

        faces["Fixture-SEMIBOLD"] = regular;
        Assert.Equal(semibold, PsdFonts.FindInstalled("Fixture-Semibold", faces, out ambiguous));
        Assert.False(ambiguous);
        Assert.Null(PsdFonts.FindInstalled("Fixture-SemiBold", faces, out ambiguous));
        Assert.True(ambiguous);
    }

    [Theory]
    [InlineData("#Nne", 3, 2, true)]
    [InlineData("#Pxl", 6, 4, false)]
    [InlineData("#Pnt", 3, 2, false)]
    public void Dashed_open_PSD_path_uses_width_units_and_point_offset_after_edit_and_reopen(string unit, double on, double off, bool relative)
    {
        const int side = 50;
        var writer = new PsdWriter { Width = side, Height = side, Resolution = 144 };
        writer.Layers.Add(new PsdWriterLayer().With("vmsk", PsdWriter.OpenVectorPath(side, side, new(40, 25), new(10, 25)))
            .With("vstk", PsdWriter.StrokeSettings(false, true, 1, SKColors.Blue, unit: "#Pnt", resolution: 144,
                dash: [on, off, 0, 0], dashUnit: unit, offset: 1)));
        var import = PsdImport.Load(writer.Build());
        Assert.Empty(import.Conversions);
        var doc = import.ToDocument();
        var layer = Assert.Single(doc.Layers);
        var style = Assert.IsType<ShapeStyle>(layer.Shape);
        Assert.False(style.RoundCaps);
        Assert.Equal(2, style.StrokeWidth);
        Assert.Equal(new StrokeDash(relative ? [3, 2] : [6, 4], 2, relative), style.Dash);
        using (var pixels = DocumentRenderer.Flatten(doc))
        {
            Assert.Equal(SKColors.Blue, pixels.GetPixel(11, 24));
            Assert.Equal(0, pixels.GetPixel(16, 24).Alpha);
            Assert.Equal(SKColors.Blue, pixels.GetPixel(21, 24));
        }

        var session = new EditorSession(doc);
        var path = style.Path!;
        var nodes = path.Nodes.ToArray();
        nodes[^1] = nodes[^1] with { X = nodes[^1].X - .02, InX = nodes[^1].InX - .02 };
        session.ChangeShapeStyle(layer, style with { Path = path with { Nodes = nodes } });
        Assert.NotEqual(style.Path, layer.Shape!.Path);
        Assert.Equal(layer.Shape, Reopen(session.Document).Layers[0].Shape);
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(2, 16)]
    public void Angular_stroke_keeps_direction_transparency_and_center_in_PSD_and_PSB(int version, int depth)
    {
        byte[] Stop(int loc, SKColor color) => Descriptor.Objc(new Descriptor().Add("Lctn", Descriptor.Long(loc))
            .Add("Mdpn", Descriptor.Long(50)).Add("Clr ", Descriptor.Objc(PsdWriter.Rgb(color))));
        byte[] Opacity(int loc, double amount) => Descriptor.Objc(new Descriptor().Add("Lctn", Descriptor.Long(loc))
            .Add("Mdpn", Descriptor.Long(50)).Add("Opct", Descriptor.UntF("#Prc", amount)));
        var stops = new Descriptor().Add("GrdF", Descriptor.Enum("GrdF", "CstS"))
            .Add("Clrs", Descriptor.List(Stop(0, SKColors.Red), Stop(2048, SKColors.Blue), Stop(4096, SKColors.Lime)))
            .Add("Trns", Descriptor.List(Opacity(0, 100), Opacity(4096, 50)));
        var offset = new Descriptor().Add("Hrzn", Descriptor.UntF("#Prc", 10)).Add("Vrtc", Descriptor.UntF("#Prc", -5));
        var paint = new Descriptor().Add("Type", Descriptor.Enum("GrdT", "Angl")).Add("Angl", Descriptor.UntF("#Ang", 90))
            .Add("gradientsInterpolationMethod", Descriptor.Enum("gradientsInterpolationMethod", "Smoo"))
            .Add("Ofst", Descriptor.Objc(offset)).Add("Grad", Descriptor.Objc(stops));
        const int side = 100;
        var writer = new PsdWriter { Width = side, Height = side, Version = (ushort)version, Depth = (ushort)depth };
        writer.Layers.Add(new PsdWriterLayer().With("vmsk", PsdWriter.VectorMask(side, side, new(20, 20), new(80, 20), new(80, 80), new(20, 80)))
            .With("vstk", PsdWriter.StrokeSettings(false, true, 6, SKColors.White, paint: paint)));
        var import = PsdImport.Load(writer.Build());
        Assert.Empty(import.Conversions);
        var doc = import.ToDocument();
        var layer = Assert.Single(doc.Layers);
        var style = Assert.IsType<ShapeStyle>(layer.Shape);
        var gradient = Assert.IsType<VectorGradient>(style.StrokeGradient);
        Assert.True(gradient.Angular);
        Assert.True(gradient.Perceptual);
        Assert.Equal(90, gradient.Angle);
        Assert.Equal(.1, gradient.CenterOffsetX);
        Assert.Equal(-.05, gradient.CenterOffsetY);
        Assert.Equal(.5, gradient.Ramp.Opacities[1].Opacity);

        // Either side of the upward seam from (56,47), the ramp reaches its opposite ends.
        using (var pixels = DocumentRenderer.Flatten(doc))
        {
            var clockwise = pixels.GetPixel(58, 20);
            var counter = pixels.GetPixel(53, 20);
            Assert.True(clockwise.Red > 230 && clockwise.Green < 60 && clockwise.Alpha > 230);
            Assert.True(counter.Green > 230 && counter.Red < 60 && counter.Alpha is >= 125 and <= 140);
            Assert.True(pixels.GetPixel(55, 79).Blue > 225);
        }
        var session = new EditorSession(doc);
        session.ChangeShapeStyle(layer, style with { StrokeWidth = 4 });
        Assert.Equal(layer.Shape, Reopen(session.Document).Layers[0].Shape);
    }

    private static Document Reopen(Document document)
    {
        using var stream = new MemoryStream();
        ProjectFile.Write(document, stream);
        stream.Position = 0;
        return ProjectFile.Read(stream);
    }
}
