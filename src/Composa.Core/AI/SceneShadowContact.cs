using Composa.Rendering;
using SkiaSharp;

namespace Composa.AI;

/// <summary>Registers each significant generated shadow component to the original support contour.</summary>
internal static class SceneShadowContact
{
    private sealed record Component(List<int> Pixels,long Mass,byte Peak);
    internal static SKBitmap Anchor(SKBitmap shadow,SceneEditInput input,double modelToDocument,CancellationToken token,out int count,out SKPointI correction)
    {
        correction=default; count=0;
        var w=shadow.Width;var h=shadow.Height;var b=input.SupportBounds;
        var radius=Math.Clamp((int)Math.Ceiling(Math.Min(b.Height*.2,48*modelToDocument)),2,96);
        byte peak=0;foreach(var value in shadow.GetPixelSpan()) peak=Math.Max(peak,value);
        var threshold=Math.Max(1,Math.Min(8,peak/2));
        var labels=new int[checked(w*h)];var components=new List<Component>();
        var queue=new Queue<int>();
        for(var y=0;y<h;y++)
        {
            token.ThrowIfCancellationRequested();
            for(var x=0;x<w;x++)
            {
                var start=y*w+x;if(labels[start]!=0||shadow.GetPixel(x,y).Alpha<threshold)continue;
                var id=components.Count+1;var pixels=new List<int>();long mass=0;byte maximum=0;
                labels[start]=id;queue.Enqueue(start);
                while(queue.Count>0)
                {
                    var p=queue.Dequeue();pixels.Add(p);var px=p%w;var py=p/w;var value=shadow.GetPixel(px,py).Alpha;mass+=value;maximum=Math.Max(maximum,value);
                    if((pixels.Count&4095)==0) token.ThrowIfCancellationRequested();
                    for(var dy=-1;dy<=1;dy++)for(var dx=-1;dx<=1;dx++)
                    {
                        var nx=px+dx;var ny=py+dy;if(nx<0||ny<0||nx>=w||ny>=h)continue;
                        var next=ny*w+nx;if(labels[next]==0&&shadow.GetPixel(nx,ny).Alpha>=threshold){labels[next]=id;queue.Enqueue(next);}
                    }
                }
                components.Add(new(pixels,mass,maximum));
            }
        }
        var shifts=new SKPointI?[components.Count];long largest=0;
        for(var index=0;index<components.Count;index++)
        {
            var component=components[index];if(component.Pixels.Count<9)continue;
            long contactMass=0;
            foreach(var p in component.Pixels)
            {
                var x=p%w;var y=p/w;if(x<b.Left||x>=b.Right)continue;
                var foot=input.SupportY[x];var alpha=shadow.GetPixel(x,y).Alpha;
                if(foot>=b.Bottom-Math.Max(8,b.Height/8)&&y>foot&&y<=foot+2&&alpha>=Math.Max(threshold,component.Peak*.4))contactMass+=alpha;
            }
            if(contactMass>=Math.Max(threshold*3,component.Mass*.005)) {shifts[index]=default(SKPointI);continue;}
            var best=double.PositiveInfinity;var offset=default(SKPointI);
            foreach(var p in component.Pixels)
            {
                var x=p%w;var y=p/w;
                // A faint/contact sliver must not certify a detached dominant cast lobe.
                if(shadow.GetPixel(x,y).Alpha<Math.Max(threshold,component.Peak*.4)||input.ObjectAlpha.GetPixel(x,y).Alpha>8)continue;
                if(y<b.Bottom-radius-Math.Max(8,b.Height/8)||y>b.Bottom+radius)continue;
                for(var sx=Math.Max(b.Left,x-radius);sx<Math.Min(b.Right,x+radius+1);sx++)
                {
                    var foot=input.SupportY[sx];if(foot<b.Bottom-Math.Max(8,b.Height/8))continue;
                    var distance=Math.Sqrt((x-sx)*(x-sx)+(y-foot)*(y-foot));
                    if(distance<best){best=distance;offset=new(sx-x,foot+1-y);}
                }
            }
            if(best>radius)continue; // Unrelated or strongly displaced model darkening is rejected.
            shifts[index]=best>2?offset:default(SKPointI);
            if(component.Mass>largest){largest=component.Mass;correction=shifts[index]!.Value;}
        }
        var result=Pixels.NewColor(w,h);
        try
        {
            for(var y=0;y<h;y++)
            {
                token.ThrowIfCancellationRequested();
                for(var x=0;x<w;x++)
                {
                    var alpha=shadow.GetPixel(x,y).Alpha;if(alpha==0)continue;
                    var label=labels[y*w+x];
                    // Assign only a small soft skirt to a significant core; weak noise cannot bridge detached components.
                    if(label==0)
                    {
                        var nearest=double.PositiveInfinity;
                        for(var dy=-3;dy<=3;dy++)for(var dx=-3;dx<=3;dx++)
                        {var nx=x+dx;var ny=y+dy;if(nx<0||ny<0||nx>=w||ny>=h)continue;var candidate=labels[ny*w+nx];var distance=dx*dx+dy*dy;if(candidate>0&&distance<nearest){nearest=distance;label=candidate;}}
                    }
                    if(label==0||shifts[label-1] is not { } shift)continue;
                    var tx=x+shift.X;var ty=y+shift.Y;if(tx<0||ty<0||tx>=w||ty>=h)continue;
                    var coverage=Math.Min(alpha,input.ShadowMask.GetPixel(tx,ty).Alpha);
                    if(coverage>result.GetPixel(tx,ty).Alpha)result.SetPixel(tx,ty,new(0,0,0,coverage));
                }
            }
            for(var y=0;y<h;y++)for(var x=0;x<w;x++)if(result.GetPixel(x,y).Alpha>0)count++;
            Pixels.Invalidate(result);return result;
        }
        catch{result.Dispose();throw;}
    }
}
