using Composa.AI;
using Composa.App.AI;
using SkiaSharp;

namespace Composa.App.Dialogs;

internal static class SelectionOutput
{
    internal static string Describe(SKRectI canvas, SKRectI selection, EngineProfile? engine, bool original,
        int width, int height, string quality)
    {
        var bounds = AutomaticAiMask.Geometry(selection, canvas).Bounds;
        string Pixels(int w, int h) => $"{w} × {h} px";
        var request = "Workflow-defined";
        if (NanoBanana.IsModel(engine?.ApiModel))
            request = $"{NanoBanana.Resolution(quality)} · {NanoBanana.Aspect(bounds.Width, bounds.Height)}";
        else if (engine?.PaidApi == true)
        {
            var scale = Math.Sqrt((double)width * height / bounds.Width / bounds.Height);
            var desired = original ? AiDimensions.OriginalGenerationSize(bounds.Width, bounds.Height)
                : ((int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale));
            var size = engine.ApiModel == PartnerNodes.Seedream ? PartnerNodes.SeedreamSize(desired.Item1, desired.Item2) : PartnerImageSize.Plan(desired.Item1, desired.Item2, original ? Composa.Model.DocumentLimits.MinimumGenerationPixels : PartnerImageSize.MinimumPixels);
            request = Pixels(size.Width, size.Height);
        }
        else if (engine?.Id is "flux2-klein-intel-xpu" or QwenImageWorkflow.EngineId or Flux3Workflow.EngineId)
        {
            var size = AiContextGeometry.FluxSize(bounds, selection, width, height, original);
            request = engine.Id is QwenImageWorkflow.EngineId or Flux3Workflow.EngineId ? Pixels((size.Content.Width + 31) / 32 * 32, (size.Content.Height + 31) / 32 * 32)
                : Pixels(size.Padded.Width, size.Padded.Height);
        }
        return $"Selection  {Pixels(selection.Width, selection.Height)}\nContext  {Pixels(bounds.Width, bounds.Height)}\nModel request  {request}\nCanvas  {Pixels(canvas.Width, canvas.Height)}";
    }
}
