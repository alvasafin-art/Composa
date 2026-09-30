// addRectangle itself creates the layer; do not assign layer.kind or pixels.
const doc = app.activeDocument;
const width = Math.max(1, Math.min(240, doc.width * 0.6));
const height = Math.max(1, Math.min(120, doc.height * 0.4));
doc.addRectangle((doc.width - width) / 2, (doc.height - height) / 2,
  width, height, "#87CEEB", "Blue Rectangle");
