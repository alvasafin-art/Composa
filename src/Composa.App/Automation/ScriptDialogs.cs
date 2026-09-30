using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Composa.App.Dialogs;

namespace Composa.App.Automation;

public interface IScriptDialogs
{
    Task<IReadOnlyDictionary<string, object?>> ShowAsync(ScriptForm form, CancellationToken token);
}

public sealed record ScriptField(string Name, string Label, string Type, JsonElement Value, double? Min = null, double? Max = null);
public sealed record ScriptForm(string Title, ScriptField[] Fields)
{
    public static ScriptForm Parse(string json)
    {
        if (json.Length > 32768) throw new ArgumentException("The input form is too large.");
        var form = JsonSerializer.Deserialize<ScriptForm>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new ArgumentException("A form is required.");
        if (string.IsNullOrWhiteSpace(form.Title) || form.Title.Length > 200 || form.Fields == null || form.Fields.Length is < 1 or > 16)
            throw new ArgumentException("Use a title and 1–16 input fields.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in form.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name) || field.Name.Length > 80 || !names.Add(field.Name) || string.IsNullOrWhiteSpace(field.Label) || field.Label.Length > 200)
                throw new ArgumentException("Each field needs a unique name and a label.");
            if (field.Type is not ("number" or "text" or "boolean")) throw new ArgumentException("Field type is number, text or boolean.");
            if (field.Type == "number" && (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetDouble(out var value) || !double.IsFinite(value)
                || field.Min is { } min && (!double.IsFinite(min) || value < min)
                || field.Max is { } max && (!double.IsFinite(max) || value > max))) throw new ArgumentException("Number fields need a finite value within min/max.");
            if (field.Type == "text" && (field.Value.ValueKind != JsonValueKind.String || field.Value.GetString()!.Length > 4096)) throw new ArgumentException("Text fields need a string of at most 4096 characters.");
            if (field.Type == "boolean" && field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("Boolean fields need true/false.");
        }
        return form;
    }
}

/// <summary>Native owned dialogs, no DOM/CLR or arbitrary UI access is exposed to scripts.</summary>
internal sealed class ScriptDialogs(Window owner) : IScriptDialogs
{
    public async Task<IReadOnlyDictionary<string, object?>> ShowAsync(ScriptForm form, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var values = new Dictionary<string, object?>();
        var body = Ui.Column(10);
        foreach (var field in form.Fields)
        {
            Control input;
            if (field.Type == "number")
            {
                values[field.Name] = field.Value.GetDouble();
                input = Ui.Number(field.Value.GetDouble(), field.Min ?? -Composa.Model.DocumentLimits.MaxSide, field.Max ?? Composa.Model.DocumentLimits.MaxSide,
                    value => values[field.Name] = value, width: 240);
            }
            else if (field.Type == "boolean")
            {
                var check = new CheckBox { IsChecked = field.Value.GetBoolean() };
                values[field.Name] = check.IsChecked == true;
                check.IsCheckedChanged += (_, _) => values[field.Name] = check.IsChecked == true;
                input = check;
            }
            else
            {
                var box = new TextBox { Text = field.Value.GetString(), Width = 300, MaxLength = 4096 };
                values[field.Name] = box.Text;
                box.TextChanged += (_, _) => values[field.Name] = box.Text ?? "";
                input = box;
            }
            body.Children.Add(Ui.Column(4, Ui.Label(field.Label), input));
        }
        var dialog = new DialogWindow(form.Title, body);
        using var registration = token.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
        if (!await dialog.Ask(owner)) throw new OperationCanceledException("Script input was cancelled.", token);
        token.ThrowIfCancellationRequested();
        return values;
    }
}
