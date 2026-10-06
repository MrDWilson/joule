import type { InvestigationSchedule } from "../types";
import { ArrowRight, BrainCircuit, Clock } from "lucide-react";
import { Button, ButtonLink } from "./ui";
import { PlainText } from "./PlainText";
import { clock, dayTime } from "../lib/time";

/** True when automatic AI checks need the homeowner: the AI connection is failing or today's allowance is used up. */
export function automationNeedsAction(schedule: InvestigationSchedule | undefined, scheduled: boolean) {
  return scheduled && (schedule?.state === "ProviderUnavailable" || schedule?.state === "DailyLimit");
}

/**
 * Today's version: a one-line band shown only when automatic AI checks need action: "AI checks paused: the AI connection is failing" or
 * "Today's 24 checks are used up; the next one runs after midnight". Otherwise the AI card's status line says it all.
 */
export function AutomationAlert({
  schedule,
  scheduled,
  timeZone,
}: {
  schedule?: InvestigationSchedule;
  scheduled: boolean;
  timeZone?: string;
}) {
  if (!automationNeedsAction(schedule, scheduled) || !schedule) return null;
  const limit = schedule.state === "DailyLimit";
  const title = limit
    ? `Today’s ${schedule.maxRunsPerDay} AI checks are used up`
    : "Automatic AI checks are paused: the AI connection needs attention";
  return (
    <section className="automation-band" aria-label="AI checks need attention">
      <BrainCircuit size={18} aria-hidden="true" />
      <p>
        <strong>{title}</strong>
        <span>
          {limit ? (
            schedule.nextRunAt ? (
              ` The next one can run from ${clock(schedule.nextRunAt, { timeZone })}.`
            ) : (
              " More can run after midnight."
            )
          ) : (
            <>
              {" "}
              <PlainText text={schedule.reason} />
            </>
          )}
        </span>
      </p>
      <ButtonLink href="#/setup/ai" size="sm">
        {limit ? "Change the limit" : "Check the AI connection"}
      </ButtonLink>
    </section>
  );
}

/** The full automatic-check status, for the AI settings page. */
export function AutomationStatus({
  schedule,
  scheduled,
  mode,
  onConfigure,
  timeZone,
}: {
  schedule?: InvestigationSchedule;
  scheduled: boolean;
  mode: string;
  onConfigure?: () => void;
  timeZone?: string;
}) {
  const running = schedule?.state === "Running",
    off = !running && !scheduled;
  const title = running
    ? "Review in progress"
    : off
      ? "Automatic reviews are off."
      : schedule?.state === "Due"
        ? "Next review is due"
        : schedule?.state === "Waiting"
          ? "Automatic reviews are scheduled"
          : schedule?.state === "DailyLimit"
            ? "Daily review limit reached"
            : schedule?.state === "ProviderUnavailable"
              ? "AI connection needs attention"
              : "Automatic review schedule";
  // When reviews are off the title says everything; the server reason only adds detail for active schedules.
  return (
    <section className="automation-status" aria-label="Automatic review status">
      <span className={`automation-icon ${scheduled ? "enabled" : ""}`}>
        <BrainCircuit size={22} />
      </span>
      <div className="automation-copy">
        <strong>{title}</strong>
        {!off && <p>{schedule?.reason || "Checking when the next review can run."}</p>}
        {mode === "Monitor" && (
          <small>The AI is set to Watch only: reviews record findings but never change settings.</small>
        )}
        {schedule?.nextRunAt && (
          <p className="next-review">
            <Clock size={13} />
            Next review from {dayTime(schedule.nextRunAt, { timeZone })}
          </p>
        )}
      </div>
      <div className="automation-meta">
        {schedule && (
          <span>
            {schedule.runsToday} of {schedule.maxRunsPerDay} reviews used today
          </span>
        )}
        {onConfigure && (
          <Button variant="ghost" onClick={onConfigure}>
            Configure reviews <ArrowRight size={14} />
          </Button>
        )}
      </div>
    </section>
  );
}
