# Windows brush latency replay

Run in the logged-in desktop session. Leave the benchmark window and pointer alone while the script runs. Use the same display, DPI, .NET runtime and settings for both versions.

```powershell
./scripts/performance/Run-BrushLatency.ps1 -DataDirectory C:/temp/brush-latency -Label candidate -Repeat 10
./scripts/performance/Run-BrushLatency.ps1 -DataDirectory C:/temp/brush-latency -Label large-at-100 -BrushSize 500 -Zoom 100 -Repeat 5
```

For a baseline, copy this directory's source files and `Run-BrushLatency.ps1` to a separate checkout at the baseline revision and repeat with `-Label baseline`. The benchmark uses the built-in model files required by the regular app build.

Each process opens the Win32 editor at 1200 × 760 DIPs and opens a 4096 × 3072 RGB8 document. The simple document has one white raster. The layered document adds five translucent rasters and a 5 px outside stroke plus a shadow on the active layer. The defaults are fit, a 150 px brush and editor OpenCL off. Set `COMPOSA_PERF_EDITOR_GPU=1` to request the editor OpenCL path. Each process measures 32 alternating hover positions, the first dab, seven further isolated dabs and 12 moves in one continuous stroke. Use a separate label for every size and zoom.

Set `COMPOSA_PERF_FULL_OUTLINE=1` to compare the full-diameter contour. The replay reads the three adjacent hover pixels with a single GDI copy.

- `queueMs` measures local injection to Avalonia's routed tunnel handler. Hover uses `SetCursorPos`. Painting uses `PostMessage` on the test HWND.
- `inputMs` measures routing through the window's bubble handler. `diagnostics.Stages` includes instrumented brush setup, dabs, compositing and Skia drawing.
- `frameMs` measures local injection to the recorded Skia canvas draw, when the canvas drew after injection. `observedMs` measures local injection to a changed desktop pixel or a visible custom cursor at the destination. Native cursor samples set `nativeCursor` and leave both frame fields empty. Failed desktop probes leave `observedMs` empty and remain in the raw report.
- A remote session measures the Windows desktop and system cursor on the RDP host. Measuring the transport, decoder, client compositor and panel requires a client-side capture with synchronized timestamps or an external camera.

Pool each phase and document separately across processes. For each metric, report the number of observed values and the nearest-rank percentiles at indexes `ceil(0.50*N)-1` and `ceil(0.95*N)-1` in the sorted observations.
