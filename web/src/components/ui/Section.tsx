import type { ReactNode } from "react";

/** A titled panel: the standard card for a page section. */
export function Section({
  title,
  children,
  subtitle,
  eyebrow,
}: {
  title: ReactNode;
  children: ReactNode;
  subtitle?: string;
  eyebrow?: ReactNode;
}) {
  return (
    <section className="panel">
      <div className="panel-head">
        <div>
          {eyebrow && <div className="panel-eyebrow">{eyebrow}</div>}
          <h2>{title}</h2>
          {subtitle && <p>{subtitle}</p>}
        </div>
      </div>
      {children}
    </section>
  );
}
