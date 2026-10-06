/**
 * Predbat's plan as a short list of windows: what the battery does, when, at what price, and where its level goes.
 *
 * Consecutive half-hours with the same action are one window, whatever the price does (Agile export prices change every
 * half-hour) and whatever the target does along the way (a charge to 80% then 100% is one charge to 100%). Two folds keep
 * the list honest but calm:
 *   - a short export between two charges in the cheap period is a deliberate trade (sell high, buy back cheap), so it is
 *     folded into the charge window and described ("Sells 2.7 kWh at 13.9p and re-buys at 6.67p: +7.2p/kWh");
 *   - a "power your home" pause of 30 minutes or less inside a run of the same action becomes a note on that window.
 * Pure functions, unit-tested in windows.test.ts.
 */
import { slotAction, type PlanActionKey, type PlanActionTone } from "../../lib/planActions";
import { clock, dayLabel, localDate } from "../../lib/time";
import { kwh, number, pence, percent } from "../../lib/format";
import type { TimelineSlot } from "../charts/timeline";

const MIN = 60000;
export type ActionView = ReturnType<typeof slotAction>;

/** A plan slot with the server's detail fields (all optional, so older plans still read). */
export type PlanSlotLike = TimelineSlot & { socChangeKwh?: number | null };

export interface Range {
  min: number;
  max: number;
}
export type WindowTag = "free" | "saving" | "estimated";
export const windowTagLabels: Record<WindowTag, string> = {
  free: "Free session",
  saving: "Saving session",
  estimated: "≈ estimated price",
};

export interface Trades {
  /** Exports folded into the window. */
  count: number;
  /** When the first of them starts, ms. */
  at: number;
  /** Battery energy sold, kWh (from Predbat's planned battery change), when known. */
  kwh: number | null;
  sell: Range;
  /** The cheapest import price in the window: what the energy is bought back at. */
  buy: number;
  /** Average sell price minus the buy-back price, pence per kWh. */
  spread: number;
}

export interface PlanWindow {
  id: string;
  start: number;
  end: number;
  phase: "past" | "current" | "future";
  action: ActionView;
  key: PlanActionKey;
  tone: PlanActionTone;
  /** The plain label, e.g. "Charge from the grid", or "Export window – battery at reserve". */
  label: string;
  /** Badge text with the target: "Charge → 100%", "Export → 35%", "Power home". */
  short: string;
  /** The battery level the window aims for (the last slot's target), when Predbat set one. */
  target: number | null;
  /** Which price matters: import for charging and home use, export for exporting. */
  rate: "import" | "export";
  price: Range | null;
  importPrice: Range | null;
  exportPrice: Range | null;
  /** Planned battery level at the start and end of the window. */
  soc: { start: number | null; end: number | null };
  /** Battery energy moved, kWh: positive charging, negative discharging (Predbat's soc_change), when known. */
  kwh: number | null;
  /** Predbat's planned cost for the window, £ (negative means it earns). */
  cost: number | null;
  tags: WindowTag[];
  /** An export window that starts and stays at the reserve: nothing will actually be exported. */
  atReserve: boolean;
  trades: Trades | null;
  /** Short "power your home" breaks folded into the window. */
  pauses: { start: number; end: number }[];
  slots: PlanSlotLike[];
}

const startOf = (s: PlanSlotLike) => Date.parse(s.time);
const endOf = (s: PlanSlotLike) => startOf(s) + (s.durationMinutes || 30) * MIN;
const finite = (v: number | null | undefined): v is number => typeof v === "number" && Number.isFinite(v);

const SHORT: Record<string, string> = {
  demand: "Power home",
  charge: "Charge",
  "freeze-charge": "Hold",
  "hold-charge": "Hold at target",
  "no-charge": "No charge needed",
  export: "Export",
  "freeze-export": "Solar export",
  "hold-export": "Export paused",
  "charge-export": "Charge, then export",
  "hold-for-car": "Hold for car",
  "hold-for-iboost": "Hold for iBoost",
  unknown: "Other",
};
/** "Charge → 100%", "Export → 35%", "Hold"; the short form of an action for badges and the ribbon. */
export function shortAction(action: ActionView, target?: number | null) {
  const base = SHORT[action.id] ?? SHORT[action.key] ?? action.label;
  const t = target ?? action.target;
  return finite(t) && (action.key === "charge" || action.key === "export" || action.key === "charge-export")
    ? `${base} → ${Math.round(t)}%`
    : base;
}

