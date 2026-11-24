using System.Text;
using Composa.Editing;
using Composa.IO;
using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using Composa.Text;
using SkiaSharp;
using Descriptor = Composa.Core.Tests.PsdWriter.Descriptor;

namespace Composa.Core.Tests;

public class PsdDesignCompatibilityTests
{
    [Fact]
    public void Photoshop_path_type_reopens_as_editable_text_on_its_document_contour()
    {
        var writer = new PsdWriter { Width = 200, Height = 200 };
        var type = new PsdWriter.TypeTool
        {
            TextIndex = 0, Text = "120", FontSize = 18, Tx = 70, Ty = 20,
            Bounds = new SKRect(0, 0, 5, 140), GlyphBounds = new SKRect(0, 0, 14, 70)
        };
        writer.Layers.Add(new PsdWriterLayer { Name = "Dimension" }.With("TySh", type.Build()));
        // An open cubic whose source frame is off the origin. The range starts one-quarter into that cubic.
        writer.GlobalExtra.Add(("Txt2", Encoding.ASCII.GetBytes("""
            /98 << /0 13 >>
            /0 << /8 << /0 [ << /0 <<
                /1 << /0 [ 15 10 15 10 15 150 15 150 ] >>
                /2 << /0 2 /2 [ 1 0 0 1 -15 -10 ] /6 [ 0.25 1 ] >>
            >> >> ] >> >>
            /1 << /1 [ << /0 << /0 (120) >> /1 << /2 [ << /6 [ << /99 /F /10 0 >> ] >> ] >> >> ] >>
            """)));
        var document = PsdImport.Load(writer.Build()).ToDocument();
        var layer = Assert.Single(document.Layers);
        var text = Assert.IsType<TextStyle>(layer.Text);
        Assert.Equal(.25, text.Path!.Start);
        Assert.False(text.IsBox);
        Assert.Equal("120", text.Text);
        var layout = new TextLayout(text);
        var caret = layout.CaretSegmentAt(0)!.Value;
        Assert.Equal(caret.Top.Y, caret.Bottom.Y, 3);
        Assert.Equal(0, layout.IndexAt(caret.Top));
        var start = LayoutStart(layer, layout);
        Assert.Equal(70f, start.X, 3);
        Assert.Equal(41.875f, start.Y, 2);
        var ink = Pixels.ContentBounds(layer.Pixels!);
        Assert.True(ink.Height > ink.Width);

        var editor = new EditorSession(document);
        var edit = editor.EditText(layer)!;
        edit.SelectAll();
        edit.Insert("125");
        edit.SelectAll();
        edit.SetSize(22);
        editor.FinishText();
        Assert.Equal("125", layer.Text!.Text);
        var moved = LayoutStart(layer, new TextLayout(layer.Text));
        Assert.Equal(start.X, moved.X, 3);
        Assert.Equal(start.Y, moved.Y, 3);

        using var saved = new MemoryStream();
        ProjectFile.Write(document, saved);
        saved.Position = 0;
        var reopened = ProjectFile.Read(saved);
        Assert.Equal(layer.Text, reopened.Layers[0].Text);
        using var edited = DocumentRenderer.Flatten(document);
        using var loaded = DocumentRenderer.Flatten(reopened);
        Assert.Equal(edited.GetPixelSpan().ToArray(), loaded.GetPixelSpan().ToArray());
        Assert.Contains(PsdExport.Conversions(reopened), c => c.LayerName == layer.Name && c.Message.Contains("saved as pixels"));

        static SKPoint LayoutStart(Layer layer, TextLayout layout)
        {
            using var curve = layout.CreateLocalTextPath()!;
            using var measure = new SKPathMeasure(curve);
            Assert.True(measure.GetPositionAndTangent(0, out var point, out _));
            return layer.Matrix.MapPoint(point);
        }
    }

