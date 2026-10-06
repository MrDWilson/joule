import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import type { ReactNode } from "react";
import { createPortal } from "react-dom";
import { Info } from "lucide-react";

/**
 * A small (i) button that explains a term. It opens on hover and keyboard focus and stays open while the pointer is over
 * the button or the bubble (WCAG 1.4.13), closing 150ms after the pointer leaves both. A click or tap pins it open (a
 * second one closes it); Escape or a tap elsewhere closes it. The explanation is always the button's description, via a
 * visually hidden copy, so screen readers hear it on first focus. The hit area is 32px (44px on touch screens).
 */
export function Hint({ label, children }: { label: string; children: ReactNode }) {
  const [open, setOpen] = useState(false),
    [pinned, setPinned] = useState(false);
  const [place, setPlace] = useState<{ left: number; top: number } | null>(null);
  const button = useRef<HTMLButtonElement>(null),
    bubble = useRef<HTMLSpanElement>(null);
  const closeTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const id = useId(),
    descriptionId = `${id}-description`;
  const cancelClose = () => clearTimeout(closeTimer.current);
  const close = useCallback(() => {
    clearTimeout(closeTimer.current);
    setOpen(false);
    setPinned(false);
  }, []);
  const closeSoon = () => {
    if (pinned) return;
    cancelClose();
    closeTimer.current = setTimeout(() => setOpen(false), 150);
  };
  const show = () => {
    cancelClose();
    setOpen(true);
  };
  // Fixed position under (or, without room, above) the button, clamped inside the viewport; it follows the button on scroll.
  const position = useCallback(() => {
    const b = button.current?.getBoundingClientRect(),
      t = bubble.current?.getBoundingClientRect();
    if (!b || !t) return;
    const vw = document.documentElement.clientWidth,
      vh = window.innerHeight;
    const left = Math.max(8, Math.min(b.left + b.width / 2 - t.width / 2, vw - t.width - 8));
    const below = b.bottom + 6;
    setPlace({ left, top: below + t.height > vh - 8 && b.top - t.height - 6 > 8 ? b.top - t.height - 6 : below });
  }, []);
  useLayoutEffect(() => {
    if (open) position();
    else setPlace(null);
  }, [open, position]);
  useEffect(() => () => clearTimeout(closeTimer.current), []);
  useEffect(() => {
    if (!open) return;
    const key = (e: KeyboardEvent) => {
      if (e.key === "Escape") close();
    };
    const outside = (e: PointerEvent) => {
      const target = e.target as Node;
      if (!button.current?.contains(target) && !bubble.current?.contains(target)) close();
    };
    window.addEventListener("keydown", key);
    window.addEventListener("pointerdown", outside);
    window.addEventListener("scroll", position, true);
    window.addEventListener("resize", position);
    return () => {
      window.removeEventListener("keydown", key);
      window.removeEventListener("pointerdown", outside);
      window.removeEventListener("scroll", position, true);
      window.removeEventListener("resize", position);
    };
  }, [open, position, close]);
  return (
    <span className="hint">
      <button
        ref={button}
        type="button"
        className="hint-button"
        aria-label={label}
        aria-expanded={open}
        aria-describedby={descriptionId}
        onMouseEnter={show}
        onMouseLeave={closeSoon}
        onFocus={show}
        onBlur={(e) => {
          if (!bubble.current?.contains(e.relatedTarget as Node)) close();
        }}
        onClick={() => {
          const pin = !pinned;
          setPinned(pin);
          setOpen(pin);
        }}
      >
        <Info size={14} aria-hidden="true" />
      </button>
      {/* The description lives outside the label (so the label's own text stays just the label) but is always present. */}
      {createPortal(
        <span id={descriptionId} hidden>
          {children}
        </span>,
        document.body,
      )}
      {open &&
        createPortal(
          <span
            ref={bubble}
            id={id}
            role="tooltip"
            className="hint-bubble"
            onMouseEnter={cancelClose}
            onMouseLeave={closeSoon}
            style={place ? { left: place.left, top: place.top } : { left: 0, top: 0, visibility: "hidden" }}
          >
            {children}
          </span>,
          document.body,
        )}
    </span>
  );
}
