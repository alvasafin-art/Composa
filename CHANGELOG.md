# Changelog

All notable changes to Composa are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.2.1-preview.21] - 2026-10-08 · TEST

### Fixed

- FLUX removal and expansion use clean color continuation for reference and inpaint conditioning, rather than encoding a black patch as an independent reference. Thin selections receive enough surrounding context; ordinary selected generation retains its established context behavior. Generated output keeps automatic insertion coverage and exact source pixels outside its support.
- Narrow automatic expansions of a verified uniform background continue its tone without retaining hallucinated details. Textured surroundings, large expansions and ordinary prompted fills retain model synthesis.
- Crop options again expose Gen Expand. Vector anchors and handles are edited while Ctrl is held; double-click closes the current path without adding a duplicate anchor.
- Photoshop exchange uses the local image pipe directly, avoiding a second editor process. Receiving applications are restored and activated; exports still use the whole visible merged image. Send to Photoshop remains in Scripts and is removed from File.

### Added

- SeedVR2 upscale workflows for native ComfyUI nodes and ComfyUI-SeedVR2_VideoUpscaler. Output tiles, overlap, VAE tiling and compatible devices are automatic. Available VRAM controls tile size; out-of-memory retries use smaller tiles, and cancellation leaves the document unchanged. Models and custom nodes must already be installed in ComfyUI.
- SeedVR2 setup and primary-source research in `docs/seedvr2-upscale.md`, included in Windows packages.

Validation: 1,000 automated tests passed (471 core, 529 headless app). The actual Windows PowerShell helper exchanged merged images with the test editor and restored its minimized window. Opt-in FLUX GPU checks and replay of recorded model output cover thin expansion, insertion support and undo/redo.

This is an experimental TEST pre-release. Automatic tiling cannot guarantee that every model fits every GPU. Live Photoshop COM/ExtendScript exchange remains unverified because Photoshop is unavailable on the test machine.

## [1.2.1-preview.20] - 2026-10-07 · TEST

### Changed

- EfficientSAM S is the default local object-selection model; MobileSAM and EfficientSAM Ti remain available as faster alternatives. Legacy SAM preferences migrate once. MobileSAM reconstructs its mask from logits with the actual image geometry instead of stretching the exporter's fixed crop; all local SAM models reconstruct before thresholding and refine boundaries against image detail.
- Spot Healing prefers a coherent clean donor with structural matching and boundary adaptation. Excluded damage cannot contaminate the correction tone. Sample All Layers supports healing onto an empty retouch layer while preserving source layers and undo.
- Alignment controls are separated from the reference selector and flip controls, with filled alignment/distribution icons. Tool-folder choices survive document changes, closing all tabs and restarting the application.

### Fixed

- Photoshop exchange has a dedicated local image bridge that works without enabling general AI control and stays connected when window closing is cancelled. Export uses the complete visible composite, independently of the selection.
- Windows packages include ordinary `.js` entry points as well as the Photoshop `.jsx` script. Photoshop sends a merged duplicate, restoring the original document and dialog settings even when exchange fails. Composa offers File > Send Image to Photoshop and a compatible automation script.

### Added

- Comparative research on Photoshop healing, PatchMatch, exemplar inpainting, diffusion/PDE filling and modern learned alternatives: `docs/spot-healing-research.md`, included in Windows packages.

Validation: 983 automated tests passed (468 core, 515 headless app). Real bundled SAM models and retouching were also checked on a local photo, including source preservation and undo/redo. The actual Windows PowerShell helper exchanged images with the running test editor. Photoshop scripting contracts were checked with a simulated host; live Photoshop COM/ExtendScript exchange remains unverified because Photoshop is unavailable on this machine. This is an experimental TEST pre-release.

## [1.2.1-preview.19] - 2026-10-07 · TEST

### Changed

- Masked FLUX and GPT edits automatically choose context, conditioning overlap and insertion coverage. Manual grow, blend, blur, context and color-match controls are removed; saved legacy values and script overrides are ignored.
- Generation uses the selection contour at full strength, independently of selection-tool opacity and feather. The current selection remains unchanged; a preserved background subject retains its separate original cutout matte.
- Generated pixels receive conservative local registration and a screened regional color correction before one editable layer mask is applied. Nearly uniform backgrounds receive an additional small neutral tone-drift correction; Relight, Harmonize and background replacement preserve independent interior color. Pixels outside the finite insertion support are restored exactly.
- Runtime FLUX graphs use stock nodes regardless of whether Pixaroma is installed. CPU result finishing runs away from the editor thread.
- Expansion context includes partially transparent pixels even when the canvas contains no completely empty pixels.

### Added

- Research comparing Photoshop/Firefly, the supplied Pixaroma workflow, Krita AI Diffusion, FLUX.2 Klein in Diffusers, and gradient/regional harmonization methods: `docs/automatic-seams-research.md`, included in Windows downloads.

This is an experimental TEST pre-release. Automatic seam treatment cannot repair arbitrary model hallucinations or large geometric changes. Paid GPT generation and Photoshop's closed internals are not part of live validation.

Validation: 966 automated tests (458 core, 508 headless app), plus opt-in runs on Intel Arc B580 with FLUX.2 Klein 9B and full FLUX VAE. Six photo-edit scenarios checked output support and undo/redo. Live stock-node checks covered image/mask alignment and tiny low-opacity selections; the uniform-background tone drift improved from 187.85 to 199.78 against a source level of 200.

## [1.2.1-preview.18] - 2026-10-07

### Fixed

