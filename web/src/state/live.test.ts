import { describe, expect, it } from "vitest";
import { applyEvent, carsFrom, MAX_LOG, type LiveState } from "./live";
import type { LiveEvent } from "../api/types";

const empty = (): LiveState => ({ info: null, cars: carsFrom([{ vin: "vin1", name: "Model Y", fields: {} }]), commands: [], raw: [] });

describe("applyEvent", () => {
  it("sets, updates and removes fields by VIN, case-insensitive", () => {
    const s = empty();
    applyEvent(s, { type: "field", vin: "VIN1", at: "t", data: { key: "battery_level", value: 64, updated_at: "u", source: "stream" } });
    expect(s.cars.VIN1.fields.battery_level).toEqual({ value: 64, updated_at: "u", source: "stream" });
    applyEvent(s, { type: "field", vin: "vin1", at: "t2", data: { key: "battery_level", value: 65 } });
    expect(s.cars.VIN1.fields.battery_level).toEqual({ value: 65, updated_at: "t2", source: "stream" });
    applyEvent(s, { type: "field", vin: "VIN1", at: "t3", data: { key: "battery_level" } });
    expect(s.cars.VIN1.fields.battery_level).toBeUndefined();
  });

  it("keeps a bounded command log, newest first", () => {
    const s = empty();
    for (let i = 0; i < MAX_LOG + 5; i++)
      applyEvent(s, { type: "command", vin: "VIN1", at: "t", data: { command: `C${i}`, status: "succeeded", transport: "ble", error: null, note: null } } as LiveEvent);
    expect(s.commands).toHaveLength(MAX_LOG);
    expect(s.commands[0].command).toBe(`C${MAX_LOG + 4}`);
    expect(s.commands[0].vin).toBe("VIN1");
  });

  it("puts everything else in the raw log", () => {
    const s = empty();
    applyEvent(s, { type: "telemetry", vin: "VIN1", at: "t", data: { BatteryLevel: 64 } });
    expect(s.raw).toHaveLength(1);
  });
});
