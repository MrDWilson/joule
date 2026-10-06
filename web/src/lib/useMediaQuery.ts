import { useSyncExternalStore } from "react";

/**
 * True while a media query matches, e.g. useMediaQuery("(max-width: 700px)"). Use it to avoid mounting a chart in a
 * layout that hides it (Recharts warns about zero-size containers) or to pick a phone-sized variant.
 */
export function useMediaQuery(query: string) {
  return useSyncExternalStore(
    (onChange) => {
      const list = window.matchMedia(query);
      list.addEventListener("change", onChange);
      return () => list.removeEventListener("change", onChange);
    },
    () => window.matchMedia(query).matches,
    () => false,
  );
}

/** The layout breakpoints used by styles.css. */
export const breakpoints = {
  phone: "(max-width: 700px)",
  /** Below this the sidebar becomes the bottom tab bar. */
  tablet: "(max-width: 900px)",
  narrowDesktop: "(max-width: 1150px)",
  touch: "(pointer: coarse)",
} as const;
