using Composa.Editing;
using Composa.IO;
using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using Composa.Text;
using SkiaSharp;

namespace Composa.Core.Tests;

public class FontStyleAndContentTests
{
    [Fact]
    public void Installed_nonstandard_faces_survive_character_edits_project_and_native_psd()
    {
        var choice = EditorSession.FontFamilies.SelectMany(FontCatalog.ForFamily).FirstOrDefault(c => c.Face.FontStyle != null);
        if (choice == null) return; // Minimal CI images may install only the four conventional faces.
        var selected = choice!.Face;
        var session = EditorSession.NewCanvas(500, 240);
        var layer = session.AddText(new SKPoint(30, 30), new TextStyle { Text = "Styles", FontFamily = selected.FontFamily, Size = 25 });
        session.SetTextFace(_ => selected);
        Assert.Equal(selected, layer.Text!.Face);
        Assert.Equal(selected.FontStyle!.Weight, TextLayout.TypefaceFor(layer.Text).FontWeight);
        Assert.Equal(selected.FontStyle.Width, TextLayout.TypefaceFor(layer.Text).FontWidth);
        Assert.Equal(selected.FontStyle.Slant, TextLayout.TypefaceFor(layer.Text).FontSlant);
        var other = FontCatalog.ForFamily(selected.FontFamily).First(c => c.Face != selected).Face;
        session.EditText(layer);
        var editor = session.TextEdit!;
        editor.MoveTo(2, select: false); editor.MoveTo(5, select: true); editor.SetFace(_ => other);
        Assert.Equal(other, layer.Text!.FaceAt(2)); Assert.Equal(selected, layer.Text.FaceAt(1));
        editor.MoveTo(4, select: false); editor.Insert("x");
        Assert.Equal(other, layer.Text.FaceAt(4));
        session.FinishText();
        var expected = layer.Text!;
        using var project = new MemoryStream(); ProjectFile.Write(session.Document, project); project.Position = 0;
        Assert.Equal(expected, ProjectFile.Read(project).Find(layer.Id)!.Text);
        using var psd = new MemoryStream(); PsdExport.Write(session.Document, psd);
        var imported = PsdImport.Load(psd.ToArray());
        Assert.Empty(imported.Conversions);
        var actual = imported.Layers.Last(l => l.Text != null).Text!;
        for (var i = 0; i < expected.Text.Length; i++) Assert.Equal(expected.FaceAt(i), actual.FaceAt(i));
        imported.Discard();
        session.Undo(); Assert.Equal(selected, session.Document.Find(layer.Id)!.Text!.FaceAt(2));
        session.Undo(); Assert.Null(session.Document.Find(layer.Id)!.Text!.FontStyle);
        session.Redo(); Assert.Equal(selected, session.Document.Find(layer.Id)!.Text!.Face);
    }

    [Fact]
    public void Legacy_bold_commands_and_invalid_font_styles_remain_safe()
    {
        var style = new TextStyle { Text = "Test", FontStyle = new(300, 5, SKFontStyleSlant.Upright) };
        Assert.NotEqual(style, style with { FontStyle = new(500, 5, SKFontStyleSlant.Upright) });
        Assert.Null(style.WithFace(f => f with { Bold = true }, 0, 0).FontStyle);
        Assert.True(style.WithFace(f => f with { Bold = true }, 0, 0).Bold);
        Assert.Null((style with { FontStyle = new(99999, 5, SKFontStyleSlant.Upright) }).Clamped().FontStyle);
        var replaced = style.WithFace(f => f with { FontStyle = new(600, 3, SKFontStyleSlant.Oblique) }, 1, 3);
        Assert.NotNull(replaced.FontRuns);
        Assert.Null(replaced.UniformFaceIn(0, 4));
        Assert.Equal(new TextFontStyle(600, 3, SKFontStyleSlant.Oblique), replaced.FaceAt(2).FontStyle);
    }

