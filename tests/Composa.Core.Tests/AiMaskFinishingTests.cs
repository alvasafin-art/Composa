using Composa.AI;
using Composa.Rendering;
using Composa.Selections;
using Composa.Editing;
using SkiaSharp;

namespace Composa.Core.Tests;

public class AiMaskFinishingTests
{
    [Fact]
    public void Spatial_tone_matching_corrects_opposite_edge_biases_that_a_scene_average_cannot_remove()
    {
        using var context=Pixels.NewColor(64,64); using var generated=Pixels.NewColor(64,64);
        for(var y=0;y<64;y++) for(var x=0;x<64;x++)
        {
            var original=100+x; var bias=(x-32)/2;
            context.SetPixel(x,y,new SKColor((byte)original,(byte)original,(byte)original));
            generated.SetPixel(x,y,new SKColor((byte)(original+bias),(byte)(original+bias),(byte)(original+bias)));
        }
        using var mask=SelectionMask.FromRect(64,64,new SKRect(16,16,48,48));
        using var matched=AiResultPostprocessor.MatchRemoval(generated,context,mask,19,new(0,0,64,64));
        foreach(var x in new[] {18,22,42,46}) Assert.InRange(Math.Abs(context.GetPixel(x,32).Red-matched.GetPixel(x,32).Red),0,1);
        Assert.Equal(generated.GetPixel(0,0),matched.GetPixel(0,0));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(64)]
    public void Removal_ignores_legacy_blend_and_keeps_an_opaque_core_with_finite_automatic_support(int blend)
    {
        var session=EditorSession.NewCanvas(301,189,new SKColor(200,200,200));
        session.SelectRect(new SKRect(120,70,180,118));
        var request=new AiTaskRequest { Task=AiTaskKind.RemoveObject,RemoveObject=new() { Dilation=0,Feather=0 },
            Settings=new() { Values=new() { ["maskGrow"]=4,["maskBlend"]=blend } } };
        using var inputs=AiTaskInputPreparer.Prepare(session,request);
        var mask=inputs.OutputMask!;
        Assert.Equal(255,mask.GetPixel(120,94).Alpha); Assert.Equal(255,mask.GetPixel(179,94).Alpha);
        Assert.Equal(0,mask.GetPixel(50,90).Alpha);
        var radius=inputs.MaskPlan!.SeamWidth;
        for(var y=0;y<mask.Height;y++) for(var x=0;x<mask.Width;x++)
            if(x<120-radius || x>=180+radius || y<70-radius || y>=118+radius) Assert.Equal(0,mask.GetPixel(x,y).Alpha);
    }
    [Theory]
    [InlineData(20,0.5,190)]
    [InlineData(20,1,200)]
    [InlineData(48,0.5,176)]
    [InlineData(48,1,200)]
    public void Color_matching_uses_only_generated_context_and_corrects_soft_edges_once(int bias, double strength, int expected)
    {
        using var context = Pixels.NewColor(96,96); context.Erase(new SKColor(200,200,200));
        using var generated = Pixels.Clone(context);
        using(var canvas = new SKCanvas(generated)) using(var paint = new SKPaint { Color = new SKColor((byte)(200-bias),(byte)(200-bias),(byte)(200-bias)) })
            canvas.DrawRect(new SKRect(20,20,76,76),paint);
        Pixels.Invalidate(generated);
        using var support = SelectionMask.FromRect(96,96,new SKRect(32,32,64,64));
        using var mask = AiResultPostprocessor.EditMask(support,0,8);
        using var result = AiResultPostprocessor.MatchRemoval(generated,context,mask,19,new(20,20,76,76),strength);
        Assert.Equal(expected,result.GetPixel(33,48).Red); // partial alpha is applied by the layer, not the color pass
        Assert.Equal(expected,result.GetPixel(48,48).Red);
        Assert.Equal(generated.GetPixel(20,48),result.GetPixel(20,48));
        Assert.Equal(200-bias,generated.GetPixel(48,48).Red); Assert.Equal(200,context.GetPixel(48,48).Red);
        using var composite = AiResultPostprocessor.Constrain(result,context,mask);
        var amount = mask.GetPixel(33,48).Alpha;
        Assert.InRange(amount,1,254);
        Assert.Equal((200*(255-amount)+expected*amount+127)/255,composite.GetPixel(33,48).Red);
    }

    [Fact]
    public void Color_matching_preserves_texture_amplitude_alpha_and_does_not_create_noise()
    {
        using var context = Pixels.NewColor(64,64); using var generated = Pixels.NewColor(64,64);
        for(var y=0;y<64;y++) for(var x=0;x<64;x++)
        {
            context.SetPixel(x,y,new SKColor((byte)(200+(x%2==0?5:-5)),200,200));
            generated.SetPixel(x,y,new SKColor((byte)(180+(x%2==0?2:-2)),180,180));
        }
        using var mask = SelectionMask.FromRect(64,64,new SKRect(24,24,40,40));
        using var matched = AiResultPostprocessor.MatchRemoval(generated,context,mask,19);
        Assert.Equal(4,matched.GetPixel(30,30).Red-matched.GetPixel(31,30).Red);
        Assert.Equal(200,matched.GetPixel(30,30).Green);
        Assert.Equal(matched.GetPixel(30,30).Green,matched.GetPixel(31,30).Green);
        Assert.Equal(generated.GetPixel(30,30).Alpha,matched.GetPixel(30,30).Alpha);
    }
}
