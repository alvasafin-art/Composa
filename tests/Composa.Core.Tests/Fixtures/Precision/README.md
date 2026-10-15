# PNG16 fixtures

Generated with the independent ImageMagick 14.17.1 Q16 encoder. No photographic content.
17 × 9 pixels; PNG color types 0 (gray), 2 (RGB), 4 (gray + alpha), 6 (RGBA), each plain and Adam7 interlaced.
All samples are unsigned 16-bit, with important data in the low byte.

At position `(x,y)`, color channel `c` is `24000 + x*19 + y*13 + c*401`.
Gray repeats channel 0. Alpha, when present, is `10000 + (x*173 + y*531) % 50000`.
The encoder was explicitly asked for PNG bit-depth 16 and the stated color type, without color conversion.

`png16-adobe-rgb.png` uses Adobe RGB (1998); `png16-srgb-reference.png` is its independent LittleCMS conversion
to sRGB. The source RGB channels are `(33000,46000,13000) + x*19 + y*13`, alpha is opaque.
