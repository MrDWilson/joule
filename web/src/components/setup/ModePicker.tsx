import { useRef, useState, type KeyboardEvent } from "react";
import { Eye, Lightbulb, Lock, Zap } from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { Button, Modal } from "../ui";
import { modeLabel, modes, type Mode } from "../../lib/copy";
import type { Setting } from "../../types";

const ORDER: Mode[] = ["Monitor", "Recommend", "Auto"];
const ICONS: Record<Mode, LucideIcon> = { Monitor: Eye, Recommend: Lightbulb, Auto: Zap };

/** "House load scaling (up to 0.1 per change, at least 72 h apart)". */
export function limitsText(s: Setting) {
  const range =
    s.autoMinimum != null || s.autoMaximum != null
      ? `, between ${s.autoMinimum ?? "any"} and ${s.autoMaximum ?? "any"}`
      : "";
  return `up to ${s.autoMaxStep} per change, at least ${s.autoCooldownHours} h apart${range}`;
}

/**
 * How the AI helps: a radiogroup (arrow keys move and choose). Choosing Automatic asks first and lists exactly what it
 * may change. One status line underneath replaces the old stack of callouts.
 */
export function ModePicker({
  mode,
  settings,
  busy,
  writesOff,
  onChange,
}: {
  mode: string;
  settings: Setting[];
  busy: boolean;
  /** Live install with writes off: Joule only suggests, whatever the mode. */
  writesOff: boolean;
  onChange: (mode: Mode) => Promise<boolean>;
}) {
  const [confirmAuto, setConfirmAuto] = useState(false);
  const refs = useRef<(HTMLButtonElement | null)[]>([]);
  const allowed = settings.filter((s) => s.autoAllowed);
  const current = (ORDER.includes(mode as Mode) ? mode : "Recommend") as Mode;

  function choose(next: Mode) {
    if (next === current || busy) return;
    if (next === "Auto") setConfirmAuto(true);
    else void onChange(next);
  }
  function onKey(e: KeyboardEvent<HTMLButtonElement>, index: number) {
    const delta =
      e.key === "ArrowRight" || e.key === "ArrowDown" ? 1 : e.key === "ArrowLeft" || e.key === "ArrowUp" ? -1 : 0;
    if (!delta) return;
    e.preventDefault();
    const next = (index + delta + ORDER.length) % ORDER.length;
    refs.current[next]?.focus();
    choose(ORDER[next]);
  }

  const status =
    current === "Monitor"
      ? "Joule watches and explains. It never changes Predbat, and editing here is locked."
      : current === "Auto"
        ? allowed.length
          ? `Automatic can change ${allowed.map((s) => s.name).join(", ")}, within your limits. Everything else waits for you.`
          : "Automatic is on, but no setting allows automatic changes yet, so nothing changes on its own."
        : writesOff
          ? "This install is read-only: the AI suggests changes and you make them in Predbat yourself. You can still choose what it may change."
          : "The AI suggests changes and nothing changes until you approve.";

  return (
    <section className="mode-card" aria-labelledby="mode-heading">
      <div className="mode-card-head">
        <h2 id="mode-heading">How the AI helps</h2>
      </div>
      <div className="mode-options" role="radiogroup" aria-labelledby="mode-heading">
        {ORDER.map((m, i) => {
          const Icon = ICONS[m];
          const checked = m === current;
          return (
            <button
              key={m}
              ref={(el) => {
                refs.current[i] = el;
              }}
              type="button"
              role="radio"
              aria-checked={checked}
              tabIndex={checked ? 0 : -1}
              disabled={busy && !checked}
              className="mode-option"
              onClick={() => choose(m)}
              onKeyDown={(e) => onKey(e, i)}
            >
              <Icon size={16} aria-hidden="true" />
              <span className="mode-option-label">{modeLabel(m)}</span>
              <span className="mode-option-help">{modes[m].description}</span>
            </button>
          );
        })}
      </div>
      <p className="mode-status" role="status">
        {current === "Monitor" && <Lock size={14} aria-hidden="true" />}
        <span>{status}</span>
        {current === "Monitor" && (
          <Button variant="link" size="sm" disabled={busy} onClick={() => void onChange("Recommend")}>
            Unlock editing
          </Button>
        )}
        {current !== "Monitor" && writesOff && (
          <a href="#/setup" className="text-link">
            How to allow changes
          </a>
        )}
      </p>
      <Modal
        open={confirmAuto}
        onOpenChange={setConfirmAuto}
        title="Turn on Automatic?"
        description="The AI may then change some settings by itself, within the limits you set."
        footer={
          <>
            <Button variant="ghost" onClick={() => setConfirmAuto(false)}>
              Cancel
            </Button>
            <Button
              disabled={busy}
              onClick={async () => {
                if (await onChange("Auto")) setConfirmAuto(false);
              }}
            >
              Turn on Automatic
            </Button>
          </>
        }
      >
        {allowed.length ? (
          <>
            <p className="body-copy">Automatic can change only these settings:</p>
            <ul className="auto-list">
              {allowed.map((s) => (
                <li key={s.key}>
                  <strong>{s.name}</strong> <span className="muted">{limitsText(s)}</span>
                </li>
              ))}
            </ul>
            <p className="muted">
              Nothing else changes without your approval. A change being tried out, or too little evidence, still holds
              it back.
            </p>
          </>
        ) : (
          <p className="body-copy">
            Nothing yet: no setting allows automatic changes, so Automatic would behave like Suggest changes. Open a
            setting marked “Low risk” below and allow automatic changes to it first.
          </p>
        )}
        {writesOff && (
          <p className="callout">
            This install is read-only (live writes are off), so even Automatic can only suggest.
          </p>
        )}
      </Modal>
    </section>
  );
}
