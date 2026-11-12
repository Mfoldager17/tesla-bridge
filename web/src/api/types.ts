// What the bridge's HTTP API returns (src/TeslaBridge/Web/*Api.cs). Keep in step with the backend.

export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

/** GET /api/info */
export interface Info {
  version: string;
  version_display: string;
  commit: string | null;
  channel: "stable" | "edge" | "dev" | string;
  api_version: number;
  started_at: string;
  mqtt_connected: boolean;
  embedded_broker: boolean;
  auth_required: boolean;
  simulator: boolean;
  telemetry_hostname: string;
  telemetry_port: number;
  vehicles: { vin: string; name: string }[];
}

export type FieldSource = "stream" | "optimistic" | "restored" | "bridge" | "snapshot" | "ble";

export interface Field {
  value: Json;
  updated_at: string;
  source: FieldSource;
}

/** GET /api/state – one entry per car */
export interface CarState {
  vin: string;
  name: string;
  fields: Record<string, Field>;
}

export type CommandPhase = "pending" | "waking" | "succeeded" | "failed";

/** data of a "command" event */
export interface CommandStatus {
  command: string;
  phase: CommandPhase;
  transport: string | null;
  error: string | null;
  note: string | null;
  at: string;
}

/** GET /api/events (SSE) and /api/events/recent */
export type LiveEvent =
  | { type: "field"; vin: string; at: string; data: { key: string; value?: Json; updated_at?: string; source?: FieldSource } }
  | { type: "command"; vin: string; at: string; data: CommandStatus }
  | { type: string; vin: string | null; at: string; data: Record<string, Json> };

/** GET /api/update */
export interface UpdateInfo {
  installed_version: string;
  installed_display: string;
  latest_version: string;
  update_available: boolean;
  release_url: string | null;
  release_summary: string | null;
  release_notes: string | null;
  channel: string;
  checked_at: string | null;
  error: string | null;
}
