using Composa.Filters;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Vision;

/// <summary>How Select Subject, the Object Selection tool and Remove Background tell the subject from the backdrop.</summary>
public enum SubjectDetect
{
    /// <summary>U²-Net lite: any subject in an ordinary photo.</summary>
    Any,
    /// <summary>MODNet: a person, with hair and soft edges.</summary>
    Person,
    /// <summary>No model: the backdrop is what touches the picture's edges in a near-uniform color.</summary>
    Backdrop
}

/// <summary>
/// Turns a legacy Detect choice into a matte. Missing legacy weights use bundled SAM Quality;
/// the plain backdrop remains available when no installed model can run.
/// </summary>
public static class SubjectFinder
{
    public static SubjectModel? ModelFor(SubjectDetect detect) => detect switch
    {
        SubjectDetect.Any => SubjectModels.U2NetP,
        SubjectDetect.Person => SubjectModels.ModNet,
        _ => null
    };

    public static string DisplayName(SubjectDetect detect) => detect switch
    {
        SubjectDetect.Any => "Any subject",
        SubjectDetect.Person => "Person",
        _ => "Plain backdrop"
    };

    /// <summary>Whether the choice can be honoured here. The backdrop method always can.</summary>
    private static bool QualityAvailable => ModelRunner.CanRun(PromptModels.EfficientSamS.Encoder) && ModelRunner.CanRun(PromptModels.EfficientSamS.Decoder);
    public static bool IsAvailable(SubjectDetect detect) => ModelFor(detect) is not { } model || ModelRunner.CanRun(model) || QualityAvailable;

    /// <summary>The choice that will actually run: the one asked for, or the backdrop when its model is not available.</summary>
    public static SubjectDetect Resolve(SubjectDetect detect) => IsAvailable(detect) ? detect : SubjectDetect.Backdrop;

    /// <summary>Why <see cref="Resolve"/> fell back, for the status line, or null when it did not.</summary>
    public static string? FallbackReason(SubjectDetect detect)
    {
        if (ModelFor(detect) is not { } model || IsAvailable(detect)) return null;
        return !ModelRunner.IsAvailable
            ? "The subject detection runtime did not load on this machine, so the plain backdrop was used instead."
            : $"The {model.Name} model is not installed ({model.Path}), so the plain backdrop was used instead.";
    }

    /// <summary>
    /// The subject of <paramref name="source"/> as a soft <c>Alpha8</c> matte at the source's size, or null when
    /// nothing stands out. Transparent pixels are backdrop whatever their color. Runs the model when the choice
    /// names one that is available; safe off the UI thread on a committed bitmap.
    /// </summary>
    public static SKBitmap? Matte(SKBitmap source, SubjectDetect detect, CancellationToken cancellation = default)
    {
        if (ModelFor(Resolve(detect)) is { } model)
        {
            var matte = ModelRunner.CanRun(model) ? SubjectMatting.Matte(source, model, cancellation)
                : QualityMatte(source, cancellation);
            if (SelectionMask.Bounds(matte, 128).IsEmpty) { matte.Dispose(); return null; }
            return matte;
        }
        return ObjectSelection.Subject(source);
    }

    private static SKBitmap QualityMatte(SKBitmap source, CancellationToken cancellation)
    {
        using var hint = ObjectSelection.Subject(source);
        SKRectI? bounds = hint == null ? null : SelectionMask.Bounds(hint, 128);
        using var decoded = PromptSegmentation.Decode(PromptSegmentation.Encode(source, PromptModels.EfficientSamS, cancellation),
            PromptModels.EfficientSamS, source.Width, source.Height, new SKPoint(source.Width / 2f, source.Height / 2f), bounds, cancellation);
        var matte = PromptSegmentation.Refine(decoded, source, cancellation);
        try
        {
            for (var y = 0; y < source.Height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (var x = 0; x < source.Width; x++)
                    if (source.GetPixel(x,y).Alpha < 255)
                        matte.SetPixel(x,y,new SKColor(0,0,0,(byte)(matte.GetPixel(x,y).Alpha * source.GetPixel(x,y).Alpha / 255)));
            }
            Composa.Rendering.Pixels.Invalidate(matte); return matte;
        }
        catch { matte.Dispose(); throw; }
    }
}
