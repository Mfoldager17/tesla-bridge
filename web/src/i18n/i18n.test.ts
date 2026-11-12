import { describe, expect, it } from "vitest";
import en from "./en.json";
import da from "./da.json";

const placeholders = (s: string) => (s.match(/\{\d\}/g) ?? []).sort().join();

describe("translations", () => {
  it("have the same keys in English and Danish", () => {
    expect(Object.keys(da).sort()).toEqual(Object.keys(en).sort());
  });
  it("use the same {0} placeholders in both", () => {
    for (const k of Object.keys(en) as (keyof typeof en)[])
      expect(placeholders(da[k]), k).toBe(placeholders(en[k]));
  });
});
