/** Time-axis ticks on clean local hours, in the household's time zone. */

const formats = new Map<string, Intl.DateTimeFormat>();
function format(timeZone: string | undefined) {
  const key = timeZone ?? "";
  let f = formats.get(key);
  if (!f) {
    // lib/time.ts formats whole labels; a time axis needs the wall-clock parts of hundreds of instants, so this keeps one
    // cached formatter per zone (same en-GB parts and three-letter months as lib/time).
    // eslint-disable-next-line no-restricted-syntax
    f = new Intl.DateTimeFormat("en-GB", {
      timeZone,
      hourCycle: "h23",
      hour: "2-digit",
      minute: "2-digit",
      weekday: "short",
      day: "numeric",
      month: "short",
      year: "numeric",
    });
    formats.set(key, f);
  }
  return f;
}

export interface Wall {
  hour: number;
  minute: number;
  /** "18:30" */
  time: string;
  /** "Sun 4 Oct" */
  day: string;
  /** "Sun 4" */
  dayShort: string;
  /** A sortable local date key, "2026-Oct-4". */
  date: string;
}

/** Local wall-clock parts of an instant (ms). */
export function wallOf(ms: number, timeZone?: string): Wall {
  const p = Object.fromEntries(
    format(timeZone)
      .formatToParts(new Date(ms))
      .map((x) => [x.type, x.value]),
  );
  const month = String(p.month).slice(0, 3);
  return {
    hour: Number(p.hour),
    minute: Number(p.minute),
    time: `${p.hour}:${p.minute}`,
    day: `${p.weekday} ${p.day} ${month}`,
    dayShort: `${p.weekday} ${p.day}`,
    date: `${p.year}-${month}-${p.day}`,
  };
}

export interface TimeTick {
  t: number;
  time: string;
  /** Set on the ticks that carry a date line: the first, and wherever the local day changes. */
  day?: string;
  midnight: boolean;
}

const HALF_HOUR = 1800000;

/**
 * Ticks on whole local hours: the densest of every 1, 2, 3, 6, 12 or 24 hours that fits within `maxTicks`. The date is written
 * under the first tick and under every local midnight; a first tick that is not itself a midnight drops its date when the next
 * tick already carries one, so the two never collide on a phone.
 */
export function timeTicks(start: number, end: number, timeZone: string | undefined, maxTicks: number): TimeTick[] {
  if (!(end > start) || !Number.isFinite(start) || !Number.isFinite(end)) return [];
  const hours: { t: number; wall: Wall }[] = [];
  for (let t = Math.ceil(start / HALF_HOUR) * HALF_HOUR; t <= end; t += HALF_HOUR) {
    const wall = wallOf(t, timeZone);
    if (wall.minute === 0) hours.push({ t, wall });
  }
  let chosen = hours;
  for (const step of [1, 2, 3, 6, 12, 24]) {
    chosen = hours.filter((h) => h.wall.hour % step === 0);
    if (chosen.length <= Math.max(2, maxTicks)) break;
  }
  const ticks: TimeTick[] = chosen.map((h, i) => {
    const midnight = h.wall.hour === 0;
    const dayChange = i > 0 && h.wall.date !== chosen[i - 1].wall.date;
    return { t: h.t, time: h.wall.time, midnight, day: midnight || dayChange ? h.wall.day : undefined };
  });
  if (ticks.length && !ticks[0].day) {
    const next = ticks[1];
    if (!next?.day) ticks[0].day = wallOf(ticks[0].t, timeZone).day;
  }
  return ticks;
}

/** Where to draw a tick label so it never runs off either edge: right-aligned at the right edge, held in at the left. */
export function tickPlacement(px: number, tick: TimeTick, width: number) {
  const half = (Math.max(tick.time.length, tick.day?.length ?? 0) * 6.6) / 2;
  if (px + half > width - 1) return { x: width - 1, anchor: "end" as const };
  return { x: Math.max(half, px), anchor: "middle" as const };
}

/** Local midnights inside (start, end], for faint day-boundary lines. */
export function midnights(start: number, end: number, timeZone: string | undefined) {
  const out: number[] = [];
  for (let t = Math.ceil(start / HALF_HOUR) * HALF_HOUR; t <= end; t += HALF_HOUR) {
    const w = wallOf(t, timeZone);
    if (w.hour === 0 && w.minute === 0 && t > start) out.push(t);
  }
  return out;
}

/**
 * "18:30–19:00" for a slot, or "Sun 4 Oct · 18:30–19:00" with the day. A slot ending at midnight reads "23:30–00:00":
 * the chart's slots sit under day headings and a clock that never shows 24:00.
 */
export function slotLabel(start: number, end: number, timeZone: string | undefined, withDay = true) {
  const a = wallOf(start, timeZone),
    b = wallOf(end, timeZone);
  return `${withDay ? `${a.day} · ` : ""}${a.time}–${b.time}`;
}
