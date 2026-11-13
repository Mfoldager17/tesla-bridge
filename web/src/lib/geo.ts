/** Distance in metres between two positions (haversine). */
export function distanceM(a: { lat: number; lon: number }, b: { lat: number; lon: number }): number {
  const r = (d: number) => (d * Math.PI) / 180;
  const dLat = r(b.lat - a.lat), dLon = r(b.lon - a.lon);
  const h = Math.sin(dLat / 2) ** 2 + Math.cos(r(a.lat)) * Math.cos(r(b.lat)) * Math.sin(dLon / 2) ** 2;
  return 2 * 6_371_000 * Math.asin(Math.min(1, Math.sqrt(h)));
}

/** 0–360° → one of eight compass points, as index 0 (north) … 7 (north-west). */
export const compassIndex = (heading: number) => Math.round((((heading % 360) + 360) % 360) / 45) % 8;

/** The car's position as the bridge keeps it ({latitude, longitude}) → {lat, lon}, or null. */
export function point(v: unknown): { lat: number; lon: number } | null {
  if (!v || typeof v !== "object") return null;
  const o = v as { latitude?: unknown; longitude?: unknown };
  return typeof o.latitude === "number" && typeof o.longitude === "number" ? { lat: o.latitude, lon: o.longitude } : null;
}
