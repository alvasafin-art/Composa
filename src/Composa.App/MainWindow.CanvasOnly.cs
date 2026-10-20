using Avalonia.Controls;

namespace Composa.App;

public sealed partial class MainWindow
{
    // A host preserves each child's own visibility (update notices and context bars can change independently).
    private Control Chrome(Control child)
    {
        var host = new Border { Child = child, IsVisible = !settings.CanvasOnly };
        canvasChrome.Add(host); return host;
    }
    internal void SetCanvasOnly(bool enabled)
    {
        if (settings.CanvasOnly == enabled) return;
        settings.CanvasOnly = enabled; settings.Save();
        foreach (var host in canvasChrome) host.IsVisible = !enabled;
        aiFloatingLayer.IsVisible = !enabled;
        if (canvasOnlyItem != null) canvasOnlyItem.IsChecked = enabled;
        canvas.Focus();
        canvas.InvalidateVisual();
    }
}
