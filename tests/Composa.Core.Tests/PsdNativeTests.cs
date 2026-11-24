using Composa.Editing;
using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using Composa.Text;
using SkiaSharp;

namespace Composa.Core.Tests;

public class PsdNativeTests
{
    [Theory]
    [InlineData(ShapeKind.Star)]
    [InlineData(ShapeKind.Polygon)]
    [InlineData(ShapeKind.RoundedRectangle)]
    public void Custom_shape_outlines_survive_PSD_export(ShapeKind kind)
    {
        var style = new ShapeStyle(kind, 0xFF37628F, 5) { Points = 7, InnerRatio = .3 };
        if (kind == ShapeKind.RoundedRectangle) style = style.WithCorner(1, 17).WithCorner(3, 0);
        var document = new Document(220, 210);
        document.Layers.Add(ShapeLayer(style));
        Assert.Empty(PsdExport.Conversions(document));
        var bytes = Write(document);
        Assert.Contains("vmsk", System.Text.Encoding.Latin1.GetString(bytes));
        var imported = PsdImport.Load(bytes);
        try { Picture(document, imported.ToDocument()); }
        finally { imported.Discard(); }
    }

    [Fact]
    public void Selected_typography_is_preserved_in_project_and_native_PSD_runs()
    {
        var style = new TextStyle { Text = "ab cd\nef", FontFamily = Family, Size = 20, BoxWidth = 250, BoxHeight = 110 };
        var styled = style.FormatRange(3, 5, s => s with { Tracking = 2, Type = s.Type with { Case = TextLetterCase.Upper, Underline = true } })
            .FormatRange(6, 7, s => s with { Alignment = TextAlignment.Right }, paragraphs: true);
        var layout = new TextLayout(styled);
        Assert.Equal("ab CD\nef", layout.Text);
        Assert.True(layout.Lines[1].X > layout.Lines[0].X);
        var doc = new Document(320, 160); doc.Layers.Add(TextLayer(styled));
        using var project = new MemoryStream(); Composa.IO.ProjectFile.Write(doc, project); project.Position = 0;
        Assert.Equal(styled, Composa.IO.ProjectFile.Read(project).Layers[0].Text);
        Assert.Empty(PsdExport.Conversions(doc));
        var result = PsdImport.Load(Write(doc));
        try
        {
            var imported = Assert.IsType<TextStyle>(result.Layers[0].Text);
            foreach (int i in Enumerable.Range(0, styled.Text.Length)) Assert.Equal(styled.FormatAt(i), imported.FormatAt(i));
            Picture(doc, result.ToDocument());
        }
        finally { result.Discard(); }
    }