const range = (values: (number | null | undefined)[]): Range | null => {
  const v = values.filter(finite);
  return v.length ? { min: Math.min(...v), max: Math.max(...v) } : null;
};
/** "6.67p" for one price; a range to 1 dp, "12.0–13.0p", so both ends read alike. */
export function priceRange(r: Range | null) {
  if (!r) return "";
  return Math.abs(r.max - r.min) < 0.05 ? pence(r.min, { unit: "p" }) : `${number(r.min, 1)}–${number(r.max, 1)}p`;
}

const groupId = (a: ActionView) => `${a.key}|${a.id}|${a.key === "unknown" ? a.code : ""}`;
const chargeish = (w: Draft) => w.action.key === "charge" || w.action.key === "charge-export";
/** The half-hours of a charge-then-export run, whose export part counts as a trade once it joins a charge window. */
const exportPart = (d: Draft) => (d.action.key === "charge-export" ? d.slots : []);

interface Draft {
  action: ActionView;
  slots: PlanSlotLike[];
  pauses: { start: number; end: number }[];
  /** Export slots folded in as trades. */
  traded: PlanSlotLike[];
}

/**
 * The plan's windows from `slots` (sorted or not). `from`/`to` keep only windows that overlap that span (a window
 * that started earlier is kept whole). `reserve` is Predbat's minimum battery level (%), for the "at reserve" check.
 */
export function planWindows(
  slots: PlanSlotLike[],
  {
    now,
    from = -Infinity,
    to = Infinity,
    reserve,
  }: { now: number; from?: number; to?: number; reserve?: number | null },
): PlanWindow[] {
  const sorted = slots.filter((s) => Number.isFinite(startOf(s))).sort((a, b) => startOf(a) - startOf(b));
  // 1. Runs of the same action.
  let drafts: Draft[] = [];
  for (const slot of sorted) {
    const action = slotAction({ ...slot, action: slot.action ?? "" });
    const last = drafts.at(-1);
    if (last && groupId(last.action) === groupId(action) && Math.abs(endOf(last.slots.at(-1)!) - startOf(slot)) < MIN)
      last.slots.push(slot);
    else drafts.push({ action, slots: [slot], pauses: [], traded: [] });
  }
  const minutes = (d: Draft) => (endOf(d.slots.at(-1)!) - startOf(d.slots[0])) / MIN;
  const touching = (a: Draft, b: Draft) => Math.abs(endOf(a.slots.at(-1)!) - startOf(b.slots[0])) < MIN;
  const family = (a: Draft, b: Draft) => groupId(a.action) === groupId(b.action) || (chargeish(a) && chargeish(b));
  const join = (parts: Draft[], extra: Partial<Draft> = {}): Draft => {
    const lead = parts.find((p) => p.action.key === "charge") ?? parts[0];
    return {
      action: lead.action,
      slots: parts.flatMap((p) => p.slots),
      pauses: [...parts.flatMap((p) => p.pauses), ...(extra.pauses ?? [])],
      // A charge-then-export run inside a charging window: its export part counts as a trade.
      traded: [
        ...parts.flatMap((p) => p.traded),
        ...(lead.action.key === "charge" ? parts.flatMap(exportPart) : []),
        ...(extra.traded ?? []),
      ],
    };
  };
  /**
   * A run of exports and short home-power breaks (90 minutes at most, at least one export) between two charges, all
   * still at the cheap import price: a sell-and-rebuy inside one cheap charging window. Returns the index of the
   * closing charge, or null.
   */
  const cheapTrade = (list: Draft[], i: number): number | null => {
    if (!chargeish(list[i])) return null;
    let total = 0,
      exports = 0;
    for (let j = i + 1; j < list.length; j++) {
      const d = list[j];
      if (!touching(list[j - 1], d)) return null;
      if (chargeish(d)) {
        if (!exports || j === i + 1) return null;
        const cheap = Math.min(...[...list[i].slots, ...d.slots].map((s) => s.importRate).filter(finite));
        const inside = list.slice(i + 1, j).flatMap((m) => m.slots);
        return inside.every((s) => finite(s.importRate) && s.importRate <= cheap + 0.5) ? j : null;
      }
      if (d.action.key === "export") exports++;
      else if (d.action.id !== "demand") return null;
      total += minutes(d);
      if (total > 90) return null;
    }
    return null;
  };
  // 2. Folds, applied until nothing changes:
  //    - an export of an hour or less between two charges is a trade inside one charging window;
  //    - touching charge and charge-then-export runs are one charging window;
  //    - a "power your home" break of 30 minutes or less between two runs of the same kind is a pause.
  for (let changed = true; changed;) {
    changed = false;
    for (let i = 0; i < drafts.length - 1 && !changed; i++) {
      const [a, b, c] = [drafts[i], drafts[i + 1], drafts[i + 2]];
      const run = cheapTrade(drafts, i);
      if (run) {
        const middle = drafts.slice(i + 1, run);
        const merged = join([drafts[i], drafts[run]], {
          traded: middle.filter((d) => d.action.key === "export").flatMap((d) => d.slots),
          pauses: middle
            .filter((d) => d.action.key !== "export")
            .map((d) => ({ start: startOf(d.slots[0]), end: endOf(d.slots.at(-1)!) })),
        });
        merged.slots = drafts.slice(i, run + 1).flatMap((d) => d.slots);
        drafts.splice(i, run - i + 1, merged);
        changed = true;
      } else if (
        c &&
        chargeish(a) &&
        chargeish(c) &&
        b.action.key === "export" &&
        minutes(b) <= 60 &&
        touching(a, b) &&
        touching(b, c)
      ) {
        drafts.splice(i, 3, join([a, c], { traded: b.slots }));
        // Keep the slots in time order.
        drafts[i].slots = [...a.slots, ...b.slots, ...c.slots];
        changed = true;
      } else if (chargeish(a) && chargeish(b) && touching(a, b)) {
        drafts.splice(i, 2, join([a, b]));
        changed = true;
      } else if (
        c &&
        b.action.id === "demand" &&
        a.action.key !== "demand" &&
        family(a, c) &&
        minutes(b) <= 30 &&
        touching(a, b) &&
        touching(b, c)
      ) {
        drafts.splice(i, 3, join([a, c], { pauses: [{ start: startOf(b.slots[0]), end: endOf(b.slots.at(-1)!) }] }));
        drafts[i].slots = [...a.slots, ...b.slots, ...c.slots];
        changed = true;
      }
    }
  }
  drafts = drafts.filter((d) => endOf(d.slots.at(-1)!) > from && startOf(d.slots[0]) < to);
  // An older plan has no end level per slot: the next slot's start level is the same moment.
  const levelAt = (ms: number) => sorted.find((s) => Math.abs(startOf(s) - ms) < MIN)?.socForecast ?? null;
  return drafts.map((d) => toWindow(d, now, reserve, levelAt));
}

