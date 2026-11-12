import { createSignal } from "solid-js";
import { getJson } from "../api/client";

/** GET /api/car-model/info */
export interface ModelInfo {
  view: "builtin" | "custom" | "drawing";
  present: boolean;
  builtin: boolean;
  builtin_credit?: { title: string; author: string; url: string; license: string; license_url: string };
  source?: "uploaded" | "file";
  file?: string;
  size?: number;
  updated_at?: string;
}

const [modelInfo, setModelInfo] = createSignal<ModelInfo | null>(null);
export { modelInfo };

export async function loadModelInfo(): Promise<void> {
  try { setModelInfo(await getJson<ModelInfo>("/api/car-model/info")); } catch { setModelInfo(null); }
}

/** Which model to show: the chosen view, falling back when its model is missing. null = the drawing. */
export function modelSource(m: ModelInfo | null): string | null {
  if (!m) return null;
  if (m.view === "custom" && m.present) return "/api/car-model";
  if (m.view !== "drawing" && m.builtin) return "/api/car-model/builtin";
  return null;
}

// Paint and interior for the 3D model – remembered in this browser only.
const COLORS_KEY = "tb_car_colors";
export interface CarColors { paint: string; interior: string }
function loadColors(): CarColors {
  try { return JSON.parse(localStorage.getItem(COLORS_KEY) ?? "null") ?? { paint: "white", interior: "black" }; }
  catch { return { paint: "white", interior: "black" }; }
}
const [carColors, setColorsSignal] = createSignal<CarColors>(loadColors());
export { carColors };
export function setCarColors(c: CarColors): void {
  try { localStorage.setItem(COLORS_KEY, JSON.stringify(c)); } catch { /* no storage */ }
  setColorsSignal(c);
}
