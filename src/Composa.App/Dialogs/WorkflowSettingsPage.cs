using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Composa.AI;
using Composa.App.AI;

namespace Composa.App.Dialogs;

internal sealed class WorkflowSettingsPage
{
    private readonly WorkflowPreferences draft;
    private readonly EngineCatalog catalog;
    private readonly Func<IReadOnlyDictionary<string, string>> modelChoices;
    private readonly Func<ComfyServerCapabilities?> capabilities;
    private readonly Dictionary<string, (EngineProfile Engine, AiTaskKind Task)> factories = [];
    private readonly Dictionary<AiTaskKind, ComboBox> assignments = [];
    private readonly ComboBox picker = new() { Name = "WorkflowPicker", MinWidth = 250 };
    private readonly TextBox name = new() { Name = "WorkflowName" };
    private readonly TextBox json = new()
    {
        Name = "WorkflowJson", AcceptsReturn = true, Height = 190, FontFamily = new FontFamily("monospace"),
        [ScrollViewer.HorizontalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto
    };
    private readonly StackPanel rows = Ui.Column(5);
    private readonly TextBlock message = Ui.Label("", Brushes.Orange);
    private readonly Dictionary<string, WorkflowTarget> bindings = [];
    private readonly Button restore;
    private readonly Button save;
    private readonly Button duplicate;
    private string? current;
    private bool outputIsComposited;
    private bool refreshing;

    internal Control Content { get; }

    internal WorkflowSettingsPage(WorkflowPreferences draft, EngineCatalog catalog,
        Func<IReadOnlyDictionary<string, string>> modelChoices, Func<ComfyServerCapabilities?> capabilities, string? selectedId = null)
    {
        this.draft = draft;
        this.catalog = catalog;
        this.modelChoices = modelChoices;
        this.capabilities = capabilities;
        current = selectedId;
        foreach (var engine in catalog.Profiles)
            foreach (var task in WorkflowPreferences.Operations.Where(task => task != AiTaskKind.GenerativeExpand && engine.Binding(task) != null))
                factories[WorkflowPreferences.FactoryId(engine.Id, task)] = (engine, task);

        message.TextWrapping = TextWrapping.Wrap;
        message.Name = "WorkflowStatus";
        var import = Ui.TextButton("Import JSON…", () => _ = ImportAsync());
        import.Name = "ImportWorkflow";
        duplicate = Ui.TextButton("Duplicate", () => Save(copy: true));
        restore = Ui.TextButton("Restore factory", Restore);
        save = Ui.TextButton("Save workflow", () => Save(copy: false), accent: true);
        save.Name = "SaveWorkflow";
        var assignmentFields = new List<(string, Control)>();
        foreach (var task in WorkflowPreferences.Operations)
        {
            var combo = new ComboBox { Name = "WorkflowFor" + task, MinWidth = 230 };
            assignments[task] = combo;
            combo.SelectionChanged += (_, _) => Assign(task, Key(combo));
            assignmentFields.Add((task.DisplayName(), combo));
        }
        var note = Ui.Label(Locale.T("Active workflows use the saved graph. The bindings below show every input Composa can replace."), Palette.Secondary);
        note.TextWrapping = TextWrapping.Wrap;
        var format = Ui.Label(Locale.T(EditableWorkflows.ImportFormats), Palette.Secondary);
        format.TextWrapping = TextWrapping.Wrap;
        Content = Ui.Column(9, CanvasDialogs.Form(assignmentFields.ToArray()), Wrap(picker, import), format, Wrap(duplicate, restore, save),
            CanvasDialogs.Form(("Workflow name", name)), Ui.Label(Locale.T("Graph JSON")), json,
            Ui.Label(Locale.T("Workflow bindings")), note, rows, message);
        picker.SelectionChanged += (_, _) => { if (!refreshing) Load(Key(picker)); };
        Refresh();
        Load(Key(picker));
    }

    private static string? Key(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private static ComboBoxItem Entry(string? id, string title) => new() { Tag = id, Content = title };

    private static void Select(ComboBox combo, string? id) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (item.Tag as string) == id) ?? combo.Items[0];

    private void Refresh()
    {
        refreshing = true;
        try
        {
            picker.ItemsSource = factories.Keys.Union(draft.Workflows.Keys).Select(id =>
            {
                if (draft.Workflows.TryGetValue(id, out var saved)) return Entry(id, saved.Name);
                var (engine, task) = factories[id];
                return Entry(id, engine.DisplayName + " · " + Locale.T(task.DisplayName()) + " · " + Locale.T("Factory"));
            }).ToArray();
            if (picker.ItemCount > 0) Select(picker, current);
            foreach (var (task, combo) in assignments)
            {
                combo.ItemsSource = new[] { Entry(null, Locale.T("Follow image model")) }
                    .Concat(draft.Workflows.Select(pair => Entry(pair.Key, pair.Value.Name))).ToArray();
                Select(combo, draft.Assignments.GetValueOrDefault(task));
            }
        }
        finally { refreshing = false; }
    }

    private void Load(string? id)
    {
        current = id;
        message.Text = "";
        try
        {
            var workflow = id == null ? null : draft.Workflows.GetValueOrDefault(id);
            if (workflow == null && id != null && factories.TryGetValue(id, out var factory))
                workflow = EditableWorkflows.Factory(catalog, factory.Engine, factory.Task, modelChoices(), capabilities());
            name.Text = workflow?.Name ?? "";
            json.Text = workflow?.Graph.ToJsonString(EditableWorkflows.Indented) ?? "";
            outputIsComposited = workflow?.OutputIsComposited == true;
            bindings.Clear();
            if (workflow != null)
                foreach (var entry in workflow.Bindings) bindings.Add(entry.Key, entry.Value);
            RenderBindings();
            save.IsEnabled = duplicate.IsEnabled = json.IsEnabled = name.IsEnabled = workflow != null;
            restore.IsEnabled = id != null && factories.ContainsKey(id) && draft.Workflows.ContainsKey(id);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException)
        {
            json.Text = name.Text = "";
            bindings.Clear();
            RenderBindings();
            save.IsEnabled = duplicate.IsEnabled = false;
            restore.IsEnabled = false;
            message.Text = Locale.Message(error.Message);
        }
    }

    private void RenderBindings()
    {
        rows.Children.Clear();
        foreach (var (source, target) in bindings)
        {
            var label = Ui.Label(SourceName(source) + " → " + target.NodeId + ".inputs." + target.Input);
            label.TextWrapping = TextWrapping.Wrap;
            var remove = Ui.TextButton("Remove binding", () => { bindings.Remove(source); RenderBindings(); });
            rows.Children.Add(Wrap(label, remove));
        }
        if (current == null) return;
        var sources = EditableWorkflows.Sources.Where(source => !bindings.ContainsKey(source)).ToArray();
        if (sources.Length == 0) return;
        var selected = sources[0];
        var sourcePicker = Ui.Combo(sources, selected, SourceName, value => selected = value, 148);
        var node = new TextBox { Name = "WorkflowBindingNode", PlaceholderText = Locale.T("Node ID"), Width = 100 };
        var input = new TextBox { Name = "WorkflowBindingInput", PlaceholderText = Locale.T("Input key"), Width = 130 };
        rows.Children.Add(Wrap(sourcePicker, node, input, Ui.TextButton("Add binding", () =>
        {
            try
            {
                var updated = new Dictionary<string, WorkflowTarget>(bindings)
                {
                    [selected] = new() { NodeId = node.Text?.Trim() ?? "", Input = input.Text?.Trim() ?? "" }
                };
                EditableWorkflows.Validate(new(name.Text ?? "", EditableWorkflows.Parse(json.Text ?? ""), updated));
                bindings.Add(selected, updated[selected]);
                message.Text = "";
                RenderBindings();
            }
            catch (InvalidDataException error) { message.Text = Locale.Message(error.Message); }
        })));
    }

    private static WrapPanel Wrap(params Control[] controls)
    {
        var panel = new WrapPanel { ItemSpacing = 6, LineSpacing = 4 };
        panel.Children.AddRange(controls);
        return panel;
    }

    internal static string SourceName(string source) => Locale.T(source switch
    {
        "prompt" => "Prompt", "negativePrompt" => "Negative prompt", "sourceImage" => "Source image",
        "contextImage" => "Context image", "activeLayerImage" => "Active layer image", "selectionMask" => "Selection mask",
        "seed" => "Seed", "width" => "Width", "height" => "Height", "steps" => "Steps", "sampler" => "Sampler",
        _ when source.StartsWith("referenceImage", StringComparison.Ordinal) => "Reference image " + source["referenceImage".Length..],
        _ => source
    });

    private void Assign(AiTaskKind task, string? id)
    {
        if (refreshing) return;
        try
        {
            if (id == null) draft.Assignments.Remove(task);
            else
            {
                EditableWorkflows.Validate(draft.Workflows[id], task);
                draft.Assignments[task] = id;
            }
            message.Text = "";
        }
        catch (InvalidDataException error)
        {
            Refresh();
            message.Text = Locale.Message(error.Message);
        }
    }

    private void Save(bool copy)
    {
        if (current == null) return;
        try
        {
            var title = name.Text?.Trim() ?? "";
            var workflow = new SavedWorkflow(title, EditableWorkflows.Parse(json.Text ?? ""), new(bindings), outputIsComposited);
            EditableWorkflows.Validate(workflow);
            if (!copy)
            {
                if (factories.TryGetValue(current, out var factory)) EditableWorkflows.Validate(workflow, factory.Task);
                foreach (var (task, id) in draft.Assignments)
                    if (id == current) EditableWorkflows.Validate(workflow, task);
            }
            if (copy)
            {
                current = Guid.NewGuid().ToString("N");
                workflow = workflow with { Name = title + " · " + Locale.T("Copy") };
            }
            draft.Workflows[current] = workflow;
            Refresh();
            Load(current);
            message.Text = Locale.T("Workflow ready. Save settings to apply your changes.");
        }
        catch (InvalidDataException error) { message.Text = Locale.Message(error.Message); }
    }

    private void Restore()
    {
        if (current == null || !factories.ContainsKey(current)) return;
        draft.Workflows.Remove(current);
        foreach (var task in draft.Assignments.Where(pair => pair.Value == current).Select(pair => pair.Key).ToArray())
            draft.Assignments.Remove(task);
        Refresh();
        Load(current);
        message.Text = Locale.T("Factory workflow restored. Save settings to apply your changes.");
    }

    private async Task ImportAsync()
    {
        try
        {
            if (TopLevel.GetTopLevel(Content) is not { } window) return;
            var files = await window.StorageProvider.OpenFilePickerAsync(new()
            {
                Title = Locale.T("Import workflow JSON"), AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
            });
            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            Import(Path.GetFileNameWithoutExtension(files[0].Name), await reader.ReadToEndAsync());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { message.Text = Locale.Message(error.Message); }
    }

    internal void Import(string title, string contents)
    {
        var graph = EditableWorkflows.Parse(contents);
        var id = Guid.NewGuid().ToString("N");
        draft.Workflows[id] = new(title, graph, []);
        current = id;
        Refresh();
        Load(id);
        message.Text = Locale.T("Add the required bindings before assigning this workflow to an operation.");
    }
}
