import { Hint } from "../Hint";
import { Chip } from "../ui";
import { gbp, kwh, percent } from "../../lib/format";
import { clock } from "../../lib/time";
import { ActionMeaning, iconFor } from "./ActionBadge";
import { priceRange, shownCost, tradeNote, windowTagLabels, windowTitle, windowWhen, type PlanWindow } from "./windows";

/** "84% → 75%". */
export const socText = (w: { soc: { start: number | null; end: number | null } }) =>
  w.soc.start != null && w.soc.end != null ? `${percent(w.soc.start)} → ${percent(w.soc.end)}` : "";

/**
 * "≈£1.20" (a cost) or "earns ≈£1.26": Predbat's planned money for the window, when it is worth mentioning. `cost` is
 * the figure the window shows (see shownCost); `partial` adds "still to come" for the window in progress.
 */
export function moneyText(w: Pick<PlanWindow, "cost">, cost: number | null = w.cost, partial = false) {
  if (cost == null || Math.abs(cost) < 0.05) return "";
  const text = cost < 0 ? `earns ≈${gbp(-cost)}` : `≈${gbp(cost)}`;
  return partial ? `${text} still to come` : text;
}

/** The same money in words, for the line under a window's title: "about £1.36 from the grid", "earns about £1.98". */
export function moneyWords(cost: number | null, partial = false) {
  if (cost == null || Math.abs(cost) < 0.05) return "";
  if (cost < 0) return `earns about ${gbp(-cost)}${partial ? " more" : ""}`;
  return `about ${gbp(cost)}${partial ? " more" : ""} from the grid`;
}

/** "now 7% → 4%" for the window in progress when the battery's measured level is known, else "84% → 75%". */
export function socNowText(w: Pick<PlanWindow, "phase" | "soc">, liveSoc?: number | null) {
  if (w.phase === "current" && liveSoc != null && Number.isFinite(liveSoc) && w.soc.end != null)
    return `now ${percent(liveSoc)} → ${percent(w.soc.end)}`;
  return socText(w);
}

/**
 * Where the battery's energy goes in a window with no price to show: "1.9 kWh from battery" when it powers the home,
 * "5.0 kWh into the battery" when solar fills it during a "power your home" window. Empty when it barely moves.
 */
export function batteryFlowText(w: Pick<PlanWindow, "kwh">) {
  if (w.kwh == null || Math.abs(w.kwh) < 0.1) return "";
  return w.kwh < 0 ? `${kwh(-w.kwh)} from battery` : `${kwh(w.kwh)} into the battery`;
}

/**
 * One line of notes under a window: trades, pauses, reserve. `reserve` is Predbat's minimum battery level (%); without it
 * a level of 6% or less counts as the reserve. `cost` is the figure the window shows beside the notes (see shownCost),
 * so the notes never repeat or contradict it.
 */
export function windowNotes(w: PlanWindow, timeZone?: string, reserve?: number | null, cost: number | null = w.cost) {
  const notes: string[] = [];
  if (w.trades) notes.push(tradeNote(w.trades, timeZone));
  if (w.pauses.length)
    notes.push(
      `Pauses ${w.pauses.map((p) => `${clock(p.start, { timeZone })}–${clock(p.end, { timeZone })}`).join(", ")} to power the home`,
    );
  if (w.atReserve) notes.push("The battery is already at its reserve, so nothing will actually be exported.");
  const floor = reserve != null && Number.isFinite(reserve) ? reserve + 1 : 6;
  if (w.key === "demand" && w.soc.end != null && cost != null && cost >= 0.05 && w.soc.end <= floor)
    notes.push("The battery reaches its reserve; the grid covers the rest.");
  return notes;
}

/**
 * The plan's windows as a calm list: icon, what happens and when, the price that matters (or, for home use, where the
 * battery level goes), and any notes. Used on Today ("Coming up").
 */
export function WindowList({
  windows,
  now,
  timeZone,
  stale = false,
  reserve,
  liveSoc,
}: {
  windows: PlanWindow[];
  now: number;
  timeZone?: string;
  /** The battery's measured level now (%): the window in progress reads "now 7% → 4%". */
  liveSoc?: number | null;
  /** Predbat's minimum battery level (%), for the "reaches its reserve" note. */
  reserve?: number | null;
  /** The plan is out of date: rows are greyed. */
  stale?: boolean;
}) {
  return (
    <ul className={`window-list${stale ? " is-stale" : ""}`} aria-label="Plan windows">
      {windows.map((w) => {
        const Icon = iconFor(w.action);
        const title = windowTitle(w);
        const showPrice = w.key !== "demand" && w.key !== "no-charge" && w.price;
        const shown = shownCost(w, now);
        const notes = windowNotes(w, timeZone, reserve, shown.cost);
        const flow = batteryFlowText(w);
        const money = moneyWords(shown.cost, shown.partial);
        const soc = socNowText(w, liveSoc);
        return (
          <li key={w.id} className={`window-item${w.phase === "current" ? " is-now" : ""}`} data-action={w.key}>
            <span className={`window-glyph tone-${w.tone}`} aria-hidden="true">
              <Icon size={17} />
            </span>
            <div className="window-main">
              <div className="window-title">
                <strong>{title}</strong>
                <Hint label={`What does “${title}” mean?`}>
                  <ActionMeaning action={w.action} />
                </Hint>
                {w.tags
                  .filter((t) => t !== "estimated")
                  .map((t) => (
                    <Chip key={t} tone="success" className="window-tag">
                      {windowTagLabels[t]}
                    </Chip>
                  ))}
              </div>
              <span className="window-when">
                {w.phase === "current" && <span className="now-dot" aria-hidden="true" />}
                {windowWhen(w.start, w.end, now, timeZone)}
                {money && <span className="window-money"> · {money}</span>}
              </span>
            </div>
            <div className="window-figures">
              {showPrice ? (
                <strong>
                  {priceRange(w.price)}{" "}
                  <small>
                    {w.rate === "export" ? "export" : "import"}
                    {w.tags.includes("estimated") ? ", estimated" : ""}
                  </small>
                </strong>
              ) : (
                <strong>
                  {soc || "—"} <small>battery</small>
                </strong>
              )}
              {showPrice && soc && <small className="window-soc">{soc}</small>}
              {!showPrice && flow && <small className="window-soc">{flow}</small>}
            </div>
            {notes.length > 0 && (
              <div className="window-notes">
                {notes.map((n) => (
                  <span key={n} className="window-note">
                    {n}
                  </span>
                ))}
              </div>
            )}
          </li>
        );
      })}
    </ul>
  );
}
