import { useEffect, useRef } from "react";

/**
 * Marks a scroll box that has more to the side: sets data-overflow="end" while content is hidden past its right edge
 * ("start" once scrolled to the end, "both" in between), so CSS can fade that edge. Re-checked on resize and scroll.
 */
export function useOverflow<T extends HTMLElement>() {
  const ref = useRef<T>(null);
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const check = () => {
      const more = el.scrollWidth - el.clientWidth;
      if (more <= 1) el.removeAttribute("data-overflow");
      else {
        const atStart = el.scrollLeft <= 1,
          atEnd = el.scrollLeft >= more - 1;
        el.setAttribute("data-overflow", atStart ? "end" : atEnd ? "start" : "both");
      }
    };
    check();
    const observer = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(check);
    observer?.observe(el);
    if (el.firstElementChild) observer?.observe(el.firstElementChild);
    el.addEventListener("scroll", check, { passive: true });
    return () => {
      observer?.disconnect();
      el.removeEventListener("scroll", check);
    };
  });
  return ref;
}
