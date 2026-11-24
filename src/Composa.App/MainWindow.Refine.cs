using Composa.App.Dialogs;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;

namespace Composa.App;

public sealed partial class MainWindow
{
    private async Task RefineSelection()
    {
        var target = session!;
        if (target.Selection is not { } original) return;
        var revision = target.Revision; var snapshot = target.Document.Clone();
        var active = snapshot.ActiveLayer is { Pixels: not null } layer ? layer : null;
        using var source = MaskRefinement.Source(snapshot);
        await using var dialog = new SelectMaskDialog(source, original, DisplayProfile, active != null,
            active == null ? null : () => DocumentRenderer.Flatten(snapshot));
        if (!await dialog.Ask(this) || dialog.ResultMask is not { } result) return;
        if (!sessions.Contains(target) || target.Revision != revision || target.Document.ActiveLayerId != snapshot.ActiveLayerId)
        { ShowProblem("The document or active layer changed while refining; the result was not applied."); return; }
        if (dialog.Output == "Layer mask")
        {
            if (target.ActiveLayer?.Pixels == null || target.PixelsLocked(target.ActiveLayer))
            { ShowProblem("Select an unlocked pixel layer to create a layer mask."); return; }
            target.RefinedSelectionToMask(result);
        }
        else if (dialog.Output == "New layer with mask")
        {
            target.AddRefinedCutout(dialog.ResultColors!, result, dialog.UsesActiveLayer ? active?.Id : null);
            dialog.AdoptResult();
        }
        else
        {
            target.Select(result, SelectionMode.Replace, "Select and Mask");
            if (!SelectionMask.IsEmpty(result)) dialog.AdoptResult(); // History owns the nonempty replacement.
        }
    }
}
