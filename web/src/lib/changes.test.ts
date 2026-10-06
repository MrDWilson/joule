import { describe, expect, it } from "vitest";
import { buildTimeline, groupByDay, predbatVersion } from "./changes";
import type { Experiment, Revision, Setting, SettingEvent } from "../types";

const setting = (key: string, extra: Partial<Setting> = {}): Setting => ({
  key,
  name: key,
  description: "",
  category: "Battery",
  value: "0",
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
  ...extra,
});
const revision = (
  id: number,
  at: string,
  source: string,
  reason: string,
  changes: Revision["changes"],
  extra: Partial<Revision> = {},
): Revision => ({
  id,
  at,
  source,
  reason,
  changes,
  values: {},
  reverts: null,
  fileVersionBefore: null,
  fileVersionAfter: null,
  ...extra,
});

// The live history on 5 Oct 2026: one first copy, then only Predbat's own controls changed (calculating flag, manual
// overrides, two version updates). Those arrive as events; the legacy revisions must not show twice or offer Undo.
const live = {
  settings: [
    setting("active", { name: "Predbat is calculating", kind: "control", type: "boolean", value: "off" }),
    setting("update", {
      name: "Predbat version",
      kind: "software",
      type: "select",
      value: "v9.3.5 Bug fixes cloud inverters & Misc",
    }),
    setting("manual_charge", { name: "Manual charge slots", kind: "override", type: "select", value: "off" }),
    setting("load_scaling", { name: "House load scaling", value: "1.0" }),
  ],
  revisions: [
    revision(1, "2026-10-02T09:14:29Z", "Predbat", "First copy of your Predbat settings", []),
    revision(2, "2026-10-03T08:02:01Z", "Predbat", "Changed in Predbat: Predbat is calculating off → on", [
      { key: "active", before: "off", after: "on" },
    ]),
    revision(5, "2026-10-04T14:22:52Z", "Predbat", "Changed in Predbat: Manual charge slots off → +Sun 15:00", [
      { key: "manual_charge", before: "off", after: "+Sun 15:00" },
    ]),
    revision(8, "2026-10-04T20:22:26Z", "Predbat", "Changed in Predbat: Predbat version …", [
      { key: "update", before: "v9.3.4 IOG started-dispatch fix", after: "v9.3.5 Bug fixes cloud inverters & Misc" },
    ]),
  ],
  settingEvents: [
    {
      id: "a",
      at: "2026-10-04T14:22:52Z",
      kind: "override",
      key: "manual_charge",
      name: "Manual charge slots",
      before: "off",
      after: "+Sun 15:00",
      title: "Manual charge slots set for Sun 15:00 (cleared after 20 min)",
      revertedAt: "2026-10-04T14:42:55Z",
    },
    {
      id: "b",
      at: "2026-10-04T20:22:26Z",
      kind: "software",
      key: "update",
      name: "Predbat version",
      before: "v9.3.4",
      after: "v9.3.5",
      title: "Predbat updated to v9.3.5",
      revertedAt: null,
    },
  ] as SettingEvent[],
  experiments: [] as Experiment[],
};

