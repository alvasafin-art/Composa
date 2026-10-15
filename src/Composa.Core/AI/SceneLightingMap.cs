using SkiaSharp;

namespace Composa.AI;

/// <summary>Floating point, normalized lighting transfer. Only reliable subject interiors supply gains.</summary>
internal sealed class SceneLightingMap
{
    private readonly SKRectI area;
    private readonly int width,height;
    private readonly float[] values;
    private readonly double scale;

    internal SceneLightingMap(SceneEditInput input,SKBitmap generated,SKBitmap baseline,CancellationToken token)
    {
        var bounds=input.SubjectBounds;
        // Transfer illumination, not the model's high-frequency outlines, texture or ringing.
        var sigma=Math.Clamp(Math.Min(bounds.Width,bounds.Height)/24d,3,24);
        var margin=(int)Math.Ceiling(sigma*3)+2;
        area=SKRectI.Intersect(new(bounds.Left-margin,bounds.Top-margin,bounds.Right+margin,bounds.Bottom+margin),input.Before.Info.Rect);
        scale=Math.Min(1,384d/Math.Max(area.Width,area.Height));
        width=Math.Max(1,(int)Math.Ceiling(area.Width*scale)); height=Math.Max(1,(int)Math.Ceiling(area.Height*scale));
        var raw=new float[checked(width*height*4)];
        byte maximum=0;
        for(var y=bounds.Top;y<bounds.Bottom;y++) for(var x=bounds.Left;x<bounds.Right;x++) maximum=Math.Max(maximum,input.ObjectAlpha.GetPixel(x,y).Alpha);
        var threshold=Math.Max(32,Math.Min(224,maximum*.85));
        var guard=Math.Clamp((int)Math.Ceiling(Math.Min(bounds.Width,bounds.Height)*.12),4,24);
        for(var row=0;row<height;row++)
        {
            token.ThrowIfCancellationRequested();
            for(var col=0;col<width;col++)
            {
                var cell=(row*width+col)*4;
                for(var sy=0;sy<2;sy++) for(var sx=0;sx<2;sx++)
                {
                    var x=Math.Clamp(area.Left+(int)((col+(sx+.5)/2)/scale),0,generated.Width-1);
                    var y=Math.Clamp(area.Top+(int)((row+(sy+.5)/2)/scale),0,generated.Height-1);
                    var alpha=input.ObjectAlpha.GetPixel(x,y).Alpha;
                    if(alpha<threshold || input.SubjectMask.GetPixel(x,y).Alpha==0) continue;
                    // Codec/background mixing at a silhouette is not object illumination.
                    if(!input.WholeImage && (input.ObjectAlpha.GetPixel(Math.Max(0,x-guard),y).Alpha<threshold || input.ObjectAlpha.GetPixel(Math.Min(generated.Width-1,x+guard),y).Alpha<threshold
                        || input.ObjectAlpha.GetPixel(x,Math.Max(0,y-guard)).Alpha<threshold || input.ObjectAlpha.GetPixel(x,Math.Min(generated.Height-1,y+guard)).Alpha<threshold)) continue;
                    var source=baseline.GetPixel(x,y); var target=generated.GetPixel(x,y);
                    static float Gain(byte source,byte target)=>(float)Math.Log(Math.Clamp((Linear(target)+.01)/(Linear(source)+.01),.25,4));
                    var weight=(float)Math.Pow(alpha/255d,2);
                    raw[cell]+=Gain(source.Red,target.Red)*weight; raw[cell+1]+=Gain(source.Green,target.Green)*weight; raw[cell+2]+=Gain(source.Blue,target.Blue)*weight; raw[cell+3]+=weight;
                }
            }
        }
        values=Blur(Blur(raw,Math.Max(.8,sigma*scale),true,token),Math.Max(.8,sigma*scale),false,token);
        // Carry reliable interior illumination to thin/transparent edges. Leaving their gain at 1 produces a bright rim.
        var queue=new Queue<int>();var known=new bool[width*height];
        for(var p=0;p<known.Length;p++) if(values[p*4+3]>.05) {known[p]=true;queue.Enqueue(p);}
        while(queue.Count>0)
        {
            var p=queue.Dequeue();var x=p%width;var y=p/width;
            if((p&4095)==0) token.ThrowIfCancellationRequested();
            foreach(var (dx,dy) in new[] {(1,0),(-1,0),(0,1),(0,-1)})
            {
                var nx=x+dx;var ny=y+dy;if(nx<0||ny<0||nx>=width||ny>=height)continue;
                var next=ny*width+nx;if(known[next])continue;
                for(var c=0;c<4;c++)values[next*4+c]=values[p*4+c];
                known[next]=true;queue.Enqueue(next);
            }
        }
    }

    internal (double R,double G,double B) At(int x,int y)
    {
        if(x<area.Left || x>=area.Right || y<area.Top || y>=area.Bottom) return (1,1,1);
        var gx=(x+.5-area.Left)*scale-.5; var gy=(y+.5-area.Top)*scale-.5;
        var left=(int)Math.Floor(gx); var top=(int)Math.Floor(gy); var fx=gx-left; var fy=gy-top;
        double r=0,g=0,b=0,weight=0;
        for(var dy=0;dy<2;dy++) for(var dx=0;dx<2;dx++)
        {
            var cell=(Math.Clamp(top+dy,0,height-1)*width+Math.Clamp(left+dx,0,width-1))*4;
            var blend=(dx==0?1-fx:fx)*(dy==0?1-fy:fy);
            r+=values[cell]*blend; g+=values[cell+1]*blend; b+=values[cell+2]*blend; weight+=values[cell+3]*blend;
        }
        return weight<.001 ? (1,1,1) : (Math.Exp(r/weight),Math.Exp(g/weight),Math.Exp(b/weight));
    }

    private float[] Blur(float[] input,double sigma,bool horizontal,CancellationToken token)
    {
        var radius=(int)Math.Ceiling(3*sigma); var kernel=new float[radius*2+1]; double total=0;
        for(var k=-radius;k<=radius;k++) {kernel[k+radius]=(float)Math.Exp(-k*k/(2*sigma*sigma));total+=kernel[k+radius];}
        for(var k=0;k<kernel.Length;k++) kernel[k]/=(float)total;
        var output=new float[input.Length];
        for(var y=0;y<height;y++)
        {
            token.ThrowIfCancellationRequested();
            for(var x=0;x<width;x++) for(var k=-radius;k<=radius;k++)
            {
                var source=(Math.Clamp(y+(horizontal?0:k),0,height-1)*width+Math.Clamp(x+(horizontal?k:0),0,width-1))*4;
                var target=(y*width+x)*4; var weight=kernel[k+radius];
                for(var c=0;c<4;c++) output[target+c]+=input[source+c]*weight;
            }
        }
        return output;
    }
    private static double Linear(byte value) {var v=value/255d;return v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);}
}
