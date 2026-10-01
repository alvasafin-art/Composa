# AI platform

Composa keeps AI optional. The editor starts and all ordinary editing continues to work when no server or Engine Pack is available.

## Connect ComfyUI

Open **AI > ComfyUI Settings** and enter one **ComfyUI Server URL**, such as `http://127.0.0.1:8188` or a LAN/VPN address. There is no Local/Remote switch: both are the same HTTP/WebSocket server contract. **Test Connection** reports the server version and device when available, then validates the selected Model (internally an Engine Profile) against required nodes and assets.

The compact AI controls share the tool-options row and show contextual actions, progress, errors and Cancel. After a selection gesture finishes, the floating panel opens below it with up to six ordered references, a workflow-pack picker, prompt, **Generate + 1/2/3**, **Remove**, more tasks, Close and Advanced. Its anchor follows the viewport; drag any blank part of the header to move it. Reference cells have centered image icons; thumbnails open previews and can be removed, with file, clipboard and drop support. Image sizing keeps the selection/canvas proportions. **Generate Image** and **Image Edit** support full images; **Generative Fill** requires a selection. **Generative Expand** has a fixed hidden instruction: selection edits use masked fill without mode choice, while Crop/no-selection expansion defaults to Empty area only with optional whole-image regeneration. The permanent **AI** menu also contains server settings and Assistant.

**Object Selection AI** is in the Magic Wand tool group: draw a rectangle and release to segment that region. The menu operation also uses an existing selection as its ROI; without one it processes the full image. The duplicate AI Select Subject menu was removed; native non-AI selection commands and legacy script aliases remain. **Remove Object** sits below Brush: paint the area and release to run removal without the floating panel. Both tools require a connected ComfyUI server. Segmentation uses a local compatible workflow even when the generation picker is set to GPT.

The bundled FLUX.2 Klein Engine Pack provides the first production workflows. With no compatible pack installed, controls correctly explain that an Engine Pack is required. No successful result is simulated.

## Editing behavior

- AI results arrive as new layers. The ordinary document selection is unchanged. For already-stitched workflows, the layer's coverage mask reveals changed pixels without applying the soft selection a second time; the smooth transition is already in the returned pixels. Raw-patch workflows continue using the selection/output mask.
- Inserting all parts of one result is one undo history step; original pixels are not changed.
- **Remove Object** is a separate task. The bundled workflow creates a black patch for model conditioning and asks the model to reconstruct the background from surrounding content. Grow/blend are performed once in the workflow. Its stitcher blends against the untouched original context, not against the blackened conditioning image. It does not require an object-specific prompt.
- **Change Background** treats the selection as the protected subject. Without a selection it first selects the subject automatically. A separate workflow generates the complete background scene without redrawing the subject. Results are an editable group with the generated background and untouched original subject on top; automatic selection and replacement undo together.
- **Mask context** controls how far the inpaint crop extends beyond the selection (2× by default), so the model sees enough of the surrounding image to rebuild edges and background. Mask Grow and Mask Blend remain independently configurable.
- **Upscale** enlarges the document only when there is no selection. With a selection it sends that patch plus a 32-pixel context halo to avoid artificial model boundaries. The enhanced context is fitted back, cropped to the exact original selection bounds and inserted with the original mask; the canvas stays unchanged.
- Segmentation output becomes the normal document selection, not a parallel AI selection type.
- The **Selection Brush** paints that same selection mask. Shift adds and Alt subtracts. It shares the Lasso group, has a dashed selection-contour icon and retains its Q shortcut.

## Engine Packs and task bindings

An Engine Profile describes a complete compatible pipeline: workflows, task bindings, required node types and model assets, supported parameters, LoRA rules, semantic inputs, output nodes, and version compatibility. Weights stay on the ComfyUI server.

Tasks are stable editor concepts; bindings are explicit and versioned. Several tasks may reference one workflow. Semantic inputs such as `prompt`, `sourceImage`, `selectionMask`, `referenceImage1`…`referenceImage6`, `upscaleModel`, and `preprocessedImage` map to exact node ids and input keys, so editor code contains no Flux/Qwen-specific node ids and never searches node display names. See [`ai/engines/README.md`](../ai/engines/README.md) for the manifest shape.

Execution validates the actual bound workflow and its selected loader/model choices. An upscaler does not require unrelated segmentation or diffusion models from the same pack. See [AI diagnostics](ai-diagnostics.md) for the mask, alignment and finishing issues addressed and the remaining model-quality limits.

## Layer tags and automation foundation

Layers can carry multiple optional tags such as `title`, `logo`, `product`, `background`, or a custom tag. Tags survive `.cmps` save/load and are available through `FindLayersByTag`. The shared command service batches ordinary editor commands, scripts, Assistant edits, and queued AI tasks into one undo transaction rather than mutating view models.
