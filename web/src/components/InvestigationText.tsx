import { useContext, useId, useMemo, useState, type ReactNode } from "react";
import { ChevronDown, ChevronUp } from "lucide-react";
import { Button, Chip } from "./ui";
import type { Investigation } from "../types";
import "./InvestigationEvidence.css";
import { AppContext } from "../context/AppContext";
import { richParts, type RichPart } from "../lib/richText";
import { nameBook, verdictLook, type NameBook, type VerdictView } from "../lib/insights";
import { metricLabel } from "../lib/labels";

export { investigationVerdict, verdictLabels, type VerdictView } from "../lib/insights";

/** Friendly names for the entity ids and setting keys AI text mentions, from the settings and meter mappings on screen. */
function useNameBook(): NameBook | undefined {
  const app = useContext(AppContext);
  const settings = app?.data.state.settings,
    mappings = app?.measured.telemetry?.entityMappings;
  return useMemo(
    () => (settings || mappings ? nameBook(settings ?? [], mappings ?? {}, metricLabel) : undefined),
    [settings, mappings],
  );
}

function Parts({ parts }: { parts: RichPart[] }): ReactNode {
  return parts.map((p, i) => {
    switch (p.kind) {
      case "strong":
        return (
          <strong key={i}>
            <Parts parts={p.parts} />
          </strong>
        );
      case "code":
        return (
          <code key={i} className="inline-code">
            {p.value}
          </code>
        );
      case "name":
        return (
          <abbr key={i} className="entity-name" title={p.id} data-raw>
            {p.value}
          </abbr>
        );
      case "term":
        return (
          <abbr key={i} className={`plan-term tone-${p.tone}`} title={p.title} data-raw>
            {p.value}
          </abbr>
        );
      default:
        return p.value;
    }
  });
}

/** One line of AI text, richly: bold, code, Predbat terms in plain words (code in the tooltip) and friendly names. */
export function RichText({ text }: { text: string | null | undefined }) {
  const book = useNameBook();
  const parts = useMemo(() => richParts(text, book), [text, book]);
  return <Parts parts={parts} />;
}

type Block =
  { kind: "paragraph" | "heading"; text: string } | { kind: "list"; items: string[]; ordered: boolean; start: number };

// Segment for presentation only: never paraphrase, infer headings, or add claims.
function paragraphs(text: string): string[] {
  const sentences = Array.from(new Intl.Segmenter("en", { granularity: "sentence" }).segment(text), (s) => s.segment);
  const result: string[] = [];
  let current = "";
  let count = 0;
  for (const sentence of sentences) {
    if (current && (count >= 2 || current.length + sentence.length > 520)) {
      result.push(current.trim());
      current = "";
      count = 0;
    }
    current += sentence;
    count++;
  }
  if (current.trim()) result.push(current.trim());
  return result;
}

function blocks(text: string): Block[] {
  const result: Block[] = [];
  let prose: string[] = [];
  function flush() {
    if (prose.length) result.push(...paragraphs(prose.join(" ")).map((text) => ({ kind: "paragraph" as const, text })));
    prose = [];
  }
  for (const line of text.replaceAll("\r\n", "\n").split("\n")) {
    const trimmed = line.trim();
    if (!trimmed) {
      flush();
      continue;
    }
    const heading = /^#{1,4}\s+(.+)$/.exec(trimmed);
    const item = /^(?:([-*•])\s+|(\d+)[.)]\s+)(.+)$/.exec(trimmed);
    if (heading) {
      flush();
      result.push({ kind: "heading", text: heading[1] });
    } else if (item) {
      flush();
      const ordered = Boolean(item[2]);
      const last = result.at(-1);
      if (last?.kind === "list" && last.ordered === ordered) last.items.push(item[3]);
      else result.push({ kind: "list", items: [item[3]], ordered, start: Number(item[2] || 1) });
    } else prose.push(trimmed);
  }
  flush();
  return result;
}

export function InvestigationText({ text }: { text: string | null | undefined }) {
  return (
    <div className="investigation-text">
      {blocks(text ?? "").map((block, index) => {
        if (block.kind === "heading")
          return (
            <h4 key={index}>
              <RichText text={block.text} />
            </h4>
          );
        if (block.kind === "list") {
          const items = block.items.map((item, n) => (
            <li key={n}>
              <RichText text={item} />
            </li>
          ));
          return block.ordered ? (
            <ol key={index} start={block.start}>
              {items}
            </ol>
          ) : (
            <ul key={index}>{items}</ul>
          );
        }
        return (
          <p key={index}>
            <RichText text={block.text} />
          </p>
        );
      })}
    </div>
  );
}

function preview(text: string, limit: number) {
  if (text.length <= limit) return text;
  const candidate = text.slice(0, limit);
  const sentenceEnd = Array.from(new Intl.Segmenter("en", { granularity: "sentence" }).segment(candidate))
    .filter((s) => /[.!?][\s\u201d\u2019"')]*$/.test(s.segment))
    .at(-1);
  const end = sentenceEnd ? sentenceEnd.index + sentenceEnd.segment.length : candidate.lastIndexOf(" ");
  return `${text.slice(0, end > limit / 2 ? end : limit).trimEnd()}…`;
}

/** A bounded stored-summary preview; expansion reveals the original answer. */
export function InvestigationSummary({
  text: raw,
  previewCharacters = 420,
}: {
  text: string | null | undefined;
  previewCharacters?: number;
}) {
  const text = raw ?? "";
  const [expanded, setExpanded] = useState(false);
  const id = useId();
  const long = text.length > previewCharacters;
  return (
    <div className="investigation-summary">
      <div id={id}>
        <InvestigationText text={long && !expanded ? preview(text, previewCharacters) : text} />
      </div>
      {long && (
        <Button
          type="button"
          variant="ghost"
          className="investigation-expand"
          aria-expanded={expanded}
          aria-controls={id}
          onClick={() => setExpanded(!expanded)}
        >
          {expanded ? "Show less" : "Read full summary"}
          {expanded ? <ChevronUp size={15} aria-hidden="true" /> : <ChevronDown size={15} aria-hidden="true" />}
        </Button>
      )}
    </div>
  );
}

/** A check's verdict in sentence case: problem amber, opportunity green, didn't finish and nothing new grey. */
export function VerdictBadge({
  verdict,
  investigation,
}: {
  verdict: VerdictView;
  /** Pass the record when you have it, so a minor problem reads "Minor" rather than "Problem". */
  investigation?: Pick<Investigation, "verdict" | "status" | "severity">;
}) {
  const look = investigation
    ? verdictLook(investigation)
    : verdictLook({
        verdict: verdict === "didnt_finish" || verdict === "running" ? null : verdict,
        status: verdict === "didnt_finish" ? "Failed" : verdict === "running" ? "Running" : "Completed",
      });
  return (
    <Chip tone={look.tone} dot className="verdict-badge">
      {look.label}
    </Chip>
  );
}
