import { describe, expect, it } from "vitest";
import {
  applyFilter,
  bySection,
  changedFromDefault,
  displayValue,
  isTunable,
  kindOf,
  matchesQuery,
  maxAutoStep,
  riskBadge,
  sameValue,
  statusGroups,
  statusValue,
  valueHistory,
} from "./settings";
import type { Setting } from "../types";

const s = (key: string, extra: Partial<Setting> = {}): Setting => ({
  key,
  name: key,
  description: "Something useful.",
  category: "Battery",
  value: "1",
  type: "number",
  risk: "Low",
  entityId: `input_number.predbat_${key}`,
  min: 0,
  max: 2,
  step: 0.01,
  options: [],
  autoAllowed: false,
  autoMinimum: null,
  autoMaximum: null,
  autoMaxStep: 0.1,
  autoCooldownHours: 72,
  editable: true,
  documentation: "",
  kind: "tunable",
  section: "Battery",
  ...extra,
});

describe("settings", () => {
  it("classifies Predbat's own controls even from an older server", () => {
    expect(kindOf({ key: "update" })).toBe("software");
    expect(kindOf({ key: "manual_charge" })).toBe("override");
    expect(kindOf({ key: "mode" })).toBe("control");
    expect(kindOf({ key: "debug_enable" })).toBe("debug");
    expect(kindOf({ key: "load_scaling" })).toBe("tunable");
    expect(isTunable({ key: "active", kind: "tunable" })).toBe(false);
  });

  it("notes only catalogued Medium / High settings, quietly rather than as an alarm", () => {
    expect(riskBadge(s("a", { risk: "Low" }))).toBeNull();
    expect(riskBadge(s("a", { risk: "Medium" }))).toEqual({ label: "Change with care", tone: "neutral" });
    expect(riskBadge(s("a", { risk: "High" }))).toEqual({ label: "Always asks you", tone: "neutral" });
    expect(riskBadge(s("a", { risk: "High", description: "", documentationAnchor: null }))).toBeNull();
    expect(riskBadge(s("update", { risk: "High", kind: "software" }))).toBeNull();
  });

  it("compares values as numbers where it can", () => {
    expect(sameValue("1.0", "1")).toBe(true);
    expect(sameValue("on", "On")).toBe(true);
    expect(sameValue("0.95", "1")).toBe(false);
    expect(changedFromDefault(s("a", { value: "1.0", default: "1" }))).toBe(false);
    expect(changedFromDefault(s("a", { value: "0.9", default: "1" }))).toBe(true);
    expect(changedFromDefault(s("a", { value: "0.9", default: null }))).toBe(false);
  });

  it("reads values the way people say them", () => {
    expect(displayValue({ type: "boolean", unit: "" }, "on")).toBe("On");
    expect(displayValue({ type: "number", unit: "%" }, "10")).toBe("10%");
    expect(displayValue({ type: "number", unit: "kW" }, "3.6")).toBe("3.6 kW");
    expect(displayValue({ type: "number", unit: "" }, "")).toBe("Not set");
    expect(displayValue({ type: "select", unit: "" }, "07:00:00")).toBe("07:00");
  });

  it("shows Predbat's version as the number, with the release title as its detail", () => {
    const update = { key: "update", type: "select", unit: "" };
    expect(statusValue(update, "v9.3.5 Bug fixes cloud inverters & Misc")).toEqual({
      value: "9.3.5",
      detail: "Bug fixes cloud inverters & Misc",
    });
    expect(statusValue(update, "main")).toEqual({ value: "main", detail: null });
    expect(statusValue({ key: "auto_update", type: "boolean", unit: "" }, "off")).toEqual({
      value: "Off",
      detail: null,
    });
  });

  it("filters to commonly tuned, changed, allowed or all tunables, and hides Predbat's controls", () => {
    const list = [
      s("a", { commonlyTuned: true }),
      s("b", { default: "0.5" }),
      s("c", { autoAllowed: true }),
      s("update", { kind: "software" }),
      s("manual_soc", { kind: "override" }),
    ];
    expect(applyFilter(list, "common").map((x) => x.key)).toEqual(["a"]);
    expect(applyFilter(list, "changed").map((x) => x.key)).toEqual(["b"]);
    expect(applyFilter(list, "ai").map((x) => x.key)).toEqual(["c"]);
    expect(applyFilter(list, "all").map((x) => x.key)).toEqual(["a", "b", "c"]);
    expect(statusGroups(list).map((g) => [g.label, g.settings.map((x) => x.key)])).toEqual([
      ["Predbat software", ["update"]],
      ["Manual overrides", ["manual_soc"]],
    ]);
  });

  it("groups by section in the catalogue's order", () => {
    const groups = bySection([
      s("x", { section: "Export" }),
      s("y", { section: "Battery" }),
      s("z", { section: "Mystery" }),
    ]);
    expect(groups.map((g) => g.section)).toEqual(["Battery", "Export", "Other"]);
  });

  it("searches names, keys, entity ids and descriptions", () => {
    const x = s("load_scaling", { name: "House load scaling", description: "Multiplies the load forecast." });
    expect(matchesQuery(x, "house")).toBe(true);
    expect(matchesQuery(x, "predbat_load")).toBe(true);
    expect(matchesQuery(x, "forecast")).toBe(true);
    expect(matchesQuery(x, "solar")).toBe(false);
  });

  it("scales the automatic step limit with the range", () => {
    expect(maxAutoStep({ min: 0, max: 2, step: 0.01 })).toBeCloseTo(0.2);
    expect(maxAutoStep({ min: 1, max: 5, step: 1 })).toBe(1);
    expect(maxAutoStep({ min: null, max: null, step: 0.01 })).toBe(0.1);
  });

  it("keeps only the points where a value changed", () => {
    const points = valueHistory("a", [
      { at: "1", values: { a: "1.0" } },
      { at: "2", values: { a: "1" } },
      { at: "3", values: { a: "1.1" } },
      { at: "4", values: {} },
    ]);
    expect(points).toEqual([
      { at: "1", value: "1.0" },
      { at: "3", value: "1.1" },
    ]);
  });
});