- Local MobileSAM and EfficientSAM selection masks survive inference cleanup, rendering, undo and redo instead of leaving the document with a disposed bitmap.
- FLUX uses the reference-latent and inpaint-conditioning chain from the supplied Klein workflow, with native Euler/simple sampling. Mask blur follows nearest-neighbour resizing in model pixels; final coverage has an opaque core and an outward seam. Technical padding is removed before result resampling, preventing edge contamination. Runtime graphs use stock nodes without requiring Pixaroma.
- FLUX MP sizes describe the complete request including context. Explicit pixel sizes use the longest side, with preserved proportions and shared image/mask geometry.
- The floating selection panel can collapse and stays collapsed across selections, documents and relaunches until reopened. Alignment has spacing and adjacent horizontal/vertical flip buttons; color swatches sit directly below the tools. Object Selection and Selection Brush expose Contract alongside Expand and Feather.

### Changed

- Content-Aware Fill has a larger resizable workspace, direct pixel display and an optional reduced-resolution fast preview. Accepting computes the final fill at full resolution.
- Curves retain the pointer's grab offset, allow horizontal endpoint movement and use a natural cubic spline. Existing curve points remain readable; their interpolated appearance can differ from earlier builds.
- Healing Brush has its own brush-and-cross icon in the toolbar and history. Healing Brush and Patch remain separate tools because their interactions differ.

### Added

- Experimental Windows scripts exchange flattened images between running Composa and Photoshop. The Windows packages include the scripts and instructions; real Photoshop exchange and live FLUX quality were not validated in this development session.

Verification: 957 automated tests passed locally (449 core and 508 headless UI), followed by 14 targeted icon/toolbar/history checks. Interface screenshots were inspected and the Photoshop helper's actual MCP handshake was checked.

## [1.2.1-preview.17] - 2026-10-06

### Added

- Local MobileSAM and EfficientSAM Ti with their real encoder/decoder weights, click and rectangle prompts, CPU inference, cancellation and one cached image embedding. Both choices are available for comparison without ComfyUI; their combined weights add about 82 MiB.
- Brush Flow independently of whole-stroke Opacity, Spacing, pressure size/flow switches, normal/full outlines, factory and saved brush presets. Stroke coverage uses sparse 16-bit tiles rather than a full-frame allocation.
- Source Healing Brush, donor-drag Patch with live preview, and a conventional Content-Aware Fill workspace with sampling exclusions, preview/cancel and repair-layer output. Texture synthesis now includes coarse-to-fine PatchMatch refinement and boundary color adaptation.
- Select and Mask with image-guided boundaries, smoothing, feather, contrast, edge shift, preview backgrounds, color decontamination and selection/mask/cutout output. Saved selections are undoable alpha channels that follow canvas edits.
- Pixel, position and transparency locks, including inherited folder locks.
- Editable Bezier paths with corner/smooth handles, node insertion/deletion, path-to-selection and vector masks.
- Non-destructive filter stacks on raster layers and smart objects, with per-filter edit/enable/delete controls and retained source pixels. Smart-object updates and canvas resizing preserve the stack.
- Multi-stop gradients with independent opacity stops, presets and a checkerboard preview, for the Gradient tool and Gradient Overlay effect.
- Native project format 10 retains paths, vector masks, smart filters, channels, locks and gradient stops. Older projects remain readable. PSD export reports features saved as rendered pixels.

### Changed

- Removed optional heavyweight native BiRefNet and its setup flow. Old preferences migrate to U²-Net lite; bundled lightweight models and real ComfyUI BiRefNet/HR/matting choices remain available.
- Object alignment now uses the second selected root object, retaining selection order across undo, duplicates and added selections. Alignment/distribution buttons share the single options row; position, size, rotation and shape properties remain directly accessible in that row.
- The floating AI selection panel retains its established compact layout, with prompt, references, workflow and Generate/Remove controls.
- ComfyUI Settings separates Connection, Workflows & models, Prompts and API access into tabs; invalid URLs cannot be saved. Generation dialogs group workflow, instructions, optional references and output settings. The compact AI menu remains accessible while disconnected.

## [1.2.1-preview.16] - 2026-10-05

### Added

- Character-size runs inside one text block, with selection-aware Type controls, matching caret/line layout, undo, native project persistence and editable Photoshop text interchange. Native project format is now version 9; older projects remain readable.
- Embedded Photoshop PSD/PSB and raster smart-object sources, shared instances, affine/perspective placement and masks. Linked external files, warps, vector masks and smart filters retain compatibility pixels with an explicit conversion report.
- Photoshop layer shadows, glows, color overlays and supported strokes, including separate fill opacity. Unsupported effects and advanced parameters are reported instead of silently dropped.
- Move-bar alignment to the canvas or first-selected object in all six directions, plus equal horizontal/vertical gaps. Commands move selected objects/groups in one undo step.
- Object Selection lists actual models from the connected ComfyUI server. Optional native BiRefNet FP32 runs without ComfyUI, verifies the selected ONNX file and releases its inference session after each operation. Its 973 MB weights are not bundled; a measured CPU run took about 13 seconds and peaked near 9 GB RAM.
- Assistant Settings can select a complete llama.cpp folder and create a portable server launcher using the application's model, address and context settings.

### Fixed

- Move can pick text throughout its editable box, including whitespace, while preserving visible-layer order and masks.
- Cropped-away PSD layers use a minimal transparent placeholder rather than allocating a full canvas.
- Headless/transient windows do not issue background update requests that shift the canvas during pointer checks.

Verification: automated core and headless UI checks cover mixed text sizes, PSD sources/effects, alignment, server model choices and launcher generation. Interface screenshots and an actual native BiRefNet portrait mask were inspected. Photoshop itself was not run.

