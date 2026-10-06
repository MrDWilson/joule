import { useContext, useEffect, useId, useState } from "react";
import { Check, Copy, FileCode2 } from "lucide-react";
import { Button } from "./ui";
import { InvestigationText, RichText } from "./InvestigationText";
import { RecommendationReply } from "./RecommendationReply";
import type { Api, Mutate } from "../completion-types";
import type { ConfigFileChange, Investigation } from "../types";
import "./ConfigFileChangeCard.css";
import { AppContext } from "../context/AppContext";
import { closedStatus, isOpenFileChange } from "../lib/insights";
import { dayTime } from "../lib/time";
import { plainText } from "../lib/sanitize";

export const isOpenConfigFileChange = isOpenFileChange;

const changeKey = (change: ConfigFileChange) =>
  `${change.file.toLowerCase()}\n${change.snippet.replace(/\s+/g, " ").trim().toLowerCase()}`;

/** Open configuration file changes across every check, newest first, one card per distinct edit. */
export function openConfigFileChanges(investigations: Investigation[], limit = 8) {
  const seen = new Set<string>();
  const newestFirst = [...investigations].sort((a, b) => Date.parse(b.at) - Date.parse(a.at));
  return newestFirst
    .flatMap((investigation) =>
      (investigation.fileChanges ?? []).flatMap((change) => {
        if (!isOpenConfigFileChange(change)) return [];
        const key = changeKey(change);
        if (seen.has(key)) return [];
        seen.add(key);
        return [{ investigation, change }];
      }),
    )
    .slice(0, limit);
}

/** File edits waiting for you to make (not ones you've marked applied, which wait for the next check): for badges. */
export const fileEditsNeedingYou = (investigations: Investigation[]) =>
  openConfigFileChanges(investigations, Infinity).filter((x) => x.change.status === "pending");

function statusText(change: ConfigFileChange) {
  if (change.status === "pending") return "Waiting for you";
  if (change.status === "applied") return "You applied this";
  return closedStatus("file", change.status, change.thread, change.closedReason, change.decisionNote);
}

/** True when part of the snippet was hidden for safety (a credential), so copying it would paste a broken file. */
export const isRedactedSnippet = (change: Pick<ConfigFileChange, "snippet">) => /\[redacted/i.test(change.snippet);
/** True when the edit replaces lines already in the file: it carries the old lines, or its instructions say replace. */
export const isReplacement = (change: Pick<ConfigFileChange, "before" | "location" | "summary">) =>
  !!change.before?.trim() || /\breplac/i.test(change.location) || /\breplac/i.test(change.summary);

/** The edit as a diff: removed lines from `before`, then the lines to add. */
function SnippetDiff({ change }: { change: ConfigFileChange }) {
  const replaces = isReplacement(change);
  const lines = [
    ...(change.before ? change.before.split("\n").map((text) => ({ sign: "-", text })) : []),
    ...change.snippet.split("\n").map((text) => ({ sign: "+", text })),
  ];
  return (
    <figure className="config-diff">
      <figcaption>
        {replaces ? "Replace" : "Add"} in <code>{change.file}</code>
      </figcaption>
      <pre tabIndex={0} aria-label={`${replaces ? "Replacement" : "Lines to add"} for ${change.file}`}>
        <code>
          {lines.map((line, index) => (
            <span key={index} className={line.sign === "+" ? "diff-add" : "diff-remove"}>
              <span className="diff-sign" aria-hidden="true">
                {line.sign}
              </span>
              {line.text || " "}
              {"\n"}
            </span>
          ))}
        </code>
      </pre>
    </figure>
  );
}

export async function copyText(text: string) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    // Clipboard access needs a secure context; a plain-HTTP deployment falls back to a selection copy.
    const area = Object.assign(document.createElement("textarea"), { value: text });
    area.setAttribute("readonly", "");
    area.style.position = "fixed";
    area.style.opacity = "0";
    document.body.appendChild(area);
    area.select();
    const copied = document.execCommand("copy");
    area.remove();
    return copied;
  }
}

/** How the next check confirms the edit took effect, honestly: Joule can only read apps.yaml through Predbat's MCP tools. */
function useVerificationNote(change: ConfigFileChange) {
  const app = useContext(AppContext);
  const tools = app?.data.ai.mcp?.tools ?? [];
  const canRead = tools.some((t) => /^get_apps/.test(t));
  const file = change.file || "the file";
  if (app?.data.connection.demo) return "The next check confirms the edit took effect.";
  return canRead
    ? `Joule reads ${file} through Predbat at the next check to confirm the edit is there.`
    : `Joule can't read ${file} directly, so the next check confirms the effect shows up in Predbat's readings instead.`;
}