function toWindow(
  d: Draft,
  now: number,
  reserve: number | null | undefined,
  levelAt: (ms: number) => number | null | undefined,
): PlanWindow {
  const first = d.slots[0],
    last = d.slots.at(-1)!;
  const start = startOf(first),
    end = endOf(last);
  const action = d.action;
  d.traded = [...new Set(d.traded)];
  d.pauses.sort((a, b) => a.start - b.start);
  const own = d.slots.filter(
    (s) => !d.traded.includes(s) && !d.pauses.some((p) => startOf(s) >= p.start && startOf(s) < p.end),
  );
  // The server's target for the last slot that has one, else a target written into Predbat's code ("Chrg 70%").
  const lastTarget =
    [...own].reverse().find((s) => finite(s.targetPercent))?.targetPercent ??
    (finite(action.target) ? action.target : null);
  const socStart = finite(first.socForecast) ? first.socForecast : null;
  const nextLevel = levelAt(end);
  const socEnd = finite(last.socForecastEnd) ? last.socForecastEnd : finite(nextLevel) ? nextLevel : null;
  const changes = d.slots.map((s) => s.socChangeKwh);
  const kwh = changes.some(finite) ? changes.reduce<number>((t, v) => t + (finite(v) ? v : 0), 0) : null;
  const costs = d.slots.map((s) => s.cost);
  const cost = costs.some(finite) ? costs.reduce<number>((t, v) => t + (finite(v) ? v : 0), 0) : null;
  const importPrice = range(own.map((s) => s.importRate));
  const exportPrice = range(own.map((s) => s.exportRate));
  const rate: "import" | "export" = action.rate === "export" ? "export" : "import";
  const tags: WindowTag[] = [];
  if (own.some((s) => finite(s.importRate) && s.importRate <= 0)) tags.push("free");
  if (own.some((s) => s.importRateType === "saving" || s.exportRateType === "saving")) tags.push("saving");
  if (own.some((s) => s.rateEstimated || ["offset", "future", "copy"].includes(s.importRateType ?? "")))
    tags.push("estimated");
  const floor = finite(reserve) ? reserve : null;
  const atReserve =
    action.key === "export" &&
    socStart != null &&
    socEnd != null &&
    Math.abs(socStart - socEnd) < 1 &&
    (floor != null ? socStart <= floor + 1 : socStart <= 10);
  let trades: Trades | null = null;
  if (d.traded.length) {
    const sell = range(d.traded.map((s) => s.exportRate))!;
    const buy = range(own.map((s) => s.importRate))?.min ?? sell.min;
    const avgSell = d.traded.reduce((t, s) => t + (s.exportRate ?? 0), 0) / d.traded.length;
    const sold = d.traded.map((s) => s.socChangeKwh).filter(finite);
    // Count separate exports, not half-hours.
    const count = d.traded.filter((s, i) => i === 0 || Math.abs(endOf(d.traded[i - 1]) - startOf(s)) >= MIN).length;
    trades = {
      count,
      at: startOf(d.traded[0]),
      kwh: sold.length ? -sold.filter((v) => v < 0).reduce((t, v) => t + v, 0) : null,
      sell,
      buy,
      spread: avgSell - buy,
    };
  }
  return {
    id: `${start}`,
    start,
    end,
    phase: end <= now ? "past" : start <= now ? "current" : "future",
    action,
    key: action.key,
    tone: atReserve ? "neutral" : action.tone,
    label: atReserve ? "Export window – battery at reserve" : action.label,
    short: atReserve ? "Export (at reserve)" : shortAction(action, lastTarget),
    target: lastTarget,
    rate,
    price: rate === "export" ? exportPrice : importPrice,
    importPrice,
    exportPrice,
    soc: { start: socStart, end: socEnd },
    kwh,
    cost,
    tags,
    atReserve,
    trades,
    pauses: d.pauses,
    slots: d.slots,
  };
}

