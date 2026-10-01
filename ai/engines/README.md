# Composa Engine Packs

An Engine Pack is a lightweight directory named after its `id`. It contains `manifest.json`, ComfyUI API-format workflow JSON files, and optional task metadata. It never contains model weights.

Bundled packs: **FLUX.2 Klein** (local models; up to three model-only LoRAs) and **[CHAT GPT 2.5](chatgpt-image-2.5/README.md)** (official paid Comfy.org Partner Node; no local weights). The floating selector chooses the whole pack; Advanced adapts to its capabilities. `paidApi`/`apiModel` distinguish the Partner Node path from local inference while old pack ids and saved local-model choices remain valid.

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
