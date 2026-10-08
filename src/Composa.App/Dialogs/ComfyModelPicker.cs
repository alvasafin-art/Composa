using Avalonia.Controls;
using Avalonia.Media;
using Composa.App.AI;

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
        if (service.SelectedEngine is { PaidApi: true } api)
        {
            Children.Add(Ui.Label(api.DisplayName, weight: Avalonia.Media.FontWeight.SemiBold));
            Children.Add(Note($"Paid Comfy.org Partner Node · {api.ApiModel}\nNo diffusion model, text encoder or VAE files are needed.\nBalance is not exposed by the local ComfyUI API; check Credits in ComfyUI."));
        }
        Children.Add(Ui.Label("Models on ComfyUI", weight: Avalonia.Media.FontWeight.SemiBold));
        if (!connected) Children.Add(Note("Refresh models to read this server. Lists from another computer are not used."));
        if (service.SelectedEngine is not { } engine) { Children.Add(Note("Select a workflow pack first.")); return; }
        var slots = (engine.PaidApi ? [] : service.Engines.ModelSlots(engine)).Concat(service.Engines.Profiles
            .Where(profile => profile.Id.StartsWith("seedvr2", StringComparison.Ordinal)).SelectMany(profile => UpscaleModels.Slots(service.Engines, profile)))
            .DistinctBy(slot => slot.Key).ToArray();
        foreach (var slot in slots)
        {
            var available = connected ? service.ServerCapabilities?.ModelChoices.GetValueOrDefault(slot.LoaderKey) : null;
            if (available != null) available = new HashSet<string>(UpscaleModels.Available(slot, available), StringComparer.OrdinalIgnoreCase);
            var options = new List<Option> { new(null, "Workflow default · " + slot.Default) };
            options.AddRange((available ?? []).Order(StringComparer.OrdinalIgnoreCase).Select(name => new Option(name, name)));
            choices.TryGetValue(slot.Key, out var saved);
            if (saved != null && !options.Any(option => option.Name == saved))
                options.Add(new(saved, (available != null && WorkflowModels.Resolve(saved, available) != null ? "Saved · " : "Unavailable · ") + saved));
            var backendAvailable = !slot.EngineId.StartsWith("seedvr2", StringComparison.Ordinal)
                || service.Engines.Find(slot.EngineId)!.RequiredNodeTypes.All(type => service.ServerCapabilities?.NodeTypes.Contains(type) == true);
            var combo = new ComboBox { Width = 410, ItemsSource = options, SelectedItem = options.First(option => option.Name == saved), IsEnabled = connected && available?.Count > 0 && backendAvailable };
            ToolTip.SetTip(combo, slot.NodeType + "." + slot.Input + " · node " + slot.NodeId);
            var hint = Note("");
            void Hint()
            {
                var selected = (combo.SelectedItem as Option)?.Name ?? slot.Default;
                var resolved = available == null ? null : WorkflowModels.Resolve(selected, available);
                hint.Text = !connected ? "Not connected to this server." : !backendAvailable ? "This SeedVR2 backend is not installed on the server." : available == null
                    ? (service.ServerCapabilities!.NodeTypes.Contains(slot.NodeType) ? "This loader does not report a model list." : "Missing node: " + slot.NodeType)
                    : resolved == null ? "Not found or ambiguous. Choose a model above; files must match this workflow's architecture."
                    : "Server model: " + resolved;
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
            Children.Add(Ui.Column(4, Ui.Label((slot.EngineId.StartsWith("seedvr2", StringComparison.Ordinal) ? service.Engines.Find(slot.EngineId)!.DisplayName + " · " : "")
                + slot.Label + (slots.Count(other => other.Kind == slot.Kind) > 1 ? " · " + slot.NodeId : "")), combo, hint));
        }
        Children.Add(Note("The server reads its own models and shared folders. No model files or network drives are needed on this computer."));
    }

    private static TextBlock Note(string text) => new() { Text = text, MaxWidth = 430, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Secondary };
}
