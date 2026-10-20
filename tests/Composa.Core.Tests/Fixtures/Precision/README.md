# PNG16 fixtures

Generated with the independent ImageMagick 14.17.1 Q16 encoder. No photographic content.
17 × 9 pixels; PNG color types 0 (gray), 2 (RGB), 4 (gray + alpha), 6 (RGBA), each plain and Adam7 interlaced.
All samples are unsigned 16-bit, with important data in the low byte.

At position `(x,y)`, color channel `c` is `24000 + x*19 + y*13 + c*401`.
Gray repeats channel 0. Alpha, when present, is `10000 + (x*173 + y*531) % 50000`.
The encoder was explicitly asked for PNG bit-depth 16 and the stated color type, without color conversion.

`png16-adobe-rgb.png` uses Adobe RGB (1998); `png16-srgb-reference.png` is its independent LittleCMS conversion
to sRGB. The source RGB channels are `(33000,46000,13000) + x*19 + y*13`, alpha is opaque.

`rgb16-alpha.psd` is an independent ImageMagick Q16 PSD, 512 × 2, with straight RGB
`24000 + x + c*1000`, alpha `65535` on the first row and `32767` on the second.
It exercises adjacent low-bit levels and layered alpha decoding. ImageMagick writes the merged RGB
without Photoshop's white matte; its own default decoder turns the semitransparent merged row black.
This fixture's layer channels are authoritative; it is not used to validate the merged appearance.

`photoshop-16bit5x5.psd` comes from the MIT-licensed psd-tools test corpus:
https://github.com/psd-tools/psd-tools/blob/main/tests/psd_files/16bit5x5.psd
See `psd-tools-LICENSE.txt`. Both its layers and its merged image are checked against an independent
ImageMagick Q16 reading. The center pixel is RGB (60539, 62141, 64391), opaque.

`rgb16-adobe-rgb.psd` is ImageMagick Q16's PSD encoding of `png16-adobe-rgb.png`, preserving its
Adobe RGB profile and source samples. The expected appearance is the same LittleCMS PNG reference.
