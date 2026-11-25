using System.Runtime.InteropServices;
using Composa.AI;
using Composa.App.AI;
using Composa.ColorManagement;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.App.Tests;

public class SelectionMaskPipelineTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "SelectionFixtures", name + ".png");
    private static byte[] Bytes(SKBitmap bitmap) => bitmap.GetPixelSpan().ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BiRefNet_replays_the_pre_SAM_coverage_and_adds_a_color_preserving_mask(bool click)
    {
        using var source = SKBitmap.Decode(Fixture("birefnet-source"));
        using var response = SKBitmap.Decode(Fixture("birefnet-response"));
        var session = Scene(source);
        var server = new AiVariantsTests.Connection { DownloadImage = () => SKBitmap.Decode(Fixture("birefnet-response")) };
        var service = new AiTaskService(() => server.Address.ToString(), Path.Combine(AppContext.BaseDirectory, "ai", "engines"), _ => server);
        await service.RunAsync(new EditorCommandService(session), new AiTaskRequest
        {
            Task = AiTaskKind.ObjectSelection,
            SelectionRegion = session.Document.Bounds,
            SelectionPoint = click ? new SKPointI(130, 140) : null
        }, TestContext.Current.CancellationToken);

        var graph = Assert.Single(server.Graphs);
        Assert.Equal("MaskToImage", graph["image"]!["class_type"]!.GetValue<string>());
        Assert.Equal("birefnet.safetensors", graph["model"]!["inputs"]!["bg_removal_name"]!.GetValue<string>());
        var mask = Assert.IsType<SKBitmap>(session.Selection);
        // The ced1ce8 decoder reduces an opaque grayscale PNG to luma with weights
        // 54, 183, 19. As those sum to 256, the input gray byte is the expected alpha.
        var expected = new byte[mask.RowBytes * mask.Height];
        for (int y = 0; y < response.Height; y++)
        for (int x = 0; x < response.Width; x++)
            expected[y * mask.RowBytes + x] = response.GetPixel(x, y).Red;
        Assert.Equal(expected, Bytes(mask));
        Assert.Equal(254, mask.GetPixel(360, 140).Alpha); // The second object survives a click on the first.
        AssertPlainMask(session);
        Assert.False(session.HasForegroundSelection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BiRefNet_search_upload_keeps_source_samples_on_the_original_grid(bool click)
    {
        using var source = Pixels.NewColor(22, 16);
        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
                source.SetPixel(x, y, (x + y) % 2 == 0 ? SKColors.Black : SKColors.White);
        Pixels.Invalidate(source);
        var session = Scene(source);
        using var composite = session.Flatten();
        using var expected = Pixels.DisplayCopy(composite);
        var bounds = click ? session.Document.Bounds : new SKRectI(3, 5, 14, 12);
        var server = new AiVariantsTests.Connection
        {
            InspectUpload = (semantic, upload) =>
            {
                Assert.Equal("sourceImage", semantic);
                // Check the encoded PNG too: an extracted view retains the full source's stride.
                using var png = SKBitmap.Decode(ComfyClient.EncodeUploadPng(upload));
                Assert.Equal(bounds.Width, png.Width);
                Assert.Equal(bounds.Height, png.Height);
                for (var y = 0; y < png.Height; y++)
                    for (var x = 0; x < png.Width; x++)
                        Assert.Equal(expected.GetPixel(x + bounds.Left, y + bounds.Top), png.GetPixel(x, y));
            },
            DownloadImage = () =>
            {
                var response = Pixels.NewColor(bounds.Width, bounds.Height);
                response.Erase(new SKColor(173, 173, 173));
                return response;
            }
        };
        var service = new AiTaskService(() => server.Address.ToString(), Path.Combine(AppContext.BaseDirectory, "ai", "engines"), _ => server);
        await service.RunAsync(new EditorCommandService(session), new AiTaskRequest
        {
            Task = AiTaskKind.ObjectSelection, SelectionRegion = bounds, SelectionPoint = click ? new SKPointI(8, 7) : null
        }, TestContext.Current.CancellationToken);

        Assert.Single(server.Uploads);
        var mask = Assert.IsType<SKBitmap>(session.Selection);
        for (var y = 0; y < mask.Height; y++)
            for (var x = 0; x < mask.Width; x++)
                Assert.Equal(bounds.Contains(x, y) ? 173 : 0, mask.GetPixel(x, y).Alpha);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public async Task Comfy_selection_reads_cutout_alpha_for_dark_opaque_pixels_and_the_soft_edge(int width)
    {
        using var source = Pixels.NewColor(width, 4);
        source.Erase(SKColors.White);
        var session = Scene(source);
        var server = new AiVariantsTests.Connection
        {
            DownloadImage = () =>
            {
                var cutout = Pixels.NewColor(4, 4);
                for (var y = 0; y < cutout.Height; y++)
                {
                    cutout.SetPixel(0, y, SKColors.Black);
                    cutout.SetPixel(1, y, SKColors.Navy);
                    cutout.SetPixel(2, y, new SKColor(255, 255, 255, 128));
                }
                Pixels.Invalidate(cutout);
                return cutout;
            }
        };
        var service = new AiTaskService(() => server.Address.ToString(), Path.Combine(AppContext.BaseDirectory, "ai", "engines"), _ => server);
        await service.RunAsync(new EditorCommandService(session), new AiTaskRequest { Task = AiTaskKind.SelectSubject },
            TestContext.Current.CancellationToken);

        var mask = Assert.IsType<SKBitmap>(session.Selection);
        Assert.Equal(255, mask.GetPixel(0, 1).Alpha);
        Assert.Equal(255, mask.GetPixel(width / 4, 1).Alpha);
        Assert.Equal(width == 4 ? 128 : 96, mask.GetPixel(width == 4 ? 2 : 5, 1).Alpha);
        Assert.Equal(0, mask.GetPixel(width - 1, 1).Alpha);
    }

    [Fact]
    public async Task Local_SAM_adds_a_color_preserving_mask_after_precise_edges()
    {
        using var source = Pixels.NewColor(160, 120);
        source.Erase(SKColors.ForestGreen);
        using (var canvas = new SKCanvas(source))
        using (var paint = new SKPaint { Color = SKColors.Bisque, IsAntialias = true })
            canvas.DrawOval(new(39.5f, 19.5f, 120.5f, 100.5f), paint);
        var session = Scene(source);
        Assert.True(await session.SelectPromptObjectAsync(PromptModels.EfficientSamS, null, new(30, 10, 130, 110),
            SelectionMode.Replace, TestContext.Current.CancellationToken, preciseEdges: true));
        Assert.True(session.HasForegroundSelection);
        Assert.Equal(255, session.Selection!.GetPixel(80, 60).Alpha);
        AssertPlainMask(session);
    }

    private static EditorSession Scene(SKBitmap source)
    {
        var document = new Document(source.Width, source.Height) { Depth = ColorDepth.Sixteen, Profile = RgbProfile.DisplayP3 };
        var background = Pixels.NewWorkingColor(source.Width, source.Height, document.Depth, document.Profile);
        background.Erase(SKColors.White);
        document.Layers.Add(Layer.Raster("White", background));
        var session = new EditorSession(document);
        var working = Pixels.ConvertWorking(source, document.Depth, document.Profile);
        // Preserve a non-opaque source pixel as well as the fully opaque source RGB.
        var corner = MemoryMarshal.Cast<byte, float>(working.GetPixelSpan())[..4];
        for (int channel = 0; channel < corner.Length; channel++) corner[channel] *= .375f;
        Pixels.Invalidate(working);
        session.AddImageLayer("Source", working, new(source.Width / 2f, source.Height / 2f), fit: false);
        return session;
    }

    private static void AssertPlainMask(EditorSession session)
    {
        var layer = session.ActiveLayer!;
        var original = layer.Pixels!;
        var sourceBytes = Bytes(original);
        Assert.Equal(.375f, MemoryMarshal.Cast<byte, float>(original.GetPixelSpan())[3]);
        var selection = Bytes(session.Selection!);
        var layerCount = session.Document.Layers.Count;
        var state = session.History.CurrentId;
        using var before = session.Flatten();
        // CMPS stores RGB16 samples at 16-bit precision. Compare both saved versions
        // to isolate the mask from the format's normal float-to-16-bit quantization.
        var savedWithoutMask = Reopen(session.Document);
        using var reopenedBefore = savedWithoutMask.Flatten();

        session.AddMask(layer);
        Assert.Same(original, layer.Pixels);
        Assert.Equal(sourceBytes, Bytes(layer.Pixels!));
        Assert.Equal(layerCount, session.Document.Layers.Count);
        Assert.Equal(selection, Bytes(layer.Mask!));
        Assert.Null(session.Selection);
        Assert.Same(layer.Mask, session.EditingMaskBitmap);
        Assert.True(layer.MaskEnabled);
        Assert.Equal(RgbProfile.DisplayP3, Pixels.ProfileOf(layer.Pixels!));
        Assert.Equal(SKColorType.RgbaF32, layer.Pixels!.ColorType);
        using var masked = session.Flatten();
        AssertCompositeCoverage(before, masked, layer.Mask!);

        var reopened = Reopen(session.Document);
        var savedLayer = reopened.Document.Find(layer.Id)!;
        Assert.Equal(Bytes(savedWithoutMask.Document.Find(layer.Id)!.Pixels!), Bytes(savedLayer.Pixels!));
        Assert.Equal(RgbProfile.DisplayP3, Pixels.ProfileOf(savedLayer.Pixels!));
        Assert.Equal(selection, Bytes(savedLayer.Mask!));
        Assert.True(savedLayer.MaskEnabled);
        using var reopenedComposite = reopened.Flatten();
        AssertCompositeCoverage(reopenedBefore, reopenedComposite, savedLayer.Mask!);
        reopened.SetMaskEnabled(savedLayer, false);
        using var reopenedUnmasked = reopened.Flatten();
        Assert.Equal(Bytes(reopenedBefore), Bytes(reopenedUnmasked));

        session.SetMaskEnabled(layer, false);
        using var disabled = session.Flatten();
        Assert.Equal(Bytes(before), Bytes(disabled));
        session.Undo(); // Re-enable.
        Assert.True(session.ActiveLayer!.MaskEnabled);
        session.Undo(); // Remove the mask.
        Assert.Equal(state, session.History.CurrentId);
        Assert.Null(session.ActiveLayer!.Mask);
        Assert.Equal(selection, Bytes(session.Selection!));
        Assert.Same(original, session.ActiveLayer.Pixels);
        using var undone = session.Flatten();
        Assert.Equal(Bytes(before), Bytes(undone));
    }

    private static EditorSession Reopen(Document document)
    {
        using var cmps = new MemoryStream();
        ProjectFile.Write(document, cmps);
        cmps.Position = 0;
        return new EditorSession(ProjectFile.Read(cmps));
    }

    private static void AssertCompositeCoverage(SKBitmap before, SKBitmap after, SKBitmap mask)
    {
        var original = MemoryMarshal.Cast<byte, float>(before.GetPixelSpan());
        var composite = MemoryMarshal.Cast<byte, float>(after.GetPixelSpan());
        var coverage = mask.GetPixelSpan();
        var hasFringe = false;
        for (int y = 0; y < mask.Height; y++)
        for (int x = 0; x < mask.Width; x++)
        {
            var alpha = coverage[y * mask.RowBytes + x];
            var index = y * (before.RowBytes / sizeof(float)) + 4 * x;
            if (alpha == 255)
                Assert.True(original.Slice(index, 4).SequenceEqual(composite.Slice(index, 4)));
            else if (alpha > 0)
            {
                var weight = alpha / 255f;
                for (int channel = 0; channel < 3; channel++)
                    Assert.InRange(composite[index + channel], original[index + channel] * weight + 1 - weight - .005f,
                        original[index + channel] * weight + 1 - weight + .005f);
                Assert.Equal(1f, composite[index + 3]);
                hasFringe = true;
            }
        }
        Assert.True(hasFringe);
    }
}
