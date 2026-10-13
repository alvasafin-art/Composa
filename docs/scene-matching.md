# Scene editing: Match to Scene, Relight and Change Background

Place the background below a transparent PNG and select the PNG layer. Choose the operation in the AI menu. PNG alpha supplies the object mask, including its placement and layer mask. For an opaque photograph, make a subject selection or install SAM Quality for automatic subject detection. Whole-image Relight does not need a subject mask.

Results are added as new layers. The original layer remains in the document, hidden when replaced. Generation, visibility and all variants undo together. Failed or cancelled generation inserts nothing. Editing during generation prevents insertion into a changed document.

## Match to Scene / Harmonize

Default **Match light + separate shadow** adapts illumination, exposure, color temperature and reflected light using the whole composition as context. The background is protected during insertion.

**Preserve source detail using a lighting map** transfers smooth lighting changes onto the original subject pixels. It retains the original silhouette and fine texture. Turn it off to accept the generated subject within the original alpha mask; identity and details may then change.

The separate **Contact and Cast Shadow** layer extracts model darkening on the permitted floor, discounting background exposure drift. It excludes the subject and keeps connected darkening near its base. It does not copy a regenerated background into the document. If no connected shadow is detected, the operation reports that and omits it.

**Infer light direction** uses a bright-region cue from the background or the Relight preset. Broad ambient light does not impose a direction. This is an image-space hint, not reconstructed depth or normals; clouds, reflections or multiple lights can mislead it. Disable inference to set **Light from** manually: degrees counterclockwise from image right, shadow falling the other way. **Floor reach** limits the permitted area; it does not guarantee a shadow of that length.

**Match light only** disables the shadow area. **Local tone and color · no AI** keeps the previous offline operation: two editable clipped Curves layers, Match Light and Match Color. This mode cannot create directional lighting or shadows.

## Relight

Choose **Subject** or **Whole visible image**, then a practical preset:

- Match surrounding light
- Soft studio from left or right
- Window light
- Overcast daylight
- Golden hour
- Backlight / rim
- Product studio
- Blue / magenta neon

**Strength** controls the transferred lighting. Detail preservation is enabled by default. Subject mode leaves the scene outside the subject and extracted shadow unchanged. Whole-image mode inserts a replacement above the composition and hides original root layers in the same undo step.

Presets guide an image-editing model. They are not draggable physical lights or a calibrated 3D renderer. Direction and material response are not guaranteed for every photo.

## Change Background

Describe the new environment. Composa removes the subject from the conditioning image, generates a clean background, then inserts **New Background** below **Original Subject**. The original cutout pixels and alpha are preserved; an opaque photo uses the selected or SAM-derived matte. Refine hair and translucent edges first if the matte is poor.

Replacement does not automatically relight the subject or guarantee matching perspective, scale or contact shadows. Run Match to Scene on Original Subject afterwards to integrate it into the new environment.

## Local FLUX workflow

Use FLUX.2 Klein on ComfyUI 0.37 or newer, with the model, Qwen encoder and FLUX.2 VAE selected in AI Settings. Refresh Models after configuring another model folder. Weights can stay in another ComfyUI installation: point the running server's extra-model-paths configuration to its models directory rather than duplicating them.

The bundled `scene-edit.json` uses **VAEEncode → SetLatentNoiseMask → KSampler** plus ReferenceLatent conditioning. It starts from the encoded image, rather than an empty latent or an encoder that blanks the object. Default denoise is **0.80** for lighting and **0.95** for background replacement. Denoise alone cannot preserve identity; the separate lighting-map insertion provides detail preservation.

Requests preserve proportions, pad only technical edges to multiples of 16 and remove padding before insertion. References upload once; variants run sequentially to limit memory. MP changes generation resolution without resizing the document. Original size observes the minimum generation area. Intel Auto mode uses CPU text encoding and tiled VAE; the first run may take several minutes.

GPT and Nano Banana use full-image editing with the same extraction. Comfy credits are required; denoise applies only to local FLUX. Availability, dimensions and quality are checked before upload. Paid providers were checked with simulated responses; live paid-provider image quality was not validated.

## Research and limits

Adobe describes [Harmonize](https://helpx.adobe.com/au/photoshop/desktop/repair-retouch/remove-objects-fill-space/blend-subjects-with-harmonize.html) as adapting an object's appearance, lighting and shadows to its background. This defines the intended workflow. Composa uses an independent FLUX pipeline; Photoshop visual parity is not established.

The official [ComfyUI FLUX.2 Klein editing template](https://github.com/Comfy-Org/workflow_templates/blob/main/templates/image_flux2_klein_image_edit_9b_base.json) supplies encoded-image reference conditioning. Composa adds an image latent, noise mask and separate extraction. General editing models can invent incorrect shadows, particularly with little visible floor. Direction cues and masks constrain output but do not solve scene geometry.

[IC-Light](https://github.com/lllyasviel/IC-Light) is a possible specialized relighting backend. It requires separate models/runtime and photographic validation and is not included here. A specialized shadow model or depth/normal approach remains a possible next step for harder scenes.
