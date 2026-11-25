using Composa.Rendering;
using Composa.Editing;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Core.Tests;

public class PromptSegmentationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Loose_box_recovers_contents_only_when_a_new_prompt_confirms_the_assembly(bool contained)
    {
        const int width = 100, height = 80;
        var planes = new float[2 * width * height];
        var proposed = new float[width * height];
        Array.Fill(planes, -1f);
        Array.Fill(proposed, -1f);
        for (int y = 23; y < 57; y++)
            for (int x = 20; x < 80; x++)
            {
                if (x < 24 || x >= 76 || y < 27 || y >= 53)
                    planes[y * width + x] = 1;
                if (x >= 23 && x < 77 && y >= 26 && y < 54)
                    proposed[y * width + (contained ? x : x + 20)] = 1;
            }

        var result = BoxMaskCandidates.Resolve(planes, [.9f, .55f], 0, width, height,
            new SKRectI(4, 4, 96, 76), default, reframe: _ => proposed);

        Assert.Equal(contained, result[40 * width + 50] > 0);
        Assert.True(result[40 * width + 22] > 0);
        Assert.True(result[40 * width + 88] < 0);
    }

    [Fact]
    public void Backdrop_consensus_recovers_a_supported_extension_and_drops_an_open_run()
    {
        const int width = 100, height = 100;
        var foreground = new float[width * height];
        var backdrop = new float[width * height];
        Array.Fill(foreground, -1f);
        Array.Fill(backdrop, 1f);
        for (int y = 20; y < 77; y++)
            for (int x = 20; x < 55; x++)
            {
                foreground[y * width + x] = 1;
                backdrop[y * width + x] = -1;
            }
        for (int y = 42; y < 58; y++)
            for (int x = 55; x < 58; x++)
                backdrop[y * width + x] = -1;
        for (int y = 79; y < 83; y++)
            for (int x = 47; x < width; x++)
                backdrop[y * width + x] = -1;

        var repaired = BackdropMaskCandidates.Recover(backdrop, [.85f], width, height, foreground,
            new SKRectI(8, 10, 72, 91), _ => throw new InvalidOperationException(), default);

        Assert.NotNull(repaired);
        Assert.True(repaired[49 * width + 57] > 0);
        Assert.True(repaired[80 * width + 57] < 0);
        Assert.True(repaired[20 * width + 20] > 0);
    }

    [Fact]
    public void Recheck_edge_band_when_it_has_erased_the_interior_of_a_row_of_thin_solids()
    {
        using var outline = Pixels.NewMask(220, 68);
        using (var canvas = new SKCanvas(outline))
        using (var paint = new SKPaint { Color = SKColors.White })
            for (int x = 10; x < 200; x += 50) canvas.DrawOval(new SKRect(x, 20, x + 47, 40), paint);
        using var empty = Pixels.NewMask(220, 68);
        Assert.True(PromptSegmentation.LostInterior(outline, empty, 14, default));
        Assert.False(PromptSegmentation.LostInterior(outline, outline, 14, default));
        Assert.False(PromptSegmentation.LostInterior(outline, empty, 2, default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Selecting_an_end_face_requires_confirmation_from_the_body(bool confirmed)
    {
        const int width = 120, height = 60, size = width * height;
        var masks = Enumerable.Repeat(-1f, 3 * size).ToArray();
        for (int y = 15; y < 45; y++)
            for (int x = 12; x < 108; x++)
            {
                int j = y * width + x;
                if (x < 34) masks[j] = 1;
                masks[size + j] = masks[2 * size + j] = 1;
            }
        int result = PointMaskCandidates.Resolve(masks, [.86f, .81f, .79f], 0, width, height, new(22, 30),
            p => p.X > 34 && confirmed ? masks.AsSpan(size, size).ToArray() : null, default);
        Assert.Equal(confirmed ? 1 : 0, result);
    }

    [Fact]
    public void Scattered_dark_patches_need_a_semantic_check_before_replacing_the_box_selection()
    {
        using var image = Pixels.NewColor(120, 80);
        image.Erase(SKColors.LightGray);
        using (var canvas = new SKCanvas(image))
        using (var paint = new SKPaint { Color = SKColors.Black })
            foreach (var point in new SKPointI[] { new(5, 4), new(83, 10), new(35, 46), new(98, 58) })
                canvas.DrawRect(point.X, point.Y, 18, 16, paint);
        using var details = RepeatedDetailSelection.FromBox(image, SKRectI.Create(120, 80), default, out bool checkShape);
        Assert.NotNull(details);
        Assert.True(checkShape);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Loose_box_requires_an_independent_agreeing_prediction_to_select_low_scoring_contents(bool agrees)
    {
        const int width = 80, height = 60, size = width * height;
        var masks = Enumerable.Repeat(-1f, 2 * size).ToArray();
        var rim = new SKRectI(12, 13, 68, 48);
        var cavity = new SKRectI(15, 13, 65, 44);
        for (int y = rim.Top; y < rim.Bottom; y++)
            for (int x = rim.Left; x < rim.Right; x++)
            {
                int j = y * width + x;
                masks[size + j] = 1;
                if (!cavity.Contains(x, y)) masks[j] = 1;
            }
        var confirmation = masks.AsSpan(agrees ? size : 0, size).ToArray();
        foreach (var box in new[] { new SKRectI(2, 3, 78, 58), new SKRectI(0, 1, 76, 55) })
        {
            var result = BoxMaskCandidates.Resolve(masks, [.80f, .53f], 0, width, height, box, default,
                guidedCandidate: () => confirmation);
            Assert.Equal(agrees, result[30 * width + 40] > 0);
            Assert.True(result[46 * width + 40] > 0);
            Assert.True(result[52 * width + 40] < 0);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Box_can_include_a_supported_interior_but_rejects_an_adjoining_subject(bool spillsOutside)
    {
        const int width = 80, height = 40, size = width * height;
        var prompt = new SKRectI(5, 3, 39, 37);
        var rim = new SKRectI(6, 4, 38, 36);
        var centre = new SKRectI(9, 7, 35, 33);
        var planes = Enumerable.Repeat(-1f, size * 2).ToArray();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                if (rim.Contains(x, y) && !centre.Contains(x, y)) planes[index] = 1;
                if (rim.Contains(x, y) || (spillsOutside && x >= rim.Right && x < 66 && y >= 10 && y < 30))
                    planes[size + index] = 1;
            }
        var mask = BoxMaskCandidates.Resolve(planes, [.92f, .85f], 0, width, height, prompt, default);
        Assert.Equal(!spillsOutside, mask[20 * width + 20] > 0);
        Assert.True(mask[20 * width + 7] > 0);
        Assert.True(mask[20 * width + 50] < 0);
    }

    [Fact]
    public void Box_prefers_a_contained_subject_over_a_similarly_scored_pair_spilling_past_the_gesture()
    {
        const int width = 64, height = 40, size = width * height;
        var planes = Enumerable.Repeat(-1f, size * 2).ToArray();
        for (int y = 5; y < 34; y++)
            for (int x = 4; x < 44; x++)
            {
                planes[y * width + x] = 1;
                if (x < 31) planes[size + y * width + x] = 1;
            }
        var mask = BoxMaskCandidates.Resolve(planes, [.92f, .88f], 0, width, height, new(2, 2, 40, 37), default);
        Assert.True(mask[16 * width + 22] > 0);
        Assert.True(mask[16 * width + 37] < 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disconnected_neighbor_crossing_the_box_needs_independent_support(bool supported)
    {
        const int width = 56, height = 44, size = width * height;
        var planes = Enumerable.Repeat(-1f, 3 * size).ToArray();
        var subject = new SKRectI(6, 4, 40, 29);
        var neighbor = new SKRectI(12, 31, 24, 39);
        for (int i = 0; i < 3; i++)
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (subject.Contains(x, y) || (neighbor.Contains(x, y) && (i == 0 || supported)))
                        planes[i * size + y * width + x] = 1;
        var mask = BoxMaskCandidates.Resolve(planes, [.93f, .91f, .88f], 0, width, height, new(3, 2, 43, 35), default);
        Assert.True(mask[16 * width + 22] > 0);
        Assert.Equal(supported, mask[33 * width + 16] > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Printed_rings_on_a_shaded_plate_keep_their_holes_and_fractional_edges(bool lightPlate)
    {
        using var image = Pixels.NewColor(220, 100);
        var rgb = new PixelBuffer(image);
        double Alpha(int x, int y)
        {
            int center = x < 83 ? 46 : x < 149 ? 116 : 180;
            double radius = Math.Sqrt(Math.Pow(x - center, 2) + Math.Pow(y - 50, 2));
            return Math.Clamp(3.5 - Math.Abs(radius - 22), 0, 1);
        }
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                float tone = .09f + x * .0018f + y * .0004f;
                if (lightPlate) tone = 1 - tone;
                var plate = new PixelValue(tone, tone, tone, 1);
                var ink = x > 149 ? new PixelValue(.1f, .78f, .30f, 1) : lightPlate ? new(.06f, .06f, .06f, 1) : new(.94f, .94f, .94f, 1);
                rgb.Write(x, y, plate + (ink - plate) * (float)Alpha(x, y));
            }
        using var mask = GraphicSelection.FromBox(image, new(8, 8, 211, 94), default);
        Assert.NotNull(mask);
        for (int y = 8; y < 94; y++)
            for (int x = 8; x < 211; x++)
                Assert.InRange(Math.Abs(mask.GetPixel(x, y).Alpha - 255 * Alpha(x, y)), 0, 8);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Loose_boxes_recognize_regular_sparse_detail_but_leave_scattered_marks_to_the_model(bool bright, bool irregular)
    {
        using var image = Pixels.NewColor(320, 220);
        image.Erase(bright ? SKColors.DarkSlateGray : SKColors.LightGray);
        using var canvas = new SKCanvas(image);
        using var paint = new SKPaint { Color = bright ? SKColors.White : SKColors.Black };
        int[] centers = irregular ? [40, 103, 176, 259] : [40, 96, 152, 208, 264];
        foreach (int x in centers) canvas.DrawRoundRect(x - 6, 36, 13, 32, 5, 5, paint);
        if (!irregular)
            foreach (int x in centers) canvas.DrawRoundRect(x - 6, 102, 13, 32, 5, 5, paint);
        canvas.DrawCircle(301, 166, 4, paint);
        using var mask = RepeatedDetailSelection.FromBox(image, new(17, 10, 311, 183), default);
        if (irregular) { Assert.Null(mask); return; }
        Assert.NotNull(mask);
        Assert.Equal(255, mask.GetPixel(152, 50).Alpha);
        Assert.Equal(255, mask.GetPixel(96, 122).Alpha);
        Assert.Equal(0, mask.GetPixel(301, 166).Alpha);
        Assert.Equal(0, mask.GetPixel(113, 80).Alpha);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Confirmed_hole_requires_the_same_outer_shape(bool differentSubject)
    {
        using var joint = SelectionMask.FromRect(100, 90, new(10, 20, 70, 70));
        using var local = Pixels.NewMask(60, 50);
        local.Erase(SKColors.White);
        var bytes = local.GetPixelSpan();
        for (int y = 10; y < 20; y++)
            bytes.Slice(y * local.RowBytes + 20, 12).Clear();
        if (differentSubject)
            for (int y = 0; y < 50; y++) bytes.Slice(y * local.RowBytes, 10).Clear();
        GroupedObjectSelection.ApplyAgreedHoles(joint, local, new(10, 20, 70, 70), default);
        Assert.Equal(differentSubject ? 255 : 0, joint.GetPixel(36, 34).Alpha);
        Assert.Equal(255, joint.GetPixel(12, 25).Alpha);
        Assert.Equal(0, joint.GetPixel(8, 18).Alpha);
    }

    [Fact]
    public void A_pattern_inside_a_complete_curved_subject_does_not_replace_its_silhouette()
    {
        using var shape = Pixels.NewMask(200, 120);
        using var canvas = new SKCanvas(shape);
        using var paint = new SKPaint { Color = SKColors.White };
        canvas.DrawOval(new SKRect(3, 3, 197, 117), paint);
        using var photo = Pixels.NewColor(200, 120);
        Assert.False(PromptSegmentation.IsBoxSilhouette(shape, photo, new(0, 0, 200, 120)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_box_shaped_prediction_is_checked_against_the_photographed_edge(bool hasEdge)
    {
        var box = new SKRectI(20, 20, 140, 80);
        using var shape = SelectionMask.FromRect(160, 100, box);
        using var photo = Pixels.NewColor(160, 100);
        photo.Erase(SKColors.DarkSlateGray);
        using var canvas = new SKCanvas(photo);
        using var paint = new SKPaint { Color = SKColors.Beige };
        if (hasEdge) canvas.DrawRect(box, paint);
        Assert.Equal(!hasEdge, PromptSegmentation.IsBoxSilhouette(shape, photo, box));
    }

    [Fact]
    public void Printed_edges_use_the_background_next_to_each_stroke()
    {
        using var photo = Pixels.NewColor(90, 44);
        using var coarse = Pixels.NewMask(90, 44);
        var rgb = new PixelBuffer(photo); var mask = coarse.GetPixelSpan();
        var ink = new PixelValue(.2f, .9f, .8f, 1);
        foreach (int y in Enumerable.Range(0, 44))
            for (int x = 0; x < 90; x++)
            {
                float shade = x / 400f + .015f * (y % 3);
                var bg = new PixelValue(shade, shade, shade, 1);
                int offset = x % 24;
                bool stroke = x < 72 && y is >= 8 and < 36 && offset is >= 8 and <= 15;
                float a = !stroke ? 0 : offset == 8 ? .5f : 1;
                rgb.Write(x, y, bg + (ink - bg) * a);
                mask[y * coarse.RowBytes + x] = !stroke ? (byte)0 : offset == 8 ? (byte)192 : (byte)255;
            }
        using var refined = GraphicSelection.RefinePrintedEdges(photo, coarse, new(0, 0, 90, 44), default);
        Assert.NotNull(refined);
        foreach (int x in new[] { 8, 32, 56 }) Assert.InRange(refined.GetPixel(x, 22).Alpha, (byte)120, (byte)136);
        Assert.Equal(192, coarse.GetPixel(32, 22).Alpha);
    }

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
