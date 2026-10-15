using Avalonia.Controls;
using Composa.App.Dialogs;
using Composa.Model;

namespace Composa.App;

public sealed partial class MainWindow
{
    private async Task EditMaskProperties(Layer layer)
    {
        if (session is not { } target || canvas.IsDragging || layer.Mask == null || target.PixelsLocked(layer)) return;
        var original = (layer.MaskDensity, layer.MaskFeather);
        target.Begin("Layer Mask Properties");
        var rows = new StackPanel { Spacing = 10 };
        rows.Children.Add(Ui.SliderField("Density", layer.MaskDensity * 100, 0, 100,
            value => target.PreviewMaskProperties(layer, value / 100, layer.MaskFeather), width: 330, reset: 100));
        rows.Children.Add(Ui.SliderField("Feather", layer.MaskFeather, 0, 1000,
            value => target.PreviewMaskProperties(layer, layer.MaskDensity, value), step: .1, format: "0.0 px", width: 330, reset: 0));
        try
        {
            if (await new DialogWindow("Layer Mask Properties", rows).Ask(this) && original != (layer.MaskDensity, layer.MaskFeather)) target.Commit();
            else target.Cancel();
        }
        catch { target.Cancel(); throw; }
        target.NotifyLayersChanged();
    }
}