    [Fact]
    public void A_shape_can_start_a_separate_undoable_path_text_layer()
    {
        var session = new EditorSession(new Document(300, 300));
        var shape = session.AddPath(new VectorPath
        {
            Nodes = [BezierNode.Corner(40, 140) with { OutX = 80, OutY = 70 },
                BezierNode.Corner(250, 140) with { InX = 200, InY = 70 }]
        });
        session.TextDefaults = new TextStyle { Size = 22 };
        var label = session.BeginTextOnPath(shape);
        Assert.NotNull(label);
        var draft = session.TextEdit!;
        draft.Insert("Follow the curve");
        draft.SelectAll();
        Assert.NotEmpty(draft.Layout.SelectionPolygons(draft.SelectionStart, draft.SelectionEnd));
        session.FinishText();
        Assert.Equal("Follow the curve", label!.Text!.Text);
        Assert.NotNull(label.Text.Path);
        Assert.NotNull(session.Document.Find(shape.Id));
        Assert.Null(session.TextDefaults.Path);
        session.Undo();
        Assert.Null(session.Document.Find(label.Id));
    }

    [Fact]
    public void Mixed_explicit_leading_places_baselines_without_subtracting_changes_in_font_size()
    {
        var style = new TextStyle { Text = "BIG\u2028small\u2028small", Size = 70, Leading = 55 }
            .WithSize(30, 4, 15)
            .FormatRange(4, 15, format => format with { Leading = 40 });
        var layout = new TextLayout(style);
        Assert.Equal(3, layout.Lines.Count);
        Assert.Equal(55, layout.Lines[1].Baseline - layout.Lines[0].Baseline, 2);
        Assert.Equal(40, layout.Lines[2].Baseline - layout.Lines[1].Baseline, 2);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(37)]
    public void A_linear_gradient_overlay_imports_as_an_effect_and_preserves_its_stops(int midpoint)
    {
        static byte[] Color(int location, SKColor color, int middle = 50) => Descriptor.Objc(new Descriptor()
            .Add("Lctn", Descriptor.Long(location)).Add("Mdpn", Descriptor.Long(middle)).Add("Clr ", Descriptor.Objc(PsdWriter.Rgb(color))));
        static byte[] Opacity(int location, double amount) => Descriptor.Objc(new Descriptor()
            .Add("Lctn", Descriptor.Long(location)).Add("Opct", Descriptor.UntF("#Prc", amount)));
        var stops = new Descriptor().Add("GrdF", Descriptor.Enum("GrdF", "CstS"))
            .Add("Clrs", Descriptor.List(Color(0, SKColors.Red, midpoint), Color(2048, SKColors.Green), Color(4096, SKColors.Blue)))
            .Add("Trns", Descriptor.List(Opacity(0, 25), Opacity(4096, 100)));
        var overlay = new Descriptor().Add("enab", Descriptor.Bool(true)).Add("Type", Descriptor.Enum("GrdT", "Lnr "))
            .Add("Md  ", Descriptor.Enum("BlnM", "Nrml")).Add("Angl", Descriptor.UntF("#Ang", 25))
            .Add("Scl ", Descriptor.UntF("#Prc", 120)).Add("Rvrs", Descriptor.Bool(true)).Add("Grad", Descriptor.Objc(stops));
        var effects = new PsdWriter.Buffer();
        effects.U32(0); effects.U32(16);
        effects.Bytes(new Descriptor().Add("GrFl", Descriptor.Objc(overlay)).ToArray());
        using var bitmap = Pixels.NewColor(23, 18);
        bitmap.Erase(SKColors.White);
        var writer = new PsdWriter { Width = 28, Height = 20 };
        writer.Layers.Add(new PsdWriterLayer { Image = bitmap }.With("lfx2", effects.ToArray()));
        var import = PsdImport.Load(writer.Build());
        if (midpoint == 50) Assert.Empty(import.Conversions);
        else Assert.Contains(import.Conversions, note => note.Message.Contains("midpoints are approximated"));
        var gradient = Assert.IsType<GradientOverlayEffect>(import.Layers.Single().Effects?.GradientOverlay);
        Assert.True(gradient.Enabled);
        Assert.True(gradient.Reverse);
        Assert.Equal(25, gradient.Angle);
        Assert.Equal(120, gradient.Scale);
        Assert.Equal(midpoint == 50 ? 3 : 4, gradient.Ramp!.Colors.Length);
        if (midpoint != 50) Assert.Equal(midpoint / 200.0, gradient.Ramp.Colors[1].Position, 4);
        Assert.Equal(.25, gradient.Ramp.Opacities[0].Opacity);
        using var output = new MemoryStream();
        ProjectFile.Write(import.ToDocument(), output);
        output.Position = 0;
        Assert.Equal(gradient, ProjectFile.Read(output).Layers.Single().Effects?.GradientOverlay);
    }
}
