# Z-Image Turbo

Local ComfyUI text-to-image generation. Put the weights in these folders on the ComfyUI server and refresh the model list in AI Settings.

- `models/diffusion_models/z_image_turbo_bf16.safetensors`
- `models/text_encoders/qwen_3_4b.safetensors`
- `models/vae/ae.safetensors`

[ComfyUI model downloads and reference workflow](https://docs.comfy.org/tutorials/image/z-image/z-image-turbo)

The generation prompt enhancer adapts Tongyi MAI's guidance for concrete scenes and exact lettering. Turbo uses a single positive prompt. [Reference enhancer](https://huggingface.co/spaces/Tongyi-MAI/Z-Image-Turbo/blob/15079c6f802f6615ddcc294c3479b6c80c0953ba/pe.py) · [Official model card](https://huggingface.co/Tongyi-MAI/Z-Image-Turbo)