## [1.2.1-preview.15] - 2026-10-05

### Fixed

- FLUX masked MP editing sizes the selected region as requested and scales its context uniformly with it. Image and masks share their content dimensions and right/bottom padding; padding is removed before placement. The dialog shows both selected-area and actual context dimensions, and unexpected output sizes are rejected in every size mode.
- Conditioning blur is applied in source coordinates before resizing, including wide blur values. Fill's Mask grow now affects conditioning without enlarging final selection coverage. A separate binary noise mask fully denoises the repair halo instead of repeatedly mixing unfinished soft-mask latents; editable layer masks still blend once.
- Object removal uses a finite outward transition with an opaque replacement core, preventing Gaussian mask tails from changing distant background. Color matching extends a smooth local tone field from unchanged generated surroundings, distinguishes subtle/strong, corrects soft edges once and preserves texture amplitude without contrast amplification or synthetic grain. A known decoded ROI permits stronger exposure correction while unidentified legacy results retain their conservative bound.
- FLUX reuses its source latent instead of redundant inpainting encodes. Advanced adds Auto / Lower VRAM / Standard execution: Lower VRAM places the text encoder on CPU and tiles VAE work, retaining selected model files. Auto selects this path on Intel XPU to avoid repeated large text-encoder transfers; Standard retains normal server placement.
- Added resolution, padding, finite-mask, color and UI regression checks, plus opt-in live ComfyUI pixel-registration, VAE comparison and photographic task/undo checks. Existing model choices, task profiles and UI styling are retained.

## [1.2.1-preview.14] - 2026-10-05

### Added

- On-device object and subject selection using bundled U²-Net lite and MODNet models, adapted from original Composa 1.4. The Object Selection options bar chooses U²-Net lite (default), MODNet, Plain backdrop or the existing ComfyUI workflow; the choice persists across tabs and launches.
- Local selection by point or search rectangle, sampling the active layer or all visible layers, with Shift/Alt combination, undo and cancellable background inference. Select > Subject shares the model choice. Missing model/runtime falls back to Plain backdrop with an explicit status message.
- CPU-only ONNX Runtime loaded on demand, pinned model size/SHA-256 verification, model fetching during packaging and complete notices in Windows and Linux builds. No model is downloaded at application runtime.

## [1.2.1-preview.13] - 2026-10-04

### Added

- PSD opening and saving with a merged compatibility preview, editable horizontal text and supported vector shapes, layer masks, groups and blend modes. Unsupported effects, perspective and adjustment settings produce an explicit conversion report; native `.cmps` projects retain editor settings.
- Gradient Overlay in Layer Effects, with color, angle, scale, radial and reverse controls.
- Installed font styles in the Type bar instead of Bold/Italic buttons. Exact weight, width and slant follow character selections, undo, project saves and editable PSD text. New project saves use format version 8; older projects remain readable.
- Editable shape fill, stroke, stroke width and supported corner radii directly in the options bar.

### Fixed

- Alt-drag duplicates selected objects and moves the copies in one undo step; cancelling removes the copies.
- Move selects the visible object under the pointer rather than always dragging the top layer. Empty space and clicks outside the canvas clear the layer selection.
- Filled selections and gradient objects use transform frames and inspector sizes around their non-transparent content, preserving the original source pixels and masks.
- Native controls keep the editor's blue accent instead of inheriting a different Windows accent.

Verification: 837 automated tests passed (368 core and 469 headless UI), with inspected interface screenshots and project/PSD reopen checks. Earlier PSD validation also checked compatibility caches with ImageMagick and native records with an independent reader. Photoshop itself was not run. PSD interchange uses the existing C#/Skia stack without adding a runtime library; some Photoshop features still require conversion.

## [1.2.1-preview.12] - 2026-10-02

### Changed

- Every workflow/task pair has independent Advanced and generation preferences, seeded from existing settings without resetting other operations. FLUX Expand starts with Original size, 1 MP references, List execution, grow/blend/conditioning blur 16/48/16 px, context 2, Color match subtle and random seed. Advanced titles identify the operation, and workflow switching restores independent values and unsaved dialog drafts.

### Fixed

- Original-size FLUX masked crops use right/bottom padding for VAE alignment instead of image/mask rescaling followed by an inverse resize. Decoded pixels return unscaled to their saved coordinates; technical padding is discarded and unexpected dimensions are rejected. Explicit MP and Expand minimum-side budgets remain intentionally scaled.
- Expand color matching uses the expanded source context. Programmatic variant-control refresh no longer overwrites another workflow's settings.

Verification: 752 automated tests passed. A live local ComfyUI stock-node check preserved every pixel in an odd-size image crop and its padded mask without using a model or paid API. Generative model detail drift is not eliminated by this host-coordinate fix.

## [1.2.1-preview.11] - 2026-10-02

### Changed

- FLUX Generative Fill starts with Mask grow 4 px, Mask blend 8 px, conditioning blur 4 px and Mask context 1.2. Its values persist independently of Expand and other local operations; existing GPT settings remain unchanged. Context preview uses the Fill-specific values.

Verification: 745 automated tests passed, including migration, persistence, Advanced controls and context bounds.

## [1.2.1-preview.10] - 2026-10-02

### Added

- Optional selection-context bounds overlay, session-only API key prompt before paid uploads, and stable floating-panel placement until closed.
- Requested defaults migrate once: Original size; FLUX grow/blend/conditioning blur 16/48/16 px, color match off, context 2; GPT grow/blend 4/8 px, padding 0; LoRAs off. Generate Image starts with GPT, Fill and Expand with FLUX. Later user preferences remain persistent.

### Fixed

