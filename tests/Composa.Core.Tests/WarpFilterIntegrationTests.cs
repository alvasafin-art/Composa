using Composa.ColorManagement;
using Composa.Editing;
using Composa.Filters;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class WarpFilterIntegrationTests
{
    private static (EditorSession Session, Layer Layer) Scene(ColorDepth depth)
    {
        var document = new Document(360, 260) { Depth = depth, Profile = RgbProfile.DisplayP3 };
        var source = Pixels.NewWorkingColor(100, 80, depth, document.Profile); var pixels = new PixelBuffer(source);
        for (var y = 0; y < 80; y++) for (var x = 0; x < 100; x++) pixels.Write(x, y, new(.24f + x / 65535f, .35f + y / 65535f, .14f, .8f));
        var layer = Layer.Raster("Object", source, 80, 80); document.InsertAboveActive(layer); var session = new EditorSession(document);
        session.SetWarp(layer, WarpLayerTests.Shift(WarpMesh.Create(), .4, 0).WithPoint(5, new(.66, .23)));
        return (session, layer);
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Filter_padding_does_not_redefine_the_mesh_basis_or_move_any_control_node(ColorDepth depth)
    {
        var (session, layer) = Scene(depth); var mesh = layer.Warp!; var points = mesh.Points.Select(layer.WarpNodePosition).ToArray();
        session.AddSmartFilter(layer, new() { Kind = FilterKind.GaussianBlur, Radius = 4 });
        Assert.True(layer.FilterPaddingX > 0); Assert.True(layer.Pixels!.Width > layer.WarpSourceSize.Width); Assert.Equal(new SKSizeI(100, 80), layer.WarpSourceSize);
        for (var i = 0; i < points.Length; i++) Assert.InRange(SKPoint.Distance(points[i], layer.WarpNodePosition(mesh.Points[i])), 0, .0002f);
        var edit = session.BeginWarp(); Assert.Equal(mesh, edit.Mesh); edit.MovePoint(5, points[5] + new SKPoint(3, -2)); session.CancelWarp();
        layer = session.ActiveLayer!; Assert.Equal(mesh, layer.Warp); Assert.NotNull(layer.FilterSource);
        session.ChangeSmartFilter(layer, layer.SmartFilters[0].Id, remove: true);
        for (var i = 0; i < points.Length; i++) Assert.InRange(SKPoint.Distance(points[i], layer.WarpNodePosition(mesh.Points[i])), 0, .0002f);
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Filter_mask_paints_at_the_visible_pointer_beyond_the_old_source_frame_and_reuses_both_warped_materials(ColorDepth depth)
    {
        var (session, layer) = Scene(depth); using var baseline = session.Flatten();
        session.AddSmartFilter(layer, new() { Kind = FilterKind.GaussianBlur, Radius = 3 });
        session.AddSmartFilter(layer, new() { Kind = FilterKind.AddNoise, Amount = 65, Seed = 17 });
        using var full = session.Flatten(); session.AddSmartFilterMask(layer, hideAll: true);
        using var black = session.Flatten(); Close(baseline, black, depth == ColorDepth.Sixteen ? 4f / 65535 : 2f / 255);
        var before = layer.WarpAppearance(); var filtered = before.Pixels; var original = before.FilterSource;
        Assert.NotNull(original); Assert.NotSame(original, filtered);
        var error = session.PaintStroke([new SKPoint(200, 120), new SKPoint(201, 120)], new() { Size = 14, Hardness = 1, Opacity = 1 }, SKColors.White);
        Assert.Null(error); using var painted = session.Flatten();
        var actual = new PixelBuffer(painted); var expected = new PixelBuffer(full); var unchanged = new PixelBuffer(baseline);
        var tolerance = depth == ColorDepth.Sixteen ? 4f / 65535 : 2f / 255;
        for (var c = 0; c < 4; c++)
        {
            Assert.InRange(Math.Abs(actual.Read(200, 120)[c] - expected.Read(200, 120)[c]), 0, tolerance);
            Assert.InRange(Math.Abs(actual.Read(155, 120)[c] - unchanged.Read(155, 120)[c]), 0, tolerance);
        }
        Assert.True(Math.Abs(actual.Read(200, 120).R - unchanged.Read(200, 120).R) + Math.Abs(actual.Read(200, 120).G - unchanged.Read(200, 120).G) > .015);
        var after = layer.WarpAppearance(); Assert.Same(filtered, after.Pixels); Assert.Same(original, after.FilterSource);
        Assert.Same(after.RenderFilterPixels(), layer.Clone().WarpAppearance().RenderFilterPixels());
        using var stream = new MemoryStream(); ProjectFile.Write(session.Document, stream); stream.Position = 0; var loaded = ProjectFile.Read(stream);
        using var restored = DocumentRenderer.Flatten(loaded); Close(painted, restored, depth == ColorDepth.Sixteen ? 6f / 65535 : 2f / 255);
        session.RasterizeShape(layer); using var rasterized = session.Flatten(); Close(painted, rasterized, tolerance);
    }

    [Fact]
    public void Existing_filter_stack_is_prepared_in_its_current_grid_and_unchanged_commit_restores_exact_source_fields()
    {
        var session = EditorSession.NewCanvas(120, 90, SKColors.Coral); var layer = session.ActiveLayer!;
        session.AddSmartFilter(layer, new() { Kind = FilterKind.GaussianBlur, Radius = 3 });
        var filtered = layer.Pixels; var original = layer.FilterSource; var filters = layer.SmartFilters; var placement = layer.Transform; var history = session.History.Count;
        var edit = session.BeginWarp(); Assert.Null(layer.FilterSource); Assert.NotNull(layer.SmartObject); session.CommitWarp();
        Assert.Same(filtered, layer.Pixels); Assert.Same(original, layer.FilterSource); Assert.Same(filters, layer.SmartFilters); Assert.Equal(placement, layer.Transform); Assert.Equal(history, session.History.Count);
        edit = session.BeginWarp(); var point = layer.WarpNodePosition(edit.Mesh.Points[5]); edit.MovePoint(5, point + new SKPoint(3, -3));
        session.CommitWarp(); Assert.NotNull(layer.Warp); Assert.NotNull(layer.SmartObject); Assert.Null(layer.FilterSource);
        Assert.Same(filtered, layer.SmartObject!.OpenDocument().ActiveLayer!.Pixels);
    }

    [Fact]
    public void Source_pixels_added_outside_the_old_PNG_control_region_are_visible_after_updating_the_smart_object()
    {
        var session = EditorSession.NewCanvas(360, 260); session.Document.Layers.Clear();
        var image = Pixels.NewColor(100, 80);
        using (var canvas = new SKCanvas(image)) using (var paint = new SKPaint { Color = SKColors.Red }) canvas.DrawRect(30, 20, 40, 40, paint);
        var layer = session.AddImageLayer("PNG", image, new(130, 120), fit: false);
        session.SetWarp(layer, WarpLayerTests.Shift(EditorSession.WarpGridFor(layer), .1, 0));
        Assert.True(layer.Warp!.Domain.Width < 1); var mesh = layer.Warp; var source = layer.SmartObject!; var contents = source.OpenDocument();
        var original = contents.ActiveLayer!.Pixels!; var bytes = original.Bytes; var next = Pixels.Clone(original);
        using (var canvas = new SKCanvas(next)) using (var paint = new SKPaint { Color = SKColors.Blue }) canvas.DrawRect(84, 35, 10, 10, paint);
        Pixels.Invalidate(next); contents.ActiveLayer.Pixels = next; session.UpdateSmartObject(source, contents);
        Assert.Equal(mesh, layer.Warp); Assert.Equal(bytes, original.Bytes);
        using var rendered = session.Flatten(); Assert.Equal(SKColors.Blue, rendered.GetPixel(178, 120));
        Assert.True(layer.Bounds.Contains(178, 120));
    }

    [Fact]
    public void Native_and_reduced_render_bands_can_share_mask_caches_during_collection_without_changing_pixels()
    {
        var (session, layer) = Scene(ColorDepth.Sixteen);
        session.AddSmartFilter(layer, new() { Kind = FilterKind.GaussianBlur, Radius = 3 });
        session.AddSmartFilter(layer, new() { Kind = FilterKind.AddNoise, Amount = 45, Seed = 19 });
        session.AddSmartFilterMask(layer, hideAll: true);
        Assert.Null(session.PaintStroke([new SKPoint(185, 120), new SKPoint(205, 120)], new() { Size = 24, Hardness = .6 }, SKColors.White));
        using var full = session.Flatten(); var expected = full.Bytes;
        using var preview = Pixels.NewWorkingColor(180, 130, ColorDepth.Sixteen, session.Document.Profile);
        DocumentRenderer.Render(session.Document, preview, new(0, 0, 180, 130), new RenderView(.5f, SKPoint.Empty)); var expectedSmall = preview.Bytes;
        var document = session.Document.Clone();
        Parallel.For(0, 12, i =>
        {
            var snapshot = document.Clone(); GC.Collect();
            if (i % 2 == 0) { using var rendered = DocumentRenderer.Flatten(snapshot); Assert.Equal(expected, rendered.Bytes); }
            else
            {
                using var reduced = Pixels.NewWorkingColor(180, 130, ColorDepth.Sixteen, snapshot.Profile);
                DocumentRenderer.Render(snapshot, reduced, new(0, 0, 180, 130), new RenderView(.5f, SKPoint.Empty)); Assert.Equal(expectedSmall, reduced.Bytes);
            }
        });
    }

    [Fact]
    public void Resizing_smart_contents_updates_the_canonical_grid_and_renders_new_content_with_the_retained_mesh()
    {
        var (session, layer) = Scene(ColorDepth.Sixteen); var mesh = layer.Warp!; var source = layer.SmartObject!; var original = source.Preview.Bytes;
        session.AddSmartFilter(layer, new() { Kind = FilterKind.GaussianBlur, Radius = 3 });
        var content = new Document(150, 90) { Depth = ColorDepth.Sixteen, Profile = session.Document.Profile };
        var image = Pixels.NewWorkingColor(150, 90, content.Depth, content.Profile);
        using (var canvas = new SKCanvas(image)) canvas.DrawBitmap(source.Preview, 0, 0);
        var data = new PixelBuffer(image); for (var y = 20; y < 40; y++) for (var x = 125; x < 145; x++) data.Write(x, y, new(.1f, .7f, .1f, 1)); Pixels.Invalidate(image);
        content.InsertAboveActive(Layer.Raster("Expanded source", image)); session.UpdateSmartObject(source, content);
        Assert.Equal(mesh, layer.Warp); Assert.Equal(new SKSizeI(150, 90), layer.WarpSourceSize); Assert.Equal(original, source.Preview.Bytes);
        var point = mesh.Evaluate(.9, 1.0 / 3); var document = layer.WarpMatrix.MapPoint((float)(point.X * 150), (float)(point.Y * 90));
        using var result = session.Flatten(); var color = new PixelBuffer(result).Read((int)document.X, (int)document.Y);
        Assert.True(color.G > color.R + .3f && color.G > color.B + .3f); Assert.InRange(color.A, .99f, 1f);
    }

    private static void Close(SKBitmap first, SKBitmap second, float tolerance)
    {
        Assert.Equal(first.Info.Size, second.Info.Size); var a = new PixelBuffer(first); var b = new PixelBuffer(second);
        for (var y = 0; y < first.Height; y++) for (var x = 0; x < first.Width; x++) for (var c = 0; c < 4; c++)
            Assert.InRange(Math.Abs(a.Read(x, y)[c] - b.Read(x, y)[c]), 0, tolerance);
    }
}
