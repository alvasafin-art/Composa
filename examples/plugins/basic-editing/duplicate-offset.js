const doc = app.activeDocument;
if (!doc.activeLayer) throw new Error("Select a layer first.");
const copy = doc.activeLayer.duplicate();
copy.name += " · copy";
copy.moveBy(20, 20); // Also moves group children and document-space masks.
