# Object Selection models

The options bar lists bundled EfficientSAM S (Quality), MobileSAM (Fast), EfficientSAM Ti (Fast), U²-Net lite, MODNet and Plain backdrop. The three SAM models accept point/rectangle prompts through real local encoder/decoder pipelines. EfficientSAM S is the default for new preferences; older preferences that used MobileSAM or Ti migrate once to S. Other explicit choices remain unchanged. Connecting ComfyUI adds actual background-removal choices reported by that server, such as BiRefNet, BiRefNet HR or BiRefNet matting when installed.

MobileSAM's frozen ONNX crop is bypassed: low-resolution logits are resized to the model square, padding is cropped at the actual input proportions, and the remaining mask is mapped back to the photograph. Logits are resized before thresholding; confidence is not selection opacity. Image-guided refinement follows the original edges and removes faint background haze. Masks keep native coordinates and undo safely.

The heavyweight native BiRefNet option was removed. Old preferences selecting it migrate to U²-Net lite. MODNet remains a lightweight local option for people and hair. S adds 106,124,065 bytes of packaged weights. All SAM models use CPU ONNX Runtime and require no Python or runtime downloads. S uses more memory and CPU than Ti; detailed hair and transparent objects still benefit from Select and Mask. See [retouching and non-destructive editing](professional-editing.md).

BiRefNet, HR and matting are server choices when the connected ComfyUI offers them. Native inference stays on this computer; a ComfyUI choice sends the image to the configured server.
