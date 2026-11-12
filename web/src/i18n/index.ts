import { createSignal } from "solid-js";
import en from "./en.json";
import da from "./da.json";

export type Key = keyof typeof en;
export type Lang = "en" | "da";

const dict: Record<Lang, Record<string, string>> = { en, da };
const LANG_KEY = "tb_lang";

function initial(): Lang {
  try {
    const saved = localStorage.getItem(LANG_KEY);
    if (saved === "en" || saved === "da") return saved;
  } catch { /* no storage */ }
  return navigator.language.toLowerCase().startsWith("da") ? "da" : "en";
}

const [lang, setLangSignal] = createSignal<Lang>(initial());
export { lang };

export function setLang(l: Lang): void {
  try { localStorage.setItem(LANG_KEY, l); } catch { /* no storage */ }
  document.documentElement.lang = l;
  setLangSignal(l);
}

/** t("ver.update", "1.2.0") → "Version 1.2.0 is out". Reactive: re-runs when the language changes. */
export function t(key: Key, ...args: (string | number)[]): string {
  const text = dict[lang()][key] ?? dict.en[key] ?? key;
  return text.replace(/\{(\d)\}/g, (_, i: string) => String(args[Number(i)] ?? ""));
}

export const locale = () => (lang() === "da" ? "da-DK" : "en-GB");
export { dict as dictionaries };
