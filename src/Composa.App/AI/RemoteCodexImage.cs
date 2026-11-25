using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace Composa.App.AI;

internal static class RemoteCodexImage
{
    internal const string Node = "AVSPromptHelper";
    internal const string Preview = "image_result";
    private const string DiscoveryPath = "avs-prompt/image-cli";
    private const int Protocol = 1;

    internal sealed record Model(string Id, string Name, string[] Reasoning,
        [property: JsonPropertyName("is_default")] bool IsDefault)
    {
        public override string ToString() => Name == Id ? Id : $"{Name} · {Id}";
    }

    internal sealed record Catalog(int Protocol, string Version, string Adapter, Model[] Models);
    private sealed record ImageRequest(int Version, string Operation, int Width, int Height,
        [property: JsonPropertyName("request_id")] Guid Id);

    internal static async Task<Catalog> DiscoverAsync(string server, CancellationToken token)
    {
        var address = ComfyServerAddress.Parse(server);
        using var client = Http(address);
        using var result = await client.GetAsync(address.Api(DiscoveryPath), token);
        if (result.StatusCode == HttpStatusCode.NotFound)
        {
            using var node = await client.GetAsync(address.Api($"object_info/{Node}"), token);
            node.EnsureSuccessStatusCode();
            var info = await node.Content.ReadFromJsonAsync<JsonObject>(token);
            throw new InvalidOperationException(info?.ContainsKey(Node) == true
                ? "Update AVS Prompt Helper on the ComfyUI server to use CLI image generation."
                : "Install AVS Prompt Helper on the ComfyUI server to use CLI image generation.");
        }
        if (result.StatusCode == HttpStatusCode.BadRequest &&
            await result.Content.ReadFromJsonAsync<JsonObject>(token) is { } error &&
            error["error"]?.GetValue<string>() is { Length: > 0 } reason)
            throw new InvalidOperationException(reason);
        result.EnsureSuccessStatusCode();
        var catalog = await result.Content.ReadFromJsonAsync<Catalog>(token);
        if (catalog is not { Protocol: Protocol, Adapter: "codex", Models: { Length: > 0 } } ||
            catalog.Models.Any(model => string.IsNullOrWhiteSpace(model.Id) || model.Reasoning == null))
            throw new InvalidOperationException("The ComfyUI server returned an incompatible CLI image catalogue.");
        return catalog;
    }

    internal static async Task<SKBitmap> GenerateAsync(string server, string model, string effort, string prompt, int width,
        int height, DirectImageFiles? files, bool edit, CancellationToken token)
    {
        var catalog = await DiscoverAsync(server, token);
        var selected = string.IsNullOrWhiteSpace(model) ? catalog.Models.FirstOrDefault(item => item.IsDefault)
            : catalog.Models.FirstOrDefault(item => item.Id == model);
        if (selected == null)
            throw new InvalidOperationException("Choose an available ComfyUI server language model in AI Settings → CLI.");
        if (effort != "default" && !selected.Reasoning.Contains(effort))
            throw new InvalidOperationException("Choose a reasoning effort offered by the ComfyUI server model.");

        using var client = new ComfyClient(server);
        var names = new List<string>();
        foreach (var png in files?.Images ?? [])
            names.Add(await client.UploadPngAsync("cli-image", png, token));
        var id = Guid.NewGuid();
        var graph = Workflow(selected.Id, effort, prompt, width, height, names, edit, id);
        try
        {
            var result = await client.ExecuteAsync(graph, null, token, interruptOnCancel: false);
            using var history = result.History;
            var images = result.Images.Where(item => item.NodeId == Preview).ToArray();
            if (images.Length != 1)
                throw new InvalidDataException("The ComfyUI server did not return one CLI image.");
            return await client.DownloadAsync(images[0], token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            using var cancel = Http(client.Address);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                using var response = await cancel.PostAsync(client.Address.Api($"{DiscoveryPath}/cancel/{id:D}"), null, deadline.Token);
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
            throw;
        }
    }

    internal static JsonObject Workflow(string model, string effort, string prompt, int width, int height,
        IReadOnlyList<string> inputs, bool edit, Guid id)
    {
        var state = new
        {
            version = 1,
            prompt,
            settings = new { adapter = "codex", model, reasoning = effort == "default" ? "auto" : effort,
                output_mode = "text_and_image", timeout = 240 },
            image_request = new ImageRequest(Protocol, edit ? "edit" : "generate", width, height, id)
        };
        var helper = new JsonObject { ["state"] = JsonSerializer.Serialize(state, JsonSerializerOptions.Web) };
        var graph = new JsonObject
        {
            ["cli_image"] = new JsonObject { ["class_type"] = Node, ["inputs"] = helper },
            [Preview] = new JsonObject { ["class_type"] = "PreviewImage",
                ["inputs"] = new JsonObject { ["images"] = new JsonArray("cli_image", 1) } }
        };
        for (var index = 0; index < inputs.Count; index++)
        {
            var name = $"image_{index + 1}";
            graph[name] = new JsonObject { ["class_type"] = "LoadImage", ["inputs"] = new JsonObject { ["image"] = inputs[index] } };
            helper[name] = new JsonArray(name, 0);
        }
        return graph;
    }

    private static HttpClient Http(ComfyServerAddress address) => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = ComfyServerAddress.UseProxy(address.HttpBase)
    }) { Timeout = TimeSpan.FromSeconds(65) };
}
