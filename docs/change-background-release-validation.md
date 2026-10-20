# Change Background release validation — 20 October 2026

Scope: finish the current Change Background fix and release completed RGB stages. The user explicitly deferred the remaining larger audit features on 20 October. This document does not certify Photoshop parity.

## Failure and implementation

The first scene pass can change a product's pose, scale or outline. The old cleanup mask followed the original PNG only. Parts of the temporary generated product outside that outline survived, so placing the unchanged PNG over the plate produced a duplicate object or silhouette residue.

The production pipeline now verifies the already bundled EfficientSAM S models before submitting generation. It locates the temporary object in the actual arranged scene with room around the original bounds, keeps connected components anchored to the source alpha, and unions that mask with the original footprint before padded silhouette cleanup. Unrelated disconnected objects are excluded. Native SAM inference operates on its reduced image; its mask returns to full document coordinates. The original PNG, alpha, profile and placement are not replaced by the model silhouette.

Mask reliability is bounded by overlap and area checks. Failure, cancellation or a failed second pass applies no partial document change. SAM uses the existing pinned bundled models; this change adds no model downloads or weights. Harmonize's requested `integrate object into scene` prompt and workflow are unchanged.

## Photographic check

Real photographic source: [red shoe](https://images.unsplash.com/photo-1542291026-7eec264c27ff?w=900&h=650&fit=crop&q=85), cut out with SAM into a transparent PNG; [forest background](https://images.unsplash.com/photo-1448375240586-882707db888b?w=1003&h=731&fit=crop&q=80). Images are development inputs, not shipped assets. Two downloaded inputs total approximately 250 KB. Deleted historical artifact directories were not restored.

The ordinary `SceneProductLiveTests` production path saves each workflow, raw model result and VAE baseline, as well as separate background/subject and the final composite. It checks unchanged original pixels, exact subject alpha and original-subject colors for Change Background, and exact Undo/Redo. Empty plates must also be inspected visually; these assertions alone would not catch a second generated product.

- 1003 × 731 canvas, fractional position, 320 px source grid width, seed 73: both passes completed in 197 seconds including cold model loading. The empty plate was inspected separately: no remaining shoe/duplicate/silhouette patch; the neighboring plant and cup and the model-generated support remained. The final original PNG rests on that support.
- 1008 × 736 canvas, fractional position, 530 px source grid width, seed 73: both passes completed in 105 seconds. The empty plate and final composite were inspected separately; the temporary shoe was removed, the plant and cup retained complete shapes, and no duplicate or silhouette patch was visible. The model's support/shadow composition differs from the first example; exact physical grounding remains a model-quality limit.

Evidence for this release is confined to `artifacts/change-background-release`; model files and old large artifact trees are not recreated. Large final archive extraction/startup is performed on the release runner, not downloaded into another local staging tree.

## Regression and release gates

Core regressions cover changed silhouettes outside the old footprint, disconnected neighbors, preserved holes/context, immutable inputs, mismatched grids and cancellation. App contracts cover local and partner paths, atomic second-pass failure and failed isolation. Paid-provider image quality is not tested by these simulated contracts.

The release gate includes full Core/App checks, Linux/Windows CI and all twelve platform packages plus their checksum manifest. The Windows x64 release job extracts its actual portable archive, verifies version and bundled model presence, waits for the editor's UI event loop and checks clean shutdown. ARM64 packages are built but their Windows runtime is not exercised on the x64 runner.

Generic generation can still invent scene details or unsuitable lighting. Object isolation is not a universal semantic guarantee for heavily occluded, transparent, duplicated or substantially relocated subjects. The broader audit's Artboards, Actions/batch, Selective Color, editable fill layers, soft proof and remaining acceptance corpus are deferred; see [the implementation ledger](rgb-implementation.md).
