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
    private AiTaskKind? tool;
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

    internal void ShowFilesFor(EngineProfile engine, AiTaskKind? task)
    {
        service.SelectedEngine = engine;
        tool = task;
        Refresh();
    }

    internal static IEnumerable<WorkflowModelSlot> FileSlots(EngineCatalog catalog, EngineProfile engine, AiTaskKind? tool) =>
        tool == null ? catalog.ModelSlots(engine).Where(slot => slot.Kind is not (EngineAssetKind.Upscaler or EngineAssetKind.BackgroundRemoval))
            : WorkflowModels.Slots(catalog.ReadWorkflow(engine, EngineCatalog.WorkflowFor(engine, tool.Value)), engine.Id);

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
            StudioSettingsLayout.SetupField("Connection", StudioSettingsLayout.SetupInput(
                Ui.Combo(new[] { address }, address, value => value, _ => { }, double.NaN)))
        };
        if (service.SelectedEngine is { PaidApi: true } api)
        {
            fields.Add(StudioSettingsLayout.Field("Main model", new TextBox { Text = api.ApiModel, IsReadOnly = true }));
        }

        if (service.SelectedEngine is not { } engine) { Children.Add(Note("Select a workflow pack first.")); return; }
        WorkflowModelSlot[] slots;
        try
        {
            slots = engine.PaidApi ? [] : FileSlots(service.Engines, engine, tool).ToArray();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Children.Add(Note(error.Message));
            return;
        }
        foreach (var group in slots.GroupBy(slot => (slot.LoaderKey, slot.Default)))
        {
            var slot = group.First();
            var available = connected ? service.ServerCapabilities?.ModelChoices.GetValueOrDefault(slot.LoaderKey) : null;
            if (available != null) available = new HashSet<string>(UpscaleModels.Available(slot, available)
                .Where(name => !WorkflowModels.IsKnownIncompatible(slot, name)), StringComparer.Ordinal);
            var workflowValue = available == null ? null : WorkflowModels.ResolveSlot(slot, slot.Default, available);
            var saved = group.Select(item => choices.GetValueOrDefault(item.Key)).FirstOrDefault(value => value != null);
            var options = new List<Option> { new(null, workflowValue ?? slot.Default) };
            options.AddRange((available ?? []).Order(StringComparer.OrdinalIgnoreCase).Select(name => new Option(name, name)));
            if (saved != null && !options.Any(option => option.Name == saved))
                options.Add(new(saved, (available != null && WorkflowModels.Resolve(saved, available) != null ? "Saved · " : "Unavailable · ") + saved));
            var backendAvailable = !slot.EngineId.StartsWith("seedvr2", StringComparison.Ordinal)
                || service.Engines.Find(slot.EngineId)!.RequiredNodeTypes.All(type => service.ServerCapabilities?.NodeTypes.Contains(type) == true);
            var combo = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                ItemsSource = options, SelectedItem = options.First(option => option.Name == saved), IsEnabled = connected && available?.Count > 0 && backendAvailable };
            StudioSettingsLayout.SetupInput(combo);
            combo.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Option>((option, _) =>
                FileLabel(option?.Name == null && option != null ? option.Label + " · " + Locale.T("Automatic") : option?.Label));
            combo.SelectionBoxItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Option>((option, _) => FileLabel(option?.Label));
            Locale.Tip(combo, slot.NodeType + "." + slot.Input + " · node " + slot.NodeId);
            var hint = Note("");
            void Hint()
            {
                var selected = (combo.SelectedItem as Option)?.Name ?? slot.Default;
                var resolved = available == null ? null : WorkflowModels.ResolveSlot(slot, selected, available);
                hint.Text = Locale.Message(!connected ? "Not connected to this server." : !backendAvailable ? "This SeedVR2 backend is not installed on the server." : available == null
                    ? (service.ServerCapabilities!.NodeTypes.Contains(slot.NodeType) ? "This loader does not report a model list." : "Missing node: " + slot.NodeType)
                    : resolved == null ? $"Unavailable file: {selected} ({slot.LoaderKey})"
                    : "Server model: " + resolved);
                hint.IsVisible = connected && resolved == null;
                Locale.Tip(combo, hint.Text);
                hint.Foreground = connected && resolved == null ? Brushes.Orange : Palette.Secondary;
            }
            combo.SelectionChanged += (_, _) =>
            {
                foreach (var component in group)
                    if (combo.SelectedItem is Option { Name: { } name }) choices[component.Key] = name;
                    else choices.Remove(component.Key);
                changed.Add(address);
                Hint();
                ChoicesChanged?.Invoke();
            };
            Hint();
            var label = slot.Kind == EngineAssetKind.DiffusionModel ? "Main model" : slot.Label;
            fields.Add(Ui.Column(4, StudioSettingsLayout.SetupField(label +
                (slots.Count(other => other.Kind == slot.Kind && (other.LoaderKey != slot.LoaderKey || other.Default != slot.Default)) > 0
                    ? " · " + slot.NodeId : ""), combo), hint));
        }
        foreach (var row in fields.Chunk(2)) Children.Add(StudioSettingsLayout.SetupColumns(row));
    }

    private static TextBlock Note(string text) => new() { Text = text, MaxWidth = 430, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Secondary };
    private static TextBlock FileLabel(string? text) => new() { Text = text, TextTrimming = TextTrimming.CharacterEllipsis };
}
