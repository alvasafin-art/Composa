# Object Selection

**Automatic · Local** is the default. Click a subject, drag a box around it, or choose **Find object** after marking an area with the marquee, lasso or Selection Brush. The bounds of the marked area guide recognition. A small object can be encoded with surrounding context at higher resolution. On a coloured backdrop, a box inside a thumbnail can target the subject inside that photograph.

EfficientSAM finds the object. The bundled ViTMatte estimates partial coverage along its boundary. Local foreground and background colour samples help resolve gaps in hair or fur. **Quick outline · Local** produces a crisp selection. The original active-layer RGB or the visible composition is sampled according to **Sample all layers**. An interrupted or stale result is discarded. Each completed selection is one undo step.

On Windows, EfficientSAM and ViTMatte use the GPU through DirectML by default. If the driver cannot load or run a model, Composa retries that model on the CPU. Turn off GPU acceleration in **Settings → Editor** to force CPU processing. On other platforms both models use the CPU.

## Moving a subject to another background

After **Automatic · Local** object selection, **Layer via Copy** (Ctrl+J), **Copy** and **Copy Merged** recover foreground colour where the photograph mixed the subject with the old backdrop. The new cutout carries the recovered RGB and the selection alpha. Already translucent input pixels keep their existing colour.

**Add Layer Mask** uses the selection as coverage and preserves the layer's original RGB, alpha and profile. To remove colour from the original backdrop in a separate cutout, open **Select and Mask** and raise **Decontaminate**. This selects **New layer with mask** and preserves the original pixels on their own layer. Use **Keep edge** to retain the current selection coverage when cleaning the colours.

BiRefNet variants appear when the connected ComfyUI server offers them. A click selects the entire server matte. A box sends the boxed area and places the returned coverage back into that area. This path preserves the server's soft coverage. It also supports explicit colour recovery in **Select and Mask**.

For examples, settings and a reproducible comparison of clicks and boxes, see the [Studio 16.7 object selection study (Russian)](https://github.com/alvasafin-art/Composa/blob/v1.3.0-preview.4.studio.16.7/docs/object-selection-study.md).
