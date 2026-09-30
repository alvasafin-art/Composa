// Reusable native ruler guides. Nothing is drawn into the image.
const doc = app.activeDocument;
const maxMargin = Math.floor(Math.min(doc.width, doc.height) / 2);
const input = await ui.form({
  title: 'Направляющие',
  fields: [{name: 'margin', label: 'Отступ от края холста (px)', type: 'number', value: Math.min(50, maxMargin), min: 0, max: maxMargin}]
});
doc.addGuide('vertical', input.margin);
doc.addGuide('vertical', doc.width - input.margin);
doc.addGuide('horizontal', input.margin);
doc.addGuide('horizontal', doc.height - input.margin);
console.log('Направляющие созданы, отступ:', input.margin);
