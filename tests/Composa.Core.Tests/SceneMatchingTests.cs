using Composa.AI;
using Composa.Editing;
using Composa.Filters;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class SceneMatchingTests
{
    [Fact]
    public void Textured_background_does_not_add_grain_or_blur_to_a_flat_subject_and_undo_preserves_pixels()
    {
        var session = EditorSession.NewCanvas(80, 60);
        var background = session.ActiveLayer!.Pixels!;
        var texture = Pixels.Clone(background);
        for (var y=0;y<60;y++) for (var x=0;x<80;x++) texture.SetPixel(x,y,(x+y)%2==0 ? SKColors.DarkGray : SKColors.LightGray);
        Pixels.Invalidate(texture);
        session.AddImageLayer("Scene",texture,new(40,30),fit:false);
        var subject = Pixels.NewColor(20,20); subject.Erase(new SKColor(60,60,60));
        session.AddImageLayer("Subject",subject,new(40,30),fit:false);
        using var before = Pixels.Clone(session.Flatten());
        var history = session.History.Count;
        var layers = session.MatchActiveLayerToScene();
        Assert.All(layers,l => Assert.IsType<CurvesAdjustment>(l.Adjustment));
        using var after = Pixels.Clone(session.Flatten());
        var center = after.GetPixel(40,30); Assert.True(center.Red>60);
        for(var y=23;y<37;y++) for(var x=33;x<47;x++) Assert.Equal(center,after.GetPixel(x,y));
        Assert.Equal(before.GetPixel(0,0),after.GetPixel(0,0));
        Assert.Equal(new SKColor(60,60,60),subject.GetPixel(10,10));
        Assert.Equal(history+1,session.History.Count);
        session.Undo(); Assert.Equal(before.Bytes,session.Flatten().Bytes);
        session.Redo(); Assert.Equal(after.Bytes,session.Flatten().Bytes);
    }

    [Fact]
    public void A_saturated_object_does_not_supply_a_false_white_balance_cast()
    {
        using var subject=Pixels.NewColor(20,20); subject.Erase(new SKColor(220,25,15));
        using var scene=Pixels.NewColor(20,20); scene.Erase(new SKColor(40,150,80));
        var (light,color)=SceneMatcher.Match(SceneMatcher.Measure(subject,(_,_)=>true),SceneMatcher.Measure(scene,(_,_)=>true));
        Assert.True(color.IsIdentity);
        AssertMonotone(light);
    }

    [Fact]
    public void Neutral_cast_is_reduced_without_clipping_endpoints_or_creating_nonmonotone_curves()
    {
        using var subject=Pixels.NewColor(20,20); subject.Erase(new SKColor(150,135,120));
        using var scene=Pixels.NewColor(20,20); scene.Erase(new SKColor(135,135,135));
        var (light,color)=SceneMatcher.Match(SceneMatcher.Measure(subject,(_,_)=>true),SceneMatcher.Measure(scene,(_,_)=>true));
        Assert.True(color.Value(150,1)-color.Value(120,3)<30);
        AssertMonotone(light); AssertMonotone(color);
    }

    [Fact]
    public void Hidden_mask_pixels_do_not_bias_a_translated_subject()
    {
        static EditorSession Scene(bool hiddenBright)
        {
            var s=EditorSession.NewCanvas(80,60,new SKColor(140,140,140));
            var image=Pixels.NewColor(20,20); image.Erase(hiddenBright ? SKColors.White : SKColors.Black);
            for(var y=0;y<20;y++) for(var x=0;x<10;x++) image.SetPixel(x,y,new SKColor(70,70,70));
            Pixels.Invalidate(image); s.AddImageLayer("Subject",image,new(45,35),fit:false);
            var mask=Pixels.NewMask(20,20);
            for(var y=0;y<20;y++) for(var x=0;x<10;x++) mask.SetPixel(x,y,SKColors.White);
            Pixels.Invalidate(mask); s.ActiveLayer!.Mask=mask; return s;
        }
        var a=Scene(true); var b=Scene(false);
        a.MatchActiveLayerToScene(); b.MatchActiveLayerToScene();
        Assert.Equal(a.Flatten().Bytes,b.Flatten().Bytes);
    }

    [Fact]
    public void Selection_limits_the_adjustment_and_missing_context_creates_no_history_entry()
    {
        var s=EditorSession.NewCanvas(80,60,new SKColor(140,140,140));
        var subject=Pixels.NewColor(20,20); subject.Erase(new SKColor(60,60,60)); s.AddImageLayer("Subject",subject,new(40,30),fit:false);
        s.SelectRect(new SKRect(30,20,40,40)); using var before=Pixels.Clone(s.Flatten());
        s.MatchActiveLayerToScene(); Assert.Equal(before.GetPixel(45,30),s.Flatten().GetPixel(45,30));
        Assert.NotEqual(before.GetPixel(35,30),s.Flatten().GetPixel(35,30));
        var empty=EditorSession.NewCanvas(20,20,SKColors.White);
        Assert.Throws<InvalidOperationException>(()=>empty.MatchActiveLayerToScene()); Assert.Equal(0,empty.History.Count);
    }

    private static void AssertMonotone(CurvesAdjustment adjustment)
    {
        for(var c=0;c<4;c++)
        {
            Assert.Equal(0,adjustment.Value(0,c)); Assert.Equal(255,adjustment.Value(255,c));
            for(var x=1;x<=255;x++) Assert.InRange(adjustment.Value(x,c),adjustment.Value(x-1,c),255);
        }
    }
}
