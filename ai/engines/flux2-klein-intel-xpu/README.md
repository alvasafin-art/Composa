# FLUX.2 Klein · Intel XPU

Production Engine Pack for the Intel Arc ComfyUI installation used to validate Composa stage 2.

- Generation and image editing use `flux-2-klein-9b_int8_convrot.safetensors`, Qwen 3 8B FP8 and the FLUX.2 VAE.
- Fill, removal, harmonization, relighting and expansion use `InpaintCropImproved` / `InpaintStitchImproved`. The crop is padded internally, sampled at dimensions divisible by 16, and stitched back to the exact source canvas, so odd document sizes are not silently cropped.
- Masked edits use Klein's image-edit conditioning (`ReferenceLatent`) and `EmptyFlux2LatentImage` sized from the actual crop, not grey native inpaint conditioning. Mask hole filling is disabled so an inverse background mask does not swallow the protected subject. Remove hides the selected pixels with opaque black before generation; its output mask preserves the stitcher's blending margin.
- The inpaint mask grows by 8 px and blends over 32 px by default. Mask context is 2× the selection bounds by default, giving the model surrounding pixels for coherent reconstruction. All three values are configurable in Advanced settings.
- Editing and inpainting accept zero to six ordered visual references. Missing optional branches are pruned before the workflow is queued.
- Upscale can use either installed `4x-UltraSharpV2.safetensors` or `4x_NMKD-Siax_200k.pth`, selected in ComfyUI Settings. Without a selection it resizes the document in the same undo transaction as the generated layer; with a selection it enhances only that patch and returns it at the original bounds as a masked layer without resizing the canvas.
- Select Subject and Object Selection use native ComfyUI background removal with `birefnet.safetensors` and return an ordinary editable Composa selection. BiRefNet is MIT-licensed; unlike BRIA RMBG 2.0 it does not impose a non-commercial model restriction.
- Change Background uses a separate background-generation workflow with zero to six optional references, without the original subject as model conditioning. The complete scene is fitted to the original canvas beneath the untouched original-subject layer. This avoids hallucinated duplicate subjects and old-background fringes caused by inverse-mask image editing. With no selection it runs subject selection first, in the same undo transaction.
- Match to Scene is local and non-generative: it adds clipped Exposure, Color Balance and, when useful, Focus or Grain adjustment layers in one undo step.
