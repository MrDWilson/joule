import { useEffect, useId, useState } from "react";
import { useApp } from "../../context/AppContext";
import type { StandingChargeSettings } from "../../completion-types";
import { gbp, pence } from "../../lib/format";
import { dayLabel } from "../../lib/time";
import { Button, Switch } from "../ui";
import { LoadingBlock } from "../ui/States";

const perDay = (p: number) => `${pence(p, { unit: "p" })} a day (${gbp(p / 100)})`;

/** Where today's figure comes from, in one sentence. */
export function standingChargeSource(s: StandingChargeSettings): string {
  if (s.todaySource === "sensor" && s.todayPencePerDay != null)
    return `${perDay(s.todayPencePerDay)}, read from Home Assistant${s.entityOrigin === "octopus" ? " (the Octopus Energy sensor on the same meter as your import rate)" : ""}.`;
  if (s.todaySource === "manual" && s.todayPencePerDay != null)
    return `${perDay(s.todayPencePerDay)}, the figure you entered.`;
  if (s.entity)
    return "Joule is watching for the standing charge sensor but hasn’t had a reading yet. You can enter the figure below meanwhile.";
  return "Joule doesn’t know it yet. With the Octopus Energy integration it’s read automatically from the same meter as your import rate; otherwise enter it below. It’s on your bill or in your supplier’s app.";
}

/** Rate changes, newest first: "From Thu 1 Oct: 53.68p a day". Consecutive days at the same rate are one entry. */
export function rateChanges(recent: StandingChargeSettings["recent"]) {
  const oldestFirst = [...recent].sort((a, b) => a.day.localeCompare(b.day));
  const changes: { from: string; pence: number; source: string }[] = [];
  for (const r of oldestFirst)
    if (changes.at(-1)?.pence !== r.pencePerDay) changes.push({ from: r.day, pence: r.pencePerDay, source: r.source });
  return changes.reverse();
}

/**
 * Setup › Sensors: the standing charge. Read from the Octopus Energy sensor when there is one; otherwise a figure in pence per
 * day. A switch chooses whether the headline net cost includes it (it does by default). Saving applies at once, no restart.
 */
export function StandingChargeSetting() {
  const { api, measured, notify } = useApp();
  const id = useId();
  const [settings, setSettings] = useState<StandingChargeSettings | null>(null);
  const [value, setValue] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    let live = true;
    api<StandingChargeSettings>("/telemetry/standing-charge")
      .then((s) => {
        if (!live) return;
        setSettings(s);
        setValue(s.manualPencePerDay != null ? String(s.manualPencePerDay) : "");
      })
      .catch((e: Error) => live && setError(e.message));
    return () => {
      live = false;
    };
  }, [api]);

  async function save(
    body: { manualPencePerDay?: number | null; includeInNet?: boolean; clearManual?: boolean },
    done: string,
  ) {
    setBusy(true);
    setError("");
    try {
      const next = await api<StandingChargeSettings>("/telemetry/standing-charge", body);
      setSettings(next);
      setValue(next.manualPencePerDay != null ? String(next.manualPencePerDay) : "");
      measured.retry();
      notify(done);
    } catch (e) {
      setError((e as Error).message);
    }
    setBusy(false);
  }

  const parsed = Number(value.trim().replace(/p$/i, ""));
  const valid = value.trim() !== "" && Number.isFinite(parsed) && parsed >= 0 && parsed <= 1000;
  // The history is worth a line only once the rate has changed.
  const changes = settings ? rateChanges(settings.recent).slice(0, 3) : [];
  if (changes.length < 2) changes.length = 0;
  return (
    <section className="panel standing-charge" aria-labelledby={`${id}-heading`}>
      <div className="panel-head">
        <div>
          <h2 id={`${id}-heading`}>Standing charge</h2>
          <p>The fixed daily charge on your electricity bill, counted in your daily and period costs.</p>
        </div>
      </div>
      {!settings && !error && <LoadingBlock lines={2} label="Loading the standing charge" />}
      {settings && (
        <>
          <p className="standing-now" role="status">
            {standingChargeSource(settings)}
          </p>
          <form
            className="standing-form"
            onSubmit={(e) => {
              e.preventDefault();
              if (valid) void save({ manualPencePerDay: parsed }, "Standing charge saved.");
            }}
          >
            <label className="field" htmlFor={`${id}-pence`}>
              Standing charge (p/day)
              <span className="standing-row">
                <input
                  id={`${id}-pence`}
                  type="text"
                  inputMode="decimal"
                  autoComplete="off"
                  placeholder="53.68"
                  value={value}
                  disabled={busy}
                  aria-describedby={`${id}-note`}
                  onChange={(e) => setValue(e.target.value)}
                />
                <Button type="submit" size="sm" disabled={busy || !valid || parsed === settings.manualPencePerDay}>
                  Save
                </Button>
                {settings.manualPencePerDay != null && (
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    disabled={busy}
                    onClick={() => void save({ clearManual: true }, "Your figure is cleared.")}
                  >
                    Clear
                  </Button>
                )}
              </span>
            </label>
            <p className="form-note muted" id={`${id}-note`}>
              {settings.todaySource === "sensor"
                ? "Used only on days the sensor has no reading."
                : "Pence per day including VAT, as on your bill. It applies from today; earlier days keep the rate they had."}
            </p>
          </form>
          <label className="standing-switch">
            <Switch
              checked={settings.includeInNet}
              disabled={busy}
              label="Include the standing charge in net cost"
              onCheckedChange={(on) =>
                void save(
                  { includeInNet: on },
                  on ? "Net cost now includes the standing charge." : "Net cost now leaves out the standing charge.",
                )
              }
            />
            Include it in net cost
          </label>
          {changes.length > 0 && (
            <ul className="standing-history" aria-label="Standing charge by day">
              {changes.map((c) => (
                <li key={c.from}>
                  From {dayLabel(c.from + "T12:00:00Z")}: {pence(c.pence, { unit: "p" })} a day
                  <span className="muted"> · {c.source === "sensor" ? "from the sensor" : "your figure"}</span>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
      {error && (
        <p className="sheet-warning" role="alert">
          {error}
        </p>
      )}
    </section>
  );
}
