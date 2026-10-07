import { ArrowDown, ArrowRight, ArrowUp, Minus, Zap } from "lucide-react";
import { Chip } from "../ui";
import { Hint } from "../Hint";
import { percent, pence } from "../../lib/format";
import { clock } from "../../lib/time";
import { useMediaQuery } from "../../lib/useMediaQuery";
import { ActionMeaning, iconFor } from "../plan/ActionBadge";
import {
  priceRange,
  windowTagLabels,
  windowTitle,
  windowWhen,
  type ActionView,
  type PlanWindow,
  type WindowTag,
} from "../plan/windows";
import { directionText, presentAction, type BatteryNow, type PriceNow } from "./model";

/**
 * The battery level as a ring: one arc for the level and nothing else on it, so there is no mark to decode. The arc has flat
 * ends, so its start at 12 o'clock is a clean edge rather than a dot. The reserve is written under the ring ("Reserve 4%"),
 * and the arc turns amber when the level is at or within 5 points of it; greyed when it isn't a current reading.
 */
export function BatteryRing({
  value,
  reserve,
  stale,
  size = 84,
}: {
  value: number | null;
  reserve?: number | null;
  stale?: boolean;
  size?: number;
}) {
  const stroke = size >= 70 ? 7 : 6;
  const r = (size - stroke) / 2 - 1,
    c = 2 * Math.PI * r,
    mid = size / 2;
  const v = value == null ? 0 : Math.max(0, Math.min(100, value));
  const hasReserve = reserve != null && reserve > 0 && reserve < 100;
  const low = value != null && hasReserve && value <= reserve + 5;
  return (
    <div className={`battery-ring${stale ? " is-stale" : ""}${low ? " is-low" : ""}`}>
      <div className="ring-dial" style={{ width: size, height: size }}>
        <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} aria-hidden="true">
          <circle className="ring-track" cx={mid} cy={mid} r={r} strokeWidth={stroke} />
          {value != null && v > 0 && (
            <circle
              className="ring-value"
              cx={mid}
              cy={mid}
              r={r}
              strokeWidth={stroke}
              strokeLinecap="butt"
              strokeDasharray={`${(v / 100) * c} ${c}`}
              transform={`rotate(-90 ${mid} ${mid})`}
            />
          )}
        </svg>
        <span className="ring-label">
          {value == null ? "—" : Math.round(value)}
          {value != null && <small>%</small>}
        </span>
      </div>
      {hasReserve && <span className="ring-reserve">Reserve {Math.round(reserve)}%</span>}
    </div>
  );
}

const DirectionIcon = ({ b }: { b: BatteryNow }) =>
  b.direction === "charging" ? (
    <ArrowUp size={14} aria-hidden="true" />
  ) : b.direction === "discharging" ? (
    <ArrowDown size={14} aria-hidden="true" />
  ) : (
    <Minus size={14} aria-hidden="true" />
  );

/**
 * "Right now": the battery ring, what Predbat is doing in plain words, which way the battery is going, the price now, and
 * the next thing that will happen. Under it, one sentence on how today is going.
 */
export function NowHero({
  action,
  battery,
  price,
  next,
  reserve,
  sentence,
  now,
  timeZone,
  dispatching,
  tags = [],
  stale,
  demo,
}: {
  action: ActionView | null;
  battery: BatteryNow;
  price: PriceNow;
  next: PlanWindow | null;
  reserve?: number | null;
  sentence: string;
  now: number;
  timeZone?: string;
  /** Octopus Intelligent is dispatching cheap energy right now. */
  dispatching?: boolean;
  /** Tags on the window in progress (saving or free session, estimated price). */
  tags?: WindowTag[];
  /** The plan on screen is out of date: say so instead of claiming what happens now. */
  stale?: string | null;
  demo?: boolean;
}) {
  const narrow = useMediaQuery("(max-width: 400px)");
  const ActionIcon = action ? iconFor(action) : Zap;
  const headline = action ? presentAction(action) : "Waiting for Predbat's plan";
  const target = action?.target != null && (action.key === "charge" || action.key === "export") ? action.target : null;
  return (
    <section className="now-hero" aria-labelledby="now-heading">
      <div className="now-top">
        <BatteryRing value={battery.value} reserve={reserve} stale={battery.stale} size={narrow ? 60 : 84} />
        <div className="now-copy">
          <span className="now-eyebrow" id="now-heading">
            Right now{demo ? " · demo house" : ""}
          </span>
          <p className="now-headline">
            <ActionIcon size={20} aria-hidden="true" className={`now-icon tone-${action?.tone ?? "neutral"}`} />
            {/* The (i) sits inside the text so it follows the last word when the headline wraps. */}
            <span className="now-headline-text">
              {headline}
              {target != null && <span className="now-target"> → {percent(target)}</span>}
              {action && (
                <>
                  {" "}
                  <Hint label={`What does “${headline}” mean?`}>
                    <ActionMeaning action={action} />
                  </Hint>
                </>
              )}
            </span>
          </p>
          <p className="now-battery">
            {battery.stale ? (
              <span className="muted">
                Battery {battery.value != null ? percent(battery.value) : ""}
                {battery.time ? ` as of ${clock(battery.time, { timeZone })}` : ""} · sensor not reporting
              </span>
            ) : battery.direction ? (
              <span className={`now-direction dir-${battery.direction}`}>
                <DirectionIcon b={battery} />
                {directionText(battery)}
                {battery.directionSource === "plan" || battery.kw != null ? (
                  <Hint label="Where does this rate come from?">
                    {battery.directionSource === "measured"
                      ? "The direction is from your battery's measured level over the last half-hour; the rate is Predbat's plan for this half-hour."
                      : "From Predbat's plan for this half-hour: the measured level hasn't moved enough yet to tell."}
                  </Hint>
                ) : null}
              </span>
            ) : (
              <span className="muted">Waiting for a battery reading</span>
            )}
          </p>
        </div>
        <div className="now-prices">
          {price.import != null && (
            <Chip tone={price.cheap ? "success" : "neutral"} className="price-chip">
              Import {pence(price.import, { unit: "p" })}
              {price.cheap ? " · cheap" : ""}
            </Chip>
          )}
          {price.export != null && <Chip className="price-chip">Export {pence(price.export, { unit: "p" })}</Chip>}
          {tags.map((t) => (
            <Chip key={t} tone={t === "estimated" ? "neutral" : "success"}>
              {windowTagLabels[t]}
            </Chip>
          ))}
          {dispatching && <Chip tone="info">Octopus dispatch now</Chip>}
        </div>
      </div>
      {stale ? (
        <p className="now-next is-stale" role="status">
          {stale}
        </p>
      ) : next ? (
        <p className="now-next">
          <span className="now-next-label">Next</span>
          <span className={`now-next-dot tone-${next.tone}`} aria-hidden="true" />
          <strong>{windowTitle(next)}</strong>
          <span>{windowWhen(next.start, next.end, now, timeZone)}</span>
          {next.key !== "demand" && next.price && (
            <span className="muted">
              at {priceRange(next.price)}
              {next.tags.includes("saving") ? ` · ${windowTagLabels.saving}` : ""}
            </span>
          )}
          <a className="now-next-link" href="#/plan">
            Plan <ArrowRight size={13} aria-hidden="true" />
          </a>
        </p>
      ) : null}
      {sentence && <p className="now-sentence">{sentence}</p>}
    </section>
  );
}
