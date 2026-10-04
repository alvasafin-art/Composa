# AI tool reference

Every tool an agent gets, with its parameters. Coordinates are canvas pixels with the origin at the top left. Colors are written as `#rrggbb` or `#aarrggbb`. Every tool that changes something is one undoable step.

Most tools take a `document` parameter: the tab number as `list_documents` reports it. Leave it out for the active document. Tools that work on a layer take `layer`: the layer's name, or the eight-character id that `describe_document` shows in brackets when two layers share a name. Leave it out for the active layer.

## Documents

**list_documents**: the documents open in Composa, numbered as their tabs are, with their size, layer count and whether they have unsaved changes. No parameters.

**describe_document**: the canvas size and resolution, the selection's bounds, and the layer stack top layer first, with each layer's kind, id, position and size, visibility, opacity, blend mode, mask, clipping and effects. Parameters: `document`.

**new_document**: creates a document in a new tab and makes it active. Parameters: `width` and `height` in pixels; `background` as a color, left out for a transparent canvas.

**open_document**: opens a project or an image file in a new tab, or makes an already open file the active document. Photoshop and camera RAW files are refused because they need a dialog. Parameter: `path`, absolute.

**save_document**: saves a `.cmps` project or a layered `.psd` in the background as Ctrl+S does. Parameters: `path`, absolute, left out to save to the document's own file; `overwrite`, needed to replace an existing file at a new path; `allowConversion`, required for PSD when live content, effects or adjustments need conversion. PSD results report what was converted; `.cmps` retains all settings.

**export_image**: exports the document flattened to a PNG, JPEG or WebP, by the extension of `path`. Parameters: `path`; `quality` from 1 to 100 for JPEG and WebP, 90 by default; `overwrite`.

**render**: the document as it looks now, as a PNG, so the agent can see the result of its work. Parameters: `maxSide`, the longest side in pixels, 1024 by default, never larger than the document; `grid`, a spacing in canvas pixels for a labelled grid over the picture, 0 for none; `x`, `y`, `width` and `height` for a region to render instead of the whole canvas, at full size up to `maxSide`.

**undo**: takes back the last step in the document, whoever made it. Parameters: `document`.

## Layers

**guides**: real non-exported ruler/snap guides, not line layers. Parameters: `action` (`list`, `add`, `move`, `remove`, `clear`, `show`, `hide`, `lock`, `unlock`); `axis` (`vertical` = X, `horizontal` = Y), `position`, full guide `id`, `document`. Add/move/remove respect the guide lock. `list` returns ids and visibility/lock state.

**measure_text**: actual font-layout width/height, line count and overflow without a new layer. Parameters: `text`, `size`, `font`, `bold`, `italic`, optional paragraph `boxWidth`/`boxHeight`, `document`. `add_text` and `set_text` also accept box dimensions and `fitToCanvas` (true by default), preserving complete editable text inside the canvas through wrapping and font reduction.

**new_layer**: adds an empty, transparent layer the size of the canvas above the active layer and makes it active. Parameters: `name`, left out for the next free "Layer n".

**place_image**: places an image file as a layer above the active one. PNG, JPEG, WebP, BMP, GIF, SVG, and HEIC, AVIF or TIFF when ImageMagick is available. Parameters: `path`; `x` and `y` for the center of the image, left out to center it on the canvas; `fit`, true by default, scales the image down to fit the canvas but never up; `scale` multiplies the placed size, so 0.5 places it at half the size it would otherwise get.

**select_layer**: makes a layer the active one. Parameters: `layer`.

**set_layer**: changes a layer's `name`, `visible`, `opacity` from 0 to 1, or `blend` mode named as the Layers panel names it. Give only what should change.

**set_shape**: changes an existing live shape's fill color, preserving geometry, placement and masks. Parameters: `layer`, `color` (for example `#FFFF00`), optional `document`. Never rasterize merely to recolor a square or ellipse.

