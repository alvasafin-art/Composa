using SkiaSharp;

namespace Composa.AI;

/// <summary>Discounts global model color/contrast changes without classifying them as cast shadows.</summary>
internal sealed class SceneBackgroundCalibration
{
    private readonly (double Gain,double Offset)[] channels;
    internal SceneBackgroundCalibration(SceneEditInput input,SKBitmap generated,SKBitmap baseline,CancellationToken token)
    {
        var samples=new List<(double[] Source,double[] Target)>();
        var step=Math.Clamp(((int)Math.Sqrt(generated.Width*(double)generated.Height/1024)+7)/8*8,8,32);
        for(var top=0;top<generated.Height;top+=step)for(var left=0;left<generated.Width;left+=step)
        {
            token.ThrowIfCancellationRequested();var sourceSum=new double[3];var targetSum=new double[3];var valid=0;
            for(var y=top;y<Math.Min(generated.Height,top+step);y++)for(var x=left;x<Math.Min(generated.Width,left+step);x++)
            {
                if(input.ObjectAlpha.GetPixel(x,y).Alpha>0)continue;
                var s=baseline.GetPixel(x,y);var t=generated.GetPixel(x,y);if(s.Alpha<250||t.Alpha<250)continue;
                var sr=Linear(s.Red);var sg=Linear(s.Green);var sb=Linear(s.Blue);
                var l=.2126*sr+.7152*sg+.0722*sb;if(l<.03||l>.85)continue;
                sourceSum[0]+=sr;sourceSum[1]+=sg;sourceSum[2]+=sb;
                targetSum[0]+=Linear(t.Red);targetSum[1]+=Linear(t.Green);targetSum[2]+=Linear(t.Blue);valid++;
            }
            if(valid<step*step/2)continue;
            for(var c=0;c<3;c++){sourceSum[c]/=valid;targetSum[c]/=valid;}
            samples.Add((sourceSum,targetSum));
        }
        channels=Enumerable.Range(0,3).Select(c=>Fit(samples.Select(s=>(X:s.Source[c],Y:s.Target[c])).ToArray())).ToArray();
    }
    internal double Correct(byte value,int channel)
    {var (gain,offset)=channels[channel];return Math.Clamp((Linear(value)-offset)/gain,0,1);}
    private static (double Gain,double Offset) Fit((double X,double Y)[] samples)
    {
        if(samples.Length<4)return (1,0);
        static double Median(IEnumerable<double> values){var sorted=values.Order().ToArray();return sorted.Length==0?0:sorted[sorted.Length/2];}
        var ratios=samples.Where(s=>s.X>.005).Select(s=>s.Y/s.X).ToArray();if(ratios.Length<4)return (1,0);
        var gain=Math.Clamp(Median(ratios),.5,2);double offset=0;
        for(var iteration=0;iteration<5;iteration++)
        {
            var sigma=Math.Max(.005,Median(samples.Select(s=>Math.Abs(s.Y-gain*s.X-offset)))*1.4826);
            double total=0,x=0,y=0,xx=0,xy=0;
            foreach(var s in samples)
            {
                var residual=(s.Y-gain*s.X-offset)/(2.5*sigma);var weight=1/(1+residual*residual);
                total+=weight;x+=weight*s.X;y+=weight*s.Y;xx+=weight*s.X*s.X;xy+=weight*s.X*s.Y;
            }
            var variance=xx-x*x/total;if(variance/total<.00001)break;
            gain=Math.Clamp((xy-x*y/total)/variance,.5,2);offset=Math.Clamp((y-gain*x)/total,-.12,.12);
        }
        return (gain,offset);
    }
    private static double Linear(byte value){var v=value/255d;return v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);}
}
