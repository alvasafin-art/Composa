using Composa.ColorManagement;
using Composa.Editing;
using Composa.Filters;
using Composa.IO;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Core.Tests;

[Collection(ClipboardCollection.Name)]
public class ChannelEditingTests
{
    [Fact]
    public void Copy_cut_and_paste_edit_the_channel_without_creating_or_deleting_image_layers()
    {
        var session = EditorSession.NewCanvas(80, 60, new SKColor(40, 120, 200)); var image = session.ActiveLayer!.Pixels!;
        session.EditRgbChannels(RgbChannels.Green); Assert.False(session.CanCopyLayers); Assert.True(session.Copy());
        Assert.Null(EditorSession.CopiedLayers); Assert.Equal(new SKColor(120, 120, 120), EditorSession.Clipboard!.Pixels.GetPixel(20, 20));
        session.Cut(); Assert.Single(session.Document.Layers); Assert.Equal(new SKColor(40, 0, 200), session.ActiveLayer.Pixels!.GetPixel(20, 20));
        Assert.NotNull(session.Paste()); Assert.Single(session.Document.Layers); Assert.Equal(new SKColor(40, 120, 200), session.ActiveLayer.Pixels.GetPixel(20, 20));
        session.Undo(); Assert.Equal(new SKColor(40, 0, 200), session.ActiveLayer!.Pixels!.GetPixel(20, 20)); session.Undo(); Assert.Same(image, session.ActiveLayer.Pixels);
        session.AddAlphaChannel("Mask", revealAll: true); session.Cut(); Assert.Single(session.Document.Layers); Assert.Equal(0, session.Document.AlphaChannels["Mask"].GetPixel(20, 20).Alpha);
        session.Paste(); Assert.Equal(255, session.Document.AlphaChannels["Mask"].GetPixel(20, 20).Alpha); Assert.Same(image, session.ActiveLayer.Pixels);
        session.Fill(SKColors.Black); session.Copy(); session.Fill(SKColors.White); session.Paste();
        Assert.Equal(0, session.Document.AlphaChannels["Mask"].GetPixel(20, 20).Alpha); Assert.Single(session.Document.Layers);
    }

