import { createSignal } from "solid-js";
import { getJson } from "../api/client";

/** One telemetry field in GET /api/settings */
export interface FieldSettingInfo {
  field: string;
  group: string;
  en: string;
  da: string;
  enabled: boolean;
  interval: number;
  default_interval: number;
  continuous: boolean;
  signals_month: number;
}

/** GET /api/settings */
export interface Settings {
  fields: FieldSettingInfo[];
  groups: string[];
  snapshot_interval_minutes: number;
  snapshot_default_minutes: number;
  car_view: string;
  climate_for_seat_heat: boolean;
  month_start: string;
  month_end: string;
  now: string;
  signals_per_dollar: number;
  push?: { vin: string; ok: boolean; error?: string }[];
}

const [settings, setSettings] = createSignal<Settings | null>(null);
export { settings, setSettings };

export async function loadSettings(): Promise<void> {
  try { setSettings(await getJson<Settings>("/api/settings")); } catch { /* keep what we have */ }
}
