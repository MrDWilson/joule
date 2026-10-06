import { Fragment } from "react";
import { textParts } from "../lib/sanitize";

/**
 * Server or AI text made safe to show: Predbat codes become their labels, UTC timestamps become household time,
 * backticks disappear, and entity ids and setting keys render as small code chips. See lib/sanitize.ts.
 */
export function PlainText({ text }: { text: string | null | undefined }) {
  return (
    <>
      {textParts(text).map((part, i) =>
        part.kind === "code" ? (
          <code key={i} className="entity-chip">
            {part.value}
          </code>
        ) : (
          <Fragment key={i}>{part.value}</Fragment>
        ),
      )}
    </>
  );
}