/**
 * "Also sells 5.1 kWh overnight at 13.9–14.4p and buys it back at 6.67p (about 7.4p/kWh profit)". "Overnight" only when
 * the first sale is between 21:00 and 07:00 local time (with `timeZone`).
 */
export function tradeNote(t: Trades, timeZone?: string) {
  const hour = Number(clock(t.at, { timeZone }).slice(0, 2));
  const night = Number.isFinite(t.at) && (hour >= 21 || hour < 7) ? " overnight" : "";
  const what =
    t.kwh != null && t.kwh > 0.05
      ? `Also sells ${kwh(t.kwh)}${night}`
      : t.count === 1
        ? `Also sells in one short export${night}`
        : `Also sells in ${t.count} short exports${night}`;
  const margin =
    Math.abs(t.spread) < 0.05
      ? "about break-even"
      : `about ${number(Math.abs(t.spread), 1)}p/kWh ${t.spread > 0 ? "profit" : "loss"}`;
  return `${what} at ${priceRange(t.sell)} and buys it back at ${pence(t.buy, { unit: "p" })} (${margin})`;
}

/** Predbat's planned cost still to come in a window: elapsed half-hours drop out, the one in progress counts pro rata. */
export function costFromNow(slots: PlanSlotLike[], now: number) {
  let total = 0;
  for (const s of slots) {
    if (!finite(s.cost)) continue;
    const start = startOf(s),
      end = endOf(s);
    if (end <= now) continue;
    total += start >= now ? s.cost : (s.cost * (end - now)) / (end - start);
  }
  return total;
}

/**
 * The cost a window shows, worked out once so every place agrees (the phone card, the table cell, the notes): for the
 * window in progress only the part still to come (`partial`), otherwise Predbat's planned cost for the whole window.
 */
export function shownCost(w: Pick<PlanWindow, "phase" | "start" | "cost" | "slots">, from: number) {
  const partial = w.phase === "current" && from > w.start && w.cost != null;
  return { cost: partial ? costFromNow(w.slots, from) : w.cost, partial };
}

/** The small hours run until 05:00: a moment before then belongs to the night before; from 05:00 it is the morning. */
const NIGHT_ENDS = 5;

/**
 * The local "day word" for a moment, relative to now: "Today", "Tonight" (this evening from 18:00 until 05:00 tomorrow),
 * "Tomorrow", "Tomorrow night", "Yesterday", "Last night", otherwise "Wed 7 Oct". nights: false uses day words only.
 * 05:30 tomorrow is "Tomorrow", not "Tonight": the cheap night is over by then and the house is waking up.
 */
