/**
 * Every date and time Joule shows goes through here, always in the household's time zone (never the browser's, never UTC).
 *
 * Styles:
 *   clock     "10:20"                        (adds "BST"/"GMT" for the hour that happens twice when the clocks go back)
 *   dayTime   "Today 10:20", "Yesterday 23:56", "Tomorrow 06:00", "Sun 4 Oct, 21:22", "Sun 4 Oct 2025, 21:22"
 *   range     "Today 10:20–18:30", "Sun 4 Oct, 23:30 – Mon 5 Oct, 01:00" (en dash, the date written once)
 *   dayLabel  "Sun 5 Oct" (the year only when it isn't this year)
 *   ago       "just now", "4 min ago", "3 h ago", then dayTime
 *
 * App sets the household zone once it is known (setHouseholdTimeZone); every function also takes an explicit zone.
 */

let householdZone: string | undefined;

/** Called by the app shell when the household zone is known (from the telemetry status). */
export function setHouseholdTimeZone(timeZone: string | null | undefined) {
  householdZone = timeZone || undefined;
}
/** The household's IANA zone, or undefined (the browser's) before it is known. */
export function householdTimeZone() {
  return householdZone;
}

type When = string | number | Date;
const toDate = (t: When) => (t instanceof Date ? t : new Date(t));
const valid = (d: Date) => Number.isFinite(d.getTime());

const cache = new Map<string, Intl.DateTimeFormat>();
function fmt(timeZone: string | undefined, options: Intl.DateTimeFormatOptions) {
  const key = `${timeZone ?? ""}|${JSON.stringify(options)}`;
  let f = cache.get(key);
  if (!f) {
    f = new Intl.DateTimeFormat("en-GB", { timeZone, ...options });
    cache.set(key, f);
  }
  return f;
}

interface Wall {
  year: number;
  month: number;
  day: number;
  hour: number;
  minute: number;
  weekday: string;
  monthName: string;
  /** "2026-10-05": the local calendar date. */
  date: string;
  /** "10:20" */
  time: string;
}
function wall(d: Date, timeZone?: string): Wall {
  const parts = Object.fromEntries(
    fmt(timeZone, {
      year: "numeric",
      month: "short",
      day: "numeric",
      weekday: "short",
      hour: "2-digit",
      minute: "2-digit",
      hourCycle: "h23",
    })
      .formatToParts(d)
      .map((p) => [p.type, p.value]),
  );
  const month = Number(
    fmt(timeZone, { month: "numeric" })
      .formatToParts(d)
      .find((p) => p.type === "month")?.value,
  );
  const year = Number(parts.year),
    day = Number(parts.day);
  return {
    year,
    month,
    day,
    hour: Number(parts.hour),
    minute: Number(parts.minute),
    weekday: parts.weekday,
    // en-GB writes September as "Sept"; everywhere else in the app uses three letters.
    monthName: String(parts.month).slice(0, 3),
    date: `${year}-${String(month).padStart(2, "0")}-${String(day).padStart(2, "0")}`,
    time: `${parts.hour}:${parts.minute}`,
  };
}

/** Whole local days from `a` to `b` (calendar dates, so DST days still count as one). */
function dayDifference(a: Wall, b: Wall) {
  return Math.round((Date.UTC(b.year, b.month - 1, b.day) - Date.UTC(a.year, a.month - 1, a.day)) / 86400000);
}

/** The short zone name, e.g. "BST" or "GMT" for Europe/London. */
function zoneName(d: Date, timeZone?: string) {
  return fmt(timeZone, { timeZoneName: "short" })
    .formatToParts(d)
    .find((p) => p.type === "timeZoneName")?.value;
}

/** True when the same wall-clock time happens twice in this zone (the hour repeated when the clocks go back). */
export function isAmbiguousTime(t: When, timeZone = householdZone) {
  const d = toDate(t);
  if (!valid(d)) return false;
  const here = wall(d, timeZone);
  return [-3600000, 3600000].some((shift) => {
    const other = wall(new Date(d.getTime() + shift), timeZone);
    return other.date === here.date && other.time === here.time;
  });
}

export interface TimeOptions {
  timeZone?: string;
  /** "Now" for relative words; defaults to the current time. */
  now?: When;
}

