import type { ReactNode } from "react";
import { MoreHorizontal } from "lucide-react";
import { Popover } from "../ui";

export interface OverflowItem {
  label: string;
  icon?: ReactNode;
  onSelect: () => void;
  disabled?: boolean;
  /** Shown under a disabled item: why it can't be used. */
  reason?: string;
}

/** A "More actions" button with a short list of secondary actions (download, restore). */
export function OverflowMenu({ label, items }: { label: string; items: OverflowItem[] }) {
  return (
    <Popover
      label={label}
      className="overflow-menu"
      trigger={(props) => (
        <button type="button" className="icon-button overflow-trigger" aria-label={label} {...props}>
          <MoreHorizontal size={18} aria-hidden="true" />
        </button>
      )}
    >
      {(close) => (
        <ul className="overflow-items">
          {items.map((item) => (
            <li key={item.label}>
              <button
                type="button"
                disabled={item.disabled}
                onClick={() => {
                  close();
                  item.onSelect();
                }}
              >
                {item.icon}
                <span>
                  {item.label}
                  {item.disabled && item.reason && <small>{item.reason}</small>}
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </Popover>
  );
}
