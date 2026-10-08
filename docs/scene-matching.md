# Match to Scene

Match to Scene automatically matches the visible raster layer to the surrounding composition. It runs locally and creates two editable clipped Curves layers, **Match Light** and **Match Color**, in one undo step. The source pixels remain unchanged.

The analysis renders the layer with its placement and mask, and samples the nearby visible scene. A selection restricts both the target sample and the resulting correction. Without a selection, the target is excluded from the background sample. Transparent pixels and hidden mask content are excluded.

Brightness uses robust luminance medians and a bounded smooth tone curve. Color balance uses sufficiently neutral midtones, with limited channel gains; saturated materials do not define a false neutral cast. The curves preserve black and white endpoints. Background texture is never converted into added grain; no blur or noise filter is inserted.

Public Match Color behavior describes layer/merged-scene sampling, luminance, color cast and selection scope. These informed the requirements for this implementation:

- [Match color between two images](https://helpx.adobe.com/photoshop/desktop/adjust-color/selective-color-adjustments/match-color-between-two-images.html)
- [Match color of two layers](https://helpx.adobe.com/photoshop/desktop/adjust-color/selective-color-adjustments/match-color-of-two-layers-in-the-same-image.html)

Local tone/color matching preserves existing geometry, texture and lighting structure. It cannot synthesize a missing shadow or change the direction of light; use the separate AI Harmonize or Relight operation when that is required. Insufficient visible context produces an explanation and leaves the document unchanged.

Validation covers a textured background with a flat subject, translated masked layers, saturated objects, neutral cast reduction, monotone curves, unchanged pixels outside the selection, immutable source pixels and undo/redo. This avoids deterministic added artifacts; scene plausibility still depends on the source photographs and their lighting.
