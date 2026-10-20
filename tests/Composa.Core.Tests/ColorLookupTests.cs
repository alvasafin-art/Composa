using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Composa.ColorManagement;
using Composa.Editing;
using Composa.Filters;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;

namespace Composa.Core.Tests;

public class ColorLookupTests
{
    private static CubeLut Parse(string text) => CubeLut.Read(new StringReader(text));
    private static string Cube(int size, Func<double, double, double, (double R, double G, double B)> map, string header = "")
    {
        var text = new StringBuilder(header + $"\nLUT_3D_SIZE {size}\n");
        for (var b = 0; b < size; b++) for (var g = 0; g < size; g++) for (var r = 0; r < size; r++)
        { var c = map(r / (double)(size - 1), g / (double)(size - 1), b / (double)(size - 1)); text.AppendLine(FormattableString.Invariant($"{c.R:R} {c.G:R} {c.B:R}")); }
        return text.ToString();
    }
    private static void Near((double R, double G, double B) expected, (double R, double G, double B) actual, double tolerance = 2e-7)
    { Assert.InRange(Math.Abs(expected.R - actual.R), 0, tolerance); Assert.InRange(Math.Abs(expected.G - actual.G), 0, tolerance); Assert.InRange(Math.Abs(expected.B - actual.B), 0, tolerance); }

    [Fact]
    public void Adobe_cube_red_fastest_linear_map_and_1D_per_channel_domains_are_respected()
    {
        var table = Parse(Cube(2, (r, g, b) => (r, (3 * g + b) / 4, b), "TITLE \"Blue # label\" # trailing comment"));
        Assert.Equal("Blue # label", table.Title); Near((.2, .55, .7), table.Map(.2, .5, .7)); Near((0, 1, 1), table.Map(-1, 3, 3));
        var curves = Parse("LUT_1D_SIZE 3\r\nDOMAIN_MIN 0 0 0\r\nDOMAIN_MAX 1 2 3\r\n0 0 0\r\n0.5 1 1.5\r\n1 1 1\r\n");
        Near((.25, .5, .75), curves.Map(.25, .5, .75)); Near((1, 1, 1.25), curves.Map(9, 2, 2.25));
        Assert.Equal(3, curves.Size1D); Assert.Equal(0, curves.Size3D);
    }

    [Theory]
    [InlineData(.2, .5, .8)] [InlineData(.2, .8, .5)] [InlineData(.5, .2, .8)]
    [InlineData(.8, .2, .5)] [InlineData(.5, .8, .2)] [InlineData(.8, .5, .2)]
    [InlineData(.3, .3, .3)] [InlineData(1, 1, 1)] [InlineData(0, 0, 0)]
    public void Tetrahedral_interpolation_uses_all_six_tetrahedra_and_is_not_trilinear(double r, double g, double b)
    {
        var table = Parse(Cube(2, (r, g, b) => (r * g * b, r, b)));
        Near((Math.Min(r, Math.Min(g, b)), r, b), table.Map(r, g, b));
    }

    [Fact]
    public void Resolve_shaper_is_applied_before_3D_with_independent_ranges_and_without_clipping_between_stages()
    {
        var table = Parse(Cube(2, (r, g, b) => (b, g, r), "LUT_1D_SIZE 2\nLUT_1D_INPUT_RANGE 0 2\nLUT_3D_INPUT_RANGE 0 4").Replace("LUT_3D_SIZE 2\n", "LUT_3D_SIZE 2\n0 0 0\n4 4 4\n"));
        Near((.4, .25, .1), table.Map(.2, .5, .8)); Assert.Equal(2, table.Size1D); Assert.Equal(2, table.Size3D);
        var reverse = Parse(Cube(2, (r, g, b) => (1 - r, 1 - g, 1 - b), "LUT_1D_SIZE 2").Replace("LUT_3D_SIZE 2\n", "LUT_3D_SIZE 2\n1 1 1\n0 0 0\n"));
        Near((.123, .456, .789), reverse.Map(.123, .456, .789));
    }

