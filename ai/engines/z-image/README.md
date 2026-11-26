# Z-Image

Local ComfyUI text-to-image generation. Put the weights in these folders on the ComfyUI server and refresh the model list in AI Settings.

- `models/diffusion_models/z_image_bf16.safetensors`
- `models/text_encoders/qwen_3_4b.safetensors`
- `models/vae/ae.safetensors`

[ComfyUI model downloads and reference workflow](https://docs.comfy.org/tutorials/image/z-image/z-image)

The generation prompt enhancer adapts Tongyi MAI's guidance for concrete composition and exact lettering. It returns the positive prompt for the editor. [Reference enhancer](https://huggingface.co/spaces/Tongyi-MAI/Z-Image/blob/4d44787a1fbfee7f4ce0246038ea76031bab1613/pe.py) · [Official inference examples](https://github.com/Tongyi-MAI/Z-Image#-quick-start)
