using SkiaSharp;
using Composa.Rendering;

namespace Composa.AI;

/// <summary>Only explicit angles and preset key lights constrain direction. Scene inference belongs to the model.</summary>
public sealed record SceneLightHint(SKPoint Direction, bool Directional, string Instruction)
{
    public bool SoftContactOnly {get;init;}
    public static SceneLightHint Measure(SKBitmap background,SKBitmap subject,SKRectI bounds,SceneEditSettings settings,AiTaskKind task)
    {
        var angle=settings.LightAngle;
        if(angle==null && task==AiTaskKind.Relight && settings.Preset!=RelightPreset.MatchScene)
            angle=settings.Preset is RelightPreset.SoftboxRight ? 45 : settings.Preset is RelightPreset.SoftboxLeft or RelightPreset.WindowLight or RelightPreset.GoldenHour or RelightPreset.Neon ? 135 : 90;
        if(angle is { } degrees)
        {
            var radians=degrees*Math.PI/180;
            return Create(new((float)-Math.Cos(radians),(float)Math.Sin(radians)),true,$"The dominant light comes from angle {degrees:0} degrees counterclockwise from image right. ");
        }
        if(HasSmoothSupport(background,subject,bounds))
            return new(new(0,1),false,"The visible support has no strong directional shadow-edge evidence. Match broad diffuse illumination: a short, faint, very soft contact shadow immediately around the base. Keep the surrounding floor evenly lit; use the existing background objects as the ambient-light reference.") {SoftContactOnly=true};
        // A bright pixel is not a calibrated light source: reflections/clouds cannot determine ground perspective.
        return new(new(0,1),false,"Use the existing cast shadows of background objects and their illuminated faces as the lighting reference, not the subject's original studio highlights. Match the same projected shadow direction, length, density and softness, including backward shadows. Under diffuse or overcast light, use a short, faint, very soft contact shadow. A long dark directional cast belongs only in a scene with direct light and matching existing cast shadows. Begin shading directly at each ground contact.");
    }
    private static bool HasSmoothSupport(SKBitmap background,SKBitmap subject,SKRectI bounds)
    {
        // Only use a conservative smooth-ground cue for softness, never a bright pixel as a
        // source position. Strongly textured, occluded or insufficient ground stays model-led.
        var scale=Math.Min(1,256d/Math.Max(background.Width,background.Height));
        var w=Math.Max(1,(int)Math.Round(background.Width*scale));var h=Math.Max(1,(int)Math.Round(background.Height*scale));
        if(w<16||h<16)return false;
        using var small=Pixels.NewColor(w,h);using var mask=Pixels.NewMask(w,h);
        using(var draw=new SKCanvas(small))draw.DrawImage(Pixels.ImageOf(background),SKRect.Create(w,h),new SKSamplingOptions(SKFilterMode.Linear));
        using(var draw=new SKCanvas(mask))draw.DrawImage(Pixels.ImageOf(subject),SKRect.Create(w,h),new SKSamplingOptions(SKFilterMode.Linear));
        Pixels.Invalidate(small);Pixels.Invalidate(mask);
        using var smooth=Pixels.NewColor(w,h);
        using(var blur=SKImageFilter.CreateBlur(2,2))using(var paint=new SKPaint {ImageFilter=blur})using(var draw=new SKCanvas(smooth))draw.DrawImage(Pixels.ImageOf(small),0,0,paint);
        Pixels.Invalidate(smooth);
        var contrast=new List<double>();var top=Math.Max(2,(int)((bounds.Bottom-bounds.Height*.75)*scale));
        static double Luma(SKColor c)=>.2126*c.Red+.7152*c.Green+.0722*c.Blue;
        for(var y=top;y<h-2;y++)for(var x=2;x<w-2;x++)
        {
            if(mask.GetPixel(x,y).Alpha>0||mask.GetPixel(x-2,y).Alpha>0||mask.GetPixel(x+2,y).Alpha>0||mask.GetPixel(x,y-2).Alpha>0||mask.GetPixel(x,y+2).Alpha>0)continue;
            if(smooth.GetPixel(x,y).Alpha<250)continue;
            var dx=Luma(smooth.GetPixel(x+2,y))-Luma(smooth.GetPixel(x-2,y));
            var dy=Luma(smooth.GetPixel(x,y+2))-Luma(smooth.GetPixel(x,y-2));
            contrast.Add(Math.Sqrt(dx*dx+dy*dy)/Math.Max(32,Luma(smooth.GetPixel(x,y))));
        }
        if(contrast.Count<Math.Max(128,w*h/20))return false;
        contrast.Sort();
        return contrast[(int)((contrast.Count-1)*.95)]<.065&&contrast[(int)((contrast.Count-1)*.99)]<.12;
    }
    private static SceneLightHint Create(SKPoint direction,bool directional,string prefix)
    {
        var horizontal=direction.X<-.35 ? "left" : direction.X>.35 ? "right" : "";
        var vertical=direction.Y>.35 ? "bottom foreground, toward the camera" : direction.Y<-.35 ? "upper background" : "";
        return new(direction,directional,prefix+"The shadow starts directly against the object's base and extends toward the "+string.Join(" and ",new[]{vertical,horizontal}.Where(word=>word.Length>0))+" along the support surface, following its perspective. ");
    }
}
