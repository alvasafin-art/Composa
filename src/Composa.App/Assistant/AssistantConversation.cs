using Composa.AI;
using Composa.IO;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Assistant;

public sealed class AssistantConversation
{
    public List<AssistantChatEntry> Entries { get; } = [];
}

public sealed record AssistantChatEntry(string Role, string Text, string Script = "", string? Outcome = null)
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string AttachmentContext { get; init; } = "";
}

/// <summary>Only files explicitly attached in the chat become available to the assistant.</summary>
internal sealed record AssistantFile(string Path, AssistantAttachment Content)
{
    internal static async Task<AssistantFile> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Attachment not found.", path);
        var extension = file.Extension.ToLowerInvariant();
        if (extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp")
        {
            using var image = ImageFiles.Load(path);
            return new(path, new AssistantAttachment(file.Name, ImageDataUrl: Preview(image)));
        }
        if (extension is not (".js" or ".ts" or ".txt" or ".md" or ".json" or ".csv" or ".yaml" or ".yml" or ".svg"))
            throw new InvalidDataException("Attach a JavaScript, text/data file, or PNG/JPEG/WebP image.");
        if (file.Length > 256_000) throw new InvalidDataException("Text attachments must be smaller than 256 KB.");
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        if (text.Contains('\0')) throw new InvalidDataException("The attachment is not a text file.");
        return new(path, new AssistantAttachment(file.Name, Text: text));
    }

    internal static string Preview(SKBitmap source)
    {
        var scale = Math.Min(1, 1024.0 / Math.Max(source.Width, source.Height));
        using var preview = Pixels.NewColor(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
        using (var canvas = new SKCanvas(preview))
            canvas.DrawImage(Pixels.ImageOf(source), new SKRect(0, 0, preview.Width, preview.Height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var image = SKImage.FromBitmap(preview);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return "data:image/png;base64," + Convert.ToBase64String(encoded.ToArray());
    }
}