- Bundled FLUX and GPT masked results retain unmasked generated context pixels under a separate editable layer mask, instead of baking the cutout and feathering twice. Crop coordinates and output placement remain in source pixels. Custom workflow compatibility remains unchanged.
- Expanded canvases refit the active Crop frame and recenter. GPT Expand always receives its dedicated automatic prompt, including whole-image mode with no user prompt.
- Generate Image Advanced hides mask/context controls; LoRA strengths use double-click numeric entry without the duplicate 123 button.

Verification: 743 automated tests passed. Live ComfyUI/GPU generation and paid GPT image quality could not be tested on this computer.

## [1.2.1-preview.9] - 2026-10-02

### Added

- Object Selection AI supports Shift to add and Alt to subtract. Its rectangle is an independent search region; the existing selection remains intact until the mask arrives, and combination is one undoable change.
- ComfyUI Settings assigns compatible workflow packs independently per task. Fill, image generation and Expand keep separate choices; an unavailable explicit assignment is reported instead of silently selecting another pack.
- Generate Image and Image Edit dialogs share the floating panel's ordered reference editor: up to six images, file selection, clipboard paste, drop, previews and removal, with live paid-cost estimates.
- The Assistant uses a shared native MCP operation catalog with full schemas, bounded dynamic tool discovery, structured execution receipts and changed-layer identities. Structured document inspection and explicit postcondition verification are available to both MCP and chat. Task intent replaces edit-verb matching; declared inspect tasks cannot mutate documents and the host independently rechecks final assertions on actual output ids before commit.
- General query/batch layer tools filter by native type, geometry, name, tag and group, then compute panel order/start/step deterministically. Batch changes validate all targets before editing and preserve unmatched layers, masks and live geometry in one Undo entry. Verified already-satisfied requests are valid no-ops.
- Task plans and a bounded operation journal survive conversation compaction; task declaration is removed from the tool list once accepted. The scripting manual loads on demand, context-aware pruning preserves complete tool exchanges, and enabled vision receives the actual native render region/grid.
- Agent contract tests and a real-model acceptance suite cover basic shapes/guides, grouped text, masks, alternating-layer batches and embedded smart objects, including preservation and Undo/Redo checks. See [agent architecture and tests](docs/assistant-agent.md).

### Fixed

- Assistant tool JSON is no longer truncated mid-object. Native editor allocations are excluded from the JavaScript interpreter memory limit while native surface budgets and script execution limits remain active. Local llama.cpp schemas normalize unrestricted boolean schema nodes without changing their meaning.

Verification: 732 automated tests passed, plus six real Qwen 3.5 9B acceptance scenarios covering shapes, guides, grouped text, masks, alternating-layer edits and smart objects with Undo/Redo. This is a finite acceptance suite, not a guarantee of arbitrary model answers.

## [1.2.1-preview.8] - 2026-10-01

### Fixed

- GPT selection edits send only a rectangular source crop with independent context padding and ordered user references, without native masks, extra mask-guide images or crop-coordinate prompts. Ordinary Fill retains its original image; Remove and Expand keep their black repair areas and dedicated instructions. Working FLUX generation is unchanged.
- GPT Advanced offers context padding in source pixels (32 px per side by default), separate from local Mask blend and FLUX context/conditioning blur. Cost estimates count only images actually sent. Results return to the exact saved crop size and position, preserve pixels outside the local mask and apply feathering once; identical-size results avoid unnecessary resampling.

Paid GPT visual quality still requires a funded-account test. Automated tests cover crops, references, boundaries, soft masks, placement, variants and Undo without spending credits.

## [1.2.1-preview.7] - 2026-10-01

### Added

- Native `set_shape` and scripting `setShapeColor` recolor editable shapes directly. The Assistant receives bounded, valid paged JSON instead of truncated layer records, sees actual shape styles, and has explicit guidance for alternating-layer edits and one-shot batch scripts; prose-only editing claims are reported as failures. A live Qwen 9B test covers alternating-square recoloring and one-step Undo.
- Object Selection AI in the Magic Wand group draws a rectangle and processes only its region. Existing selections also constrain segmentation; masks return to canvas coordinates. Duplicate AI Select Subject menu entries are removed (script compatibility remains).
- Remove Object tool below Brush paints a selection and removes it on release without a floating panel. Paint Bucket joins the Gradient group and fills connected pixels with selection, transform and undo support. New tools have distinct vector icons.

### Fixed

- GPT masked fill sends a visible black editing patch and crop-relative region instructions in addition to mask guidance; local compositing still preserves untouched pixels. Relight/Harmonize do not erase their source subject.
- Expand has a fixed English instruction, no prompt field and no stale Gen Fill prompt. With selection it uses masked fill and hides mode choice; without selection or from Crop, Empty area only is the default and whole-image regeneration remains optional. A bounded overlap and inward blend soften the expansion seam.
- Crop starts with an immediately adjustable image-filling frame in the chosen aspect ratio. The floating AI panel can be dragged from the whole blank header area while its buttons remain clickable.

Paid GPT inference still needs visual testing on a funded account; input, mask-confinement, placement and UI tests do not spend credits.

## [1.2.1-preview.6] - 2026-10-01

### Fixed

- GPT masked replacement restores the untouched source outside the allowed edit area. Final feathering is inward and capped for small selections, separate from the larger conditioning mask and context; reference-mask guidance cannot accidentally replace the background. Answers with incompatible proportions are rejected instead of stretched into the selection.
- GPT requests use explicit Custom dimensions with uniform sizing under the official limits, rather than Auto or square stretching. Generate Image defaults to canvas dimensions; MP sizing preserves proportions. Size constraints are shown before generation.
- Native healing repairs previously skipped pixels when full-resolution donor remapping crosses the excluded area, using the bounded synthesized guide as a fallback instead of leaving original spots or stripes.

