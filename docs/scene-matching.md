# Scene editing: Match to Scene, Relight and Change Background

Place the background below a transparent PNG and select the PNG layer. Choose the operation in the AI menu. PNG alpha supplies the object mask, including its placement and layer mask. For an opaque photograph, make a subject selection or install SAM Quality for automatic subject detection. Whole-image Relight does not need a subject mask.

Results are added as new layers. The original layer remains in the document, hidden when replaced. Generation, visibility and all variants undo together. Failed or cancelled generation inserts nothing. Editing during generation prevents insertion into a changed document.

## Match to Scene / Harmonize

Default **Match light + separate shadow** adapts illumination, exposure, color temperature and reflected light using the whole composition as context. The background is protected during insertion.

Local FLUX Harmonize uses **`integrate object into scene`**. The complete composited canvas supplies the visual context; there is no automatic second background reference, generated scene description, generic workflow guidance or negative prompt. Optional text typed in this dialog is appended only when it differs from that instruction; an explicit manual light angle also remains available. Subject Relight keeps its preset and separate background reference.

**Preserve source detail using a lighting map** transfers smooth lighting changes onto the original subject pixels. It retains the original silhouette and fine texture. Turn it off to accept the generated subject within the original alpha mask; identity and details may then change.

The separate **Contact and Cast Shadow** layer extracts ground darkening against the input's VAE reconstruction, discounting background drift and foreground lighting. PNG alpha attenuates an underlaid shadow once: its antialiased base is not cut out a second time. Significant cast components are registered to the original support; weak specks cannot certify a detached main lobe. Strongly displaced or unrelated darkening is rejected. This corrects the shadow, never the PNG, and does not copy a regenerated background into the document. Shape and physical perspective remain model-dependent.

**Use scene / preset light direction** lets the model interpret the complete scene or uses the Relight preset. Subject Relight also sends the entire background alone as a separate lighting reference. Bright pixels are not treated as calibrated light coordinates. Disable automatic direction to set **Light from** manually: degrees counterclockwise from image right, shadow falling the other way. **Canvas reach** limits the allowed distance in canvas units; 100% covers the canvas in every direction. Shadows are not cropped to a small rectangle below the object. Their full connected penumbra is retained.

Repeated subject editing uses its original PNG and excludes that subject's previous lighting and shadow layers from the inputs. It retains the current placement and the current visible background (including a chosen replacement background). Previous results remain recoverable through Undo; they are hidden together when the new result is committed. Hidden layers and hidden variants are excluded. Source and placement links travel with the project in layer tags. Older releases' nested results also retire their ancestor shadows when the source can be recovered.

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

Describe the environment, support surface and lighting; for example: **"A beige photography studio, matte stone tabletop, soft window light from the left."** The editor supplies the object's normalized bounds and support position automatically. The first pass sees the actual PNG at that placement. The second sees the actual arranged scene and receives a direct instruction to remove the main foreground object while keeping surrounding objects, perspective and lighting. Its mask follows a padded silhouette instead of erasing the bounding rectangle, preserving context between separate parts and beside diagonal objects. Native cleanup uses the encoded arranged image with full denoise inside that mask; the surrounding latent remains context. It does not put a gray silhouette into the reference. The complete decoded plate is used directly: no regenerated rectangle or source-object reconstruction residual is pasted into it. **New Background** and **Original Subject** are inserted together. Original cutout pixels and alpha are preserved; an opaque photo uses its selection or SAM matte. Refine poor hair/translucent masks first.

Replacement can arrange a support and shadow around the supplied object but does not relight the original subject or guarantee physical perspective. Run Match to Scene on Original Subject for further integration. There are **two generation requests per variant**, including paid providers; the dialog estimates both. Failure or cancellation in pass two applies nothing.

## Local FLUX workflow

Use FLUX.2 Klein on ComfyUI 0.37 or newer, with the model, Qwen encoder and FLUX.2 VAE selected in AI Settings. Refresh Models after configuring another model folder. Weights can stay in another ComfyUI installation: point the running server's extra-model-paths configuration to its models directory rather than duplicating them.

The bundled `scene-edit.json` uses **VAEEncode → SetLatentNoiseMask → KSampler** and ReferenceLatent. Harmonize sends one complete composite; subject Relight adds a smaller complete-background reference. Inputs are padded, not cropped. The workflow also decodes the encoded source as a control. Lighting/shadow comparisons against this reconstruction discount codec blur, ringing and tone drift. Shadow extraction fits robust channel gain/offset corrections on background block means, then smooths signed differences before accepting coherent darkening. Bright/dark texture fluctuations cannot create a shadow merely through one-sided clipping. Smooth floating-point gains come from reliable subject interiors and extend to the original contour. A small model-space opaque-contour anchor limits silhouette changes. Cleanup keeps the actual encoded image for both the initial latent and reference conditioning; local sampling and a short removal instruction target the product while retaining the surrounding geometry. Default denoise is **0.80** for lighting and **0.95** for background layout; cleanup uses 1.0. Denoise alone cannot preserve identity.

Requests preserve proportions, pad only technical edges to multiples of 16 and remove padding before insertion. References upload once; variants run sequentially to limit memory. MP changes generation resolution without resizing the document. Original size observes the minimum generation area. Intel Auto mode uses CPU text encoding and tiled VAE; the first run may take several minutes.

At identical input/output sizes, pixels are copied without another cubic resampling pass. Regression checks put distinct markers at all four canvas corners and verify the complete round trip.

GPT and Nano Banana use full-image editing with the same extraction. Comfy credits are required; denoise applies only to local FLUX. Availability, dimensions and quality are checked before upload. Paid providers were checked with simulated responses; live paid-provider image quality was not validated.

## Research and limits

BFL's [official Klein demo](https://huggingface.co/spaces/black-forest-labs/FLUX.2-klein-4B/blob/main/app.py) distinguishes concise image-edit instructions from detailed text-to-image descriptions. Its [FLUX.2 guidance](https://help.bfl.ai/articles/7734566352-does-flux-2-support-negative-prompting) says to describe the desired result instead of using negative prompts. This is not a claim that all long prompts fail: the user's exact integration instruction is the default for this operation, checked separately from workflow and extraction changes. See the [validation report](scene-edit-validation.md) for same-input comparisons and community references.

Adobe describes [Harmonize](https://helpx.adobe.com/au/photoshop/desktop/repair-retouch/remove-objects-fill-space/blend-subjects-with-harmonize.html) as adapting an object's appearance, lighting and shadows to its background. This defines the intended workflow. Composa uses an independent FLUX pipeline; Photoshop visual parity is not established.

The official [ComfyUI FLUX.2 Klein editing template](https://github.com/Comfy-Org/workflow_templates/blob/main/templates/image_flux2_klein_image_edit_9b_base.json) supplies encoded-image reference conditioning. Composa adds an image latent, noise mask and separate extraction. General editing models can invent incorrect shadows, particularly with little visible floor. Direction cues and masks constrain output but do not solve scene geometry.

[IC-Light](https://github.com/lllyasviel/IC-Light) is a possible specialized relighting backend. It requires separate models/runtime and photographic validation and is not included here. A specialized shadow model or depth/normal approach remains a possible next step for harder scenes.
