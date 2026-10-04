using Composa.Editing;
using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using Composa.Text;
using SkiaSharp;

namespace Composa.Core.Tests;

public class PsdNativeTests
{
    private static string Family => EditorSession.FontFamilies.FirstOrDefault(f => f == "Arial") ?? EditorSession.FontFamilies[0];
    private static Layer TextLayer(TextStyle style, double x = 30, double y = 20)
    {
        var layer = Layer.Raster("Editable text", new TextLayout(style).Render(), x, y); layer.Text = style; return layer;
    }
    private static Layer ShapeLayer(ShapeStyle style, int width = 80, int height = 50)
    {
        var layer = Layer.Raster("Editable shape", EditorSession.RenderShape(style, width, height), 40, 50); layer.Shape = style; return layer;
    }
    private static byte[] Write(Document document)
    {
        using var stream = new MemoryStream(); PsdExport.Write(document, stream); return stream.ToArray();
    }
    private static void Picture(Document a, Document b, int tolerance = 2)
    {
        using var x = DocumentRenderer.Flatten(a); using var y = DocumentRenderer.Flatten(b);
        var p = x.GetPixelSpan(); var q = y.GetPixelSpan(); Assert.Equal(p.Length, q.Length);
        for (var i = 0; i < p.Length; i++) Assert.InRange(Math.Abs(p[i] - q[i]), 0, tolerance);
    }
    private static void Placement(Layer a, Layer b, float tolerance = .03f)
    {
        var x = a.Transform.Corners(a.Pixels!.Width, a.Pixels.Height); var y = b.Transform.Corners(b.Pixels!.Width, b.Pixels.Height);
        // A horizontal reflection can be expressed as a half turn plus a vertical reflection.
        foreach (var p in x) Assert.Contains(y, q => Math.Abs(p.X - q.X) < tolerance && Math.Abs(p.Y - q.Y) < tolerance);
    }