### Added

- Generative Expand offers FLUX/GPT selection, default empty-area-only masked generation with context and 768–2048 px minimum-side presets, or whole-image regeneration using the expanded canvas dimensions by default. Expand can fill existing transparent canvas with or without a selection; its black input and result placement preserve geometry and undo the canvas change together.
- Image Edit is available without a selection. Remove, Change Background, Harmonize and Relight can also use the complete image without an artificial mask; only Generative Fill requires a selection. Background replacement still separates the original subject when a selection is supplied.
- Each workflow pack has its own optional editable additional prompt, preserving existing custom instructions. All generation windows have a model picker and joined Generate + 1/2/3 control. Seed remains in Advanced; GPT quality is named separately from reasoning, which its image node does not expose.
- Costs show Comfy credits / USD using the documented 211 credits per dollar, including references, mask guides and variants. LoRA strength accepts direct numeric entry and is restricted to 0–3.

Paid GPT inference still requires testing with a funded account; automated tests verify input graphs, geometry, mask confinement and UI without billing.

## [1.2.1-preview.5] - 2026-10-01

### Added

- CHAT GPT 2.5 Engine Pack uses the official ComfyUI OpenAIGPTImageNodeV2 and the requested gpt-image-2.5-sunburst model. No local model weights are needed. Supports text generation, ordered references, masked fill/removal, expansion, background replacement, harmonization and relighting. With multiple images the node's native-mask restriction is handled explicitly using a mask guide and local blending.
- Paid Partner Nodes accept a Comfy.org API key from an environment variable or the current session, never saved in preferences or workflows. The connected server's own price-badge tables supply approximate estimates including all inputs/variants. Binary node progress supplies reported credit costs where supported; local-account balance and OpenAI usage tokens are explicitly unavailable.
- Advanced offers up to three FLUX model-only LoRAs from the connected server, individual strengths and enable switches, with an overall enable switch. Missing models, unsupported settings and incompatible packs fail preflight before image uploads or paid submissions.

### Improved

- Floating AI panel has a bottom-left pack selector, compact 1/2/3 dropdown after Generate, completion status above the actions, centered picture-icon reference cells and responsive thumbnail wrapping. FLUX display no longer includes XPU. GPT Advanced hides local-model/LoRA/seed controls and offers server-supported size and quality. Two variants also work in local List/Batch mode.
- Native Content-Aware Fill and Spot Healing keep exact whole-patch matches fast, but synthesize difficult holes from coherent small exemplars using boundary/structure priorities, nearby donor searches and bounded color adaptation. Guide matching is bounded in size while the output samples original-resolution texture; no models or native libraries are added.
- Healing searches respect the actual selected repair area while excluding the full brush footprint from donor samples. Donor patches are checked against every excluded pixel, soft coverage is applied once, and a completely selected layer with no evidence is left intact.

## [1.2.1-preview.4] - 2026-10-01

### Added

- Drop images onto document tabs or the empty tab strip to open separate documents in file order. Dropping onto the canvas still places image layers; a drop is never applied twice.

### Fixed

- Remove finishing no longer synthesizes random grain from edge-detail statistics. A toggleable, editable image-preservation prompt is available in ComfyUI Settings.
- Bundled masked Klein edits sample an encoded source latent with a real noise mask, rather than regenerating the complete context crop. Installed Pixaroma crop/stitch nodes are used automatically with aspect-preserving sizing, outward-only seam feathering and optional context-based color matching; conditioning blur and color match are separate Advanced settings. Final stitched outputs are not masked a second time.

Masked Remove was verified against a live ComfyUI GPU server in both single-image List and three-image Batch modes. Projects continue to use format version 7.

## [1.2.1-preview.3] - 2026-10-01

### Added

- One or three AI variants, sequential low-VRAM List or native Batch execution, reusable uploads, exclusive variant groups and undoable result switching. Cancellation/failure never inserts a partial batch; a result refuses to overwrite a changed document state.
- Separate AI Advanced options for generation, references and mask context/grow/blend; ComfyUI Settings focuses on server connectivity and actual model lists. Upscale offers ×2 and ×4, preserving odd dimensions, selection masks and source detail with final server-side sizing.
- Embedded smart objects with layered contents opened in a tab, Ctrl+S/Save contents updates to shared instances, independent copies, masks, transforms, undo/redo and project serialization. Smart sources are stored once and protected from accidental pixel painting; scripts and native agent tools can convert/copy/rasterize them.

Projects saved by this release use format version 7 and require this release or newer to open. Live GPU Batch inference remains unverified while the local ComfyUI server is offline; workflow execution is covered by automated tests.

### Improved

- Soft brush rendering uses a small per-stroke squared-radius lookup table and selective subpixel integration at tiny/hard edges; broad soft brushes do not allocate oversampled stamps.

## [1.2.1-preview.2] - 2026-09-30

### Added

- ComfyUI Settings lists model choices from the connected server per workflow loader (diffusion, text encoder, VAE, upscale and subject selection), preserving selections separately for each endpoint. Shared folders and LAN servers require no local model files in Composa.

### Fixed

- Workflow model identifiers resolve unique matching filenames in server subfolders. Missing/ambiguous models and nodes are checked before uploading source images; failed refreshes discard stale server lists, and explicit script parameters can still override saved choices.

## [1.2.1-preview.1] - 2026-09-30

