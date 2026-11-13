import { describe, expect, it } from "vitest";
import { compassIndex, distanceM, point } from "./geo";

describe("geo", () => {
  it("measures short distances in metres", () => {
    const d = distanceM({ lat: 56.1185, lon: 10.1235 }, { lat: 56.1187, lon: 10.1235 });
    expect(d).toBeGreaterThan(21);
    expect(d).toBeLessThan(23);
  });
  it("turns a heading into one of eight compass points", () => {
    expect([0, 44, 90, 180, 270, 338, 359, -90].map(compassIndex)).toEqual([0, 1, 2, 4, 6, 0, 0, 6]);
  });
  it("reads the bridge's location value", () => {
    expect(point({ latitude: 56.1, longitude: 10.2 })).toEqual({ lat: 56.1, lon: 10.2 });
    expect(point(null)).toBeNull();
    expect(point({ latitude: "56" })).toBeNull();
  });
});
