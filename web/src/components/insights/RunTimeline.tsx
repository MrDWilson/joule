import { useId, useState } from "react";
import { AlertCircle, ChevronDown, ChevronRight, RotateCw } from "lucide-react";
import { useApp } from "../../context/AppContext";
import { Button, ButtonLink, Chip } from "../ui";
import { RichText } from "../InvestigationText";
import {
  failureView,
  headlineOf,
  impactChipFor,
  isQuiet,
  isUnfinished,
  plainOf,
  quietChecks,
  runTimeline,
  verdictLook,
  type CalmEntry,
  type QuietCheck,
  type TimelineItem,
} from "../../lib/insights";
import { buildHash } from "../../lib/router";
import { clock, dayTime } from "../../lib/time";
import type { Investigation } from "../../types";

/** Chips for what a check left behind: to-dos, file edits, suggestions, repeats. */
function RunChips({ i, proposals }: { i: Investigation; proposals: number }) {
  const todos = (i.nextSteps ?? []).filter((s) => (s.status ?? "open") !== "closed").length;
  const files = (i.fileChanges ?? []).filter((f) => f.status === "pending" || f.status === "applied").length;
  // Left out when the headline already states money the other way ("lost about 67p" beside "£1.07 under plan").
  const impact = impactChipFor(i);
  const chips: { label: string; tone: string }[] = [];
  if (impact) chips.push(impact);
  if (proposals) chips.push({ label: proposals === 1 ? "1 suggestion" : `${proposals} suggestions`, tone: "neutral" });
  if (todos) chips.push({ label: todos === 1 ? "1 to-do" : `${todos} to-dos`, tone: "neutral" });
  if (files) chips.push({ label: files === 1 ? "1 file edit" : `${files} file edits`, tone: "neutral" });
  if (i.occurrences && i.occurrences > 1) chips.push({ label: `Seen ${i.occurrences}×`, tone: "neutral" });
  if (i.dismissedAt) chips.push({ label: "Dismissed", tone: "neutral" });
  if (!chips.length) return null;
  return (
    <span className="run-chips">
      {chips.map((c) => (
        <Chip key={c.label} tone={c.tone}>
          {c.label}
        </Chip>
      ))}
    </span>
  );
}

/** One check as a row: verdict dot, headline, one line, time and chips. Opens the check's own page. */
export function RunRow({ i, selected, compact = false }: { i: Investigation; selected?: boolean; compact?: boolean }) {
  const { data, mutate, busy } = useApp();
  const look = verdictLook(i);
  const unfinished = isUnfinished(i);
  const quiet = isQuiet(i);
  const failure = unfinished ? failureView(i) : null;
  const proposals = data.state.proposals.filter((p) => p.investigationId === i.id && p.status === "Pending").length;
  const href = buildHash("insights", "", { id: i.id });
  const sub = failure ? [failure.message, failure.nextTry].filter(Boolean).join(" ") : plainOf(i, compact ? 110 : 160);
  return (
    <li
      className={`run-row tone-${look.tone}${unfinished ? " is-unfinished" : ""}${quiet ? " is-quiet" : ""}${selected ? " is-selected" : ""}`}
    >
      <a className="run-link" href={href} aria-current={selected ? "page" : undefined}>
        {unfinished ? (
          // A check that didn't finish gets its own marker, never the hollow dot of a quiet check.
          <AlertCircle size={14} className="run-alert" aria-hidden="true" />
        ) : (
          <span className="run-dot" aria-hidden="true" />
        )}
        <span className="run-body">
          <span className="run-top">
            <span className="run-headline">
              <span className="sr-only">{look.label}: </span>
              {failure ? failure.title : <RichText text={headlineOf(i)} />}
            </span>
            {!quiet && (
              // The marker's meaning in a word, so colour is never the only cue (the screen reader hears it first).
              <span className="run-kind" aria-hidden="true">
                {look.label}
              </span>
            )}
            <time className="run-time" dateTime={i.at}>
              {clock(i.at)}
            </time>
          </span>
          {sub && (
            <span className="run-sub">
              <RichText text={sub} />
            </span>
          )}
          {!unfinished && <RunChips i={i} proposals={proposals} />}
        </span>
        <ChevronRight size={16} className="run-chevron" aria-hidden="true" />
      </a>
      {failure?.action && (
        <div className="run-actions">
          {failure.action.kind === "resume" ? (
            <Button
              size="sm"
              variant="secondary"
              disabled={busy || data.ai.running}
              onClick={() =>
                void mutate(`/investigations/${encodeURIComponent(i.id)}/resume`, {}, "Trying that check again.")
              }
            >
              <RotateCw size={13} aria-hidden="true" />
              {failure.action.label}
              <span className="sr-only"> the check from {dayTime(i.at)}</span>
            </Button>
          ) : (
            <ButtonLink size="sm" variant="secondary" href="#/setup/ai">
              {failure.action.label}
            </ButtonLink>
          )}
        </div>
      )}
    </li>
  );
}

