import { createResource, For, onCleanup, Show } from "solid-js";
import { t } from "../../i18n";
import { getJson, sendJson } from "../../api/client";
import { esc } from "../../lib/format";
import { toast } from "../../components/Toast";

/** GET /api/ble – one per car */
interface BleCar {
  vin: string; name: string; configured: boolean; url: string | null; username: string | null; has_password: boolean;
  source: "env" | "dashboard" | null; esp_connected: boolean; car_in_range: boolean; car_asleep: boolean | null;
  rssi: number | null; esp_version: string | null; entities: number; looks_like_tesla_ble: boolean;
  last_error: string | null; car_ble_name: string;
}

function status(c: BleCar): [string, string, string] {
  if (!c.configured) return ["", t("ble.off"), t("ble.offSub")];
  if (!c.esp_connected) return ["bad", t("ble.down"), c.last_error ?? c.url ?? ""];
  if (!c.looks_like_tesla_ble && c.entities > 0) return ["warn", c.url ?? "", t("ble.notTesla")];
  const bits = [c.car_asleep == null ? null : t(c.car_asleep ? "ble.asleep" : "ble.awake"),
    c.rssi == null ? null : t("ble.rssi", Math.round(c.rssi)), c.esp_version ? `ESPHome ${c.esp_version.split(" ")[0]}` : null].filter(Boolean);
  return c.car_in_range ? ["ok", t("ble.inRange"), bits.join(" · ")] : ["warn", t("ble.away"), bits.join(" · ")];
}

/** Settings → Bluetooth (ESP32): where the ESP32 is, whether it sees the car, pair and test. */
export function BlePanel() {
  const [cars, { refetch, mutate }] = createResource(() => getJson<BleCar[]>("/api/ble"));
  let panel!: HTMLDivElement;
  const timer = setInterval(() => { if (!panel.contains(document.activeElement)) void refetch(); }, 5000);
  onCleanup(() => clearInterval(timer));

  const act = async (vin: string, what: "pair" | "wake" | "remove") => {
    try {
      if (what === "remove") mutate(await sendJson<BleCar[]>(`/api/ble/${vin}`, "DELETE"));
      else { await sendJson(`/api/ble/${vin}/${what}`, "POST"); toast(t(what === "pair" ? "ble.pairSent" : "ble.wakeSent")); }
    } catch (e) { toast((e as Error).message); }
  };
  const save = async (vin: string, form: HTMLFormElement) => {
    const f = new FormData(form);
    const body: Record<string, string | null> = { url: String(f.get("url")).trim(), username: String(f.get("username")).trim() || null };
    if (f.get("password")) body.password = String(f.get("password"));
    try {
      mutate(await sendJson<BleCar[]>(`/api/ble/${vin}`, "PUT", body));
      toast(t("ble.saved"));
      setTimeout(refetch, 2500);
    } catch (e) { toast((e as Error).message); }
  };

  return (
    <div class="panel" ref={panel}>
      <h2>{t("ble.title")}</h2>
      <p class="sub">{t("ble.sub")}</p>
      <For each={cars() ?? []}>{(c) => {
        const st = () => status(c);
        return (
          <div class="blecar">
            <Show when={(cars() ?? []).length > 1}><p class="small muted" style={{ margin: "0 0 6px" }}>{c.name} · {c.vin}</p></Show>
            <div class="state"><span class={`dot ${st()[0]}`} />{st()[1]}<Show when={c.source === "env"}> <small>({t("ble.fromEnv")})</small></Show></div>
            <div class="small muted">{st()[2]}</div>
            <form class="bleform" autocomplete="off" onSubmit={(e) => { e.preventDefault(); void save(c.vin, e.currentTarget); }}>
              <label>{t("ble.url")}<input type="text" name="url" placeholder="http://192.168.1.50" value={c.url ?? ""} spellcheck={false} /></label>
              <label>{t("ble.user")}<input type="text" name="username" value={c.username ?? ""} spellcheck={false} /></label>
              <label>{t("ble.pass")}<input type="password" name="password" placeholder={c.has_password ? t("ble.passKeep") : ""} /></label>
              <div class="toolbar" style={{ margin: "0" }}>
                <button type="submit" class="primary">{t("ble.save")}</button>
                <Show when={c.source === "dashboard"}><button type="button" class="ghost" onClick={() => act(c.vin, "remove")}>{t("ble.remove")}</button></Show>
              </div>
            </form>
            <Show when={c.configured && c.esp_connected && c.looks_like_tesla_ble}>
              <div class="blepair">
                <b>{t("ble.pairTitle")}</b>
                <ol><li>{t("ble.pair1")}</li><li>{t("ble.pair2")}</li><li>{t("ble.pair3")}</li></ol>
                <div class="toolbar" style={{ margin: "0" }}>
                  <button type="button" class="primary" onClick={() => act(c.vin, "pair")}>{t("ble.pair")}</button>
                  <button type="button" disabled={!c.car_in_range} onClick={() => act(c.vin, "wake")}>{t("ble.wake")}</button>
                </div>
              </div>
            </Show>
            <details class="small muted" style={{ "margin-top": "10px" }} open={!c.configured}>
              <summary>{t("ble.howTitle")}</summary>
              <p innerHTML={t("ble.how", esc(c.car_ble_name))} />
            </details>
          </div>
        );
      }}</For>
    </div>
  );
}
