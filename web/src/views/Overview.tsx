import { createSignal, For, Show, type JSX } from "solid-js";
import { t, locale } from "../i18n";
import { state } from "../state/live";
import { vin, val, pending, motion, chargingNow } from "../state/selection";
import { ago, hm, nf } from "../lib/format";
import { CarView } from "../components/CarView";
import { CarTiles } from "../components/CarTiles";
import { CommandLog } from "../components/CommandLog";

function Battery() {
  const pct = () => val<number>("battery_level");
  const limit = () => val<number>("charge_limit");
  const range = () => val<number>("est_range") ?? val<number>("ideal_range");
  const power = () => (val<number>("ac_charging_power") ?? 0) + (val<number>("dc_charging_power") ?? 0);
  const chargeLine = (): JSX.Element => {
    const cs = val<string>("charging_state");
    if (cs === "charging") {
      const parts = [power() ? `${nf(power(), 1)} kW` : null,
        val("time_to_full_charge") && limit() != null ? t("charging.until", hm(val<number>("time_to_full_charge")), limit()!) : null].filter(Boolean);
      return <><span class="state on">{t("charging")}</span>{parts.map((p) => ` · ${p}`).join("")}</>;
    }
    if (cs === "starting") return <span class="state on">{t("charging.starting")}</span>;
    if (cs === "complete") return <span class="state">{t("charging.done")}</span>;
    if (cs === "stopped") return <span class="state">{t("charging.stopped")}</span>;
    if (val("charge_cable_connected")) return <span class="state">{t("charging.plugged")}</span>;
    return <span class="muted">{t("charging.unplugged")}</span>;
  };
  return (
    <div class="battery">
      <div class="pct num">{pct() == null ? "–" : <>{Math.round(pct()!)}<small>%</small></>}</div>
      <div class="range">{range() != null ? (() => { const [a, b] = t("range", "\u0000").split("\u0000"); return <>{a}<b class="num">{nf(range()!)}</b>{b}</>; })() : t("range.none")}</div>
      <div class="gauge" aria-hidden="true">
        <div class="fill" classList={{ charging: chargingNow(), low: !chargingNow() && pct() != null && pct()! < 20 }}
          style={{ width: `${Math.max(0, Math.min(100, pct() ?? 0))}%` }} />
        <Show when={limit() != null}><div class="limit" style={{ left: `calc(${limit()}% - 1px)` }} /></Show>
      </div>
      <div class="gauge-scale"><span>0</span><span>{limit() == null ? "" : t("limit", limit()!)}</span><span>100 %</span></div>
      <div class="charge-line">{chargeLine()}</div>
    </div>
  );
}

function Facts() {
  const yes = (v: unknown) => (v === true ? t("on") : v === false ? t("off") : "–");
  const lockText = () => {
    const m = motion("locked"), lk = val<boolean>("locked");
    return m === "LOCKING" ? t("locking") : m === "UNLOCKING" ? t("unlocking") : lk === true ? t("locked") : lk === false ? t("unlocked") : "–";
  };
  const loc = () => val<{ latitude: number; longitude: number }>("location");
  const dest = () => val<string>("destination");
  const arrive = () => {
    const m = val<number>("minutes_to_arrival");
    return dest() && m != null ? t("arrive", new Date(Date.now() + m * 60000).toLocaleTimeString(locale(), { hour: "2-digit", minute: "2-digit" })) : "";
  };
  const rows = (): [string, JSX.Element, string, string | null][] => [
    [t("f.climate"), yes(val("climate_on")), `${val("inside_temp") ?? "–"} °C ${t("f.inside").toLowerCase()} · ${val("outside_temp") ?? "–"} °C ${t("f.outside")}`, "climate_on"],
    [t("f.lock"), lockText(), "", "locked"],
    [t("f.sentry"), yes(val("sentry_mode")), "", "sentry_mode"],
    [t("f.drive"), !val("gear") || val("gear") === "P" ? t("parked") : `${val("gear")} · ${val("speed") ?? 0} km/h`, "", "gear"],
    [t("f.location"), loc() ? <a href={`https://www.openstreetmap.org/?mlat=${loc()!.latitude}&mlon=${loc()!.longitude}#map=16/${loc()!.latitude}/${loc()!.longitude}`} target="_blank" rel="noopener">{t("showmap")}</a> : "–", "", null],
    [t("f.destination"), dest() ?? t("nonav"), arrive(), null],
    [t("f.seen"), ago(val("last_seen")), "", null],
    [t("f.odometer"), val("odometer") != null ? `${nf(val<number>("odometer")!)} km` : "–", "", null],
    [t("f.software"), val<string>("software_version") ?? "–", "", null],
  ];
  return (
    <dl class="facts">
      <For each={rows()}>{([k, v, sub, key]) => (
        <div><dt>{k}</dt><dd classList={{ pending: !!key && pending(key) }}>{v}<Show when={sub}><span class="sub">{sub}</span></Show></dd></div>
      )}</For>
    </dl>
  );
}

export function Overview() {
  const [threeD, setThreeD] = createSignal(false);
  const name = () => (vin() ? state.cars[vin()!.toUpperCase()]?.name : undefined);
  return (
    <section class="view">
      <div class="hero" classList={{ has3d: threeD() }}>
        <CarView name={name()} onMode={setThreeD} />
        <Battery />
      </div>
      <div class="tiles compact"><CarTiles compact /></div>
      <Facts />
      <div class="panel">
        <h2>{t("log.title")}</h2>
        <p class="sub">{t("log.sub")}</p>
        <CommandLog />
      </div>
    </section>
  );
}
