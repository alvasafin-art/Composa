using Composa.Editing;
using Composa.IO;
using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using Composa.Text;
using SkiaSharp;

namespace Composa.Core.Tests;

public class TextSizeAndAlignmentTests
{
    [Fact]
    public void Selected_characters_keep_size_when_typing_deleting_scaling_and_undoing()
    {
        var e = new TextEditor(new TextStyle { Text = "Small BIG small", Size = 20 });
        e.MoveTo(6, false); e.MoveTo(9, true); e.SetSize(60);
        Assert.Equal([new TextSizeRun(6, 3, 60)], e.Style.SizeRuns);
        Assert.Equal(20, e.Style.SizeAt(0)); Assert.Equal(60, e.SizeAtCaret);
        e.MoveTo(9, false); e.Insert("!");
        Assert.Equal([new TextSizeRun(6, 4, 60)], e.Style.SizeRuns);
        e.Undo(); Assert.Equal("Small BIG small", e.Text);
        e.MoveTo(0, false); e.Insert("😀 ");
        Assert.Equal([new TextSizeRun(9, 3, 60)], e.Style.SizeRuns);
        Assert.Equal(120, e.Style.Scaled(2).SizeAt(9));
        e.MoveTo(9, false); e.Delete(); Assert.Equal([new TextSizeRun(9, 2, 60)], e.Style.SizeRuns);
        e.SelectAll(); e.SetSize(32); Assert.Null(e.Style.SizeRuns);
        e.Undo(); Assert.NotNull(e.Style.SizeRuns);
    }

    [Fact]
    public void Mixed_sizes_share_baseline_and_use_the_largest_size_for_each_line()
    {
        var style = new TextStyle { Text = "aBB\nc", Size = 20 }.WithSize(70, 1, 3);
        var layout = new TextLayout(style);
        Assert.InRange(layout.Lines[0].Height, 83.99, 84.01); Assert.InRange(layout.Lines[1].Height, 23.99, 24.01);
        var large = layout.CaretAt(2); var small = layout.CaretAt(1);
        Assert.True(large.Bottom - large.Top > 2 * (small.Bottom - small.Top));
        Assert.Equal(4, layout.IndexAt(new SKPoint(0, layout.Lines[1].Top + 2)));
        Assert.True(layout.Height >= layout.Lines[^1].Baseline + layout.Lines[^1].Descent);
        using var rendered = layout.Render(); Assert.False(Pixels.ContentBounds(rendered).IsEmpty);
        var explicitLeading = new TextLayout(style with { Leading = 36 });
        Assert.InRange(explicitLeading.Lines[1].Baseline - explicitLeading.Lines[0].Baseline, 35.99, 36.01);
        Assert.All(explicitLeading.Lines, l => Assert.True(l.Baseline - l.Ascent >= TextLayout.Padding));
    }

    [Fact]
    public void Native_project_and_psd_roundtrip_preserve_character_sizes_and_appearance()
    {
        var s = EditorSession.NewCanvas(500, 280);
        var family = EditorSession.FontFamilies.FirstOrDefault(f => f == "Arial") ?? EditorSession.FontFamilies[0];
        var style = new TextStyle { Text = "a BIG text\nnext", FontFamily = family, Size = 20, Tracking = .5 }.WithSize(48, 2, 5).WithColor(0xFFFF8000, 2, 5);
        var original = s.AddText(new SKPoint(40, 40), style);
        using var project = new MemoryStream(); ProjectFile.Write(s.Document, project); project.Position = 0;
        Assert.Equal(style, ProjectFile.Read(project).ActiveLayer!.Text);
        using var psd = new MemoryStream(); PsdExport.Write(s.Document, psd);
        var imported = PsdImport.Load(psd.ToArray()); Assert.Empty(imported.Conversions);
        var actual = Assert.Single(imported.Layers, l => l.Text != null);
        for (var i = 0; i < style.Text.Length; i++) Assert.Equal(style.SizeAt(i), actual.Text!.SizeAt(i), 4);
        using var a = DocumentRenderer.Flatten(s.Document); using var b = DocumentRenderer.Flatten(imported.ToDocument());
        var pixelsA = a.GetPixelSpan(); var pixelsB = b.GetPixelSpan();
        for (var i = 0; i < pixelsA.Length; i++) Assert.InRange(Math.Abs(pixelsA[i] - pixelsB[i]), 0, 3);
        s.EditText(original); s.TextEdit!.MoveTo(2, false); s.TextEdit.MoveTo(5, true); s.SetTextSize(40);
        Assert.Equal(20, original.Text!.SizeAt(0)); Assert.Equal(40, original.Text.SizeAt(2));
        s.FinishText(); s.Undo(); Assert.Equal(48, s.Document.Find(original.Id)!.Text!.SizeAt(2));
        imported.Discard();
    }

