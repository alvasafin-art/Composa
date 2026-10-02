# AI platform

Composa keeps AI optional. The editor starts and all ordinary editing continues to work when no server or Engine Pack is available.

## Connect ComfyUI

Open **AI > ComfyUI Settings** and enter one **ComfyUI Server URL**, such as `http://127.0.0.1:8188` or a LAN/VPN address. There is no Local/Remote switch: both are the same HTTP/WebSocket server contract. **Test Connection** reports the server version and device when available, then validates the selected Model (internally an Engine Profile) against required nodes and assets.

The compact AI controls share the tool-options row and show contextual actions, progress, errors and Cancel. After a selection gesture finishes, the floating panel opens below it with up to six ordered references, a workflow-pack picker, prompt, **Generate + 1/2/3**, **Remove**, more tasks, Close and Advanced. Its anchor follows the viewport; drag any blank part of the header to move it. Reference cells have centered image icons; thumbnails open previews and can be removed, with file, clipboard and drop support. Image sizing keeps the selection/canvas proportions. **Generate Image** and **Image Edit** support full images; **Generative Fill** requires a selection. **Generative Expand** has a fixed hidden instruction: selection edits use masked fill without mode choice, while Crop/no-selection expansion defaults to Empty area only with optional whole-image regeneration. The permanent **AI** menu also contains server settings and Assistant.

**Object Selection AI** is in the Magic Wand tool group: draw a rectangle and release to segment that region. Hold Shift at pointer-down to add the resulting mask, Alt to subtract (Alt wins when both are held), or neither to replace. Shift does not square the search rectangle. The previous selection stays intact throughout the request and combines with the returned mask in one undoable step. The menu operation also uses an existing selection as its ROI; without one it processes the full image. The duplicate AI Select Subject menu was removed; native non-AI selection commands and legacy script aliases remain. **Remove Object** sits below Brush: paint the area and release to run removal without the floating panel. Both tools require a connected ComfyUI server. Segmentation inherits a local compatible workflow when the default pack is GPT unless explicitly assigned otherwise.

**ComfyUI Settings → Workflow by task** stores independent task-to-pack assignments, filtered by supported bindings. Initial assignments use GPT for Generate Image and FLUX for the other tasks; explicitly choosing Use default workflow inherits the default pack. The floating picker changes Fill only; dialog pickers change their own task only. Explicit unavailable assignments fail visibly instead of silently switching packs. Expand keeps its assignment even when a selected-region request is internally normalized to Fill. Generate Image and Image Edit dialogs use the same shared six-image reference editor as the floating panel, including clipboard/drop, preview, removal and live cost updates.

The bundled FLUX.2 Klein Engine Pack provides the first production workflows. With no compatible pack installed, controls correctly explain that an Engine Pack is required. No successful result is simulated.

## Editing behavior

- AI results arrive as new layers. The ordinary document selection is unchanged. Bundled FLUX and GPT masked edits preserve the generated context crop as raw layer pixels with a separate editable soft mask, applied once. Removing the mask reveals the decoded crop. Legacy custom already-stitched workflows retain coverage-mask compatibility.
- Inserting all parts of one result is one undo history step; original pixels are not changed.
- **Remove Object** is a separate task. The bundled workflow creates a black patch for model conditioning and asks the model to reconstruct the background from surrounding content. Sampling coverage and final soft layer-mask coverage are separate; original context is never replaced by the black conditioning image. It does not require an object-specific prompt.
- **Change Background** treats the selection as the protected subject. Without a selection it first selects the subject automatically. A separate workflow generates the complete background scene without redrawing the subject. Results are an editable group with the generated background and untouched original subject on top; automatic selection and replacement undo together.
- **Mask context** controls how far the inpaint crop extends beyond the selection (2× by default), so the model sees enough of the surrounding image to rebuild edges and background. Mask Grow and Mask Blend remain independently configurable.
- **Upscale** enlarges the document only when there is no selection. With a selection it sends that patch plus a 32-pixel context halo to avoid artificial model boundaries. The enhanced context is fitted back, cropped to the exact original selection bounds and inserted with the original mask; the canvas stays unchanged.
- Segmentation output becomes the normal document selection, not a parallel AI selection type.
- The **Selection Brush** paints that same selection mask. Shift adds and Alt subtracts. It shares the Magic Wand group, has a dashed selection-contour icon and retains its Q shortcut.

After a selection finishes, a dashed **AI context** rectangle previews the Fill input bounds for the selected workflow. Disable it through ComfyUI Settings. A dragged floating panel keeps its viewport position across successive selections until closed with ×. Canvas expansion refits the active Crop frame and recenters the view. Generate Image Advanced omits mask/context controls. A missing paid API key is requested before uploads in a session-only modal.

Defaults are migrated once for existing preferences: Original size; FLUX 16 px grow / 48 px blend / 16 px conditioning blur / color match off / context 2; GPT 4 px grow / 8 px blend / padding 0; LoRAs off. Later user changes survive reopening and relaunch. Model paths, server URL and custom pack assignments are preserved.

FLUX Generative Fill now keeps independent defaults of 4 px grow / 8 px blend / 4 px conditioning blur / context 1.2. Existing preferences gain these Fill values without changing Expand or GPT. Its Advanced dialog and selection-context preview use this independent set.

All Advanced and generation preferences now belong to a **workflow + task** profile: dimensions, reference resizing, masks/context, color match, seed, variants/execution, LoRAs, paid quality and expansion/upscale sizes. Existing preferences seed every other profile unchanged. FLUX Expand starts with Original size, 1 MP references, List execution, 16/48/16 px, context 2, Color match subtle and seed −1. Switching workflows restores that task's values; cancelling a generation dialog does not save its unaccepted drafts. Advanced identifies its operation in the title.

Original-size FLUX masked crops no longer undergo two inverse resizes to fit the VAE. Stock padding nodes add only technical right/bottom pixels, and the decoded crop is clipped into its saved source coordinates without scaling. Unexpected output dimensions are rejected. MP budgets and explicit Expand minimum-side requests still intentionally resize. This prevents host-side subpixel drift but cannot prevent a generative model from redrawing or moving details.

## Engine Packs and task bindings

An Engine Profile describes a complete compatible pipeline: workflows, task bindings, required node types and model assets, supported parameters, LoRA rules, semantic inputs, output nodes, and version compatibility. Weights stay on the ComfyUI server.

Tasks are stable editor concepts; bindings are explicit and versioned. Several tasks may reference one workflow. Semantic inputs such as `prompt`, `sourceImage`, `selectionMask`, `referenceImage1`…`referenceImage6`, `upscaleModel`, and `preprocessedImage` map to exact node ids and input keys, so editor code contains no Flux/Qwen-specific node ids and never searches node display names. See [`ai/engines/README.md`](../ai/engines/README.md) for the manifest shape.

Execution validates the actual bound workflow and its selected loader/model choices. An upscaler does not require unrelated segmentation or diffusion models from the same pack. See [AI diagnostics](ai-diagnostics.md) for the mask, alignment and finishing issues addressed and the remaining model-quality limits.

## Layer tags and automation foundation

Layers can carry multiple optional tags such as `title`, `logo`, `product`, `background`, or a custom tag. Tags survive `.cmps` save/load and are available through `FindLayersByTag`. The shared command service batches ordinary editor commands, scripts, Assistant edits, and queued AI tasks into one undo transaction rather than mutating view models.
