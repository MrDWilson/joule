import { describe, expect, it } from "vitest";
import { editOutcome, jouleEditInPlace } from "./configEdits";
import { buildTimeline } from "./changes";
import type { ConfigFileEdit, SettingEvent } from "../types";

const edit = (check: ConfigFileEdit["check"], checkNote: string | null = null): ConfigFileEdit => ({
  at: "2026-10-07T13:00:00Z",
  snapshotVersion: "a".repeat(32),
  appliedVersion: "b".repeat(32),
  placement: "Adds 2 lines under pred_bat, after import_today",
  keys: ["pred_bat › export_today"],
  check,
  checkNote,
});

describe("apps.yaml edits Joule makes", () => {
  it("says how Predbat took the edit, in one sentence per outcome", () => {
    expect(editOutcome(edit("checking"), "apps.yaml", "today 14:00")).toEqual({
      text: "Joule made this edit today 14:00. Watching Predbat reload apps.yaml…",
      tone: "info",
    });
    expect(
      editOutcome(edit("confirmed", "Predbat reloaded apps.yaml and logged no errors."), "apps.yaml", "14:00"),
    ).toEqual({
      text: "Joule made this edit 14:00. Predbat reloaded apps.yaml and logged no errors.",
      tone: "success",
    });
    expect(editOutcome(edit("confirmed"), "apps.yaml", "14:00").text).toBe(
      "Joule made this edit 14:00. Predbat reloaded apps.yaml.",
    );
    const undone = editOutcome(
      edit("rolled_back", "Predbat reported a problem. Joule put the previous apps.yaml back."),
      "apps.yaml",
      "14:00",
    );
    expect(undone.tone).toBe("warn");
    expect(undone.text).toBe(
      "Joule made this edit 14:00, then undid it. Predbat reported a problem. Joule put the previous apps.yaml back.",
    );
    expect(editOutcome(edit("restored"), "apps.yaml", "14:00").text).toBe(
      "Joule made this edit 14:00. You put the previous apps.yaml back.",
    );
    expect(editOutcome(edit("attention", "Check it in Files."), "apps.yaml", "14:00").tone).toBe("warn");
  });

  it("offers Restore only while Joule's edit is still in the file", () => {
    expect(jouleEditInPlace({ edit: edit("checking") })).toBe(true);
    expect(jouleEditInPlace({ edit: edit("confirmed") })).toBe(true);
    expect(jouleEditInPlace({ edit: edit("unconfirmed") })).toBe(true);
    expect(jouleEditInPlace({ edit: edit("rolled_back") })).toBe(false);
    expect(jouleEditInPlace({ edit: edit("restored") })).toBe(false);
    expect(jouleEditInPlace({ edit: edit("attention") })).toBe(false);
    expect(jouleEditInPlace({ edit: null })).toBe(false);
  });

  it("shows file edits in the Changes timeline as Joule's, with the settings they touched", () => {
    const events: SettingEvent[] = [
      {
        id: "e1",
        at: "2026-10-07T13:00:00Z",
        kind: "file",
        key: "file:apps.yaml",
        name: "apps.yaml",
        before: "",
        after: "pred_bat › export_today",
        title: "Joule edited apps.yaml: Add export_today",
        revertedAt: null,
      },
    ];
    const [entry] = buildTimeline({ revisions: [], settings: [], experiments: [], settingEvents: events });
    expect(entry).toMatchObject({
      icon: "file",
      title: "Joule edited apps.yaml: Add export_today",
      detail: "Settings: pred_bat › export_today",
      via: "Through Joule",
      undoable: false,
    });
  });
});
