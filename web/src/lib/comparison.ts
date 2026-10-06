import { zonedDateMidnight } from "./period";

/** The local calendar date ("2026-10-04") of an instant in an IANA zone. */
export function zonedDay(at: Date, timeZone: string) {
  return new Intl.DateTimeFormat("en-CA", { timeZone, year: "numeric", month: "2-digit", day: "2-digit" }).format(at);
}

/** Local midnight at the end of `day`, i.e. the start of the next calendar day. */
export function endOfZonedDay(day: string, timeZone: string) {
  const next = new Date(`${day}T12:00:00Z`);
  next.setUTCDate(next.getUTCDate() + 1);
  return zonedDateMidnight(next.toISOString().slice(0, 10), timeZone);
}

const wallFormats = new Map<string, Intl.DateTimeFormat>();
/** Wall-clock fields of an instant in a zone, as a UTC timestamp of the same fields (so differences are wall-clock differences). */
function wallStamp(ms: number, timeZone: string) {
  let f = wallFormats.get(timeZone);
  if (!f) {
    f = new Intl.DateTimeFormat("en-GB", {
      timeZone,
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
      hourCycle: "h23",
    });
    wallFormats.set(timeZone, f);
  }
  const p = Object.fromEntries(f.formatToParts(new Date(ms)).map((x) => [x.type, x.value]));
  return (
    Date.UTC(Number(p.year), Number(p.month) - 1, Number(p.day), Number(p.hour), Number(p.minute), Number(p.second)) +
    (((ms % 1000) + 1000) % 1000)
  );
}

/**
 * The instant at which the zone's clocks show `wall` (fields encoded as a UTC timestamp). A time that happens twice (the
 * hour repeated when the clocks go back) resolves to its first occurrence; a time skipped when the clocks go forward
 * resolves to the same distance past the jump.
 */
export function instantOfWall(wall: number, timeZone: string) {
  const offsetAt = (ms: number) => wallStamp(ms, timeZone) - ms;
  const candidates = new Set<number>();
  for (const probe of [wall - 36 * 3600000, wall, wall + 36 * 3600000]) candidates.add(wall - offsetAt(probe));
  for (const c of [...candidates]) candidates.add(wall - offsetAt(c));
  const exact = [...candidates].filter((c) => wallStamp(c, timeZone) === wall).sort((a, b) => a - b);
  if (exact.length) return exact[0];
  // In the gap: use the offset in force before the jump.
  return wall - offsetAt(wall - 36 * 3600000);
}

/**
 * The same local clock time `days` calendar days later (negative: earlier). On the days the clocks change this is not a
 * multiple of 24 hours: 10:30 on Sun 25 Oct 2026 (GMT) shifted back a day is 10:30 BST on Sat 24 Oct, 25 hours earlier.
 */
export function shiftLocalDays(ms: number, days: number, timeZone: string) {
  if (!Number.isFinite(ms) || !days) return ms;
  return instantOfWall(wallStamp(ms, timeZone) + days * 86400000, timeZone);
}

export interface EarlierPeriod {
  params: URLSearchParams;
  /** How the change reads after "vs": "this time yesterday", "the day before", "the previous 3 days". */
  label: string;
  /** The earlier period as a noun for short notes: "yesterday", "the day before". */
  noun: string;
  /** The period being looked at, as a noun: "today", "this day", "this period". */
  currentNoun: string;
}

/**
 * The equivalent earlier period for periods of up to a week: the same number of local days immediately before, and for a period
 * that runs up to now ("today so far") only up to the same local clock time, so a partial day is never compared with a whole
 * one. Both ends move by whole local calendar days, so the window stays right on the days the clocks change.
 * Shared by the Data page and the Overview cards.
 */
export function previousPeriod(
  fromDay: string,
  toDay: string,
  today: string,
  start: Date,
  end: Date,
  requestedEnd: Date,
  timeZone: string,
): EarlierPeriod | null {
  const count = Math.round((Date.parse(`${toDay}T12:00:00Z`) - Date.parse(`${fromDay}T12:00:00Z`)) / 86400000) + 1;
  if (!(count >= 1 && count <= 7)) return null;
  const shifted = new Date(`${fromDay}T12:00:00Z`);
  shifted.setUTCDate(shifted.getUTCDate() - count);
  const prevStart = zonedDateMidnight(shifted.toISOString().slice(0, 10), timeZone);
  const prevEnd = end < requestedEnd ? new Date(shiftLocalDays(end.getTime(), -count, timeZone)) : start;
  if (!Number.isFinite(prevStart.getTime()) || prevEnd <= prevStart) return null;
  const partialToday = fromDay === today && end < requestedEnd;
  const label =
    count === 1
      ? fromDay === today
        ? partialToday
          ? "this time yesterday"
          : "yesterday"
        : "the day before"
      : `the previous ${count} days`;
  const noun = count === 1 ? (fromDay === today ? "yesterday" : "the day before") : `the previous ${count} days`;
  const currentNoun = count === 1 ? (fromDay === today ? "today" : "this day") : "this period";
  return {
    params: new URLSearchParams({ from: prevStart.toISOString(), to: prevEnd.toISOString() }),
    label,
    noun,
    currentNoun,
  };
}

/** Today so far, and the same stretch of yesterday: midnight to the same local clock time. */
export function todaySoFar(now: Date, timeZone: string) {
  const today = zonedDay(now, timeZone);
  const start = zonedDateMidnight(today, timeZone);
  const earlier = previousPeriod(today, today, today, start, now, endOfZonedDay(today, timeZone), timeZone);
  return { params: new URLSearchParams({ from: start.toISOString(), to: now.toISOString() }), earlier };
}
