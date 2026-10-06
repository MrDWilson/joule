/**
 * Predbat plan states in plain English, driven by the one shared glossary (./predbat-states.json, a byte-for-byte copy of
 * src/Joule.Api/Knowledge/predbat-states.json — a backend unit test keeps them identical; run scripts/sync-glossary.sh).
 *
 * Predbat writes short codes into its plan ("Chrg", "FrzExp", "HoldChrg"…), decorated with arrows (↗ ↘ →, or the HTML
 * entities &nearr; &searr;), the forced-slot marker ⅎ, a car 🚗 for "hold for car", or a trailing target ("Chrg 70%").
 * Older stored plans carry those raw codes or "Charge"/"Export"; newer ones carry the canonical key ("freeze-export") plus
 * actionKey/actionLabel/targetPercent/reasonText from the server. Every place that shows an action goes through here.
 */
import glossary from "./predbat-states.json";

export type PlanActionKey = "demand" | "charge" | "freeze-charge" | "hold-charge" | "no-charge" | "export" | "freeze-export" | "hold-export" | "charge-export" | "unknown";
export type PlanActionTone = "neutral" | "blue" | "green" | "amber" | "violet";
/** Which price applies to the slot: what you pay to import, or what you are paid to export. */
export type PlanActionRate = "import" | "export";
/** How the battery level is expected to move during the action. */
export type SocDirection = "up" | "down" | "up-or-down" | "hold-or-up" | "hold-or-down" | "up-then-down" | "unknown";

export interface PlanAction {
  key: PlanActionKey;
  /** Short plain label, e.g. "Charge from the grid". Never a raw Predbat code. */
  label: string;
  /** One-line explanation for a tooltip or hint. */
  description: string;
  tone: PlanActionTone;
  rate: PlanActionRate;
  /** The code as stored, without decorations; useful in a tooltip ("Predbat: FrzExp"). */
  code: string;
  /** Glossary entry id ("hold-for-car", "freeze-export", "read-only"…), or "unknown". */
  id: string;
  /** What Predbat itself calls this state ("Freeze exporting"). */
  predbatName: string;
  /** Expected battery behaviour in one sentence. */
  battery: string;
  socDirection: SocDirection;
  mayCharge: boolean;
  mayDischarge: boolean;
  /** Target battery level parsed from the code ("Chrg 70%" → 70), when present. */
  target?: number;
}

type ActionKey = Exclude<PlanActionKey, "unknown">;
interface GlossaryAction { label: string; predbatName: string; description: string; battery: string; socDirection: string; mayCharge: boolean; mayDischarge: boolean; tone: string; rate: string }
interface GlossaryState { id: string; key: string | null; label?: string; description?: string; battery?: string; socDirection?: string; mayCharge?: boolean; mayDischarge?: boolean; codes: string[] }

const actions = glossary.actions as Record<ActionKey, GlossaryAction>;
const states = glossary.states as GlossaryState[];
const CAR = "\u{1F697}";
const fold = (code: string) => code.trim().replace(/\s+/g, " ").toLowerCase();

const byCode = new Map<string, GlossaryState>();
for (const state of states) {
  for (const code of state.codes) byCode.set(fold(code), state);
  if (!byCode.has(fold(state.id))) byCode.set(fold(state.id), state);
}

const chargeSide: string[] = ["charge", "freeze-charge", "hold-charge", "no-charge"];
const exportSide: string[] = ["export", "freeze-export", "hold-export"];

const arrows: Record<string, string> = { rarr: "→", larr: "←", uarr: "↑", darr: "↓", nearr: "↗", searr: "↘", nwarr: "↖", swarr: "↙", amp: "&", nbsp: " ", lt: "<", gt: ">", quot: '"' };
/** Decodes the HTML entities Predbat writes into plan text and drops table markup from split cells. */
export function decodePlanText(raw: string | null | undefined): string {
  return (raw ?? "")
    .replace(/&(#x[0-9a-f]+|#\d+|[a-z]+);/gi, (match, body: string) => {
      if (body[0] === "#") {
        const point = body[1] === "x" || body[1] === "X" ? parseInt(body.slice(2), 16) : parseInt(body.slice(1), 10);
        return Number.isFinite(point) && point > 0 && point <= 0x10ffff ? String.fromCodePoint(point) : match;
      }
      return arrows[body.toLowerCase()] ?? match;
    })
    .replace(/<[^>]*>/g, " ");
}

