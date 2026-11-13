import { createEffect, createMemo, createSignal, For, on, Show } from "solid-js";
import { t, type Key } from "../i18n";
import { state } from "../state/live";
import { vin } from "../state/selection";
import { api } from "../api/client";
import { ago, clock } from "../lib/format";
import { Tabs } from "../components/Tabs";
import type { Field, Json, LiveEvent } from "../api/types";

function fmt(key: string, v: Json | undefined): string {
  if (v === null || v === undefined) return "–";
  if (typeof v === "boolean") return v ? t("yes") : t("no");
  if (typeof v === "object") {
    const o = v as { latitude?: number; longitude?: number };
    return key.includes("location") && o.latitude != null ? `${o.latitude.toFixed(5)}, ${o.longitude!.toFixed(5)}` : JSON.stringify(v);
  }
  return String(v);
}

/** One field; lights up briefly when its value changes. */
function FieldRow(props: { name: string; field: Field }) {
  const [flash, setFlash] = createSignal(false);
  createEffect(on(() => props.field.updated_at, () => { setFlash(true); setTimeout(() => setFlash(false), 1200); }, { defer: true }));
  return (
    <tr classList={{ flash: flash() }}>
      <td class="code">{props.name}</td>
      <td class="val">{fmt(props.name, props.field.value)}</td>
      <td><span class={`src ${props.field.source}`}>{props.field.source || "–"}</span></td>
      <td class="age">{ago(props.field.updated_at)}</td>
    </tr>
  );
}

function Fields() {
  const [filter, setFilter] = createSignal("");
  const fields = () => (vin() ? state.cars[vin()!.toUpperCase()]?.fields ?? {} : {});
  const keys = createMemo(() => Object.keys(fields()).sort().filter((k) => !filter() || k.includes(filter().toLowerCase())));
  return (
    <>
      <div class="toolbar">
        <input type="text" placeholder={t("data.filter")} value={filter()} onInput={(e) => setFilter(e.currentTarget.value)} />
        <span class="spacer" /><span class="muted small">{t("data.count", keys().length)}</span>
      </div>
      <div class="tablewrap"><table>
        <thead><tr><th>{t("th.field")}</th><th>{t("th.value")}</th><th>{t("th.source")}</th><th>{t("th.updated")}</th></tr></thead>
        <tbody>
          <For each={keys()} fallback={<tr><td colspan="4" class="empty">{t("data.noData")}</td></tr>}>
            {(k) => <FieldRow name={k} field={fields()[k]} />}
          </For>
        </tbody>
      </table></div>
    </>
  );
}

function Raw() {
  const [paused, setPaused] = createSignal<LiveEvent[] | null>(null);
  const rows = () => (paused() ?? state.raw.filter((e) => !e.vin || e.vin.toUpperCase() === vin()?.toUpperCase())).slice(0, 200);
  const what = (e: LiveEvent) => {
    const d = e.data as Record<string, Json>;
    if (e.type === "raw") return <><span class="code">{String(d.field)}</span> = <span class="code">{JSON.stringify(d.value)}</span></>;
    if (e.type === "connectivity") return String(d.status);
    return <span class="code">{JSON.stringify(d)}</span>;
  };
  const simulate = async () => { if (vin()) await api(`/api/vehicles/${vin()}/simulate`, { method: "POST" }); };
  return (
    <>
      <div class="toolbar">
        <span class="muted small">{t("data.rawhint")}</span><span class="spacer" />
        <button type="button" onClick={() => setPaused(paused() ? null : [...rows()])}>{paused() ? t("data.resume") : t("data.pause")}</button>
        <Show when={state.info?.simulator}><button type="button" onClick={simulate}>{t("data.sim")}</button></Show>
      </div>
      <div class="tablewrap"><table>
        <thead><tr><th>{t("th.time")}</th><th>{t("th.type")}</th><th>{t("th.data")}</th></tr></thead>
        <tbody>
          <For each={rows()} fallback={<tr><td colspan="3" class="empty">{t("data.waiting")}</td></tr>}>{(e) => (
            <tr>
              <td class="age num">{clock(e.at)}</td>
              <td>{e.type === "telemetry_error" ? <span class="st failed">{e.type}</span> : e.type}</td>
              <td class="val">{what(e)}</td>
            </tr>
          )}</For>
        </tbody>
      </table></div>
    </>
  );
}

function CarConfig() {
  const [out, setOut] = createSignal<string | null>(null);
  const show = async (path: string, method = "GET") => {
    setOut(t("loading"));
    const r = await api(path, { method });
    setOut(JSON.stringify(await r.json().catch(() => ({ error: r.statusText })), null, 2));
  };
  const host = () => (state.info?.telemetry_hostname ? `${state.info.telemetry_hostname}:${state.info.telemetry_port}` : "–");
  return (
    <>
      <p class="muted small">{t("data.cfgHint", host())}</p>
      <div class="toolbar">
        <button type="button" onClick={() => vin() && show(`/api/vehicles/${vin()}/telemetry-config`)}>{t("data.cfg")}</button>
        <button type="button" onClick={() => vin() && show(`/api/vehicles/${vin()}/telemetry-errors`)}>{t("data.err")}</button>
        <button type="button" class="primary" onClick={() => vin() && confirm(t("confirm.push")) && show(`/api/vehicles/${vin()}/telemetry-config`, "POST")}>{t("data.push")}</button>
      </div>
      <Show when={out()}><pre>{out()}</pre></Show>
    </>
  );
}

type Pane = "fields" | "raw" | "car";

export function Data() {
  const [pane, setPane] = createSignal<Pane>("fields");
  return (
    <section class="view">
      <h1>{t("nav.data")}</h1>
      <p class="lede">{t("data.lede")}</p>
      <Tabs tabs={(["fields", "raw", "car"] as Pane[]).map((p) => [p, t(`data.${p}` as Key)])} value={pane()} onChange={setPane} />
      <Show when={pane() === "fields"}><Fields /></Show>
      <Show when={pane() === "raw"}><Raw /></Show>
      <Show when={pane() === "car"}><CarConfig /></Show>
    </section>
  );
}