First downloadable AI preview of the alvasafin-art fork. Self-contained Windows packages include the ready executable and launch batch file; neither the .NET SDK nor a separately installed .NET runtime is required. AI services and model weights are configured separately and are not bundled.

Catches up with Compositor 1.3.3 to 1.4: Select > Color Range, fonts per letter and the smaller items.

### Added

- ComfyUI integration with task-specific workflows, contextual selection actions, reference images and megapixel sizing, mask context/blend/grow controls, non-destructive generated layers and model-based upscaling.
- Built-in conversational Assistant with native editor tools, actual document/layer context, undoable transactions, error recovery and duplicate-command protection. Script requests return reusable code instead of applying edits automatically.
- JavaScript script editor, reusable script library and installable script plugins. Native awaited input forms, real ruler guides and renderer-based text fitting are available to scripts and the Assistant.
- Windows launch batch file in the portable package. Ready packages omit build caches, native debug symbols, AI model weights and credentials.
- Select > Color Range: click a color in the image to select it everywhere, then adjust Fuzziness and add or remove colors with the eyedroppers, or with Shift and Alt. Invert selects everything else, such as the subject in front of a green screen. The panel sits beside the canvas rather than over it, shows the selection in black and white, and the marching ants follow on the canvas as you go; OK keeps the selection as one undo step. An agent gets it as the select_color_range tool.
- The font, Bold and Italic can differ from letter to letter: select some of the text while typing and choose a family or tick Bold or Italic, and only those letters take it, as a color already does. The menu says (Multiple) for a selection in several families, and choosing one from it puts them all in that family. Project files that use this are format version 5.
- A History panel under the Layers panel lists every step, oldest first, and goes back or forward any number of them in one click, as Photoshop's does. Press on the list and drag to scrub through the steps with the canvas following. Steps Redo would bring back are dimmed until the next change drops them, the step in the saved file carries a disk, and each step has an icon for what it did. Click its header to collapse it, drag the line above it to size it, and use the new Window menu to hide or show it; the layout is remembered between launches.
- View > Grid Settings: the layout grid's color (Photoshop's set or a custom one), solid, dashed or dotted lines, their opacity, the pixels between gridlines (2 to 4096) and the subdivisions per square (1 to 64, never finer than a pixel). The grid shows while the dialog is open and follows every change; Cancel puts it back. The settings are remembered between launches like the other view options.
- Marquees, shapes and a selection outline being moved snap to the View > Snap To targets (guides, the grid, layer edges and the canvas edges), as moved layers and crop boxes already did. Ctrl places them freely, and Shift pressed while moving an outline keeps it on one axis.
- New Canvas opens on the size of the image on the clipboard: a Clipboard preset of that size is listed first and selected, so what you paste next fills the canvas exactly, and every other preset is a choice away.
- Layer > Merge Visible combines the visible layers and leaves the hidden ones; Layer > Stamp Visible (Ctrl+Alt+Shift+E) puts the picture as it looks on a new layer on top and keeps every layer.
- Image > Reveal All grows the canvas back to every layer, the way back from a crop; Image > Duplicate opens a copy of the document in a new tab.
- Edit > Paste Special: Paste in Place (Ctrl+Shift+V) keeps the place pixels were copied from even partly outside the canvas, and Paste Into (Ctrl+Alt+Shift+V) pastes onto a new layer masked by the selection.
- Right-click a document's tab for Copy Image (its whole flattened picture, whatever is selected), Duplicate, Open Containing Folder, Close and Close Others.
- The status bar says what a copy put on the clipboard, such as "Copied 1920 × 1080 px".
- New Canvas presets for screens and social formats: 4K, 1440p and 1080p; iPhone, MacBook Pro and Studio Display; Instagram Square, Portrait and Story and a YouTube thumbnail, with the print sizes kept. The preset follows a typed size.

### Changed

- Selection Brush shares the Magic Wand tool group and keeps its diameter outline visible during selection painting.
- Undoing back to the state that was saved counts as saved again: the tab's dot goes, and closing asks nothing. Before, any undo marked the document as changed.
- A right-click on a tab opens its menu instead of switching to it.
- Hue/Saturation raises saturation as Photoshop does: +50 doubles it and +100 saturates any color fully. Before, +100 tripled it, so imported Photoshop layers came out too strong at small amounts and too weak near the top.
- Tools that come in groups are picked as in Photoshop. Press and hold a toolbar button, or right-click it, and its group opens beside it: each tool with its icon, name and key, and a dot at the current one. Click a tool there, or keep holding, slide onto one and let go. Marquee, Lasso, Magic, Brush and Eraser, Smear and Shape have groups, marked by a small triangle in the button's corner, and each button shows the tool last picked, so Smear and Shape now show their mode and shape too. The options bar no longer has boxes for choosing a variant; it names the tool in use instead. The keys work as before.

## [1.2.0] - 2026-09-27

An AI agent can drive Composa, a photo can become a painting, and the catch-up with Compositor 1.2.11 and 1.3.2.

### Added

