using Composa.ColorManagement;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Core.Tests;

public class ChannelTransformTests
{
    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Moving_a_component_preserves_other_components_alpha_placement_and_exact_return_to_start(ColorDepth depth)
    {
        var pixels = Pixels.NewWorkingColor(80, 60, depth, RgbProfile.DisplayP3); var original = new PixelBuffer(pixels);
        for (var y = 0; y < 60; y++) for (var x = 0; x < 80; x++) original.Write(x, y, new(.17f, (20000 + x * 50) / 65535f * .7f, .47f, .7f));
        var layer = Layer.Raster("Image", pixels); var document = new Document(80, 60) { Depth = depth, Profile = RgbProfile.DisplayP3 }; document.InsertAboveActive(layer);
        var session = new EditorSession(document); var frame = layer.Transform; var bytes = pixels.Bytes; session.EditRgbChannels(RgbChannels.Green);
        var edit = session.BeginTransform("Move")!; edit.MoveBy(7, 0);
        Assert.Equal(frame, layer.Transform); CheckOtherComponents();
        var moved = new PixelBuffer(layer.Pixels!); Assert.InRange(Math.Abs(moved.Read(27, 20).G - original.Read(20, 20).G), 0, depth == ColorDepth.Sixteen ? 2e-7f : 1f / 255);
        edit.MoveBy(0, 0); Assert.Same(pixels, layer.Pixels); Assert.Equal(bytes, pixels.Bytes);
        edit.MoveBy(7, 0); session.CommitTransform(); CheckOtherComponents(); Assert.Equal(bytes, pixels.Bytes);
        session.Undo(); Assert.Same(pixels, session.ActiveLayer!.Pixels); Assert.Equal(frame, session.ActiveLayer.Transform);

        void CheckOtherComponents()
        {
            var actual = new PixelBuffer(layer.Pixels!);
            for (var y = 0; y < 60; y++) for (var x = 0; x < 80; x++)
            { var a = original.Read(x, y); var b = actual.Read(x, y); Assert.Equal(a.R, b.R); Assert.Equal(a.B, b.B); Assert.Equal(a.A, b.A); }
        }
    }

    [Fact]
    public void Alpha_selection_moves_black_and_white_values_and_can_duplicate_cancel_and_undo_without_a_layer()
    {
        var session = new EditorSession(new Document(100, 80)); session.AddAlphaChannel("Mask");
        session.Select(SelectionMask.FromRect(100, 80, new(10, 10, 20, 25)), SelectionMode.Replace, "Selection"); session.Fill(SKColors.White);
        session.Select(SelectionMask.FromRect(100, 80, new(35, 10, 40, 25)), SelectionMode.Replace, "Selection"); session.Fill(SKColors.White);
        session.Select(SelectionMask.FromRect(100, 80, new(10, 10, 30, 25)), SelectionMode.Replace, "Selection");
        var selection = session.Selection; var original = session.Document.AlphaChannels["Mask"]; var bytes = original.Bytes;
        var edit = session.BeginTransform("Move")!; edit.MoveBy(15, 0);
        Assert.Equal(0, session.Document.AlphaChannels["Mask"].GetPixel(15, 15).Alpha);
        Assert.Equal(255, session.Document.AlphaChannels["Mask"].GetPixel(30, 15).Alpha);
        Assert.Equal(0, session.Document.AlphaChannels["Mask"].GetPixel(37, 15).Alpha); // Selected black is data, not transparency.
        Assert.Equal(new SKRectI(25, 10, 45, 25), SelectionMask.Bounds(session.Selection!));
        edit.MoveBy(0, 0); Assert.Same(original, session.Document.AlphaChannels["Mask"]); Assert.Same(selection, session.Selection);
        edit.MoveBy(15, 0); session.CancelTransform(); Assert.Same(original, session.Document.AlphaChannels["Mask"]); Assert.Equal(bytes, original.Bytes);
        Assert.True(selection!.Handle != IntPtr.Zero); // SelectionInTargetSpace can return the committed selection itself.
        edit = session.BeginTransform("Move", duplicate: true)!; edit.MoveBy(15, 0); session.CommitTransform();
        Assert.Equal(255, session.Document.AlphaChannels["Mask"].GetPixel(15, 15).Alpha); Assert.Equal(255, session.Document.AlphaChannels["Mask"].GetPixel(30, 15).Alpha);
        Assert.Equal(0, session.Document.AlphaChannels["Mask"].GetPixel(37, 15).Alpha);
        Assert.Empty(session.Document.Layers); session.Undo(); Assert.Same(original, session.Document.AlphaChannels["Mask"]);
    }

    [Fact]
    public void Inspector_and_repeated_nudges_follow_actual_channel_contents_without_moving_the_image()
    {
        var session = EditorSession.NewCanvas(100, 80, new SKColor(40, 120, 200)); var layer = session.ActiveLayer!; var frame = layer.Transform;
        session.AddAlphaChannel("Mask"); session.Select(SelectionMask.FromRect(100, 80, new(10, 10, 20, 25)), SelectionMode.Replace, "Selection"); session.Fill(SKColors.White); session.Deselect();
        Assert.Equal(10, session.ChannelControlTransform.X);
        session.SetChannelControlTransform(session.ChannelControlTransform with { X = 15 }); Assert.Equal(15, session.ChannelControlTransform.X);
        session.Nudge(3, 0); Assert.Equal(18, session.ChannelControlTransform.X); Assert.Equal(frame, layer.Transform);
        Assert.Equal(255, session.Document.AlphaChannels["Mask"].GetPixel(20, 15).Alpha); Assert.Equal(0, session.Document.AlphaChannels["Mask"].GetPixel(10, 15).Alpha);
        session.Undo(); Assert.Equal(15, session.ChannelControlTransform.X); session.Undo(); Assert.Equal(10, session.ChannelControlTransform.X);
    }

    [Fact]
    public void Channel_selection_frame_handles_perspective_and_target_switch_commits_the_right_plane()
    {
        var session = EditorSession.NewCanvas(120, 100, new SKColor(40, 120, 200), ColorDepth.Sixteen);
        var layer = session.ActiveLayer!; layer.Transform = layer.Transform with { Rotation = 18, Distort = [2, 0, -3, 2, 4, -2, 0, 1] };
        session.Select(SelectionMask.FromRect(120, 100, new(20, 20, 70, 60)), SelectionMode.Replace, "Selection");
        session.EditRgbChannels(RgbChannels.Red); var original = layer.Pixels!; var bytes = original.Bytes; var frame = layer.Transform;
        var edit = session.BeginTransform("Distort")!; var corner = new SKPoint(76, 14); edit.DistortCorner(1, corner);
        Assert.InRange(SKPoint.Distance(edit.Corners()[1], corner), 0, .01f); Assert.Equal(frame, layer.Transform);
        var before = new PixelBuffer(original); var after = new PixelBuffer(layer.Pixels!);
        for (var y = 0; y < 100; y++) for (var x = 0; x < 120; x++)
        { Assert.Equal(before.Read(x, y).G, after.Read(x, y).G); Assert.Equal(before.Read(x, y).B, after.Read(x, y).B); Assert.Equal(before.Read(x, y).A, after.Read(x, y).A); }
        session.EditingMask = false; Assert.Null(session.Transform); Assert.Equal(bytes, original.Bytes); Assert.Equal(frame, layer.Transform);
        session.Undo(); Assert.Same(original, session.ActiveLayer!.Pixels);
    }
}
