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
/** Settings → AI assistants (MCP) */
export interface McpSettings {
  enabled: boolean;
  /** read_only · safe (default) · all · custom */
  policy: "read_only" | "safe" | "all" | "custom";
  allowed_commands: string[];
  token_set: boolean;
  path: string;
  /** every command, with its group, whether it opens the car, and whether the current policy allows it */
  commands: { id: string; group: string; security: boolean; allowed: boolean }[];
}

export interface Settings {
  fields: FieldSettingInfo[];
  groups: string[];
  snapshot_interval_minutes: number;
  snapshot_default_minutes: number;
  car_view: string;
  climate_for_seat_heat: boolean;
  map_enabled: boolean;
  mcp: McpSettings;
  map_tile_url: string | null;
  map_tile_attribution: string | null;
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
