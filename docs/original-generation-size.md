# Original generation size

In preview.23, **Original size** keeps an input at or above 1 MP unchanged. A smaller input is enlarged uniformly to at least 1 MP before generation. FLUX may pad to the next multiple of 16; this padding is discarded on insertion.

For a selected edit, the input is the automatic context crop including the selection, not the isolated selection rectangle. The image and conditioning mask use the same scale. The generated result is fitted back into the original document coordinates; the source canvas size and output mask are retained. Generation still ignores the application's legacy mask grow, blend, blur and context controls.

For ordinary Generate Image, the requested original canvas aspect ratio is retained. Explicit megapixel and long-side choices keep their existing behavior; their dimensions are not forced to 1 MP. Upscale and object selection are not affected by this generation minimum.

FLUX conditioning and seamless finishing are restored to preview.21. The preview.22 broad constant-background reconstruction and recognition of the default expansion prompt are removed; the older narrow-expansion behavior remains. This change does not promise that higher resolution will remove every model artifact.
