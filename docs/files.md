# Files

## Opening

File > Open (Ctrl+O) opens one or more files. Each project opens in its own tab. Each image becomes a new, unsaved document with a single layer named after the file. A file that is already open just becomes the active tab. File > Open Recent lists the last twelve files you opened or saved.

Files can also be dropped onto the window. Dropped projects open in tabs. Dropped images are placed as layers into the document you are working on, centered where you dropped them if that is inside the canvas; with no document open, or when projects are dropped at the same time, images open as documents instead.

### What opens

- **Composa projects**: `.cmps` files, with all their layers.
- **Images**: PNG, JPEG, WebP, BMP, GIF and ICO, plus HEIC, HEIF, AVIF and TIFF when ImageMagick is available. The orientation stored by a camera is honoured.
- **SVG**: drawn at the size the file declares. The result is pixels; it does not stay a vector drawing.
- **Photoshop**: `.psd` and `.psb` files, 8-bit or 16-bit RGB; 16-bit channels are converted to the editor's 8-bit channels with a report. See [Photoshop files](#photoshop-files).
- **Camera RAW**: DNG, CR2, CR3, NEF, ARW, RAF, ORF, RW2, PEF and most other RAW formats, when ImageMagick is available. See [Camera RAW files](#camera-raw-files).

A single image or layer can be up to 30,000 pixels on a side and 200 megapixels. A whole document has a budget for its layers that depends on the memory in your machine, between 200 and 800 megapixels, so a banner with many large layers opens as long as the machine can hold it.

## Placing

File > Place Images as Layers adds image files to the current document as layers, scaled down to fit the canvas if they are larger and centered, and switches to the Move tool so you can position them. An SVG placed this way is drawn to fit the canvas, so a small icon comes in sharp rather than enlarged. A Photoshop file placed into a document arrives as a folder named after the file, with its layers inside.

## Saving

File > Save (Ctrl+S) writes the project; the first time it asks where. File > Save As (Ctrl+Shift+S) writes a copy under a new name and the document continues from there.

A project is a `.cmps` file. It keeps the canvas size and resolution, every layer with its pixels, transform, mask, effects, live text and shape settings, adjustment layers with their settings, folders, the guides and which layer was active. It is a zip archive with a description and one image per layer, so nothing in it is secret.

Saving happens in the background. The document as it is when you press Save goes to disk while you keep working, and the status bar shows "Saving" with the file name until it is done. Only the state that was saved counts as saved: an edit you make meanwhile leaves the document modified. Closing a document or quitting waits for a save still in progress, so a file is never cut short.

Projects saved by the macOS app cannot be opened; Composa has its own format.

## Exporting

Exporting flattens the document to a single image and leaves the project as it is.

- **File > Export PNG** (Ctrl+Shift+E): lossless, with transparency.
- **File > Export JPEG** (Ctrl+Alt+Shift+S): shows a preview with a quality slider from 1 to 100, the image size and the resulting file size, and composites transparent areas over white. The quality you choose is remembered.
- **File > Export WebP**: uses the quality last chosen for JPEG.

## Photoshop files

Composa opens PSD and PSB and saves layered PSD. **File > Save as PSD…** writes a Photoshop document; PSD is also available in **Save As…**. A PSD that opens without conversions keeps its path, so Ctrl+S saves it again. An import needing conversions opens without a save path to protect the original. Keep a `.cmps` project alongside interchange files to retain every editable setting.

PSD export keeps editable horizontal text: wording, actual font faces (identified by their OpenType PostScript names), size, colors and faces per character, alignment, tracking, leading, point text and paragraph frames. Rectangles, rounded rectangles, ellipses and round-ended lines are written as native vector contours with editable solid fill and stroke; rounded rectangles carry their corner parameters. Affine text transforms, including rotation, reflection, stretching and shear, remain editable. Shapes with a stroke retain rotation, reflection and uniform scale; unstroked shapes also retain stretching and shear. Every live layer also has a raster compatibility cache, and the document includes a merged preview.

Nested folders, visibility, opacity, masks (including disabled masks), clipping, all supported blend modes, Unicode names, resolution and guides survive. Raster transforms are applied to pixels. Perspective distortion, live layers with enabled layer effects, separate translucent shape fills/strokes, nonuniformly stretched vector strokes, degenerate or tiny paragraph frames and exceptionally fragmented text styles use raster fallback. The conversion dialog explains each fallback before saving. Smart objects and layer effects also remain raster interchange. When the document contains adjustments, an exact appearance layer is saved above a hidden **Composa source layers** folder containing the separate source layers; adjustment settings remain in `.cmps`.

The writer uses lossless PackBits compression, writes rows to disk without buffering all encoded layers, and renders the merged preview once. It embeds a small standard sRGB ICC profile. Like project saving, PSD saving takes an immutable snapshot, writes in the background and replaces the destination only after completion. PSD export is 8-bit RGB and limited to 2 GB; PSB is read but not written. CMYK and 32-bit RGB import are not supported.

What survives on import: layers and folders, visibility, opacity and fill, masks, clipping, and blend modes (Dissolve, Darker Color and Lighter Color become Normal). Levels, Curves, Hue/Saturation, Brightness/Contrast, Exposure, Invert, Color Balance and Black & White adjustment layers arrive as Composa adjustment layers. Horizontal affine text, character colors and font faces arrive editable. Different sizes and spacing per character use the first size/spacing and are reported; vertical text stays pixels. Solid fills, simple vector shapes and straight round-ended lines become live shapes where possible, including fill and stroke settings.

Fonts are referenced, not embedded. Photoshop may request a text-engine update; after editing, layout can change with font availability and differences between the two text engines. The exported compatibility images show the original Composa appearance before regeneration. The writer adds no runtime packages and does not run another application to save the file.

What does not: layer effects are dropped, smart objects arrive as pixels, and gradient and pattern fills arrive empty. When anything has to be converted, an "Open" dialog lists what will change, layer by layer, before the file is opened; Import goes ahead. **Use Photoshop's merged image instead (one layer)** opens its authored compatibility image, preserving complex effects at the cost of layer editing; a file without a usable compatibility image reports an error. Embedded RGB ICC profiles are converted to sRGB using Skia. A file too large for memory has its layers cropped to the canvas rather than being refused, and the dialog lists every layer that was cut.

## Camera RAW files

A RAW file opens through a "Develop" dialog with Exposure (in EV, from -3 to 3), Temperature and Tint, and a Reset button. Import develops the whole frame with those settings and opens it as a document. For finer control afterwards, use the [Camera Raw Filter](camera-raw.md).

## Closing and quitting

Closing a tab or the window with unsaved changes asks "Save changes before closing?" with Don't Save, Cancel and Save. Quit is File > Quit (Ctrl+Q).
