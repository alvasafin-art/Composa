using SkiaSharp;

namespace Composa.AI;

/// <summary>Only explicit angles and preset key lights constrain direction. Scene inference belongs to the model.</summary>
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
        // A bright pixel is not a calibrated light source: reflections/clouds cannot determine ground perspective.
        return new(new(0,1),false,"Read the key-light side from the existing background objects, their illuminated faces and their cast shadows. Use that same light and ground-shadow direction for the foreground subject.");
    }
    private static SceneLightHint Create(SKPoint direction,bool directional,string prefix)
    {
        var horizontal=direction.X<-.35 ? "left" : direction.X>.35 ? "right" : "";
        var vertical=direction.Y>.35 ? "bottom foreground, toward the camera" : direction.Y<-.35 ? "upper background" : "";
        return new(direction,directional,prefix+"The shadow starts directly against the object's base and extends toward the "+string.Join(" and ",new[]{vertical,horizontal}.Where(word=>word.Length>0))+" along the support surface, following its perspective. ");
    }
}
