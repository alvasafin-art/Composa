# Windows brush latency replay

Run in the logged-in desktop session. Leave the benchmark window and pointer alone while the script runs. Use the same display, DPI, .NET runtime and settings for both versions.

```powershell
./scripts/performance/Run-BrushLatency.ps1 -DataDirectory C:/temp/brush-latency -Label candidate -Repeat 10
./scripts/performance/Run-BrushLatency.ps1 -DataDirectory C:/temp/brush-latency -Label large-at-100 -BrushSize 500 -Zoom 100 -Repeat 5
```

For a baseline, copy this directory's source files and `Run-BrushLatency.ps1` to a separate checkout at the baseline revision and repeat with `-Label baseline`. The benchmark uses the built-in model files required by the regular app build.

Each process opens the Win32 editor at 1200 × 760 DIPs and opens a 4096 × 3072 RGB8 document. The simple document has one white raster. The layered document adds five translucent rasters and a 5 px outside stroke plus a shadow on the active layer. The defaults are fit, a 150 px brush and editor OpenCL off. Set `COMPOSA_PERF_EDITOR_GPU=1` to request the editor OpenCL path. Each process measures 32 alternating hover positions, the first dab, seven further isolated dabs and 12 moves in one continuous stroke. Use a separate label for every size and zoom.

Set `COMPOSA_PERF_FULL_OUTLINE=1` to compare the full-diameter contour. The replay reads the three adjacent hover pixels with a single GDI copy.

Set `COMPOSA_PERF_OPAQUE_LAYER=1` to compare an opaque active layer with outside effects in the layered case. `COMPOSA_PERF_LOW_LATENCY_COMPOSITION=1` requests the optional DXGI composition mode with the standard fallbacks, only in the replay.

- `queueMs` measures local injection to Avalonia's routed tunnel handler. Hover uses `SetCursorPos`. Painting uses `PostMessage` on the test HWND.
- `inputMs` measures routing through the window's bubble handler. `diagnostics.Stages` includes instrumented brush setup, dabs, compositing and Skia drawing.
- `frameMs` measures local injection to the recorded Skia canvas draw, when the canvas drew after injection. `observedMs` measures local injection to a changed desktop pixel or a visible custom cursor at the destination. Native cursor samples set `nativeCursor` and leave both frame fields empty. Failed desktop probes leave `observedMs` empty and remain in the raw report.
- A remote session measures the Windows desktop and system cursor on the RDP host. Measuring the transport, decoder, client compositor and panel requires a client-side capture with synchronized timestamps or an external camera.

Pool each phase and document separately across processes. For each metric, report the number of observed values and the nearest-rank percentiles at indexes `ceil(0.50*N)-1` and `ceil(0.95*N)-1` in the sorted observations.

## Interaction in a normal Windows window

```powershell
dotnet build scripts/performance/BrushLatency -c Release -p:CanvasPerformance=true
$env:COMPOSA_PERF_EDITOR_GPU = '0'
$env:COMPOSA_PERF_SOFTWARE_PRESENTATION = '0'
$env:COMPOSA_PERF_LOW_LATENCY_COMPOSITION = '0'
dotnet scripts/performance/BrushLatency/bin/Release/net10.0/Composa.App.Tests.dll interaction C:/bench/interaction-default-off.json
```

Use a fresh process for each configuration. Set `COMPOSA_PERF_EDITOR_GPU=1` to request editor OpenCL, independently of the presentation mode. Set `COMPOSA_PERF_SOFTWARE_PRESENTATION=1` to force Avalonia's Win32 software renderer in the replay. Set `COMPOSA_PERF_LOW_LATENCY_COMPOSITION=1` to request LowLatencyDxgiSwapChain first with the normal fallback list. Repeat in reverse configuration order and keep the window visible. The replay creates disposable synthetic documents and keeps editor preferences in memory.

Wheel zoom enters at Avalonia's routed input boundary with Ctrl set explicitly. Pan, drags, panel hover and the brush control use Win32 messages sent to the replay window. The native stack uses explicit invalidation to isolate composition. Its warm samples intentionally reproduce the same pixels. The returned `events` and `draws` count routed events and canvas draw callbacks. `canvasDrawMs` ends at the first canvas draw callback after input arrives. `desktopPixelMs` detects a color change at a GDI probe point and can be absent if the point shows the same color. It represents observation of the Windows desktop, including the host desktop when the session runs over RDP. Set `COMPOSA_PERF_SKIP_DESKTOP_PROBE=1` to check whether GDI polling affects the callback timing.
