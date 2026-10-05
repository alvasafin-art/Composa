# Object Selection models

The options bar lists the bundled U²-Net lite, MODNet and Plain backdrop methods. Connecting ComfyUI adds the actual background-removal model choices reported by that server, such as BiRefNet, BiRefNet HR or BiRefNet matting when installed. Choices belong to the configured server; names are not guessed and unavailable variants are not advertised.

For native BiRefNet without ComfyUI, click **Local BiRefNet…**, open the linked pinned FP32 ONNX download, then choose that file. Composa verifies its size and SHA-256 before enabling it. The file remains where you selected it; it is not copied into the installation. The 973 MB model is optional and is not included in release packages.

The tested CPU implementation used about 9 GB of memory at its peak and took about 13 seconds for a 1536 × 2752 portrait on the development machine. Other machines and images differ. Inference memory is released after the operation. The smaller bundled models remain available for lighter workloads.

Native support currently uses the verified standard FP32 model, not HR, matting or FP16 variants. ComfyUI can offer those variants through its installed nodes/models. Native inference stays on this computer; a ComfyUI choice sends the image to the configured server.
