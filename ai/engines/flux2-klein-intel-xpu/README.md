# FLUX.2 Klein · Intel XPU

Production Engine Pack for the Intel Arc ComfyUI installation used to validate Composa stage 2.

- Generation and image editing use `flux-2-klein-9b_int8_convrot.safetensors`, Qwen 3 8B FP8 and the FLUX.2 VAE.
- Fill, removal, background replacement, harmonization, relighting and expansion use `InpaintCropImproved` / `InpaintStitchImproved`. The crop is padded internally, sampled at dimensions divisible by 16, and stitched back to the exact source canvas, so odd document sizes are not silently cropped.
- The inpaint mask grows by 8 px and blends over 32 px by default. Mask context is 2× the selection bounds by default, giving the model surrounding pixels for coherent reconstruction. All three values are configurable in Advanced settings.
- Editing and inpainting accept zero to six ordered visual references. Missing optional branches are pruned before the workflow is queued.
- Upscale can use either installed `4x-UltraSharpV2.safetensors` or `4x_NMKD-Siax_200k.pth`, selected in ComfyUI Settings. Without a selection it resizes the document in the same undo transaction as the generated layer; with a selection it enhances only that patch and returns it at the original bounds as a masked layer without resizing the canvas.
- Select Subject and Object Selection use native ComfyUI background removal with `birefnet.safetensors` and return an ordinary editable Composa selection. BiRefNet is MIT-licensed; unlike BRIA RMBG 2.0 it does not impose a non-commercial model restriction.
- Change Background generates only the inverse of the subject selection and returns an editable group with a generated background layer and an untouched original-subject layer.
- Match to Scene is local and non-generative: it adds clipped Exposure, Color Balance and, when useful, Focus or Grain adjustment layers in one undo step.
