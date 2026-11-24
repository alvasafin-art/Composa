using Avalonia.Controls;
using Avalonia.Media;
using Composa.App.AI;
using Composa.AI;

namespace Composa.App.Dialogs;

/// <summary>Edits a draft, not live settings. Only the connected endpoint supplies dropdown choices.</summary>
public sealed class ComfyModelPicker : StackPanel
{
    private readonly Settings settings;
    private readonly AiTaskService service;
    private readonly Func<string> serverUrl;
    private readonly Dictionary<string, Dictionary<string, string>> drafts = new(StringComparer.Ordinal);
    private readonly HashSet<string> changed = new(StringComparer.Ordinal);
    public event Action? ChoicesChanged;
    private AiTaskKind? operation;
    public void SetOperation(AiTaskKind? value)
    {
        if (operation == value) return;
        operation = value;
        Refresh();
    }
    private sealed record Option(string? Name, string Label) { public override string ToString() => Label; }

    public ComfyModelPicker(Settings settings, AiTaskService service, Func<string> serverUrl)
    {
        this.settings = settings;
        this.service = service;
        this.serverUrl = serverUrl;
        Spacing = 10;
        Refresh();
    }

    public IReadOnlyDictionary<string, string> Choices(string url)
    {
        var key = ComfyServerAddress.Parse(url).ToString();
        if (!drafts.TryGetValue(key, out var choices)) drafts[key] = choices = new(settings.ComfyModelsFor(key), StringComparer.Ordinal);
        return choices;
    }

    public void Save()
    {
        foreach (var url in changed) settings.SetComfyModels(url, drafts[url]);
    }

    public void Refresh()
    {
        Children.Clear();
        string address;
        try { address = ComfyServerAddress.Parse(serverUrl()).ToString(); }
        catch (FormatException) { Children.Add(Note("Enter a complete ComfyUI server URL, then refresh models.")); return; }
        var choices = (Dictionary<string, string>)Choices(address);
        var connected = service.ConnectedServerUrl == address && service.ConnectionState == ComfyConnectionState.Connected;
        var fields = new List<Control>
        {
            StudioSettingsLayout.Field("Connection", new TextBox { Text = address, IsReadOnly = true })
        };
        if (service.SelectedEngine is { PaidApi: true } api)
        {
            fields.Add(StudioSettingsLayout.Field("Main model", new TextBox { Text = api.ApiModel, IsReadOnly = true }));
        }

        if (service.SelectedEngine is not { } engine) { Children.Add(Note("Select a workflow pack first.")); return; }
        WorkflowModelSlot[] slots;
        try
        {
            var scoped = operation is { } task && engine.Binding(task) != null ? EngineCatalog.WorkflowFor(engine, task) : null;
            slots = engine.PaidApi ? [] : scoped == null ? service.Engines.ModelSlots(engine).ToArray()
                : WorkflowModels.Slots(service.Engines.ReadWorkflow(engine, scoped), engine.Id).ToArray();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Children.Add(Note(error.Message));
            return;
        }
        foreach (var slot in slots)
        {
            var available = connected ? service.ServerCapabilities?.ModelChoices.GetValueOrDefault(slot.LoaderKey) : null;
            if (available != null) available = new HashSet<string>(UpscaleModels.Available(slot, available)
                .Where(name => !WorkflowModels.IsKnownIncompatible(slot, name)), StringComparer.Ordinal);
            var workflowValue = available == null ? null : WorkflowModels.ResolveSlot(slot, slot.Default, available);
            choices.TryGetValue(slot.Key, out var saved);
            var options = new List<Option> { new(null, workflowValue ?? slot.Default) };
            options.AddRange((available ?? []).Where(name => name != workflowValue || name == saved)
                .Order(StringComparer.OrdinalIgnoreCase).Select(name => new Option(name, name)));
            if (saved != null && !options.Any(option => option.Name == saved))
                options.Add(new(saved, (available != null && WorkflowModels.Resolve(saved, available) != null ? "Saved · " : "Unavailable · ") + saved));
            var backendAvailable = !slot.EngineId.StartsWith("seedvr2", StringComparison.Ordinal)
                || service.Engines.Find(slot.EngineId)!.RequiredNodeTypes.All(type => service.ServerCapabilities?.NodeTypes.Contains(type) == true);
            var combo = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                ItemsSource = options, SelectedItem = options.First(option => option.Name == saved), IsEnabled = connected && available?.Count > 0 && backendAvailable };
            AiGenerationControls.StyleModelField(combo, slot.Label);
            combo.Height = double.NaN;
            combo.MinHeight = 34;
            combo.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Option>((option, _) =>
                new TextBlock { Text = option?.Label, TextWrapping = TextWrapping.Wrap });
            combo.SelectionBoxItemTemplate = combo.ItemTemplate;
            Locale.Tip(combo, slot.NodeType + "." + slot.Input + " · node " + slot.NodeId);
            var hint = Note("");
            var origin = StudioSettingsLayout.Caption("");
            var reset = Ui.TextButton("Use workflow value", () => combo.SelectedIndex = 0);
            void Hint()
            {
                var selected = (combo.SelectedItem as Option)?.Name ?? slot.Default;
                reset.IsVisible = (combo.SelectedItem as Option)?.Name != null;
                origin.Text = Locale.T(reset.IsVisible ? "Set in Composa" : "From workflow");
                var resolved = available == null ? null : WorkflowModels.ResolveSlot(slot, selected, available);
                hint.Text = Locale.Message(!connected ? "Not connected to this server." : !backendAvailable ? "This SeedVR2 backend is not installed on the server." : available == null
                    ? (service.ServerCapabilities!.NodeTypes.Contains(slot.NodeType) ? "This loader does not report a model list." : "Missing node: " + slot.NodeType)
                    : resolved == null ? "Not found or ambiguous. Choose a model above; files must match this workflow's architecture."
                    : "Server model: " + resolved);
                hint.IsVisible = connected && resolved == null;
                Locale.Tip(combo, hint.Text);
                hint.Foreground = connected && resolved == null ? Brushes.Orange : Palette.Secondary;
            }
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is Option { Name: { } name }) choices[slot.Key] = name;
                else choices.Remove(slot.Key);
                changed.Add(address);
                Hint();
                ChoicesChanged?.Invoke();
            };
            Hint();
            var label = slot.Kind == EngineAssetKind.DiffusionModel ? "Main model" : slot.Label;
            fields.Add(Ui.Column(4, StudioSettingsLayout.Caption((slot.EngineId.StartsWith("seedvr2", StringComparison.Ordinal)
                ? service.Engines.Find(slot.EngineId)!.DisplayName + " · " : "") + label +
                (slots.Count(other => other.Kind == slot.Kind) > 1 ? " · " + slot.NodeId : "")),
                StudioSettingsLayout.Columns("*,Auto", combo, reset), origin, hint));
        }
        foreach (var row in fields.Chunk(2)) Children.Add(StudioSettingsLayout.Columns("*,*", row));
    }

    private static TextBlock Note(string text) => new() { Text = text, MaxWidth = 430, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Secondary };
}
