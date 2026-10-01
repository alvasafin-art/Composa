# CHAT GPT 2.5 · Comfy.org Partner Node

Uses the official `OpenAIGPTImageNodeV2` with **`gpt-image-2.5-sunburst`**, as in the supplied example. The pack contains no model weights and needs no local diffusion model, encoder, VAE or LoRA. Update ComfyUI until this exact model appears in the node; Composa does not silently substitute GPT Image 2 or another model.

## Connect

1. Set your ComfyUI server URL under **AI → ComfyUI Settings**, and refresh/test it. This works across a trusted LAN too.
2. Create a **Comfy.org API key** at [platform.comfy.org](https://platform.comfy.org/). This is not an OpenAI API key. A ComfyUI browser login is not shared with external applications.
3. Paste the key into **Session API key** (memory only, discarded when Composa exits), or set `COMPOSA_COMFY_API_KEY` on the computer running Composa. You can change the environment variable's name in settings. The secret is never written to preferences or workflow JSON. Use HTTPS outside a trusted LAN.
4. Select **CHAT GPT 2.5** in the floating panel or any generation window. `low` is the initial quality, matching the supplied workflow; quality is available in the generation window and Advanced. There is no separate reasoning/effort parameter in this image node. LoRA and local model controls are hidden for this pack. Select FLUX to use local upscaling/subject-selection models.

## Images and masks

- Text-to-image generation accepts zero to six references, ordered as **image 1, image 2, …**.
- Editing sends the source crop as **image 1**. Reference thumbnails are **image 2, image 3, …**, in their displayed order. Source context is controlled by Mask context, rather than cropping to the mask alone.
- The official node currently accepts a native API mask only when there is **exactly one input image**. With references, Composa appends a separate grayscale mask-guide image after all references, explains its white/edit and black/keep areas in the prompt, and does NOT attach the unsupported native mask. This is model guidance, not a guaranteed strict inpainting mask. The final result is constrained/blended locally using the editor mask. The guide also counts as a paid image input.
- Final blend coverage is distinct from the larger context/conditioning mask. Fill never changes pixels outside the selection; feathering goes inward and small selections retain an editable core. Remove/Harmonize/Relight can also change the explicitly configured Mask grow margin. Native API masks and reference mask guides do not override this local confinement. Already composited pixels are not feathered twice.
- Remove sends a black-filled repair area and the removal instruction when selected. Without selection it sends the entire image with the user's removal request. The original committed pixels stay unchanged.
- Change Background with a subject selection produces a generated background plus the original subject as separate editable layers. Without selection it is a full-image edit using the preservation instruction; it does not claim pixel-perfect subject preservation. The pack does not bundle a segmentation model.
- Image Edit, Remove, Change Background, Harmonize and Relight are available without selections. Image Edit sends the complete image and intentionally ignores a current selection; Generative Fill requires one.
- Expand's default **Empty area only** mode fills transparent/new canvas with opaque black, sends a soft mask plus context, and locally preserves existing content. A selection limits empty-space filling inside an existing canvas. **Whole image** mode sends the complete expanded image without a mask and may redraw everything. Its default size follows the post-Crop dimensions. Minimum-side choices are 768, 1024 (default masked mode), 1280, 1536, 1792 and 2048 px. Applying an expanded crop and all variants is one undo step.
- Variants 1/2/3: List performs sequential API calls, Batch sets the official node's `n`. Every image is billed in either mode. Results are applied together as one undoable document edit. A failed run never applies a partial set, but already completed API calls may still have charged credits.

## Cost and balance

Comfy.org **credits** are not OpenAI token counts. The local ComfyUI API does not expose the browser account's balance. Check balance in ComfyUI's Credits settings; Composa shows it as unavailable, never as zero.

The panel's approximate estimate is derived from the connected node's **own price-badge data tables**, including quality, references, mask-guide and all variants. The display is **credits / USD**, converted at the documented **211 credits = $1** rate ([Comfy pricing](https://support.comfy.org/articles/1982697177-partner-nodes-pricing), verified 2026-10-01). It is not a quote or a spending cap. If the server does not expose a recognized price badge, the estimate is unavailable. Progress text can provide `Price: … credits`; when present Composa shows the server-reported cost. Older servers may not emit it. The node does not expose raw OpenAI usage tokens to Composa.

## Size and prompt settings

Generate Image defaults to original canvas dimensions. MP options scale area uniformly, not aspect ratio. GPT receives explicit **Custom** width/height instead of `auto`, which selects dimensions based on the prompt and does not guarantee the source aspect ratio. The current official node requires dimensions divisible by 16, 480–3840 px edges, 655,360–8,294,400 pixels and a ratio from 1:3 to 3:1. Composa scales uniformly within these limits and reports the actual request dimensions; a wider whole-image canvas is refused before billing (use FLUX instead). Returned crops are fitted uniformly back to the exact editor coordinates, preserving odd canvas sizes. A result with materially incompatible proportions is rejected, never stretched to the mask; a billed API call is not refunded by this rejection.

Each pack has an independent optional additional prompt in ComfyUI Settings. Existing custom text is inherited until explicitly changed for a pack. Seed and List/Batch execution are in Advanced; GPT's unused seed control is hidden. All generation windows use the joined Generate + 1/2/3 variant control.

No automatic retries of paid generation are performed. Cancelling or undoing the document edit does not guarantee cancellation/refund of an already started cloud operation. Actual paid generation must be tested with a funded account; unit/UI tests and schema checks do not spend credits.

Official sources: [ComfyUI node implementation](https://github.com/Comfy-Org/ComfyUI/blob/master/comfy_api_nodes/nodes_openai.py), [external API key example](https://github.com/Comfy-Org/ComfyUI/blob/master/script_examples/basic_api_example.py), [Partner Nodes](https://docs.comfy.org/tutorials/partner-nodes/overview), [OpenAI image-generation documentation](https://developers.openai.com/api/docs/guides/image-generation).
