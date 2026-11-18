using Composa.AI;
using Composa.Rendering;
using Composa.Selections;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.AI;

/// <summary>Register the new support to the immutable PNG, using a small translation rather than a scene homography.</summary>
internal sealed class QwenBackgroundFrame : IDisposable
{
    internal SKBitmap Image { get; }
    internal SKBitmap Subject { get; }
    internal SKPointI Offset { get; }

    internal QwenBackgroundFrame(SceneEditInput scene, SKBitmap layout, SKBitmap subject)
    {
        var bounds = SelectionMask.Bounds(subject, 128);
        if (bounds.IsEmpty) throw new InvalidDataException("Qwen background layout has no reliable foreground support.");
        // SAM may include a neighbouring vertical prop. Its total bounding-box center/height
        // therefore cannot establish the subject's scale or horizontal placement.
        const int dx = 0;
        var dy = scene.SubjectBounds.Bottom - bounds.Bottom;
        if (Math.Abs(dy) > Math.Max(16, scene.SubjectBounds.Height / 4))
            throw new InvalidDataException($"Qwen moved or resized the subject too far while arranging the background ({bounds}; expected {scene.SubjectBounds}). No edit was applied; retry with clearer placement guidance.");
        Offset = new(dx, dy);
        Image = Pixels.NewColor(layout.Width, layout.Height);
        try
        {
            Subject = Pixels.NewMask(subject.Width, subject.Height);
            using (var draw = new SKCanvas(Image))
            using (var shader = Pixels.ImageOf(layout).ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                new SKSamplingOptions(SKFilterMode.Nearest), SKMatrix.CreateTranslation(dx, dy)))
            using (var paint = new SKPaint { Shader = shader }) draw.DrawRect(SKRect.Create(Image.Width, Image.Height), paint);
            using (var draw = new SKCanvas(Subject)) draw.DrawImage(Pixels.ImageOf(subject), dx, dy);
            Pixels.Invalidate(Image); Pixels.Invalidate(Subject);
        }
        catch { Image.Dispose(); Subject?.Dispose(); throw; }
    }
    public void Dispose() { Image.Dispose(); Subject.Dispose(); }

    internal SceneEditResult RestoreShadow(SceneEditInput original,SKBitmap clean,AiTaskRequest request,CancellationToken token)
    {
        // Cleanup can remove the object's cast along with the object. Recover it from the
        // arranged scene against the clean plate, using the same source-alpha/contact solver.
        var document=new Document(clean.Width,clean.Height);
        document.Layers.Add(Layer.Raster("Clean plate",clean));
        var source=Layer.Raster("Original subject",original.ModelForeground);document.Layers.Add(source);document.SetActive(source.Id);
        var extraction=request with {Task=AiTaskKind.Harmonize,ReferenceImages=[],ReferenceImage=null};
        using var scene=SceneEditInput.Prepare(document,extraction,token);
        using var evidence=Pixels.Clone(Image);
        for(var y=0;y<Image.Height;y++)
        {
            token.ThrowIfCancellationRequested();
            for(var x=0;x<Image.Width;x++)
                if(Subject.GetPixel(x,y).Alpha>8 && original.ObjectAlpha.GetPixel(x,y).Alpha==0)
                    evidence.SetPixel(x,y,clean.GetPixel(x,y));
        }
        Pixels.Invalidate(evidence);
        var measured=SceneEditResult.Extract(scene,evidence,extraction,token);
        try
        {
            var result=SceneEditResult.Extract(original,clean,request,token);
            var shadow=measured.Layers.FirstOrDefault(layer=>layer.Tags?.Contains("scene-shadow")==true);
            foreach(var layer in measured.Layers) if(!ReferenceEquals(layer,shadow)) {layer.Pixels.Dispose();layer.Mask?.Dispose();}
            return shadow==null ? result : new([result.Layers[0],shadow,result.Layers[1]],measured.ShadowPixels,measured.ContactCorrectionX,measured.ContactCorrectionY);
        }
        catch {foreach(var layer in measured.Layers) {layer.Pixels.Dispose();layer.Mask?.Dispose();}throw;}
    }
}
