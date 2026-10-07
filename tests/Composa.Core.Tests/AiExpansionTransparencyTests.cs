using Composa.AI;
using Composa.Editing;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class AiExpansionTransparencyTests
{
    [Fact]
    public void Expansion_finds_partially_transparent_space_even_without_fully_empty_pixels()
    {
        var session = EditorSession.NewCanvas(32, 32);
        var image = Pixels.NewColor(32, 32); image.Erase(new SKColor(100, 120, 140, 200));
        session.AddImageLayer("Translucent source", image, new SKPoint(16, 16), fit: false);
        using var before = session.Flatten(); var state = session.History.CurrentId;
        using var inputs = AiTaskInputPreparer.Prepare(session, new() { Task = AiTaskKind.GenerativeExpand, ExpansionMinimumSide = 0 });
        Assert.Equal((byte)200, before.GetPixel(16, 16).Alpha);
        Assert.Equal(new SKRectI(0, 0, 32, 32), inputs.MaskPlan!.Bounds);
        Assert.True(inputs.PreprocessedMask!.GetPixel(16, 16).Alpha > 0);
        Assert.Equal(state, session.History.CurrentId);
        using var unchanged = session.Flatten(); Assert.Equal(before.GetPixelSpan().ToArray(), unchanged.GetPixelSpan().ToArray());
    }
}
