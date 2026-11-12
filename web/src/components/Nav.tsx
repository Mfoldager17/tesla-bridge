import { For, Show } from "solid-js";
import { A } from "@solidjs/router";
import icons from "./navIcons.json";
import { t, lang, setLang, type Key } from "../i18n";
import { theme, cycleTheme } from "../theme";
import { connected, state } from "../state/live";
import { update } from "../state/update";

export const VIEWS = ["overview", "controls", "data", "usage", "settings", "setup"] as const;
export type View = (typeof VIEWS)[number];

export function Nav(props: { carName?: string }) {
  const version = () => update()?.installed_display ?? state.info?.version_display ?? state.info?.version ?? "?";
  return (
    <nav class="side">
      <div class="brand">
        <b>Tesla Bridge</b>
        <span>{props.carName}</span>
      </div>
      <div class="navlist" role="list">
        <For each={VIEWS}>
          {(v) => (
            <A href={`/${v}`} end>
              {/* the icons are our own static SVG paths (navIcons.json) */}
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" innerHTML={(icons as Record<string, string>)[v]} />
              <span>{t(`nav.${v}` as Key)}</span>
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
