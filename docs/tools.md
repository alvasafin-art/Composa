# Tools

The toolbar on the left holds fifteen tools. Each has a single-letter key, and pressing the key of a tool that has modes switches to the next mode; Tab does the same while the tool is active. Below the tools sit the foreground and background swatches: click one to open the color picker, press X to swap them and D to reset them to black and white.

Six buttons hold a group of tools, as in Photoshop, and show a small triangle in their corner: Marquee, Lasso, Magic, Brush and Eraser, Smear, and Shape. A click uses the tool the button shows. Press and hold the button, or right-click it, and the group opens beside it, listing each tool with its icon and key, with a dot at the current one. Click a tool there, or keep the button held, slide onto a tool and let go. The button then shows the tool you picked and the options bar names it.

Tool keys are ignored while you are typing text. While you are dragging, a command from the menu waits until the drag ends.

## Getting around the canvas

- **Pan**: hold Space and drag, drag with the middle mouse button, or use the Hand tool.
- **Wheel**: scrolls the canvas; with Shift it scrolls sideways; with Ctrl or Alt it zooms around the pointer.
- **Zoom**: from 1 to 6400 percent. View > Zoom In (Ctrl and plus) and Zoom Out (Ctrl and minus) step through fixed stops; View > Fit Canvas (Ctrl+0) fits the window without going above 100 percent; View > Actual Pixels (Ctrl+1) shows the image at 100 percent.
- **Ctrl-drag** with any tool but Move moves the layer under the pointer, as a temporary Move tool.
- **Arrow keys** nudge the layer by one pixel with the Move tool, or ten with Shift, and move the selection by the same steps with a selection tool.
- **Digits**: with a brush tool, 1 to 9 set the opacity to 10 to 90 percent and 0 to 100 percent; with the Gradient tool they set its opacity; with the Move tool they set the active layer's opacity.
- **[ and ]** change the brush size; **{ and }** change its hardness by 25 percent.
- **Escape** cancels a drag, a polygon or a crop; **Enter** closes a polygon or applies a crop.

## Move (V)

Moves, resizes, rotates and distorts layers.

- Drag to move a layer. Shift locks the move to one axis. Moves snap to guides, the grid, other layers' edges and centers and the document bounds, according to View > Snap To; magenta lines show what you snapped to. Hold Ctrl to move without snapping.
- **Auto Select** (on by default) selects the layer whose pixels you click. With it off, a drag moves the current layer from anywhere. Ctrl-click always picks the layer under the pointer, and Shift adds it to the selected layers.
- **Transform controls** (on by default, also View > Show Transform Controls, Ctrl+H) show a frame with handles around the layer. Drag a handle to resize, proportionally by default; Shift resizes freely and Alt resizes from the center. Dragging the frame through itself flips the layer. Drag just outside a corner to rotate; Shift snaps to 15 degrees. Ctrl-drag a corner to distort. Live text and shapes are redrawn sharp at their new size.
- The options bar shows X, Y, W, H and the angle as numbers. Drag a label to change its value, hold Alt for finer steps, or type a value.
- With a selection on a plain pixel layer, dragging inside the selection moves the selected pixels; with Alt it moves a copy.
- A double-click on text opens it for typing.

## Marquee (M)

Selects a rectangle or an ellipse; press M again or Tab to switch. Drag to select. Shift adds to the selection, Alt subtracts, Shift and Alt together keep the intersection. With no selection, holding Shift from the start makes a square or a circle; while adding, letting Shift go and pressing it again does the same. Drag inside a selection to move its outline (Shift pressed during the drag keeps it on one axis); Ctrl-drag inside to move the pixels, Ctrl and Alt to move a copy. A click without a drag deselects. The corners of a marquee, and a moved outline's edges, snap to guides, the grid, layer edges and the canvas edges according to View > Snap To; hold Ctrl while dragging to place them freely.

The options bar has a Feather slider for new selections, buttons to expand, contract and feather the current selection by a number of pixels, and Select All, Deselect and Inverse.

## Lasso (L)

Selects a freehand or a polygonal outline; press L again or Tab to switch. Freehand: drag. Polygonal: click each corner, then click the start point, double-click or press Enter to close; Backspace removes the last corner and Escape cancels. The Shift and Alt modes and the options bar are as for the Marquee.

## Magic (W)

Magic Wand, Object Selection and Selection Brush share the Magic button's group. Selection Brush has its own Q shortcut and a dashed brush icon.

- **Magic Wand** selects the connected area of similar color under the click. Tolerance (0 to 255, 32 by default) says how different a color may be; Contiguous limits the selection to the connected area.
- **Object Selection** traces the object under the click: the connected piece of everything that is not the plain backdrop touching the picture's edges. The Edge setting, from -10 to 10, tightens or loosens the outline.
- **Selection Brush (Q)** paints the selection rather than image pixels. Size/Feather control its footprint; the diameter outline remains visible during the stroke and follows canvas zoom. Shift adds, Alt subtracts.

