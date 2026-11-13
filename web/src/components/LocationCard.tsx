import { createEffect, createMemo, createSignal, onCleanup, onMount, Show } from "solid-js";
import { t, locale, type Key } from "../i18n";
import { val, vin } from "../state/selection";
import { field } from "../state/live";
import { settings } from "../state/settings";
import { createAddress } from "../state/address";
import { isDark } from "../theme";
import { ago, nf } from "../lib/format";
import { compassIndex, point } from "../lib/geo";
import type { CarMap } from "../lib/map";

/** Where the car is: a small map, the street address, and where it's heading. */
export function LocationCard() {
  const pos = createMemo(() => point(val("location")), undefined, {
    equals: (a, b) => a?.lat === b?.lat && a?.lon === b?.lon,
  });
  const destPos = createMemo(() => point(val("destination_location")));
  const enabled = () => settings()?.map_enabled !== false;
  const { address, failed } = createAddress(pos, enabled);

  const gear = () => val<string>("gear");
  const speed = () => val<number>("speed");
  const moving = () => !!gear() && gear() !== "P" && (speed() ?? 0) > 0;
  const heading = () => val<number>("heading");
  const dest = () => val<string>("destination");
  const status = () => {
    if (!gear() || gear() === "P") return t("parked");
    const h = heading();
    const dir = h == null ? "" : ` ${t("loc.toward")} ${t(`loc.dir${compassIndex(h)}` as Key)}`;
    return `${t("loc.driving")} ${nf(speed() ?? 0)} km/h${dir}`;
  };
  const arrive = () => {
    const m = val<number>("minutes_to_arrival");
    return m == null ? null : t("arrive", new Date(Date.now() + m * 60000).toLocaleTimeString(locale(), { hour: "2-digit", minute: "2-digit" }));
  };
  const coords = () => (pos() ? `${pos()!.lat.toFixed(5)}, ${pos()!.lon.toFixed(5)}` : "");
  const updated = () => field(vin(), "location")?.updated_at;

  // The map itself – only built when there is a position and the map is on.
  let el!: HTMLDivElement;
  const [map, setMap] = createSignal<CarMap>();
  const [mapFailed, setMapFailed] = createSignal(false);
  onMount(() => {
    let disposed = false;
    let loading = false;
    let ro: ResizeObserver | undefined;
    createEffect(() => {
      if (!enabled() || !pos() || map() || mapFailed() || loading) return;
      loading = true;
      import("../lib/map")
        .then((m) => {
          loading = false;
          if (disposed || !enabled()) return;
          const s = settings();
          const cm = m.mount(el, {
            dark: isDark(),
            tileUrl: s?.map_tile_url, attribution: s?.map_tile_attribution,
            labels: { car: t("loc.car"), destination: t("f.destination"), recenter: t("loc.recenter") },
          });
          setMap(cm);
          ro = new ResizeObserver(() => cm.resize());
          ro.observe(el);
        })
        .catch(() => { loading = false; setMapFailed(true); });
    });
    createEffect(() => {
      const m = map(), p = pos();
      if (m && p) m.update({ car: { ...p, heading: heading(), moving: moving() }, destination: dest() ? destPos() : null });
    });
    createEffect(() => map()?.setDark(isDark()));
    // Turned off under Settings: take the map away.
    createEffect(() => { if (!enabled() && map()) { ro?.disconnect(); map()!.dispose(); setMap(undefined); } });
    onCleanup(() => { disposed = true; ro?.disconnect(); map()?.dispose(); });
  });

  const showMap = () => enabled() && !!pos() && !mapFailed();
  return (
    <div class="panel loc" classList={{ "no-map": !showMap() }}>
      <div class="loc-map" ref={el} hidden={!showMap()} />
      <div class="loc-info">
        <h2 class="loc-line1">
          <Show when={pos()} fallback={t("loc.none")}>
            {enabled() && address() ? address()!.line1 : coords()}
          </Show>
        </h2>
        <Show when={pos()}>
          <p class="loc-line2">
            <Show when={enabled() && address()} fallback={
              <span class="muted">{!enabled() ? t("loc.mapOff") : failed() ? t("loc.noAddress") : t("loc.finding")}</span>}>
              {[address()!.place, address()!.line2].filter(Boolean).join(" · ")}
            </Show>
          </p>
        </Show>
        <dl class="loc-facts">
          <div><dt>{t("loc.status")}</dt><dd>{status()}</dd></div>
          <div>
            <dt>{t("f.destination")}</dt>
            <dd>{dest() ?? <span class="muted">{t("nonav")}</span>}<Show when={dest() && arrive()}><span class="sub">{arrive()}</span></Show></dd>
          </div>
          <Show when={pos()}>
            <div><dt>{t("loc.updated")}</dt><dd>{ago(updated())}<Show when={enabled() && address()}><span class="sub num">{coords()}</span></Show></dd></div>
          </Show>
        </dl>
        <Show when={pos()}>
          <p class="loc-links">
            <a href={`https://www.google.com/maps/search/?api=1&query=${pos()!.lat},${pos()!.lon}`} target="_blank" rel="noopener">Google Maps</a>
            <a href={`https://www.openstreetmap.org/?mlat=${pos()!.lat}&mlon=${pos()!.lon}#map=17/${pos()!.lat}/${pos()!.lon}`} target="_blank" rel="noopener">OpenStreetMap</a>
          </p>
        </Show>
      </div>
    </div>
  );
}
