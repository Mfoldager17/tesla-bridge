import { createMemo, createSignal, For, Show } from "solid-js";
import { createStore, reconcile } from "solid-js/store";
import { t, lang, type Key } from "../../i18n";
import { nf, usd } from "../../lib/format";
import type { FieldSettingInfo, Settings } from "../../state/settings";

export const INTERVALS = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 21600, 86400];

export function dur(sec: number): string {
  if (sec < 60) return `${sec} s`;
  if (sec < 3600) return `${sec / 60} min`;
  const h = sec / 3600;
  return `${nf(h, h % 1 ? 1 : 0)} ${lang() === "da" ? "t" : "h"}`;
}

export type Draft = Record<string, { enabled: boolean; interval: number }>;

/** The fields and their intervals, edited as a draft until saved. */
export function createDraft(settings: () => Settings | null) {
  const [draft, setDraft] = createStore<Draft>({});
  const reset = () => {
    const s = settings();
    if (s) setDraft(reconcile(Object.fromEntries(s.fields.map((f) => [f.field, { enabled: f.enabled, interval: f.interval }]))));
  };
  const changed = createMemo(() =>
    (settings()?.fields ?? []).filter((f) => draft[f.field] && (draft[f.field].enabled !== f.enabled || draft[f.field].interval !== f.interval)));

  /** Monthly signal cost now and with the draft, from this month's real signal counts. */
  const estimate = createMemo(() => {
    const s = settings();
    if (!s) return null;
    const start = +new Date(s.month_start), end = +new Date(s.month_end), now = +new Date(s.now);
    if ((now - start) / 86400000 < 0.25) return null;
    const factor = (end - start) / (now - start);
    let current = 0, next = 0, any = false;
    for (const f of s.fields) {
      const monthly = f.signals_month * factor;
      if (f.signals_month > 0) any = true;
      current += monthly;
      const d = draft[f.field];
      if (!d?.enabled) continue;
      if (!f.enabled) { next += f.continuous ? ((86400 * 30) / d.interval) * 0.05 : 0; continue; }   // re-enabled: rough guess
      next += f.continuous ? monthly * (f.interval / d.interval) : monthly;
    }
    return any ? { current: current / s.signals_per_dollar, next: next / s.signals_per_dollar } : null;
  });

  const preset = (kind: "economy" | "default" | "live") => {
    for (const f of settings()?.fields ?? []) {
      if (!f.continuous) continue;
      const def = f.default_interval;
      const target = kind === "economy" ? Math.min(3600, Math.max(30, def * 5)) : kind === "live" ? Math.max(1, Math.round(def / 10)) : def;
      setDraft(f.field, { enabled: true, interval: INTERVALS.reduce((a, c) => (Math.abs(c - target) < Math.abs(a - target) ? c : a), INTERVALS[0]) });
    }
  };

  return { draft, setDraft, reset, changed, estimate, preset };
}

export function FieldGroups(props: { settings: Settings; d: ReturnType<typeof createDraft> }) {
  const [filter, setFilter] = createSignal("");
  const [onlyCost, setOnlyCost] = createSignal(false);
  const [open, setOpen] = createSignal(new Set(["driving", "charging"]));
  const name = (f: FieldSettingInfo) => (lang() === "da" ? f.da : f.en);
  const { draft, setDraft } = props.d;

  const groups = createMemo(() => props.settings.groups.map((g) => {
    const q = filter().toLowerCase();
    const rows = props.settings.fields.filter((f) => f.group === g && (!onlyCost() || f.continuous)
      && (!q || name(f).toLowerCase().includes(q) || f.field.toLowerCase().includes(q)));
    return { g, rows };
  }).filter((x) => x.rows.length));

  const isChanged = (f: FieldSettingInfo) => draft[f.field] && (draft[f.field].enabled !== f.enabled || draft[f.field].interval !== f.interval);

  return (
    <>
      <div class="toolbar">
        <input type="text" placeholder={t("settings.filter")} value={filter()} onInput={(e) => setFilter(e.currentTarget.value)} />
        <span class="spacer" />
        <label class="small muted"><input type="checkbox" checked={onlyCost()} onChange={(e) => setOnlyCost(e.currentTarget.checked)} /> {t("settings.onlyCost")}</label>
      </div>
      <Show when={groups().length} fallback={<p class="empty">–</p>}>
        <div class="frow head"><span /><span>{t("settings.colInterval")}</span><span>{t("settings.colSignals")}</span><span>{t("settings.colOn")}</span></div>
        <For each={groups()}>{({ g, rows }) => {
          const costly = rows.filter((f) => f.continuous).length;
          const off = () => rows.filter((f) => !draft[f.field]?.enabled).length;
          const nChanged = () => rows.filter(isChanged).length;
          return (
            <details class="fgroup" open={open().has(g) || !!filter()}
              onToggle={(e) => { const s = new Set(open()); if (e.currentTarget.open) s.add(g); else s.delete(g); setOpen(s); }}>
              <summary>
                <b>{t(`g.${g}` as Key)}</b>
                <span>{t("settings.groupInfo", rows.length, costly)}{off() ? ` · ${t("settings.groupOff", off())}` : ""}</span>
                <Show when={nChanged()}><span class="chg">{t("settings.groupChanged", nChanged())}</span></Show>
              </summary>
              <div>
                <For each={rows}>{(f) => {
                  const d = () => draft[f.field] ?? { enabled: f.enabled, interval: f.interval };
                  const opts = () => [...new Set([...INTERVALS, d().interval, f.default_interval])].sort((a, b) => a - b);
                  return (
                    <div class="frow" classList={{ off: !d().enabled }}>
                      <div class="name">
                        {name(f)}
                        <Show when={f.continuous}><span class="tag">{t("settings.costly")}</span></Show>
                        <Show when={isChanged(f)}><span class="chg">{t("settings.changed")}</span></Show>
                        <small>{f.field}</small>
                      </div>
                      <select aria-label={name(f)} disabled={!d().enabled} value={d().interval}
                        onChange={(e) => setDraft(f.field, { ...d(), interval: Number(e.currentTarget.value) })}>
                        <For each={opts()}>{(s) => <option value={s}>{dur(s)}{s === f.default_interval ? " ★" : ""}</option>}</For>
                      </select>
                      <div class="sig num" title={t("settings.signals", nf(f.signals_month))}>{f.signals_month ? nf(f.signals_month) : "–"}</div>
                      <button type="button" class="switch" role="switch" aria-checked={d().enabled} aria-label={name(f)}
                        onClick={() => setDraft(f.field, { ...d(), enabled: !d().enabled })} />
                    </div>
                  );
                }}</For>
              </div>
            </details>
          );
        }}</For>
      </Show>
    </>
  );
}

export function Estimate(props: { d: ReturnType<typeof createDraft>; dirty: boolean }) {
  return (
    <div class="est">
      <Show when={props.d.estimate()} fallback={<span class="muted small">{t("settings.estNone")}</span>}>{(e) => (
        <>
          <span innerHTML={t("settings.est", usd(e().next))} />
          <Show when={props.dirty}> <span class="muted small">({t("settings.estFrom", usd(e().current))})</span></Show>
        </>
      )}</Show>
    </div>
  );
}
