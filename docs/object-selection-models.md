# Object Selection

**Automatic · Local** is the default. Click a subject, drag a box around it, or choose **Find object** after marking an area with the marquee, lasso or Selection Brush. The bounds of the marked area guide recognition. A small object can be encoded with surrounding context at higher resolution. On a coloured backdrop, a box inside a thumbnail can target the subject inside that photograph.

EfficientSAM finds the object. The bundled ViTMatte estimates partial coverage along its boundary. Local foreground and background colour samples help resolve gaps in hair or fur. **Quick outline · Local** produces a crisp selection. Both modes run on CPU using ONNX Runtime. The original active-layer RGB or the visible composition is sampled according to **Sample all layers**. An interrupted or stale result is discarded. Each completed selection is one undo step.

## Moving a subject to another background

After automatic object selection, **Layer via Copy** (Ctrl+J), **Copy** and **Copy Merged** recover foreground colour where the photograph mixed the subject with the old backdrop. The new cutout carries the recovered RGB and the selection alpha. Already translucent input pixels keep their existing colour.

Adding a mask to an ordinary raster layer from an automatic object selection keeps the mask editable. When RGB correction changes pixels and fits the document budget, a hidden copy of the original raster is kept below the layer. Disabling the mask reveals the corrected raster. The hidden original contains the unmodified photograph. Live layers use their regular mask. **Select and Mask** opens with colour recovery and new-layer output for an automatic object selection. Lower **Decontaminate** to adjust the effect. Manual feathered selections keep their sampled colours during ordinary copy operations.

BiRefNet variants appear when the connected ComfyUI server offers them. A server selection uses the configured server and keeps its model's coverage. Foreground colour recovery for the cutout runs in Composa.

For examples, settings and a reproducible comparison of clicks and boxes, see the [Studio 16.7 object selection study (Russian)](https://github.com/alvasafin-art/Composa/blob/v1.3.0-preview.4.studio.16.7/docs/object-selection-study.md).
