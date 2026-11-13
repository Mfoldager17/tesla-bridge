import { createEffect, createSignal, For, on, onCleanup, Show } from "solid-js";
import { api } from "../api/client";
import { send, sendConfirmed } from "../api/commands";
import { t, lang, type Key } from "../i18n";
import { val } from "../state/selection";
import { carColors, modelInfo, modelSource, setCarColors } from "../state/carModel";
import type { Car3D, Car3DState } from "../lib/car3d";
import { CarPlan } from "./CarPlan";
import { toast } from "./Toast";
import { esc } from "../lib/format";

const SIDES = ["fl", "fr", "rl", "rr"] as const;

function car3dState(): Car3DState {
  const d = (k: string) => !!val(`door_${k}`);
  const doors = { fl: d("driver_front_open"), fr: d("passenger_front_open"), rl: d("driver_rear_open"), rr: d("passenger_rear_open") };
  const windows = { fl: !!val("window_driver_front_open"), fr: !!val("window_passenger_front_open"), rl: !!val("window_driver_rear_open"), rr: !!val("window_passenger_rear_open") };
  const cs = val<string>("charging_state"), temp = val<number>("driver_temp_setting"), inside = val<number>("inside_temp");
  return {
    doors, windows,
    open: { frunk: !!val("frunk_open"), trunk: !!val("trunk_open"), port: !!val("charge_port_open"),
      fl: doors.fl || windows.fl, fr: doors.fr || windows.fr, rl: doors.rl || windows.rl, rr: doors.rr || windows.rr },
    charging: cs === "charging" || cs === "starting",
    speed: val<number>("speed") ?? 0,
    reverse: val("gear") === "R",
    climate: val("climate_on") ? ((temp ?? 21) >= (inside ?? 20) ? "heat" : "cool") : null,
  };
}

function onHotspot(id: string): void {
  if (id === "frunk") {
    if (val("frunk_open")) toast(t("frunkManual")); else sendConfirmed(t("confirm.frunk"), "frunk", "OPEN");
  } else if (id === "trunk") {
    if (val("trunk_open")) void send("trunk", "CLOSE"); else sendConfirmed(t("confirm.trunkOpen"), "trunk", "OPEN");
  } else if (id === "port") void send("charge_port_door", val("charge_port_open") ? "CLOSE" : "OPEN");
}

