# Nano Banana 2 · Gemini 3.1 Flash Image

Choose **Nano Banana 2 · Gemini 3.1** in the generation dialog. This profile uses the official built-in **GeminiNanoBanana2V2** ComfyUI node with **Nano Banana 2 (Gemini 3.1 Flash Image)** selected. It requires no local model weights or third-party node pack.

Update ComfyUI, refresh its connection in Composa, and enter a Comfy.org API key in ComfyUI Settings. Availability and supported resolutions are checked against the server before image uploads. Browser sign-in alone does not supply this key. The key is sent in sensitive execution metadata, never stored in workflow inputs or included in error messages.

Generate Image, Image Edit, Generative Fill, Remove Object, Generative Expand, Change Background, Harmonize and Relight use the same official provider node. Output resolutions are **1K**, **2K** and **4K**. Variants run as separate requests while reusing uploads, and are inserted together in one undo step. Cancellation or a failed request creates no partial document edit. Each completed provider request may be billed; Undo does not refund it.

The source is the first input image when an edit needs it; ordered references follow. Up to 14 total images are supported by the adapter (the current reference picker retains its six-reference limit). Selection masks remain local and are applied once when inserting the result. Existing selection grow, feather and legacy generation blend preferences do not control this generation path.

The provider supports fixed aspect ratios. Composa chooses the nearest supported ratio, adds technical edge padding where needed, then removes it before restoring the original canvas placement. Original image pixels are not stretched into a different ratio. A returned image with an unexpected ratio is rejected. Image size controls placement/document resolution; the 1K/2K/4K selector controls provider output resolution.

Cost estimates come from the server's official price badge. When the badge is unavailable Composa reports that an estimate is unavailable. Actual usage is shown when the server reports it.

Official sources, checked 2026-10-08:

- [ComfyUI Nano Banana 2 image-edit template](https://github.com/Comfy-Org/workflow_templates/blob/main/templates/api_google_nano_banana2_image_edit.json)
- [ComfyUI Nano Banana 2 text-to-image template](https://github.com/Comfy-Org/workflow_templates/blob/main/templates/api_google_nano_banana2_text_to_image.json)
- [Official Gemini node implementation and schema](https://github.com/Comfy-Org/ComfyUI/blob/master/comfy_api_nodes/nodes_gemini.py)

Validation checks the real local ComfyUI schema and offline official prompt validation, plus simulated execution of all supported operations, resolution switching, aspect restoration, references, masks, variants, errors, API key handling and undo. No paid provider generation was executed for release validation.