    [Theory]
    [InlineData("LUT_1D_SIZE 1\n0 0 0")]
    [InlineData("LUT_3D_SIZE 129\n0 0 0")]
    [InlineData("LUT_1D_SIZE 2\n0 0 0")]
    [InlineData("LUT_1D_SIZE 2\n0 0 0\n1 1 1\n2 2 2")]
    [InlineData("LUT_1D_SIZE 2\n0 0 NaN\n1 1 1")]
    [InlineData("LUT_1D_SIZE 2\n0 0 1e38\n1 1 1")]
    [InlineData("LUT_1D_SIZE 2\n0 0 0\nDOMAIN_MAX 1 1 1\n1 1 1")]
    [InlineData("LUT_1D_SIZE 2\nLUT_1D_SIZE 2\n0 0 0\n1 1 1")]
    [InlineData("LUT_1D_SIZE 2\nDOMAIN_MIN 1 0 0\nDOMAIN_MAX 1 1 1\n0 0 0\n1 1 1")]
    [InlineData("LUT_1D_SIZE 2\nLUT_3D_INPUT_RANGE 0 1\n0 0 0\n1 1 1")]
    [InlineData("LUT_1D_SIZE 2\nDOMAIN_MIN 0 0 0\nLUT_1D_INPUT_RANGE 0 1\n0 0 0\n1 1 1")]
    [InlineData("TITLE notquoted\nLUT_1D_SIZE 2\n0 0 0\n1 1 1")]
    public void Malformed_or_ambiguous_tables_are_refused(string text) => Assert.Throws<InvalidDataException>(() => Parse(text));

