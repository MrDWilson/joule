import type { ReactNode } from "react";
import { ChevronDown } from "lucide-react";
import { cn } from "@/lib/utils";

/**
 * The one expand/collapse style: a full-width row with the label on the left and an optional count and a chevron on the
 * right. Built on <details>, so it works without JavaScript and with find-in-page.
 */
export function Disclosure({
  summary,
  count,
  children,
  defaultOpen = false,
  className,
  onToggle,
}: {
  summary: ReactNode;
  count?: number | string;
  children: ReactNode;
  defaultOpen?: boolean;
  className?: string;
  onToggle?: (open: boolean) => void;
}) {
  return (
    <details
      className={cn("disclosure", className)}
      open={defaultOpen || undefined}
      onToggle={(e) => onToggle?.((e.currentTarget as HTMLDetailsElement).open)}
    >
      <summary>
        <span className="disclosure-label">{summary}</span>
        {count !== undefined && <span className="disclosure-count">{count}</span>}
        <ChevronDown className="disclosure-chevron" size={16} aria-hidden="true" />
      </summary>
      <div className="disclosure-body">{children}</div>
    </details>
  );
}
