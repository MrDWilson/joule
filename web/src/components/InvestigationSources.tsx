import { useState } from "react";
import { Database, FileText, ScrollText, Settings2, Sparkles } from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { Button } from "./ui";
import type { Investigation } from "../types";
import type { Api, ToolEvidence } from "../completion-types";
import { stepLabel } from "../lib/insights";
import { dayTime } from "../lib/time";

function readable(value: unknown): string {
  if (value == null) return "—";
  if (Array.isArray(value)) return value.map(readable).join("; ");
  if (typeof value === "object")
    return Object.entries(value as Record<string, unknown>)
      .map(([key, v]) => `${key.replaceAll("_", " ")}: ${readable(v)}`)
      .join(" · ");
  return String(value);
}
function Samples({ tool }: { tool: ToolEvidence }) {
  let result: unknown;
  try {
    result = JSON.parse(tool.resultJson);
  } catch {
    return <p className="muted">No table of results to show.</p>;
  }
  const object =
    result && typeof result === "object" && !Array.isArray(result) ? (result as Record<string, unknown>) : null;
  const candidate = Array.isArray(result) ? result : object ? Object.values(object).find(Array.isArray) : null;
  const rows = Array.isArray(candidate)
    ? candidate
        .slice(0, 10)
        .filter((r): r is Record<string, unknown> => !!r && typeof r === "object" && !Array.isArray(r))
    : [];
  const keys = Array.from(new Set(rows.flatMap(Object.keys))).slice(0, 10);
  if (rows.length)
    return (
      <>
        <p className="muted">First {rows.length} rows, as they were when read.</p>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                {keys.map((k) => (
                  <th key={k}>{k.replaceAll("_", " ")}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((row, n) => (
                <tr key={n}>
                  {keys.map((k) => (
                    <td key={k}>{readable(row[k])}</td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </>
    );
  if (Array.isArray(candidate) && candidate.length && candidate.every((x) => typeof x === "string"))
    return (
      <pre className="source-lines" tabIndex={0}>
        {(candidate as string[]).slice(0, 20).join("\n")}
      </pre>
    );
  if (object)
    return (
      <dl className="result-summary">
        {Object.entries(object)
          .slice(0, 12)
          .map(([k, v]) => (
            <div key={k}>
              <dt>{k.replaceAll("_", " ")}</dt>
              <dd>{readable(v)}</dd>
            </div>
          ))}
      </dl>
    );
  return <p className="body-copy">{readable(result)}</p>;
}

const KIND_ICON: Record<string, LucideIcon> = {
  mcp: ScrollText,
  plan_vs_actual: Database,
  summary: Database,
  query: Database,
  snapshots: Database,
  configuration: Settings2,
  documentation: FileText,
  model: Sparkles,
};
const BOOKKEEPING = new Set(["schema", "evidence"]);

/** What a source was, in words: the server's label, or the raw request translated. */
export function sourceLabel(tool: ToolEvidence) {
  if (tool.label) return tool.label;
  if (tool.kind === "model") return tool.success ? "The AI's answer" : "An AI answer that failed Joule's checks";
  return stepLabel(`${tool.kind}: ${tool.request}${tool.success ? "" : " (failed)"}`).label;
}

/**
 * The data a check read, fetched on demand: one labelled row per source, with what came back and the raw request
 * behind "Technical details". Schema look-ups and re-reads are bookkeeping and stay hidden unless asked for.
 * references limits the list to the sources one to-do or claim cites.
 */
export function EvidencePanel({
  api,
  id,
  references,
  highlight,
}: {
  api: Api;
  id: string;
  references?: string[];
  /** Evidence id to open and mark (an evidence point links to its source). */
  highlight?: string | null;
}) {
  const [record, setRecord] = useState<Investigation | null>(null),
    [loading, setLoading] = useState(false),
    [error, setError] = useState(""),
    [showAll, setShowAll] = useState(false);
  async function load() {
    setLoading(true);
    setError("");
    try {
      setRecord(await api<Investigation>(`/investigations/${encodeURIComponent(id)}`));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  }
  const tools =
    record?.toolEvidence?.filter(
      (tool) =>
        !references || references.includes(tool.id) || tool.sourceReferences.some((ref) => references.includes(ref.id)),
    ) ?? [];
  const main = tools.filter((t) => !BOOKKEEPING.has(t.kind));
  const extra = tools.length - main.length;
  const list = showAll ? tools : main;
  return (
    <details
      className="investigation-evidence"
      onToggle={(e) => {
        if (e.currentTarget.open && !record && !loading && !error) void load();
      }}
    >
      <summary>{references ? "Show the evidence for this" : "Sources"}</summary>
      {loading && <p role="status">Loading the sources…</p>}
      {error && (
        <div role="alert" className="callout">
          {error}{" "}
          <Button variant="secondary" size="sm" onClick={() => void load()}>
            Try again
          </Button>
        </div>
      )}
      {record &&
        (tools.length ? (
          <>
            <ul className="source-list">
              {list.map((tool) => {
                const Icon = KIND_ICON[tool.kind] ?? Database;
                return (
                  <li
                    key={tool.id}
                    id={`source-${tool.id}`}
                    className={tool.id === highlight ? "is-highlighted" : undefined}
                  >
                    <details className="tool-evidence" open={tool.id === highlight || undefined}>
                      <summary>
                        <Icon size={15} aria-hidden="true" />
                        <span className="source-label">{sourceLabel(tool)}</span>
                        <time dateTime={tool.retrievedAt}>{dayTime(tool.retrievedAt)}</time>
                      </summary>
                      {tool.error && (
                        <p role="alert" className="callout">
                          {tool.error}
                        </p>
                      )}
                      {!tool.success && tool.kind === "model" && (
                        <p className="callout">
                          This AI answer failed Joule's checks and was thrown out. It is kept only for troubleshooting.
                        </p>
                      )}
                      <Samples tool={tool} />
                      {tool.sourceReferences.map((ref) => (
                        <article className="citation" key={ref.id}>
                          <a href={ref.url} target="_blank" rel="noreferrer">
                            {ref.path} · lines {ref.startLine}–{ref.endLine} (opens in new tab)
                          </a>
                          <blockquote>{ref.excerpt}</blockquote>
                        </article>
                      ))}
                      <details className="technical" data-raw>
                        <summary>Technical details</summary>
                        <p className="mono-text">{tool.request}</p>
                        <p className="muted">Reference {tool.id}</p>
                      </details>
                    </details>
                  </li>
                );
              })}
            </ul>
            {extra > 0 && (
              <Button variant="ghost" size="sm" onClick={() => setShowAll(!showAll)}>
                {showAll ? "Hide bookkeeping steps" : `Show ${extra} bookkeeping ${extra === 1 ? "step" : "steps"}`}
              </Button>
            )}
          </>
        ) : (
          <p className="callout">
            {references
              ? "The evidence for this couldn't be found."
              : "No data was saved with this check, so its text can't be checked against evidence."}
          </p>
        ))}
    </details>
  );
}
