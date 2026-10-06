/**
 * Joule's vocabulary. One word for each idea, used everywhere (and mirrored in the server's own strings), so the same
 * thing is never called an investigation on one page, a review on another and an analysis on a third.
 *
 *   An AI run                      → a "check"            (not investigation / review / analysis / check-up)
 *   Something to act on            → a "suggestion" (a setting change), a "to-do" (something to look at yourself),
 *                                    or a "file edit" (a change to Predbat's configuration files)
 *   You said no                    → "Declined"           (not denied / dismissed / rejected)
 *   The AI dropped it              → "No longer needed"   (not retired / superseded / closed)
 *   The house's own consumption    → "Home use"           (not household load / house load / load)
 *   Joule (this app)               → "Joule"; "Predbat" only for the optimiser itself.
 */
export const words = {
  check: "check",
  checks: "checks",
  Check: "Check",
  Checks: "Checks",
  suggestion: "suggestion",
  suggestions: "suggestions",
  Suggestion: "Suggestion",
  Suggestions: "Suggestions",
  todo: "to-do",
  todos: "to-dos",
  fileEdit: "file edit",
  fileEdits: "file edits",
  declined: "Declined",
  noLongerNeeded: "No longer needed",
  homeUse: "Home use",
  /** One run of the AI ("An AI check is running", Setup › AI checks). */
  aiCheck: "AI check",
  aiChecks: "AI checks",
  /** The scheduler's look for anything new, without the AI. */
  quickCheck: "quick check",
  /** How a check ended: it found something to look at, nothing new, or it stopped part-way. */
  foundSomething: "Found something",
  nothingNew: "Nothing new",
  didntFinish: "Didn't finish",
} as const;

/** "1 check", "3 checks"; "1 to-do", "2 to-dos". */
export function plural(n: number, one: string, many = `${one}s`) {
  return `${n} ${n === 1 ? one : many}`;
}

export type Mode = "Monitor" | "Recommend" | "Auto";
/** The AI modes in plain words; the server keeps its own names. */
export const modes: Record<Mode, { label: string; description: string }> = {
  Monitor: { label: "Watch only", description: "Collect data and investigate. Never changes settings." },
  Recommend: { label: "Suggest changes", description: "Propose setting changes for you to approve." },
  Auto: { label: "Automatic", description: "Apply small changes you have allowed, within your limits." },
};
export const modeLabel = (m: string) => modes[m as Mode]?.label ?? m;
export const writesOff = "This install can't change Predbat's settings yet (live writes are off).";

/**
 * What Joule can actually do on this install, in one honest phrase: writes off means Joule only suggests and you make
 * the change in Predbat yourself.
 */
export function abilityLabel(mode: string, writesEnabled: boolean, demo: boolean) {
  if (mode === "Monitor") return "Watch only: Joule never changes Predbat";
  if (!writesEnabled && !demo) return "Read-only: Joule suggests, you change Predbat yourself";
  if (mode === "Auto") return "Automatic within the limits you set";
  return "Suggests changes and applies them when you approve";
}

/** Standing text for the disclaimer and first-run notice. */
export const disclaimer = {
  project: "Independent project. Works alongside Predbat; not affiliated with it.",
  ai: "AI suggestions can be wrong. Nothing changes without your approval.",
} as const;
