using Composa.Rendering;
using Composa.Editing;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Core.Tests;

public class PromptSegmentationTests
{
    [Fact]
    public async Task A_box_over_transparent_pixels_preserves_the_previous_selection()
    {
        var session = EditorSession.NewCanvas(80, 60, SKColors.Red);
        session.AddBlankLayer();
        session.SelectRect(new SKRect(4, 4, 20, 18));
        var previous = session.Selection;
        var state = session.History.CurrentId;
        Assert.False(await session.SelectPromptObjectAsync(PromptModels.EfficientSamS, null, new(40, 22, 68, 52), SelectionMode.Replace));
        Assert.Same(previous, session.Selection);
        Assert.Equal(state, session.History.CurrentId);
    }
    [Fact]
    public void A_box_uses_a_lower_scored_candidate_when_the_best_one_misses_the_gesture()
    {
        var logits = new float[2 * 20 * 12];
        logits[1] = 10; logits[240 + 7 * 20 + 9] = 3;
        Assert.Equal(1, PromptSegmentation.PromptCandidate(logits, [.95f, .72f], 20, 12, new(7, 5, 12, 9), default));
        Assert.Equal(-1, PromptSegmentation.PromptCandidate(logits, [.95f, .72f], 20, 12, new(15, 5, 18, 8), default));
    }

    [Fact]
    public void Grouped_print_keeps_counters_and_a_small_secondary_ink()
    {
        using var image = Pixels.NewColor(220, 110);
        image.Erase(new SKColor(28, 30, 32));
        using (var c = new SKCanvas(image))
        using (var p = new SKPaint { Color = SKColors.White, StrokeWidth = 8, Style = SKPaintStyle.Stroke, IsAntialias = true })
        {
            c.DrawRect(25, 22, 54, 66, p); c.DrawRect(95, 22, 54, 66, p);
            p.Color = SKColors.LimeGreen; c.DrawCircle(180, 55, 10, p);
        }
        using var mask = GraphicSelection.FromBox(image, new(10, 8, 210, 103), default);
        Assert.NotNull(mask);
        Assert.Equal(0, mask.GetPixel(50, 55).Alpha);
        Assert.Equal(0, mask.GetPixel(121, 55).Alpha);
        Assert.InRange(mask.GetPixel(26, 55).Alpha, (byte)250, (byte)255);
        Assert.InRange(mask.GetPixel(170, 55).Alpha, (byte)250, (byte)255);
        Assert.Equal(0, mask.GetPixel(180, 55).Alpha);
    }

    [Fact]
    public void A_tight_box_finds_repeated_dark_slots_across_a_bright_surface_band()
    {
        using var image = Pixels.NewColor(210, 100);
        image.Erase(new SKColor(28, 28, 28));
        using (var c = new SKCanvas(image))
        using (var p = new SKPaint { Color = SKColors.Silver })
        {
            c.DrawRect(0, 55, 210, 12, p);
            p.Color = new SKColor(2, 2, 2);
            foreach (var x in new[] { 28, 87, 148 }) c.DrawRoundRect(x, 26, 16, 48, 7, 7, p);
        }
        using var mask = RepeatedDetailSelection.FromBox(image, new(15, 14, 181, 86), default);
        Assert.NotNull(mask);
        foreach (var x in new[] { 36, 95, 156 }) Assert.Equal(255, mask.GetPixel(x, 60).Alpha);
        Assert.Equal(0, mask.GetPixel(65, 60).Alpha);
    }
    private static PromptModel Model(PromptModelKind kind) => kind switch
    { PromptModelKind.MobileSam => PromptModels.MobileSam, PromptModelKind.EfficientSamTi => PromptModels.EfficientSamTi, _ => PromptModels.EfficientSamS };

    [Fact]
    public void SAM_refinement_returns_a_crisp_selection_without_feathered_strips_including_canvas_edges()
    {
        using var guide=Pixels.NewColor(101,79);guide.Erase(SKColors.White);
        using(var draw=new SKCanvas(guide))using(var paint=new SKPaint {Color=SKColors.DarkRed})draw.DrawRect(0,0,61,55,paint);
        Pixels.Invalidate(guide);
        using var coarse=SelectionMask.FromRect(101,79,new(0,0,61,55));
        using var refined=PromptSegmentation.Refine(coarse,guide,default);
        for(var y=0;y<79;y++)for(var x=0;x<101;x++)
        {
            Assert.True(refined.GetPixel(x,y).Alpha is 0 or 255);
            Assert.Equal(coarse.GetPixel(x,y),refined.GetPixel(x,y));
        }
        var session=EditorSession.NewCanvas(101,79);session.AddImageLayer("SAM source",Pixels.Clone(guide),new(50.5f,39.5f),fit:false);
        session.ApplyAiSelection(Composa.AI.AiTaskKind.SelectSubject,Pixels.Clone(refined));session.ClearSelection();
        for(var y=0;y<55;y++)for(var x=0;x<61;x++)Assert.Equal(0,session.ActiveLayer!.Pixels!.GetPixel(x,y).Alpha);
        session.Undo();Assert.Equal(SKColors.DarkRed,session.ActiveLayer!.Pixels!.GetPixel(60,54));
    }

