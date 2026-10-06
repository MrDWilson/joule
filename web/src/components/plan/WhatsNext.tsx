import { Fragment, useState } from "react";
import { ChevronDown, ChevronRight } from "lucide-react";
import { Hint } from "../Hint";
import { Chip } from "../ui";
import { gbp, kwh, percent, pence } from "../../lib/format";
import { clock } from "../../lib/time";
import { useMediaQuery, breakpoints } from "../../lib/useMediaQuery";
import { ActionBadge } from "./ActionBadge";
import { moneyText, socNowText, windowNotes } from "./WindowList";
import {
  costFromNow,
  dayWord,
  priceRange,
  shortAction,
  shownCost,
  windowTagLabels,
  type PlanSlotLike,
  type PlanWindow,
} from "./windows";
import { useOverflow } from "./useOverflow";

export { costFromNow };
import { slotAction } from "../../lib/planActions";

const MIN = 60000;
const finite = (v: number | null | undefined): v is number => typeof v === "number" && Number.isFinite(v);

/** A price's place between the cheapest and dearest import price in view: 0 (cheapest) to 4 (dearest). */
export function priceBand(p: number | null | undefined, lo: number, hi: number) {
  if (!finite(p)) return null;
  if (hi - lo < 1) return 2;
  return Math.max(0, Math.min(4, Math.round(((p - lo) / (hi - lo)) * 4)));
}

/**
 * An export price's place among the export prices in view, 0 (least paid) to 4 (best paid). The sell scale is styled
 * neutral to green: a high export price pays well, so it must never borrow the import scale's amber.
 */
export const sellBand = priceBand;

/**
 * The time-weighted average of a window's price (import, or export for an export window), so a range such as 6.7–31.7p
 * that is mostly 31.73p is coloured as dear, not by its cheap end.
 */
export function averagePrice(slots: PlanSlotLike[], kind: "import" | "export") {
  let minutes = 0,
    sum = 0;
  for (const s of slots) {
    const v = kind === "export" ? s.exportRate : s.importRate;
    if (!finite(v)) continue;
    const d = s.durationMinutes || 30;
    minutes += d;
    sum += v * d;
  }
  return minutes ? sum / minutes : null;
}

/** The scales a price pill is graded on: import from cheapest to dearest, export from least to best paid. */
interface Scales {
  lo: number;
  hi: number;
  sellLo: number;
  sellHi: number;
}

/** A window's or half-hour's headline price: what you are paid for an export, otherwise what you pay to import. */
function PricePill({
  kind,
  text,
  value,
  scales,
}: {
  kind: "import" | "export";
  text: string;
  value: number | null;
  scales: Scales;
}) {
  if (!text) return <span className="muted">—</span>;
  const cls =
    kind === "export"
      ? `price-pill sell sell-${sellBand(value, scales.sellLo, scales.sellHi)}`
      : `price-pill band-${priceBand(value, scales.lo, scales.hi)}`;
  return <span className={cls}>{text}</span>;
}

/**
 * A window's headline price: what you are paid for an export window, otherwise what you pay to import. `value` is the
 * time-weighted average, which sets the colour.
 */
export function headlinePrice(w: Pick<PlanWindow, "rate" | "importPrice" | "exportPrice" | "slots">) {
  const kind = w.rate;
  return {
    kind,
    text: priceRange(kind === "export" ? w.exportPrice : w.importPrice),
    value: averagePrice(w.slots, kind),
  };
}

/**
 * A window's price as shown: the coloured pill, with "estimated" under it when Octopus hasn't published the rate yet. A
 * "power your home" window that costs nothing runs on the battery, so its import price is only what the grid would
 * cost: plain muted text, "if from grid", not a pill that looks like a charge.
 */
function WindowPrice({ w, cost, scales }: { w: PlanWindow; cost: number | null; scales: Scales }) {
  const price = headlinePrice(w);
  const estimated = w.tags.includes("estimated");
  if (w.key === "demand" && price.text && Math.abs(cost ?? 0) < 0.05)
    return (
      <span className="price-quiet">
        <span className="muted tabular">{price.text}</span>
        <small className="cell-sub">if from grid{estimated ? ", estimated" : ""}</small>
      </span>
    );
  return (
    <span className="price-cell">
      <PricePill {...price} scales={scales} />
      {(price.kind === "export" || estimated) && (
        <small className="cell-sub">
          {[price.kind === "export" ? "export price" : "", estimated ? "estimated" : ""].filter(Boolean).join(", ")}
        </small>
      )}
    </span>
  );
}

