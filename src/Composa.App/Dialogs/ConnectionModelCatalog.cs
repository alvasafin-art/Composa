using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using static Composa.App.Dialogs.StudioSettingsLayout;

namespace Composa.App.Dialogs;

/// <summary>Connection-scoped model names and IDs are staged until the settings window is saved.</summary>
internal sealed class ConnectionModelCatalog
{
    private sealed class Entry(string id, string name)
    {
        internal string Id = id;
        internal string Name = name;
    }

    private readonly Dictionary<string, Dictionary<string, string>> saved;
    private readonly Dictionary<string, List<Entry>> drafts = new(StringComparer.Ordinal);
    private readonly Func<(Uri Credential, Uri Models)> address;
    private readonly Func<Uri, string?> credential;
    private readonly bool images;
    private readonly bool compactSetup;
    private readonly bool autoProbe;
    private readonly CancellationToken lifetime;
    private CancellationTokenSource? background;
    private Func<bool>? canProbe;
    private string? typedKeyScope;
    private readonly TextBox key;
    private readonly string legacyModel;
    private readonly string? legacyScope;
    private readonly StackPanel rows = Ui.Column(6);
    private readonly TextBlock status = Caption("");
    private readonly Button fetch;
    private readonly Button add;
    private readonly Button reset;
    private bool checkedConnection;
    private bool failed;
    private string? current;
    private int revision;
    private readonly Dictionary<string, string[]> efforts = new(StringComparer.Ordinal);
    internal Control View { get; }
    internal SettingsStatus? CheckStatus => checkedConnection || failed
        ? Status(status.Text ?? "", checkedConnection, failed) : null;
    internal event Action? CheckChanged;
    internal bool? Connected => failed ? false : checkedConnection ? true : null;
    internal string CheckMessage => string.IsNullOrEmpty(status.Text) ? "Connection has not been checked" : status.Text;
    internal string[]? ReasoningFor(string id) => efforts.GetValueOrDefault(id);