    [Theory]
    [InlineData(PromptModelKind.MobileSam, 240, 96)]
    [InlineData(PromptModelKind.MobileSam, 96, 240)]
    [InlineData(PromptModelKind.EfficientSamTi, 96, 240)]
    [InlineData(PromptModelKind.EfficientSamS, 240, 96)]
    [InlineData(PromptModelKind.EfficientSamS, 96, 240)]
    public void Real_models_preserve_object_geometry_on_landscape_and_portrait(PromptModelKind kind, int width, int height)
    {
        using var image = Pixels.NewColor(width, height); image.Erase(SKColors.White);
        var objectBounds = new SKRectI(width / 4, height / 4, width * 3 / 4, height * 3 / 4);
        using (var canvas = new SKCanvas(image)) using (var paint = new SKPaint { Color = new SKColor(190, 35, 45) }) canvas.DrawRect(objectBounds, paint);
        var model = Model(kind); Assert.True(model.Encoder.Verify()); Assert.True(model.Decoder.Verify());
        var embedding = PromptSegmentation.Encode(image, model);
        using var mask = PromptSegmentation.Decode(embedding, model, width, height, null,
            new SKRectI(objectBounds.Left - 3, objectBounds.Top - 3, objectBounds.Right + 3, objectBounds.Bottom + 3));
        int intersection = 0, union = 0;
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var actual = mask.GetPixel(x, y).Alpha >= 128; var expected = objectBounds.Contains(x, y);
            if (actual && expected) intersection++; if (actual || expected) union++;
        }
        var iou = (double)intersection / Math.Max(1, union);
        Assert.True(iou > .88, $"{kind}, {width}x{height}: IoU {iou:0.000}");
        Assert.Equal((byte)0, mask.GetPixel(3, 3).Alpha);
        Assert.Equal((byte)255, mask.GetPixel(width / 2, height / 2).Alpha);
    }

    [Fact]
    public void Mobile_logit_reconstruction_removes_padded_rows_before_resizing_portrait()
    {
        var logits = Enumerable.Repeat(-8f, 256 * 256).ToArray();
        for (var y = 64; y < 192; y++) for (var x = 24; x < 72; x++) logits[y * 256 + x] = 8;
        using var mask = PromptSegmentation.Reconstruct(logits, 256, 256, 384, 1024, 96, 256, true);
        Assert.Equal(new SKRectI(24, 64, 72, 192), SelectionMask.Bounds(mask, 128));
        Assert.Equal((byte)0, mask.GetPixel(90, 230).Alpha);
    }
    [Theory]
    [InlineData(PromptModelKind.MobileSam, -1)]
    [InlineData(PromptModelKind.MobileSam, 0)]
    [InlineData(PromptModelKind.MobileSam, 1)]
    [InlineData(PromptModelKind.EfficientSamTi, -1)]
    [InlineData(PromptModelKind.EfficientSamTi, 0)]
    [InlineData(PromptModelKind.EfficientSamTi, 1)]
    public async Task Committed_prompt_mask_survives_inference_cleanup_render_and_history(PromptModelKind kind, int edge)
    {
        var model = Model(kind);
        var session = EditorSession.NewCanvas(160, 96, SKColors.White); session.ObjectEdgeOffset = edge;
        var image = Pixels.NewColor(160, 96); image.Erase(SKColors.White);
        using (var canvas = new SKCanvas(image)) using (var paint = new SKPaint { Color = SKColors.Red }) canvas.DrawRect(45, 20, 65, 50, paint);
        session.AddImageLayer("Object", image, new SKPoint(80, 48), fit: false);
        await session.SelectPromptObjectAsync(model, null, new SKRectI(40, 15, 115, 75), SelectionMode.Replace);
        var selection = Assert.IsType<SKBitmap>(session.Selection);
        Assert.Equal(160, selection.Width); Assert.Equal(96, selection.Height);
        Assert.NotEqual(IntPtr.Zero, selection.GetPixels());
        var before = selection.GetPixelSpan().ToArray();
        using var render = session.Flatten();
        session.Undo(); Assert.Null(session.Selection); session.Redo();
        Assert.Equal(before, session.Selection!.GetPixelSpan().ToArray());
        await session.SelectPromptObjectAsync(model, new SKPointI(70, 40), null, SelectionMode.Add);
        Assert.Equal(160, session.Selection!.Width);
    }

    [Theory]
    [InlineData(PromptModelKind.MobileSam)]
    [InlineData(PromptModelKind.EfficientSamTi)]
    [InlineData(PromptModelKind.EfficientSamS)]
    public void Real_encoder_decoder_accept_point_and_box_on_non_square_image(PromptModelKind kind)
    {
        var model = Model(kind);
        Assert.True(model.Encoder.Verify()); Assert.True(model.Decoder.Verify());
        using var image = Pixels.NewColor(160, 96); image.Erase(SKColors.White);
        using (var canvas = new SKCanvas(image)) { using var paint = new SKPaint { Color = SKColors.Red }; canvas.DrawRect(45, 20, 65, 50, paint); }
        var embedding = PromptSegmentation.Encode(image, model);
        if (kind == PromptModelKind.EfficientSamS)
        {
            using var identical = Pixels.Clone(image);
            Assert.Same(embedding, PromptSegmentation.Encode(identical, model, embedding, default));
            identical.Erase(SKColors.Blue);
            Assert.NotSame(embedding, PromptSegmentation.Encode(identical, model, embedding, default));
        }
        using var point = PromptSegmentation.Decode(embedding, model, image.Width, image.Height, new SKPoint(70, 40), null);
        using var box = PromptSegmentation.Decode(embedding, model, image.Width, image.Height, null, new SKRectI(40, 15, 115, 75));
        Assert.Equal(image.Width, point.Width); Assert.Equal(image.Height, point.Height);
        Assert.True(box.GetPixel(70, 40).Alpha > box.GetPixel(10, 10).Alpha, "Box mask should prefer the prompted object to the background.");
    }
}
