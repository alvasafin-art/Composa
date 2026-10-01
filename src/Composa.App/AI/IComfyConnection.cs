using System.Text.Json.Nodes;
using Composa.AI;
using SkiaSharp;

namespace Composa.App.AI;

internal interface IComfyConnection : IDisposable
{
    ComfyServerAddress Address { get; }
    TimeSpan ConnectionTimeout { get; set; }
    Task<(ComfyServerInfo Info, ComfyServerCapabilities Capabilities)> TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<string> UploadPngAsync(string semantic, SKBitmap image, CancellationToken cancellationToken = default);
    Task<ComfyExecutionResult> ExecuteAsync(JsonObject workflow, IProgress<AiOperationState>? progress = null, CancellationToken cancellationToken = default);
    Task<SKBitmap> DownloadAsync(ComfyImageReference image, CancellationToken cancellationToken = default);
}
