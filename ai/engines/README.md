# Composa Engine Packs

An Engine Pack is a lightweight directory named after its `id`. It contains `manifest.json`, ComfyUI API-format workflow JSON files, and optional task metadata. It never contains model weights.

Generative packs include **FLUX.2 Klein** (local; up to three model-only LoRAs), **[Qwen Image 2.1 Turbo](qwen-image-2.1-turbo/README.md)** (local; native eight-step image generation and editing), **[GPT Image 2.5 Sunburst](chatgpt-image-2.5/README.md)** and **Nano Banana 2** (paid Comfy.org Partner Nodes; no local weights). The floating selector chooses the whole pack; Advanced adapts to its capabilities. `paidApi`/`apiModel` distinguish the Partner Node path from local inference while old pack ids and saved local-model choices remain valid.

The manifest declares the complete pipeline rather than a checkpoint filename: required node types and server assets, versioned workflows, supported editor tasks, semantic-to-node input bindings, parameters, output nodes, output mode, and LoRA support. Node references are explicit ComfyUI node ids; Composa never searches display names.

Task bindings can declare `outputIsComposited: true` when a workflow already stitches its output against the input canvas. Such masked outputs must match the original canvas dimensions; Composa refuses a stretched cropped result and does not multiply the blend transition by the selection a second time. Their editable layer mask covers changed pixels, within the mask's grow/blend support. Raw outputs keep the legacy selection-mask path. `preprocess: "remove-object-in-workflow"` avoids client-side growth/feathering when the removal workflow owns those passes itself.

Pack-wide diagnostics remain useful during connection setup. Execution instead checks only the actual bound graph and the exact selected model-loader choices from `object_info`, so choosing an alternative upscaler does not require the pack's default upscaler or its unrelated diffusion/segmentation assets.

The bundled `flux2-klein-intel-xpu` pack is the first production pack. It was validated against ComfyUI 0.37 on Intel Arc with the exact nodes and assets listed in its manifest. Other model families should ship as separate packs rather than changing these verified graphs in place.

Minimal shape:

```json
{
  "id": "example-engine",
  "displayName": "Example Engine",
  "manifestVersion": 1,
  "requiredNodeTypes": ["LoadImage", "SaveImage"],
  "requiredAssets": [
    { "kind": "checkpoint", "name": "example.safetensors" }
  ],
  "workflows": [
    { "id": "edit", "version": 1, "file": "workflows/edit.json", "outputNodes": ["9"] }
  ],
  "tasks": [
    {
      "task": "generativeFill",
      "version": 1,
      "workflow": "edit",
      "outputMode": "newLayerWithMask",
      "inputs": {
        "prompt": { "nodeId": "6", "input": "text" },
        "sourceImage": { "nodeId": "1", "input": "image" },
        "selectionMask": { "nodeId": "2", "input": "image" },
        "referenceImage": { "nodeId": "3", "input": "image" }
      }
    }
  ],
  "parameters": [],
  "lora": { "supported": false, "maximum": 0 }
}
```

### FLUX 3 Image Klein and Seedream 5.5

FLUX 3 Image Klein runs on the connected ComfyUI server. Its distilled graph uses 16 Euler steps, CFG 1, the simple schedule, a Qwen3-VL 4B encoder in flux3 mode and the FLUX.2 VAE. The edit graph attaches the source and ordered references to both conditioning branches. Selection edits use a local noise mask and retain an editable layer mask. Model files can be selected in ComfyUI Settings. [Official ComfyUI edit graph](https://github.com/Comfy-Org/workflow_templates/blob/main/templates/image_flux3_klein_distilled_image_edit.json) · [BFL reference implementation](https://github.com/black-forest-labs/flux-3-image-klein)

Seedream 5.5 uses Comfy credits through ByteDanceSeedreamNodeV3. Its API accepts a prompt and ordered images. Composa sends the surrounding image crop for selection edits, restores its placement, and applies an editable local mask. The Generate and Edit prompt instructions can be customized independently in settings. [BytePlus image generation guide](https://docs.byteplus.com/en/docs/modelark/image-generation-tutorial) · [Seedream prompt guide](https://docs.byteplus.com/en/docs/modelark/seedream-4-0-5-0-prompt-guide)

### Prompt enhancer

The enhancer reads `enhancer-generate.txt` for Generate Image and `enhancer.txt` for edits. AI Settings can store an override per model and operation. The enhancer gets the user's text and the number of added references from the editor. Image pixels go to the image workflow when it runs.

| Model | Generation and editing guidance | Official sources |
| --- | --- | --- |
| FLUX.2 Klein 9B | Prioritize subject and action in positive prose. For edits, state the action and the replacement or surface to reconstruct. | [Klein 9B model card](https://huggingface.co/black-forest-labs/FLUX.2-klein-9B), [BFL editing guide](https://github.com/black-forest-labs/skills/blob/master/skills/flux-image-best-practices/rules/i2i-prompting.md) |
| FLUX 3 Image Klein | Write a full scene caption for generation and the intended end state for edits. Use prose references by attachment order in the workflow without boxes. | [BFL Klein guide](https://docs.bfl.ml/flux_3/flux3_image_klein), [BFL upsampler instructions](https://github.com/black-forest-labs/flux-3-image-klein/blob/main/src/flux3/pup_prompts_external_vlm.json) |
| Qwen Image 2.1 Turbo | Describe visible attributes and typography. For edits, give the replacement. With multiple inputs, use one-based `<imageN>` tags to identify references in the local workflow. | [Qwen reference rules](https://github.com/QwenLM/Qwen-Image-2.1/blob/main/prompt_rewrite/prompts/system_prompt_edit.txt#L41-L43), [Alibaba model guide](https://www.alibabacloud.com/help/en/model-studio/qwen-image-2-1-turbo), [ComfyUI text encoder](https://github.com/Comfy-Org/ComfyUI/blob/master/comfy/text_encoders/qwen_image21.py) |
| GPT Image 2.5 Sunburst | Lead with the deliverable. For edits, describe the specific change and protect source identity, framing and exact copy where relevant. | [OpenAI 2.5 guide](https://developers.openai.com/api/docs/guides/image-prompting?model=gpt-image-2.5) |
| GPT Image 2.6 Sunburst | Scale the brief from a single visual requirement to a structured layout with relationships and labels. For edits, scope each change to its target and keep untouched layout and data. | [OpenAI 2.6 guide](https://developers.openai.com/api/docs/guides/image-prompting?model=gpt-image-2.6) |
| Nano Banana 2 | Describe the scene conversationally. For edits, explain how the change integrates with the original light, viewpoint and nearby objects. | [Google image prompting guide](https://ai.google.dev/gemini-api/docs/image-generation#prompt-guide) |
| Seedream 5.5 | Describe the subject in its setting. For iterative edits, state what to change in the current image and what to carry forward, using the user's language where supported. | [Seedream 5.5 guide](https://docs.volcengine.com/docs/ark/seedream-5-5?lang=zh), [Image API](https://docs.volcengine.com/docs/ark/image-generation-api?lang=en) |
| GPT Image CLI | Write a visual brief for the Codex image tool, with reference roles and details to preserve in edits. Both Codex routes use this guidance. | [OpenAI Codex image prompting](https://github.com/openai/skills/blob/main/skills/.system/imagegen/references/prompting.md) |
| GPT Image 2 via direct API | Define the deliverable or scoped change in terms of concrete visual requirements. | [OpenAI GPT Image 2 guide](https://developers.openai.com/api/docs/guides/image-prompting?model=gpt-image-2) |