    private static (EditorSession Session, Layer Layer) Square(bool gradient)
    {
        var session = EditorSession.NewCanvas(200, 150);
        var layer = session.ActiveLayer!;
        session.SelectRect(new SKRect(40, 30, 80, 70));
        if (gradient)
        {
            session.Foreground = SKColors.Red; session.Background = SKColors.Blue;
            var original = session.BeginGradient(layer);
            session.DrawGradient(layer, original, new SKPoint(40, 30), new SKPoint(80, 70)); session.Commit();
        }
        else session.Fill(SKColors.Red);
        session.Deselect();
        return (session, layer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Filled_selection_has_content_controls_and_resizes_without_cropping_source_or_mask(bool gradient)
    {
        var (session, layer) = Square(gradient);
        var pixels = layer.Pixels!;
        session.AddMask(layer); session.EditingMask = false;
        var mask = layer.Mask;
        Assert.Equal(new SKRectI(40, 30, 80, 70), Pixels.ContentBounds(pixels));
        Assert.Equal(new SKRect(40, 30, 80, 70), layer.ControlBounds);
        var edit = session.BeginTransform()!;
        Assert.Equal(new SKRect(40, 30, 80, 70), edit.StartFrame);
        edit.Resize(TransformHandle.BottomRight, new SKPoint(120, 110), free: true, fromCenter: false);
        session.CommitTransform();
        Assert.Equal(new SKRect(40, 30, 120, 110), layer.ControlBounds);
        Assert.Same(pixels, layer.Pixels); Assert.Same(mask, layer.Mask);
        session.Undo(); Assert.Equal(new SKRect(40, 30, 80, 70), session.ActiveLayer!.ControlBounds);
        session.Redo(); Assert.Equal(new SKRect(40, 30, 120, 110), session.ActiveLayer!.ControlBounds);
        Assert.True(session.Composite().GetPixel(100, 90).Alpha > 0);
        Assert.Equal(0, session.Composite().GetPixel(20, 20).Alpha);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(35, false, false)]
    [InlineData(-50, true, false)]
    [InlineData(90, false, true)]
    public void Content_frame_rotates_scales_flips_and_cancels_about_its_own_center(int angle, bool flipX, bool flipY)
    {
        var (session, layer) = Square(false);
        session.SetTransform(layer, layer.Transform with { Rotation = angle, FlipHorizontal = flipX, FlipVertical = flipY });
        var before = layer.Transform; var center = layer.ControlTransform.Center;
        var edit = session.BeginTransform()!;
        var frame = edit.StartFrame;
        edit.Set(SKRect.Create(frame.MidX - frame.Width, frame.MidY - frame.Height, frame.Width * 2, frame.Height * 2), angle + 30);
        Assert.InRange(SKPoint.Distance(center, layer.ControlTransform.Center), 0, .001f);
        Assert.Equal(80, layer.ControlTransform.Width, 3); Assert.Equal(80, layer.ControlTransform.Height, 3);
        session.CancelTransform(); Assert.Equal(before, session.ActiveLayer!.Transform);
        Assert.Equal(center, session.ActiveLayer.ControlTransform.Center);
        var steps = session.History.Count;
        session.BeginTransform(); session.CommitTransform(); Assert.Equal(steps, session.History.Count);
    }

    [Fact]
    public void Content_corner_distortion_keeps_the_dragged_handle_on_the_pointer()
    {
        var (session, layer) = Square(false);
        var pixels = layer.Pixels;
        var edit = session.BeginTransform()!;
        edit.DistortCorner(0, new SKPoint(45, 35));
        var corners = edit.Corners();
        Assert.InRange(SKPoint.Distance(new(45, 35), corners[0]), 0, .001f);
        Assert.InRange(SKPoint.Distance(new(80, 70), corners[2]), 0, .001f);
        session.CommitTransform(); Assert.Same(pixels, layer.Pixels);
        Assert.InRange(SKPoint.Distance(new(45, 35), layer.ControlTransform.Corners(200, 150)[0]), 0, .001f);
    }

    [Fact]
    public void Pixel_edits_invalidate_cached_content_bounds_and_inspector_uses_content_sizes()
    {
        var (session, layer) = Square(false);
        Assert.Equal(40, layer.ControlTransform.Width);
        session.SelectRect(new SKRect(100, 90, 130, 110)); session.Fill(SKColors.Blue); session.Deselect();
        Assert.Equal(new SKRect(40, 30, 130, 110), layer.ControlBounds);
        var source = layer.Pixels;
        session.SetControlTransform(layer, layer.ControlTransform with { X = 20, Y = 10, Width = 180, Height = 160 });
        Assert.Equal(new SKRect(20, 10, 200, 170), layer.ControlBounds); Assert.Same(source, layer.Pixels);
        session.Undo(); Assert.Equal(new SKRect(40, 30, 130, 110), session.ActiveLayer!.ControlBounds);
        session.Undo(); // Deselect has its own history step.
        session.Undo(); Assert.Equal(new SKRect(40, 30, 80, 70), session.ActiveLayer!.ControlBounds);
    }
}
