using Avalonia.Controls;
using Composa.AI;

namespace Composa.App.Dialogs;

public static partial class AiDialogs
{
    private static Control SceneAdvanced(AiTaskKind task, SceneEditSettings initial, bool paid, Action<SceneEditSettings> changed)
    {
        var scene = initial;
        void Update(SceneEditSettings value) { scene = value; changed(value); }
        var controls = Ui.Column(9);
        if (task != AiTaskKind.ChangeBackground)
        {
            var mode = Ui.Combo(Enum.GetValues<SceneMatchMode>(), scene.MatchMode,
                value => value switch { SceneMatchMode.LightAndShadows => "Light and shadows", SceneMatchMode.LightOnly => "Light only", _ => "Tone and color · no AI" },
                value => Update(scene with { MatchMode = value }), 300);
            var strength = Ui.SliderField("Strength", scene.Strength * 100, 0, 100, value => Update(scene with { Strength = value / 100 }), 1, "0", 300, reset: 100);
            var reach = Ui.SliderField("Canvas reach", scene.ShadowReach * 100, 10, 200, value => Update(scene with { ShadowReach = value / 100 }), 1, "0", 300, reset: 100);
            var preserve = Ui.Check("Preserve source detail", scene.PreserveDetails, value => Update(scene with { PreserveDetails = value }));
            var shadows = Ui.Check("Create a separate shadow layer", scene.AddShadows, value => Update(scene with { AddShadows = value }));
            var angleValue = scene.LightAngle ?? 135;
            var angle = Ui.AngleField("Light from", angleValue, 0, 360, value => { angleValue = value; Update(scene with { LightAngle = value }); }, 300);
            var autoAngle = Ui.Check("Automatic light direction", scene.LightAngle == null,
                value => Update(scene with { LightAngle = value ? null : angleValue }));
            autoAngle.IsCheckedChanged += (_, _) => angle.IsEnabled = autoAngle.IsChecked != true;
            angle.IsEnabled = scene.LightAngle != null;
            if (task == AiTaskKind.MatchToScene) controls.Children.Add(CanvasDialogs.Form(("Mode", mode)));
            controls.Children.Add(CanvasDialogs.Form(("Effect", strength)));
            controls.Children.Add(preserve); controls.Children.Add(shadows); controls.Children.Add(autoAngle);
            controls.Children.Add(CanvasDialogs.Form(("Direction", angle), ("Shadow area", reach)));
        }
        if (!paid)
            controls.Children.Add(CanvasDialogs.Form(("FLUX denoise", Ui.SliderField("Denoise", scene.Denoise, .1, 1,
                value => Update(scene with { Denoise = value }), .01, "0.00", 300, reset: .8))));
        return Section(task == AiTaskKind.ChangeBackground ? "Background generation" : "Lighting and shadows", controls);
    }
}