/** "10:20", or "01:30 BST" / "01:30 GMT" in the repeated hour on the day the clocks go back. */
export function clock(t: When, options: TimeOptions = {}) {
  const timeZone = options.timeZone ?? householdZone;
  const d = toDate(t);
  if (!valid(d)) return "";
  const text = wall(d, timeZone).time;
  return isAmbiguousTime(d, timeZone) ? `${text} ${zoneName(d, timeZone)}` : text;
}

/** "Sun 5 Oct", with the year only when it isn't this year: "Sun 5 Oct 2025". */
export function dayLabel(t: When, options: TimeOptions & { relative?: boolean } = {}) {
  const timeZone = options.timeZone ?? householdZone;
  const d = toDate(t);
  if (!valid(d)) return "";
  const w = wall(d, timeZone),
    now = wall(toDate(options.now ?? Date.now()), timeZone);
  if (options.relative) {
    const days = dayDifference(now, w);
    if (days === 0) return "Today";
    if (days === -1) return "Yesterday";
    if (days === 1) return "Tomorrow";
  }
  return `${w.weekday} ${w.day} ${w.monthName}${w.year === now.year ? "" : ` ${w.year}`}`;
}

/** "Today 10:20", "Yesterday 23:56", "Tomorrow 06:00", otherwise "Sun 4 Oct, 21:22" (year only for other years). */
export function dayTime(t: When, options: TimeOptions = {}) {
  const d = toDate(t);
  if (!valid(d)) return "";
  const day = dayLabel(d, { ...options, relative: true });
  const time = clock(d, options);
  return /^(Today|Yesterday|Tomorrow)$/.test(day) ? `${day} ${time}` : `${day}, ${time}`;
}

/**
 * A period with an en dash and the date written once: "Today 10:20–18:30", "Sun 4 Oct, 10:20–18:30", and across days
 * "Today 23:30 – Tomorrow 01:00". An end at exactly midnight reads as "24:00" on the start day.
 */
export function range(a: When, b: When, options: TimeOptions = {}) {
  const timeZone = options.timeZone ?? householdZone;
  const start = toDate(a),
    end = toDate(b);
  if (!valid(start) || !valid(end)) return "";
  const ws = wall(start, timeZone),
    we = wall(end, timeZone);
  const endsAtMidnight = we.time === "00:00" && dayDifference(ws, we) === 1 && end > start;
  if (ws.date === we.date || endsAtMidnight) {
    const head = dayTime(start, options);
    return `${head}–${endsAtMidnight ? "24:00" : clock(end, options)}`;
  }
  return `${dayTime(start, options)} – ${dayTime(end, options)}`;
}

/** "just now", "4 min ago", "3 h ago"; beyond 12 hours the day and time. */
export function ago(t: When, options: TimeOptions = {}) {
  const d = toDate(t);
  if (!valid(d)) return "";
  const seconds = (toDate(options.now ?? Date.now()).getTime() - d.getTime()) / 1000;
  if (seconds < 0) return dayTime(d, options);
  if (seconds < 60) return "just now";
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`;
  if (seconds < 12 * 3600) return `${Math.floor(seconds / 3600)} h ago`;
  return dayTime(d, options);
}

/** The local calendar date ("2026-10-05") of an instant. */
export function localDate(t: When, options: TimeOptions = {}) {
  const d = toDate(t);
  return valid(d) ? wall(d, options.timeZone ?? householdZone).date : "";
}

/**
 * A readable date and time for records (investigations, notes, files): the same as dayTime.
 * Kept under its old name for the components that already import it.
 */
export const stamp = (s: string, timeZone?: string) => dayTime(s, { timeZone });
/** The browser's own zone. */
export const browserTimeZone = () => Intl.DateTimeFormat().resolvedOptions().timeZone;
/** The household's time zone, named only when this browser is somewhere else, e.g. " (Europe/London time)". */
export const zoneNote = (timeZone: string | undefined = householdZone) =>
  timeZone && timeZone !== browserTimeZone() ? ` (${timeZone} time)` : "";

/** Local date and time for tooltip labels: "Sat 4 Oct, 23:00" (always absolute, so a tooltip never says "Today"). */
export const when = (s: string, timeZone?: string) => {
  const d = toDate(s);
  if (!valid(d)) return "";
  return `${dayLabel(d, { timeZone })}, ${clock(d, { timeZone })}`;
};
