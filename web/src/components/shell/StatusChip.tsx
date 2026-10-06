import { RefreshCw, Settings2 } from "lucide-react";
import { Popover } from "../ui/Popover";
import { Button, ButtonLink } from "../ui/Button";
import { PlainText } from "../PlainText";
import type { Health } from "../../lib/health";

/** The chip's label in one word, for phones. */
const shortLabels: Record<string, string> = {
  "Joule offline": "Offline",
  "Not set up": "Setup",
  "Can't reach Predbat": "No Predbat",
  "Predbat out of date": "Stale",
};

/**
 * The one status indicator: "Demo", "Read-only", "Connected", "Not set up", "Can't reach Predbat" or "Joule offline".
 * Its popover explains each part (Joule, Predbat, Home Assistant, changes, AI) and offers the next step.
 */
export function StatusChip({
  health,
  predbatConfigured,
  demo,
  onRefresh,
  refreshing,
  setup = null,
}: {
  /** Live setup progress: while required steps are missing the chip reads "Setup 3/6" and links to the checklist. */
  setup?: { done: number; total: number; requiredDone: boolean } | null;
  health: Health;
  predbatConfigured: boolean;
  demo: boolean;
  onRefresh: () => void;
  refreshing: boolean;
}) {
  const { rows } = health;
  // Being offline matters more than setup: that chip wins.
  const settingUp = !!setup && !setup.requiredDone && !demo && health.chip.tone !== "danger";
  const chip = settingUp ? { label: `Setup ${setup!.done}/${setup!.total}`, tone: "warn" as const } : health.chip;
  return (
    <Popover
      label="Connection status"
      trigger={(props) => (
        <button
          type="button"
          className={`status-chip tone-${chip.tone}`}
          aria-label={`Status: ${chip.label}. Show details`}
          {...props}
        >
          <i className="status-dot" aria-hidden="true" />
          <span className="status-label">{chip.label}</span>
          {/* Phones: one word when something needs saying; a healthy install is just the ringed dot. */}
          {chip.tone !== "success" && (
            <span className="status-short" aria-hidden="true">
              {settingUp ? chip.label : (shortLabels[chip.label] ?? chip.label)}
            </span>
          )}
        </button>
      )}
    >
      {(close) => (
        <>
          <h2>{chip.label}</h2>
          <p className="popover-lead">How this install of Joule is connected, and what it can do.</p>
          <dl className="status-rows">
            {rows.map((r) => (
              <div key={r.key} className={`tone-${r.tone}`}>
                <i className="status-dot" aria-hidden="true" />
                <dt>{r.label}</dt>
                <dd>
                  <PlainText text={r.detail} />
                </dd>
              </div>
            ))}
          </dl>
          <div className="popover-actions">
            <Button
              variant="secondary"
              size="sm"
              disabled={refreshing}
              onClick={() => {
                onRefresh();
                close();
              }}
            >
              <RefreshCw size={14} aria-hidden="true" className={refreshing ? "spin" : undefined} />
              Refresh now
            </Button>
            {settingUp ? (
              <ButtonLink href="#/setup" variant="primary" size="sm">
                Continue setup
              </ButtonLink>
            ) : !predbatConfigured ? (
              <ButtonLink href="#/setup" variant="primary" size="sm">
                {demo ? "Connect my Predbat" : "Set up Predbat"}
              </ButtonLink>
            ) : (
              <ButtonLink href="#/setup" variant="ghost" size="sm">
                <Settings2 size={14} aria-hidden="true" />
                Setup
              </ButtonLink>
            )}
          </div>
        </>
      )}
    </Popover>
  );
}
