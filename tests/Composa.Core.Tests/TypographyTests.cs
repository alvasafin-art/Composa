using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using Composa.Text;
using SkiaSharp;

namespace Composa.Core.Tests;

public class TypographyTests
{
    [Fact]
    public void Formatting_follows_letters_through_typing_and_soft_breaks()
    {
        var s = new TextStyle { Text = "a\u2028b\nc" };
        s = s.FormatRange(2, 3, st => st with { Type = st.Type with { Strike = true } })
             .FormatRange(2, 3, st => st with { Type = st.Type with { ParagraphSpacing = 14 } }, paragraphs: true);
        Assert.Equal(14, s.FormatAt(0).Type.ParagraphSpacing);
        Assert.Equal(0, s.FormatAt(4).Type.ParagraphSpacing);
        var editor = new TextEditor(s);
        editor.MoveTo(3, false); editor.Insert("!");
        Assert.True(editor.Style.FormatAt(3).Type.Strike);
        Assert.False(editor.Style.FormatAt(4).Type.Strike);
        Assert.Equal(14, editor.Style.FormatAt(4).Type.ParagraphSpacing);
    }

    [Fact]
    public void Paragraph_settings_reflow_preserve_source_and_roundtrip()
    {
        var session = EditorSession.NewCanvas(420, 280, SKColors.White);
        var style = new TextStyle
        {
            Text = "Первая строка\nещё текст",
            Size = 22,
            BoxWidth = 220,
            BoxHeight = 180,
            Type = new Typography
            {
                Case = TextLetterCase.Upper,
                List = TextList.Numbers,
                Vertical = TextVertical.Bottom,
                Underline = true,
                ParagraphSpacing = 11,
                Indent = 13,
                Figures = FigureStyle.Tabular
            }
        };
        session.AddText(new(15, 20), style);
        var layout = new TextLayout(style);
        Assert.Equal("1.", layout.Lines[0].Marker);
        Assert.Equal("2.", layout.Lines[^1].Marker);
        Assert.True(layout.Lines[0].Top > TextLayout.Padding);
        Assert.Contains("ПЕРВАЯ", layout.Text);
        using var stream = new MemoryStream(); ProjectFile.Write(session.Document, stream); stream.Position = 0;
        var restored = ProjectFile.Read(stream).AllLayers().Single(l => l.Text != null).Text!;
        Assert.Equal(style, restored);
    }

    [Fact]
    public void A_soft_break_continues_the_paragraph_and_list_marker()
    {
        var style = new TextStyle { Text = "One\u2028two\nThree", Size = 20, Leading = 24,
            Type = new Typography { ParagraphSpacing = 16, List = TextList.Numbers, Indent = 7 } };
        var lines = new TextLayout(style).Lines;
        Assert.Equal(3, lines.Count);
        Assert.Equal(24, lines[1].Baseline - lines[0].Baseline);
        Assert.Equal(40, lines[2].Baseline - lines[1].Baseline);
        Assert.Equal(new[] { "1.", "", "2." }, lines.Select(l => l.Marker));
    }

    [Fact]
    public void Relative_line_height_follows_font_size_and_matches_pixel_leading()
    {
        var relative = new TextStyle { Text = "one\ntwo", Size = 20, Leading = 150, Type = new() { PercentLeading = true } };
        var absolute = relative with { Leading = 30, Type = Typography.Default };
        Assert.Equal(new TextLayout(absolute).Lines[1].Baseline, new TextLayout(relative).Lines[1].Baseline);
        var scaled = relative.Scaled(2, 2);
        Assert.Equal(150, scaled.Leading);
        Assert.Equal(60, scaled.LineHeight);
    }

    [Fact]
    public void Preview_pixels_are_view_only_and_clearing_restores_original_view()
    {
        var s = EditorSession.NewCanvas(150, 95, SKColors.White);
        var layer = s.AddText(new(10, 10), new TextStyle { Text = "123", Size = 30 });
        var initial = layer.Pixels; int revision = s.Revision;
        using var before = s.Flatten();
        var sample = layer.Text! with { Color = 0xffff0000 };
        s.ShowFontSample(layer, sample, EditorSession.RenderText(sample));
        using var view = Pixels.NewColor(150, 95); var geometry = new RenderView(1, new());
        s.RenderView(view, view.Info.Rect, geometry);
        Assert.False(before.Bytes.SequenceEqual(view.Bytes));
        using var export = s.Flatten(); Assert.Equal(before.Bytes, export.Bytes);
        s.ClearFontSample(); s.RenderView(view, view.Info.Rect, geometry);
        Assert.Equal(before.Bytes, view.Bytes); Assert.Same(initial, layer.Pixels); Assert.Equal(revision, s.Revision);
    }

    [Fact]
    public void Installed_variable_font_renders_different_axis_positions()
    {
        var face = TextLayout.TypefaceFor(new TextFace("Bahnschrift", false, false));
        var axis = FontVariations.Axes(face).FirstOrDefault(a => a.Tag == "wght");
        if (axis == null) return;
        var style = new TextStyle { FontFamily = face.FamilyName, Text = "Sample 123", Size = 48, BoxWidth = 400, BoxHeight = 100 };
        using var light = EditorSession.RenderText(style with { Type = new() { Axes = FontVariations.WithValue("", axis, axis.Min) } });
        using var heavy = EditorSession.RenderText(style with { Type = new() { Axes = FontVariations.WithValue("", axis, axis.Max) } });
        Assert.False(light.Bytes.SequenceEqual(heavy.Bytes));
    }
}
