using Composa.Editing;
using Composa.Filters;
using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Core.Tests;

/// <summary>The session's Select Subject, Object Selection and Remove Background through the models, on a scene U²-Net finds every time.</summary>
public class SubjectSessionTests
{
    /// <summary>A 320×240 canvas with a dark noisy photo layer carrying a red disc at (160, 100), radius 60.</summary>
    private static EditorSession Scene(out SKBitmap photo)
    {
        var session = EditorSession.NewCanvas(320, 240, SKColors.Transparent);
        photo = Pixels.NewColor(320, 240);
        var random = new Random(3);
        for (var y = 0; y < 240; y++)
        for (var x = 0; x < 320; x++)
        {
            var inside = (x - 160) * (x - 160) + (y - 100) * (y - 100) < 60 * 60;
            var n = (byte)random.Next(0, 24);
            photo.SetPixel(x, y, inside ? new SKColor(225, 40, 30) : new SKColor((byte)(35 + n), (byte)(40 + n), (byte)(45 + n)));
        }
        session.AddImageLayer("photo", photo, new SKPoint(160, 120)); // Centred on the canvas, so the layer covers it.
        return session;
    }

    [Fact]
    public async Task Select_subject_with_the_model_selects_the_disc_and_the_matte_is_reused()
    {
        var session = Scene(out _);
        session.Detect = SubjectDetect.Any;
        Assert.True(await session.SelectSubjectAsync());
        Assert.Equal("Select Subject", session.History.UndoName);
        Assert.True(session.Selection!.GetPixel(160, 100).Alpha > 200);
        Assert.True(session.Selection.GetPixel(10, 10).Alpha < 40, $"corner {session.Selection.GetPixel(10, 10).Alpha}");
        var bounds = SelectionMask.Bounds(session.Selection, 128);
        Assert.InRange(bounds.Left, 85, 115);
        Assert.InRange(bounds.Right, 205, 235);

        // The same picture again costs no model run: the cached matte answers, and an object click over the whole picture shares it.
        var first = await session.FindSubjectAsync(wholePicture: true);
        Assert.Same(first, await session.FindSubjectAsync(wholePicture: true));
        session.SampleAllLayers = true;
        Assert.Same(first, await session.FindSubjectAsync());
        session.Deselect();
        await session.SelectObjectAsync(160, 100);
        Assert.Equal("Object Selection", session.History.UndoName);
        Assert.True(session.Selection!.GetPixel(160, 100).Alpha > 200);
        Assert.True(session.Selection.GetPixel(10, 10).Alpha == 0);
        await session.SelectObjectAsync(10, 10); // The backdrop: nothing to select.
        Assert.Null(session.Selection);
        session.ObjectEdgeOffset = -4;
        await session.SelectObjectAsync(160, 100);
        Assert.True(session.Selection!.GetPixel(160, 100).Alpha > 200);
    }

    [Fact]
    public async Task A_document_that_changes_while_the_model_runs_throws_the_result_away()
    {
        var session = Scene(out _);
        session.Detect = SubjectDetect.Any;
        var running = session.SelectSubjectAsync();
        session.AddImageLayer("late", Pixels.NewColor(4, 4), new SKPoint(0, 0));
        Assert.False(await running);
        Assert.Null(session.Selection);
        Assert.Equal("Add Image", session.History.UndoName);
    }

    [Fact]
    public async Task A_cancelled_selection_commits_nothing()
    {
        var session = Scene(out _);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SelectSubjectAsync(SelectionMode.Replace, cancelled.Token));
        Assert.Null(session.Selection);
        Assert.False(session.CanUndo && session.History.UndoName == "Select Subject");
    }

    [Theory]
    [InlineData(SelectionMode.Replace)]
    [InlineData(SelectionMode.Add)]
    [InlineData(SelectionMode.Subtract)]
    public async Task A_box_only_selects_its_subject_and_combines_as_one_undo_step(SelectionMode mode)
    {
        var session = Scene(out _);
        session.SelectRect(new SKRect(0, 0, 200, 240));
        var previous = session.Selection;
        var history = session.History.Count;
        await session.SelectObjectInBoxAsync(new SKRectI(60, 20, 260, 200), mode);
        Assert.Equal(mode == SelectionMode.Replace ? 0 : 255, session.Selection!.GetPixel(10, 10).Alpha);
        if (mode == SelectionMode.Subtract) Assert.True(session.Selection.GetPixel(160, 100).Alpha < 30);
        else Assert.True(session.Selection.GetPixel(160, 100).Alpha > 200);
        Assert.Equal(0, session.Selection.GetPixel(300, 100).Alpha);
        Assert.Equal(history + 1, session.History.Count);
        session.Undo(); Assert.Same(previous, session.Selection);
        session.Redo(); Assert.Equal(history + 1, session.History.Count);
    }

    [Fact]
    public async Task Cancelled_and_outside_boxes_leave_the_old_selection_intact()
    {
        var session = Scene(out _);
        session.SelectRect(new SKRect(0, 0, 20, 20));
        var previous = session.Selection;
        var history = session.History.Count;
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SelectObjectInBoxAsync(new SKRectI(60, 20, 260, 200), cancellation: cancel.Token));
        await session.SelectObjectInBoxAsync(new SKRectI(-20, -20, -1, -1));
        Assert.Same(previous, session.Selection); Assert.Equal(history, session.History.Count);
    }

    [Fact]
    public async Task A_layer_matte_follows_canvas_size_and_a_cached_result_still_honours_cancel()
    {
        var session = Scene(out _);
        var first = await session.FindSubjectAsync(); Assert.NotNull(first);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.FindSubjectAsync(cancellation: cancel.Token));
        session.ResizeCanvas(400, 300, Anchor.TopLeft);
        var resized = await session.FindSubjectAsync(); Assert.NotNull(resized);
        Assert.NotSame(first, resized); Assert.Equal((400, 300), (resized!.Width, resized.Height));
    }
}