/** Local day of a window, for the sticky day headers: "Today", "Tomorrow", "Wed 7 Oct". */
const dayOf = (ms: number, now: number, timeZone?: string) => dayWord(ms, now, timeZone, { nights: false });

const range = (a: number, b: number, timeZone?: string) => `${clock(a, { timeZone })}–${clock(b, { timeZone })}`;

/**
 * "What's next": the plan's windows by day, each expanding to its half-hours, with the price coloured from cheap to
 * dear, the battery level, Predbat's planned cost and a running total. Future rows show the forecast only. On a phone
 * each window is a two-line card.
 */
export function WhatsNext({
  windows,
  now,
  timeZone,
  reserve,
  totalFrom = now,
  liveSoc,
}: {
  windows: PlanWindow[];
  now: number;
  timeZone?: string;
  /** Predbat's minimum battery level (%), for the "reaches its reserve" note. */
  reserve?: number | null;
  /** The running total counts cost from this moment (now; an earlier plan counts from its start). */
  totalFrom?: number;
  /** The battery's measured level now (%): the window in progress reads "now 7% → 4%". */
  liveSoc?: number | null;
}) {
  // Phones and tablets get cards: at 768px the table would hide Cost and Total behind a sideways scroll.
  const cards = useMediaQuery(breakpoints.tablet);
  const wrap = useOverflow<HTMLDivElement>();
  const [open, setOpen] = useState<Set<string>>(() => new Set());
  const toggle = (id: string) =>
    setOpen((s) => {
      const next = new Set(s);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  const prices = windows.flatMap((w) => w.slots.map((s) => s.importRate)).filter(finite);
  const sells = windows.flatMap((w) => w.slots.map((s) => s.exportRate)).filter(finite);
  const scales: Scales = {
    lo: prices.length ? Math.min(...prices) : 0,
    hi: prices.length ? Math.max(...prices) : 0,
    sellLo: sells.length ? Math.min(...sells) : 0,
    sellHi: sells.length ? Math.max(...sells) : 0,
  };
  const days: { day: string; windows: PlanWindow[] }[] = [];
  for (const w of windows) {
    const day = dayOf(Math.max(w.start, now), now, timeZone);
    const last = days.at(-1);
    if (last && last.day === day) last.windows.push(w);
    else days.push({ day, windows: [w] });
  }
  let running = 0;
  const totals = new Map(windows.map((w) => [w.id, (running += costFromNow(w.slots, totalFrom))]));
  if (!windows.length) return <p className="muted">Nothing planned yet.</p>;

  if (cards)
    return (
      <div className="next-cards">
        {days.map((d) => (
          <section key={d.day} aria-label={d.day}>
            <h3 className="day-head">{d.day}</h3>
            <ul>
              {d.windows.map((w) => {
                const expanded = open.has(w.id);
                const shown = shownCost(w, totalFrom);
                const soc = socNowText(w, liveSoc);
                const money = moneyText(w, shown.cost, shown.partial);
                return (
                  <li key={w.id} className={`next-card${w.phase === "current" ? " is-now" : ""}`}>
                    <div className="next-card-top">
                      <span className="next-card-line">
                        <span className="next-card-time">
                          {w.phase === "current"
                            ? `Now–${clock(w.end, { timeZone })}`
                            : range(w.start, w.end, timeZone)}
                        </span>
                        <ActionBadge action={w.action} text={w.short} tone={w.tone} />
                      </span>
                      {w.price && (
                        <span className="next-card-price">
                          <WindowPrice w={w} cost={shown.cost} scales={scales} />
                        </span>
                      )}
                    </div>
                    <div className="next-card-line muted">
                      {soc && <span>Battery {soc}</span>}
                      {money && <span>{money}</span>}
                      {w.tags
                        .filter((t) => t !== "estimated")
                        .map((t) => (
                          <span key={t}>{windowTagLabels[t]}</span>
                        ))}
                    </div>
                    {windowNotes(w, timeZone, reserve, shown.cost).map((n) => (
                      <p key={n} className="window-note">
                        {n}
                      </p>
                    ))}
                    {w.slots.length > 1 && (
                      <button
                        type="button"
                        className="expand-button"
                        aria-expanded={expanded}
                        onClick={() => toggle(w.id)}
                      >
                        {expanded ? (
                          <ChevronDown size={14} aria-hidden="true" />
                        ) : (
                          <ChevronRight size={14} aria-hidden="true" />
                        )}
                        {expanded ? "Hide" : "Show"} {w.slots.length} half-hours
                      </button>
                    )}
                    {expanded && (
                      <ul className="next-card-slots">
                        {w.slots.map((s) => (
                          <SlotLine key={s.time} slot={s} timeZone={timeZone} scales={scales} />
                        ))}
                      </ul>
                    )}
                  </li>
                );
              })}
            </ul>
          </section>
        ))}
      </div>
    );

  return (
    <div ref={wrap} className="plan-table-wrap" tabIndex={0} role="region" aria-label="What's next, window by window">
      <table className="plan-table">
        <caption className="sr-only">
          Predbat’s plan window by window, with prices, battery levels and planned cost. Expand a window to see its
          half-hours.
        </caption>
        <thead>
          <tr>
            <th scope="col">When</th>
            <th scope="col">Plan</th>
            <th scope="col">Price</th>
            <th scope="col">Battery</th>
            <th scope="col">
              Home use
              <Hint label="What are these figures?">
                Predbat’s forecast for each window, in kWh. Measured figures appear under What happened once a half-hour
                has passed.
              </Hint>
            </th>
            <th scope="col">Solar</th>
            <th scope="col" className="num">
              Cost
              <Hint label="What is the cost column?">
                Predbat’s planned cost of grid energy for the window (negative earns), and the running total from now.
                For the window in progress, only the part still to come.
              </Hint>
            </th>
            <th scope="col" className="num">
              Total
            </th>
          </tr>
        </thead>
        {days.map((d) => (
          <tbody key={d.day}>
            <tr className="day-row">
              <th colSpan={8} scope="rowgroup">
                {d.day}
              </th>
            </tr>
            {d.windows.map((w) => {
              const expanded = open.has(w.id);
              const load = w.slots.map((s) => s.loadForecast).filter(finite);
              const pv = w.slots.map((s) => s.pvForecast).filter(finite);
              const pvTotal = pv.reduce((a, b) => a + b, 0);
              // The window in progress shows what is still to come, so its cost and the running total agree.
              const { cost, partial } = shownCost(w, totalFrom);
              const notes = windowNotes(w, timeZone, reserve, cost);
              return (
                <Fragment key={w.id}>
                  <tr className={`plan-window-row${w.phase === "current" ? " is-now" : ""}`}>
                    <th scope="row">
                      {w.slots.length > 1 ? (
                        <button
                          type="button"
                          className="expand-button"
                          aria-expanded={expanded}
                          aria-label={`${expanded ? "Hide" : "Show"} the half-hours of ${range(w.start, w.end, timeZone)}`}
                          onClick={() => toggle(w.id)}
                        >
                          {expanded ? (
                            <ChevronDown size={14} aria-hidden="true" />
                          ) : (
                            <ChevronRight size={14} aria-hidden="true" />
                          )}
                        </button>
                      ) : (
                        <span className="expand-spacer" />
                      )}
                      <span className="when">
                        {w.phase === "current" ? (
                          <>Now–{clock(w.end, { timeZone })}</>
                        ) : (
                          range(w.start, w.end, timeZone)
                        )}
                      </span>
                    </th>
                    <td>
                      <ActionBadge action={w.action} text={w.short} tone={w.tone} />
                      {w.tags
                        .filter((t) => t !== "estimated")
                        .map((t) => (
                          <Chip key={t} tone="success" className="window-tag">
                            {windowTagLabels[t]}
                          </Chip>
                        ))}
                      {notes.map((n) => (
                        <span key={n} className="window-note">
                          {n}
                        </span>
                      ))}
                    </td>
                    <td>
                      <WindowPrice w={w} cost={cost} scales={scales} />
                    </td>
                    <td className="tabular">{socNowText(w, liveSoc) || "—"}</td>
                    <td className="tabular">{load.length ? kwh(load.reduce((a, b) => a + b, 0)) : "—"}</td>
                    <td className="tabular">{pvTotal >= 0.05 ? kwh(pvTotal) : <span className="muted">—</span>}</td>
                    <td className={`num tabular${(cost ?? 0) < 0 ? " good" : ""}`}>
                      {cost != null ? gbp(cost) : "—"}
                      {partial && <small className="cell-sub">rest of window</small>}
                    </td>
                    <td className="num tabular muted">{gbp(totals.get(w.id))}</td>
                  </tr>
                  {expanded &&
                    w.slots.map((s) => <SlotRow key={s.time} slot={s} timeZone={timeZone} scales={scales} now={now} />)}
                </Fragment>
              );
            })}
          </tbody>
        ))}
      </table>
    </div>
  );
}

function SlotRow({
  slot,
  timeZone,
  scales,
  now,
}: {
  slot: PlanSlotLike;
  timeZone?: string;
  scales: Scales;
  now: number;
}) {
  const start = Date.parse(slot.time),
    end = start + (slot.durationMinutes || 30) * MIN;
  const action = slotAction({ ...slot, action: slot.action ?? "" });
  return (
    <tr className={`slot-row${start <= now && end > now ? " is-now" : ""}`}>
      <th scope="row">
        <span className="expand-spacer" />
        <span className="when">{range(start, end, timeZone)}</span>
      </th>
      <td>
        <ActionBadge action={action} text={shortAction(action)} hint={false} />
      </td>
      <td>
        <SlotPrice slot={slot} scales={scales} sub />
      </td>
      <td className="tabular">
        {finite(slot.socForecast) ? percent(slot.socForecast) : "—"}
        {finite(slot.socForecastEnd) ? ` → ${percent(slot.socForecastEnd)}` : ""}
      </td>
      <td className="tabular">{finite(slot.loadForecast) ? kwh(slot.loadForecast, { precision: "table" }) : "—"}</td>
      <td className="tabular">
        {finite(slot.pvForecast) && slot.pvForecast > 0.005 ? (
          kwh(slot.pvForecast, { precision: "table" })
        ) : (
          <span className="muted">—</span>
        )}
      </td>
      <td className={`num tabular${(slot.cost ?? 0) < 0 ? " good" : ""}`}>
        {finite(slot.cost) ? gbp(slot.cost) : "—"}
      </td>
      <td />
    </tr>
  );
}

/** A half-hour's price: the export price for an export half-hour (with the import price as a note), else the import price. */
function SlotPrice({ slot, scales, sub = false }: { slot: PlanSlotLike; scales: Scales; sub?: boolean }) {
  const action = slotAction({ ...slot, action: slot.action ?? "" });
  const kind = action.rate === "export" ? "export" : "import";
  const value = kind === "export" ? slot.exportRate : slot.importRate;
  const other = kind === "export" ? slot.importRate : slot.exportRate;
  return (
    <>
      <PricePill
        kind={kind}
        text={finite(value) ? pence(value, { unit: "p" }) : ""}
        value={value ?? null}
        scales={scales}
      />
      {sub && finite(other) && (
        <small className="cell-sub">
          {kind === "export" ? "import" : "export"} {pence(other, { unit: "p" })}
        </small>
      )}
    </>
  );
}

function SlotLine({ slot, timeZone, scales }: { slot: PlanSlotLike; timeZone?: string; scales: Scales }) {
  const start = Date.parse(slot.time),
    end = start + (slot.durationMinutes || 30) * MIN;
  const action = slotAction({ ...slot, action: slot.action ?? "" });
  return (
    <li>
      <span className="next-card-time">{range(start, end, timeZone)}</span>
      <span>{shortAction(action)}</span>
      <SlotPrice slot={slot} scales={scales} />
      <span className="muted">{finite(slot.socForecast) ? percent(slot.socForecast) : ""}</span>
    </li>
  );
}
