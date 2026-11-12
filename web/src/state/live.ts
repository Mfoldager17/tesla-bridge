import { createSignal } from "solid-js";
import { createStore, produce, reconcile } from "solid-js/store";
import type { CarState, CommandStatus, Field, Info, LiveEvent } from "../api/types";

export interface CommandEntry extends CommandStatus { vin: string }

export interface LiveState {
  info: Info | null;
  /** fields per VIN – read with field(vin, key) */
  cars: Record<string, { name: string; fields: Record<string, Field> }>;
  /** newest first, at most MAX_LOG */
  commands: CommandEntry[];
  /** other events (raw telemetry, connectivity …), newest first */
  raw: LiveEvent[];
}

export const MAX_LOG = 200;

/** Pure: applies one event to the state. Kept apart from the store so it can be tested. */
export function applyEvent(s: LiveState, e: LiveEvent): void {
  if (e.type === "field" && e.vin) {
    const d = (e as Extract<LiveEvent, { type: "field" }>).data;
    const car = (s.cars[e.vin.toUpperCase()] ??= { name: e.vin, fields: {} });
    if (d.value === undefined && !d.source) delete car.fields[d.key];
    else car.fields[d.key] = { value: d.value ?? null, updated_at: d.updated_at ?? e.at, source: d.source ?? "stream" };
  } else if (e.type === "command" && e.vin) {
    s.commands.unshift({ ...(e.data as unknown as CommandStatus), vin: e.vin });
    if (s.commands.length > MAX_LOG) s.commands.length = MAX_LOG;
  } else {
    s.raw.unshift(e);
    if (s.raw.length > MAX_LOG) s.raw.length = MAX_LOG;
  }
}

export function carsFrom(list: CarState[]): LiveState["cars"] {
  return Object.fromEntries(list.map((c) => [c.vin.toUpperCase(), { name: c.name, fields: c.fields }]));
}

const [state, setState] = createStore<LiveState>({ info: null, cars: {}, commands: [], raw: [] });
const [connected, setConnected] = createSignal(false);
export { state, connected };

export function setInfo(info: Info): void { setState("info", reconcile(info)); }
export function setCars(list: CarState[]): void { setState("cars", reconcile(carsFrom(list))); }
export function addEvents(events: LiveEvent[]): void { setState(produce((s) => events.forEach((e) => applyEvent(s, e)))); }

export function field(vin: string | undefined, key: string): Field | undefined {
  return vin ? state.cars[vin.toUpperCase()]?.fields[key] : undefined;
}
export function value<T = unknown>(vin: string | undefined, key: string): T | undefined {
  return field(vin, key)?.value as T | undefined;
}
export const isPending = (vin: string | undefined, key: string) => field(vin, key)?.source === "optimistic";

/** Opens /api/events and keeps it open (the browser reconnects by itself). */
export function connect(url: string): () => void {
  const es = new EventSource(url);
  es.onopen = () => setConnected(true);
  es.onerror = () => setConnected(false);
  es.onmessage = (m) => {
    try { addEvents([JSON.parse(m.data) as LiveEvent]); } catch { /* ignore a malformed event */ }
  };
  return () => es.close();
}