    internal ConnectionModelCatalog(Dictionary<string, Dictionary<string, string>> saved,
        TextBox endpoint, TextBox key, TextBox selectedModel, Func<(Uri Credential, Uri Models)> address,
        Func<Uri, string?> credential, CancellationToken lifetime, bool images = false, bool compactSetup = false, bool autoProbe = false)
    {
        this.saved = saved;
        this.address = address;
        this.credential = credential;
        this.images = images;
        this.compactSetup = compactSetup;
        this.autoProbe = autoProbe;
        this.lifetime = lifetime;
        this.key = key;
        legacyModel = selectedModel.Text?.Trim() ?? "";
        try { legacyScope = address().Credential.AbsoluteUri; }
        catch (FormatException) { }
        fetch = compactSetup ? SetupLink("Get available models", () => { }) : Ui.TextButton("Get available models", () => { });
        add = Ui.TextButton("Add model", () => { if (current != null) { Entries().Add(new("", "")); Render(); } });
        reset = Ui.TextButton("Reset this connection’s catalog", () =>
        {
            if (current == null) return;
            drafts.Remove(current);
            Render();
        });
        fetch.Click += async (_, _) =>
        {
            var requestedRevision = revision;
            try { await FetchAsync(lifetime); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) when (error is InvalidOperationException or FormatException or HttpRequestException or OperationCanceledException or JsonException or IOException)
            {
                if (requestedRevision == revision) SetCheckStatus(Locale.Message(error.Message), failed: true);
            }
        };
        reset.Classes.Add("flat");
        fetch.Classes.Add("flat");
        add.HorizontalAlignment = HorizontalAlignment.Stretch;
        var toolbar = new WrapPanel { ItemSpacing = 5, LineSpacing = 4, Children = { reset, fetch } };
        if (compactSetup)
        {
            toolbar.Children.Clear();
            Locale.Tip(fetch, "Check connection");
            add.Classes.Add("flat");
            add.Height = 32;
            add.Padding = new Thickness(0);
            add.Content = Ui.Row(6, Icons.Create(Icons.Plus, 13), Ui.Label(Locale.T("Add model"), Palette.Secondary, 11));
            var border = new Avalonia.Controls.Shapes.Rectangle
            {
                RadiusX = 5, RadiusY = 5, Stroke = Palette.SettingsFieldLine, StrokeThickness = 1,
                StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 3, 2 }, IsHitTestVisible = false
            };
            var addFrame = new Grid { Margin = new Thickness(0, 2, 0, 0), Children = { add, border } };
            View = new Border
            {
                BorderBrush = Palette.SettingsLine, BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 9, 0, 0),
                Child = Ui.Column(7, Columns("*,Auto", Heading("Models", 11), fetch), status, rows, addFrame)
            };
        }
        else
        {
            fetch.IsVisible = !autoProbe;
            View = new Border
            {
                BorderBrush = Palette.SettingsLine, BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 8, 0, 0),
                Child = Ui.Column(7, Columns("*,Auto", Heading("Models", 12), toolbar), status, rows, add)
            };
        }
        endpoint.TextChanged += (_, _) =>
        {
            SelectEndpoint();
            if (!endpoint.IsFocused) ScheduleCheck();
        };
        endpoint.LostFocus += (_, _) => ScheduleCheck();
        key.TextChanged += (_, _) =>
        {
            typedKeyScope = current;
            InvalidateCheck();
            ScheduleCheck();
        };
        SelectEndpoint();
    }

    internal IReadOnlyList<(string Id, string Label)> Models => current == null ? [] :
        Entries().Where(row => !string.IsNullOrWhiteSpace(row.Id)).DistinctBy(row => row.Id.Trim())
            .Select(row => (row.Id.Trim(), string.IsNullOrWhiteSpace(row.Name) ? row.Id.Trim() : row.Name.Trim())).ToArray();

    internal event Action? Changed;
    internal bool CanFetch => current != null;
    internal string LegacySelection => current != null && current == legacyScope && Models.Any(row => row.Id == legacyModel) ? legacyModel : "";

    internal void EnableMonitoring(Func<bool> visible) => canProbe = visible;

    internal void ScheduleCheck()
    {
        if (!autoProbe || current == null || canProbe?.Invoke() != true || lifetime.IsCancellationRequested ||
            !string.IsNullOrWhiteSpace(key.Text) && typedKeyScope != current)
            return;
        background?.Cancel();
        _ = ProbeAsync();
        async Task ProbeAsync()
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            background = request;
            var expected = revision;
            try
            {
                await Task.Delay(650, request.Token);
                if (canProbe?.Invoke() == true) await FetchAsync(request.Token);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception error) when (error is InvalidOperationException or FormatException or HttpRequestException or
                OperationCanceledException or JsonException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                if (expected == revision && !request.IsCancellationRequested) SetCheckStatus(Locale.Message(error.Message), failed: true);
            }
            finally { if (background == request) background = null; }
        }
    }

    internal async Task<int> FetchAsync(CancellationToken token)
    {
        if (current == null) throw new FormatException("Enter a complete API base URL.");
        var version = revision;
        fetch.IsEnabled = false;
        SetCheckStatus(Locale.T("Reading model catalog…"));
        try
        {
            var (scope, list) = address();
            using var client = new HttpClient(new HttpClientHandler { UseProxy = !list.IsLoopback, AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 4 * 1024 * 1024
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, list);
            if (credential(scope) is { Length: > 0 } key) request.Headers.Authorization = new("Bearer", key);
            using var response = await client.SendAsync(request, token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException("The API refused authorization. Check the key.");
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Catalog request failed (HTTP {(int)response.StatusCode}).");
            using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: token);
            token.ThrowIfCancellationRequested();
            if (version != revision) return 0;
            if (payload == null || !payload.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The API returned an invalid model catalog.");
            var entries = Entries();
            var known = entries.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
            efforts.Clear();
            var found = 0;
            foreach (var model in data.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("id", out var idValue) ||
                    idValue.ValueKind != JsonValueKind.String || idValue.GetString() is not { Length: > 0 } id) continue;
                if (images && model.TryGetProperty("architecture", out var architecture) && architecture.ValueKind == JsonValueKind.Object &&
                    architecture.TryGetProperty("output_modalities", out var outputs) && outputs.ValueKind == JsonValueKind.Array &&
                    !outputs.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "image")) continue;
                found++;
                if (!images && (model.TryGetProperty("supported_reasoning_efforts", out var levels) ||
                    model.TryGetProperty("supportedReasoningEfforts", out levels)) && levels.ValueKind == JsonValueKind.Array)
                    efforts[id] = levels.EnumerateArray().Select(level => level.ValueKind == JsonValueKind.String ? level.GetString()
                        : level.ValueKind == JsonValueKind.Object && level.TryGetProperty("reasoningEffort", out var value) &&
                          value.ValueKind == JsonValueKind.String ? value.GetString() : null).OfType<string>().Distinct().ToArray();
                if (known.Add(id) && entries.Count < 500)
                    entries.Add(new(id, model.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? id : id));
            }
            SetCheckStatus(Locale.T(found > 0 ? "The API responded. Models are available below." : "The API reported no models."),
                checkedConnection: true, hide: found > 0);
            Render();
            return found;
        }
        finally { fetch.IsEnabled = current != null; }
    }

    internal Dictionary<string, Dictionary<string, string>> Save()
    {
        var result = new Dictionary<string, Dictionary<string, string>>(saved, StringComparer.Ordinal);
        foreach (var (scope, entries) in drafts)
        {
            var models = entries.Where(row => !string.IsNullOrWhiteSpace(row.Id)).DistinctBy(row => row.Id.Trim())
                .ToDictionary(row => row.Id.Trim(), row => row.Name.Trim(), StringComparer.Ordinal);
            if (models.Count > 0) result[scope] = models;
            else result.Remove(scope);
        }
        return result;
    }

    private void SelectEndpoint()
    {
        InvalidateCheck();
        try { current = address().Credential.AbsoluteUri; }
        catch (FormatException) { current = null; }
        fetch.IsEnabled = add.IsEnabled = reset.IsEnabled = current != null;
        Render();
    }

    internal void InvalidateCheck()
    {
        revision++;
        background?.Cancel();
        efforts.Clear();
        SetCheckStatus("");
    }

    private void SetCheckStatus(string message, bool checkedConnection = false, bool failed = false, bool hide = false)
    {
        this.checkedConnection = checkedConnection;
        this.failed = failed;
        status.Text = message;
        status.IsVisible = !hide && message.Length > 0;
        CheckChanged?.Invoke();
    }

    private List<Entry> Entries()
    {
        if (!drafts.TryGetValue(current!, out var entries))
        {
            drafts[current!] = entries = saved.GetValueOrDefault(current!)?.Select(pair => new Entry(pair.Key, pair.Value)).ToList() ?? [];
            if (current == legacyScope && legacyModel.Length > 0 && !entries.Any(entry => entry.Id == legacyModel))
                entries.Add(new(legacyModel, ""));
        }
        return entries;
    }

    private void Render()
    {
        rows.Children.Clear();
        if (current == null) return;
        var entries = Entries();
        Grid CatalogRow(params Control[] fields)
        {
            var grid = Columns(compactSetup ? "*,*,26" : "*,*,35", fields);
            if (compactSetup) grid.ColumnSpacing = 8;
            return grid;
        }
        rows.Children.Add(compactSetup ? CatalogRow(SetupLabel("Model ID", translate: false), SetupLabel("Display name"))
            : CatalogRow(Caption("Model ID"), Caption("Display name")));
        foreach (var row in entries)
        {
            var id = new TextBox { Text = row.Id };
            var name = new TextBox { Text = row.Name };
            Avalonia.Automation.AutomationProperties.SetName(id, Locale.T("Model ID"));
            Avalonia.Automation.AutomationProperties.SetName(name, Locale.T("Display name"));
            id.TextChanged += (_, _) => { row.Id = id.Text ?? ""; Changed?.Invoke(); };
            name.TextChanged += (_, _) => { row.Name = name.Text ?? ""; Changed?.Invoke(); };
            var remove = Ui.IconButton(Icons.Trash, "Remove model", () => { entries.Remove(row); Render(); }, 14);
            remove.MinWidth = compactSetup ? 0 : 30;
            SetupInput(id);
            SetupInput(name);
            if (compactSetup)
            {
                remove.Padding = new Thickness(0);
                remove.Height = 30;
                remove.Width = 26;
                remove.HorizontalContentAlignment = HorizontalAlignment.Center;
                remove.VerticalContentAlignment = VerticalAlignment.Center;
                remove.VerticalAlignment = VerticalAlignment.Center;
            }
            rows.Children.Add(CatalogRow(id, name, remove));
        }
        Changed?.Invoke();
    }
}
