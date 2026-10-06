import { ArrowRight, CircleCheck } from "lucide-react";
import { PlainText } from "../PlainText";
import { VerdictBadge, investigationVerdict, verdictLabels } from "../InvestigationText";
import { preview } from "../ui";
import { clock, dayTime } from "../../lib/time";
import { buildHash } from "../../lib/router";
import type { Investigation, InvestigationSchedule } from "../../types";
import { stripLegend, type RunStrip } from "./model";

/**
 * The AI, briefly: when Joule last checked and checks next, the one thing worth knowing from the last two days (or
 * "Nothing needs you right now"), and today's checks as a strip of dots. A check that didn't finish is a grey dot,
 * never a problem.
 */
export function AiCard({
  schedule,
  scheduled,
  top,
  strip,
  timeZone,
  onOpen,
}: {
  schedule?: InvestigationSchedule & {
    nextCheckAt?: string | null;
    lastQuietCheckAt?: string | null;
    lastQuietCheck?: string | null;
  };
  scheduled: boolean;
  top: Investigation | null;
  strip: RunStrip;
  timeZone?: string;
  onOpen: (id: string) => void;
}) {
  const t = (s: string | null | undefined) => (s ? clock(s, { timeZone }) : null);
  // Two kinds of check, in the words used everywhere: an AI check (uses the AI and counts against the daily limit) and a
  // quick check (reads Predbat's log and the battery without the AI).
  const quick = schedule?.lastQuietCheckAt ?? null,
    ai = schedule?.lastCompletedAt ?? null;
  const last =
    quick && (!ai || Date.parse(quick) > Date.parse(ai))
      ? { kind: "quick check", at: quick }
      : ai
        ? { kind: "AI check", at: ai }
        : null;
  const next = schedule?.nextCheckAt
    ? { kind: "quick check", at: schedule.nextCheckAt }
    : schedule?.nextRunAt
      ? { kind: "AI check", at: schedule.nextRunAt }
      : null;
  const used = schedule?.runsToday ?? strip.used;
  const max = schedule?.maxRunsPerDay ?? 0;
  const status = !scheduled
    ? "Automatic checks are off"
    : [
        last ? `Last ${last.kind} ${t(last.at)}` : "Not checked yet today",
        next && Date.parse(next.at) > Date.now()
          ? `next${next.kind === last?.kind ? "" : ` ${next.kind}`} ${t(next.at)}`
          : null,
        // The allowance only matters once it is nearly used up.
        used != null && max > 0 && used >= 0.8 * max ? `${used} of ${max} daily checks used` : null,
      ]
        .filter(Boolean)
        .join(" · ");
  const verdict = top ? investigationVerdict(top) : null;
  const checks = `${strip.runs.length} AI ${strip.runs.length === 1 ? "check" : "checks"}`;
  return (
    <section className="panel ai-card" aria-labelledby="ai-card-heading">
      <div className="panel-head">
        <div>
          <h2 id="ai-card-heading">AI checks</h2>
          <p>{status}</p>
        </div>
        <a className="text-link" href="#/insights">
          Insights <ArrowRight size={14} aria-hidden="true" />
        </a>
      </div>
      {!scheduled && (
        <p className="ai-off">
          Joule only checks when you ask. <a href="#/setup/ai">Turn on automatic checks</a>
        </p>
      )}
      <div className="ai-top">
        <span className="ai-eyebrow">Latest thing worth knowing</span>
        {top && verdict ? (
          <article className="ai-finding">
            <div className="ai-finding-head">
              <VerdictBadge verdict={verdict} />
              <span className="muted">{dayTime(top.at, { timeZone })}</span>
            </div>
            <h3>
              <a
                href={buildHash("insights", "", { id: top.id })}
                onClick={(e) => {
                  e.preventDefault();
                  onOpen(top.id);
                }}
              >
                <PlainText text={top.headline || top.title} />
              </a>
            </h3>
            <p>
              <PlainText text={preview(top.plain || top.summary, 220)} />
            </p>
          </article>
        ) : (
          <p className="ai-quiet">
            <CircleCheck size={18} aria-hidden="true" />
            <span>
              <strong>Nothing needs you right now.</strong>
              {schedule?.lastQuietCheck ? (
                <span className="muted">
                  {" "}
                  <PlainText text={schedule.lastQuietCheck} />
                </span>
              ) : null}
            </span>
          </p>
        )}
      </div>
      {strip.runs.length > 0 && (
        <div className="ai-strip">
          <ol
            className="run-dots"
            aria-label={`${checks} today: ${strip.findings} found something, ${strip.quiet} nothing new, ${strip.failed} didn’t finish`}
          >
            {strip.runs.map((r) => (
              <li key={r.id} className={`run-dot v-${r.view}`} title={undefined}>
                <span className="sr-only">
                  {clock(r.at, { timeZone })} {verdictLabels[r.view]}
                </span>
              </li>
            ))}
          </ol>
          <p className="ai-strip-text">
            <span>{checks} today</span>
            {stripLegend(strip).map((l) => (
              <span key={l.key} className="ai-legend-item">
                <span className="ai-legend-dots" aria-hidden="true">
                  {l.views.map((v) => (
                    <span key={v} className={`run-dot v-${v}`} />
                  ))}
                </span>
                {l.text}
              </span>
            ))}
          </p>
        </div>
      )}
    </section>
  );
}
