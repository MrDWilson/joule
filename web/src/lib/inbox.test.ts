import { describe, expect, it } from "vitest";
import { bellItems, isUnread } from "./inbox";
import { eventsValue, logStatus, quietParts } from "./pushApi";
import type { InboxItem } from "../types";

const now = Date.parse("2026-10-07T09:00:00Z");
const item = (id: string, over: Partial<InboxItem> = {}): InboxItem => ({
  id,
  key: `proposal:${id}`,
  event: "needs_you",
  label: "Suggestion",
  title: `Item ${id}`,
  detail: null,
  link: "#/insights/suggestions",
  tone: "accent",
  at: "2026-10-07T08:00:00Z",
  readAt: null,
  dismissedAt: null,
  resolvedAt: null,
  open: true,
  ...over,
});

describe("the bell", () => {
  it("counts only open, unread, undismissed items", () => {
    const { items, unread } = bellItems(
      [
        item("a"),
        item("b", { readAt: "2026-10-07T08:30:00Z" }),
        item("c", { open: false, resolvedAt: "2026-10-07T08:40:00Z" }),
        item("d", { dismissedAt: "2026-10-07T08:50:00Z" }),
      ],
      undefined,
      now,
    );
    expect(items.map((i) => i.id)).toEqual(["a", "b", "c"]);
    expect(unread).toBe(1);
    expect(isUnread(items[2])).toBe(false);
  });

  it("drops handled items after two days and lists newest first", () => {
    const { items } = bellItems(
      [
        item("old", { open: false, at: "2026-10-01T08:00:00Z", resolvedAt: "2026-10-04T08:00:00Z" }),
        item("older", { at: "2026-10-06T08:00:00Z" }),
        item("newer", { at: "2026-10-07T08:59:00Z" }),
      ],
      undefined,
      now,
    );
    expect(items.map((i) => i.id)).toEqual(["newer", "older"]);
  });

  it("applies read, dismiss, mark all read and clear all at once, before the server answers", () => {
    const inbox = [item("a"), item("b"), item("c")];
    expect(bellItems(inbox, { read: ["a"], dismissed: [] }, now).unread).toBe(2);
    expect(bellItems(inbox, { read: [], dismissed: ["b"] }, now).items.map((i) => i.id)).toEqual(["a", "c"]);
    expect(bellItems(inbox, { read: [], dismissed: [], allRead: true }, now)).toMatchObject({ unread: 0 });
    expect(bellItems(inbox, { read: [], dismissed: [], allDismissed: true }, now).items).toEqual([]);
  });

  it("copes with a server that has no inbox yet", () => {
    expect(bellItems(undefined, undefined, now)).toEqual({ items: [], unread: 0 });
  });

  it("shows at most thirty", () => {
    const many = Array.from({ length: 40 }, (_, n) => item(String(n)));
    expect(bellItems(many, undefined, now).items).toHaveLength(30);
  });
});

describe("phone notification settings", () => {
  it("never saves an empty event list (that would mean the defaults)", () => {
    expect(eventsValue([])).toBe("none");
    expect(eventsValue(["needs_you", "offline"])).toBe("needs_you,offline");
  });
  it("splits quiet hours, with the usual night as the starting point", () => {
    expect(quietParts("23:00-06:30")).toEqual(["23:00", "06:30"]);
    expect(quietParts(null)).toEqual(["22:00", "07:00"]);
  });
  it("names each delivery state in words", () => {
    expect(logStatus({ status: "sent" })).toEqual({ label: "Sent", tone: "success" });
    expect(logStatus({ status: "retrying" }).label).toBe("Trying again");
    expect(logStatus({ status: "held" }).label).toBe("Waiting");
    expect(logStatus({ status: "skipped" }).label).toBe("Not sent");
    expect(logStatus({ status: "failed" }).tone).toBe("danger");
  });
});
