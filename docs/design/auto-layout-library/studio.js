'use strict';

const byId = id => document.getElementById(id);
const stage = byId('stage');
const board = byId('board');
const layers = byId('layers');
const properties = byId('properties');
const dialog = byId('dialog');
const names = {
  group: 'Карточка кофе', photo: 'Фотография', title: 'Заголовок',
  subtitle: 'Подзаголовок', row: 'Иконка + цена', icon: 'Иконка', price: 'Цена',
  background: 'Фон документа'
};
const escapeHtml = value => String(value).replace(/[&<>"']/g, char => (
  { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[char]
));
const clone = value => structuredClone(value);
const initial = {
  title: 'Дрип-кофе', subtitle: 'Эфиопия · светлая обжарка', price: '690 ₽',
  order: ['photo', 'title', 'subtitle', 'row'], auto: false, gap: 20, pad: [18, 18, 18, 18],
  direction: 'column', align: 'start', photoHeight: 176, photoWidth: 264, imageMode: 'fixed', fit: 'cover',
  portrait: false, iconWidth: 23, rowGap: 8, color: '', linked: true, detachedSize: 22
};
const peer = { ...initial, title: 'Дрип-кофе', subtitle: 'Колумбия · средняя обжарка', price: '750 ₽', auto: true, fit: 'contain' };
const state = {
  theme: 'dark', doc: 'a', docs: { a: clone(initial), b: null },
  selection: 'multi', preview: false, tab: 'layers', tool: 'move',
  styleSize: { a: 22, b: 22 }, details: false, menu: '', inline: false, edit: false,
  resource: null, history: { a: ['Новый холст', 'Добавление слоёв'], b: ['Новый холст'] },
  undo: []
};
let timer;
let gesture;
let movingKey;
const card = () => state.docs[state.doc];
const selected = key => state.selection === key;
const effective = choice => choice.auto || (choice === card() && state.preview);
const priceSize = choice => choice.linked ? state.styleSize[choice === peer ? 'a' : state.doc] : choice.detachedSize;
const portraitHeight = choice => choice.portrait && choice.imageMode === 'content' ? 228 : choice.photoHeight;
const sizing = choice => effective(choice) && choice.direction === 'row';


function announce(message) {
  const output = byId('status');
  output.hidden = false;
  output.value = message;
  clearTimeout(timer);
  timer = setTimeout(() => { output.hidden = true; }, 3400);
}
function snapshot() {
  return clone({ docs: state.docs, styleSize: state.styleSize, history: state.history, doc: state.doc });
}
function record(title, before = snapshot()) {
  state.undo.push(before);
  state.history[state.doc].push(title);
}
function undo() {
  const before = state.undo.pop();
  if (!before) return;
  Object.assign(state, before);
  state.selection = card() ? (card().auto ? 'group' : 'multi') : 'background';
  state.preview = false;
  render();
}
function command(action, text, className = 'cmd', extra = '') {
  return `<button type="button" class="${className}" data-action="${action}" ${extra}>${text}</button>`;
}
function numeric(key, title, value, unit = 'px', minimum = 0, maximum = 64) {
  return `<label class="field">${title}<span class="numeric"><input aria-label="${title}" type="number" min="${minimum}" max="${maximum}" value="${value}" data-field="${key}"><span>${unit}</span></span></label>`;
}
function selectField(key, title, choices, current) {
  return `<label class="field">${title}<select data-field="${key}">${choices.map(([value, text]) => `<option value="${value}" ${current === value ? 'selected' : ''}>${text}</option>`).join('')}</select></label>`;
}
function glyph(width) {
  return `<svg aria-hidden="true" width="${width}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.55"><path d="M3.6 6.7H15v8.1a3.2 3.2 0 0 1-3.2 3.2H6.8a3.2 3.2 0 0 1-3.2-3.2zM15 8h3a2.1 2.1 0 0 1 0 4.3h-3M2 20h19" stroke-linejoin="round" stroke-linecap="round"/></svg>`;
}
function photoSource(choice) {
  return choice.portrait ? 'assets/coffee-portrait.jpg' : 'assets/coffee.jpg';
}
function mini(choice) {
  return `<span class="mini"><span class="mini-inner"><img src="${photoSource(choice)}" alt=""><b>${escapeHtml(choice.title)}</b><span>${escapeHtml(choice.price)}</span></span></span>`;
}
function renderCard(choice, active) {
  const interactive = active && state.tool !== 'brush';
  const guide = interactive && effective(choice) && selected('group') && !sizing(choice) && choice.order[0] === 'photo';
  const group = interactive && (state.preview || selected('group'));
  const height = portraitHeight(choice);
  const inner = effective(choice) ? choice.pad : initial.pad;
  const align = ({ start: 'flex-start', center: 'center', end: 'flex-end' })[choice.align];
  const dimensions = `--pad:${inner[0]}px;--photo:${height}px;--gap:${choice.gap}px;padding:${inner.map(px => px + 'px').join(' ')};gap:${choice.gap}px;align-items:${align};flex-direction:${sizing(choice) ? 'row' : 'column'}`;
  const unit = (key, highlight = key) => {
    const chosen = interactive && selected(highlight) ? ' selected-part' : '';
    const multi = interactive && selected('multi') && !state.preview ? ' multi-part' : '';
    return `class="unit ${key}${chosen}${multi}" ${interactive ? `data-select="${highlight}" data-unit="${key}"` : ''}`;
  };
  const units = {
    photo: `<div ${unit('photoframe', 'photo')} style="width:${sizing(choice) ? '34%' : 'min(100%, ' + choice.photoWidth + 'px)'}"><img class="photo" src="${photoSource(choice)}" alt="Керамическая чашка с дрип-фильтром" style="height:${height}px;object-fit:${choice.fit}" draggable="false">${active && state.tool === 'brush' ? '<span class="brushring"></span>' : ''}</div>`.replace('data-unit="photoframe"', 'data-unit="photo"'),
    title: active && state.edit
      ? `<textarea id="title-editor" class="title editing" aria-label="Текст заголовка">${escapeHtml(choice.title)}</textarea>`
      : `<h2 ${unit('title')}>${escapeHtml(choice.title)}</h2>`,
    subtitle: `<p ${unit('subtitle')}>${escapeHtml(choice.subtitle)}</p>`,
    row: `<div ${unit('price', 'row')} ${group && choice.auto ? 'draggable="true" title="Перетащить строку цены"' : ''} style="gap:${choice.rowGap}px;color:#6a3b24;font-size:${priceSize(choice)}px">${glyph(choice.iconWidth)}<span style="color:${choice.color || 'inherit'}" class="quantity${interactive && selected('price') ? ' selected-part' : ''}" ${interactive && !group ? 'data-select="price"' : ''}>${escapeHtml(choice.price)}</span></div>`.replace('data-unit="price"', 'data-unit="row"')
  };
  return `<article class="card ${active ? 'active-card' : ''}" style="${dimensions}" aria-label="${active ? 'Карточка кофе' : 'Вторая карточка с общим стилем'}">
    ${choice.order.map(key => units[key]).join('')}
    ${group ? '<span class="bounds" aria-hidden="true"><i></i><i></i><i></i><i></i></span>' : ''}
    ${guide && !state.inline ? `<button class="measure" data-action="canvas-gap" title="Перетащить или нажать для точного ввода" aria-label="Между элементами, ${choice.gap} пикселей"><span>${choice.gap} px</span></button>` : ''}
    ${guide && state.inline ? `<input id="inline-gap" class="inline-edit" aria-label="Интервал на холсте в пикселях" inputmode="numeric" value="${choice.gap}">` : ''}
  </article>`;
}
function renderCanvas() {
  const current = card();
  board.innerHTML = current ? `<div class="cards${sizing(current) ? ' horizontal' : ''}">${renderCard(current, true)}${state.doc === 'a' ? renderCard(peer, false) : ''}</div>` : '';
}
function renderTabs() {
  byId('tabs').innerHTML = [['a', 'Каталог кофе.cmps'], ['b', 'Новая подборка.cmps']].map(([doc, title]) =>
    command('doc-' + doc, title + '<small>(RGB/8)</small>', 'tab', `aria-selected="${state.doc === doc}"`)).join('');
  byId('theme').textContent = state.theme === 'dark' ? '☼' : '◐';
}
function eye() {
  return '<svg class="eye" width="14" height="14" aria-hidden="true" viewBox="0 0 16 16" fill="none" stroke="currentColor"><path d="M1 8c4-6 10-6 14 0-4 6-10 6-14 0Z"/><circle cx="8" cy="8" r="2.1"/></svg>';
}
function layer(key, depth = 0, note = '') {
  const thumbnail = key === 'photo' ? `<img class="thumb" src="${photoSource(card())}" alt="">`
    : `<span class="thumb" aria-hidden="true">${({ group: '↧', title: 'T', subtitle: 'T', row: '↔', icon: '◇', price: 'T', background: '▧' })[key]}</span>`;
  const pressed = selected(key) || (selected('multi') && ['photo', 'title', 'subtitle', 'row'].includes(key));
  return `<button class="layer" type="button" data-action="select-${key}" aria-pressed="${pressed}" style="--depth:${depth}">${eye()}${thumbnail}<span>${names[key]}</span><span class="side-note">${note}</span></button>`;
}
function renderLayers() {
  const current = card();
  const tab = (key, title) => command('tab-' + key, title, '', `aria-selected="${state.tab === key}"`);
  const tabs = `<div class="panel-tabs">${tab('layers', 'Слои')}${tab('resources', 'Ресурсы')}</div>`;
  if (state.tab === 'resources') {
    layers.innerHTML = tabs + `<input class="resource-search" type="search" placeholder="Поиск ресурсов" aria-label="Поиск ресурсов">
      <div class="resource-list">${state.resource ? `<button type="button" class="resource-tile" draggable="true" data-action="insert" title="Вставить в текущий документ (Enter)">${mini(state.resource.card)}<strong>${escapeHtml(state.resource.name)}</strong><small>Группа</small></button><p class="fine empty-search" hidden>Совпадений нет</p>` : '<p class="fine">Сохраните выделенный блок, чтобы он появился здесь.</p>'}</div>
      <div class="resource-actions">${command('folder', 'Открыть папку библиотеки', 'text-action')}</div>`;
    return;
  }
  const childRows = current ? [...current.order].reverse().map(key => key === 'row'
    ? layer('row', effective(current) ? 1 : 0, '↔') + (selected('row') || selected('icon') || selected('price') ? layer('price', 2) + layer('icon', 2) : '')
    : layer(key, effective(current) ? 1 : 0)).join('') : '';
  layers.innerHTML = tabs + `<div class="blend"><span class="input-like">Обычный <b>⌄</b></span><span class="range"></span><span class="input-like" style="width:43px">100</span><small>%</small></div>
    <div class="tree">${current && effective(current) ? layer('group', 0, state.preview ? 'Предпросмотр' : current.direction === 'column' ? 'Колонка' : 'Ряд') : ''}${childRows}${layer('background')}</div>
    <div class="layer-actions native-actions" aria-hidden="true"></div>`;
}
function photoProperties(current) {
  return `<div class="row2">${numeric('photoWidth', 'Ширина', current.photoWidth, 'px', 120, 264)}
    ${numeric('photoHeight', 'Высота', portraitHeight(current), 'px', 112, 228)}</div>
    ${selectField('imageMode', 'Режим размера', [['fixed', 'Фиксировано'], ['content', 'По содержимому']], current.imageMode)}
    ${selectField('fit', 'Заполнение рамки', [['cover', 'Кадрировать'], ['contain', 'Вписать']], current.fit)}
    ${command('swap-photo', current.portrait ? 'Вернуть широкое фото' : 'Заменить на вертикальное', 'cmd full')}`;
}
function groupProperties(current) {
  const pad = state.details ? `<div class="row4">${['Верх', 'Право', 'Низ', 'Лево'].map((side, i) => numeric('pad' + i, side, current.pad[i], '', 0, 36)).join('')}</div>`
    : `<div class="control-row">${numeric('padding', 'Внутри группы', current.pad[0], 'px', 0, 36)}${command('pad-details', '⋯', 'icon-button', 'aria-label="Раздельные внутренние поля" aria-expanded="false"')}</div>`;
  return `<div class="group-name"><strong>${names.group}</strong><small>4 элемента</small></div>
    <div class="row2">${selectField('direction', 'Направление', [['column', 'Колонка ↓'], ['row', 'Ряд →']], current.direction)}
    ${selectField('align', 'Выравнивание', [['start', 'К началу'], ['center', 'По центру'], ['end', 'К концу']], current.align)}</div>
    ${numeric('gap', 'Между элементами', current.gap)}
    ${pad}
    ${state.details ? command('pad-details', 'Свернуть поля', 'text-action') : ''}
    <div>${command('save', 'Сохранить в библиотеку…', 'cmd full')}</div>
    ${command('disable', 'Отключить автокомпоновку', 'text-action')}`;
}
function renderProperties() {
  const current = card();
  let content = `<strong>${names.background}</strong>`;
  if (current) {
    if (state.tool === 'brush') {
      content = `<strong>${names.photo}</strong>${numeric('opacity', 'Непрозрачность', 100, '%', 0, 100).replace('<input ', '<input readonly ')}`;
    } else if (state.preview) {
      content = `<div class="group-name"><strong>Предпросмотр</strong><small>4 элемента</small></div>
        <div class="row2">${selectField('direction', 'Направление', [['column', 'Колонка ↓'], ['row', 'Ряд →']], current.direction)}${numeric('gap', 'Между элементами', current.gap)}</div>
        <p class="fine">${current.direction === 'column' && current.gap === 20 ? 'Исходное расположение сохранится.' : 'Проверьте новое расположение на холсте.'}</p>
        <div class="row2">${command('cancel-preview', 'Отмена')}${command('apply', 'Применить', 'primary')}</div>`;
    } else if (selected('multi')) {
      content = `<strong>Выбрано слоёв: 4</strong>${command('preview', 'Автокомпоновка', 'cmd full')}<p class="fine">Фотография, заголовок, подзаголовок и строка цены.</p>`;
    } else if (selected('group')) {
      content = groupProperties(current);
    } else if (selected('photo')) {
      content = `<strong>${names.photo}</strong>${photoProperties(current)}`;
    } else if (selected('title') || selected('subtitle')) {
      const title = selected('title');
      const key = title ? 'title' : 'subtitle';
      content = `<strong>${names[key]}</strong><label class="field">Текст<input type="text" aria-label="Текст" data-field="${key}" value="${escapeHtml(current[key])}"></label>
        ${selectField('titleWidth', 'Ширина', [['fill', 'Заполнить']], 'fill')}
        <div class="row2"><label class="field">Гарнитура<span class="input-like">${title ? 'Georgia' : 'Arial'} <b>⌄</b></span></label><label class="field">Кегль<span class="input-like">${title ? '36' : '14'} <small>px</small></span></label></div>
        ${title ? command('edit-title', 'Редактировать на холсте', 'text-action') : ''}`;
    } else if (selected('price')) {
      content = `<strong>Цена · Текст</strong><div class="row2"><label class="field">Гарнитура<span class="input-like">Arial <b>⌄</b></span></label><label class="field">Кегль<span class="input-like">${priceSize(current)} <small>px</small></span></label></div>
        <label class="field">Стиль текста${command('style-menu', `Aa &nbsp; ${current.linked ? 'Цена' : 'Без стиля'} <span>⌄</span>`, 'input-like', 'aria-haspopup="menu"')}</label>
        ${current.color && current.linked ? '<p class="fine">Цвет · локальное переопределение</p>' + command('reset-color', 'Вернуть значение стиля', 'text-action') : ''}
        ${current.linked ? `<p class="fine">Связанные объекты: ${state.doc === 'a' ? 2 : 1}</p>` : ''}`;
    } else if (selected('row') || selected('icon')) {
      content = selected('row') ? `<strong>${names.row}</strong><div class="row2">${selectField('rowDirection', 'Направление', [['row', 'Ряд →']], 'row')}${numeric('rowGap', 'Между элементами', current.rowGap)}</div>`
        : `<strong>${names.icon}</strong>${numeric('iconWidth', 'Ширина', current.iconWidth, 'px', 14, 52)}`;
    }
  }
  properties.innerHTML = `<h2 class="panel-heading"><span>⌄</span> Свойства</h2><div class="property-body">${content}</div>`;
}
function renderHistory() {
  byId('history').innerHTML = `<h2 class="panel-heading"><span>⌄</span> История</h2><ul>${state.history[state.doc].slice(-2).map(text => `<li>♧ &nbsp;${text}</li>`).join('')}</ul>`;
}
function renderOptions() {
  const brush = state.tool === 'brush';
  byId('movebar').hidden = brush;
  byId('brushbar').hidden = !brush;
  const current = card();
  const shape = board.querySelector('.active-card');
  byId('movebar').innerHTML = `<div class="coord-cover options-row">X <b>${current ? 68 : '—'}</b> Y <b>${current ? 42 : '—'}</b></div><div class="size-cover options-row">W <b>${current ? 300 : '—'}</b> H <b>${shape ? shape.offsetHeight : '—'}</b> ∠ <b>0</b></div>`;
  byId('brushbar').innerHTML = '<strong>Кисть</strong>' + [['● Наборы', '⌄'], ['Размер', 40], ['Жёсткость', 80], ['Поток', 100], ['◐', 100], ['Интервал', 8], ['Сглаживание', 0], ['Динамика', '⌄']].map(([text, n]) => `<span class="option"><span>${text}</span>${n}</span>`).join('');
}
function renderPopup() {
  const popup = byId('overlay');
  if (state.menu === 'style') {
    popup.innerHTML = `<div class="popup" role="menu" style="right:9px;top:506px">
      ${command('local-style', 'Изменить только эту цену<small>Цвет · 1 объект</small>', '', 'role="menuitem"')}
      ${command('shared-style', 'Изменить стиль…<small>Кегль · ' + (state.doc === 'a' ? '2 объекта' : '1 объект') + ' в документе</small>', '', 'role="menuitem"')}
      ${command('detach', 'Отвязать стиль<small>Оформление сохранится</small>', '', 'role="menuitem"')}</div>`;
  } else if (state.menu === 'context') {
    popup.innerHTML = `<div class="popup" role="menu" style="left:457px;top:426px">
      ${command('preview', 'Автокомпоновка', '', 'role="menuitem"')}</div>`;
  } else {
    popup.replaceChildren();
  }
}
function render() {
  stage.dataset.theme = state.theme;
  stage.dataset.tool = state.tool;
  document.documentElement.style.background = state.theme === 'dark' ? '#1e1e1e' : '#d1d1d1';
  renderTabs();
  renderCanvas();
  renderOptions();
  renderLayers();
  renderProperties();
  renderHistory();
  renderPopup();
}
function openDialog(type) {
  const current = card();
  const count = state.doc === 'a' && current.linked ? 2 : 1;
  const titles = { save: 'Сохранить в библиотеку', local: 'Изменить только эту цену', shared: 'Изменить стиль «Цена»' };
  const body = {
    save: `<div class="dialog-row">${mini(current)}<label class="field">Имя ресурса<input name="resource-name" required maxlength="60" value="Карточка кофе"></label></div>`,
    local: '<p class="fine">Изменение затронет только выделенный объект. Связь со стилем сохранится.</p><label class="field">Цвет<input name="price-color" type="color" value="' + (current.color || '#164d97') + '"></label>',
    shared: `<p class="fine">Связанные объекты в этом документе: <strong>${count}</strong>. Локальные переопределения цвета сохранятся.</p>${numeric('styleSize', 'Кегль', state.styleSize[state.doc], 'px', 14, 42)}`
  };
  for (const name of ['--text', '--panel', '--field', '--line', '--hover', '--muted', '--sky']) {
    dialog.style.setProperty(name, getComputedStyle(stage).getPropertyValue(name));
  }
  dialog.innerHTML = `<h2 id="dialog-title">${titles[type]}</h2><form>
    ${body[type]}<div class="dialog-actions">${command('close-dialog', 'Отмена')}<button class="primary" type="submit">${type === 'save' ? 'Сохранить' : 'Применить'}</button></div></form>`;
  dialog.querySelector('form').addEventListener('submit', event => {
    event.preventDefault();
    if (type === 'save') {
      const name = dialog.querySelector('[name="resource-name"]').value.trim();
      if (!name) return;
      state.resource = { name, card: clone(current), styleSize: priceSize(current) };
      state.tab = 'resources';
      announce('Карточка сохранена в библиотеку');
    } else if (type === 'local') {
      record('Локальное переопределение');
      current.color = dialog.querySelector('[name="price-color"]').value;
    } else {
      record('Изменение стиля «Цена»');
      const size = Number(dialog.querySelector('[data-field="styleSize"]').value);
      state.styleSize[state.doc] = size;
    }
    dialog.close();
    render();
  });
  dialog.showModal();
  dialog.querySelector('input')?.select();
}
function select(key) {
  if (state.preview || (selected(key) && !state.menu)) return;
  state.selection = key;
  state.edit = false;
  render();
}
function changeGap(value, before) {
  const gap = Math.max(0, Math.min(64, Math.round(value)));
  if (!Number.isFinite(gap) || gap === card().gap) return;
  if (!state.preview) record('Изменение интервала', before);
  card().gap = gap;
}
function setValue(input) {
  const current = card();
  if (!current) return;
  const key = input.dataset.field;
  const raw = input.value;
  if (key === 'gap') {
    changeGap(raw === '' ? current.gap : Number(raw));
  } else if (['direction', 'align', 'imageMode', 'fit', 'title', 'subtitle'].includes(key)) {
    if (current[key] !== raw && (raw.trim() || key === 'subtitle')) {
      if (!state.preview) record(key === 'title' || key === 'subtitle' ? 'Правка текста' : 'Изменение свойств');
      current[key] = raw.trim();
    }
  } else if (['photoHeight', 'photoWidth', 'iconWidth', 'rowGap', 'padding', 'pad0', 'pad1', 'pad2', 'pad3'].includes(key) && raw !== '') {
    const value = Math.max(Number(input.min), Math.min(Number(input.max), Number(raw)));
    if (!Number.isFinite(value)) return;
    record(key.startsWith('pad') ? 'Изменение внутренних полей' : 'Изменение размера');
    if (key === 'padding') current.pad.fill(value);
    else if (key.startsWith('pad')) current.pad[Number(key.at(-1))] = value;
    else current[key] = value;
  }
  render();
}
function insertResource() {
  if (!state.resource) return;
  if (state.docs[state.doc]) {
    announce('Откройте вкладку «Новая подборка.cmps» для вставки');
    return;
  }
  record('Вставка из библиотеки');
  state.docs[state.doc] = clone(state.resource.card);
  state.docs[state.doc].detachedSize = state.resource.styleSize;
  state.styleSize[state.doc] = state.resource.styleSize;
  state.selection = 'group';
  state.tab = 'layers';
  render();
  announce('Вставлена редактируемая копия карточки');
}
function handle(action) {
  const current = card();
  const previousMenu = state.menu;
  state.menu = '';
  if (action === 'close-dialog') { dialog.close(); return; }
  if (action.startsWith('select-')) { select(action.slice(7)); return; }
  if (action.startsWith('doc-')) {
    state.doc = action.slice(4);
    state.preview = false;
    state.edit = false;
    state.tool = 'move';
    state.selection = card() ? (card().auto ? 'group' : 'multi') : 'background';
  }
  if (action.startsWith('tab-')) state.tab = action.slice(4);
  switch (action) {
    case 'theme': state.theme = state.theme === 'dark' ? 'light' : 'dark'; break;
    case 'preview':
      state.preview = true;
      state.previewBaseline = { direction: current.direction, gap: current.gap };
      state.selection = 'multi';
      break;
    case 'cancel-preview':
      Object.assign(current, state.previewBaseline);
      state.preview = false;
      break;
    case 'apply': {
      const before = snapshot();
      Object.assign(before.docs[state.doc], state.previewBaseline);
      record('Автокомпоновка', before);
      current.auto = true;
      state.preview = false;
      state.selection = 'group';
      break;
    }
    case 'disable':
      record('Отключение автокомпоновки');
      current.auto = false;
      state.selection = 'multi';
      break;
    case 'pad-details': state.details = !state.details; break;
    case 'canvas-gap': state.inline = true; break;
    case 'swap-photo':
      record('Замена фотографии');
      current.portrait = !current.portrait;
      break;
    case 'edit-title': state.selection = 'title'; state.edit = true; break;
    case 'style-menu': state.menu = previousMenu === 'style' ? '' : 'style'; break;
    case 'reset-color':
      record('Возврат цвета стиля');
      current.color = '';
      break;
    case 'detach':
      record('Отвязка стиля');
      current.detachedSize = priceSize(current);
      current.linked = false;
      break;
    case 'insert': insertResource(); return;
    case 'folder': announce('Папка пользовательских данных Composa / Library'); break;
    case 'brush':
      if (!current) return;
      state.edit = false;
      state.tool = 'brush';
      state.selection = 'photo';
      break;
    case 'move': state.tool = 'move'; break;
  }
  render();
  if (action === 'save') openDialog('save');
  if (action === 'local-style') openDialog('local');
  if (action === 'shared-style') openDialog('shared');
  if (action === 'style-menu' && state.menu) byId('overlay').querySelector('button').focus();
  if (action === 'canvas-gap') {
    byId('inline-gap').focus();
    byId('inline-gap').select();
  }
  if (action === 'edit-title') {
    const editor = byId('title-editor');
    editor.focus();
    editor.setSelectionRange(editor.value.length, editor.value.length);
  }
}

function finishInline(input, cancel = false) {
  if (input.id === 'inline-gap' && state.inline) {
    state.inline = false;
    if (!cancel && /^\d+$/.test(input.value.trim())) changeGap(Number(input.value));
    render();
  }
  if (input.id === 'title-editor' && state.edit) {
    state.edit = false;
    const value = input.value.trim();
    if (!cancel && value && card().title !== value) {
      record('Правка заголовка');
      card().title = value;
    }
    render();
  }
}
document.addEventListener('click', event => {
  const button = event.target.closest('[data-action]');
  const object = event.target.closest('[data-select]');
  if (button) handle(button.dataset.action);
  else if (object) select(object.dataset.select);
  else if (state.menu) { state.menu = ''; renderPopup(); }
});
document.addEventListener('change', event => {
  if (event.target.matches('[data-field]') && !event.target.closest('dialog')) setValue(event.target);
});
document.addEventListener('focusout', event => finishInline(event.target));
document.addEventListener('dblclick', event => {
  if (event.target.closest('.active-card h2[data-select="title"]')) handle('edit-title');
});
document.addEventListener('contextmenu', event => {
  if (!card() || !selected('multi') || state.preview || !event.target.closest('.active-card, .tree')) return;
  event.preventDefault();
  state.menu = 'context';
  renderPopup();
  byId('overlay').querySelector('button').focus();
});
document.addEventListener('keydown', event => {
  const { key, target } = event;
  if (target.id === 'inline-gap' || target.id === 'title-editor') {
    if (key === 'Escape' || (key === 'Enter' && !event.shiftKey)) {
      event.preventDefault();
      finishInline(target, key === 'Escape');
    }
    return;
  }
  if (key === 'Enter' && target.closest('.resource-tile')) { event.preventDefault(); insertResource(); return; }
  if (key === 'Escape' && state.menu) { state.menu = ''; renderPopup(); return; }
  if (dialog.open) return;
  if (key === 'Enter' && target.matches('input[data-field]')) { event.preventDefault(); target.blur(); return; }
  if (target.matches('input, textarea, select')) return;
  if ((event.ctrlKey || event.metaKey) && key.toLowerCase() === 'z') {
    event.preventDefault();
    undo();
  } else if (!event.ctrlKey && !event.metaKey && key.toLowerCase() === 'b') handle('brush');
  else if (!event.ctrlKey && !event.metaKey && key.toLowerCase() === 'v') handle('move');
});
layers.addEventListener('input', event => {
  if (event.target.type !== 'search' || !state.resource) return;
  const match = state.resource.name.toLowerCase().includes(event.target.value.toLowerCase().trim());
  layers.querySelector('.resource-tile').hidden = !match;
  layers.querySelector('.empty-search').hidden = match;
});
board.addEventListener('pointerdown', event => {
  if (!event.target.closest('.measure') || event.button !== 0) return;
  gesture = { start: event.clientY, initial: card().gap, before: snapshot(), id: event.pointerId };
  board.setPointerCapture(event.pointerId);
});
board.addEventListener('pointermove', event => {
  if (gesture && event.pointerId === gesture.id) {
    const dy = (event.clientY - gesture.start) / scale();
    if (Math.abs(dy) > 3 || gesture.dragged) {
      gesture.dragged = true;
      card().gap = Math.max(0, Math.min(64, Math.round(gesture.initial + dy)));
      renderCanvas();
      const field = properties.querySelector('[data-field="gap"]');
      if (field) field.value = card().gap;
    }
  }
  const frame = event.target.closest('.active-card .photoframe');
  const ring = frame?.querySelector('.brushring');
  if (ring) {
    const rect = frame.getBoundingClientRect();
    ring.style.left = (event.clientX - rect.left) / scale() + 'px';
    ring.style.top = (event.clientY - rect.top) / scale() + 'px';
  }
});
board.addEventListener('pointerup', event => {
  if (!gesture || gesture.id !== event.pointerId) return;
  if (gesture.dragged) {
    if (card().gap !== gesture.initial) record('Изменение интервала', gesture.before);
    render();
  } else {
    handle('canvas-gap');
  }
  gesture = null;
});
board.addEventListener('pointercancel', () => {
  if (!gesture) return;
  card().gap = gesture.initial;
  gesture = null;
  render();
});
function clearInsertionLine() {
  board.querySelectorAll('.insert-line, .insert-after').forEach(node => node.classList.remove('insert-line', 'insert-after'));
}
stage.addEventListener('dragstart', event => {
  const row = event.target.closest('[data-unit="row"][draggable="true"]');
  const resource = event.target.closest('.resource-tile');
  if (!row && !resource) return;
  movingKey = resource ? 'resource' : 'row';
  event.dataTransfer.setData('text/plain', movingKey);
  event.dataTransfer.effectAllowed = resource ? 'copy' : 'move';
});
board.addEventListener('dragover', event => {
  if (movingKey === 'resource') {
    event.preventDefault();
    return;
  }
  const target = event.target.closest('.active-card [data-unit]');
  clearInsertionLine();
  if (movingKey !== 'row' || !target || target.dataset.unit === 'row') return;
  event.preventDefault();
  const box = target.getBoundingClientRect();
  target.classList.add(event.clientY < (box.top + box.bottom) / 2 ? 'insert-line' : 'insert-after');
});
board.addEventListener('drop', event => {
  const anchor = board.querySelector('.insert-line, .insert-after');
  event.preventDefault();
  if (movingKey === 'resource') insertResource();
  if (movingKey === 'row' && anchor) {
    const next = card().order.filter(key => key !== 'row');
    next.splice(next.indexOf(anchor.dataset.unit) + Number(anchor.classList.contains('insert-after')), 0, 'row');
    if (next.join() !== card().order.join()) {
      record('Перемещение строки цены');
      card().order = next;
      render();
    }
  }
  clearInsertionLine();
});
stage.addEventListener('dragend', () => {
  movingKey = null;
  clearInsertionLine();
});
function scale() {
  return Math.min(innerWidth / 1280, innerHeight / 720, 1);
}
function fit() {
  stage.style.transform = `scale(${scale()})`;
}
addEventListener('resize', fit);
const params = new URLSearchParams(location.search);
if (params.get('theme') === 'light') state.theme = 'light';
if (params.get('view') === 'group') {
  card().auto = true;
  card().gap = 12;
  state.selection = 'group';
  state.history.a.push('Автокомпоновка', 'Изменение интервала');
}
render();
fit();
