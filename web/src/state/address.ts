import { createEffect, createSignal, onCleanup } from "solid-js";
import { api } from "../api/client";
import { lang } from "../i18n";
import { distanceM } from "../lib/geo";

/** GET /api/geocode */
export interface Address {
  line1: string;
  line2: string | null;
  place: string | null;
  country: string | null;
  country_code: string | null;
  full: string;
}

/** Ask the bridge again only after the car moved this far … */
export const MOVE_M = 25;
/** … and at most this often while it drives. */
export const EVERY_MS = 15_000;

/**
 * The street address of a position, looked up by the bridge (which caches it). Follows the position, but only
 * asks again when the car has moved MOVE_M and not more than once per EVERY_MS – so a drive costs a few lookups
 * a minute at most.
 */
export function createAddress(pos: () => { lat: number; lon: number } | null, enabled: () => boolean) {
  const [address, setAddress] = createSignal<Address | null>(null);
  const [failed, setFailed] = createSignal(false);
  let asked: { lat: number; lon: number; lang: string } | null = null;
  let askedAt = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;

  async function lookup(p: { lat: number; lon: number }, l: string) {
    asked = { ...p, lang: l };
    askedAt = Date.now();
    try {
      const res = await api(`/api/geocode?lat=${p.lat.toFixed(6)}&lon=${p.lon.toFixed(6)}&lang=${l}`);
      if (!res.ok) throw new Error(String(res.status));
      setAddress(((await res.json()) as { address: Address | null }).address);
      setFailed(false);
    } catch {
      setFailed(true);   // keep the last address; the coordinates are shown meanwhile
    }
  }

  createEffect(() => {
    const p = pos(), l = lang(), on = enabled();
    clearTimeout(timer);
    if (!p || !on) { if (!on) setAddress(null); return; }
    const sameLang = asked?.lang === l;
    if (asked && sameLang && distanceM(asked, p) < MOVE_M) return;
    const wait = sameLang ? askedAt + EVERY_MS - Date.now() : 0;
    if (wait > 0) timer = setTimeout(() => void lookup(pos() ?? p, lang()), wait);
    else void lookup(p, l);
  });
  onCleanup(() => clearTimeout(timer));

  return { address, failed };
}
