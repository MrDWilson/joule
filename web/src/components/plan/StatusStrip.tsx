import { AlertTriangle } from "lucide-react";
import { Chip } from "../ui";
import { Hint } from "../Hint";
import { percent } from "../../lib/format";
import { ago, clock, dayTime } from "../../lib/time";
import { planAge, planEvery, presentAction, planStaleness } from "../today/model";
import { dayWord, priceRange, windowTitle, type ActionView, type PlanWindow } from "./windows";

/** Predbat's running version from its update setting ("v9.3.5 Bug fixes …" → "9.3.5"). */
export function predbatVersion(settings: { key: string; value: string }[]) {
  const raw = settings.find((s) => s.key === "version")?.value ?? settings.find((s) => s.key === "update")?.value;
  const m = raw ? /v?(\d+\.\d+(?:\.\d+)?)/.exec(raw) : null;
  return m ? m[1] : null;
}

/**
 * "Charge to 100% 23:30–05:30 at 6.67p": the next thing Predbat will do, in one line. A window that starts on another
 * day than today names it: "Export down to 4% tomorrow 17:30–19:00 at 32.6–34.4p".
 */
export function nextText(w: PlanWindow, now: number, timeZone?: string) {
  const price = w.price ? ` at ${priceRange(w.price)}` : "";
  const day = dayWord(w.start, now, timeZone, { nights: false });
  const on = day === "Today" ? "" : ` ${/^[A-Z][a-z]+$/.test(day) ? day.toLowerCase() : day}`;
  return `${windowTitle(w)}${on} ${clock(w.start, { timeZone })}–${clock(w.end, { timeZone })}${price}`;
}

/**
 * Predbat at a glance: how old the plan is (amber when older than three of Predbat's planning intervals, or when Joule
 * hasn't been able to read Predbat), what it is doing now, the battery, what's next and the reserve it keeps. Predbat's
 * mode and version sit in the plan's info popover.
 */
export function StatusStrip({
  settings,
  planAt,
  collectedAt,
  lastCollection,
  collectionError,
  action,
  reserve,
  now,
  timeZone,
  demo,
  selected,
  battery,
  next,
}: {
  settings: { key: string; value: string }[];
  planAt: string | null | undefined;
  collectedAt: string | null | undefined;
  /** When Joule last read Predbat successfully. */
  lastCollection?: string | null;
  collectionError?: string | null;
  action: ActionView | null;
  reserve: number | null;
  now: number;
  timeZone?: string;
  demo?: boolean;
  /** An earlier plan is on screen, not the latest. */
  selected?: boolean;
  /** The battery's measured level now (%). */
  battery?: number | null;
  /** The next window that charges or exports. */
  next?: PlanWindow | null;
}) {
  const version = predbatVersion(settings);
  const mode = settings.find((s) => s.key === "mode")?.value;
  const every = planEvery(settings);
  const age = planAge(planAt, now, every);
  const stale = selected
    ? null
    : planStaleness({ collectedAt, lastCollection, collectionError, everyMinutes: every, now });
  return (
    <section className="plan-status" aria-label="Predbat status">
      <div className="plan-status-main">
        <span className="plan-status-label">{selected ? "Earlier plan" : demo ? "Sample plan" : "Plan"}</span>
        <strong className={age?.old && !selected ? "is-old" : undefined}>
          {!planAt
            ? "not read yet"
            : selected
              ? `from ${dayTime(planAt, { timeZone })}`
              : `from ${clock(planAt, { timeZone })}`}
          {planAt && !selected && <span className="muted"> ({ago(planAt, { now, timeZone })})</span>}
        </strong>
        {(age || mode || version) && (
          <Hint label="About this plan and Predbat">
            {age && !selected && (
              <>
                Predbat recalculates its plan every {age.every} minutes
                {age.old ? "; this one is older than three of those, so Predbat may not be re-planning." : "."}
              </>
            )}
            {(mode || version) && (
              <span className="plan-status-about">
                {mode && <>Mode: {mode}. </>}
                {version && <>Predbat {version}.</>}
              </span>
            )}
          </Hint>
        )}
      </div>
      <dl className="plan-status-facts">
        {action && !selected && (
          <div>
            <dt>Now</dt>
            <dd>{presentAction(action)}</dd>
          </div>
        )}
        {battery != null && !selected && (
          <div>
            <dt>Battery</dt>
            <dd>{percent(battery)}</dd>
          </div>
        )}
        {next && !selected && (
          <div>
            <dt>Next</dt>
            <dd>{nextText(next, now, timeZone)}</dd>
          </div>
        )}
        {reserve != null && (
          <div>
            <dt>Reserve</dt>
            <dd>{percent(reserve)}</dd>
          </div>
        )}
      </dl>
      {stale && (
        <Chip tone="warn" icon={<AlertTriangle size={13} aria-hidden="true" />} className="plan-stale-chip">
          {stale.label}
        </Chip>
      )}
    </section>
  );
}
