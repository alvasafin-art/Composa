# Scripting

Composa scripts are standard JavaScript. They use live editor objects exposed through `app` and queue generative work through `ai`. The complete TypeScript-style declaration is in [`composa.d.ts`](composa.d.ts).

```javascript
const title = app.activeDocument.findLayersByTag("title")[0];
title.text = "Winter Sale";
title.opacity = 0.9;
title.transform = { ...title.transform, width: 520 };
```

Layer tags are preferred for repeatable templates. `findLayersByName` is available when a document has no tags. Assignments to `name`, `text`, `tags`, `visible`, `opacity`, and `transform` use ordinary editor commands.

In Assistant chat, `doc.addAttachedImage(index)` imports an explicitly attached image as a new layer. The zero-based index counts all files in the current message. Attach the file and explicitly ask to import it; this is not a general filesystem API.

```javascript
const doc = app.activeDocument;
doc.selectRect(80, 60, 400, 300);
ai.generativeFill("replace the selected area with white flowers", { seed: 42 });
doc.export("C:/Exports/winter-sale.webp", 90);
```

AI calls are queued in script order after local document edits. They use the selected Engine Pack and the image, reference-size, mask, upscaler, seed and megapixel settings from Composa. Export happens last, after successful AI work.

Available AI functions are `generateImage`, `generativeFill`, `removeObject`, `generativeExpand`, `changeBackground`, `harmonize`, `matchToScene`, `relight`, `upscale`, `selectSubject`, and `objectSelection`. Optional `{ width, height, seed }` overrides are accepted; Generative Expand also accepts `{ x, y }` for its output origin.

Scripts cannot load assemblies, start processes, access the network, or read arbitrary files. A failed command, failed AI task, cancellation, timeout, or script exception restores the document to its state before the script.
