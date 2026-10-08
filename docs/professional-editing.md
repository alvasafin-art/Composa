# Retouching, paths and non-destructive editing

## Brushes

**Opacity** limits the coverage of one continuous stroke. **Flow** sets the paint added by each dab: low flow builds gradually up to that opacity. **Spacing** is the distance between dabs as a percentage of diameter. The preset menu offers soft/hard and low-flow brushes.

**Dynamics** contains pressure-to-size and pressure-to-flow switches, Smoothing, the full brush outline and **Save current brush**. The usual outline marks the half-coverage radius; the full outline shows the entire footprint. Smoothing ends at the release point. Saved presets live in application settings.

## Conventional retouching

- **Spot Healing Brush**: transfers a coherent nearby donor patch for small repairs, matching structure separately from overall tone and adapting boundary color. Sample all layers snapshots the visible composite before the stroke and writes only the repair onto the active layer, including an empty retouch layer. Broad strokes without a complete valid donor fall back to the existing exemplar fill. [Research and checks](spot-healing-research.md).
- **Healing Brush**: Alt-click a clean source, then paint. Source texture is adapted to destination boundary color. Aligned keeps the offset across strokes; Sample all layers reads the composite on an untransformed target.
- **Patch**: make a selection, choose Patch and drag inside it to a clean donor. Releasing applies the preview; Escape cancels. Clicking outside starts a new rectangular selection.
- **Edit > Content-Aware Fill** opens a resizable sampling workspace. Green is available sampling, red is excluded sampling and purple is the repair area. Paint exclusions, or choose Include sampling to erase them. **Fast preview** works at up to 960 pixels on the long side; OK always computes the full-resolution fill before committing. Disable Fast preview to preview the full result. Pointer feedback uses direct bitmap copies instead of PNG encoding. Output to new layer keeps the original and creates a transparent repair layer; untick it to edit the target directly.

These tools use conventional texture search rather than generative AI. The fill searches at several resolutions, samples original-resolution detail and adapts the boundary. It cannot invent missing scene structure; large objects and repetitive textures may require another donor or manual retouching. Feathered repair-layer extraction is exact on an opaque backdrop; overlapping translucent content can composite differently, so use direct output when that matters.

## Local object selection

The model menu offers **SAM Quality** (local EfficientSAM S) and installed **BiRefNet** variants from ComfyUI. Choose a model, then click an object or use Object Selection AI's rectangle tool. Select > Subject prompts the full canvas. Shift adds and Alt subtracts.

SAM Quality uses ONNX Runtime locally and requires no Python, PyTorch, ComfyUI, runtime download or GPU. S adds about 101 MiB of weights. Sessions load on first use and remain cached; one image embedding speeds repeated prompts. Selection consumes additional CPU/RAM while running; idle selection performs no inference. BiRefNet runs on the configured ComfyUI server.

SAM selections receive their own committed mask; inference temporaries are disposed separately. Real encoder/decoder tests cover portrait and landscape object geometry, and selection history survives inference cleanup. Folder choices (including Bucket instead of Gradient, Eraser, marquee and lasso variants) follow document tabs, survive closing all documents, and are saved between launches.

The floating AI selection panel collapses to its draggable header. This preference survives new selections, document tabs and relaunch, until its arrow is clicked again. Selection Brush and Object Selection AI expose Expand, Contract and Feather through **Modify**. Layer flip buttons sit after the alignment group. Foreground/background swatches follow the tool buttons, whose height adapts to ordinary window heights.

Tone curves use a natural cubic spline and allow the black/white endpoint handles to move in both axes. The value outside the endpoints is constant. Dragging from near a handle preserves the grab offset. Existing curve points remain saved in the same format; their interpolation follows the updated curve behavior.

## Refined masks and saved selections

**Select > Select and Mask** offers Gray, Black, White and Mask preview backgrounds. Smooth, Feather, Contrast and Shift edge alter the outline. Refine with image edges adjusts uncertain boundaries while retaining certain interiors. Decontaminate edge colors borrows nearby interior color and outputs a new layer, preserving the source. Output can otherwise be a selection, an unlocked pixel layer's mask or a cutout layer.

**Select > Save Selection** stores a named alpha channel. **Load Selection** restores it with replace/add/subtract/intersect and can delete stored channels. Channels follow crop, image size, canvas rotation and flips, and participate in Undo.

## Locks

Right-click a layer and open **Lock**. Pixels blocks painting and destructive pixel changes. Position blocks transforms and alignment. Transparency permits recoloring while preserving alpha. Folder locks are inherited. All three together protect deletion, appearance and reordering; visibility remains switchable.

## Pen and vector masks

Choose **Pen (P)**. Click for a corner; drag for curve handles. Click the first node to close a path, or Enter to finish it open. Ctrl-click begins another path. Drag existing nodes/handles; Alt moves one handle independently. Shift-click near a curve inserts a node without altering its geometry. Click a node and press Delete to remove it. Escape cancels a draft or drag.

Fill and stroke controls are available in the Pen, Shape and Move bars. **Path to selection** loads the outline. Right-click a closed path and choose **Use path as vector mask**, then the target layer. On the target, enable **Edit vector mask** in the Pen bar to edit its nodes. Vector and raster masks multiply if both exist. Right-click the target to load or delete its vector mask.

## Smart filters

Choose **Filter > Add Smart Filter** on a raster layer or smart object. Source pixels are retained. Rows under the layer offer enable, edit and delete controls. Changing a filter rebuilds the stack from its source; deleting the final filter restores an ordinary raster layer. Undo and project saving retain settings and source pixels.

Painting requires Rasterize Layer. Canceling a filter dialog restores the previous stack. Filters apply to the entire source rather than the selection; layer masks can limit the appearance. Filters that expand the source must be configured before a perspective transform. Live text/shapes require conversion to a smart object or rasterization.

## Gradients and files

**Gradient > Edit stops** edits color stops and opacity stops independently. Presets include black-to-white, transparent fade, sunset and spectrum. The checkerboard preview shows transparency. **Use foreground / background** restores a two-color gradient. Gradient Overlay uses the same editor with angle, scale, opacity and reverse controls.

Save **.cmps** to retain every new editable feature. Format 10 reads older projects; older builds cannot open new-format files. PSD keeps rendered appearances for new Bezier paths, vector masks and smart filters, with an explicit conversion report. Saved alpha channels stay in .cmps. Warp, advanced Liquify, 16-bit editing and full color management are outside this update.
