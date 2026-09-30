# Scripting

`console.log`, `console.info`, `console.warn` and `console.error` are available for diagnostics. They write at most 4096 characters to the script result, without filesystem, process or network access. Printing a success message does not count as evidence that a document edit was applied.

Composa scripts are standard JavaScript. They use live editor objects exposed through `app` and queue generative work through `ai`. The complete TypeScript-style declaration is in [`composa.d.ts`](composa.d.ts).

## Run and reuse

Open **Scripts > Script Editor** to write, edit, run, or stop a script. **Run Script File** opens a selected `.js` file for review before execution. **Save to Library** adds a persistent command to the Scripts menu; **Save .js** exports source code. Import, edit, remove, and manage commands in **Scripts > Scripts & Plugins**. Assign keys in **Help > Keyboard Shortcuts**. TypeScript declarations describe the API; the runtime executes JavaScript, not uncompiled TypeScript.

The library lives under Composa's config folder (`AppPaths.Config/automation`). Updating a saved script keeps the previous `.bak` version. Removing a script moves it and its backup into `automation/archive`; import the archived `.js` to recover it. Existing script identities/filenames stay stable so assigned shortcuts survive edits. See [Plugins](plugins.md) for installable multi-command packages.

Long asynchronous commands started from a menu open a small progress window with **Stop**. The script editor has its own Stop button. Cancelling restores document changes; Escape in the locked editor also cancels a library command. Finish or stop an active application before closing the editor.

## Drawing and layers

`doc.guides`, `addGuide('vertical'|'horizontal', position)`, `moveGuide(id, position)`, `removeGuide(id)` and `clearGuides()` operate on real ruler/snap guides. Vertical positions are X, horizontal positions are Y in canvas pixels. Guides are not layers and are never exported. Locked guides reject add/move/remove. Adding an existing guide at the same position is harmless. `addLine` draws image content and is not a guide.

Text creation and `layer.text` changes use the actual font layout to keep all text inside the canvas: wrap first, reduce font size if necessary, never truncate. Optional `boxWidth`/`boxHeight` define a paragraph; `addText(...,{fitToCanvas:false})` explicitly permits overflow. The normal manual Type tool remains unchanged.

## Native input windows

The app's script runner supports top-level `await`:

```javascript
const doc = app.activeDocument;
const input = await ui.form({title:'Guides', fields:[
  {name:'margin',label:'Margin (px)',type:'number',value:50,min:0,max:Math.min(doc.width,doc.height)/2}
]});
doc.addGuide('vertical', input.margin);
doc.addGuide('vertical', doc.width-input.margin);
```

Use `await prompt('Title', 'Hello')` for one text field. Forms support number/text/boolean values, up to 16 fields and 8 sequential dialogs. **Always await the result.** Browser DOM/window/alert and arbitrary native UI are not available. Cancelling input or pressing Stop rolls back the entire script. Human input waiting is excluded from the execution timeout; editing callbacks resume on the UI thread. See the ready example `examples/scripts/guides-with-input.js`.

```javascript
const doc = app.activeDocument;
const card = doc.addRectangle(40, 40, 240, 120, "#87CEEB", "Blue Rectangle");
card.tags = ["card"];
doc.addText("Hello", 60, 70, { size: 36, color: "#102030", name: "Title" });
```

`addRectangle`, `addEllipse`, `addShape`, `addLine`, and `addText` create real editable layers. Do not add an empty raster layer first for these commands. `id` and `kind` are read-only; assigning them or inventing `pixels`/`drawRectangle` properties throws an error rather than silently doing nothing. Colors use `#RRGGBB` or `#AARRGGBB` (alpha first).

For raster painting, `doc.addLayer`, `doc.paintStroke(points, options)`, `doc.fill(color)` and `layer.fill(color)` use the ordinary brush and selection. Live text/shape layers cannot be filled or painted as raster pixels. `layer.duplicate()`, `layer.blendMode`, `doc.groupLayers(layers, name)`, `selectEllipse`, `selectAll`, `invertSelection`, `resizeImage` and `resizeCanvas` also use normal editor commands.

```javascript
const title = app.activeDocument.findLayersByTag("title")[0];
title.text = "Winter Sale";
title.opacity = 0.9;
title.transform = { ...title.transform, width: 520 };
```

Layer tags are preferred for repeatable templates. `findLayersByName` is available when a document has no tags. Assignments to `name`, `text`, `tags`, `visible`, `opacity`, and `transform` use ordinary editor commands.

`layer.transform.x += 20` works directly on raster/shape/text layers, as does replacing a partial transform object. `layer.moveBy(dx,dy)` also moves whole groups and their masks; direct transform fields on pixel-less groups/adjustments are rejected. `layer.tags` is a snapshot: reassign the array after changing it. Layer collections are snapshots of live layer handles. `app.documents` currently contains only the current document; scripts do not switch or edit other tabs.

`doc.selection` returns the current selection's read-only `{ x, y, width, height }` bounds, or null. Change the selection through selection methods; modifying returned snapshots does not paint a mask.

In Assistant chat, `doc.addAttachedImage(index)` imports an explicitly attached image as a new layer. The zero-based index counts all files in the current message. Attach the file and explicitly ask to import it; this is not a general filesystem API.

```javascript
const doc = app.activeDocument;
doc.selectRect(80, 60, 400, 300);
ai.generativeFill("replace the selected area with white flowers", { seed: 42 });
doc.export("C:/Exports/winter-sale.webp", 90);
```

AI calls are queued in script order after all local document edits. Prepare local edits and selection first; these calls are not promises and cannot be awaited inside JavaScript. Each queued call uses the resulting document state (selection is not snapshotted per call). They use the selected Engine Pack and the image, reference-size, mask, upscaler, seed and megapixel settings from Composa. Export happens last, after successful AI work. An export failure rolls back document changes; exported files are not part of Undo.

Available AI functions are `generateImage`, `generativeFill`, `removeObject`, `generativeExpand`, `changeBackground`, `harmonize`, `matchToScene`, `relight`, `upscale`, `selectSubject`, and `objectSelection`. Optional `{ width, height, seed }` overrides are accepted; Generative Expand also accepts `{ x, y }` for its output origin.

Scripts cannot load assemblies, start processes, access the network, or read arbitrary files. A failed command, failed AI task, cancellation, timeout, or script exception restores the document to its state before the script.

Native text and brush commands participate in the same enclosing transaction, including strokes that touch nothing. One Ctrl+Z undoes the complete successful script. JavaScript execution is bounded to eight seconds, 100,000 statements and 24 MB of engine allocations; editor raster limits also apply. During asynchronous application the target document is locked against competing edits. Scripts are never run automatically at startup.
