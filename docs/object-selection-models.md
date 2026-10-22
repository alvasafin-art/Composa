# Object Selection models

The options bar offers only **SAM Quality** (bundled EfficientSAM S) and installed **BiRefNet** variants reported by the connected ComfyUI server. Older local choices and unsupported server model preferences migrate to SAM Quality. When disconnected, only SAM Quality is listed; no server model is invented.

SAM Quality accepts point/rectangle prompts through its local encoder/decoder pipeline. Image-guided refinement follows the original edges and removes faint background haze. Masks keep native coordinates and undo safely.

SAM Quality uses CPU ONNX Runtime and requires no Python or runtime downloads. Enable **Precise edges** next to the model menu to run the already bundled ViTMatte automatically after SAM. SAM identifies the object; matting refines its uncertain boundary into soft alpha at the original document size. This works for clicks, rectangles and Select > Subject. The normal unchecked mode keeps a crisp selection.

Precise edges takes longer and is remembered between tools, tabs and launches. It applies only to local SAM; server models keep their own output. It uses the original active layer before old masks/styles, or the visible RGB composition when Sample all layers is enabled. Source RGB16/ICC pixels remain unchanged. Replace/add/subtract/intersect and edge offsets apply to the completed result as one Undo step. Cancellation, changed source/layer/sample scope or a switched/closed tab discards the result, including the intermediate SAM selection. No new weights are included. Select and Mask remains available for manual corrections and decontamination. See [retouching and non-destructive editing](professional-editing.md).

BiRefNet, HR and matting are server choices when the connected ComfyUI offers them. Native inference stays on this computer; a ComfyUI choice sends the image to the configured server.