/**
 * A change to one of Predbat's configuration files, which you make by hand: where, the exact lines, why, and how Joule
 * will confirm it. embedded drops the card frame and title for an inbox row.
 */
export function ConfigFileChangeCard({
  investigation,
  change,
  api,
  mutate,
  onSource,
  embedded = false,
}: {
  investigation: Investigation;
  change: ConfigFileChange;
  api: Api;
  mutate: Mutate;
  onSource?: (id: string) => void;
  embedded?: boolean;
}) {
  const titleId = useId();
  const [copied, setCopied] = useState<"yes" | "no" | "">("");
  useEffect(() => {
    if (!copied) return;
    const timer = setTimeout(() => setCopied(""), 2500);
    return () => clearTimeout(timer);
  }, [copied]);
  const verification = useVerificationNote(change);
  const open = isOpenConfigFileChange(change);
  const redacted = isRedactedSnippet(change);
  const base = `/investigations/${encodeURIComponent(investigation.id)}/filechanges/${encodeURIComponent(change.id)}`;
  return (
    <article
      className={`config-change${embedded ? " is-embedded" : ""}${open ? "" : " is-closed"}`}
      aria-labelledby={titleId}
    >
      {!embedded && (
        <div className="config-change-meta">
          <span>
            <FileCode2 size={14} aria-hidden="true" />
            File edit · {statusText(change)}
          </span>
          <time dateTime={investigation.at}>{dayTime(investigation.at)}</time>
        </div>
      )}
      <h3 id={titleId} className={embedded ? "sr-only" : undefined}>
        <RichText text={change.summary} />
      </h3>
      <p className="config-change-location">
        <span>Where</span>
        <code>{change.file}</code>
        <span className="config-change-place">
          <RichText text={change.location} />
        </span>
      </p>
      <SnippetDiff change={change} />
      {redacted && open && (
        <p className="config-change-hidden">
          Part of this edit was hidden for safety. Open {change.file || "apps.yaml"} and replace the values by hand.
        </p>
      )}
      <div className="config-change-reason">
        <InvestigationText text={change.reason} />
      </div>
      {open && (
        <p className="config-change-note">
          {change.status === "applied" && change.appliedAt
            ? `You marked this applied ${dayTime(change.appliedAt)}. ${verification}`
            : verification}
        </p>
      )}
      {!open && (change.closedAt || change.decisionNote) && (
        <p className="config-change-note">
          {change.closedAt ? `Closed ${dayTime(change.closedAt)}` : "Closed"}
          {change.decisionNote ? ` · Your note: “${change.decisionNote}”` : ""}
        </p>
      )}
      <RecommendationReply
        title={plainText(change.summary)}
        thread={change.thread}
        open={open}
        replyPath={`${base}/reply`}
        dismissPath={`${base}/dismiss`}
        api={api}
        mutate={mutate}
        target="file"
        quickReplies={change.status === "pending"}
        actionsBefore={
          <>
            {open && !redacted && (
              <Button
                size="sm"
                variant={change.status === "pending" ? "primary" : "secondary"}
                aria-describedby={titleId}
                onClick={async () => setCopied((await copyText(change.snippet)) ? "yes" : "no")}
              >
                {copied === "yes" ? <Check size={14} aria-hidden="true" /> : <Copy size={14} aria-hidden="true" />}
                {copied === "yes" ? "Copied" : copied === "no" ? "Copy failed" : "Copy snippet"}
              </Button>
            )}
            {change.status === "pending" && (
              <Button
                size="sm"
                variant={redacted ? "primary" : "secondary"}
                aria-describedby={titleId}
                onClick={() => void mutate(`${base}/applied`, {}, "Marked as applied. The next check confirms it.")}
              >
                Mark as applied
              </Button>
            )}
            {change.status === "applied" && (
              <Button
                size="sm"
                variant="secondary"
                aria-describedby={titleId}
                aria-label="Undo marking this applied"
                onClick={() => void mutate(`${base}/unapply`, {}, "Back to waiting for you.")}
              >
                Undo
              </Button>
            )}
            {!open && (
              <Button
                size="sm"
                variant="secondary"
                aria-describedby={titleId}
                onClick={() => void mutate(`${base}/reopen`, {}, "Back on your list.")}
              >
                Reopen
              </Button>
            )}
          </>
        }
        actionsAfter={
          onSource && (
            <Button variant="link" size="sm" onClick={() => onSource(investigation.id)} aria-describedby={titleId}>
              Open the check
            </Button>
          )
        }
      />
    </article>
  );
}
