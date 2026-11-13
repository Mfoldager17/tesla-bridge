import { createEffect, createMemo, createSignal, For, on, onCleanup, Show } from "solid-js";
import { t } from "../i18n";
import { sendJson } from "../api/client";
import { loadSettings, setSettings, settings, type Settings as SettingsData } from "../state/settings";
import { toast } from "../components/Toast";
import { createDraft, dur, Estimate, FieldGroups } from "./settings/TelemetryFields";
import { CarModelPanel } from "./settings/CarModelPanel";
import { BlePanel } from "./settings/BlePanel";
import { McpPanel } from "./settings/McpPanel";

const SNAPSHOTS = [0, 15, 30, 60, 120, 360, 720, 1440];

export function Settings() {
  void loadSettings();
  const d = createDraft(settings);
  const [snapshot, setSnapshot] = createSignal<number | null>(null);
  const [saving, setSaving] = createSignal(false);
  const [result, setResult] = createSignal<{ ok: boolean; text: string } | null>(null);
  // a fresh draft whenever the saved settings change
  createEffect(on(settings, (s) => { d.reset(); setSnapshot(s?.snapshot_interval_minutes ?? null); }));

  const dirty = createMemo(() => !!settings() && (d.changed().length > 0 || snapshot() !== settings()!.snapshot_interval_minutes));
  const leave = (e: BeforeUnloadEvent) => { if (dirty()) { e.preventDefault(); e.returnValue = t("unsaved"); } };
  window.addEventListener("beforeunload", leave);
  onCleanup(() => window.removeEventListener("beforeunload", leave));

  const pushResult = (res: SettingsData) => {
    const failed = (res.push ?? []).filter((p) => !p.ok);
    setResult(!res.push ? { ok: true, text: t("settings.savedLocal") }
      : failed.length ? { ok: false, text: t("settings.pushFail", failed.map((f) => String(f.error).replace(/\.+$/, "")).join("; ")) }
      : { ok: true, text: t("settings.saved") });
  };
  const save = async () => {
    setSaving(true);
    try {
      const body: Record<string, unknown> = {
        fields: Object.fromEntries(d.changed().map((f) => [f.field, { enabled: d.draft[f.field].enabled, interval_seconds: d.draft[f.field].interval }])),
      };
      if (snapshot() !== settings()!.snapshot_interval_minutes) body.snapshot_interval_minutes = snapshot();
      const res = await sendJson<SettingsData>("/api/settings", "PUT", body);
      setSettings(res); pushResult(res);
    } catch (e) { setResult({ ok: false, text: (e as Error).message }); }
    finally { setSaving(false); }
  };
  const reset = async () => {
    if (!confirm(t("confirm.reset"))) return;
    const res = await sendJson<SettingsData>("/api/settings/reset", "POST");
    setSettings(res); pushResult(res);
  };
  const setSeatClimate = async (on: boolean) => {
    try {
      await sendJson("/api/settings", "PUT", { climate_for_seat_heat: on, push: false });
      setSettings({ ...settings()!, climate_for_seat_heat: on });
      toast(t("seatClimate.saved"));
    } catch (e) { toast((e as Error).message); }
  };
  const setMapEnabled = async (on: boolean) => {
    try {
      await sendJson("/api/settings", "PUT", { map_enabled: on, push: false });
      setSettings({ ...settings()!, map_enabled: on });
      toast(t("map.saved"));
    } catch (e) { toast((e as Error).message); }
  };

  return (
    <section class="view">
      <h1>{t("nav.settings")}</h1>
      <p class="lede">{t("settings.lede")}</p>
      <Show when={settings()}>{(s) => (
        <>
          <div class="panel">
            <h2>{t("settings.presets")}</h2>
            <p class="sub">{t("settings.presetsSub")}</p>
            <div class="presets">
              <For each={["economy", "default", "live"] as const}>{(p) => (
                <button class="preset" type="button" onClick={() => d.preset(p)}>
                  <b>{t(`preset.${p}`)}</b><span>{t(`preset.${p}Sub`)}</span>
                </button>
              )}</For>
            </div>
          </div>

          <div class="panel">
            <h2>{t("settings.fields")}</h2>
            <p class="sub">{t("settings.fieldsSub")}</p>
            <FieldGroups settings={s()} d={d} />
          </div>

          <CarModelPanel />

          <div class="panel">
            <h2>{t("map.title")}</h2>
            <p class="sub">{t("map.sub")}</p>
            <select value={s().map_enabled === false ? "off" : "on"} onChange={(e) => setMapEnabled(e.currentTarget.value === "on")}>
              <option value="on">{t("map.on")}</option>
              <option value="off">{t("map.off")}</option>
            </select>
          </div>

          <McpPanel />

          <div class="panel">
            <h2>{t("seatClimate.title")}</h2>
            <p class="sub">{t("seatClimate.sub")}</p>
            <select value={s().climate_for_seat_heat === false ? "off" : "on"} onChange={(e) => setSeatClimate(e.currentTarget.value === "on")}>
              <option value="on">{t("seatClimate.on")}</option>
              <option value="off">{t("seatClimate.off")}</option>
            </select>
          </div>

          <BlePanel />

          <div class="panel">
            <h2>{t("settings.snapshot")}</h2>
            <p class="sub">{t("settings.snapshotSub")}</p>
            <select value={snapshot() ?? s().snapshot_interval_minutes} onChange={(e) => setSnapshot(Number(e.currentTarget.value))}>
              <For each={[...new Set([...SNAPSHOTS, snapshot() ?? 0])].sort((a, b) => a - b)}>{(m) => (
                <option value={m}>{m === 0 ? t("snap.off") : t("snap.every", dur(m * 60))}{m === s().snapshot_default_minutes ? " ★" : ""}</option>
              )}</For>
            </select>
          </div>

          <div class="savebar" classList={{ dirty: dirty() }}>
            <Estimate d={d} dirty={dirty()} />
            <button type="button" class="ghost" onClick={reset}>{t("settings.reset")}</button>
            <button type="button" disabled={!dirty()} onClick={() => { d.reset(); setSnapshot(s().snapshot_interval_minutes); }}>{t("settings.undo")}</button>
            <button type="button" class="primary" disabled={!dirty() || saving()} onClick={save}>{saving() ? t("settings.saving") : t("settings.save")}</button>
          </div>
          <Show when={result()}>{(r) => <div class={`note ${r().ok ? "ok" : "bad"}`}>{r().text}</div>}</Show>
        </>
      )}</Show>
    </section>
  );
}
