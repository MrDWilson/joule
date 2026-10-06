import { useEffect, useState } from "react";
import { ChevronLeft, ChevronRight } from "lucide-react";
import type { Api } from "../completion-types";
import { Button } from "./ui";
import { zonedDateMidnight } from "../lib/period";
import { endOfZonedDay } from "../lib/comparison";
import { dayTime, localDate } from "../lib/time";

interface PlanPage {
  items: { id: string; at: string; source: string }[];
  total: number;
  offset: number;
  limit: number;
}

/**
 * The stored plans either side of the one made at `at` (newest first on the server, `to` exclusive): the one just
 * before, and the one just after. `later` is "latest" when the next plan is the newest one, so stepping forward from the
 * last-but-one plan returns to the live page. Only GET requests.
 */
function useNeighbours(api: Api, at: string | undefined, selected: string, refreshKey?: string) {
  const [result, setResult] = useState<{ earlier: string | null; later: string | null | "latest" } | null>(null);
  useEffect(() => {
    let active = true;
    setResult(null);
    if (!at) return;
    const when = Date.parse(at);
    if (!Number.isFinite(when)) return;
    const query = (params: Record<string, string>) => api<PlanPage>(`/plans?${new URLSearchParams(params)}`);
    const before = query({ to: new Date(when).toISOString(), limit: "1" }).then(
      (page) => page.items.find((p) => p.id !== selected)?.id ?? null,
    );
    // Plans after this one, newest first: the last of them is the next one.
    const after = selected
      ? query({ from: new Date(when + 1).toISOString(), limit: "1" }).then(async (page) => {
          if (page.total <= 1 || !page.items.some((p) => p.id !== selected)) return "latest" as const;
          const next = await query({
            from: new Date(when + 1).toISOString(),
            offset: String(page.total - 1),
            limit: "1",
          });
          return next.items.find((p) => p.id !== selected)?.id ?? ("latest" as const);
        })
      : Promise.resolve(null);
    Promise.all([before, after])
      .then(([earlier, later]) => active && setResult({ earlier, later }))
      .catch(() => active && setResult({ earlier: null, later: selected ? "latest" : null }));
    return () => {
      active = false;
    };
  }, [api, at, selected, refreshKey]);
  return result;
}

/**
 * Picks a stored plan: the plan before or after the one on screen, or any plan from a chosen day (today by default),
 * listed by time ("Today 19:13"). `at` is when the plan on screen was made (the latest plan's time when none is chosen).
 * Only GET requests.
 */
export function PlanBrowser({
  api,
  selected,
  at,
  onChoose,
  refreshKey,
  timeZone = "Europe/London",
}: {
  refreshKey?: string;
  timeZone?: string;
  api: Api;
  /** The chosen plan's id, or "" for the latest. */
  selected: string;
  /** When the plan on screen was made. */
  at?: string;
  onChoose: (id: string) => void;
}) {
  // The day follows the plan on screen (today for the latest), until the person picks another.
  const shownDay = localDate(at ?? Date.now(), { timeZone });
  const [day, setDay] = useState(shownDay);
  const [offset, setOffset] = useState(0);
  useEffect(() => {
    setDay(shownDay);
    setOffset(0);
  }, [shownDay]);
  const [result, setResult] = useState<PlanPage | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);
  useEffect(() => {
    let active = true;
    const params = new URLSearchParams({ offset: String(offset), limit: "50" });
    if (day) {
      params.set("from", zonedDateMidnight(day, timeZone).toISOString());
      params.set("to", endOfZonedDay(day, timeZone).toISOString());
    }
    setLoading(true);
    setError("");
    api<PlanPage>(`/plans?${params}`)
      .then((value: PlanPage) => {
        if (active) setResult(value);
      })
      .catch((e: Error) => {
        if (active) setError(e.message);
      })
      .finally(() => {
        if (active) setLoading(false);
      });
    return () => {
      active = false;
    };
  }, [api, day, offset, refreshKey, timeZone]);
  const neighbours = useNeighbours(api, at, selected, refreshKey);
  const items = result?.items ?? [];
  const listed = items.some((p) => p.id === selected);
  const earlier = neighbours?.earlier ?? null;
  const later = neighbours?.later ?? null;
  return (
    <div className="plan-browser">
      <div className="plan-browser-row">
        <div className="plan-browser-step" role="group" aria-label="Step through saved plans">
          <Button variant="secondary" size="sm" disabled={!earlier} onClick={() => earlier && onChoose(earlier)}>
            <ChevronLeft size={14} aria-hidden="true" /> Earlier
          </Button>
          <select
            className="control-sm"
            aria-label="Plan snapshot"
            value={selected}
            onChange={(e) => onChoose(e.target.value)}
            disabled={loading && !result}
          >
            <option value="">Latest plan</option>
            {selected && !listed && (
              <option value={selected}>{at ? dayTime(at, { timeZone }) : "The plan on screen"}</option>
            )}
            {items.map((p) => (
              <option key={p.id} value={p.id}>
                {dayTime(p.at, { timeZone })}
              </option>
            ))}
          </select>
          <Button
            variant="secondary"
            size="sm"
            disabled={!selected || !later}
            onClick={() => later && onChoose(later === "latest" ? "" : later)}
          >
            {later === "latest" ? "Latest" : "Later"} <ChevronRight size={14} aria-hidden="true" />
          </Button>
        </div>
        <label className="plan-browser-day">
          <span>Day</span>
          <input
            className="control-sm"
            aria-label="Snapshot generation date"
            type="date"
            value={day}
            onChange={(e) => {
              setDay(e.target.value);
              setOffset(0);
            }}
          />
        </label>
      </div>
      {error && (
        <p role="alert" className="callout">
          {error}
        </p>
      )}
      <p className="muted plan-browser-note">
        {loading
          ? "Loading saved plans…"
          : result
            ? `${result.total} saved ${result.total === 1 ? "plan" : "plans"}${day ? " on this date" : ""}`
            : ""}
        {result && result.total > items.length + offset && !loading && (
          <>
            {" · "}
            <button type="button" className="text-link" onClick={() => setOffset(offset + 50)}>
              Older plans
            </button>
          </>
        )}
        {offset > 0 && !loading && (
          <>
            {" · "}
            <button type="button" className="text-link" onClick={() => setOffset(Math.max(0, offset - 50))}>
              Newer plans
            </button>
          </>
        )}
      </p>
    </div>
  );
}
