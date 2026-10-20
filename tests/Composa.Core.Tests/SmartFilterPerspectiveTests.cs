using Composa.Editing;
using Composa.Filters;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class SmartFilterPerspectiveTests
{
    private static void SameSourcePlacement(SKMatrix expected, Layer layer, int sourceWidth, int sourceHeight)
    {
        foreach (var point in new[] { new SKPoint(0, 0), new(sourceWidth, 0), new(sourceWidth, sourceHeight), new(0, sourceHeight), new(sourceWidth * .3f, sourceHeight * .7f) })
        {
            var a = expected.MapPoint(point);
            var b = layer.Matrix.MapPoint(point.X + layer.FilterPaddingX, point.Y + layer.FilterPaddingY);
            Assert.InRange(Math.Abs(a.X - b.X), 0, .002); Assert.InRange(Math.Abs(a.Y - b.Y), 0, .002);
        }
    }

    [Theory]
    [InlineData(ColorDepth.Eight, false)]
    [InlineData(ColorDepth.Sixteen, true)]
    public void Changing_growing_filters_keeps_perspective_source_mask_and_vector_coordinates(ColorDepth depth, bool flipped)
    {
        var session = EditorSession.NewCanvas(180, 140, SKColors.Red, depth: depth); var layer = session.ActiveLayer!;
        layer.Transform = layer.Transform with { X = 10, Y = 8, Width = 110, Height = 90, Rotation = 19, FlipHorizontal = flipped,
            Distort = [0, 0, 9, 4, -12, 0, 3, -9] };
        var source = layer.Pixels!; var placement = layer.Matrix;
        session.AddMask(layer); session.SetMaskLinked(layer, false); session.EditingMask = false;
        var mask = layer.Mask; var maskMatrix = layer.MaskMatrix;
        layer.VectorMask = new() { Closed = true, Nodes = [BezierNode.Corner(.2, .2), BezierNode.Corner(.8, .2), BezierNode.Corner(.8, .8), BezierNode.Corner(.2, .8)] };
        var nodePosition = placement.MapPoint(source.Width * .2f, source.Height * .2f);
        session.AddSmartFilter(layer, new() { Kind = FilterKind.GaussianBlur, Radius = 2, ClampEdges = false });
        SameSourcePlacement(placement, layer, source.Width, source.Height); var filter = layer.SmartFilters.Single();
        session.ChangeSmartFilter(layer, filter.Id, settings: filter.Settings with { Radius = 7 });
        SameSourcePlacement(placement, layer, source.Width, source.Height); Assert.Same(mask, layer.Mask); Assert.Equal(maskMatrix, layer.MaskMatrix);
        var node = layer.VectorMask!.Nodes[0]; var point = layer.Matrix.MapPoint((float)node.X * layer.Pixels!.Width, (float)node.Y * layer.Pixels.Height);
        Assert.InRange(Math.Abs(point.X - nodePosition.X), 0, .002); Assert.InRange(Math.Abs(point.Y - nodePosition.Y), 0, .002);
        using var flattened = session.Flatten(); Assert.NotNull(flattened);
        session.ChangeSmartFilter(layer, filter.Id, enabled: false); Assert.Same(source, layer.Pixels);
        SameSourcePlacement(placement, layer, source.Width, source.Height);
        session.Undo(); layer = session.ActiveLayer!; SameSourcePlacement(placement, layer, source.Width, source.Height);
        using var stream = new MemoryStream(); ProjectFile.Write(session.Document, stream); stream.Position = 0;
        var document = ProjectFile.Read(stream); var restored = new EditorSession(document); layer = restored.ActiveLayer!;
        restored.ChangeSmartFilter(layer, filter.Id, remove: true); SameSourcePlacement(placement, layer, source.Width, source.Height);
        Assert.Null(layer.FilterSource);
    }

    [Fact]
    public void Updating_smart_object_content_after_perspective_and_filter_growth_keeps_the_same_quad()
    {
        var session = EditorSession.NewCanvas(80, 60, SKColors.Red); var layer = session.ConvertToSmartObject();
        layer.Transform = layer.Transform with { X = 3, Y = 4, Distort = [0, 2, -8, 0, 3, -6, 6, 0] };
        var placement = layer.Matrix;
        session.AddSmartFilter(layer, new() { Kind = FilterKind.GaussianBlur, Radius = 3 });
        var content = new EditorSession(layer.SmartObject!.OpenDocument()); content.Fill(SKColors.Blue);
        session.UpdateSmartObject(layer.SmartObject, content.Document);
        SameSourcePlacement(placement, layer, 80, 60);
        Assert.True(layer.Pixels!.GetPixel(layer.FilterPaddingX + 40, layer.FilterPaddingY + 30).Blue > 240);
    }
}