describe("buildTimeline", () => {
  it("shows Predbat's own controls as events, never as undoable versions", () => {
    const t = buildTimeline(live);
    expect(t.map((e) => e.title)).toEqual([
      "Predbat updated to 9.3.5",
      "Manual charge slots set for Sun 15:00 (cleared after 20 min)",
      "First settings snapshot",
    ]);
    expect(t.map((e) => e.icon)).toEqual(["software", "override", "first"]);
    expect(t.filter((e) => e.undoable)).toEqual([]);
    expect(t.filter((e) => e.restorable).map((e) => e.id)).toEqual(["revision-1"]);
  });

  it("writes tunable changes as sentences with friendly names and before → after", () => {
    const settings = [setting("load_scaling", { name: "House load scaling", value: "1.00" })];
    const t = buildTimeline({
      settings,
      revisions: [
        revision(1, "2026-10-01T09:00:00Z", "Initial snapshot", "Demo configuration imported", []),
        revision(2, "2026-10-02T09:00:00Z", "You", "You changed House load scaling 1.08 → 1.00", [
          { key: "load_scaling", before: "1.08", after: "1.00" },
        ]),
      ],
      experiments: [
        {
          id: "x",
          title: "You changed House load scaling 1.08 → 1.00",
          revisionId: 2,
          status: "Running",
        } as Experiment,
      ],
    });
    expect(t[0]).toMatchObject({
      icon: "you",
      title: "You changed House load scaling 1.08 → 1.00 via Joule",
      undoable: true,
      restorable: true,
    });
    expect(t[0].lines).toEqual([{ key: "load_scaling", name: "House load scaling", before: "1.08", after: "1.00" }]);
    expect(t[0].trials.map((e) => e.id)).toEqual(["x"]);
  });

  it("offers Undo only while the change is still in place", () => {
    const settings = [setting("load_scaling", { name: "House load scaling", value: "1.05" })];
    const t = buildTimeline({
      settings,
      experiments: [],
      revisions: [
        revision(1, "2026-10-01T09:00:00Z", "Predbat", "First copy of your Predbat settings", []),
        revision(2, "2026-10-02T09:00:00Z", "Approved by you", "Bring the evening load closer", [
          { key: "load_scaling", before: "1.08", after: "1.00" },
        ]),
        revision(
          3,
          "2026-10-02T10:00:00Z",
          "You",
          "Undid version 2",
          [{ key: "load_scaling", before: "1.00", after: "1.08" }],
          { reverts: 2 },
        ),
        revision(4, "2026-10-03T10:00:00Z", "Predbat", "Changed in Predbat", [
          { key: "load_scaling", before: "1.08", after: "1.05" },
        ]),
      ],
    });
    const byId = Object.fromEntries(t.map((e) => [e.id, e]));
    expect(byId["revision-2"]).toMatchObject({
      icon: "approved",
      undoable: false,
      undoNote: "Already undone",
      detail: "Bring the evening load closer",
    });
    expect(byId["revision-3"]).toMatchObject({
      icon: "undo",
      title: "You undid a change: House load scaling 1.00 → 1.08",
      undoable: false,
    });
    expect(byId["revision-3"].undoNote).toMatch(/changed again later/);
    expect(byId["revision-4"]).toMatchObject({
      icon: "predbat",
      title: "House load scaling 1.08 → 1.05 (changed in Predbat)",
      undoable: true,
    });
  });

  it("names restores and multi-setting changes", () => {
    const settings = [
      setting("a", { name: "A", value: "2" }),
      setting("b", { name: "B", value: "2" }),
      setting("c", { name: "C", value: "2" }),
    ];
    const t = buildTimeline({
      settings,
      experiments: [],
      revisions: [
        revision(1, "2026-10-01T09:00:00Z", "Predbat", "First", []),
        revision(
          2,
          "2026-10-02T09:00:00Z",
          "You",
          "Restored settings from version 1",
          ["a", "b", "c"].map((key) => ({ key, before: "1", after: "2" })),
        ),
      ],
    });
    expect(t[0].icon).toBe("restore");
    expect(t[0].title).toMatch(/^You restored settings from .+: A and 2 more settings$/);
  });

  it("formats on/off values", () => {
    const t = buildTimeline({
      settings: [setting("combine", { name: "Combine charge slots", type: "boolean", value: "on" })],
      experiments: [],
      revisions: [
        revision(1, "2026-10-01T09:00:00Z", "Predbat", "First", []),
        revision(2, "2026-10-02T09:00:00Z", "Predbat", "x", [{ key: "combine", before: "off", after: "on" }]),
      ],
    });
    expect(t[0].title).toBe("Combine charge slots Off → On (changed in Predbat)");
  });
});

describe("event titles", () => {
  it("reads Predbat's manual target syntax as words", () => {
    const t = buildTimeline({
      settings: [],
      revisions: [],
      experiments: [],
      settingEvents: [
        {
          id: "m",
          at: "2026-10-03T08:07:03Z",
          kind: "override",
          key: "manual_soc",
          name: "Manual battery targets",
          before: "off",
          after: "+Sat 09:00=100.0",
          title: "Manual battery targets set for Sat 09:00=100.0, Sat 09:30=95.5 (cleared after 5 min)",
          revertedAt: null,
        },
      ],
    });
    expect(t[0].title).toBe(
      "Manual battery targets set for Sat 09:00 at 100%, Sat 09:30 at 95.5% (cleared after 5 min)",
    );
  });
});

describe("event wording", () => {
  it("writes values with their unit and versions in one format", () => {
    const t = buildTimeline({
      settings: [setting("best_soc_keep", { name: "Battery reserve", unit: "%", kind: "control" })],
      revisions: [],
      experiments: [],
      settingEvents: [
        {
          id: "r",
          at: "2026-10-03T08:07:03Z",
          kind: "control",
          key: "best_soc_keep",
          name: "Battery reserve",
          before: "0",
          after: "100",
          title: "Battery reserve changed from 0 to 100",
          revertedAt: null,
        },
      ] as SettingEvent[],
    });
    expect(t[0].title).toBe("Battery reserve changed from 0% to 100%");
    expect(predbatVersion("v9.3.5 IOG started-dispatch fix")).toBe("9.3.5");
    expect(predbatVersion("9.3.5")).toBe("9.3.5");
    expect(predbatVersion(null)).toBeNull();
  });
});

describe("groupByDay", () => {
  it("groups newest first under relative day names", () => {
    const days = groupByDay(buildTimeline(live), {
      timeZone: "Europe/London",
      now: Date.parse("2026-10-05T12:00:00Z"),
    });
    expect(days.map((d) => [d.label, d.entries.length])).toEqual([
      ["Yesterday", 2],
      ["Fri 2 Oct", 1],
    ]);
  });
});
