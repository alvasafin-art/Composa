using Composa.App.Dialogs;
using Composa.Rendering;

namespace Composa.App;

public sealed partial class MainWindow
{
    private async Task Liquify()
    {
        var target = session!;
        if (!target.CanLiquify || target.ActiveLayer?.Pixels is not { } source) return;
        try
        {
            var revision = target.Revision; var layerId = target.ActiveLayer.Id;
            using var selection = target.SelectionInLayerSpace(target.ActiveLayer);
            using var converted = Pixels.DepthOf(source) != target.Document.WorkingDepth || !Pixels.ProfileOf(source).Equals(target.Document.Profile)
                ? Pixels.ConvertWorking(source, target.Document.WorkingDepth, target.Document.Profile) : null;
            await using var dialog = new LiquifyDialog(converted ?? source, selection, DisplayProfile);
            if (!await dialog.Ask(this) || dialog.Result is not { } result) return;
            if (!sessions.Contains(target) || target.Revision != revision || target.ActiveLayer?.Id != layerId)
            { ShowProblem("The document or active layer changed while Liquify was open; the result was not applied."); return; }
            if (target.ApplyLiquify(source, dialog.Field, result)) dialog.AdoptResult();
        }
        catch (Exception error) { ShowProblem("Liquify failed: " + error.Message); }
    }
}