export function dayWord(at: number, now: number, timeZone?: string, { nights = true }: { nights?: boolean } = {}) {
  const hour = (ms: number) => Number(clock(ms, { timeZone }).slice(0, 2));
  const date = (ms: number) => localDate(ms, { timeZone });
  const dayIndex = (ms: number) => Math.round(Date.parse(`${date(ms)}T12:00:00Z`) / 86400000);
  // A night belongs to the evening it starts on.
  const nightOf = (ms: number) => (hour(ms) >= 18 ? dayIndex(ms) : hour(ms) < NIGHT_ENDS ? dayIndex(ms) - 1 : null);
  const today = dayIndex(now);
  const night = nights ? nightOf(at) : null;
  const nowNight = nightOf(now) ?? today;
  if (night != null) {
    if (night === nowNight) return "Tonight";
    if (night === nowNight + 1) return "Tomorrow night";
    if (night === nowNight - 1) return "Last night";
  }
  const day = dayIndex(at);
  if (day === today) return "Today";
  if (day === today + 1) return "Tomorrow";
  if (day === today - 1) return "Yesterday";
  return dayLabel(at, { timeZone, now });
}

/**
 * "Tonight 23:30–01:00", "Today 10:30–18:30", "Now until 18:30", "Tomorrow 07:00–08:30". A window only counts as a night
 * one when it is over by 09:00 the next morning.
 */
export function windowWhen(start: number, end: number, now: number, timeZone?: string) {
  const t = (ms: number) => clock(ms, { timeZone });
  if (start <= now && end > now) return `Now until ${t(end)}`;
  const hours = (end - start) / 3600000;
  const endHour = Number(t(end).slice(0, 2)) + Number(t(end).slice(3, 5)) / 60;
  const startHour = Number(t(start).slice(0, 2));
  const overnight = hours <= 15 && (startHour >= 18 ? endHour <= 9 || endHour >= startHour : endHour <= 9);
  return `${dayWord(start, now, timeZone, { nights: overnight })} ${t(start)}–${t(end)}`;
}

/** "Charge to 100%", "Export down to 35%", "Power your home": a window's title in words. */
export function windowTitle(w: Pick<PlanWindow, "atReserve" | "label" | "target" | "key">) {
  if (w.atReserve) return w.label;
  if (w.target != null && w.key === "charge") return `Charge to ${percent(w.target)}`;
  if (w.target != null && w.key === "export") return `Export down to ${percent(w.target)}`;
  return w.label;
}

/** "18 h", or "2 days" from 47.5 hours on. */
export function hoursText(ms: number) {
  const h = Math.round(ms / 3600000);
  return h >= 48 && h % 24 === 0 ? `${h / 24} days` : `${h} h`;
}

const RELATIVE = /^(Now|Today|Tonight|Tomorrow|Yesterday|Last night)/;
/**
 * The plan between two instants in one sentence, built from the same windows the page lists: "Next 24 h: Predbat plans
 * to charge to 100% tonight 00:00–05:30 at 6.67p and export down to 19% tomorrow 17:30–19:00 at 32.6–34.4p, plus 1
 * more." The two longest charging or exporting windows are named, in time order.
 */
export function planSentence(windows: PlanWindow[], from: number, to: number, now: number, timeZone?: string) {
  // Only the part of the range still ahead is "next"; a range that is all (or nearly all) past has nothing to say.
  const ahead = to - Math.max(from, now);
  if (!(ahead >= 3600000)) return "";
  const span = `Next ${hoursText(ahead)}`;
  from = Math.max(from, now);
  const active = windows.filter((w) => w.end > from && w.start < to && w.tone !== "neutral" && !w.atReserve);
  if (!active.length) return `${span}: no charging or exporting planned.`;
  const main = [...active]
    .sort((a, b) => b.end - b.start - (a.end - a.start) || a.start - b.start)
    .slice(0, 2)
    .sort((a, b) => a.start - b.start);
  const named = main.map((w) => {
    const title = windowTitle(w);
    const when = windowWhen(w.start, w.end, now, timeZone).replace(RELATIVE, (m) => m.toLowerCase());
    const price = w.price ? ` at ${priceRange(w.price)}` : "";
    return `${title.charAt(0).toLowerCase()}${title.slice(1)} ${when}${price}`;
  });
  const more = active.length - main.length;
  return `${span}: Predbat plans to ${named.join(" and ")}${more > 0 ? `, plus ${more} more` : ""}.`;
}
