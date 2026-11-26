using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Composa.App.Controls;

namespace Composa.App;

public sealed partial class MainWindow
{
    private Control BuildPenModes()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        (PenEditMode Mode, Icons.Icon Icon, string Label, string Tip)[] modes =
        [
            (PenEditMode.Draw, Icons.Pen, "Pen", "Draw or continue a vector path"),
            (PenEditMode.Move, Icons.Move, "Move", "Move selected vector points or handles"),
            (PenEditMode.Lasso, Icons.Lasso, "Lasso", "Lasso vector points · Shift adds · Alt removes"),
            (PenEditMode.Bend, Icons.VectorBend, "Bend", "Drag a vector segment to bend it"),
            (PenEditMode.Cut, Icons.VectorCut, "Cut path", "Click a vector segment to split the contour"),
            (PenEditMode.Erase, Icons.Eraser, "Eraser", "Erase vector points or segments")
        ];
        foreach (var (mode, icon, label, hint) in modes)
        {
            if (mode == PenEditMode.Move) row.Children.Add(Ui.Separator(true));
            var image = Icons.Create(icon, 15, Palette.Foreground);
            var text = Ui.Label(Locale.T(label));
            var button = new Button { Name = "PenMode" + mode, Classes = { "flat" }, Padding = new Thickness(7, 0),
                Content = Ui.Row(5, image, text), HorizontalContentAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetName(button, Locale.T(label));
            var surface = new Border { CornerRadius = new CornerRadius(4), Child = button };
            Locale.Tip(button, hint);
            button.Click += (_, _) => canvas.SwitchPenMode(mode);
            void Refresh()
            {
                var selected = canvas.PenMode == mode;
                surface.Background = selected ? Palette.Active : Brushes.Transparent;
                text.Foreground = selected ? Palette.OnActive : Palette.Foreground;
                button.IsEnabled = mode is PenEditMode.Cut or PenEditMode.Erase ? canvas.CanCutPenOutline : mode == PenEditMode.Draw || canvas.HasPenOutline;
            }
            Refresh(); refreshOptions += Refresh;
            row.Children.Add(surface);
        }
        return new Border { Background = Palette.Window, CornerRadius = new CornerRadius(5), Padding = new Thickness(2), Child = row };
    }
}