Sample all layers reads the merged picture instead of the active layer alone. Shift adds and Alt subtracts, as with the marquee. Object Selection and Select > Subject work from the plain backdrop connected to the picture's edges, so a busy background defeats them.

## Crop (C)

Drag a box over the area to keep; handles adjust it, dragging inside moves it, and rule-of-thirds lines help you frame. The Ratio dropdown keeps a shape (Free, Original, 1:1, 4:3, 3:4, 16:9, 9:16); with Free, Shift keeps the box's proportions and Alt crops symmetrically. The box snaps to guides and layer edges; Ctrl turns that off. If a selection exists when you switch to Crop, the box starts from it. Enter or Apply crops; Escape or Cancel leaves the document as it was. Pixels outside the new canvas are kept, so you can enlarge the canvas later. Trim transparent edges, also in the options bar, crops away transparent borders in one click.

## Brush (B) and Eraser (E)

Brush and Eraser share a button. The brush paints with the foreground color; the eraser clears pixels instead. Size runs from 1 to 500 in the bar (larger with the ] key, up to 2500), Hardness from 0 for a soft edge to 100 for a hard one, and Opacity is the most a whole stroke can cover, so going back over your own stroke never darkens it further. Smoothing, from 0 to 100, makes the brush trail the pointer on a string of that length, so a shaky hand still draws a smooth line.

Alt-click picks the foreground color from the merged picture. Shift-click draws a straight line from where the last stroke ended. A pen's pressure varies the size. Painting is refused on folders, on adjustment layers without a mask, on hidden layers, and on live text or shapes until you rasterize them (Layer > Rasterize Layer).

## Spot Healing Brush (J)

Drag over a blemish and it is filled from its surroundings. Size and Hardness as for the brush. It works on pixels, not on masks.

## Clone Stamp (S)

Alt-click to set the source, then paint: pixels are copied from the source, which moves along with the brush. Aligned (on by default) keeps the offset between source and brush across strokes; off, every stroke starts again from the source point. Sample all layers copies from the merged picture. A crosshair shows where the source is while you paint.

## Smear (R)

One tool with five modes, picked from the button's group or cycled with R or Tab: Liquify pushes pixels around, Blur softens, Smudge drags color along, Dodge lightens and Burn darkens. Size, Hardness and Strength as for the brush.

## Gradient (G)

Drag to draw a gradient from the foreground color to the background color, or to transparent with Foreground to transparent ticked, linear or radial. Shift snaps the direction to 45 degrees. After you let go the gradient stays adjustable: drag either end. Enter applies it, Escape cancels. The gradient fills the whole canvas within the selection. On a mask it is drawn in gray.

## Shape (U)

Draws a rectangle, rounded rectangle, ellipse or line in the foreground color, each on its own live shape layer that stays editable: transform it later and it is redrawn sharp. Pick one from the button's group, or step through them with Shift+U or Tab. Shift makes a square or circle, Alt draws from the center, and a line snaps to 45 degrees with Shift. The corners snap to guides, the grid, layer edges and the canvas edges according to View > Snap To; hold Ctrl to draw freely. Rounded rectangles have a corner radius (0 to 400) and lines a width (1 to 100).

## Type (T)

Click for a line of text, drag a box for a paragraph, or click existing text to edit it. See [Text](text.md).

## Eyedropper (I)

Click to set the foreground color from the merged picture; Alt-click sets the background color. Dragging keeps sampling. Transparent pixels are ignored.

## Hand (H) and Zoom (Z)

The Hand tool pans; Space does the same with any tool. The Zoom tool zooms in on a click, out on an Alt-click, and drag left or right to zoom smoothly. Both offer Fit, 100% and 200% buttons in the options bar.

## Colors

**Paint Bucket** shares the Gradient button (hold or right-click). Click connected pixels to fill them with the foreground color; Tolerance controls color matching. The fill respects selections and the layer/mask transform and is undoable. It operates on raster pixels or layer masks, not live shape geometry.

Crop begins with a ready image-filling frame in the chosen aspect ratio; drag its interior to move it or its handles to resize. A selection starts the frame at its bounds.

The color picker has a spectrum with saturation and brightness in the square and hue on the strip, as Photoshop lays it out, a palette, color model fields and a hex field, and a swatch comparing the color before and after. Edit > Fill with Foreground Color (Alt+Backspace) and Fill with Background Color (Ctrl+Backspace) fill the layer, or the selection, with a color; on a text layer they recolor the text and keep it editable.
