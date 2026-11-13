import { createSignal } from "solid-js";
import { createStore, produce, reconcile } from "solid-js/store";
import type { CarState, CommandStatus, Field, Info, LiveEvent } from "../api/types";

export interface CommandEntry extends CommandStatus { vin: string; at: string }

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
  if (e.type === "field") {
    const d = e.data;
    const car = (s.cars[e.vin.toUpperCase()] ??= { name: e.vin, fields: {} });
    if (d.value === undefined && !d.source) delete car.fields[d.key];
    else car.fields[d.key] = { value: d.value ?? null, updated_at: d.updated_at ?? e.at, source: d.source ?? "stream" };
  } else if (e.type === "command") {
    s.commands.unshift({ ...e.data, vin: e.vin, at: e.at });
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

/**
 * Opens /api/events and keeps it open (the browser reconnects by itself). Changes made while the stream was down
 * aren't sent again, so resync() runs each time it comes back.
 */
export function connect(url: string, resync: () => void): () => void {
  const es = new EventSource(url);
  let wasDown = false;
  es.onopen = () => {
    setConnected(true);
    if (wasDown) resync();
    wasDown = false;
  };
  es.onerror = () => { setConnected(false); wasDown = true; };
  es.onmessage = (m) => {
    try { addEvents([JSON.parse(m.data) as LiveEvent]); } catch { /* ignore a malformed event */ }
  };
  return () => es.close();
}
