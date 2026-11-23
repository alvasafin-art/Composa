using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using SkiaSharp;

namespace Composa.App.Tests;

public sealed class DirectImageEditTests
{
    public static bool LiveCodexEdit => Environment.GetEnvironmentVariable("COMPOSA_LIVE_CODEX_EDIT") == "1";

    [Fact]
    public async Task Provider_endpoints_keep_the_same_model_id_and_encode_references_for_their_protocol()
    {
        const string model = "google/gemini-nano-banana-2.1";
        var files = new DirectImageFiles([new byte[] { 1, 2, 3 }]);
        var token = TestContext.Current.CancellationToken;
        var openRouter = DirectImageApi.Parse("https://openrouter.ai/api/v1");
        Assert.Equal("https://openrouter.ai/api/v1/images", openRouter.Endpoint(edit: true).AbsoluteUri);
        using var routerBody = openRouter.CreateBody(model, "edit", 1536, 1024, files);
        using var router = JsonDocument.Parse(await routerBody.ReadAsStringAsync(token));
        Assert.Equal(model, router.RootElement.GetProperty("model").GetString());
        Assert.Equal("3:2", router.RootElement.GetProperty("aspect_ratio").GetString());
        Assert.Equal("data:image/png;base64,AQID", router.RootElement.GetProperty("input_references")[0].GetProperty("image_url").GetProperty("url").GetString());

        var polza = DirectImageApi.Parse("https://polza.ai/api/v1/images/generations");
        Assert.Equal("https://polza.ai/api/v2/images/generations", polza.Endpoint(edit: false).AbsoluteUri);
        Assert.Equal("https://polza.ai/api/v1/media", polza.Endpoint(edit: true).AbsoluteUri);
        using var polzaBody = polza.CreateBody(model, "edit", 1024, 1024, files);
        using var media = JsonDocument.Parse(await polzaBody.ReadAsStringAsync(token));
        Assert.Equal(model, media.RootElement.GetProperty("model").GetString());
        var attachment = media.RootElement.GetProperty("input").GetProperty("images")[0];
        Assert.Equal("base64", attachment.GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,AQID", attachment.GetProperty("data").GetString());

        var compatible = DirectImageApi.Parse("https://api.selectel.ru/aig");
        Assert.Equal("https://api.selectel.ru/aig/v1/images/edits", compatible.Endpoint(edit: true).AbsoluteUri);
        Assert.True(compatible.UsesNativeMask);
    }

    [Fact]
    public async Task Polza_polls_the_media_task_and_downloads_the_result_without_forwarding_the_key()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var origin = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var api = DirectImageApi.Parse(origin + "/api/v1/media");
        using var swatch = new SKBitmap(8, 8);
        swatch.Erase(SKColors.OrangeRed);
        var png = DirectImageEdit.EncodePng(swatch);
        var task = api.GenerateAsync("fixture-key", "google/gemini-nano-banana-2.1", "Edit", 1024, 1024,
            new([png]), deadline.Token);
        await Respond("POST /api/v1/media", "fixture-key",
            JsonSerializer.SerializeToUtf8Bytes(new { id = "gen_test", status = "pending" }), "application/json");
        await Respond("GET /api/v1/media/gen_test", "fixture-key",
            JsonSerializer.SerializeToUtf8Bytes(new { status = "completed", data = new { url = origin + "/test-result.png" } }), "application/json");
        await Respond("GET /test-result.png", null, png, "image/png");
        using var result = await task;
        Assert.Equal(SKColors.OrangeRed, result.GetPixel(0, 0));

        async Task Respond(string expectedRequest, string? key, byte[] body, string mediaType)
        {
            var token = deadline.Token;
            using var connection = await listener.AcceptTcpClientAsync(token);
            await using var stream = connection.GetStream();
            using var input = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            Assert.Equal(expectedRequest + " HTTP/1.1", await input.ReadLineAsync(token));
            string? authorization = null;
            while (await input.ReadLineAsync(token) is { Length: > 0 } line)
                if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) authorization = line;
            Assert.Equal(key == null ? null : "Authorization: Bearer " + key, authorization);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {mediaType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), token);
            await stream.WriteAsync(body, token);
        }
    }

    [AvaloniaFact]
    public async Task Direct_api_sends_source_and_png_mask_and_preserves_pixels_outside_the_selection()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var endpoint = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/v1";
        var reply = ServeMaskedEditAsync(server, deadline.Token);
        var previous = Environment.GetEnvironmentVariable("COMPOSA_IMAGE_API_KEY");
        Environment.SetEnvironmentVariable("COMPOSA_IMAGE_API_KEY", "test-image-key");
        var window = new MainWindow();
        try
        {
            window.Show();
            var document = EditorSession.NewCanvas(256, 256, SKColors.CornflowerBlue);
            document.SelectRect(new(96, 96, 160, 160));
            window.AddSession(document);
            window.Settings.ImageGenerationRoute = ImageGenerationRoute.DirectApi;
            window.Settings.ImageGenerationApiUrl = endpoint;
            var before = document.History.CurrentId;
            await window.RunAiForTests(AiTaskKind.GenerativeFill, "Recolor the selected area orange.").WaitAsync(deadline.Token);
            await reply;
            Assert.Equal(AiOperationStatus.Completed, window.AiTasks.Operation?.Status);
            AssertSelectionAndUndo(document, before, SKColors.CornflowerBlue);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMPOSA_IMAGE_API_KEY", previous);
            window.Close();
        }
    }

    [AvaloniaFact(Skip = "Requires signed-in Codex CLI and sends only synthetic color shapes.", SkipUnless = nameof(LiveCodexEdit))]
    public async Task Codex_edits_a_synthetic_selection_with_a_color_reference()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            var document = EditorSession.NewCanvas(256, 256, SKColors.CornflowerBlue);
            document.SelectRect(new(96, 96, 160, 160));
            window.AddSession(document);
            window.Settings.ImageGenerationRoute = ImageGenerationRoute.LocalCodex;
            using var reference = new SKBitmap(128, 128);
            reference.Erase(SKColors.OrangeRed);
            var before = document.History.CurrentId;
            var request = new AiTaskRequest
            {
                Task = AiTaskKind.GenerativeFill, Prompt = "Fill the white mask region with the solid orange-red color of image 2.",
                ReferenceImages = [reference], ReferenceMegapixels = null, AlignMaskedEdits = false
            };
            await window.AiTasks.RunDirectImageAsync(new EditorCommandService(document), DirectImageGenerator.From(window.Settings),
                request, () => window.Sessions.Contains(document), TestContext.Current.CancellationToken);
            Assert.Equal(AiOperationStatus.Completed, window.AiTasks.Operation?.Status);
            var center = document.Composite().GetPixel(128, 128);
            Assert.True(center.Red > center.Blue, $"Edited pixel was {center}.");
            Assert.Contains(window.GetVisualDescendants().OfType<ComboBox>(),
                picker => picker.SelectedItem is ImageModelChoice choice && choice.Name == "gpt-image-2 · Codex CLI");
            Assert.True(Screenshots.Save(window, "direct-codex-mask-edit"));
            AssertSelectionAndUndo(document, before, SKColors.CornflowerBlue);
        }
        finally { window.Close(); }
    }

    private static void AssertSelectionAndUndo(EditorSession document, long before, SKColor background)
    {
        var layer = Assert.Single(document.Document.AllLayers().Where(layer => layer.Tags.Contains("ai-generated")));
        Assert.NotNull(layer.Mask);
        Assert.Equal((byte)0, layer.Mask.GetPixel(64, 128).Alpha);
        Assert.Equal((byte)255, layer.Mask.GetPixel(128, 128).Alpha);
        var composite = document.Composite();
        for (var y = 0; y < composite.Height; y++)
        for (var x = 0; x < composite.Width; x++)
        {
            if (document.Selection!.GetPixel(x, y).Alpha == 0)
                Assert.Equal(background, composite.GetPixel(x, y));
        }
        document.Undo();
        Assert.Equal(before, document.History.CurrentId);
        Assert.Equal(background, document.Composite().GetPixel(128, 128));
    }

    private static async Task ServeMaskedEditAsync(TcpListener server, CancellationToken token)
    {
        using var client = await server.AcceptTcpClientAsync(token);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.Latin1, leaveOpen: true);
        Assert.Equal("POST /v1/images/edits HTTP/1.1", await reader.ReadLineAsync(token));
        var length = 0;
        var authorized = false;
        while (await reader.ReadLineAsync(token) is { Length: > 0 } line)
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line["Content-Length:".Length..].Trim());
            if (line.Equals("Authorization: Bearer test-image-key", StringComparison.OrdinalIgnoreCase)) authorized = true;
        }
        Assert.True(authorized);
        Assert.InRange(length, 1, 256 * 1024);
        var body = new char[length];
        Assert.Equal(length, await reader.ReadBlockAsync(body.AsMemory(), token));
        var multipart = new string(body);
        Assert.Contains("filename=image-1.png", multipart.Replace("\"", ""));
        Assert.Contains("filename=mask.png", multipart.Replace("\"", ""));
        var maskStart = multipart.IndexOf("\r\n\r\n", multipart.IndexOf("mask.png", StringComparison.Ordinal), StringComparison.Ordinal) + 4;
        var maskEnd = multipart.IndexOf("\r\n--", maskStart, StringComparison.Ordinal);
        using var mask = SKBitmap.Decode(Encoding.Latin1.GetBytes(multipart[maskStart..maskEnd]));
        Assert.NotNull(mask);
        Assert.Equal((byte)0, mask.GetPixel(mask.Width / 2, mask.Height / 2).Alpha);
        Assert.Equal((byte)255, mask.GetPixel(0, 0).Alpha);

        using var generated = new SKBitmap(1024, 1024);
        generated.Erase(SKColors.OrangeRed);
        var png = DirectImageEdit.EncodePng(generated);
        var data = JsonSerializer.SerializeToUtf8Bytes(new { data = new[] { new { b64_json = Convert.ToBase64String(png) } } });
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n"), token);
        await stream.WriteAsync(data, token);
    }
}
