import { useState } from "react";
import { Button, Modal } from "./ui";
import type { PermissionProps } from "../completion-types";
import { displayValue, maxAutoStep } from "../lib/settings";

const round = (n: number) => Math.round(n * 1e6) / 1e6;

/**
 * The limits for automatic changes to one setting: lowest and highest value, the largest single change (at most 10% of
 * the setting's range, as the server enforces) and the wait between changes.
 */
export function PermissionBounds({ setting, mutate, busy, close }: PermissionProps) {
  const stepLimit = round(maxAutoStep(setting));
  const [minimum, setMinimum] = useState(setting.autoMinimum?.toString() || ""),
    [maximum, setMaximum] = useState(setting.autoMaximum?.toString() || ""),
    [maxStep, setMaxStep] = useState(String(round(Math.min(setting.autoMaxStep || stepLimit, stepLimit)))),
    [cooldown, setCooldown] = useState(String(setting.autoCooldownHours ?? 72));
  const numeric = setting.type === "number";
  const min = minimum === "" ? null : Number(minimum),
    max = maximum === "" ? null : Number(maximum),
    step = Number(maxStep),
    wait = Number(cooldown);
  const problems = [
    (min !== null &&
      (!Number.isFinite(min) ||
        (setting.min !== null && min < setting.min) ||
        (setting.max !== null && min > setting.max))) ||
    (max !== null &&
      (!Number.isFinite(max) ||
        (setting.max !== null && max > setting.max) ||
        (setting.min !== null && max < setting.min)))
      ? `Keep the limits within ${setting.min ?? "any"}–${setting.max ?? "any"}.`
      : "",
    min !== null && max !== null && min > max ? "The lowest value must be below the highest." : "",
    !Number.isFinite(step) || step <= 0 || step > stepLimit + 1e-9
      ? `The largest change must be above 0 and at most ${stepLimit}.`
      : "",
    !Number.isInteger(wait) || wait < 24 || wait > 720
      ? "The wait must be a whole number of hours from 24 to 720."
      : "",
  ].filter(Boolean);
  return (
    <Modal
      open
      onOpenChange={(open) => {
        if (!open) close();
      }}
      title={setting.autoAllowed ? `Limits for ${setting.name}` : `Let the AI change ${setting.name}?`}
      description="The AI may change this setting by itself only in Automatic mode, and only within these limits."
    >
      <form
        onSubmit={async (e) => {
          e.preventDefault();
          if (
            await mutate(
              `/permissions/${setting.key}/bounds`,
              { allowed: true, minimum: min, maximum: max, maxStep: step, cooldownHours: wait },
              setting.autoAllowed ? "Limits saved." : `The AI may now change ${setting.name} within your limits.`,
            )
          )
            close();
        }}
      >
        <p className="body-copy">
          Now {displayValue(setting, setting.value)}. A change already being tried out, or too little evidence to undo
          it safely, still holds the AI back. This doesn't turn on live writes.
        </p>
        {numeric && (
          <div className="form-row">
            <label className="field">
              Lowest value
              <input
                aria-label="Automatic minimum"
                type="number"
                inputMode="decimal"
                step="any"
                min={setting.min ?? undefined}
                max={setting.max ?? undefined}
                value={minimum}
                onChange={(e) => setMinimum(e.target.value)}
                placeholder={setting.min != null ? `${setting.min} (no limit)` : "No limit"}
              />
            </label>
            <label className="field">
              Highest value
              <input
                aria-label="Automatic maximum"
                type="number"
                inputMode="decimal"
                step="any"
                min={setting.min ?? undefined}
                max={setting.max ?? undefined}
                value={maximum}
                onChange={(e) => setMaximum(e.target.value)}
                placeholder={setting.max != null ? `${setting.max} (no limit)` : "No limit"}
              />
            </label>
          </div>
        )}
        <div className="form-row">
          <label className="field">
            Largest change at once (up to {stepLimit})
            <input
              aria-label="Maximum automatic step"
              required
              type="number"
              inputMode="decimal"
              min={0.000001}
              max={stepLimit}
              step="any"
              value={maxStep}
              onChange={(e) => setMaxStep(e.target.value)}
            />
          </label>
          <label className="field">
            Wait between changes (hours)
            <input
              aria-label="Automatic cooldown hours"
              required
              type="number"
              inputMode="numeric"
              min={24}
              max={720}
              step={1}
              value={cooldown}
              onChange={(e) => setCooldown(e.target.value)}
            />
          </label>
        </div>
        {problems.length > 0 && (
          <p role="alert" className="callout">
            {problems.join(" ")}
          </p>
        )}
        <div className="dialog-actions">
          <Button type="button" variant="ghost" onClick={close}>
            Cancel
          </Button>
          <Button disabled={busy || problems.length > 0}>
            {setting.autoAllowed ? "Save limits" : "Allow future changes"}
          </Button>
        </div>
      </form>
    </Modal>
  );
}
