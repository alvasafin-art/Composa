# Qwen Image 2.1 Turbo

Local ComfyUI engine for Generate Image, Image Edit, Generative Fill, Remove Object,
Generative Expand, Change Background, Harmonize, Match to Scene and Relight.
Selection and upscale use their existing specialized packs.

In **AI → ComfyUI Settings → Workflows & models**, select **Qwen Image 2.1 Turbo**
as the default, or assign it to individual tasks. Refresh the server's models and
select these compatible files (subfolders are supported):

- Diffusion: `qwen_image_2.1_turbo_int8_convrot.safetensors`
- Text encoder: `qwen3vl_8b_int8_convrot.safetensors`, loader type `qwen_image`
- VAE: `texture_fix_vae_for_qwen_image_2.1_bf16.safetensors`

The server needs ComfyUI 0.43.0 or newer with `TextEncodeQwenImage21`,
`QwenImage21Cache`, `ModelAttentionBackend` and the custom `PixelDriftFix` node.
Missing nodes/models are reported before source pictures are uploaded. Composa
does not install or download them. The three model choices and additional prompts
are saved separately for this pack and server; existing FLUX/API assignments remain.

This pack uses the official Turbo eight-step Euler / BasicGuider schedule at CFG 1
and full denoise. Do not select base-model weights or add a Viggle six-step LoRA:
they need different schedules. Negative prompts have no effect at CFG 1. Variants
run sequentially, including requests from scripts. KV cache uses automatic placement
and lossless default precision. NVIDIA uses the tested Comfy Kitchen attention;
other devices use PyTorch attention. This is not a guarantee that all quantized
weight formats run on every backend.

Native source dimensions are padded on the right/bottom to multiples of 32 and
cropped back after decoding, without stretching the source frame. Masked operations
send a marked context crop to the image encoder. The sampler receives the clean
source latents and a grayscale noise mask in the same padded frame. Composa fits
the decoded crop to its document position and applies the editable insertion mask.
**Align pixels** in the prompt panel controls alignment for masked and full-image edits. Reference images follow the source in UI order; masks are never references.
Use direct editing instructions; with several images the source is `<image1>` and
user references start at `<image2>`. For Generate Image references start at `<image1>`.

Harmonize defaults to the short prompt selected by the indoor/outdoor sunlight and
diffuse-light comparison:

> Object naturally integrated into the scene, matching lighting direction, realistic shadows and contact shadow, subtle ambient occlusion, consistent reflections, physically grounded, seamless photorealism

Automatic direction is inferred from the source composite; the FLUX lighting
classifier and extra background reference are not appended. User guidance, explicit
light direction and shadow controls still apply. PixelDriftFix aligns the edit;
a reconstruction from the same VAE calibrates shadow extraction. Original PNG alpha
and source detail are retained by Composa's layer extraction, and the shadow is
inserted as a separate editable layer. Change Background uses two passes, arranged
scene followed by a clean plate, and keeps the original subject separately. The new
environment is not fitted to the old scene's homography. A small vertical registration
matches the generated support to the source PNG, and the arranged cast is recovered
against the clean plate as a separate shadow layer if cleanup removed it.

Explicit light angles and directional presets limit extraction to a broad projected
shadow cone with a soft allowance around the contact. Automatic scene matching keeps
all directions available. This rejects unrelated background darkening during Relight;
it does not synthesize a missing cast or guarantee physical accuracy on every scene.

No Qwen weights are distributed with Composa. Official Qwen Image 2.1 weights use
the [Qwen Research License](https://huggingface.co/Qwen/Qwen-Image-2.1-Turbo/blob/main/LICENSE);
commercial use requires separate rights. Check the terms for your selected weights.

References: [native ComfyUI tutorial](https://github.com/Comfy-Org/docs/blob/main/tutorials/image/qwen/qwen-image-2-1.mdx),
[official Turbo model](https://huggingface.co/Qwen/Qwen-Image-2.1-Turbo).