/** The car in 3D (bundled or your own model) or the drawing. Falls back to the drawing when 3D can't be shown. */
export function CarView(props: { name?: string; onMode?: (threeD: boolean) => void }) {
  let wrap!: HTMLDivElement;
  const [car, setCar] = createSignal<Car3D | null>(null);
  const [failed, setFailed] = createSignal(false);
  const [palette, setPalette] = createSignal<{ paints: [string, string][]; interiors: [string, string][] } | null>(null);
  const [pickerOpen, setPickerOpen] = createSignal(false);
  const src = () => (failed() || typeof WebGLRenderingContext === "undefined" ? null : modelSource(modelInfo()));
  const builtin = () => src() === "/api/car-model/builtin";

  createEffect(() => props.onMode?.(!!src()));

  // Mount (again) when the chosen model changes
  createEffect(on(src, (url) => {
    car()?.dispose();
    setCar(null);
    if (!url) return;
    let cancelled = false;
    onCleanup(() => { cancelled = true; });
    (async () => {
      try {
        const [buf, mod] = await Promise.all([(await api(url)).arrayBuffer(), import("../lib/car3d")]);
        if (cancelled) return;
        setPalette({ paints: mod.PAINTS.map(([k, c]) => [k, c]), interiors: mod.INTERIORS });
        const c = await mod.mount(wrap, buf, { labels: labels(), onHotspot });
        if (cancelled) { c.dispose(); return; }
        c.setColors(carColors());
        setCar(c);
      } catch (e) {
        console.warn("The 3D model could not be shown, using the drawing", e);
        setFailed(true);
      }
    })();
  }));
  onCleanup(() => car()?.dispose());

  const labels = () => {
    const st = car3dState();
    const l: Record<string, string> = Object.fromEntries(["frunk", "trunk", "port", ...SIDES].map((k) => [k, t(`hs.${k}` as Key)]));
    for (const k of SIDES) l[k] = `${t(`hs.${k}` as Key)} ${st.doors[k] ? t("door") : t("window")}`;
    return l;
  };
  createEffect(() => { const c = car(); if (c) { c.update(car3dState()); c.setLabels(labels()); } });
  createEffect(() => { lang(); car()?.setLabels(labels()); });
  createEffect(() => car()?.setColors(carColors()));

  const openList = () => {
    const st = car3dState(), list: string[] = [];
    if (st.open.frunk) list.push(t("hs.frunk"));
    if (st.open.trunk) list.push(t("hs.trunk"));
    if (st.open.port) list.push(t("hs.port"));
    for (const k of SIDES) {
      if (st.doors[k]) list.push(`${t(`hs.${k}` as Key)} ${t("door")}`);
      else if (st.windows?.[k]) list.push(`${t(`hs.${k}` as Key)} ${t("window")}`);
    }
    return list;
  };
  const credit = () => modelInfo()?.builtin_credit;

  return (
    <div class="car">
      <Show when={src()} fallback={
        <>
          <CarPlan name={props.name} />
          <div class="car-legend">
            <span><i style={{ background: "var(--warn)" }} />{t("car.open")}</span>
            <span><i style={{ background: "var(--charge)" }} />{t("car.plugged")}</span>
            <span><i style={{ background: "var(--pending)" }} />{t("car.pending")}</span>
          </div>
        </>
      }>
        <div class="c3d" classList={{ ready: !!car() }} ref={wrap}>
          <div class="c3d-loading">{t("car3d.loading")}</div>
          <button type="button" class="palbtn" aria-expanded={pickerOpen()} aria-label={t("car3d.colors")} onClick={() => setPickerOpen(!pickerOpen())}>
            <svg viewBox="0 0 24 24"><path d="M12 3a9 9 0 0 0 0 18c.83 0 1.5-.67 1.5-1.5 0-.39-.15-.74-.39-1.01-.23-.26-.38-.61-.38-.99 0-.83.67-1.5 1.5-1.5H16a5 5 0 0 0 5-5c0-4.42-4.03-8-9-8zm-5.5 9a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3zm3-4a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3zm5 0a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3zm3 4a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3z" /></svg>
          </button>
          <Show when={pickerOpen() && palette()}>
            <div class="cpick">
              <div><div class="lbl">{t("car3d.paint")}</div><div class="sws">
                <For each={palette()!.paints}>{([k, c]) => (
                  <button type="button" class="sw" style={{ background: c }} title={t(`paint.${k}` as Key)} aria-label={t(`paint.${k}` as Key)}
                    aria-pressed={carColors().paint === k} onClick={() => setCarColors({ ...carColors(), paint: k })} />
                )}</For>
              </div></div>
              <div><div class="lbl">{t("car3d.interior")}</div><div class="sws">
                <For each={palette()!.interiors}>{([k, c]) => (
                  <button type="button" class="sw" style={{ background: c }} title={t(`paint.${k}` as Key)} aria-label={t(`paint.${k}` as Key)}
                    aria-pressed={carColors().interior === k} onClick={() => setCarColors({ ...carColors(), interior: k })} />
                )}</For>
              </div></div>
            </div>
          </Show>
        </div>
        <div class="car-legend">{openList().length ? t("car3d.open", openList().join(", ")) : t("car3d.allClosed")}</div>
        <Show when={builtin() && credit()}>
          {(c) => <div class="credit" innerHTML={t("credit", esc(c().url), esc(c().title), esc(c().author), esc(c().license_url), esc(c().license))} />}
        </Show>
      </Show>
    </div>
  );
}