    [Theory]
    [InlineData(TextAlignment.Left, false)]
    [InlineData(TextAlignment.Center, false)]
    [InlineData(TextAlignment.Right, false)]
    [InlineData(TextAlignment.Left, true)]
    [InlineData(TextAlignment.Center, true)]
    [InlineData(TextAlignment.Right, true)]
    public void Point_and_paragraph_text_keep_wording_faces_colors_spacing_and_alignment(TextAlignment alignment, bool box)
    {
        var style = new TextStyle { Text = "Привет (PSD) \\ 😀\nSecond line", FontFamily = Family, Size = 23, Color = 0xFF1428C8,
            Alignment = alignment, Tracking = .75, Leading = 31, BoxWidth = box ? 160 : null, BoxHeight = box ? 120 : null }
            .WithColor(0xFFC83214, 1, 6).WithFace(f => f with { Bold = true, Italic = true }, 3, 10);
        var document = new Document(500, 300); document.Layers.Add(TextLayer(style));
        var pixel = document.Layers[0].Pixels;
        Assert.Empty(PsdExport.Conversions(document));
        var bytes = Write(document); Assert.Same(pixel, document.Layers[0].Pixels);
        var imported = PsdImport.Load(bytes);
        Assert.Empty(imported.Conversions);
        var layer = Assert.Single(imported.Layers); var actual = Assert.IsType<TextStyle>(layer.Text);
        Assert.Equal(style.Text, actual.Text); Assert.Equal(style.Size, actual.Size, 4); Assert.Equal(style.Tracking, actual.Tracking, 4);
        Assert.Equal(style.Leading, actual.Leading); Assert.Equal(style.Alignment, actual.Alignment); Assert.Equal(style.IsBox, actual.IsBox);
        Assert.Equal(style.BoxWidth, actual.BoxWidth); Assert.Equal(style.BoxHeight, actual.BoxHeight);
        for (var i = 0; i < style.Text.Length; i++) { Assert.Equal(style.ColorAt(i), actual.ColorAt(i)); Assert.Equal(style.FaceAt(i), actual.FaceAt(i)); }
        Placement(document.Layers[0], layer); Picture(document, imported.ToDocument());
        var session = new EditorSession(imported.ToDocument()); session.SelectLayer(layer.Id);
        session.ChangeTextStyle(t => t with { Size = 29 }); Assert.Equal(29, layer.Text!.Size); session.Undo(); Assert.Equal(23, session.Document.ActiveLayer!.Text!.Size);
        imported.Discard();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Native_type_transforms_keep_rotations_reflections_and_nonuniform_scaling(bool horizontal, bool vertical, bool stretch)
    {
        var document = new Document(500, 400);
        var layer = TextLayer(new TextStyle { Text = "Native type", FontFamily = Family, Size = 28 });
        layer.Transform = layer.Transform with { Rotation = 31.75, FlipHorizontal = horizontal, FlipVertical = vertical, Width = layer.Pixels!.Width * (stretch ? 1.45 : 1), Height = layer.Pixels.Height };
        document.Layers.Add(layer); Assert.Empty(PsdExport.Conversions(document));
        var imported = PsdImport.Load(Write(document)); Assert.Empty(imported.Conversions); Assert.NotNull(imported.Layers[0].Text);
        Placement(layer, imported.Layers[0]); if (!stretch) Picture(document, imported.ToDocument(), 3);
        imported.Discard();
    }

    [Fact]
    public void Empty_text_and_trailing_line_breaks_are_not_discarded()
    {
        foreach (var words in new[] { "", "\n", "Last\n\n" })
        {
            var document = new Document(400, 300); document.Layers.Add(TextLayer(new TextStyle { Text = words, FontFamily = Family }));
            var imported = PsdImport.Load(Write(document)); Assert.Empty(imported.Conversions); Assert.Equal(words, imported.Layers[0].Text!.Text); imported.Discard();
        }
    }

    [Fact]
    public void Sheared_text_and_unstroked_vectors_keep_their_affine_placement()
    {
        var document = new Document(400, 300);
        var text = TextLayer(new TextStyle { Text = "Shear", FontFamily = Family, Size = 23 });
        text.Transform = text.Transform with { Distort = [0, 0, 0, 0, 20, 0, 20, 0] };
        var shape = ShapeLayer(new ShapeStyle(ShapeKind.Ellipse, 0xFF010101, 0));
        shape.Transform = shape.Transform with { Width = 130, Distort = [0, 0, 0, 0, 10, 0, 10, 0] };
        document.Layers.Add(text); document.Layers.Add(shape); Assert.Empty(PsdExport.Conversions(document));
        var imported = PsdImport.Load(Write(document)); Assert.Empty(imported.Conversions);
        Placement(text, imported.Layers[0]); Placement(shape, imported.Layers[1]); Assert.Equal(0xFF010101u, imported.Layers[1].Shape!.Fill);
        Picture(document, imported.ToDocument(), 4); imported.Discard();
    }

    [Fact]
    public void Live_masks_are_separate_and_follow_rotated_geometry()
    {
        foreach (var rotated in new[] { false, true })
        {
            var document = new Document(300, 240); var layer = ShapeLayer(new ShapeStyle(ShapeKind.Rectangle, 0xFF3060A0, 0));
            layer.Mask = Pixels.NewMask(80, 50, 128);
            for (var y = 0; y < 50; y++) layer.Mask.GetPixelSpan().Slice(y * layer.Mask.RowBytes, 40).Fill(0);
            Pixels.Invalidate(layer.Mask); if (rotated) layer.Transform = layer.Transform with { Rotation = 30 };
            document.Layers.Add(layer); var imported = PsdImport.Load(Write(document)); Assert.Empty(imported.Conversions);
            var actual = imported.Layers[0]; Assert.NotNull(actual.Shape); Assert.NotNull(actual.Mask);
            Assert.Equal(0, actual.Mask!.GetPixelSpan()[25 * actual.Mask.RowBytes + 20]);
            Assert.InRange(actual.Mask.GetPixelSpan()[25 * actual.Mask.RowBytes + 60], 127, 129);
            Placement(layer, actual); if (!rotated) Picture(document, imported.ToDocument()); imported.Discard();
        }
    }

    [Fact]
    public void Rebuilding_native_layers_uses_the_budget_released_by_their_caches()
    {
        var document = new Document(300, 240); document.Layers.Add(TextLayer(new TextStyle { Text = "Budget", FontFamily = Family, Size = 23 }));
        document.Layers.Add(ShapeLayer(new ShapeStyle(ShapeKind.RoundedRectangle, 0xFF3060A0, 12)));
        var budget = document.Layers.Sum(l => (long)l.Pixels!.Width * l.Pixels.Height);
        var imported = PsdImport.Load(Write(document), budget); Assert.Empty(imported.Conversions);
        Assert.NotNull(imported.Layers[0].Text); Assert.NotNull(imported.Layers[1].Shape); imported.Discard();
    }

    [Theory]
    [InlineData(ShapeKind.Rectangle, true)]
    [InlineData(ShapeKind.RoundedRectangle, true)]
    [InlineData(ShapeKind.Ellipse, true)]
    [InlineData(ShapeKind.Rectangle, false)]
    [InlineData(ShapeKind.RoundedRectangle, false)]
    [InlineData(ShapeKind.Ellipse, false)]
    public void Native_shapes_preserve_fill_stroke_and_corners_after_reopening(ShapeKind kind, bool filled)
    {
        var style = new ShapeStyle(kind, 0xFF2846A0, kind == ShapeKind.RoundedRectangle ? 13 : 0) { FillEnabled = filled, Stroke = 0xFFC83214, StrokeWidth = 6 };
        var document = new Document(300, 200); var original = ShapeLayer(style); document.Layers.Add(original);
        Assert.Empty(PsdExport.Conversions(document));
        var imported = PsdImport.Load(Write(document)); Assert.Empty(imported.Conversions);
        var layer = Assert.Single(imported.Layers); Assert.Equal(style, layer.Shape); Placement(original, layer); Picture(document, imported.ToDocument());
        var session = new EditorSession(imported.ToDocument()); session.SelectLayer(layer.Id);
        session.ChangeShapeStyle(layer, layer.Shape! with { StrokeWidth = 10, Fill = 0xFF00FF00 });
        Assert.Equal(10, layer.Shape!.StrokeWidth); Assert.Equal(0xFF00FF00u, layer.Shape.Fill);
        session.Undo(); Assert.Equal(style, session.Document.ActiveLayer!.Shape); imported.Discard();
    }

    [Fact]
    public void Vector_geometry_stays_native_when_turned_reflected_or_scaled()
    {
        var document = new Document(300, 240); var original = ShapeLayer(new ShapeStyle(ShapeKind.RoundedRectangle, 0xFF3060A0, 12) { Stroke = 0xFF000000, StrokeWidth = 4 });
        original.Transform = original.Transform with { Rotation = -27.5, Width = 120, Height = 75, FlipHorizontal = true };
        document.Layers.Add(original); Assert.Empty(PsdExport.Conversions(document));
        var imported = PsdImport.Load(Write(document)); Assert.Empty(imported.Conversions); Assert.Equal(original.Shape, imported.Layers[0].Shape);
        Placement(original, imported.Layers[0]); Picture(document, imported.ToDocument(), 3); imported.Discard();
    }

    [Fact]
    public void Lines_keep_their_endpoints_round_caps_color_and_width()
    {
        var document = new Document(300, 240); var original = ShapeLayer(new ShapeStyle(ShapeKind.Line, 0xFF3060A0, 0)
            { LineWidth = 8, StartX = .1, StartY = .8, EndX = .9, EndY = .2 });
        original.Transform = original.Transform with { Rotation = 21 }; document.Layers.Add(original);
        Assert.Empty(PsdExport.Conversions(document)); var imported = PsdImport.Load(Write(document)); Assert.Empty(imported.Conversions);
        var layer = Assert.Single(imported.Layers); var line = Assert.IsType<ShapeStyle>(layer.Shape);
        Assert.Equal(ShapeKind.Line, line.Kind); Assert.Equal(8, line.LineWidth, 3); Assert.Equal(original.Shape!.Fill, line.Fill);
        var a = original.Matrix.MapPoint((float)(original.Shape.StartX!.Value * 80), (float)(original.Shape.StartY!.Value * 50));
        var b = layer.Matrix.MapPoint((float)(line.StartX!.Value * layer.Pixels!.Width), (float)(line.StartY!.Value * layer.Pixels.Height));
        Assert.InRange(Math.Abs(a.X - b.X), 0, .01); Assert.InRange(Math.Abs(a.Y - b.Y), 0, .01); imported.Discard();
    }

    [Fact]
    public void Unsupported_live_features_are_reported_and_written_as_compatible_pixels()
    {
        var document = new Document(300, 240);
        var effect = TextLayer(new TextStyle { Text = "Effects", FontFamily = Family, Size = 24 }); effect.Effects = new LayerEffects { Shadow = new ShadowEffect() }; document.Layers.Add(effect);
        var perspective = ShapeLayer(new ShapeStyle(ShapeKind.Ellipse, 0xFF3060A0, 0)); perspective.Transform = perspective.Transform with { Distort = [0, 0, 0, 0, -10, -5, 0, 0] }; document.Layers.Add(perspective);
        var translucent = ShapeLayer(new ShapeStyle(ShapeKind.Rectangle, 0x803060A0, 0)); document.Layers.Add(translucent);
        var stretchedStroke = ShapeLayer(new ShapeStyle(ShapeKind.Rectangle, 0xFF3060A0, 0) { Stroke = 0xFFFF0000 }); stretchedStroke.Transform = stretchedStroke.Transform with { Width = 100 }; document.Layers.Add(stretchedStroke);
        var notes = PsdExport.Conversions(document); Assert.Contains(notes, n => n.Message.Contains("Perspective")); Assert.Contains(notes, n => n.Message.Contains("translucent")); Assert.Contains(notes, n => n.Message.Contains("stretched"));
        var imported = PsdImport.Load(Write(document)); Assert.All(imported.Layers, l => { Assert.Null(l.Text); Assert.Null(l.Shape); }); Picture(document, imported.ToDocument()); imported.Discard();
    }

    [Fact]
    public void A_vector_far_outside_a_narrow_canvas_uses_reported_fallback_instead_of_overflowing()
    {
        var document = new Document(1, 80); var shape = ShapeLayer(new ShapeStyle(ShapeKind.Rectangle, 0xFF3060A0, 0));
        shape.Transform = shape.Transform with { X = 200 }; document.Layers.Add(shape);
        Assert.Contains(PsdExport.Conversions(document), n => n.Message.Contains("fixed-point"));
        var imported = PsdImport.Load(Write(document)); Assert.Null(imported.Layers[0].Shape); imported.Discard();
    }

    [Fact]
    public void Physical_stroke_units_and_opacity_are_converted_into_layer_pixels()
    {
        var writer = new PsdWriter { Width = 100, Height = 80, Resolution = 144 };
        writer.Layers.Add(new PsdWriterLayer { Name = "Stroke" }
            .With("SoCo", PsdWriter.SolidColor(SKColors.Red)).With("vogk", PsdWriter.Origination(1, new SKRect(10, 10, 60, 40)))
            .With("vstk", PsdWriter.StrokeSettings(false, true, 3, SKColors.Blue, "#Pnt", 144, 50)));
        var imported = PsdImport.Load(writer.Build()); Assert.Empty(imported.Conversions);
        Assert.Equal(6, imported.Layers[0].Shape!.StrokeWidth); Assert.Equal(0x800000FFu, imported.Layers[0].Shape!.Stroke); imported.Discard();
    }

    [Fact]
    public void A_rounded_rectangle_with_zero_radius_keeps_its_corner_controls()
    {
        var document = new Document(300, 240); document.Layers.Add(ShapeLayer(new ShapeStyle(ShapeKind.RoundedRectangle, 0xFF3060A0, 0)));
        var imported = PsdImport.Load(Write(document)); Assert.Empty(imported.Conversions);
        Assert.Equal(ShapeKind.RoundedRectangle, imported.Layers[0].Shape!.Kind); Assert.Equal(0, imported.Layers[0].Shape!.CornerRadius); imported.Discard();
    }

    [Fact]
    public void Native_content_and_compatibility_cache_can_be_inspected_by_independent_readers()
    {
        var document = new Document(640, 480) { Resolution = 144 };
        var text = new TextStyle { Text = "Привет PSD (editable) \\ 😀\nNative type", FontFamily = Family, Size = 26, Color = 0xFF3060A0,
            BoxWidth = 300, BoxHeight = 150, Alignment = TextAlignment.Center, Tracking = .5, Leading = 35 }
            .WithColor(0xFFC83214, 1, 6).WithFace(f => f with { Bold = true }, 8, 11);
        var type = TextLayer(text); type.Transform = type.Transform with { Rotation = 15 }; document.Layers.Add(type);
        document.Layers.Add(ShapeLayer(new ShapeStyle(ShapeKind.RoundedRectangle, 0xFF2846A0, 13) { Stroke = 0xFFC83214, StrokeWidth = 6 }));
        document.Layers.Add(ShapeLayer(new ShapeStyle(ShapeKind.Ellipse, 0xFF406080, 0) { FillEnabled = false, Stroke = 0xFF203040, StrokeWidth = 4 }));
        document.Layers.Add(ShapeLayer(new ShapeStyle(ShapeKind.Line, 0xFF408020, 0) { LineWidth = 8, StartX = .1, StartY = .8, EndX = .9, EndY = .2 }));
        var bytes = Write(document); var imported = PsdImport.Load(bytes); Assert.Empty(imported.Conversions); Assert.NotNull(imported.Layers[0].Text); Assert.All(imported.Layers.Skip(1), l => Assert.NotNull(l.Shape)); imported.Discard();
        if (Environment.GetEnvironmentVariable("COMPOSA_PSD_FIXTURE_DIRECTORY") is { } folder)
        {
            Directory.CreateDirectory(folder); File.WriteAllBytes(Path.Combine(folder, "native-content.psd"), bytes);
            using var expected = DocumentRenderer.Flatten(document); using var image = SKImage.FromBitmap(expected); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(folder, "native-content.png"), png.ToArray());
        }
    }
}
