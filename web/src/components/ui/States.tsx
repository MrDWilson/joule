import type { ReactNode } from "react";
import { AlertTriangle, RefreshCw } from "lucide-react";
import { BrandMark } from "../BrandMark";
import { Button } from "./Button";
import { cn } from "@/lib/utils";
import { plainError } from "@/lib/errors";

type HeadingLevel = 2 | 3 | 4;

/**
 * An empty state: an icon (the Joule mark by default), a title, one sentence and at most one action.
 * quiet renders a single muted line ("Nothing to review · last check 10:25") for sections that can simply be empty.
 * level sets the heading level so the page outline stays correct.
 */
export function EmptyState({
  title,
  children,
  icon,
  action,
  quiet = false,
  level = 3,
  className,
}: {
  title: string;
  children?: ReactNode;
  icon?: ReactNode;
  action?: ReactNode;
  quiet?: boolean;
  level?: HeadingLevel;
  className?: string;
}) {
  if (quiet)
    return (
      <p className={cn("empty-quiet", className)}>
        <strong>{title}</strong>
        {children && <span> · {children}</span>}
        {action}
      </p>
    );
  const H = `h${level}` as const;
  return (
    <div className={cn("empty-state", className)}>
      <span className="empty-state-icon" aria-hidden="true">
        {icon ?? <BrandMark size={28} variant="mono" decorative />}
      </span>
      <H className="empty-state-title">{title}</H>
      {children && <p className="empty-state-body">{children}</p>}
      {action && <div className="empty-state-action">{action}</div>}
    </div>
  );
}

/** A skeleton while something loads: lines of text, or one block of a given height (for charts). Announced once. */
export function LoadingBlock({
  lines = 3,
  height,
  label = "Loading",
  className,
}: {
  lines?: number;
  height?: number;
  label?: string;
  className?: string;
}) {
  return (
    <div className={cn("loading-block", className)} role="status" aria-live="polite">
      <span className="sr-only">{label}…</span>
      {height ? (
        <span className="skeleton" style={{ height }} aria-hidden="true" />
      ) : (
        Array.from({ length: lines }, (_, i) => (
          <span
            key={i}
            className="skeleton skeleton-line"
            style={{ width: `${92 - ((i * 17) % 40)}%` }}
            aria-hidden="true"
          />
        ))
      )}
    </div>
  );
}

/**
 * Something failed: a plain-language message and the standard "Try again". Raw browser errors ("Failed to fetch")
 * are translated by plainError.
 */
export function ErrorNotice({
  error,
  onRetry,
  title,
  className,
  retryLabel = "Try again",
}: {
  error: unknown;
  onRetry?: () => void;
  title?: string;
  className?: string;
  retryLabel?: string;
}) {
  const message = plainError(error);
  return (
    <div role="alert" className={cn("error-notice", className)}>
      <AlertTriangle size={18} aria-hidden="true" className="error-notice-icon" />
      <div className="error-notice-copy">
        {title && <strong>{title}</strong>}
        <span>{message}</span>
      </div>
      {onRetry && (
        <Button variant="secondary" size="sm" onClick={onRetry}>
          <RefreshCw size={14} aria-hidden="true" />
          {retryLabel}
        </Button>
      )}
    </div>
  );
}
