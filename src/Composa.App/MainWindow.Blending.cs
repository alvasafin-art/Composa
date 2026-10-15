using Composa.App.Dialogs;
using Composa.Model;

namespace Composa.App;

public sealed partial class MainWindow
{
    private async Task EditBlending(Layer layer)
    {
        if (session is not { } target || canvas.IsDragging || target.IsLocked(layer)) return;
        if (target.IsEditingText) target.FinishText();
        var original = (layer.FillOpacity, layer.BlendIf);
        target.Begin("Blending Options");
        try
        {
            if (await BlendingDialog.Edit(this, target, layer) && original != (layer.FillOpacity, layer.BlendIf)) target.Commit();
            else target.Cancel();
        }
        catch { target.Cancel(); throw; }
        target.NotifyLayersChanged();
    }
}
