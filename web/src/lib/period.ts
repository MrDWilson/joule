import { shiftLocalDays } from "./comparison";

function zonedParts(at: Date, timeZone: string) {
  const formatter = new Intl.DateTimeFormat("en-GB", {
    timeZone,
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hourCycle: "h23",
  });
  const p = Object.fromEntries(formatter.formatToParts(at).map((x) => [x.type, x.value]));
  return {
    year: Number(p.year),
    month: Number(p.month),
    day: Number(p.day),
    hour: Number(p.hour),
    minute: Number(p.minute),
    second: Number(p.second),
  };
}
/** UTC instant of the chosen midnight in the configured IANA zone, including DST. */
export function zonedDateMidnight(day: string, timeZone: string): Date {
  const [year, month, date] = day.split("-").map(Number);
  if (!year || !month || !date) throw new Error("Choose valid calendar dates.");
  const nominal = Date.UTC(year, month - 1, date);
  let instant = nominal;
  for (let n = 0; n < 3; n++) {
    const q = zonedParts(new Date(instant), timeZone);
    const wall = Date.UTC(q.year, q.month - 1, q.day, q.hour, q.minute, q.second);
    instant = nominal - (wall - instant);
  }
  return new Date(instant);
}
export function startOfZonedDay(now: Date, timeZone: string): Date {
  const p = zonedParts(now, timeZone);
  return zonedDateMidnight(`${p.year}-${p.month}-${p.day}`, timeZone);
}

// ------------------------------------------------------------------ Energy page periods

/** The Energy page's period choices. */
export type PeriodPreset = "today" | "yesterday" | "7d" | "30d" | "custom";
export const PERIOD_PRESETS: { value: PeriodPreset; label: string }[] = [
  { value: "today", label: "Today" },
  { value: "yesterday", label: "Yesterday" },
  { value: "7d", label: "7 days" },
  { value: "30d", label: "30 days" },
  { value: "custom", label: "Custom" },
];
export const DEFAULT_PRESET: PeriodPreset = "7d";

/** "2026-10-05" plus n calendar days (negative: earlier). */
export function addDays(day: string, n: number) {
  const d = new Date(`${day}T12:00:00Z`);
  d.setUTCDate(d.getUTCDate() + n);
  return d.toISOString().slice(0, 10);
}
/** Whole calendar days from a to b, inclusive. */
export function dayCount(fromDay: string, toDay: string) {
  return Math.round((Date.parse(`${toDay}T12:00:00Z`) - Date.parse(`${fromDay}T12:00:00Z`)) / 86400000) + 1;
}
const isDay = (s: string | null | undefined): s is string => !!s && /^\d{4}-\d{2}-\d{2}$/.test(s);

/** The local calendar date of an instant in a zone ("2026-10-05"). */
export function zonedDayOf(at: Date | number, timeZone: string) {
  return new Intl.DateTimeFormat("en-CA", { timeZone, year: "numeric", month: "2-digit", day: "2-digit" }).format(at);
}

/** The calendar days a preset covers, ending today (Today, 7 days and 30 days include today so far). */
export function presetDays(preset: Exclude<PeriodPreset, "custom">, today: string) {
  switch (preset) {
    case "today":
      return { fromDay: today, toDay: today };
    case "yesterday":
      return { fromDay: addDays(today, -1), toDay: addDays(today, -1) };
    case "7d":
      return { fromDay: addDays(today, -6), toDay: today };
    case "30d":
      return { fromDay: addDays(today, -29), toDay: today };
  }
}

/** The period in the address bar: ?period=today, or ?from=2026-10-01&to=2026-10-04 for custom dates; none means 7 days. */
export function periodFromQuery(query: URLSearchParams): { preset: PeriodPreset; fromDay?: string; toDay?: string } {
  const from = query.get("from"),
    to = query.get("to");
  if (isDay(from) && isDay(to)) return { preset: "custom", fromDay: from, toDay: to };
  const p = query.get("period");
  const known = PERIOD_PRESETS.find((x) => x.value === p && x.value !== "custom");
  return { preset: known ? known.value : DEFAULT_PRESET };
}
export function periodQuery(preset: PeriodPreset, fromDay?: string, toDay?: string) {
  const q = new URLSearchParams();
  if (preset === "custom") {
    if (fromDay && toDay) {
      q.set("from", fromDay);
      q.set("to", toDay);
    }
  } else if (preset !== DEFAULT_PRESET) q.set("period", preset);
  return q;
}