    private static Layer Box(EditorSession s, int w, int h, int x, int y)
    {
        var p = Pixels.NewColor(w, h); p.Erase(SKColors.Red);
        return s.AddImageLayer("Box", p, new SKPoint(x + w / 2f, y + h / 2f), fit: false);
    }

    [Theory]
    [InlineData(ObjectAlignment.Left, 50, 200)]
    [InlineData(ObjectAlignment.Center, 55, 200)]
    [InlineData(ObjectAlignment.Right, 60, 200)]
    [InlineData(ObjectAlignment.Top, 220, 60)]
    [InlineData(ObjectAlignment.Middle, 220, 65)]
    [InlineData(ObjectAlignment.Bottom, 220, 70)]
    public void Alignment_uses_second_clicked_object_instead_of_active_or_stack_order(ObjectAlignment align, double x, double y)
    {
        var s = EditorSession.NewCanvas(500, 300); var other = Box(s, 20, 20, 220, 200); var anchor = Box(s, 30, 30, 50, 60);
        var third = Box(s, 20, 20, 350, 240);
        s.SelectLayer(other.Id); s.SelectLayer(anchor.Id, extend: true); s.SelectLayer(third.Id, extend: true);
        Assert.Equal(third.Id, s.Document.ActiveLayerId); var history = s.History.Count;
        s.AlignObjects(align, AlignmentReference.SecondSelected);
        Assert.Equal(x, other.Transform.X, 3); Assert.Equal(y, other.Transform.Y, 3); Assert.Equal(50, anchor.Transform.X); Assert.Equal(60, anchor.Transform.Y);
        Assert.Equal(history + 1, s.History.Count);
        s.Undo(); Assert.Equal(220, s.Document.Find(other.Id)!.Transform.X); Assert.Equal(other.Id, s.Document.SelectionAnchorId);
        Assert.Equal(new[] { other.Id, anchor.Id, third.Id }, s.Document.SelectionOrder);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Distribution_uses_equal_edge_gaps_with_unequal_sizes_and_keeps_endpoints(bool horizontal)
    {
        var s = EditorSession.NewCanvas(500, 500); var a = Box(s, 20, 20, 20, 20); var b = Box(s, 40, 40, 80, 80); var c = Box(s, 30, 30, 230, 230);
        s.SelectLayer(a.Id); s.SelectLayer(c.Id, extend: true); s.SelectLayer(b.Id, extend: true);
        s.DistributeObjectGaps(horizontal);
        Assert.Equal(115, horizontal ? b.Transform.X : b.Transform.Y, 3);
        Assert.Equal(20, horizontal ? a.Transform.X : a.Transform.Y); Assert.Equal(230, horizontal ? c.Transform.X : c.Transform.Y);
        Assert.Equal(80, horizontal ? b.Transform.Y : b.Transform.X);
        s.Undo(); Assert.Equal(80, s.Document.Find(b.Id)!.Transform.X); Assert.Equal(80, s.Document.Find(b.Id)!.Transform.Y);
        s.AlignObjects(ObjectAlignment.Bottom, AlignmentReference.Canvas);
        Assert.All(s.SelectedRoots(), l => Assert.InRange(l.ControlBounds.Bottom, 499.99, 500.01));
    }
}
