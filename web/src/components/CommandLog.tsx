import { For, Show } from "solid-js";
import { t, type Key } from "../i18n";
import { state } from "../state/live";
import { vin } from "../state/selection";
import { clock } from "../lib/format";

/** The latest commands for the shown car, with how they went and which way (ble / fleet-api). */
export function CommandLog(props: { max?: number }) {
  const entries = () => state.commands.filter((c) => c.vin.toUpperCase() === vin()?.toUpperCase()).slice(0, props.max ?? 40);
  return (
    <ul class="log">
      <Show when={entries().length} fallback={<li class="empty" style={{ display: "block" }}>{t("log.empty")}</li>}>
        <For each={entries()}>{(c) => (
          <li>
            <time class="num">{clock(c.at)}</time>
            <span>{c.command}</span>
            <span class={`st ${c.status}`}>{t(`st.${c.status}` as Key)}{c.transport ? ` · ${c.transport}` : ""}</span>
            <Show when={c.error}><span class="err">{c.error}</span></Show>
            <Show when={c.note}><span class="note-line">{c.note}</span></Show>
          </li>
        )}</For>
      </Show>
    </ul>
  );
}
