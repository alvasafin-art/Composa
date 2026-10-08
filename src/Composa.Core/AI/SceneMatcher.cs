using Composa.Filters;
using SkiaSharp;

namespace Composa.AI;

/// <summary>Robust illumination matching. Pixelwise monotone curves retain texture and never synthesize grain.</summary>
internal static class SceneMatcher
{
    internal sealed record Statistics(int Count, int NeutralCount, double Median, double Red, double Green, double Blue);

    internal static Statistics Measure(SKBitmap image, Func<int,int,bool> include)
    {
        var histogram=new int[256]; int count=0,neutral=0; double r=0,g=0,b=0;
        for(var y=0;y<image.Height;y++) for(var x=0;x<image.Width;x++)
        {
            var c=image.GetPixel(x,y);
            if(c.Alpha<128 || !include(x,y)) continue;
            var luma=(54*c.Red+183*c.Green+19*c.Blue)/256.0;
            histogram[(int)luma]++; count++;
            var maximum=Math.Max(c.Red,Math.Max(c.Green,c.Blue)); var minimum=Math.Min(c.Red,Math.Min(c.Green,c.Blue));
            if(luma<24 || luma>232 || maximum-minimum>maximum*.3) continue;
            // Normalized neutral ratios estimate illuminant without treating a colored object as a cast.
            r+=c.Red/luma; g+=c.Green/luma; b+=c.Blue/luma; neutral++;
        }
        var median=0; for(var sum=0;median<255;median++) { sum+=histogram[median]; if(sum>count/2) break; }
        return new(count,neutral,median/255.0,neutral==0?1:r/neutral,neutral==0?1:g/neutral,neutral==0?1:b/neutral);
    }

    internal static (CurvesAdjustment Light,CurvesAdjustment Color) Match(Statistics source,Statistics scene)
    {
        if(source.Count==0 || scene.Count==0) throw new InvalidOperationException("There is not enough visible surrounding image to match this layer to the scene.");
        var neutral=source.NeutralCount>=Math.Max(4,source.Count/100) && scene.NeutralCount>=Math.Max(4,scene.Count/100);
        var s=Math.Clamp(source.Median,.08,.92); var t=Math.Clamp(scene.Median,.08,.92);
        var logGain=Math.Clamp(Math.Log(t*(1-s)/(s*(1-t))),-Math.Log(2),Math.Log(2))*(neutral ? .65 : .35);
        var light=new CurvesAdjustment().WithChannel(0,Curve(Math.Exp(logGain)));
        var color=new CurvesAdjustment();
        if(neutral)
        {
            var gains=new[] { scene.Red/source.Red,scene.Green/source.Green,scene.Blue/source.Blue };
            var mean=Math.Exp(gains.Sum(g=>Math.Log(g))/3);
            for(var c=0;c<3;c++) color=color.WithChannel(c+1,Curve(Math.Exp(Math.Clamp(Math.Log(gains[c]/mean),-.18,.18)*.65)));
        }
        return (light,color);
    }

    private static IEnumerable<CurvePoint> Curve(double gain)
    {
        foreach(var x in new[] {0,16,32,64,96,128,160,192,224,240,255})
        {
            var v=x/255.0;
            yield return new(x,255*gain*v/(1+(gain-1)*v));
        }
    }
}