**transform_layer**: moves, resizes or rotates a layer by setting its frame. Parameters: `x` and `y` for the left and top edge, `width`, `height`, and `rotation` in degrees, positive clockwise. Give only what should change. Text and shapes are redrawn sharp, and consecutive calls on one layer fold into one undo step.

**duplicate_layer**: copies a layer, or a folder with everything in it, right above the original; the copy becomes active.

**delete_layer**: deletes a layer, or a folder with everything in it.

**reorder_layer**: moves a layer among its siblings. Parameters: `direction`, one of up, down, top or bottom.

## Text, shapes and painting

**add_text**: adds a text layer. Parameters: `text`, where a newline starts a new line; `x` and `y` for the top-left corner; `size` in pixels, 72 by default; `color`; `font`, left out for the default; `bold` and `italic`.

**fill_layer**: fills the active layer with a color, within the selection when there is one. A text layer is recolored instead. Parameters: `color`.

**add_shape**: adds a live rectangle, rounded rectangle or ellipse as a new layer. Parameters: `kind`, one of rectangle, rounded or ellipse; `x`, `y`, `width` and `height`; `color`; `cornerRadius` for a rounded rectangle, 24 by default.

**add_line**: adds a live straight line with round ends as a new layer. Parameters: `x1`, `y1`, `x2`, `y2`; `color`; `width` in pixels, 4 by default.

**paint_stroke**: paints one brush stroke through the given points on the active layer, or on the layer named without changing which layer is active. Parameters: `points` as `[[x, y], [x, y], ...]`, where a single point is a dab and a curve needs a point every few pixels; `color`; `size`, the diameter, 40 by default; `hardness` from 0 (soft) to 1 (hard), 0.8 by default; `opacity` from 0 to 1, the most the stroke covers, or the strength for blur, smudge, dodge and burn; `mode`, one of paint, erase, blur, smudge, dodge or burn; `layer`. Live text and shapes cannot be painted on.

**paint_strokes**: paints up to two thousand strokes in one call as one undoable step, in the order given. Parameters: `strokes`, a list where each entry has `points`, `color`, `size`, `hardness`, `opacity` and `mode` as paint_stroke takes them; `layer`.

## Looking

**sample_color**: the colors at points, each averaged over a small disc. Parameters: `points` as `[[x, y], ...]`, up to five hundred; `radius` of the disc from 0 to 50 pixels, 2 by default; `layer`, to read that layer's own pixels rather than what is on screen, so a photo under the agent's strokes can still be read.

**trace_edges**: the edges in the picture as polylines in canvas pixels, longest first, so line work can follow where a face, an object or a fold really is. Parameters: `detail` from 0 to 100, how faint an edge may be, 50 by default; `minLength`, the shortest edge kept in pixels, 20 by default; `simplify`, how far a polyline may stray from the edge in pixels, 2 by default, where higher means fewer points; `maxLines`, at most how many edges come back, 150 by default and up to 1000; `layer`, to trace that layer's own pixels; `x`, `y`, `width` and `height` for a region.

## Selections

The selection tools take `mode`: replace (the default), add, subtract or intersect, as Shift, Alt and Shift with Alt do with the marquee. Where a `feather` is taken, it softens the edge by that many pixels.

**select_shape**: selects a rectangle or an ellipse given by `x`, `y`, `width` and `height`, or a polygon given by `points` with at least three. Parameters: `kind`, one of rectangle, ellipse or polygon; `mode`; `feather`.

**select_wand**: selects the pixels of a similar color around a point. Parameters: `x` and `y`; `tolerance` from 0 to 255, 32 by default; `contiguous`, true by default; `allLayers`, true by default, samples every visible layer rather than the active one; `mode`.

**select_object**: selects the object under a point, the connected piece of everything that is not the plain backdrop. Parameters: `x` and `y`; `allLayers`; `mode`.

**select_subject**: everything in the picture that is not the plain backdrop connected to its edges. Parameters: `mode`.

**select_color_range**: every pixel near the given colors anywhere in the picture, as Select > Color Range does. Parameters: `colors`, a list of colors as `#RRGGBB` or names; `exclude`, colors to leave out; `fuzziness` from 0 to 200, 40 by default; `invert`, to select everything else; `mode`.