function shortDate(day: string) {
  return new Date(`${day}T12:00:00Z`)
    .toLocaleDateString("en-GB", { day: "numeric", month: "short", timeZone: "UTC" })
    .replace("Sept", "Sep");
}
/** One plain message for a custom range that can't be shown, or null when it can. */
export function customRangeError(fromDay: string, toDay: string, today: string): string | null {
  if (!isDay(fromDay) || !isDay(toDay)) return "Choose a start and an end date.";
  if (fromDay > today) return `That's in the future: today is ${shortDate(today)}.`;
  if (fromDay > toDay) return "The start date is after the end date.";
  if (dayCount(fromDay, toDay) > 366) return "Choose a year or less.";
  return null;
}

export interface EnergyPeriod {
  preset: PeriodPreset;
  /** The days asked for. */
  fromDay: string;
  toDay: string;
  /** The window fetched: from the first day shown, cut at now. */
  from: Date;
  to: Date;
  /** Local midnight after toDay. */
  end: Date;
  /** True when the window runs up to now. */
  includesToday: boolean;
  /** Days shown (after moving the start to the day records began). */
  days: number;
  /** Set when the start was moved forward to the day records began. */
  clampedFrom?: string;
  /** True when every day asked for is before records began. */
  beforeRecords: boolean;
}

/**
 * The window for a choice of days: local midnight of the first day to local midnight after the last, cut at now, and starting
 * no earlier than the day records began (so a month view never lists weeks of "no readings").
 */
export function resolvePeriod(
  preset: PeriodPreset,
  fromDay: string,
  toDay: string,
  timeZone: string,
  now: Date,
  firstObservationAt?: string | null,
  lastCollection?: string | null,
): EnergyPeriod {
  const firstDay = firstObservationAt ? zonedDayOf(new Date(firstObservationAt), timeZone) : null;
  const today = zonedDayOf(now, timeZone);
  const lastDay = toDay > today ? today : toDay;
  const end = zonedDateMidnight(addDays(lastDay, 1), timeZone);
  const beforeRecords = !!firstDay && lastDay < firstDay;
  const startDay = firstDay && fromDay < firstDay && !beforeRecords ? firstDay : fromDay;
  const from = zonedDateMidnight(startDay, timeZone);
  const cut = Math.min(end.getTime(), now.getTime());
  // A period that runs up to now ends at the last reading: "up to 19:13" never claims fresher figures than the meters gave.
  const last = lastCollection ? Date.parse(lastCollection) : NaN;
  const to = new Date(Number.isFinite(last) && last > from.getTime() && last < cut ? last : cut);
  return {
    preset,
    fromDay,
    toDay: lastDay,
    from,
    to,
    end,
    includesToday: cut < end.getTime(),
    days: Math.max(1, dayCount(startDay, lastDay)),
    clampedFrom: startDay !== fromDay ? startDay : undefined,
    beforeRecords,
  };
}

/**
 * Why the period is shorter than asked, or null: "Only 4 days recorded so far (since Fri 2 Oct)" when the start was moved to
 * the day records began.
 */
export function clippedNote(period: Pick<EnergyPeriod, "clampedFrom" | "days" | "from">, timeZone: string) {
  if (!period.clampedFrom) return null;
  const since = new Intl.DateTimeFormat("en-GB", { timeZone, weekday: "short", day: "numeric", month: "short" })
    .format(period.from)
    .replace(",", "")
    .replace("Sept", "Sep");
  return `Only ${period.days === 1 ? "1 day" : `${period.days} days`} recorded so far (since ${since})`;
}

export interface PreviousPeriod {
  from: Date;
  to: Date;
  /** How a change reads after "vs": "this time yesterday", "the day before", "the previous 7 days". */
  label: string;
}

/**
 * The same number of days just before, cut at the same clock time when the period runs up to now, so a part day is never
 * compared with a whole one. Null when that earlier stretch starts before records began: a half-recorded comparison only
 * misleads, so the page says once that there's nothing to compare with.
 */
export function previousOf(
  period: EnergyPeriod,
  timeZone: string,
  firstObservationAt?: string | null,
): PreviousPeriod | null {
  if (period.beforeRecords || period.clampedFrom) return null;
  const n = period.days;
  const from = zonedDateMidnight(addDays(period.fromDay, -n), timeZone);
  const to = period.includesToday ? new Date(shiftLocalDays(period.to.getTime(), -n, timeZone)) : period.from;
  if (!(to > from)) return null;
  if (firstObservationAt && from.getTime() < Date.parse(firstObservationAt)) return null;
  const label = n === 1 ? (period.includesToday ? "this time yesterday" : "the day before") : `the previous ${n} days`;
  return { from, to, label };
}
