import { planAction } from "./planActions";
import { clock, dayTime } from "./time";

/**
 * A client-side safety net for text that comes from the server or the AI: nothing internal reaches the screen raw.
 * The server is the first line (it normalises Predbat codes and writes local times); this catches whatever slips past.
 *
 *   Predbat plan codes   "FrzExp", "HoldChrg", "Chrg 70%"  → their plain labels ("Export solar, don't charge battery…")
 *   "Predbat state X"    → "Other Predbat state"
 *   UTC timestamps       "2026-10-05T01:00:00Z", "2026-10-04 12:50:27Z", "2026-10-05 04:16 UTC" → "Today 02:00"
 *   Markdown backticks   "`load_scaling`" → load_scaling (entity ids become code chips in <PlainText>)
 *   Brand and jargon     "ChatGpt" → "ChatGPT", "telemetry" → "data" ("solar telemetry remains unavailable" reads "solar data…")
 */

/** Predbat's plan state codes (and the AI's common misspellings), longest first so "FrzChrg" wins over "Chrg". */
const CODES = ["FrzChrg", "FrzChg", "HoldChrg", "HoldChg", "NoChrg", "FrzExp", "HoldExp", "Chrg", "Exp"];
const codePattern = new RegExp(`(?<![\\w.])(${CODES.join("|")})(?:\\s*(\\d{1,3})\\s*%)?(?![\\w])`, "g");
/** Codes that are also ordinary English or likely in other contexts; only replaced when they look like a state. */
const AMBIGUOUS = new Set(["Exp"]);

function labelFor(code: string) {
  const canonical = code === "FrzChg" ? "FrzChrg" : code === "HoldChg" ? "HoldChrg" : code;
  return planAction(canonical).label;
}

const isoUtc = /\b(\d{4}-\d{2}-\d{2})[T ](\d{2}:\d{2})(?::(\d{2})(?:\.\d+)?)?\s*(?:Z|UTC|GMT|\+00:00)(?![\w])/g;

export function plainText(input: string | null | undefined): string {
  let text = input ?? "";
  if (!text) return text;
  // Predbat's own label for codes it doesn't recognise.
  text = text.replace(/\bPredbat state ([A-Za-z][\w/]*)/g, "Other Predbat state ($1)");
  // UTC timestamps in the household's time.
  text = text.replace(isoUtc, (_m, day: string, hm: string, s?: string) => {
    const at = new Date(`${day}T${hm}:${s ?? "00"}Z`);
    return Number.isFinite(at.getTime()) ? dayTime(at) : _m;
  });
  // A bare "04:16 UTC": read as today's date in UTC and shown as the local time.
  text = text.replace(/\b(\d{1,2}):(\d{2})\s*UTC\b/g, (m, h: string, min: string) => {
    const at = new Date();
    at.setUTCHours(Number(h), Number(min), 0, 0);
    return Number.isFinite(at.getTime()) ? clock(at) : m;
  });
  // Plan codes, with an optional target ("Chrg 70%").
  text = text.replace(codePattern, (m, code: string, target?: string, offset?: number, whole?: string) => {
    if (AMBIGUOUS.has(code) && !target) {
      // "Exp" on its own: only when it is clearly a state, e.g. "in Exp", "Exp slot", "Exp/Chrg".
      const around = (whole ?? "").slice(Math.max(0, (offset ?? 0) - 12), (offset ?? 0) + m.length + 8);
      if (!/(in|to|as|state|mode|slot|\/)\s*Exp|Exp\s*(slot|state|mode|\/)/.test(around)) return m;
    }
    return target ? `${labelFor(code)} (to ${target}%)` : labelFor(code);
  });
  text = text.replace(/\bChatGpt\b/g, "ChatGPT");
  text = text.replace(/\bTelemetry\b/g, "Data").replace(/\btelemetry\b/g, "data");
  return text;
}

/** An entity or setting id that should read as code: sensor.x, select.y, switch.z, input_number.w… */
export const entityIdPattern =
  /\b(?:sensor|binary_sensor|select|switch|input_number|input_boolean|input_select|number|predbat|button|automation|script)\.[a-z0-9_]+\b/g;

/** Entity ids, and snake_case keys such as export_today or load_scaling. */
const identifierPattern = new RegExp(
  `${entityIdPattern.source}|(?<![\\w./-])[a-z][a-z0-9]*(?:_[a-z0-9]+)+(?![\\w/-])`,
  "g",
);

export interface TextPart {
  kind: "text" | "code";
  value: string;
}

/** Splits text into plain parts and code parts: `backticked` spans and entity ids. Applies plainText to the prose. */
export function textParts(input: string | null | undefined): TextPart[] {
  const raw = input ?? "";
  const parts: TextPart[] = [];
  const push = (kind: TextPart["kind"], value: string) => {
    if (!value) return;
    const last = parts.at(-1);
    if (last && last.kind === kind && kind === "text") last.value += value;
    else parts.push({ kind, value });
  };
  // Backticked spans first: keep their content as code if it is an identifier, otherwise as plain words.
  const segments = raw.split(/`([^`\n]+)`/g);
  segments.forEach((segment, i) => {
    if (i % 2 === 1) {
      const inner = segment.trim();
      if (/^[A-Za-z_][\w.:-]*$/.test(inner) && /[._]/.test(inner) && !CODES.includes(inner)) push("code", inner);
      else push("text", plainText(inner));
      return;
    }
    const prose = plainText(segment.replace(/`/g, ""));
    let last = 0;
    for (const m of prose.matchAll(identifierPattern)) {
      push("text", prose.slice(last, m.index));
      push("code", m[0]);
      last = (m.index ?? 0) + m[0].length;
    }
    push("text", prose.slice(last));
  });
  return parts;
}