/** A check Joule made without the AI ("nothing new"), from the scheduler's activity log. */
function CheckRow({ check }: { check: QuietCheck }) {
  const nothing = /nothing new/i.test(check.text);
  const rest = check.text
    .replace(/^Checked\s+.+?:\s+(nothing new\.\s*)?/i, "")
    .replace(/\s*The AI wasn't needed\.?$/, "");
  return (
    <li className="run-row tone-neutral is-quiet is-check">
      <div className="run-link" role="presentation">
        <span className="run-dot" aria-hidden="true" />
        <span className="run-body">
          <span className="run-top">
            <span className="run-headline">{nothing ? "Checked, nothing new" : "Spotted something new"}</span>
            <time className="run-time" dateTime={check.at}>
              {clock(check.at)}
            </time>
          </span>
          {rest && <span className="run-sub">{rest}</span>}
          <span className="run-chips">
            <Chip tone="neutral">Quick check · no AI</Chip>
          </span>
        </span>
      </div>
    </li>
  );
}

function Entry({ e, selectedId }: { e: CalmEntry; selectedId?: string }) {
  return e.kind === "run" ? (
    <RunRow i={e.investigation} selected={e.investigation.id === selectedId} compact />
  ) : (
    <CheckRow check={e.check} />
  );
}

/** Quiet checks and checks that didn't finish, folded into one row that opens in place. */
function GroupRow({ item, selectedId }: { item: Extract<TimelineItem, { kind: "group" }>; selectedId?: string }) {
  const containsSelected = item.entries.some((e) => e.kind === "run" && e.investigation.id === selectedId);
  const [open, setOpen] = useState(containsSelected);
  const id = useId();
  return (
    <li className={`run-group${open ? " is-open" : ""}`}>
      <button
        type="button"
        className="run-group-toggle"
        aria-expanded={open}
        aria-controls={id}
        onClick={() => setOpen(!open)}
      >
        <span className="run-dot" aria-hidden="true" />
        <span className="run-group-label">{item.label}</span>
        <ChevronDown size={16} aria-hidden="true" className="run-group-chevron" />
      </button>
      {open && (
        <ul className="run-group-list" id={id}>
          {item.entries.map((e) => (
            <Entry key={e.kind === "run" ? e.investigation.id : `c-${e.check.at}`} e={e} selectedId={selectedId} />
          ))}
        </ul>
      )}
    </li>
  );
}

const PAGE = 14;

/** Recent checks, newest first by day, with quiet stretches folded. */
export function RunTimeline({ selectedId }: { selectedId?: string }) {
  const { data, timeZone } = useApp();
  const [shown, setShown] = useState(PAGE);
  const headingId = useId();
  const days = runTimeline(data.state.investigations, quietChecks(data.state.activities), { timeZone });
  let budget = shown;
  const visible = days
    .map((d) => {
      const items = d.items.slice(0, Math.max(0, budget));
      budget -= items.length;
      return { ...d, items };
    })
    .filter((d) => d.items.length);
  const total = days.reduce((n, d) => n + d.items.length, 0);
  return (
    <section className="insights-section recent-checks" aria-labelledby={headingId}>
      <div className="insights-section-head">
        <h2 id={headingId}>Recent checks</h2>
      </div>
      {visible.length ? (
        visible.map((day) => (
          <div className="run-day" key={day.date}>
            <h3 className="run-day-label">{day.label}</h3>
            <ul className="run-list">
              {day.items.map((item) =>
                item.kind === "group" ? (
                  <GroupRow key={item.key} item={item} selectedId={selectedId} />
                ) : item.kind === "run" ? (
                  <RunRow key={item.key} i={item.investigation} selected={item.investigation.id === selectedId} />
                ) : (
                  <CheckRow key={item.key} check={item.check} />
                ),
              )}
            </ul>
          </div>
        ))
      ) : (
        <p className="inbox-empty">
          <strong>No checks yet.</strong> Ask a question above, or run a full check.
        </p>
      )}
      {total > shown && (
        <Button variant="ghost" className="show-older" onClick={() => setShown(shown + PAGE)}>
          Show older checks
        </Button>
      )}
    </section>
  );
}
