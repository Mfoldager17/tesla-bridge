// The small map on the Car page (Leaflet). Loaded only when a position is shown, like the 3D car.
import L from "leaflet";
import "leaflet/dist/leaflet.css";

export interface MapPoint { lat: number; lon: number }
export interface MapState {
  car: MapPoint & { heading?: number | null; moving: boolean };
  destination?: MapPoint | null;
}
export interface MapOptions {
  dark: boolean;
  /** Your own tiles (Bridge__Map__TileUrl); otherwise OpenStreetMap's. */
  tileUrl?: string | null;
  attribution?: string | null;
  labels: { car: string; destination: string; recenter: string };
}
export interface CarMap {
  update(state: MapState): void;
  setDark(dark: boolean): void;
  /** After the card changed size. */
  resize(): void;
  dispose(): void;
}

const OSM = '&copy; <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">OpenStreetMap</a>';

// OpenStreetMap's own map (free, no key; light use like this is what its tile policy allows). The dark theme
// gets it through a colour filter (.map-dark in app.css), so only one set of tiles is ever loaded.
function tiles(o: MapOptions): L.TileLayer {
  return o.tileUrl
    ? L.tileLayer(o.tileUrl, { maxZoom: 19, attribution: o.attribution ?? OSM })
    : L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", { maxZoom: 19, attribution: OSM });
}

// A round marker; the arrow points the way the car faces.
const carIcon = (label: string) => L.divIcon({
  className: "map-car",
  iconSize: [34, 34],
  iconAnchor: [17, 17],
  html: `<span class="map-car-dot" role="img" aria-label="${label}"><svg viewBox="0 0 24 24" aria-hidden="true">` +
    `<path d="M12 3.5 18.5 19 12 15.6 5.5 19Z"/></svg></span>`,
});
const destIcon = (label: string) => L.divIcon({
  className: "map-dest",
  iconSize: [26, 26],
  iconAnchor: [13, 24],
  html: `<span role="img" aria-label="${label}"><svg viewBox="0 0 24 24" aria-hidden="true">` +
    `<path d="M12 2a7 7 0 0 0-7 7c0 5.2 7 13 7 13s7-7.8 7-13a7 7 0 0 0-7-7Zm0 9.5a2.5 2.5 0 1 1 0-5 2.5 2.5 0 0 1 0 5Z"/></svg></span>`,
});

export function mount(el: HTMLElement, opts: MapOptions): CarMap {
  const map = L.map(el, {
    zoomControl: true,
    scrollWheelZoom: false,          // the page scrolls past the map; zoom with the buttons or pinch
    dragging: !L.Browser.mobile,     // one finger scrolls the page on a phone, two fingers move the map
    attributionControl: true,
  });
  map.attributionControl.setPrefix(false);
  tiles(opts).addTo(map);
  el.classList.toggle("map-dark", opts.dark && !opts.tileUrl);
  let car: L.Marker | null = null;
  let dest: L.Marker | null = null;
  let line: L.Polyline | null = null;
  let following = true;
  let lastDest = "";

  // When you move the map yourself it stops following the car; a button brings it back.
  const Recenter = L.Control.extend({
    onAdd() {
      const b = L.DomUtil.create("button", "map-recenter");
      b.type = "button";
      b.title = b.ariaLabel = opts.labels.recenter;
      b.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="3.2"/><path d="M12 2v3M12 19v3M2 12h3M19 12h3"/><circle cx="12" cy="12" r="7" fill="none"/></svg>';
      L.DomEvent.disableClickPropagation(b);
      L.DomEvent.on(b, "click", () => { following = true; b.hidden = true; show(true); });
      b.hidden = true;
      return b;
    },
  });
  const recenter = new Recenter({ position: "topright" }).addTo(map);
  map.on("dragstart", () => { following = false; (recenter.getContainer() as HTMLButtonElement).hidden = false; });

  let state: MapState | null = null;
  function show(animate: boolean) {
    if (!state) return;
    const c = L.latLng(state.car.lat, state.car.lon);
    if (state.destination) {
      map.fitBounds(L.latLngBounds([c, [state.destination.lat, state.destination.lon]]), { padding: [36, 36], maxZoom: 16, animate });
    } else {
      map.setView(c, map.getZoom() > 0 && animate ? Math.max(map.getZoom(), 14) : 16, { animate });
    }
  }

  return {
    update(s) {
      const first = !state;
      state = s;
      const c: L.LatLngExpression = [s.car.lat, s.car.lon];
      if (!car) car = L.marker(c, { icon: carIcon(opts.labels.car), keyboard: false, zIndexOffset: 1000 }).addTo(map);
      else car.setLatLng(c);

      if (s.destination) {
        const d: L.LatLngExpression = [s.destination.lat, s.destination.lon];
        if (!dest) dest = L.marker(d, { icon: destIcon(opts.labels.destination), keyboard: false }).addTo(map);
        else dest.setLatLng(d);
        if (!line) line = L.polyline([c, d], { className: "map-line", interactive: false }).addTo(map);
        else line.setLatLngs([c, d]);
      } else {
        dest?.remove(); dest = null;
        line?.remove(); line = null;
      }

      // A new route frames both; otherwise the map follows the car (unless you moved it).
      const destKey = s.destination ? `${s.destination.lat.toFixed(4)},${s.destination.lon.toFixed(4)}` : "";
      if (destKey !== lastDest) {
        lastDest = destKey; following = true;
        (recenter.getContainer() as HTMLButtonElement).hidden = true;
        show(!first);
      } else if (first) show(false);
      else if (following && !s.destination) map.panTo(c, { animate: true });

      // Markers only get their element once the map has a view (above), so style the car last.
      const arrow = car.getElement()?.querySelector("svg");
      if (arrow) {
        arrow.style.transform = s.car.heading == null ? "" : `rotate(${s.car.heading}deg)`;
        arrow.classList.toggle("no-heading", s.car.heading == null);
      }
      car.getElement()?.classList.toggle("moving", s.car.moving);
    },
    setDark(dark) { el.classList.toggle("map-dark", dark && !opts.tileUrl); },
    resize() { map.invalidateSize(); },
    dispose() { map.remove(); },
  };
}
