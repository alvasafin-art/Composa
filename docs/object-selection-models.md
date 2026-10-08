# Object Selection models

The options bar offers only **SAM Quality** (bundled EfficientSAM S) and installed **BiRefNet** variants reported by the connected ComfyUI server. Older local choices and unsupported server model preferences migrate to SAM Quality. When disconnected, only SAM Quality is listed; no server model is invented.

SAM Quality accepts point/rectangle prompts through its local encoder/decoder pipeline. Image-guided refinement follows the original edges and removes faint background haze. Masks keep native coordinates and undo safely.

SAM Quality uses CPU ONNX Runtime and requires no Python or runtime downloads. Detailed hair and transparent objects still benefit from Select and Mask. See [retouching and non-destructive editing](professional-editing.md).

BiRefNet, HR and matting are server choices when the connected ComfyUI offers them. Native inference stays on this computer; a ComfyUI choice sends the image to the configured server.
