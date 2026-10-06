import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";

/**
 * A small panel anchored to a button (the status chip, the notifications bell). The trigger has aria-expanded and
 * aria-controls; Escape, a click elsewhere or choosing a link inside closes it, and focus returns to the trigger.
 */
export function Popover({
  trigger,
  label,
  children,
  align = "end",
  className,
  onOpenChange,
}: {
  onOpenChange?: (open: boolean) => void;
  /** Render prop for the trigger button: spread the props onto a <button>. */
  trigger: (props: {
    ref: (el: HTMLButtonElement | null) => void;
    onClick: () => void;
    "aria-expanded": boolean;
    "aria-controls": string;
    "aria-haspopup": "dialog";
  }) => ReactNode;
  /** Accessible name of the panel. */
  label: string;
  children: ReactNode | ((close: () => void) => ReactNode);
  align?: "start" | "end";
  className?: string;
}) {
  const [open, setOpen] = useState(false);
  const [place, setPlace] = useState<{ top: number; left: number } | null>(null);
  const button = useRef<HTMLButtonElement | null>(null);
  const panel = useRef<HTMLDivElement | null>(null);
  const id = useId();
  const close = useCallback((refocus = true) => {
    setOpen(false);
    if (refocus) button.current?.focus();
  }, []);
  const position = useCallback(() => {
    const b = button.current?.getBoundingClientRect(),
      p = panel.current?.getBoundingClientRect();
    if (!b || !p) return;
    const vw = document.documentElement.clientWidth;
    const left = align === "end" ? b.right - p.width : b.left;
    setPlace({ top: b.bottom + 8, left: Math.max(12, Math.min(left, vw - p.width - 12)) });
  }, [align]);
  const changed = useRef(onOpenChange);
  changed.current = onOpenChange;
  useLayoutEffect(() => {
    if (open) position();
    else setPlace(null);
    changed.current?.(open);
  }, [open, position]);
  useEffect(() => {
    if (!open) return;
    panel.current?.focus();
    const key = (e: KeyboardEvent) => {
      if (e.key === "Escape") close();
    };
    const outside = (e: PointerEvent) => {
      const t = e.target as Node;
      if (!panel.current?.contains(t) && !button.current?.contains(t)) close(false);
    };
    const navigated = () => close(false);
    window.addEventListener("keydown", key);
    window.addEventListener("pointerdown", outside);
    window.addEventListener("resize", position);
    window.addEventListener("popstate", navigated);
    return () => {
      window.removeEventListener("keydown", key);
      window.removeEventListener("pointerdown", outside);
      window.removeEventListener("resize", position);
      window.removeEventListener("popstate", navigated);
    };
  }, [open, close, position]);
  return (
    <>
      {trigger({
        ref: (el) => {
          button.current = el;
        },
        onClick: () => setOpen((v) => !v),
        "aria-expanded": open,
        "aria-controls": id,
        "aria-haspopup": "dialog",
      })}
      {open &&
        createPortal(
          <div
            ref={panel}
            id={id}
            role="dialog"
            aria-label={label}
            tabIndex={-1}
            className={`popover${className ? ` ${className}` : ""}`}
            style={place ?? { top: 0, left: 0, visibility: "hidden" }}
            onClick={(e) => {
              if ((e.target as HTMLElement).closest("a[href]")) close(false);
            }}
          >
            {typeof children === "function" ? children(() => close()) : children}
          </div>,
          document.body,
        )}
    </>
  );
}
