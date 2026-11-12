import { createRoot, createSignal } from "solid-js";
import { lang, locale, t } from "../i18n";

/** Ticks every 5 s so "2 min ago" keeps up without new data. */
const [now, setNow] = createRoot(() => createSignal(Date.now()));
export { now };
if (typeof window !== "undefined") setInterval(() => setNow(Date.now()), 5000);

export function ago(iso: string | null | undefined, at: number = now()): string {
  if (!iso) return "–";
  const s = Math.max(0, Math.round((at - new Date(iso).getTime()) / 1000));
  if (s < 60) return t("ago.s", s);
  if (s < 3600) return t("ago.m", Math.floor(s / 60));
  if (s < 86400) return t("ago.h", Math.floor(s / 3600));
  return t("ago.d", Math.floor(s / 86400));
}

export const nf = (n: number, digits = 0) =>
  Number(n).toLocaleString(locale(), { maximumFractionDigits: digits, minimumFractionDigits: digits });

export const usd = (v: number) => "$" + (v > 0 && v < 0.01 ? v.toFixed(4) : nf(v, 2));

export const clock = (iso: string) =>
  new Date(iso).toLocaleTimeString(locale(), { hour: "2-digit", minute: "2-digit", second: "2-digit" });

/** 1.25 → "1 t 15 min" / "1 h 15 min" */
export function hm(hours: number | null | undefined): string {
  if (hours == null) return "–";
  const m = Math.round(hours * 60);
  return m >= 60 ? `${Math.floor(m / 60)} ${lang() === "da" ? "t" : "h"} ${m % 60} min` : `${m} min`;
}

export const esc = (s: unknown) =>
  String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]!);
