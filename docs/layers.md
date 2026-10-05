# Layers

A document is a stack of layers, listed in the Layers panel with the top layer first. Each layer has a name, a visibility eye, an opacity and a blend mode, and may have a mask and effects.

## Layer kinds

- **Pixel layers** hold an image. A new layer (Layer > New Layer, Ctrl+Shift+N) is transparent and the size of the canvas.
- **Live text and shapes** are drawn from their settings rather than from pixels, so they stay sharp however you scale them and their text and shape settings stay editable. Their names are shown in italics. Layer > Rasterize Layer turns one into an ordinary pixel layer, which you can then paint on.
- **Adjustment layers** change the look of every layer below them without touching those layers' pixels, and can be edited again later. See [Adjustments and filters](adjustments-and-filters.md).
- **Folders** group layers. Layer > Group Selected Layers (Ctrl+G) puts the selected layers in a folder; Ungroup (Ctrl+Shift+G) dissolves it. A folder can be collapsed in the panel, and hiding a folder hides everything in it.

New layers are named "Layer 1", "Layer 2" and so on, folders "Folder n", shapes after their kind, adjustment layers after their adjustment, pasted layers "Pasted Layer n", and a duplicate takes the original's name with "copy". Rename a layer with Layer > Rename Layer (F2) or by double-clicking its name.

## The Layers panel

Above the list, the blend mode dropdown applies to the selected layers and the Opacity slider to the active one. The buttons at the bottom add a layer, group the selected layers, add a mask, add a layer effect, add an adjustment layer, and delete the selected layer, its mask or the selected effect.

In the list:

- Click a layer to select it; Ctrl-click adds or removes a layer from the selection; Shift-click selects a range. Several selected layers can be moved, grouped, merged or deleted together.
- The eye shows or hides a layer. Drag down the column of eyes to show or hide several at once. Alt-click an eye to show only that layer; Alt-click again to bring the others back.
- Drag a layer to reorder it; a line shows where it will land. Drop it onto the middle of a folder to put it inside. Hold Alt while dropping to move a copy instead. Layer > Move Layer Up (Ctrl+]) and Move Layer Down (Ctrl+[) do the same a step at a time.
- Alt-click a layer to clip it to the layer below, or release it.
- Double-click an adjustment layer to edit its settings, a text layer's thumbnail to edit the text, or a name to rename it.
- Right-click for a menu with duplicate, rename, delete, edit, rasterize, clipping, grouping, Move Out of Folder, merge, the mask commands, Select Pixels and hide or show.

Layer > Duplicate Layer (Ctrl+J) copies the layer; with a selection it copies only the selected pixels to a new layer instead. Layer > Delete Layer removes it, as does Backspace with a layer selected and no selection on the canvas.

## Transforming

Layer > Transform Layer (Ctrl+T) turns on the transform controls and selects the Move tool; see the Move tool in [Tools](tools.md). Layer > Rotate Layer 90° Clockwise, 90° Counterclockwise and 180°, Flip Layer Horizontal and Flip Layer Vertical turn or mirror each selected layer around its own center.

## Masks

A mask hides parts of a layer without erasing them: white shows, black hides, gray is in between. Layer > Add Layer Mask adds one that reveals everything, or takes the shape of the current selection when there is one. The layer's context menu can also add a mask that hides everything ("Hide All (Black)").

To paint on the mask, click its thumbnail in the panel or press the \ key, then use the brush, a gradient or a fill; press \ again to go back to the pixels. Layer > Invert Layer Mask swaps what is shown and hidden. Shift-click the thumbnail to disable the mask without deleting it. Layer > Apply Layer Mask bakes it into the layer's transparency, and the context menu's Delete Mask removes it. Ctrl-click the thumbnail to load the mask as a selection.

A mask can be painted anywhere on the canvas, past the layer's own pixels; the layer grows to hold it. Folders and adjustment layers take masks the size of the document, so an adjustment can be limited to part of the picture.

## Clipping

A clipped layer shows only where the layer below it has pixels, as a photo clipped into a shape or text. Layer > Create Clipping Mask (Ctrl+Alt+G) clips the active layer to the first unclipped layer below it; the same command releases it. A small arrow in the panel marks a clipped layer.

## Blend modes

The blend mode decides how a layer's colors combine with what is beneath. The dropdown groups them: Normal; Darken, Multiply, Color Burn and Linear Burn; Lighten, Screen, Color Dodge and Linear Dodge (Add); Overlay, Soft Light, Hard Light, Vivid Light, Linear Light, Pin Light and Hard Mix; Difference, Exclusion, Subtract and Divide; Hue, Saturation, Color and Luminosity.

## Merging and flattening

Layer > Merge Down (Ctrl+E) combines the active layer with the one below it; the command reads Merge Layers when several layers are selected and Merge Group when a folder is active. A layer cannot be merged into a hidden layer or into an adjustment layer.

Layer > Merge Visible combines every visible layer into one and leaves the hidden layers as they are; a folder merges as it looks, so a hidden layer inside a visible folder goes with it. Layer > Stamp Visible (Ctrl+Alt+Shift+E) puts the picture as it looks on a new layer on top and keeps every layer, so a filter can work on the whole picture without flattening it. Layer > Flatten Image combines everything into a single Background layer.

To copy the whole picture there is no need to flatten: Edit > Copy Merged (Ctrl+Shift+C) with nothing selected copies it as it looks, and leaves the layers alone.

## Align and distribute

With Move active, the top row includes six alignment buttons and equal horizontal/vertical gap buttons. Choose **Canvas** to align to the canvas, or **2nd object** to keep the second selected object in place and align the others to it. Shift-click adds objects; adding a third object does not change the reference. A selected folder counts as one object with its children's bounds. Equal gaps need at least three objects and keep the outermost objects fixed. Each operation is one Undo step.

Position, size and rotation fields are directly visible in that same row. Fill, stroke and corner controls remain inline for supported shapes.

## Layer effects

Effects are drawn around or over a layer's pixels and follow the layer as it changes. Layer > Layer Effects offers Stroke, Drop Shadow, Color Overlay, Inner Shadow, Outer Glow, Inner Glow and Gradient Overlay, each with its own dialog; the effects button in the Layers panel offers the same.

- **Stroke**: a line along the edge, outside or inside, with a color, a size and an opacity.
- **Drop Shadow** and **Inner Shadow**: a color, an opacity, an angle, a distance and a blur. The angle is where the light comes from, so the shadow falls the other way; a dial beside the field turns it, Shift snaps the dial to 15 degrees, and the arrow keys or the wheel turn it by one degree.
- **Color Overlay**: a color at an opacity over the whole layer.
- **Gradient Overlay**: a linear or radial gradient clipped to the layer and its mask, with start/end colors, opacity, angle, scale and reverse. The original pixels remain unchanged.
- **Outer Glow** and **Inner Glow**: a color, an opacity and a size.

Each effect appears as a row under its layer, with its own eye to hide or show it. Double-click the row to edit it, right-click for edit, hide and delete, and Alt-drag the row onto another layer to copy the effect there. Layer > Layer Effects > Delete Effect, or Backspace with the row selected, removes it.
