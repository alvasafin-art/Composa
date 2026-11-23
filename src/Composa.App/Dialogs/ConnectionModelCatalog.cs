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
    private readonly TextBox selectedModel;
    private readonly StackPanel rows = Ui.Column(6);
    private readonly TextBlock status = Caption("");
    private readonly Button fetch;
    private readonly Button add;
    private readonly Button reset;
    private string? current;
    private int revision;
    internal Control View { get; }

    internal ConnectionModelCatalog(Dictionary<string, Dictionary<string, string>> saved,
        TextBox endpoint, TextBox key, TextBox selectedModel, Func<(Uri, Uri)> address,
        Func<Uri, string?> credential, CancellationToken lifetime, bool images = false)
    {
        this.saved = saved;
        this.address = address;
        this.credential = credential;
        this.selectedModel = selectedModel;
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
            if (current == null) return;
            var check = revision;
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
                if (credential(scope) is { Length: > 0 } token) request.Headers.Authorization = new("Bearer", token);
                using var response = await client.SendAsync(request, lifetime);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new InvalidOperationException("The API refused authorization. Check the key.");
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Catalog request failed (HTTP {(int)response.StatusCode}).");
                using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: lifetime);
                if (check != revision || lifetime.IsCancellationRequested) return;
                if (payload == null || !payload.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("The API returned an invalid model catalog.");
                var entries = Entries();
                var known = entries.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var model in data.EnumerateArray())
                {
                    if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("id", out var idValue) ||
                        idValue.ValueKind != JsonValueKind.String || idValue.GetString() is not { Length: > 0 } id) continue;
                    if (images && model.TryGetProperty("architecture", out var architecture) && architecture.ValueKind == JsonValueKind.Object &&
                        architecture.TryGetProperty("output_modalities", out var outputs) && outputs.ValueKind == JsonValueKind.Array &&
                        !outputs.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "image"))
                        continue;
                    if (known.Add(id) && entries.Count < 500)
                        entries.Add(new(id, model.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? id : id));
                }
                status.Text = Locale.T("The API responded. Models are available below.");
                Render();
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) when (error is InvalidOperationException or HttpRequestException or OperationCanceledException or JsonException or IOException)
            {
                if (check == revision) status.Text = Locale.Message(error.Message);
            }
            finally { fetch.IsEnabled = current != null; }
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

    internal void Refresh()
    {
        if (fetch.IsEnabled) fetch.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
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
            drafts[current!] = entries = saved.GetValueOrDefault(current!)?.Select(pair => new Entry(pair.Key, pair.Value)).ToList() ?? [];
        return entries;
    }

    private void Render()
    {
        rows.Children.Clear();
        if (current == null) return;
        var entries = Entries();
        const string widths = "*,*,135";
        rows.Children.Add(Columns(widths, Caption("Model ID"), Caption("Display name"), Caption("")));
        foreach (var row in entries)
        {
            var id = new TextBox { Text = row.Id };
            var name = new TextBox { Text = row.Name };
            id.TextChanged += (_, _) => row.Id = id.Text ?? "";
            name.TextChanged += (_, _) => row.Name = name.Text ?? "";
            var use = Ui.TextButton("Use", () =>
            {
                if (!string.IsNullOrWhiteSpace(row.Id)) selectedModel.Text = row.Id.Trim();
            });
            var remove = Ui.TextButton("Remove", () => { entries.Remove(row); Render(); });
            rows.Children.Add(Columns(widths, id, name, Ui.Row(5, use, remove)));
        }
    }
}
