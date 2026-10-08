using Composa.AI;
using Composa.Model;

namespace Composa.Core.Tests;

public class OriginalGenerationSizeTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(320, 240)]
    [InlineData(173, 121)]
    [InlineData(1000, 1000)]
    [InlineData(2048, 1537)]
    public void Small_inputs_reach_one_MP_and_larger_inputs_keep_their_dimensions(int width, int height)
    {
        var size = AiDimensions.OriginalGenerationSize(width, height);
        Assert.True((long)size.Width * size.Height >= DocumentLimits.MinimumGenerationPixels);
        if ((long)width * height >= DocumentLimits.MinimumGenerationPixels) Assert.Equal((width, height), size);
        else Assert.InRange(Math.Abs((double)size.Width / size.Height / ((double)width / height) - 1), 0, .003);
    }
}
