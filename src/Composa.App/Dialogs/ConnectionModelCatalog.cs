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
    private readonly string legacyModel;
    private readonly string? legacyScope;
    private readonly StackPanel rows = Ui.Column(6);
    private readonly TextBlock status = Caption("");
    private readonly Button fetch;
    private readonly Button add;
    private readonly Button reset;
    private string? current;
    private int revision;
    internal Control View { get; }

    internal ConnectionModelCatalog(Dictionary<string, Dictionary<string, string>> saved,
        TextBox endpoint, TextBox key, TextBox selectedModel, Func<(Uri Credential, Uri Models)> address,
        Func<Uri, string?> credential, CancellationToken lifetime, bool images = false)
    {
        this.saved = saved;
        this.address = address;
        this.credential = credential;
        this.images = images;
        legacyModel = selectedModel.Text?.Trim() ?? "";
        try { legacyScope = address().Credential.AbsoluteUri; }
        catch (FormatException) { }
        fetch = Ui.TextButton("Get available models", () => { });
        add = Ui.TextButton("Add model", () => { if (current != null) { Entries().Add(new("", "")); Render(); } });
        reset = Ui.TextButton("Reset this connection’s catalog", () =>
        {
            if (current == null) return;
            drafts.Remove(current);
            Render();
        });
        fetch.Click += async (_, _) =>
        {
            try { await FetchAsync(lifetime); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) when (error is InvalidOperationException or FormatException or HttpRequestException or OperationCanceledException or JsonException or IOException)
            { status.Text = Locale.Message(error.Message); }
        };
        reset.Classes.Add("flat");
        fetch.Classes.Add("flat");
        add.HorizontalAlignment = HorizontalAlignment.Stretch;
        var toolbar = new WrapPanel { ItemSpacing = 5, LineSpacing = 4, Children = { reset, fetch } };
        View = Ui.Column(7, InsetRow(Columns("*,Auto", Heading("Models", 12), toolbar), vertical: 6), status, rows, add);
        endpoint.TextChanged += (_, _) => SelectEndpoint();
        key.TextChanged += (_, _) => { revision++; status.Text = ""; };
        SelectEndpoint();
    }

    internal IReadOnlyList<(string Id, string Label)> Models => current == null ? [] :
        Entries().Where(row => !string.IsNullOrWhiteSpace(row.Id)).DistinctBy(row => row.Id.Trim())
            .Select(row => (row.Id.Trim(), string.IsNullOrWhiteSpace(row.Name) ? row.Id.Trim() : row.Name.Trim())).ToArray();

    internal event Action? Changed;
    internal bool CanFetch => current != null;
    internal string LegacySelection => current != null && current == legacyScope && Models.Any(row => row.Id == legacyModel) ? legacyModel : "";

    internal async Task<int> FetchAsync(CancellationToken token)
    {
        if (current == null) throw new FormatException("Enter a complete API base URL.");
        var version = revision;
        fetch.IsEnabled = false;
        status.Text = Locale.T("Reading model catalog…");
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
            var found = 0;
            foreach (var model in data.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("id", out var idValue) ||
                    idValue.ValueKind != JsonValueKind.String || idValue.GetString() is not { Length: > 0 } id) continue;
                if (images && model.TryGetProperty("architecture", out var architecture) && architecture.ValueKind == JsonValueKind.Object &&
                    architecture.TryGetProperty("output_modalities", out var outputs) && outputs.ValueKind == JsonValueKind.Array &&
                    !outputs.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "image")) continue;
                found++;
                if (known.Add(id) && entries.Count < 500)
                    entries.Add(new(id, model.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? id : id));
            }
            status.Text = Locale.T(found > 0 ? "The API responded. Models are available below." : "The API reported no models.");
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
        revision++;
        try { current = address().Credential.AbsoluteUri; }
        catch (FormatException) { current = null; }
        fetch.IsEnabled = add.IsEnabled = reset.IsEnabled = current != null;
        status.Text = "";
        Render();
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
        const string widths = "*,*,35";
        rows.Children.Add(Columns(widths, Caption("Model ID"), Caption("Display name"), Caption("")));
        foreach (var row in entries)
        {
            var id = new TextBox { Text = row.Id };
            var name = new TextBox { Text = row.Name };
            Avalonia.Automation.AutomationProperties.SetName(id, Locale.T("Model ID"));
            Avalonia.Automation.AutomationProperties.SetName(name, Locale.T("Display name"));
            id.TextChanged += (_, _) => { row.Id = id.Text ?? ""; Changed?.Invoke(); };
            name.TextChanged += (_, _) => { row.Name = name.Text ?? ""; Changed?.Invoke(); };
            var remove = Ui.TextButton("", () => { entries.Remove(row); Render(); }, translate: false);
            remove.Content = Icons.Create(Icons.Trash, 14);
            remove.MinWidth = 30;
            Locale.Tip(remove, "Remove model");
            Avalonia.Automation.AutomationProperties.SetName(remove, Locale.T("Remove model"));
            rows.Children.Add(Columns(widths, id, name, remove));
        }
        Changed?.Invoke();
    }
}