const decorations = /[←-⇿⬀-⯿ⅎ⚠✎*]|\u{FE0F}|\u{1F40C}/gu;
/** Strips Predbat's plan decorations: entities, arrows, the forced marker ⅎ, the snail, the car and surrounding whitespace. */
export function planActionCode(raw: string | null | undefined) {
  return decodePlanText(raw).replace(CAR, " ").replace(decorations, " ").replace(/\s+/g, " ").trim();
}

/** Mirrors PredbatGlossary.Combine: the two halves of a split slot as one action. */
export function combinePlanActions(first: string | null | undefined, second: string): string {
  if (!first || first === second || first === "demand") return second;
  if (second === "demand") return first;
  if (second === "export") return first === "charge" ? "charge-export" : chargeSide.includes(first) ? "export" : second;
  if (exportSide.includes(second) && chargeSide.includes(first)) return first;
  return second;
}

/** Mirrors PredbatGlossary.Find: exact code or status, then without decorations, status suffixes, targets, then as a split. */
function find(raw: string): GlossaryState | undefined {
  const text = decodePlanText(raw).replace(/\s+/g, " ").trim();
  if (!text) return undefined;
  const exact = byCode.get(fold(text));
  if (exact) return exact;
  if (text.includes(CAR) && planActionCode(text) === "") return byCode.get(fold(CAR));
  const stripped = planActionCode(text);
  const plain = byCode.get(fold(stripped));
  if (plain) return plain;
  const withoutBrackets = stripped.replace(/\s*\[(?:Alert|Manual SoC(?: Max)?)\]/gi, "").trim();
  const bracketed = byCode.get(fold(withoutBrackets));
  if (bracketed) return bracketed;
  const hold = /^(.+?),\s*Hold for (car|iBoost)$/i.exec(withoutBrackets);
  if (hold) {
    const head = byCode.get(fold(hold[1]));
    if (head) return head.key === "demand" ? byCode.get(fold(`Hold for ${hold[2]}`)) : head;
  }
  const withoutTarget = withoutBrackets.replace(/\s*\d+(?:\.\d+)?\s*%?$/, "").trim();
  if (withoutTarget !== withoutBrackets) {
    const targeted = byCode.get(fold(withoutTarget));
    if (targeted) return targeted;
  }
  const parts = withoutTarget.split("/").map((part) => part.trim()).filter(Boolean);
  if (parts.length === 2) {
    const [a, b] = parts.map((part) => byCode.get(fold(part)));
    if (a?.key && b?.key) return states.find((state) => state.id === combinePlanActions(a.key, b.key!));
  }
  return undefined;
}

function fromState(state: GlossaryState, code: string): PlanAction {
  const action = state.key ? actions[state.key as ActionKey] : undefined;
  return {
    key: (state.key ?? "unknown") as PlanActionKey,
    id: state.id,
    code,
    label: state.label ?? action?.label ?? state.id,
    description: state.description ?? action?.description ?? "",
    predbatName: action?.predbatName ?? state.codes[0] ?? code,
    battery: state.battery ?? action?.battery ?? "",
    socDirection: (state.socDirection ?? action?.socDirection ?? "unknown") as SocDirection,
    mayCharge: state.mayCharge ?? action?.mayCharge ?? true,
    mayDischarge: state.mayDischarge ?? action?.mayDischarge ?? true,
    tone: (action?.tone ?? "neutral") as PlanActionTone,
    rate: (action?.rate ?? "import") as PlanActionRate,
  };
}

/** Plain-English meaning of any Predbat plan code, status string or canonical key. Unrecognised codes read "Other Predbat state". */
export function planAction(raw: string | null | undefined): PlanAction {
  const code = planActionCode(raw);
  const state = find(raw ?? "");
  if (state) {
    const result = fromState(state, code || (raw ?? "").trim());
    const target = /(\d+(?:\.\d+)?)\s*%?\s*$/.exec(code);
    if (target && code !== state.id) result.target = Number(target[1]);
    return result;
  }
  return {
    key: "unknown", id: "unknown", code,
    label: code ? "Other Predbat state" : "Unknown",
    description: code ? `Predbat reported “${code}”, which Joule doesn't recognise yet.` : "Predbat didn't say what this slot does.",
    predbatName: code, battery: "", socDirection: "unknown", mayCharge: true, mayDischarge: true, tone: "neutral", rate: "import",
  };
}

