using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Composa.Editing;

namespace Composa.App;

public sealed partial class MainWindow
{
    private ToggleButton showCanvasLayout = null!;
    private ToggleButton snapCanvasLayout = null!;
    private StackPanel canvasLayoutButtons = null!;

    private Control LayoutViewButtons()
    {
        showCanvasLayout = LayoutToggle("Show Layout Grid", Icons.LayoutSquares, (view, value) => view with { ShowCanvasLayout = value });
        snapCanvasLayout = LayoutToggle("Snap to Layout Grid", Icons.Magnet, (view, value) => view with { SnapToCanvasLayout = value });
        canvasLayoutButtons = Ui.Row(3, showCanvasLayout, snapCanvasLayout);
        canvasLayoutButtons.Margin = new Thickness(0, 0, 6, 0);
        return canvasLayoutButtons;
    }

    private ToggleButton LayoutToggle(string label, Icons.Icon icon, Func<ViewOptions, bool, ViewOptions> apply)
    {
        var button = new ToggleButton { Content = Icons.Create(icon, 15), Padding = new Thickness(5) };
        AutomationProperties.SetName(button, Locale.T(label));
        Locale.Tip(button, label);
        button.Click += (_, _) =>
        {
            if (session == null) return;
            session.View = apply(session.View, button.IsChecked == true);
            canvas.ViewOptionsChanged(session.View.ShowRulers);
            RememberToolSettings();
        };
        return button;
    }

    private void RefreshCanvasLayoutView()
    {
        canvasLayoutButtons.IsVisible = session?.Document.Layout.HasGuides == true;
        if (session == null) return;
        showCanvasLayout.IsChecked = session.View.ShowCanvasLayout;
        snapCanvasLayout.IsChecked = session.View.SnapToCanvasLayout;
    }
}
