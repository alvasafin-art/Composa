// Ported from Lolly (github.com/lolly-tools/lolly, packages/node-shell/src/ml/session.ts at 12b26ff), MPL-2.0, used under the MIT licence by permission of Andy Fitzsimon, 2026-09-30.
using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Composa.Vision;

/// <summary>
/// Caches verified ONNX sessions. Local selection models can use DirectML on Windows.
/// </summary>
public static class ModelRunner
{
    /// <summary>The thread count every session uses. Fixed, rather than every core, so a result is the same from one run to the next.</summary>
    public static readonly int Threads = Math.Clamp(Environment.ProcessorCount, 1, 8);

    private static readonly Lazy<bool> available = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Dictionary<string, InferenceSession> sessions = [];
    private static readonly HashSet<string> verified = [];
    private static readonly Lock gate = new();

    /// <summary>Whether the runtime loads on this machine. Decided once; a failure is remembered so nothing probes again.</summary>
    public static bool IsAvailable => available.Value;

    /// <summary>Whether this model can run here: the runtime loads and the file is on this machine.</summary>
    public static bool CanRun(OnnxModel model) => IsAvailable && model.IsInstalled;

    private static bool Probe()
    {
        try
        {
            using var options = new SessionOptions();
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException or OnnxRuntimeException or BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs the model on a normalized [1,3,edge,edge] tensor and returns its first output, edge by edge floats.
    /// The file is hashed the first time it is loaded; a file that is not the one the catalog names is refused.
    /// A cancellation stops the run itself, not only the stages around it.
    /// </summary>
    /// <exception cref="InvalidDataException">The model file is missing or is not the file the catalog names.</exception>
    public static float[] Run(SubjectModel model, float[] input, int edge, CancellationToken cancellation = default)
    {
        var (output, _, _) = RunImage(model, input, edge, edge, cancellation);
        if (output.Length != edge * edge) throw new InvalidDataException($"The {model.Name} model answered with {output.Length} values for {edge}×{edge} pixels.");
        return output;
    }

    /// <summary>
    /// Runs a model that takes a [1,C,height,width] tensor and answers with planes of its own size, as the upscalers
    /// do: the first output's values with its width and height. The same loading, hashing and cancellation rules as
    /// <see cref="Run"/>.
    /// </summary>
    public static (float[] Planes, int Width, int Height) RunImage(OnnxModel model, float[] input, int width, int height, CancellationToken cancellation = default)
    {
        return Execute(model, session => RunImage(session, input, width, height, cancellation), cancellation);
    }

    private static (float[] Planes, int Width, int Height) RunImage(InferenceSession session, float[] input,
        int width, int height, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var channels = input.Length / (width * height);
        var tensor = new DenseTensor<float>(input, [1, channels, height, width]);
        using var runOptions = new RunOptions();
        using var stop = cancellation.Register(() => runOptions.Terminate = true);
        try
        {
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(session.InputNames[0], tensor)], [session.OutputNames[0]], runOptions);
            var output = results[0].AsTensor<float>();
            var dims = output.Dimensions;
            return (output.ToArray(), dims[^1], dims[^2]);
        }
        catch (OnnxRuntimeException) when (cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellation);
        }
    }

    private sealed class GpuModel
    {
        internal readonly SemaphoreSlim Access = new(1);
        internal InferenceSession? Session;
        internal volatile string? Failure;
        internal volatile bool Ran;
    }

    private static readonly ConcurrentDictionary<(string Path, string Hash), GpuModel> gpuModels = new();
    private static int preferGpu = 1;
    public static bool PreferGpu
    {
        get => Volatile.Read(ref preferGpu) != 0;
        set
        {
            if (Interlocked.Exchange(ref preferGpu, value ? 1 : 0) == 0 || value) return;
            // Release VRAM after any current run finishes without blocking the settings window.
            foreach (var gpu in gpuModels.Values)
                _ = Task.Run(async () =>
                {
                    await gpu.Access.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (PreferGpu) return;
                        gpu.Session?.Dispose();
                        gpu.Session = null;
                        gpu.Ran = false;
                        gpu.Failure = null;
                    }
                    finally { gpu.Access.Release(); }
                });
        }
    }

    public static string? DeviceFor(OnnxModel model)
    {
        if (PreferGpu && gpuModels.TryGetValue((model.Path, model.Sha256), out var gpu))
        {
            if (gpu.Failure != null) return "CPU (" + gpu.Failure + ")";
            if (gpu.Ran) return "DirectML";
        }
        return PreferGpu && OperatingSystem.IsWindows() ? null : "CPU";
    }

    internal static T Execute<T>(OnnxModel model, Func<InferenceSession, T> run, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows() && PreferGpu && (model is PromptModelFile || model == RefinementModels.VitMatte))
        {
            var gpu = gpuModels.GetOrAdd((model.Path, model.Sha256), _ => new());
            // DirectML allows only one Run at a time on each session.
            gpu.Access.Wait(cancellation);
            try
            {
                if (PreferGpu && gpu.Failure == null)
                {
                    try
                    {
                        gpu.Session ??= CreateSession(model, directML: true);
                        var result = run(gpu.Session);
                        gpu.Ran = true;
                        return result;
                    }
                    catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        gpu.Failure = ex.Message;
                        gpu.Session?.Dispose();
                        gpu.Session = null;
                    }
                }
            }
            finally { gpu.Access.Release(); }
        }
        cancellation.ThrowIfCancellationRequested();
        return run(Session(model));
    }

    internal static void Prepare(OnnxModel model, CancellationToken cancellation) =>
        Execute(model, static _ => true, cancellation);

    private static InferenceSession Session(OnnxModel model)
    {
        lock (gate)
        {
            var key = model.Path + "|" + model.Sha256;
            if (sessions.TryGetValue(key, out var existing)) return existing;
            return sessions[key] = CreateSession(model, directML: false);
        }
    }

    private static InferenceSession CreateSession(OnnxModel model, bool directML)
    {
        lock (gate)
        {
            var key = model.Path + "|" + model.Sha256;
            if (!model.IsInstalled) throw new InvalidDataException($"The {model.Name} model is not installed: {model.Path} is missing.");
            if (!verified.Contains(key))
            {
                if (!model.Verify()) throw new InvalidDataException($"{model.Path} is not the {model.Name} model Composa was built with, so it is not used.");
                verified.Add(key);
            }
        }
        using var options = new SessionOptions
        {
            IntraOpNumThreads = Threads,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };
        if (directML)
        {
            options.EnableMemoryPattern = false;
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.AppendExecutionProvider_DML(0);
        }
        return new InferenceSession(model.Path, options);
    }
}
