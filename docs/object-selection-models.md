# Object Selection models

The options bar lists bundled U²-Net lite, MODNet, MobileSAM, EfficientSAM Ti and Plain backdrop. MobileSAM and EfficientSAM Ti accept point/rectangle prompts through their real local encoder/decoder pipelines. Both are available for manual comparison; the existing default remains unchanged. Connecting ComfyUI adds actual background-removal choices reported by that server, such as BiRefNet, BiRefNet HR or BiRefNet matting when installed.

The heavyweight native BiRefNet option was removed. Old preferences selecting it migrate to U²-Net lite. MODNet remains a lightweight local option for people and hair. The four new SAM weight files add about 82 MiB, use the existing CPU runtime and require no Python or runtime model download. See [retouching and non-destructive editing](professional-editing.md).

BiRefNet, HR and matting are server choices when the connected ComfyUI offers them. Native inference stays on this computer; a ComfyUI choice sends the image to the configured server.
