# FLUX.2 Klein · Intel XPU

Production Engine Pack for the Intel Arc ComfyUI installation used to validate Composa stage 2.

- Generation and image editing use `flux-2-klein-9b_int8_convrot.safetensors`, Qwen 3 8B FP8 and the FLUX.2 VAE.
- Fill, removal and expansion use `InpaintCropImproved` / `InpaintStitchImproved`. The crop is padded internally, sampled at dimensions divisible by 16, and stitched back to the exact source canvas, so odd document sizes are not silently cropped.
- The inpaint mask grows by 8 px and blends over 32 px by default. Both values are configurable in ComfyUI Settings.
- Editing and inpainting accept zero to six ordered visual references. Missing optional branches are pruned before the workflow is queued.
- Upscale uses 4x UltraSharp V2 and resizes the Composa document in the same undo transaction as the generated layer.
- Select Subject uses native ComfyUI background removal when `birefnet.safetensors` is visible to the server.

The pack intentionally does not bind Object Selection: the validated installation has no promptable SAM, Florence, GroundingDINO or equivalent detection weights. It is safer to expose that operation only after a compatible model is installed than to present subject extraction under the wrong name.
