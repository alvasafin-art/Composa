declare const app: ComposaApplication;
declare const ai: ComposaAI;

interface ComposaApplication {
  readonly activeDocument: Document;
  readonly documents: readonly Document[];
}

interface DocumentInfo {
  title: string;
  width: number;
  height: number;
  hasSelection: boolean;
  activeLayerId: string | null;
  layers: readonly LayerInfo[];
}

interface Document {
  readonly info: DocumentInfo;
  readonly width: number;
  readonly height: number;
  readonly layers: Layer[];
  readonly activeLayer: Layer | null;
  findLayersByTag(tag: string): Layer[];
  findLayersByName(name: string): Layer[];
  addLayer(name?: string): Layer;
  addAttachedImage(index: number): Layer;
  selectRect(x: number, y: number, width: number, height: number): void;
  deselect(): void;
  export(path: string, quality?: number): void;
}

interface LayerInfo {
  id: string;
  name: string;
  kind: "text" | "group" | "adjustment" | "raster";
  tags: string[];
  text: string | null;
  visible: boolean;
  opacity: number;
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
  remove(): void;
}

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
