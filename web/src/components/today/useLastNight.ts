import { useEffect, useState } from "react";
import type { Api, EnergySummary } from "../../completion-types";
import type { Plan } from "../../types";
import { endOfZonedDay, instantOfWall, zonedDay } from "../../lib/comparison";
import { zonedDateMidnight } from "../../lib/period";
import { clock } from "../../lib/time";
import type { CheapWindow, SummaryDetail } from "./model";

const MIN = 60000;

/** 18:00 local on the evening a window starting at `start` belongs to (the same day, or the day before for small hours). */
export function eveningBefore(start: number, timeZone: string) {
  const day = zonedDay(new Date(start), timeZone);
  const hour = Number(clock(start, { timeZone }).slice(0, 2));
  const d = new Date(`${day}T12:00:00Z`);
  if (hour < 18) d.setUTCDate(d.getUTCDate() - 1);
  const wall = Date.parse(`${d.toISOString().slice(0, 10)}T18:00:00Z`);
  return instantOfWall(wall, timeZone);
}

/** The planned battery level at `at` in a stored plan: the end level of the slot ending there, or the start of the next. */
export function plannedLevelAt(plan: Plan, at: number): number | null {
  for (const s of plan.slots as (Plan["slots"][number] & { socForecastEnd?: number | null })[]) {
    const start = Date.parse(s.time),
      end = start + (s.durationMinutes || 30) * MIN;
    if (Math.abs(end - at) < MIN && typeof s.socForecastEnd === "number") return s.socForecastEnd;
    if (Math.abs(start - at) < MIN && typeof s.socForecast === "number") return s.socForecast;
  }
  return null;
}

export interface LastNightData {
  /** Meter totals for the cheap window. */
  window: SummaryDetail | null;
  /** The whole of yesterday (local midnight to midnight). */
  yesterday: EnergySummary | null;
  /** What the plan in force at 18:00 the evening before expected the battery to reach by the end of the window. */
  planned: { level: number | null; at: string | null } | null;
}

/**
 * Fetches (GET only) what the "Last night" card needs: the window's meter totals, yesterday's totals and the evening plan.
 * Each request is made once per window or day; a failure leaves that part out.
 */
export function useLastNight(api: Api, window: CheapWindow | null, timeZone: string, refreshKey?: string) {
  const [data, setData] = useState<LastNightData>({ window: null, yesterday: null, planned: null });
  const windowKey = window ? `${window.start}-${window.end}` : "";
  const today = zonedDay(new Date(), timeZone);
  useEffect(() => {
    if (!window) return;
    let active = true;
    const params = new URLSearchParams({
      from: new Date(window.start).toISOString(),
      to: new Date(window.end).toISOString(),
    });
    api<SummaryDetail>(`/telemetry/summary?${params}`)
      .then((summary) => active && setData((d) => ({ ...d, window: summary })))
      .catch(() => active && setData((d) => ({ ...d, window: null })));
    const evening = eveningBefore(window.start, timeZone);
    const list = new URLSearchParams({ to: new Date(evening).toISOString(), limit: "1" });
    api<{ items: { id: string; at: string }[] }>(`/plans?${list}`)
      .then(async (page) => {
        const ref = page.items[0];
        // Only a plan made within the six hours before 18:00 counts as "the evening plan".
        if (!ref || evening - Date.parse(ref.at) > 6 * 3600000) return null;
        const plan = await api<Plan>(`/plans/${encodeURIComponent(ref.id)}`);
        return { level: plannedLevelAt(plan, window.end), at: ref.at };
      })
      .then((planned) => active && setData((d) => ({ ...d, planned })))
      .catch(() => active && setData((d) => ({ ...d, planned: null })));
    return () => {
      active = false;
    };
    // The window's own figures only change while readings for it are still arriving.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, windowKey, timeZone, refreshKey]);
  useEffect(() => {
    let active = true;
    const start = zonedDateMidnight(today, timeZone);
    const yesterdayDay = zonedDay(new Date(start.getTime() - 12 * 3600000), timeZone);
    const params = new URLSearchParams({
      from: zonedDateMidnight(yesterdayDay, timeZone).toISOString(),
      to: endOfZonedDay(yesterdayDay, timeZone).toISOString(),
    });
    api<EnergySummary>(`/telemetry/summary?${params}`)
      .then((summary) => active && setData((d) => ({ ...d, yesterday: summary })))
      .catch(() => active && setData((d) => ({ ...d, yesterday: null })));
    return () => {
      active = false;
    };
  }, [api, today, timeZone]);
  return data;
}
