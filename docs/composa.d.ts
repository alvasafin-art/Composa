declare const app: ComposaApplication;
declare const ai: ComposaAI;
declare const ui: { form(options: ScriptForm): Promise<Record<string, string | number | boolean>> };
declare function prompt(message: string, defaultValue?: string): Promise<string>;

interface ScriptForm {
  title: string;
  fields: { name: string; label: string; type: "number" | "text" | "boolean"; value: string | number | boolean; min?: number; max?: number }[];
}
interface Guide { readonly id: string; readonly axis: "horizontal" | "vertical"; readonly position: number; }

interface ComposaApplication {
  readonly activeDocument: Document;
  readonly documents: readonly Document[];
}

interface DocumentInfo {
  title: string;
  width: number;
  height: number;
  hasSelection: boolean;
  selection: SelectionBounds | null;
  activeLayerId: string | null;
  guides: readonly Guide[];
  layers: readonly LayerInfo[];
}

interface Document {
  readonly info: DocumentInfo;
  readonly width: number;
  readonly height: number;
  readonly layers: Layer[];
  readonly activeLayer: Layer | null;
  readonly selection: SelectionBounds | null;
  readonly guides: readonly Guide[];
  addGuide(axis: "vertical" | "horizontal", position: number): void;
  moveGuide(id: string, position: number): void;
  removeGuide(id: string): void;
  clearGuides(): void;
  findLayersByTag(tag: string): Layer[];
  findLayersByName(name: string): Layer[];
  addLayer(name?: string): Layer;
  addAttachedImage(index: number): Layer;
  addRectangle(x: number, y: number, width: number, height: number, color?: string, name?: string): Layer;
  addEllipse(x: number, y: number, width: number, height: number, color?: string, name?: string): Layer;
  addShape(options: ShapeOptions): Layer;
  addLine(x1: number, y1: number, x2: number, y2: number, color?: string, width?: number): Layer;
  addText(text: string, x: number, y: number, options?: TextOptions): Layer;
  fill(color: string): void;
  paintStroke(points: { x: number; y: number }[], options?: PaintOptions): void;
  selectRect(x: number, y: number, width: number, height: number): void;
  selectEllipse(x: number, y: number, width: number, height: number): void;
  selectAll(): void;
  invertSelection(): void;
  deselect(): void;
  groupLayers(layers: (Layer | string)[], name?: string): Layer;
  resizeImage(width: number, height: number): void;
  resizeCanvas(width: number, height: number, anchor?: CanvasAnchor): void;
  export(path: string, quality?: number): void;
}

interface LayerInfo {
  readonly id: string;
  name: string;
  readonly kind: "text" | "shape" | "group" | "adjustment" | "raster";
  readonly shape: { kind: "Rectangle" | "RoundedRectangle" | "Ellipse" | "Line"; color: string; alpha: number; cornerRadius: number; lineWidth: number } | null;
  tags: string[];
  text: string | null;
  visible: boolean;
  opacity: number;
  blendMode: string;
  transform: LayerTransform;
}

interface Layer extends LayerInfo {
  name: string;
  tags: string[];
  text: string | null;
  visible: boolean;
  opacity: number;
  transform: LayerTransform;
  select(): void;
  fill(color: string): void;
  duplicate(): Layer;
  setShapeColor(color: string): void;
  moveBy(dx: number, dy: number): void;
  remove(): void;
}

interface ShapeOptions {
  kind?: "rectangle" | "ellipse" | "circle" | "rounded" | "roundedrectangle";
  x?: number;
  y?: number;
  width?: number;
  height?: number;
  color?: string; // #RRGGBB or #AARRGGBB
  cornerRadius?: number;
  name?: string;
}

interface SelectionBounds { readonly x: number; readonly y: number; readonly width: number; readonly height: number; }

interface TextOptions {
  fitToCanvas?: boolean; // default true: wraps/shrinks full text without truncation
  boxWidth?: number;
  boxHeight?: number;
  size?: number;
  color?: string;
  fontFamily?: string;
  bold?: boolean;
  italic?: boolean;
  name?: string;
}

interface PaintOptions {
  color?: string;
  size?: number;
  hardness?: number; // 0..1
  opacity?: number; // 0..1
}

type CanvasAnchor = "TopLeft" | "Top" | "TopRight" | "Left" | "Center" | "Right" | "BottomLeft" | "Bottom" | "BottomRight";

interface LayerTransform {
  x: number;
  y: number;
  width: number;
  height: number;
  rotation: number;
}

interface AIOptions {
  width?: number;
  height?: number;
  seed?: number;
  x?: number;
  y?: number;
}

interface ComposaAI {
  generateImage(prompt: string, options?: AIOptions): void;
  imageEdit(prompt: string, options?: AIOptions): void;
  generativeFill(prompt: string, options?: AIOptions): void;
  removeObject(options?: AIOptions): void;
  generativeExpand(prompt: string, options?: AIOptions): void;
  changeBackground(prompt: string, options?: AIOptions): void;
  harmonize(prompt: string, options?: AIOptions): void;
  matchToScene(options?: AIOptions): void;
  relight(prompt: string, options?: AIOptions): void;
  upscale(options?: AIOptions): void;
  selectSubject(options?: AIOptions): void;
  objectSelection(prompt: string, options?: AIOptions): void;
}
