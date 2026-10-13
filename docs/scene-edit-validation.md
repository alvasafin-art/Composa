# Scene editing validation — 13 October 2026

This is an experimental scene-compositing implementation, not a measured claim of Photoshop parity. The intended behavior follows Adobe Harmonize: use the background to change the object's lighting and grounding, rather than only matching global color statistics. See [scene editing](scene-matching.md) for controls, setup, research links and limits.

## Actual photographic checks

Local ComfyUI 0.37, PyTorch XPU, Intel Arc B580 12 GB, FLUX.2 Klein 9B INT8 ConvRot, Qwen 3 8B FP8 and FLUX.2 VAE. The weights were read from a separate ComfyUI Desktop model directory through extra-model-paths configuration. No model files are included in the application release.

The foreground was a photographic person cut out using SAM Quality and saved as a transparent PNG, 544 × 1070. It was placed on two photographic forest paths, sunset (1020 × 660 document) and daylight (1020 × 496). All checks used these photos and PNG, not geometric objects. Small blue remnants in the source matte were treated as source-mask defects, rather than proof of correct generated lighting.

Background attribution: [Forest Path Sunset](https://commons.wikimedia.org/wiki/File:Forest_Path_Sunset.jpg), 44833 / Pixabay, CC0; [Daylight through forest 001](https://commons.wikimedia.org/wiki/File:Daylight_through_forest_001.jpg), BVBurton, CC BY-SA 4.0. Local daylight comparison images are adaptations with resizing and PNG compositing under that license. Photographic assets are not copied into the repository.

The first denoise comparison (0.55, 0.80, 1.00) exposed a long, overly dark shadow. It was rejected. Subsequent photographic runs identified a green residual silhouette in background replacement and a default prompt that contradicted relighting. These were corrected before final validation:

- Conservative direction guidance and a permitted floor region replaced unrestricted shadow extraction.
- Linear-light darkening is converted into encoded-RGB layer alpha, so normal-over compositing does not exaggerate shadow darkness. A small edge blur reduces extraction noise.
- A PNG background replacement starts from the actual scene with the PNG hidden, rather than approximate filling of the subject-shaped hole.
- The generic default instruction to preserve exposure/white balance is omitted for scene lighting; explicit custom workflow instructions remain active.

Final checks covered Match to Scene at denoise 0.8 on both backgrounds; Relight with soft studio-left, Golden Hour and Neon; and Change Background at 0.95 into a gray studio. Each run saved before/after PNGs, a layered project and operation timing. The original source bitmap bytes, subject alpha, protected background pixels and full-render Undo/Redo were checked directly. Background replacement additionally checked that Original Subject exactly equals the source's world-space rendered cutout.

Observed behavior: daylight matching changes the subject's illumination and adds grounded darkening on the path; sunset results remain more conservative. Neon clearly creates cyan/magenta subject light while retaining the original silhouette and texture. Golden Hour warms subject light. Gray-studio background replacement removes the residual silhouette and retains the original person. Background replacement alone leaves the subject without integrated studio contact lighting; a subsequent Match to Scene is required.

The model can still produce an inaccurate or incomplete cast shadow, particularly near the bottom of the canvas, with little floor, multiple lights or complex reflective materials. The inference is two-dimensional. Soft studio-left had only a small visible effect on a source already lit from the left. These are practical limits; passing transport and pixel-protection tests does not certify photographic realism.

## Automated and failure checks

Coverage includes encoded-image latent wiring (no empty or blanked latent), technical padding excluded from the noise mask, correct silhouette and shadow separation, shadow alpha color-space conversion, exact PNG preservation in background replacement, profile cancel behavior, practical preset controls, sequential variants, immutable source pixels, one-step Undo/Redo, cancellation/failure without partial layers and scene calls inside a script's enclosing transaction. Existing full-image and provider tests remain separate from scene extraction tests.

The ordinary full suite passed **490 Core + 601 headless App = 1,091** tests; nine opt-in tests were skipped. It ran with the GPU server stopped to avoid competing allocations. The photographic scene test was run separately with the real model for the final six scenarios, and repeated for the two Match to Scene photos after the final instruction update. Paid GPT/Nano Banana paths use simulated schema, uploads and responses; no credits were spent and no live paid-provider image-quality claim is made.

To reproduce the opt-in photographic test, provide `sunset-before.cmps` and `daylight-before.cmps` with a photographic background and active PNG layer, then set `COMPOSA_SCENE_TEST_URL`, `COMPOSA_SCENE_TEST_INPUT`, `COMPOSA_SCENE_TEST_OUTPUT` and optionally `COMPOSA_SCENE_TEST_CASES`. Cases include `sunset-0.8,daylight-0.8,relight-0.8,neon-0.8,golden-0.8,background-0.95`. Run the `SceneEditingLiveTests` test filter. Inspect the saved photos; assertions verify document integrity, not artistic judgment.

## Remaining editor/AI work from the broader audit

Higher priorities are photo-quality matting for hair/translucent edges; a clear per-task readiness display for models and nodes; real-image regression coverage for Fill/Expand/Remove across engines; reference/provider limits visible before submission; and repeatable lighting/shadow controls beyond prompt presets. Dedicated IC-Light or shadow-generation backends need their own model licensing, runtime and photographic evaluation.

For everyday Photoshop-like editing, remaining gaps include brush-based object removal/refinement, perspective/warp and Liquify workflows, independent smart-filter masks, linked smart-object source replacement, useful batch-export/action presets and additional PSD interchange fixtures authored by Photoshop. These were researched as follow-up work; they are not claimed as implemented in this scene release.
