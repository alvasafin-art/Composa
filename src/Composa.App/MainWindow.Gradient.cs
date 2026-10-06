using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.Model;

namespace Composa.App;

public sealed partial class MainWindow
{
    private async Task EditGradientRamp()
    {
        var target = session!; var ramp = target.GradientRamp ?? GradientRamp.Between((uint)target.Foreground, (uint)target.Background);
        var editor = new GradientEditor(this, ramp, value => ramp = value);
        var dialog = new DialogWindow("Gradient stops", editor);
        if (await dialog.Ask(this)) target.GradientRamp = ramp;
    }
}
