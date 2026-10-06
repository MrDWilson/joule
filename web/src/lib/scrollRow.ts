import { useEffect, type DependencyList, type RefObject } from "react";

/**
 * Marks a sideways-scrolling row (sub-navigation tabs, chart chips, a wide table) with data-fade="end" | "start" |
 * "both" | "none", so the `.scroll-row` CSS fades the edge that has more behind it. A clipped tab with no hint reads as
 * the end of the list; a fade says "there's more this way".
 */
export function useScrollRow<T extends HTMLElement>(ref: RefObject<T | null>, deps: DependencyList = []) {
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const update = () => {
      const after = el.scrollWidth - el.clientWidth - el.scrollLeft > 2;
      const before = el.scrollLeft > 2;
      el.dataset.fade = after && before ? "both" : after ? "end" : before ? "start" : "none";
    };
    update();
    el.addEventListener("scroll", update, { passive: true });
    const observer = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(update);
    observer?.observe(el);
    return () => {
      el.removeEventListener("scroll", update);
      observer?.disconnect();
    };
    // The caller's deps say when the row's content changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);
}
