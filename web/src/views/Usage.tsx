import { createResource, createSignal, For, onCleanup, Show } from "solid-js";
import { t, locale, type Key } from "../i18n";
import { getJson } from "../api/client";
import { nf, usd } from "../lib/format";
import { Tabs } from "../components/Tabs";

/** GET /api/usage */
interface Usage {
  month_start: string;
  total_usd: number;
  projected_usd: number | null;
  credit_usd: number;
  categories: Record<string, { name: string; count: number; rate: string; cost_usd: number }>;
  signals_by_field: Record<string, number>;
  commands_by_name: Record<string, number>;
  data_by_name: Record<string, number>;
  ble_by_name?: Record<string, number>;
  ble_saved_usd?: number;
  history: { month: string; total_usd: number }[];
}

type Tab = "signals_by_field" | "commands_by_name" | "data_by_name" | "ble_by_name" | "history";
const TABS: [Tab, Key][] = [["signals_by_field", "usage.byfield"], ["commands_by_name", "usage.commands"], ["data_by_name", "usage.data"],
  ["ble_by_name", "usage.ble"], ["history", "usage.history"]];

export function Usage() {
  const [usage, { refetch }] = createResource(() => getJson<Usage>("/api/usage"));
  const timer = setInterval(refetch, 30000);
  onCleanup(() => clearInterval(timer));
  const [tab, setTab] = createSignal<Tab>("signals_by_field");

  const bleCount = (u: Usage) => Object.values(u.ble_by_name ?? {}).reduce((a, b) => a + b, 0);
  const rows = (u: Usage): [string, string, number][] => {
    if (tab() === "history")
      return u.history.map((h) => [new Date(`${h.month}-01T00:00:00Z`).toLocaleDateString(locale(), { month: "long", year: "numeric", timeZone: "UTC" }),
        usd(h.total_usd), h.total_usd / u.credit_usd]);
    const e = Object.entries(u[tab()] ?? {});
    const max = Math.max(1, ...e.map(([, n]) => n));
    return e.map(([k, n]) => [k, nf(n), n / max]);
  };

  return (
    <section class="view">
      <h1>{t("nav.usage")}</h1>
      <p class="lede">{t("usage.lede")}</p>
      <Show when={usage()}>{(u) => {
        const credit = () => u().credit_usd, p = () => u().projected_usd;
        const pct = () => Math.min(100, (u().total_usd / credit()) * 100);
        const month = () => new Date(u().month_start).toLocaleDateString(locale(), { month: "long", timeZone: "UTC" });
        const catName = (k: string, name: string) => { const key = `cat.${k}` as Key; const s = t(key); return s === key ? name : s; };
        return (
          <>
            <div class="panel">
              <div class="money">
                <div><div class="v num">{usd(u().total_usd)}</div><div class="k">{t("usage.spent", month())}</div></div>
                <div><div class="v num" style={{ color: p() == null ? "" : p()! > credit() ? "var(--danger)" : p()! > credit() * 0.75 ? "var(--warn)" : "" }}>
                  {p() == null ? "–" : usd(p()!)}</div><div class="k">{t("usage.proj")}</div></div>
                <div><div class="v num">{usd(Math.max(0, credit() - u().total_usd))}</div><div class="k">{t("usage.left")}</div></div>
              </div>
              <div class="budget">
                <i style={{ width: `${pct()}%`, background: pct() > 75 ? "var(--warn)" : "var(--charge)" }} />
                <Show when={p() != null}><b style={{ left: `calc(${Math.min(100, (p()! / credit()) * 100)}% - 1px)` }} /></Show>
              </div>
              <p class="muted small">{p() == null ? t("usage.hintNone") : p()! > credit() ? t("usage.hintOver") : t("usage.hintOk", Math.round((p()! / credit()) * 100))}</p>
            </div>
            <div class="panel">
              <div class="tablewrap" style={{ border: "0", "max-height": "none" }}><table>
                <thead><tr><th>{t("th.category")}</th><th style={{ "text-align": "right" }}>{t("th.count")}</th><th>{t("th.rate")}</th><th style={{ "text-align": "right" }}>{t("th.price")}</th></tr></thead>
                <tbody>
                  <For each={Object.entries(u().categories)}>{([k, c]) => (
                    <tr><td>{catName(k, c.name)}</td><td style={{ "text-align": "right" }} class="num">{nf(c.count)}</td><td class="age">{c.rate}</td>
                      <td style={{ "text-align": "right" }} class="num">{usd(c.cost_usd)}</td></tr>
                  )}</For>
                  <tr><td><b>{t("total")}</b></td><td /><td /><td style={{ "text-align": "right" }} class="num"><b>{usd(u().total_usd)}</b></td></tr>
                  <Show when={bleCount(u())}>
                    <tr><td>{t("usage.bleRow")}</td><td style={{ "text-align": "right" }} class="num">{nf(bleCount(u()))}</td><td class="age">$0</td>
                      <td style={{ "text-align": "right", color: "var(--charge)" }} class="num">{t("usage.bleSaved", usd(u().ble_saved_usd ?? 0))}</td></tr>
                  </Show>
                </tbody>
              </table></div>
            </div>
            <div class="panel">
              <Tabs tabs={TABS.map(([id, k]) => [id, t(k)])} value={tab()} onChange={setTab} />
              <div class="tablewrap" style={{ border: "0" }}><table><tbody>
                <For each={rows(u())} fallback={<tr><td class="empty">{t("usage.none")}</td></tr>}>{([k, v, f]) => (
                  <tr><td class="code" style={{ width: "42%" }}>{k}</td><td style={{ width: "16%", "text-align": "right" }} class="num">{v}</td>
                    <td><div class="bar"><i style={{ width: `${Math.min(100, f * 100)}%` }} /></div></td></tr>
                )}</For>
              </tbody></table></div>
              <p class="muted small">{t("usage.note")}</p>
            </div>
          </>
        );
      }}</Show>
    </section>
  );
}