    [Fact]
    public void Parsing_is_culture_independent_and_binary_JSON_assets_detect_damage_and_preserve_samples()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            var table = Parse("TITLE \"Тест\"\nLUT_1D_SIZE 2\n-1e-2 .2 +0.3\n1e0 .7 2\n");
            using var stream = new MemoryStream(); table.WriteBinary(stream); var bytes = stream.ToArray(); stream.Position = 0; var restored = CubeLut.ReadBinary(stream);
            Assert.Equal(table.Fingerprint, restored.Fingerprint); Near(table.Map(.123, .567, .987), restored.Map(.123, .567, .987));
            var json = JsonSerializer.Serialize<Adjustment>(new ColorLookupAdjustment { Table = table }); var settings = Assert.IsType<ColorLookupAdjustment>(JsonSerializer.Deserialize<Adjustment>(json)); Assert.Equal(table.Fingerprint, settings.Table!.Fingerprint);
            Assert.Throws<EndOfStreamException>(() => CubeLut.ReadBinary(new MemoryStream(bytes[..^1])));
            Assert.Throws<InvalidDataException>(() => CubeLut.ReadBinary(new MemoryStream(bytes.Concat(new byte[] { 0 }).ToArray())));
            bytes[^4] = 0; bytes[^3] = 0; bytes[^2] = 128; bytes[^1] = 127; Assert.Throws<InvalidDataException>(() => CubeLut.ReadBinary(new MemoryStream(bytes)));
            Assert.Throws<InvalidDataException>(() => Parse(new string('x', DocumentLimits.MaxLutLineLength + 1)));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(ColorDepth.Eight)] [InlineData(ColorDepth.Sixteen)]
    public void Lookup_preserves_alpha_profile_and_originals_and_strength_blends_in_document_RGB(ColorDepth depth)
    {
        using var source = Pixels.NewWorkingColor(3, 1, depth, RgbProfile.DisplayP3); var input = new PixelBuffer(source); input.Write(0, 0, new(.3f, .1f, .2f, .7f)); input.Write(1, 0, new(.3f + 1f / 65535, .1f, .2f, .7f));
        var before = source.Bytes; using var result = Pixels.Clone(source); var settings = new ColorLookupAdjustment { Table = Parse(Cube(2, (r, g, b) => (b, g, r))), Space = LookupRgbSpace.WorkingRgb, Strength = 25 }; settings.Apply(result);
        var output = new PixelBuffer(result);
        for (var x = 0; x < 3; x++)
        { var p = input.Read(x, 0); var v = output.Read(x, 0); Assert.Equal(p.A, v.A); Assert.InRange(Math.Abs(v.R - (p.R * .75 + p.B * .25)), 0, depth == ColorDepth.Sixteen ? 1e-7 : .005); Assert.InRange(Math.Abs(v.B - (p.B * .75 + p.R * .25)), 0, depth == ColorDepth.Sixteen ? 1e-7 : .005); }
        if (depth == ColorDepth.Sixteen) Assert.True(output.Read(1, 0).R - output.Read(0, 0).R > .5f / 65535);
        Assert.Equal(RgbProfile.DisplayP3, Pixels.ProfileOf(result)); Assert.Equal(before, source.Bytes); Assert.Equal(default, output.Read(2, 0));
        var identity = settings with { Strength = 0 }; identity.Apply(source); Assert.Equal(before, source.Bytes);
    }

    [Fact]
    public void Explicit_sRGB_LUT_in_P3_agrees_with_independent_Skia_float_ICC_conversions()
    {
        using var original = Pixels.NewWorkingColor(11, 1, ColorDepth.Sixteen, RgbProfile.DisplayP3); var p = new PixelBuffer(original);
        for (var x = 0; x < 11; x++) p.Write(x, 0, new PixelValue(.15f + .04f * x, .3f, .5f - .03f * x, .6f).Premultiplied);
        using var srgb = Pixels.ConvertWorking(original, ColorDepth.Sixteen, RgbProfile.Srgb);
        var table = Parse(Cube(3, (r, g, b) => (.1 + .8 * b, .05 + .85 * g, .05 + .8 * r)));
        new ColorLookupAdjustment { Table = table, Space = LookupRgbSpace.WorkingRgb }.Apply(srgb);
        using var expected = Pixels.ConvertWorking(srgb, ColorDepth.Sixteen, RgbProfile.DisplayP3); using var actual = Pixels.Clone(original);
        new ColorLookupAdjustment { Table = table, Space = LookupRgbSpace.Srgb }.Apply(actual);
        var a = new PixelBuffer(actual); var e = new PixelBuffer(expected);
        double maximum = 0; var worst = "";
        for (var x = 0; x < 11; x++) for (var c = 0; c < 4; c++)
        { var error = Math.Abs(a.Read(x, 0)[c] - e.Read(x, 0)[c]); if (error > maximum) { maximum = error; worst = $"x={x}, channel={c}, actual={a.Read(x, 0)[c]:R}, Skia={e.Read(x, 0)[c]:R}"; } }
        // Different float CMM evaluation paths are not bit-identical. This is less than .026 of an RGB8 step;
        // separate tests require exact retained alpha and adjacent RGB16 levels without a profile conversion.
        Assert.True(maximum <= 1e-4, $"Max ICC difference {maximum:R}: {worst}");
    }

    [Fact]
    public void Large_LUT_is_embedded_once_outside_manifest_and_survives_source_deletion_CMPS_clone_undo_and_render()
    {
        var path = Path.Combine(Path.GetTempPath(), "composa-lut-" + Guid.NewGuid().ToString("N") + ".cube");
        CubeLut table;
        try { File.WriteAllText(path, Cube(65, (r, g, b) => (b, g, r), "TITLE \"Swap\"")); table = CubeLut.Load(path); } finally { File.Delete(path); }
        var document = new Document(3, 2) { Depth = ColorDepth.Sixteen, Profile = RgbProfile.DisplayP3 }; var source = Pixels.NewWorkingColor(3, 2, document.Depth, document.Profile); new PixelBuffer(source).Write(0, 0, new(.3f, .1f, .2f, .7f)); document.InsertAboveActive(Layer.Raster("Original", source));
        var session = new EditorSession(document); var settings = new ColorLookupAdjustment { Table = table, Space = LookupRgbSpace.WorkingRgb, Strength = 70 };
        session.AddAdjustmentLayer(settings); var one = session.ActiveLayer!.Id; session.AddAdjustmentLayer(settings with { Strength = 30 });
        Assert.Same(table, ((ColorLookupAdjustment)document.Clone().ActiveLayer!.Adjustment!).Table);
        using var view = session.Flatten(); using var stream = new MemoryStream(); ProjectFile.Write(document, stream);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true)) { Assert.Single(zip.Entries, e => e.FullName.StartsWith("lookups/")); Assert.True(zip.GetEntry("manifest.json")!.Length < 6000); }
        stream.Position = 0; var loaded = ProjectFile.Read(stream); var first = Assert.IsType<ColorLookupAdjustment>(loaded.Find(one)!.Adjustment); var last = Assert.IsType<ColorLookupAdjustment>(loaded.ActiveLayer!.Adjustment); Assert.Same(first.Table, last.Table); Assert.True(settings.ContentEquals(first));
        using var restored = new EditorSession(loaded).Flatten();
        for (var y = 0; y < 2; y++) for (var x = 0; x < 3; x++) for (var c = 0; c < 4; c++) Assert.InRange(Math.Abs(new PixelBuffer(view).Read(x, y)[c] - new PixelBuffer(restored).Read(x, y)[c]), 0, 2f / 65535);
        session.Undo(); Assert.Equal(one, session.ActiveLayer!.Id); session.Undo(); Assert.Same(source, session.ActiveLayer!.Pixels);
        // A missing asset must fail, rather than quietly render an identity or follow a local path.
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true)) zip.Entries.Single(e => e.FullName.StartsWith("lookups/")).Delete();
        stream.Position = 0; Assert.Throws<InvalidDataException>(() => ProjectFile.Read(stream));
    }
}
