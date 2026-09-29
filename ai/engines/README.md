# Composa Engine Packs

An Engine Pack is a lightweight directory named after its `id`. It contains `manifest.json`, ComfyUI API-format workflow JSON files, and optional task metadata. It never contains model weights.

The manifest declares the complete pipeline rather than a checkpoint filename: required node types and server assets, versioned workflows, supported editor tasks, semantic-to-node input bindings, parameters, output nodes, output mode, and LoRA support. Node references are explicit ComfyUI node ids; Composa never searches display names.

Production Engine Packs are intentionally not included in stage 1. They will be authored and verified against the installed ComfyUI environment in stage 2. Until then the UI reports that no Engine Pack is installed instead of simulating a result.

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
        "selectionMask": { "nodeId": "2", "input": "image" }
      }
    }
  ],
  "parameters": [],
  "lora": { "supported": false, "maximum": 0 }
}
```
