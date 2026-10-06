import type { ReactNode } from "react";
import { cn } from "@/lib/utils";

export interface SegmentedOption<T extends string> {
  value: T;
  label: ReactNode;
  /** Accessible name when the label is an icon. */
  ariaLabel?: string;
  disabled?: boolean;
  /** Why the option is disabled, or what it does: shown on hover. */
  title?: string;
}

/**
 * A choice between two to five options that applies straight away (chart range, compare with, period).
 * Each option is a toggle button with aria-pressed, inside a labelled group.
 */
export function Segmented<T extends string>({
  label,
  options,
  value,
  onChange,
  className,
  size = "md",
}: {
  /** Names the group for assistive tech, e.g. "Compare with". */
  label: string;
  options: SegmentedOption<T>[];
  value: T;
  onChange: (value: T) => void;
  className?: string;
  size?: "sm" | "md";
}) {
  return (
    <div role="group" aria-label={label} className={cn("segmented", `segmented-${size}`, className)}>
      {options.map((o) => (
        <button
          key={o.value}
          type="button"
          aria-pressed={o.value === value}
          aria-label={o.ariaLabel}
          disabled={o.disabled}
          title={o.title}
          onClick={() => o.value !== value && onChange(o.value)}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}
