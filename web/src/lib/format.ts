import { locale, t } from "../i18n";

export function ago(iso: string | null | undefined, now = Date.now()): string {
  if (!iso) return "–";
  const s = Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000));
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