/** The plan-slot fields this module reads; matches the server's PlanSlot (all detail fields optional for older plans). */
export interface PlanSlotAction {
  action: string;
  actionKey?: string | null;
  actionId?: string | null;
  actionLabel?: string | null;
  rawAction?: string | null;
  targetPercent?: number | null;
  reasonText?: string | null;
  splitTime?: string | null;
  secondaryAction?: string | null;
  override?: string | null;
}

/** A slot's action, preferring the server's normalised fields: key, composed label (split slots, car holds), target and reason. */
export function slotAction(slot: PlanSlotAction): PlanAction & { reason?: string; override?: string } {
  // Variants such as "hold for car" share a key with demand; the server's actionId keeps their own wording.
  const variant = slot.actionId ? states.find((state) => state.id === slot.actionId) : undefined;
  const base = variant ? fromState(variant, planActionCode(slot.rawAction ?? slot.action)) : planAction(slot.actionKey && slot.actionKey !== "unknown" ? slot.actionKey : slot.rawAction ?? slot.action);
  const result: PlanAction & { reason?: string; override?: string } = { ...base };
  if (slot.actionLabel) result.label = slot.actionLabel;
  if (typeof slot.targetPercent === "number") result.target = slot.targetPercent;
  if (slot.reasonText) result.reason = slot.reasonText;
  if (slot.override) result.override = slot.override;
  if (slot.rawAction) result.code = planActionCode(slot.rawAction);
  return result;
}

/** Matches Predbat's short state codes inside prose, for the InvestigationText safety net. */
export const PLAN_CODE_PATTERN = /(?<![A-Za-z0-9_/])(?:FrzChrg|FrzChg|HoldChrg|HoldChg|NoChrg|NoChg|FrzExp|HoldExp|Chrg|Exp)(?:\/(?:FrzChrg|FrzChg|HoldChrg|HoldChg|NoChrg|NoChg|FrzExp|HoldExp|Chrg|Exp))?(?![A-Za-z0-9_/])/g;

/** Splits prose into text and Predbat-code segments so a renderer can show the label with the code in a tooltip. */
export function planCodeSegments(text: string): Array<string | { code: string; action: PlanAction }> {
  const segments: Array<string | { code: string; action: PlanAction }> = [];
  let last = 0;
  for (const match of text.matchAll(PLAN_CODE_PATTERN)) {
    const index = match.index ?? 0;
    if (index > last) segments.push(text.slice(last, index));
    segments.push({ code: match[0], action: planAction(match[0]) });
    last = index + match[0].length;
  }
  if (last < text.length) segments.push(text.slice(last));
  return segments;
}

/** Replaces Predbat's short codes in prose with their plain labels (lower-cased mid-sentence). Mirrors PredbatGlossary.ReplaceCodes. */
export function replacePlanCodes(text: string): string {
  return text.replace(PLAN_CODE_PATTERN, (code: string, offset: number) => {
    const label = planAction(code).label;
    const before = text.slice(0, offset).trimEnd();
    return before === "" || /[.!?:]$/.test(before) ? label : label[0].toLowerCase() + label.slice(1);
  });
}

const rateTypes = glossary.rateTypes as Record<string, { label: string; estimated: boolean }>;
/** Plain explanation of how Predbat derived a price (import/exportRateType), or undefined for a published tariff price. */
export function rateTypeLabel(type: string | null | undefined): string | undefined {
  return type ? rateTypes[type]?.label : undefined;
}
/** True when Predbat estimated the price (future data, an offset, or copied from the previous day). */
export function rateEstimated(type: string | null | undefined): boolean {
  return !!type && !!rateTypes[type]?.estimated;
}

/** Every canonical action with its glossary meaning, for legends. */
export const planActionKeys = Object.keys(actions) as ActionKey[];
