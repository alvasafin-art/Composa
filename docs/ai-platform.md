# AI platform

Composa keeps AI optional. The editor starts and all ordinary editing continues to work when no server or Engine Pack is available.

## Connect ComfyUI

Open **AI > ComfyUI Settings** and enter one **ComfyUI Server URL**, such as `http://127.0.0.1:8188` or a LAN/VPN address. There is no Local/Remote switch: both are the same HTTP/WebSocket server contract. **Test Connection** reports the server version and device when available, then validates the selected Model (internally an Engine Profile) against required nodes and assets.

The compact AI controls share the normal tool-options row and show contextual actions, queue/execution progress, errors, and Cancel. Model/Profile selection lives in the settings dialog because it is not a routine per-operation choice. After a selection gesture finishes, a floating prompt opens beside the selection with up to six ordered reference images, **Generate**, **Remove**, the remaining task menu, Close, and Advanced settings. References support file picking, drag-and-drop, clipboard paste, previews, and individual removal. Output width and height are calculated from the chosen 0.5–4 MP budget while preserving the selection, crop, or document aspect ratio. An empty image layer exposes **Generate Image**. Extending a Crop outside the canvas exposes **Generative Expand**. The permanent **AI** menu contains every stable task plus presets, server settings, and the local Assistant. A compact **Assistant** button is also present in the normal tool-options row.

The bundled FLUX.2 Klein Engine Pack provides the first production workflows. With no compatible pack installed, controls correctly explain that an Engine Pack is required. No successful result is simulated.

## Editing behavior

- AI results arrive as new layers. Fill/removal results retain the selection as a layer mask.
- Inserting all parts of one result is one undo history step; original pixels are not changed.
- **Remove Object** is a separate task. Composa expands and feathers the selection, replaces the target with black, and asks the model to reconstruct it from the surrounding visual context. It does not require an object-specific prompt.
- **Mask context** controls how far the inpaint crop extends beyond the selection (2× by default), so the model sees enough of the surrounding image to rebuild edges and background. Mask Grow and Mask Blend remain independently configurable.
- **Upscale** enlarges the document only when there is no selection. With a selection it sends only that patch to ComfyUI, fits the enhanced result back into the original bounds, and inserts it as a masked layer without changing the canvas size.
- Segmentation output becomes the normal document selection, not a parallel AI selection type.
- The **Selection Brush** paints that same selection mask. Shift adds and Alt subtracts.

## Engine Packs and task bindings

An Engine Profile describes a complete compatible pipeline: workflows, task bindings, required node types and model assets, supported parameters, LoRA rules, semantic inputs, output nodes, and version compatibility. Weights stay on the ComfyUI server.

Tasks are stable editor concepts; bindings are explicit and versioned. Several tasks may reference one workflow. Semantic inputs such as `prompt`, `sourceImage`, `selectionMask`, `referenceImage1`…`referenceImage6`, `upscaleModel`, and `preprocessedImage` map to exact node ids and input keys, so editor code contains no Flux/Qwen-specific node ids and never searches node display names. See [`ai/engines/README.md`](../ai/engines/README.md) for the manifest shape.

## Layer tags and automation foundation

Layers can carry multiple optional tags such as `title`, `logo`, `product`, `background`, or a custom tag. Tags survive `.cmps` save/load and are available through `FindLayersByTag`. The shared command service batches ordinary editor commands, scripts, Assistant edits, and queued AI tasks into one undo transaction rather than mutating view models.
