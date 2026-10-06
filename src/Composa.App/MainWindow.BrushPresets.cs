using Avalonia.Controls;
using Composa.App.Dialogs;
using Composa.Editing;
using Avalonia;
namespace Composa.App;

public sealed partial class MainWindow
{
    private Button BrushDynamicsMenu(EditorSession target)
    {
        var button = new Button { Content = "Dynamics ▾", Padding = new Thickness(8, 0) };
        button.Click += (_, _) =>
        {
            var body = Ui.Column(10,
                Ui.Check("Pen pressure controls size", target.Brush.PressureSize, v => target.Brush = target.Brush with { PressureSize = v }),
                Ui.Check("Pen pressure controls flow", target.Brush.PressureFlow, v => target.Brush = target.Brush with { PressureFlow = v }),
                Ui.Check("Full brush outline", target.Brush.FullCursor, v => { target.Brush = target.Brush with { FullCursor = v }; canvas.InvalidateVisual(); }),
                Ui.SliderField("Smoothing", target.Brush.Smoothing, 0, 100, v => target.Brush = target.Brush with { Smoothing = v }, width: 230),
                Ui.TextButton("Save current brush…", () => _ = SaveBrushPreset()));
            body.Margin = new Thickness(12); var flyout = new Flyout { Content = body }; flyout.ShowAt(button);
        };
        ToolTip.SetTip(button, "Pen pressure, smoothing, cursor outline and saved brushes"); return button;
    }
    private async Task SaveBrushPreset()
    {
        var brush = session!.Brush; var name = new TextBox { Text = "My brush", Width = 240, MaxLength = 100 };
        var dialog = new DialogWindow("Save Brush", Ui.Row(10, Ui.Label("Name"), name));
        if (await dialog.Ask(this) && name.Text?.Trim() is { Length: > 0 } title)
        { settings.BrushPresets[title] = brush; settings.Save(); RebuildOptions(); }
    }
}
