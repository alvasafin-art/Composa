using System.Numerics;
using SkiaSharp;

namespace Composa.AI;

/// <summary>Fits smooth lighting from paired linear RGB samples of nearby source materials.</summary>
internal sealed class SceneMaterialLighting(int width, int height, SKRectI area, double scale, double sigma, SKBitmap foreground)
{
    private static readonly Vector3 Luminance = new(.2126f,.7152f,.0722f);
    private readonly Sample[] samples = new Sample[checked(width*height)];

    internal void Add(int col, int row, Vector3 source, Vector3 target, float weight, float chromaConfidence = 1)
    {
        ref var sample = ref samples[row*width+col];
        var sourceLight = Vector3.Dot(source,Luminance);
        var targetLight = Vector3.Dot(target,Luminance);
        sample.Exposure += LogGain((targetLight+.01f)/(sourceLight+.01f))*weight;
        sample.Weight += weight;
        if (sourceLight <= 0 || targetLight <= 0) return;

        // A neutral exposure change should leave the color fit at identity even
        // when the amount of exposure varies between samples in the same window.
        var chromaWeight = weight * chromaConfidence * Math.Min(1,Math.Min(sourceLight,targetLight)/.02f);
        var sourceChroma = source/sourceLight;
        var targetChroma = target/targetLight;
        sample.Material += sourceChroma*chromaWeight;
        sample.Cross += sourceChroma*targetChroma*chromaWeight;
        sample.Energy += sourceChroma*sourceChroma*chromaWeight;
    }

    internal float[] Fit(CancellationToken token)
    {
        var result = new float[checked(samples.Length*4)];
        var offsets = Kernel(sigma);
        foreach (ref var sample in samples.AsSpan())
            sample.Material = Chromaticity(sample.Material);
        for(var row=0;row<height;row++)
        {
            token.ThrowIfCancellationRequested();
            for(var col=0;col<width;col++)
            {
                var x = Math.Clamp(area.Left+(int)((col+.5)/scale),0,foreground.Width-1);
                var y = Math.Clamp(area.Top+(int)((row+.5)/scale),0,foreground.Height-1);
                var color = foreground.GetPixel(x,y);
                if(color.Alpha==0) continue;
                var source = SceneLightingMap.LinearRgb(color);
                var material = GuideMaterial(x,y,source);
                var cross = Vector3.Zero; var energy = Vector3.Zero;
                float exposure=0, totalWeight=0;
                foreach (var offset in offsets)
                {
                    var sx = col+offset.X; var sy = row+offset.Y;
                    if((uint)sx>=(uint)width || (uint)sy>=(uint)height) continue;
                    ref readonly var sample = ref samples[sy*width+sx];
                    if(sample.Weight<=0) continue;
                    exposure += sample.Exposure*offset.Weight;
                    totalWeight += sample.Weight*offset.Weight;
                    // Fit the plate from ceramic samples, for example, even where a warm
                    // layer of frosting is closer than the next visible part of the plate.
                    var distance = Vector3.DistanceSquared(material,sample.Material);
                    var weight = offset.Weight * MathF.Exp(-distance/(2*.065f*.065f));
                    cross += sample.Cross*weight;
                    energy += sample.Energy*weight;

                }
                if(totalWeight<=0) continue;
                // In a brightness estimate, a pale label or metal strip must not
                // outweigh a much larger expanse of dark material next to it.
                var brightness = MathF.Exp(exposure/totalWeight);
                // Signal energy determines how much a channel can change the color fit.
                // For weak channels it tends towards the neutral chromatic gain of 1.
                var priorEnergy = Math.Max(Vector3.Dot(energy,Luminance)*.02f,totalWeight*1e-6f);
                var prior = new Vector3(priorEnergy);
                var gains = (cross+prior)/(energy+prior);

                // Use the smooth brightness field for exposure. Selecting similar materials
                // for the color fit must not turn the source texture into an exposure pattern.
                var lit = Vector3.Dot(source*gains,Luminance);
                if(lit>0) gains *= brightness*Vector3.Dot(source,Luminance)/lit;
                var cell = (row*width+col)*4;
                result[cell] = LogGain(gains.X);
                result[cell+1] = LogGain(gains.Y);
                result[cell+2] = LogGain(gains.Z);
                result[cell+3] = 1;
            }
        }
        return result;
    }

    internal static float ChromaStability(SKBitmap frame,int x,int y,int radius,Vector3 center)
    {
        // Spatially unstable color near a material boundary is a weak match when
        // the model has redrawn the boundary by a few pixels.
        var chroma = Chromaticity(center);
        var left = Difference(Math.Max(0,x-radius),y);
        var right = Difference(Math.Min(frame.Width-1,x+radius),y);
        var top = Difference(x,Math.Max(0,y-radius));
        var bottom = Difference(x,Math.Min(frame.Height-1,y+radius));
        const float stableVariance = .0025f;
        return stableVariance/(stableVariance+Math.Max(Math.Max(left,right),Math.Max(top,bottom)));

        float Difference(int sx,int sy) =>
            Vector3.DistanceSquared(chroma,Chromaticity(SceneLightingMap.LinearRgb(frame.GetPixel(sx,sy))));
    }

    private Vector3 GuideMaterial(int x, int y, Vector3 source)
    {
        var chroma = Chromaticity(source);
        var sum = chroma;
        // Sample color over a model-map pixel so a single source grain or
        // icing speck cannot switch the material used for a lighting sample.
        var distance = Math.Max(1,(int)Math.Round(1/scale));
        for (var dy=-1; dy<=1; dy++)
            for (var dx=-1; dx<=1; dx++)
            {
                if (dx==0 && dy==0) continue;
                var sx=Math.Clamp(x+dx*distance,0,foreground.Width-1);
                var sy=Math.Clamp(y+dy*distance,0,foreground.Height-1);
                var neighbor=foreground.GetPixel(sx,sy);
                if (neighbor.Alpha<224) continue;
                var other=Chromaticity(SceneLightingMap.LinearRgb(neighbor));
                var weight=MathF.Exp(-Vector3.DistanceSquared(chroma,other)/(2*.10f*.10f));
                sum+=other*weight;
            }
        return Chromaticity(sum);
    }

    private static float LogGain(float gain) => MathF.Log(Math.Clamp(gain,.25f,4));

    private static Vector3 Chromaticity(Vector3 color) =>
        color / Math.Max(.000001f,color.X+color.Y+color.Z);

    private readonly record struct Offset(int X,int Y,float Weight);

    private static Offset[] Kernel(double sigma)
    {
        var radius = (int)Math.Ceiling(3*sigma);
        var result = new List<Offset>();
        for(var y=-radius;y<=radius;y++)
            for(var x=-radius;x<=radius;x++)
                if(x*x+y*y<=radius*radius)
                    result.Add(new(x,y,(float)Math.Exp(-(x*x+y*y)/(2*sigma*sigma))));
        return result.ToArray();
    }

    private struct Sample
    {
        internal Vector3 Material, Cross, Energy;
        internal float Exposure, Weight;
    }
}