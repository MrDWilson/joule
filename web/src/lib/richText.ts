/**
 * AI text as structured parts for rich rendering: **bold**, `code`, Predbat's plan codes and jargon as plain terms with the
 * original in a tooltip (FrzExp → "Export solar, don't charge battery"; SoC → "battery level"), and entity ids or setting keys
 * as their friendly names when Joule knows them. Everything else goes through plainText (UTC times, brand names).
 * Presentation only: no words are added or dropped beyond these substitutions.
 */
import { planAction, type PlanActionTone } from "./planActions";
import { entityIdPattern, plainText } from "./sanitize";
import type { NameBook } from "./insights";

export type RichPart =
  | { kind: "text"; value: string }
  | { kind: "strong"; parts: RichPart[] }
  | { kind: "code"; value: string }
  /** A known entity id or setting key shown by its friendly name; id is what the text said. */
  | { kind: "name"; value: string; id: string }
  /** A Predbat code or jargon word shown in plain words; title explains it and names the original. */
  | { kind: "term"; value: string; title: string; code: string; tone: PlanActionTone | "neutral" };

const CODES = ["FrzChrg", "FrzChg", "HoldChrg", "HoldChg", "NoChrg", "NoChg", "FrzExp", "HoldExp", "Chrg", "Exp"];
const canonical: Record<string, string> = { FrzChg: "FrzChrg", HoldChg: "HoldChrg", NoChg: "NoChrg" };
const JARGON: Record<string, { label: string; title: string }> = {
  SoC: { label: "battery level", title: "SoC: state of charge, how full the battery is" },
  SOC: { label: "battery level", title: "SoC: state of charge, how full the battery is" },
  PV: { label: "solar", title: "PV: photovoltaic, your solar panels" },
};

/** Codes (with an optional target, "Chrg 70%"), "Demand slot(s)/mode" or "showed Demand", SoC and PV as standalone words. */
const termPattern = new RegExp(
  [
    `(?<![\\w.])(${CODES.join("|")})(?:\\s*(\\d{1,3})\\s*%)?(?![\\w])`,
    // "Demand slot(s)/mode…", or "showed/in/to Demand" as a state (but not "in Demand is…", "to Demand response").
    `\\b(Demand)(?:(?=\\s+(?:slots?|mode|operation|state|periods?|windows?)\\b)|(?<=\\b(?:showed|shows|show|showing|in|into|to|from)\\s+Demand)(?![\\w-]|\\s+(?:is|was|were|for|of|charges?|tariffs?|rates?|response|side)\\b))`,
    `(?<![\\w.])(SoC|SOC|PV)(?![\\w.])`,
  ].join("|"),
  "g",
);
const identifierPattern = new RegExp(
  `${entityIdPattern.source}|(?<![\\w./-])[a-z][a-z0-9]*(?:_[a-z0-9]+)+(?![\\w/-])`,
  "g",
);

function startsSentence(before: string) {
  return before.trim() === "" || /[.!?:]\s*$/.test(before);
}
const capitalise = (s: string) => (s ? s[0].toUpperCase() + s.slice(1) : s);

function identifier(id: string, book?: NameBook): RichPart {
  const name = book?.names.get(id);
  return name ? { kind: "name", value: name, id } : { kind: "code", value: id };
}

function pushText(parts: RichPart[], value: string) {
  if (!value) return;
  const last = parts.at(-1);
  if (last?.kind === "text") last.value += value;
  else parts.push({ kind: "text", value });
}

/** Plain prose: plainText for times and brands, then identifiers as names or code. */
function prose(parts: RichPart[], raw: string, book?: NameBook) {
  const text = plainText(raw);
  let last = 0;
  for (const m of text.matchAll(identifierPattern)) {
    pushText(parts, text.slice(last, m.index));
    parts.push(identifier(m[0], book));
    last = (m.index ?? 0) + m[0].length;
  }
  pushText(parts, text.slice(last));
}

/** Prose with Predbat terms picked out first (so they keep their tooltip), the rest through prose(). */
function withTerms(parts: RichPart[], text: string, book?: NameBook) {
  let last = 0;
  for (const m of text.matchAll(termPattern)) {
    const index = m.index ?? 0;
    const [whole, code, target, demand, jargon] = m;
    const before = text.slice(last, index);
    if (code === "Exp" && !target) {
      // "Exp" on its own is only a state when the words around it say so ("in Exp", "Exp slot", "Exp/Chrg").
      const around = text.slice(Math.max(0, index - 12), index + whole.length + 8);
      if (!/(in|to|as|state|mode|slot|\/)\s*Exp|Exp\s*(slot|state|mode|\/)/.test(around)) continue;
    }
    prose(parts, before, book);
    const sentence = startsSentence(text.slice(0, index));
    if (jargon) {
      const j = JARGON[jargon];
      parts.push({
        kind: "term",
        value: sentence ? capitalise(j.label) : j.label,
        title: j.title,
        code: jargon,
        tone: "neutral",
      });
    } else {
      const raw = code ?? demand;
      const action = planAction(canonical[raw] ?? raw);
      const label = target ? `${action.label} (to ${target}%)` : action.label;
      parts.push({
        kind: "term",
        value: label,
        title: `Predbat: ${raw}${target ? ` ${target}%` : ""}. ${action.description}`.trim(),
        code: raw,
        tone: action.tone,
      });
    }
    last = index + whole.length;
  }
  prose(parts, text.slice(last), book);
}

/** One line or paragraph of AI text as parts. */
export function richParts(input: string | null | undefined, book?: NameBook): RichPart[] {
  const raw = input ?? "";
  const parts: RichPart[] = [];
  raw.split(/\*\*([^*\n]+?)\*\*/g).forEach((segment, i) => {
    if (i % 2 === 1) {
      parts.push({ kind: "strong", parts: inline(segment, book) });
      return;
    }
    parts.push(...inline(segment, book));
  });
  // Merge neighbouring text parts from separate segments.
  return parts.reduce<RichPart[]>((out, p) => {
    if (p.kind === "text") pushText(out, p.value);
    else out.push(p);
    return out;
  }, []);
}

function inline(segment: string, book?: NameBook): RichPart[] {
  const parts: RichPart[] = [];
  segment.split(/`([^`\n]+)`/g).forEach((piece, i) => {
    if (i % 2 === 1) {
      const inner = piece.trim();
      if (CODES.includes(inner) || JARGON[inner]) withTerms(parts, inner, book);
      else if (/^[A-Za-z_][\w.:-]*$/.test(inner) && /[._]/.test(inner)) parts.push(identifier(inner, book));
      else parts.push({ kind: "code", value: inner });
      return;
    }
    withTerms(parts, piece.replace(/`/g, ""), book);
  });
  return parts;
}

/** The parts as plain words (for accessible names, previews and tests). */
export function richToText(parts: RichPart[]): string {
  return parts.map((p) => (p.kind === "strong" ? richToText(p.parts) : p.value)).join("");
}