**select_layer_pixels**: selects the shape of a layer's pixels, or of its mask with `fromMask`. Parameters: `layer`; `fromMask`; `mode`.

**select_all**, **deselect** and **select_inverse**: select the whole canvas, drop the selection, or select what was not selected.

**modify_selection**: changes the selection; several parameters apply in this order. Parameters: `expand` and `contract` in pixels, up to 500; `feather` up to 250; `moveX` and `moveY` in pixels.

## Adjustments

Each adjustment tool takes `asLayer`, which adds an adjustment layer above the layer instead of changing its pixels, and `layer`. Values are the same as in the dialogs.

**adjust_brightness_contrast**: `brightness` and `contrast`, each from -100 to 100.

**adjust_exposure**: `exposure` in stops, `offset` for the shadows from -0.5 to 0.5, and `gamma`, where 1 is unchanged.

**adjust_hue_saturation**: `hue` from -180 to 180, `saturation` and `lightness` from -100 to 100, `range` as master or one of reds, yellows, greens, cyans, blues or magentas, and `colorize`, which tints the whole layer with the hue from 0 to 360.

**adjust_levels**: `inputBlack` and `inputWhite` from 0 to 255, `gamma` for the midtones, `outputBlack` and `outputWhite`, and `channel` as rgb, red, green or blue.

**adjust_curves**: `points` as `[[input, output], ...]` from 0 to 255, and `channel`. `[[0,0],[255,255]]` is a straight line; `[[0,0],[64,48],[192,208],[255,255]]` adds contrast.

**adjust_black_and_white**: `reds`, `yellows`, `greens`, `cyans`, `blues` and `magentas` from -200 to 300, with the usual defaults when left out; `tint`, with `tintHue` from 0 to 360 and `tintSaturation` from 0 to 100.

**adjust_color_balance**: `shadows`, `midtones` and `highlights`, each three values from -100 to 100 as `[cyan/red, magenta/green, yellow/blue]`, where negative goes to the first color; `preserveLuminosity`, true by default.

**adjust_gradient_map**: `shadows` and `highlights` colors, black to white by default, and `reversed`.

**adjust_invert**: no values.

## Filters

Each filter tool takes `layer`.

**filter_blur**: `radius` in pixels, 8 by default.

**filter_motion_blur**: `distance` in pixels, 30 by default, and `angle` from -180 to 180.

**filter_add_noise**: `amount` from 0 to 100, `gaussian` for a bell-shaped spread, and `monochrome`, true by default.

**filter_sharpen**: `amount` from 0 to 100, 60 by default, over a `radius` in pixels, 2 by default.

**filter_vignette**: `amount`, `midpoint`, `feather` and `highlights` from 0 to 100, `roundness` from -100 to 100, and `color`, black by default. On an empty layer it fills the whole canvas.

**filter_bloom**: `amount` from 0 to 100 and a `radius` in pixels.

**filter_tonal_contrast**: `amount` from 0 to 100, a `radius` in pixels, and `shadows`, `midtones` and `highlights` from -100 to 100.

**filter_lens_correction**: `distortion` from -100 (pincushion) to 100 (corrects barrel distortion).

**filter_remove_background**: `tolerance` from 0 to 100, how different a pixel may be from the backdrop and still go.

**filter_painterly**: repaints the layer in brush strokes that follow the picture, as the Painterly filter does. Parameters: `style`, one of impressionist, expressionist, colorist_wash or pointillist; `brushSize`, the largest brush's diameter in pixels, 0 to fit it to the picture; `passes` from 1 to 4; `detail` from 0 to 100; `seed`, where the same seed paints the same strokes and 0 picks one.

## Resources

For clients that read resources, three are available: `composa://documents` gives the document list, `composa://documents/1` the layer stack of the first tab, and `composa://documents/1/image` its render as a PNG at most 1024 pixels on its longest side. Replace 1 with any tab number.
