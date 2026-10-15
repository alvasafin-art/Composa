# Scene editing validation — 15 October 2026

## Preview.28 follow-up: repeated inputs, background plates and shadow extent

The preview.27 checks did not establish that a clean plate was clean when inspected without the PNG. This investigation reproduced object-shaped color/texture residuals in cleanup and a rectangular color seam in an intermediate fix. Both were rejected. The source-minus-reconstruction correction was restoring object information to the background; limiting the replacement to a regenerated rectangle retained a second seam. Cleanup now removes a padded rectangular area from image conditioning and the image latent, using stock `VAEEncodeForInpaint` with a separate erasure mask. Its sampling mask covers the complete content canvas, with technical padding protected. The complete generated empty plate is inserted directly. No original-object reconstruction residual or regional patch is reapplied.

The full canvas was already resampled proportionally and padded only at technical edges, so divisibility by 16 was not the principal defect. Subject edits now additionally send the complete background alone, resampled to at most 512 pixels on the long side, as a distinct lighting reference. Prompts include the actual normalized object bounds and support position. Background illumination guides Harmonize; explicit Relight presets/manual angles keep priority. This improves context but is not a calibrated estimate of scene lights.

Repeated edits previously could treat generated pixels and their old cast shadow as the next source. Source/placement links now recover immutable original texture at the current placement and retire the selected result's previous subject/shadow. Legacy nested results recover preceding hidden originals and ancestor shadows where possible. The current visible background, including a chosen replacement, remains context. Hidden layers and variants remain excluded. New results are siblings, rather than nested inside their predecessors, and all changes undo together. Regression coverage also moves the generated object before repeating it and checks unrelated copies remain visible.

Contour lighting now extends reliable interior gains to thin/antialiased edges instead of leaving the old bright edge after dimming the interior. Reliable samples exclude a broader codec/model boundary ring. Shadow registration includes the lower quarter of the object, accommodating slanted shoe support; accepted connected penumbrae are retained in full. Default reach permits shadows throughout the canvas in every direction, rather than cropping to a small floor rectangle. The source PNG is never shifted to meet the generated shadow.

Final model checks used the same real photographic Worn Shoe cutout and forest-path photo as preview.27, on a 1003 × 731 canvas with fractional placement. Match to Scene was also inspected with a mirrored photographic background. Final reruns covered Change Background, three consecutive Harmonize generations (different seeds), and subject Relight. Original scene/background bytes supplied to generations two and three were exactly equal to those prepared for generation one, while previous results remained in the document. Source pixels and alpha were unchanged; protected background checks and Undo/Redo passed. Harmonize and Relight reported one-neighbor-pixel contact at both visible and opaque support contours. This measures contact, not physical plausibility of the cast shape.

The final empty plate was inspected separately, as well as underneath the exact original PNG: no shoe watermark, object-shaped color patch or rectangular cleanup join was observed in this case. Local records are in `artifacts/scene-fix28/verified`, with mirrored-scene comparisons in `artifacts/scene-fix28/final`. Rejected intermediate backgrounds remain an audit trail and are not validation results for the shipped implementation. A later optional person-scene rerun could not connect because the local server had stopped during the overnight interruption; the completed final photographic product runs are the live evidence for this release.

Runtime: portable ComfyUI 0.37, Torch 2.13 XPU, Intel Arc B580, FLUX.2 Klein 9B INT8 ConvRot, Qwen 3 8B FP8 and FLUX.2 VAE from the user's existing model directory. This runtime's large-safetensors loading crashed in native storage code; an isolated test launcher used per-tensor file-backed loading and conservative VRAM placement. It changed no installed ComfyUI or model files and is not shipped. This environment workaround is distinct from the application fixes.

Full validation passed **504 Core + 603 headless App = 1,107** tests; ten opt-in live tests were skipped in that ordinary run. Completed photographic product runs above were executed separately. New regressions exercise source lineage/placement, legacy repeats, opaque-photo background context, cleanup residual exclusion, contour gains, long shadows and native graph wiring. Paid-provider request/failure paths remain simulated; no live paid image-quality claim is made.

Remaining limitation: the general FLUX editing model can still choose imperfect cast geometry or light direction in mixed-light scenes. Full context, prompts and extraction safeguards do not establish Photoshop Harmonize parity. A specialized relighting/shadow backend still requires separate photographic evaluation. Poor source mattes can retain their own fringe colors.

The preview.27 and preview.26 reports below describe earlier implementations and remain an audit trail.

## Preview.27 follow-up: contact, halos and conditioning

The prior checks protected pixels and alpha but inadequately tested product grounding. A photographic transparent shoe reproduced a detached shadow and a second shoe generated beneath the original in Change Background. A second cutout used real SAM Quality intersected with original alpha, without white detection-backdrop contamination. Source: [Worn Shoe](https://commons.wikimedia.org/wiki/File:Worn_Shoe.png), Aupajo, CC0. Both PNGs were tested on a photographic forest path at fractional placement, with 1003 × 731 and 1008 × 736 documents. The defect occurred at divisible-by-16 dimensions too: padding already cropped correctly; shape drift and compositing required separate fixes.

Shadow permission now uses support geometry and permits an underlay at the antialiased base without applying foreground coverage twice. Ground changes are recovered from mixed pixels with foreground lighting discounted. Significant cast components, not a minimum-gap speck, drive bounded support registration. It moves the shadow only and rejects unrelated/strongly displaced darkening. The source PNG geometry and alpha are unchanged. A calibrated physical shadow solution is still not established.

Lighting uses an actual source VAE encode/decode control, normalized floating-point log gains and reliable interior samples. The former brightest-region direction guess and generic added-rim-light instruction were removed. Background layout now sees the actual object and protects its foreground. A second masked image edit removes its cache from the arranged scene, with feathered expansion and codec compensation. It no longer uses approximate filled pixels as the cleanup reference or repeats placement instructions that can reintroduce a shoe.

Live runs save separate layers and contact metrics at both visible and opaque contours, plus source/alpha/protected-pixel and Undo/Redo checks. Regressions cover codec-only change, outer glow, alpha-fringe contact, translucent product bases, detached dominant components and cleanup boundaries. Photographic inspection remains necessary: a tiny touching fragment does not prove that the main cast shape is plausible. Paid-provider paths are tested without spending credits; real paid image quality is not claimed.

Final product contact checks reported an opaque-contour gap of one neighboring pixel for Match to Scene and Relight, with zero changed protected background pixels. Odd/aligned size comparisons showed that non-multiple dimensions alone did not explain the defect. Background replacement no longer inserted a second shoe. These metrics confirm contact/protection for these cases, not universal photographic realism: the generic model can still choose an imperfect cast shape or perspective, and the source product pose/placement affects the result.

Preview.27 ordinary full validation passed **496 Core + 602 App = 1,098** tests, with ten opt-in tests skipped. Product-model checks above ran separately. The two-pass Nano Banana/GPT contract and second-pass failure rollback are included in the ordinary suite.

The preview.26 report below remains an audit trail; its single-pass background and bright-region inference descriptions refer to the earlier implementation.

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

Higher editor priorities include document/display ICC workflows, 16-bit editing and Blend If. Existing affine/perspective transforms and basic pixel-push Liquify would benefit from editable meshes, freeze masks and more deformation controls. Independent smart-filter masks, linked smart-object sources, useful batch-export/actions and Photoshop-authored PSD fixtures remain follow-up work.
