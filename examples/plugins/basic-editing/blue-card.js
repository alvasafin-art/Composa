// A live shape and editable text, grouped as one undoable command.
const doc = app.activeDocument;
const width = Math.min(320, doc.width * 0.7);
const height = Math.min(160, doc.height * 0.5);
const x = (doc.width - width) / 2;
const y = (doc.height - height) / 2;
const card = doc.addShape({ kind: "rounded", x, y, width, height,
  color: "#87CEEB", cornerRadius: 16, name: "Blue Card" });
card.tags = ["card"];
const title = doc.addText("Hello", x + width * 0.1, y + height * 0.25,
  { size: Math.max(8, Math.min(40, height * 0.35)), color: "#17334D", bold: true, name: "Card Title" });
title.tags = ["title"];
doc.groupLayers([card, title], "Blue Card");
