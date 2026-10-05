# Object Selection models

The options bar lists the bundled U²-Net lite, MODNet and Plain backdrop methods. Connecting ComfyUI adds the actual background-removal model choices reported by that server, such as BiRefNet, BiRefNet HR or BiRefNet matting when installed. Choices belong to the configured server; names are not guessed and unavailable variants are not advertised.

The heavyweight native BiRefNet option was removed. Old preferences selecting it migrate to U²-Net lite. MODNet remains a lightweight local option for people and hair. There is no replacement weight download or additional model in the installer.

BiRefNet, HR and matting are server choices when the connected ComfyUI offers them. Native inference stays on this computer; a ComfyUI choice sends the image to the configured server.
