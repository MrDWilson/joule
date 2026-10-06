import type { ReactNode } from "react";
import { AlertTriangle, CloudOff, Info, OctagonAlert } from "lucide-react";
import type { Tone } from "../ui/Chip";

export interface AlertItem {
  key: string;
  tone: Extract<Tone, "danger" | "warn" | "info">;
  title: string;
  detail?: ReactNode;
  action?: ReactNode;
}

const order = { danger: 0, warn: 1, info: 2 } as const;
const icons = { danger: OctagonAlert, warn: AlertTriangle, info: Info } as const;

/**
 * Problems that need action, one row each, most severe first, each with its own action. Shown only when something is
 * wrong; a healthy install shows nothing here.
 */
export function AlertRows({ items }: { items: AlertItem[] }) {
  if (!items.length) return null;
  const sorted = [...items].sort((a, b) => order[a.tone] - order[b.tone]);
  return (
    <div className="alerts">
      {sorted.map((a) => {
        const Icon = a.key === "offline" ? CloudOff : icons[a.tone];
        return (
          <div key={a.key} role="alert" className={`alert tone-${a.tone}`}>
            <Icon size={18} aria-hidden="true" />
            <div className="alert-copy">
              <strong>{a.title}</strong>
              {a.detail && <span>{a.detail}</span>}
            </div>
            {a.action && <div className="alert-action">{a.action}</div>}
          </div>
        );
      })}
    </div>
  );
}