- AI control. Composa hosts a Model Context Protocol server, so an AI agent (Claude Code, Claude Desktop or any other MCP client) can edit the open documents through the editor's own commands. Tick Help > Allow AI Control; it is off by default and remembered between launches. Every change an agent makes is one undoable step that appears in the window as it happens, and Ctrl+Z takes it back like anything else. The status bar says "AI connected" while an agent is attached. The macOS app instead watches its project folder for changes made by other programs; Composa has the agent talk to the editor.
- Fifty-two tools cover the editor: create, open, save and export documents; place image and SVG files as layers; add layers, text, shapes and lines; select, rename, hide, reorder, duplicate, delete, move, resize and rotate layers and set their opacity and blend mode; paint with the brush, eraser, blur, smudge, dodge and burn; apply every adjustment in place or as an adjustment layer and every filter but Camera Raw; make and modify selections with the marquee, lasso, wand, object and subject; undo; and render the canvas to see the result. The document list, a document's layers and its render are also resources an agent can read by URI.
- `composa --mcp` is the bridge an MCP client launches to reach the running application. It outlives Composa: while Composa is not running the tools are simply absent and a call says so, and each time Composa starts the tools appear again, so Composa can be started, quit and updated without touching the client. A request that arrives while the connection is still being set up waits for it instead of failing, and a request in flight when Composa quits gets an answer instead of hanging. Add `--launch` to the command and the bridge starts Composa when nothing answers.
- Painterly filter, in the Filter menu and as a tool: repaints a layer in brush strokes that follow the picture, the largest brush first and each smaller one only where the picture still differs, so a photo becomes a painting that is still recognizably the same photo. Four styles (impressionist, expressionist, colorist wash and pointillist), a brush size that fits itself to the picture, the number of brushes and how closely to follow the picture; the same seed paints the same strokes. The strokes are painted with the brush engine's falloff, in parallel by bands, so a 1000 by 1500 photo takes about two seconds.
- Tools for drawing by hand, so an agent's lines and colors come from the picture instead of from a guess: the render can carry a grid labelled in canvas coordinates to read positions from, or show one region at full size; the colors at points can be read, from the screen or from one layer under the agent's strokes; the picture's edges come back as polylines, longest first; and many brush strokes go in one call as one undoable step, so a painting is no longer capped by a round trip per stroke.
- Letters of a text layer can have their own colors: select some of the text while typing and pick a color from the Type bar's swatch or the foreground swatch, and only those letters take it. With nothing selected, or on a text layer that is not open for typing, the color goes on all of the text as before. New letters take the color of the letter before them, the swatch shows the color at the caret, and Fill still paints every letter. Project files that use this are format version 4.
- Saving writes in the background: the document as it is when you press Save goes to disk off the UI thread, so the tools stay usable while a large project encodes, and only that version counts as saved. The status bar names the file while it writes; closing waits for a save still writing.
- Camera Raw's Color Grading group sits directly under Color and opens with it.
- Hue/Saturation, Black & White and Color Balance sliders show their colors on the track. Hue shows the hue circle centred on the selected range's color (red to red when colorizing), Saturation runs from gray to the range's color or the tint, Lightness from black to white, each Black & White family from dark to light in its own hue, and Color Balance from each color to its opposite. Camera Raw's Temperature, Tint, Vibrance, Saturation, Glow Warmth, Color Mixer, Color Grading and Calibration sliders show theirs too.
- Every slider in a dialog can be reset: double-click it to type and a Reset button appears on its left, which puts it back to the value that changes nothing, or to a filter's default. Camera Raw's sliders reset to a fresh grade's values.
- Motion Blur's angle has a dial beside the field, as the shadow effects have. It is drawn as a line through the centre, since a blur runs along one, and turns the full circle: the angle now runs from -180 to 180 rather than -90 to 90, as Photoshop's does.
- Drag a number's label to change its value, as in Photoshop: the transform bar's X, Y, W, H and angle, the text size, tracking and leading, the object selection's edge offset, and the width, height and resolution in the New Canvas, Canvas Size and Image Size dialogs. Dragging moves in whole numbers, Alt makes it ten times finer, and typing still takes decimals.
- A layer mask can be painted anywhere on the canvas, past the layer's own pixels, with the brush, a gradient or a fill. The mask grows with its layer; new area starts as the mask's background, so a hide-all mask stays black and a reveal-all mask stays white.

### Fixed

- Bold and italic text rendered plain when the font family has no bold or italic face available to the renderer, as the default family did not. The renderer now substitutes a face and, failing that, synthesizes the weight and slant.
- A text layer renamed by hand took its text back as its name when it was restyled or recolored. The name now follows the text only until it is named by hand.
- Closing the window or a tab while typing text commits the text first, so the save prompt appears and the text is in what gets saved. Before, a document with nothing else changed closed without a word and the text was lost.

### Removed

- The importer for projects saved by the macOS app (`.comp` folders), and the File menu item that opened them. Composa's own `.cmps` project files are unaffected.

## [1.1.0] - 2026-09-25

Catches up with Compositor 1.2.7 to 1.2.10 and reworks every slider.

### Added

- Sliders in the options bar and in every dialog are now fields whose fill is the slider: drag to change, Alt-drag for ten times finer steps, double-click to type a value, and the wheel and arrow keys step by one.
- Shadow effects turn their light with a dial beside the angle field. The bright dot points at the light and the dim stub marks where the shadow falls.
- Shift squares a marquee held from the start when there is no selection to add to. While adding to one, letting Shift go and pressing it again squares the marquee, as in Photoshop.
- A selected text layer previews the Type bar's color picker as the color changes, without being opened for typing.
- Photoshop Large Document (`.psb`) files open through the same importer as `.psd`.
- Simple Photoshop text arrives as editable text: horizontal type layers keep their wording, font, size, color, alignment, tracking and leading. Vertical, sheared or unevenly scaled text still becomes pixels, and the import report says what was dropped.
- SVG files open and place as image layers, drawn by ImageMagick's SVG renderer. Opened, an SVG becomes a document at the size it declares; placed, it is drawn to fit the canvas, so a small icon still comes in sharp.
- A Photoshop file that would not fit in memory has its layers and masks cropped to the canvas instead of being refused; the import report lists every layer that was cut. A file that fits imports exactly as before.
- Text being typed previews the color picker's working color on the canvas, from the Type bar's swatch and from the foreground swatch alike. Cancel puts its own color back.
- With the Move tool, a double-click on text opens it for typing where you clicked.

