# CHAT GPT 2.5 · Comfy.org Partner Node

Uses the official `OpenAIGPTImageNodeV2` with **`gpt-image-2.5-sunburst`**, as in the supplied example. The pack contains no model weights and needs no local diffusion model, encoder, VAE or LoRA. Update ComfyUI until this exact model appears in the node; Composa does not silently substitute GPT Image 2 or another model.

## Connect

1. Set your ComfyUI server URL under **AI → ComfyUI Settings**, and refresh/test it. This works across a trusted LAN too.
2. Create a **Comfy.org API key** at [platform.comfy.org](https://platform.comfy.org/). This is not an OpenAI API key. A ComfyUI browser login is not shared with external applications.
3. Paste the key into **Session API key** (memory only, discarded when Composa exits), or set `COMPOSA_COMFY_API_KEY` on the computer running Composa. You can change the environment variable's name in settings. The secret is never written to preferences or workflow JSON. Use HTTPS outside a trusted LAN.
4. Select **CHAT GPT 2.5** in the floating panel, then choose image size/quality under Advanced. `low` is the initial quality, matching the supplied workflow. LoRA and local model controls are hidden for this pack. Select FLUX to use local upscaling/subject-selection models.

## Images and masks

- Text-to-image generation accepts zero to six references, ordered as **image 1, image 2, …**.
- Editing sends the source crop as **image 1**. Reference thumbnails are **image 2, image 3, …**, in their displayed order. Source context is controlled by Mask context, rather than cropping to the mask alone.
- The official node currently accepts a native API mask only when there is **exactly one input image**. With references, Composa appends a separate grayscale mask-guide image after all references, explains its white/edit and black/keep areas in the prompt, and does NOT attach the unsupported native mask. This is model guidance, not a guaranteed strict inpainting mask. The final result is constrained/blended locally using the editor mask. The guide also counts as a paid image input.
- Remove sends a black-filled repair area and the removal instruction. The original committed pixels stay unchanged. Odd canvas dimensions are retained by fitting the returned crop and blending back in original canvas coordinates; no final crop to a multiple of 16.
- Change Background requires a subject selection; it produces a generated background plus the original subject as separate editable layers. The pack does not bundle a segmentation model.
- Variants 1/2/3: List performs sequential API calls, Batch sets the official node's `n`. Every image is billed in either mode. Results are applied together as one undoable document edit. A failed run never applies a partial set, but already completed API calls may still have charged credits.

## Cost and balance

Comfy.org **credits** are not OpenAI token counts. The local ComfyUI API does not expose the browser account's balance. Check balance in ComfyUI's Credits settings; Composa shows it as unavailable, never as zero.

The panel's approximate USD estimate is derived from the connected node's **own price-badge data tables**, including quality, preset size, references, mask-guide and all variants. It is not a quote or a spending cap. If the server does not expose a recognized price badge, the estimate is unavailable. Progress text can provide `Price: … credits`; when present Composa shows the server-reported cost. Older servers may not emit it. No currency-to-credit conversion is guessed and the node does not expose raw OpenAI usage tokens to Composa.

No automatic retries of paid generation are performed. Cancelling or undoing the document edit does not guarantee cancellation/refund of an already started cloud operation. Actual paid generation must be tested with a funded account; unit/UI tests and schema checks do not spend credits.

Official sources: [ComfyUI node implementation](https://github.com/Comfy-Org/ComfyUI/blob/master/comfy_api_nodes/nodes_openai.py), [external API key example](https://github.com/Comfy-Org/ComfyUI/blob/master/script_examples/basic_api_example.py), [Partner Nodes](https://docs.comfy.org/tutorials/partner-nodes/overview), [OpenAI image-generation documentation](https://developers.openai.com/api/docs/guides/image-generation).
