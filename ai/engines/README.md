# Composa Engine Packs

An Engine Pack is a lightweight directory named after its `id`. It contains `manifest.json`, ComfyUI API-format workflow JSON files, and optional task metadata. It never contains model weights.

The manifest declares the complete pipeline rather than a checkpoint filename: required node types and server assets, versioned workflows, supported editor tasks, semantic-to-node input bindings, parameters, output nodes, output mode, and LoRA support. Node references are explicit ComfyUI node ids; Composa never searches display names.

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
