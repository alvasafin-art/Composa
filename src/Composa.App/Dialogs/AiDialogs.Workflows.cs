using Avalonia.Controls;
using Avalonia.Media;
using System.Text.Json.Nodes;
using Composa.App.AI;

namespace Composa.App.Dialogs;

public static partial class AiDialogs
{
    private static Control BoundGenerationFields(AiOperationSettings profile, SavedWorkflow workflow, out Func<AiOperationSettings> read)
    {
        var bindings = workflow.Bindings;
        var original = profile.OriginalSize;
        var megapixels = profile.Megapixels;
        var seed = profile.Seed;
        var steps = profile.WorkflowSteps;
        var sampler = profile.WorkflowSampler;
        var fields = new List<(string, Control)>();

        JsonValue? GraphValue(string source)
        {
            var target = bindings[source];
            return workflow.Graph[target.NodeId]?["inputs"]?[target.Input] as JsonValue;
        }

        if (bindings.ContainsKey("width") || bindings.ContainsKey("height"))
        {
            var sizes = new[] { "Original size" }.Concat(Composa.AI.AiDimensions.MegapixelOptions.Select(Composa.AI.AiDimensions.Label)).ToArray();
            fields.Add(("Image size", Ui.Combo(sizes, original ? sizes[0] : Composa.AI.AiDimensions.Label(ClosestMegapixels(megapixels)),
                value => value, value =>
                {
                    original = value == sizes[0];
                    if (!original)
                        megapixels = Composa.AI.AiDimensions.MegapixelOptions.First(option => Composa.AI.AiDimensions.Label(option) == value);
                }, 150)));
        }
        if (bindings.ContainsKey("seed"))
            fields.Add(("Seed", Ui.Row(6, Ui.Number(seed, -1, long.MaxValue, value => seed = (long)value, width: 150), Ui.Label("-1 = random"))));
        if (bindings.ContainsKey("steps"))
        {
            var defaultSteps = GraphValue("steps") is { } value && value.TryGetValue<int>(out var count) ? count : 4;
            fields.Add(("Steps", Ui.Number(steps ?? defaultSteps, 1, int.MaxValue, value => steps = (int)value)));
        }
        if (bindings.ContainsKey("sampler"))
        {
            var defaultSampler = GraphValue("sampler") is { } value && value.TryGetValue<string>(out var method) ? method : "";
            var field = new TextBox { Text = sampler ?? "", PlaceholderText = defaultSampler, MinWidth = 150 };
            field.TextChanged += (_, _) => sampler = string.IsNullOrWhiteSpace(field.Text) ? null : field.Text.Trim();
            fields.Add(("Sampler", field));
        }

        var boundFields = bindings.Select(pair => WorkflowSettingsPage.SourceName(pair.Key) + " → " + pair.Value.NodeId + ".inputs." + pair.Value.Input);
        var summary = Ui.Label(string.Join("\n", boundFields), Palette.Secondary);
        summary.TextWrapping = TextWrapping.Wrap;
        var title = Ui.Label(workflow.Name, weight: FontWeight.SemiBold);
        title.TextWrapping = TextWrapping.Wrap;
        var note = Ui.Label(Locale.T("Change or remove bindings in Settings → Workflows."), Palette.Secondary);
        note.TextWrapping = TextWrapping.Wrap;
        read = () => profile with { OriginalSize = original, Megapixels = megapixels, Seed = seed, WorkflowSteps = steps, WorkflowSampler = sampler };
        return Ui.Column(8, title, fields.Count == 0 ? new Panel() : CanvasDialogs.Form(fields.ToArray()), summary, note);
    }
}