    [Theory]
    [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void Regular_PostScript_font_with_faux_styles_stays_editable_and_keeps_its_faces_after_reexport(bool bold, bool italic)
    {
        var regular = FontCatalog.Closest(Family, new TextFace(Family, false, false)).Face;
        var psName = PsdFonts.PostScriptName(TextLayout.TypefaceFor(regular));
        var writer = new PsdWriter { Width = 300, Height = 180 };
        using var cache = Pixels.NewColor(40, 30); cache.Erase(SKColors.Black);
        writer.Layers.Add(new PsdWriterLayer { Name = "Faux type", Image = cache }.With("TySh", new PsdWriter.TypeTool { Text = "ABC xyz", Font = psName, FauxBold = bold, FauxItalic = italic }.Build()));
        var first = PsdImport.Load(writer.Build());
        try
        {
            var style = Assert.IsType<TextStyle>(first.Layers[0].Text); Assert.Equal(regular.Bold || bold, style.Bold); Assert.Equal(regular.Italic || italic, style.Italic);
            var document = first.ToDocument(); Assert.Empty(PsdExport.Conversions(document)); var second = PsdImport.Load(Write(document));
            try { Assert.Equal(style.Face, Assert.IsType<TextStyle>(second.Layers[0].Text).Face); Assert.Equal(style.Text, second.Layers[0].Text!.Text); Picture(document, second.ToDocument(), 3); }
            finally { second.Discard(); }
        }
        finally { first.Discard(); }
    }

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

    [Fact]
    public void Typography_is_native_when_possible_and_unsupported_layout_keeps_an_editable_source()
    {
        var style = new TextStyle { Text = "One\u2028two\nThree", FontFamily = Family, Size = 25, Leading = 120, Tracking = 2.04,
            Type = new Typography { ParagraphSpacing = 12, Indent = 6, Underline = true, Strike = true,
                PercentLeading = true, PercentTracking = true, Case = TextLetterCase.Upper } };
        var document = new Document(400, 220); document.Layers.Add(TextLayer(style));
        var parsed = PsdImport.Load(Write(document));
        var actual = Assert.Single(parsed.Layers).Text!;
        Assert.Equal(style.Text, actual.Text); Assert.Equal(30, actual.LineHeight);
        Assert.Equal(.5, actual.Tracking); Assert.True(actual.Type.Underline); Assert.True(actual.Type.Strike);
        Assert.Equal(6, actual.Type.Indent); Assert.Equal(12, actual.Type.ParagraphSpacing); Assert.Equal(TextLetterCase.Upper, actual.Type.Case);
        parsed.Discard();
        var custom = style with { Type = style.Type with { List = TextList.Numbers } };
        document.Layers.Clear(); document.Layers.Add(TextLayer(custom));
        var withSource = PsdImport.Load(Write(document));
        var group = Assert.Single(withSource.Layers.Where(layer => layer.IsGroup));
        Assert.False(group.Visible);
        Assert.Equal(style.Text, Assert.Single(group.Children).Text!.Text);
        Picture(document, withSource.ToDocument());
        withSource.Discard();
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
            Alignment = alignment, Tracking = .736, Leading = 31, BoxWidth = box ? 160 : null, BoxHeight = box ? 120 : null }
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
    public void Photoshop_document_space_origination_is_not_transformed_twice()
    {
        var writer = new PsdWriter { Width = 200, Height = 160 };
        var box = new SKRect(50, 60, 120, 110);
        writer.Layers.Add(new PsdWriterLayer { Name = "Rectangle" }
            .With("SoCo", PsdWriter.SolidColor(SKColors.Blue))
            .With("vmsk", PsdWriter.VectorMask(200, 160, new(50, 60), new(120, 60), new(120, 110), new(50, 110)))
            .With("vogk", PsdWriter.Origination(1, box, transform: SKMatrix.CreateScale(1.7f, 2.3f))));
        var imported = PsdImport.Load(writer.Build());
        var layer = Assert.Single(imported.Layers);
        Assert.Empty(imported.Conversions);
        Assert.Equal(ShapeKind.Rectangle, layer.Shape!.Kind);
        Assert.Equal(box, layer.Bounds);
        imported.Discard();
    }

    [Fact]
    public void Compound_contours_keep_their_holes_and_nodes_after_project_reopen()
    {
        const int canvas = 120;
        byte[] Contour(float left, float top, float right, float bottom) =>
            PsdWriter.VectorMask(canvas, canvas, new(left, top), new(right, top), new(right, bottom), new(left, bottom));
        var outer = Contour(10, 10, 85, 85);
        var hole = Contour(30, 30, 70, 70);
        var union = Contour(65, 50, 105, 90);
        const int header = 8 + 2 * 26;
        hole[header + 4] = hole[header + 5] = 255;
        union[header + 5] = 1;
        var compound = outer.Concat(hole[header..]).Concat(union[header..]).ToArray();
        var writer = new PsdWriter { Width = canvas, Height = canvas };
        writer.Layers.Add(new PsdWriterLayer().With("SoCo", PsdWriter.SolidColor(SKColors.Blue)).With("vmsk", compound));
        var imported = PsdImport.Load(writer.Build());
        Assert.Empty(imported.Conversions);
        var doc = imported.ToDocument();
        var layer = Assert.Single(doc.Layers);
        var path = layer.Shape!.Path!;
        Assert.Equal(12, path.Nodes.Length);
        Assert.Equal(0, path.SegmentEnd(3));
        Assert.Equal(4, path.SegmentEnd(7));
        Assert.Equal(8, path.SegmentEnd(11));
        using (var image = DocumentRenderer.Flatten(doc))
        {
            Assert.Equal(SKColors.Blue, image.GetPixel(20, 20));
            Assert.Equal(0, image.GetPixel(40, 40).Alpha);
            Assert.Equal(SKColors.Blue, image.GetPixel(67, 58));
            Assert.Equal(SKColors.Blue, image.GetPixel(95, 70));
        }
        var session = new EditorSession(doc);
        session.ChangeShapeStyle(layer, layer.Shape with { Fill = (uint)SKColors.Green });
        using var project = new MemoryStream();
        Composa.IO.ProjectFile.Write(session.Document, project); project.Position = 0;
        var reopened = Composa.IO.ProjectFile.Read(project);
        Assert.Equal(path, Assert.Single(reopened.Layers).Shape!.Path);
        using var edited = DocumentRenderer.Flatten(reopened);
        Assert.Equal(SKColors.Green, edited.GetPixel(20, 20));
        Assert.Equal(0, edited.GetPixel(40, 40).Alpha);
    }

    [Fact]
    public void Photoshop_glyph_bounds_confirmed_by_the_preview_recover_type_placement()
    {
        var writer = new PsdWriter { Width = 200, Height = 140 };
        using var preview = Pixels.NewColor(48, 23);
        preview.Erase(SKColors.Black);
        var type = new PsdWriter.TypeTool { Text = "Pixels", Font = PsdFonts.PostScriptName(TextLayout.TypefaceFor(new TextFace(Family, false, false))),
            FontSize = 22, Tx = 49, Ty = 70, Bounds = new SKRect(0, 0, 105, 60), GlyphBounds = new SKRect(4, 5, 52, 28) };
        writer.Layers.Add(new PsdWriterLayer { Name = "Type", Image = preview, Left = 53, Top = 75 }.With("TySh", type.Build()));
        var imported = PsdImport.Load(writer.Build());
        var doc = imported.ToDocument();
        var layer = Assert.Single(doc.Layers);
        Assert.Equal("Pixels", layer.Text!.Text);
        Assert.Equal(22, layer.Text.Size);
        var ink = layer.Matrix.MapRect(Pixels.ContentBounds(layer.Pixels!));
        Assert.InRange(ink.Left, 52.99, 53.01);
        Assert.InRange(ink.Top, 74.99, 75.01);
        Assert.InRange(ink.Right, 100.99, 101.01);
        Assert.InRange(ink.Bottom, 97.99, 98.01);
        var session = new EditorSession(doc);
        session.SelectLayer(layer.Id);
        session.ChangeTextStyle(style => style with { Text = "Edited" });
        using var project = new MemoryStream();
        Composa.IO.ProjectFile.Write(session.Document, project); project.Position = 0;
        var reopened = Composa.IO.ProjectFile.Read(project);
        Assert.Equal("Edited", Assert.Single(reopened.Layers).Text!.Text);
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
