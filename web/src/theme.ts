import { createSignal } from "solid-js";

export type Theme = "auto" | "dark" | "light";
const KEY = "tb_theme";

function initial(): Theme {
  try {
    const v = localStorage.getItem(KEY);
    if (v === "dark" || v === "light") return v;
  } catch { /* no storage */ }
  return "auto";
}

const [theme, setThemeSignal] = createSignal<Theme>(initial());
export { theme };

export function applyTheme(th: Theme = theme()): void {
  if (th === "auto") document.documentElement.removeAttribute("data-theme");
  else document.documentElement.dataset.theme = th;
}

export function cycleTheme(): void {
  const order: Theme[] = ["auto", "dark", "light"];
  const next = order[(order.indexOf(theme()) + 1) % order.length];
  try { localStorage.setItem(KEY, next); } catch { /* no storage */ }
  setThemeSignal(next);
  applyTheme(next);
}