### Changed

- A document's total raster now has its own budget, separate from the limit on any one layer: a quarter of the machine's memory, between 200 and 800 megapixels. One layer, canvas or export may be up to 200 megapixels (was 100). A print banner with dozens of large layers no longer fails to open against a limit meant for a single image.
- Marching ants around a detailed Magic Wand selection are drawn from a screen-resolution outline when zoomed out, so a selection with hundreds of thousands of edges no longer takes seconds per redraw.
- Clicking with the Type tool puts the first baseline at the pointer, as Photoshop does, so the letters rise from where you clicked instead of appearing a line lower.
- The color picker puts saturation and brightness in the square and hue on the strip, as Photoshop does. Starting from black, one click in the square finds a color; before, the strip held the brightness and stayed at zero.

### Fixed

- Changing a size, font or color in the Type bar for a text layer that was not being typed opened it for typing and moved the keyboard to the canvas, so the rest of what was typed in the field landed in the text. The layer is now restyled in place, and a run of changes undoes as one step.
- With an effect row highlighted in the Layers panel, Delete removed the effect even after a selection was drawn. Changing the selection now drops the highlight, so Delete clears the selected pixels.

## [1.0.0] - 2026-09-23

The first stable release, and the first for Windows.

### Added

- Windows builds: an installer that needs no administrator rights and a portable zip, for x64 and arm64. The installer adds a Start menu entry, makes Composa the program for `.cmps` projects and offers it under Open with for images without taking any over.
- HEIC, AVIF, TIFF and camera RAW open on Windows with nothing else installed: the Windows build carries ImageMagick, with its licence notices next to the executable. Linux builds keep using the distribution's ImageMagick.

### Changed

- On Windows, preferences are kept in `%APPDATA%\Composa` and crash-recovery copies in `%LOCALAPPDATA%\Composa`. Linux keeps its XDG locations unchanged.
- When ImageMagick is available but cannot read a file either, the error now gives ImageMagick's reason instead of suggesting to install it.

## [0.3.0] - 2026-09-23

Catches up with Compositor 1.2.3 to 1.2.6.

### Added

- Camera Raw Filter: Light, Color (with Auto white balance and an eyedropper on the panel's thumbnail), Effects (texture, clarity, dehaze, glow, vignette, grain), Curve, Color Mixer, Color Grading, Detail, Optics and Calibration, each group switchable off without clearing it, and a histogram of the graded layer.
- Finishing filters: Vignette in any color, which on an empty layer paints across the whole canvas; Bloom / Glow; Tonal Contrast.
- Gaussian Blur, Motion Blur and Add Noise as adjustment layers. Add Noise, as a layer and as a filter, offers a Gaussian distribution.
- The Inner Glow layer effect.
- Image > Trim… with a choice of transparent pixels or a corner's color, and which edges to trim.
- Copy and paste whole layers with nothing selected, folders and adjustments included, within a project or into another tab.
- A right-click menu on every layer row for the layer, its folder and its mask.
- Crop ratios 3:4 and 9:16, and a crop box that starts at the selection.
- Composa reports when a newer version is available, as a dismissable strip rather than a dialog. It never downloads or installs anything; the notice links to the release page. The check is one anonymous request a day, it can be turned off under Help, and builds installed from the `.deb` or `.rpm` never check at all because apt and dnf own updates for them.

### Changed

- Zoom In and Zoom Out step through fixed stops (12.5% to 1600%), anchored on the view's center.
- Duplicate Layer and Ctrl+J duplicate every selected layer as one step; several copies stack together above the topmost original and end up selected.
- Lens Correction keeps only Remove Distortion; the vignette has a filter of its own.
- Grain's Roughness adds smaller particles whose size follows Size instead of one-pixel noise.
- Project files are written as format version 3, which older builds cannot open when they hold the new adjustment layers or effect.

## [0.2.0] - 2026-09-23

The first release with downloadable packages. Composa has been buildable from source for a while; this is the first version you can simply install.

### Added

- Downloads for Linux on x86-64 and arm64, in four formats: an AppImage that runs on any distribution, a `.deb`, an `.rpm`, and a portable tarball with a per-user install script. Every release is published with a `sha256sums.txt`.
- The `.deb` and `.rpm` install a launcher, icons and the `.cmps` file type, and recommend ImageMagick rather than requiring it: it is needed only to open HEIC, AVIF, TIFF and camera RAW files.
- The version is shown in the About dialog. It is derived from the git tag, so a build can always be identified.
- An icon set covering the Linux hicolor sizes, Windows and macOS.
- AppStream metadata, so the application appears properly in GNOME Software and KDE Discover.

### Changed

- The project is now called **Composa**. It was Compositor for Linux, a name that no longer fits now that Windows and macOS builds are planned, and one that invited confusion with the macOS app it reimplements.
- Projects are saved as `.cmps` rather than `.compositor`. Existing files still open, because the reader looks at the archive manifest rather than the file extension.
- Preferences and crash-recovery files moved from `~/.config/compositor` and `~/.cache/compositor` to `~/.config/composa` and `~/.cache/composa`. Settings from before the rename are not carried over.

### Fixed

- Camera RAW and HEIC files could report a misleading error instead of saying that ImageMagick was missing. ImageMagick is now located once and asked to identify itself rather than trusted for its name.

[Unreleased]: https://github.com/dvdstelt/Composa/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/dvdstelt/Composa/releases/tag/v0.2.0
