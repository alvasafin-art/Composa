using SkiaSharp;

namespace Composa.AI;

/// <summary>A conservative image-space light cue. Broad ambient light never supplies a forced shadow direction.</summary>
public sealed record SceneLightHint(SKPoint Direction, bool Directional, string Instruction)
{
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
        double maximum=0; var step=Math.Max(1,Math.Max(background.Width,background.Height)/256); var samples=new List<(int X,int Y,double L)>();
        for(var y=0;y<background.Height;y+=step) for(var x=0;x<background.Width;x+=step)
        {
            if(subject.GetPixel(x,y).Alpha>0) continue;
            var color=background.GetPixel(x,y); if(color.Alpha<250) continue;
            var l=(.2126*color.Red+.7152*color.Green+.0722*color.Blue)/255;
            maximum=Math.Max(maximum,l); samples.Add((x,y,l));
        }
        var threshold=Math.Max(.72,maximum*.9); var highlights=samples.Where(sample=>sample.L>=threshold).ToArray();
        if(highlights.Length<3 || highlights.Length>samples.Count*.25)
            return new(new(0,1),false,"Use the scene's diffuse ambient light. Keep any contact shadow soft and subtle. ");
        var sx=highlights.Average(point=>point.X); var sy=highlights.Average(point=>point.Y);
        var dx=(bounds.Left+bounds.Right)/2d-sx; var dy=bounds.Bottom-sy; var length=Math.Sqrt(dx*dx+dy*dy);
        if(length<Math.Max(8,bounds.Height/5)) return new(new(0,1),false,"Match the surrounding ambient illumination. ");
        return Create(new((float)(dx/length),(float)(dy/length)),true,
            $"The background's brightest light is near {sx/background.Width:0.00} of image width and {sy/background.Height:0.00} of image height. Match that visible source, not the object's original illumination. ");
    }
    private static SceneLightHint Create(SKPoint direction,bool directional,string prefix)
    {
        var horizontal=direction.X<-.35 ? "left" : direction.X>.35 ? "right" : "";
        var vertical=direction.Y>.35 ? "bottom foreground, toward the camera" : direction.Y<-.35 ? "upper background" : "";
        return new(direction,directional,prefix+"The cast shadow should start at the feet/base and extend toward the "+string.Join(" and ",new[]{vertical,horizontal}.Where(word=>word.Length>0))+" on the existing ground surface. It must not become a solid horizontal black stripe. ");
    }
}
