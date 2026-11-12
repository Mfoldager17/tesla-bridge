// Types for car3d.js (the three.js car, shared with the first dashboard).
export const PAINTS: [name: string, color: string, metalness: number, roughness: number][];
export const INTERIORS: [name: string, color: string][];

export interface Car3DState {
  doors: Record<"fl" | "fr" | "rl" | "rr", boolean>;
  open: Record<"frunk" | "trunk" | "port" | "fl" | "fr" | "rl" | "rr", boolean>;
  windows?: Record<"fl" | "fr" | "rl" | "rr", boolean>;
  charging: boolean;
  speed: number;
  reverse: boolean;
  climate: "heat" | "cool" | null;
}

export interface Car3D {
  update(state: Car3DState): void;
  setLabels(labels: Record<string, string>): void;
  setColors(colors: { paint: string; interior: string }): void;
  dispose(): void;
}

export function mount(
  wrap: HTMLElement,
  glb: ArrayBuffer,
  opts: { labels: Record<string, string>; onHotspot?: (id: string) => void },
): Promise<Car3D>;
