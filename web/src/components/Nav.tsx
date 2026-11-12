import { For, Show } from "solid-js";
import { A } from "@solidjs/router";
import icons from "./navIcons.json";
import { t, lang, setLang, type Key } from "../i18n";
import { theme, cycleTheme } from "../theme";
import { connected, state } from "../state/live";
import { vin, setVin, val } from "../state/selection";
import { update } from "../state/update";
import { setup, stepStates } from "../state/setup";

export const VIEWS = ["overview", "controls", "data", "usage", "settings", "setup"] as const;
export type View = (typeof VIEWS)[number];

/** Awake / asleep / waiting for the car – shown in the menu and the phone's top bar. */
export function carStatus(): { cls: string; text: string } {
  const online = val<boolean>("online");
  const text = online === true ? t("car.awake") : online === false ? t("car.asleep") : val("last_seen") ? t("car.waiting") : t("car.unknown");
  return { cls: online === true ? "ok" : online === false ? "warn" : "", text };
}

export function Nav(props: { carName?: string }) {
  const cars = () => Object.entries(state.cars);
  const missing = () => { const st = setup(); return st ? stepStates(st).filter((x) => x !== "done").length : 0; };
  const version = () => update()?.installed_display ?? state.info?.version_display ?? state.info?.version ?? "?";
  return (
    <nav class="side">
      <div class="brand">
        <b>Tesla Bridge</b>
        <span>{props.carName}</span>
        <Show when={cars().length > 1}>
          <select value={vin()?.toUpperCase()} onChange={(e) => setVin(e.currentTarget.value)}>
            <For each={cars()}>{([v, c]) => <option value={v}>{c.name}</option>}</For>
          </select>
        </Show>
      </div>
      <div class="navlist" role="list">
        <For each={VIEWS}>
          {(v) => (
            <A href={`/${v}`} end>
              {/* the icons are our own static SVG paths (navIcons.json) */}
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" innerHTML={(icons as Record<string, string>)[v]} />
              <span>{t(`nav.${v}` as Key)}</span>
              <Show when={v === "setup" && missing()}><span class="count">{missing()}</span></Show>
            </A>
          )}
        </For>
      </div>
      <div class="conn">
        <div><span class={`dot ${connected() ? "ok" : "bad"}`} />{connected() ? t("live.on") : t("live.off")}</div>
        <div>
          <span class={`dot ${state.info?.mqtt_connected ? "ok" : "bad"}`} />
          {state.info?.embedded_broker ? t("mqtt.embedded") : t("mqtt")}
        </div>
        <div><span class={`dot ${carStatus().cls}`} />{carStatus().text}</div>
      </div>
      <div class="prefs">
        <button type="button" onClick={() => setLang(lang() === "da" ? "en" : "da")}>{t("lang")}</button>
        <button type="button" onClick={cycleTheme}>{t(`theme.${theme()}` as Key)}</button>
      </div>
      <div class="ver">
        <span>v{version()}</span>
        <Show when={update()?.update_available}>
          <a href={update()!.release_url ?? "https://github.com/Mfoldager17/tesla-bridge/releases"} target="_blank" rel="noopener" title={t("ver.how")}>
            {t("ver.update", update()!.latest_version)}
          </a>
        </Show>
      </div>
    </nav>
  );
}