    [Fact]
    public void Alpha_channels_duplicate_reorder_load_and_rename_without_sharing_mutable_pixels()
    {
        var session = EditorSession.NewCanvas(80, 60, new SKColor(40, 120, 200));
        session.LoadRgbChannel(RgbChannels.Green); Assert.Equal(120, session.Selection!.GetPixel(20, 20).Alpha);
        session.AddAlphaChannel("Saved Green", fromSelection: true); session.Deselect(); var saved = session.Document.AlphaChannels["Saved Green"];
        session.DuplicateAlphaChannel("Saved Green"); Assert.Same(saved, session.Document.AlphaChannels["Saved Green copy"]);
        session.Fill(SKColors.White); Assert.Equal(120, saved.GetPixel(20, 20).Alpha);
        Assert.Equal(255, session.Document.AlphaChannels["Saved Green copy"].GetPixel(20, 20).Alpha);
        session.MoveAlphaChannel("Saved Green copy", -1); Assert.Equal("Saved Green copy", session.Document.AlphaChannels.Keys.First());
        session.Undo(); Assert.Equal("Saved Green", session.Document.AlphaChannels.Keys.First());
        session.LoadSelection("Saved Green"); Assert.Equal(120, session.Selection!.GetPixel(20, 20).Alpha);
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Component_fill_brush_and_preview_preserve_other_channels_alpha_profile_and_history(ColorDepth depth)
    {
        var image = Pixels.NewWorkingColor(80, 60, depth, RgbProfile.DisplayP3); var input = new PixelBuffer(image);
        for (var y = 0; y < 60; y++) for (var x = 0; x < 80; x++) input.Write(x, y, new(.11f + x / 65535f, .21f, .31f, .5f));
        var layer = Layer.Raster("Source", image); var document = new Document(80, 60) { Depth = depth, Profile = RgbProfile.DisplayP3 }; document.InsertAboveActive(layer);
        var session = new EditorSession(document); var before = image.Bytes;
        session.EditRgbChannels(RgbChannels.Green); Assert.False(session.IsModified);
        session.Fill(SKColors.White); CheckOtherComponents(); Assert.InRange(new PixelBuffer(layer.Pixels!).Read(20, 20).G, .49f, .51f);
        session.Undo(); layer = session.ActiveLayer!; Assert.Same(image, layer.Pixels); Assert.Equal(before, image.Bytes);
        Assert.Null(session.PaintStroke([new(20, 20), new(50, 20)], new BrushSettings { Size = 16, Hardness = 1 }, SKColors.Black));
        CheckOtherComponents(); Assert.InRange(new PixelBuffer(layer.Pixels!).Read(30, 20).G, 0, .01f);
        var painted = layer.Pixels; var paintedBytes = painted!.Bytes;
        Assert.True(session.BeginPreview("Invert")); session.PreviewAdjustment(new InvertAdjustment()); CheckOtherComponents();
        session.CancelPreview(); Assert.Same(painted, session.ActiveLayer!.Pixels); Assert.Equal(paintedBytes, painted.Bytes); Assert.Equal(before, image.Bytes);
        session.Undo(); Assert.Same(image, session.ActiveLayer!.Pixels);

        void CheckOtherComponents()
        {
            Assert.Equal(depth, Pixels.DepthOf(layer.Pixels!)); Assert.Equal(RgbProfile.DisplayP3, Pixels.ProfileOf(layer.Pixels!));
            var actual = new PixelBuffer(layer.Pixels!);
            for (var y = 0; y < 60; y++) for (var x = 0; x < 80; x++)
            {
                var a = input.Read(x, y); var b = actual.Read(x, y);
                Assert.Equal(a.R, b.R); Assert.Equal(a.B, b.B); Assert.Equal(a.A, b.A);
            }
        }
    }

    [Fact]
    public void Alpha_channel_is_paintable_without_a_layer_and_retains_selection_filter_undo_and_native_save()
    {
        var session = new EditorSession(new Document(80, 60)); session.AddAlphaChannel("Mask");
        var black = session.Document.AlphaChannels["Mask"]; var original = black.Bytes;
        Assert.True(session.CanEditPixels); Assert.True(session.IsEditingMask);
        Assert.Null(session.PaintStroke([new(20, 20), new(50, 20)], new BrushSettings { Size = 16, Hardness = 1 }, SKColors.White));
        var painted = session.Document.AlphaChannels["Mask"]; Assert.Equal(255, painted.GetPixel(30, 20).Alpha); Assert.Equal(original, black.Bytes);
        session.Select(SelectionMask.FromRect(80, 60, new SKRect(0, 0, 40, 60)), SelectionMode.Replace, "Selection");
        session.Fill(SKColors.Gray); Assert.InRange((int)session.Document.AlphaChannels["Mask"].GetPixel(30, 20).Alpha, 126, 129);
        Assert.Equal(255, session.Document.AlphaChannels["Mask"].GetPixel(45, 20).Alpha);
        session.Deselect(); Assert.True(session.BeginFilter(FilterKind.GaussianBlur)); session.PreviewFilter(new FilterSettings { Kind = FilterKind.GaussianBlur, Radius = 3 }); session.CommitPreview();
        Assert.Equal(SKColorType.Alpha8, session.Document.AlphaChannels["Mask"].ColorType); Assert.Empty(session.Document.Layers);
        var bytes = session.Document.AlphaChannels["Mask"].Bytes;
        using var file = new MemoryStream(); ProjectFile.Write(session.Document, file); file.Position = 0; var restored = ProjectFile.Read(file);
        Assert.Equal(bytes, restored.AlphaChannels["Mask"].Bytes); Assert.Empty(restored.Layers);
        session.RenameAlphaChannel("Mask", "Hair"); Assert.Equal("Hair", session.EditingAlphaChannel);
        session.Undo(); Assert.Equal("Mask", session.EditingAlphaChannel); Assert.Equal(bytes, session.Document.AlphaChannels["Mask"].Bytes);
        session.DeleteSelectionChannel("Mask"); Assert.False(session.IsEditingAlphaChannel); session.Undo(); Assert.Single(session.Document.AlphaChannels);
    }

    [Fact]
    public void Channel_view_samples_working_numbers_and_alpha_without_changing_exports_or_history()
    {
        var session = EditorSession.NewCanvas(32, 24, new SKColor(32, 128, 224)); var original = session.Flatten().Bytes; var state = session.History.CurrentId;
        session.EditRgbChannels(RgbChannels.Green);
        using var display = Pixels.NewColor(32, 24); session.RenderView(display, new(0, 0, 32, 24), RenderView.Identity);
        Assert.Equal(new SKColor(128, 128, 128), display.GetPixel(10, 10));
        session.SetChannelView(RgbChannels.All); session.RenderView(display, new(0, 0, 32, 24), RenderView.Identity);
        Assert.Equal(new SKColor(32, 128, 224), display.GetPixel(10, 10)); Assert.Equal(RgbChannels.Green, session.EditingRgbChannels);
        Assert.Equal(state, session.History.CurrentId); Assert.Equal(original, session.Flatten().Bytes);
        session.AddAlphaChannel("Alpha"); session.Fill(SKColors.Gray); var alphaState = session.History.CurrentId;
        session.RenderView(display, new(0, 0, 32, 24), RenderView.Identity); Assert.Equal(new SKColor(128, 128, 128), display.GetPixel(10, 10));
        session.SetChannelView(RgbChannels.All, alphaOverlay: true); session.RenderView(display, new(0, 0, 32, 24), RenderView.Identity);
        Assert.True(display.GetPixel(10, 10).Red > 32); Assert.Equal(original, session.Flatten().Bytes); Assert.Equal(alphaState, session.History.CurrentId);
    }
}
