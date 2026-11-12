import { createSignal } from "solid-js";
import { getJson } from "../api/client";

/** The parts of GET /api/settings the pages use so far (the settings page itself comes in phase 2.4). */
export interface Settings {
  climate_for_seat_heat: boolean;
  car_view: string;
  [key: string]: unknown;
}

const [settings, setSettings] = createSignal<Settings | null>(null);
export { settings, setSettings };

export async function loadSettings(): Promise<void> {
  try { setSettings(await getJson<Settings>("/api/settings")); } catch { /* keep what we have */ }
}
