import type { ReactNode } from "react";
import { ArrowRight } from "lucide-react";
import { EmptyState } from "./States";

export { Badge } from "./Chip";

/** An empty state with a title and one sentence (the older name for EmptyState). */
export function Empty({ title, children, level = 3 }: { title: string; children?: ReactNode; level?: 2 | 3 | 4 }) {
  return (
    <EmptyState title={title} level={level} className="empty">
      {children || "This fills in as data arrives."}
    </EmptyState>
  );
}
/**
 * A section of a page (the .panel role: 24px padding, an h2 at --fs-lg). `eyebrow` is the small uppercase label above
 * the title. Items inside a panel use the .card role; see styles.css.
 */
export function Panel({
  title,
  subtitle,
  eyebrow,
  action,
  children,
  className = "",
}: {
  title: string;
  subtitle?: string;
  eyebrow?: string;
  action?: ReactNode;
  children: ReactNode;
  className?: string;
}) {
  return (
    <section className={`panel ${className}`}>
      <div className="panel-head">
        <div>
          {eyebrow && <p className="eyebrow">{eyebrow}</p>}
          <h2>{title}</h2>
          {subtitle && <p>{subtitle}</p>}
        </div>
        {action}
      </div>
      {children}
    </section>
  );
}
export function Diff({ changes }: { changes: { key: string; before: string; after: string }[] }) {
  return (
    <div className="diff">
      {changes.map((c) => (
        <div key={c.key}>
          <code>{c.key}</code>
          <span className="before">{c.before}</span>
          <ArrowRight size={14} aria-hidden="true" />
          <strong>{c.after}</strong>
        </div>
      ))}
    </div>
  );
}
