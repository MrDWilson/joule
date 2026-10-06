import { useSyncExternalStore } from "react";

/**
 * Hash routing: #/today, #/plan[/:planId], #/insights[/inv/:id | /suggestions | /experiments],
 * #/energy[/reports]?from&to, #/setup[/settings | /ai | /sensors | /files | /changes | /about], #/kit (development).
 *
 * The URL is the source of truth, so Back and Forward work, a refresh keeps the page and any page can be linked to.
 * The old page names (Overview, Data, Recommendations…) and their hashes redirect to the new routes.
 */

export type Destination = "today" | "plan" | "insights" | "energy" | "setup" | "kit";

export interface Route {
  destination: Destination;
  /** The sub-page inside a destination, e.g. "suggestions" in #/insights/suggestions; "" for the destination itself. */
  section: string;
  /** Path parameters: planId for #/plan/:planId, id for #/insights/inv/:id. */
  params: Record<string, string>;
  query: URLSearchParams;
  /** The canonical hash for this route, e.g. "#/insights/suggestions". */
  hash: string;
}

export interface Section {
  key: string;
  label: string;
  /** One line under the page title. */
  description: string;
}

export interface DestinationInfo {
  key: Destination;
  label: string;
  description: string;
  sections: Section[];
}

/** The five destinations and their sub-pages, in navigation order. */
export const destinations: Record<Destination, DestinationInfo> = {
  today: {
    key: "today",
    label: "Today",
    description: "How today is going, what Predbat plans next and anything that needs you.",
    sections: [],
  },
  plan: {
    key: "plan",
    label: "Plan",
    description: "What Predbat plans to do, and how it compared with what happened.",
    sections: [],
  },
  insights: {
    key: "insights",
    label: "Insights",
    description: "What Joule's checks found, and what needs you.",
    sections: [
      { key: "", label: "Checks", description: "Ask why something happened, or see what the latest checks found." },
      { key: "suggestions", label: "Suggestions", description: "Setting changes to approve and to-dos to handle." },
      { key: "experiments", label: "Trials", description: "Changes being tried out, and how they did." },
    ],
  },
  energy: {
    key: "energy",
    label: "Energy",
    description: "What your meters recorded, how the sensors are doing, and your saved reports.",
    // One page: #/energy/reports opens it at the saved reports (see hiddenSections), so there are no tabs.
    sections: [],
  },
  setup: {
    key: "setup",
    label: "Setup",
    description: "Connections, Predbat's settings and how Joule's AI works.",
    sections: [
      { key: "", label: "Overview", description: "Connections, Predbat's settings and how Joule's AI works." },
      { key: "settings", label: "Predbat settings", description: "Predbat's settings, and what the AI may change." },
      { key: "ai", label: "AI checks", description: "Provider, schedule and usage." },
      { key: "sensors", label: "Sensors", description: "The Home Assistant sensors Joule reads." },
      { key: "files", label: "Files", description: "Saved copies of Predbat's configuration files." },
      { key: "changes", label: "Changes", description: "Every settings change, and which ones Joule can undo." },
      { key: "about", label: "About", description: "Version, documentation and the small print." },
    ],
  },
  kit: { key: "kit", label: "Kit", description: "Every interface primitive in every state.", sections: [] },
};

/** Sub-pages that have an address but no tab: #/energy/reports is the Energy page scrolled to its saved reports. */
const hiddenSections: Partial<Record<Destination, string[]>> = { energy: ["reports"] };

/** The four destinations on the phone's bottom bar; Setup sits behind the cog in the header. */
export const primaryDestinations: Destination[] = ["today", "plan", "insights", "energy"];

/** Old page names (and the hashes they would have had) mapped to their new home. */
const legacy: Record<string, string> = {
  overview: "#/today",
  plan: "#/plan",
  data: "#/energy",
  files: "#/setup/files",
  reports: "#/energy/reports",
  investigations: "#/insights",
  recommendations: "#/insights/suggestions",
  experiments: "#/insights/experiments",
  settings: "#/setup/settings",
  history: "#/setup/changes",
  "ai & costs": "#/setup/ai",
  "ai-costs": "#/setup/ai",
  "ai-and-costs": "#/setup/ai",
  aicosts: "#/setup/ai",
  // Setup › Reports & notifications lives with the reports themselves.
  "setup/reports": "#/energy/reports",
};

/** A legacy page name ("Recommendations") or any route-ish string as a canonical hash. */
export function toHash(target: string): string {
  const t = target.trim();
  if (t.startsWith("#/")) return t;
  if (t.startsWith("/")) return `#${t}`;
  return legacy[t.toLowerCase()] ?? `#/${t.toLowerCase()}`;
}

/** The primitives gallery (#/kit) exists only in development builds; elsewhere it is an unknown page. */
const kitAvailable = () => !!import.meta.env?.DEV;
const isDestination = (s: string): s is Destination => s in destinations && (s !== "kit" || kitAvailable());

/** Parses a hash ("#/insights/inv/abc?x=1") into a route; unknown paths fall back to Today. */
export function parseHash(hash: string): Route {
  const raw = hash.replace(/^#\/?/, "");
  const [pathPart, queryPart = ""] = raw.split("?");
  const query = new URLSearchParams(queryPart);
  const segments = pathPart.split("/").filter(Boolean).map(decodeURIComponent);
  const joined = segments.join("/").toLowerCase();
  if (legacy[joined] && legacy[joined] !== `#/${joined}`) {
    const redirected = parseHash(legacy[joined] + (queryPart ? `?${queryPart}` : ""));
    return redirected;
  }
  const [first = "today", ...rest] = segments;
  const destination: Destination = isDestination(first.toLowerCase()) ? (first.toLowerCase() as Destination) : "today";
  const params: Record<string, string> = {};
  let section = "";
  if (destination === "plan" && rest[0]) params.planId = rest[0];
  if (destination === "insights") {
    if (rest[0] === "inv" && rest[1]) params.id = rest[1];
    else if (destinations.insights.sections.some((s) => s.key === rest[0])) section = rest[0];
  }
  if (destination === "energy" || destination === "setup") {
    if (
      destinations[destination].sections.some((s) => s.key === rest[0]) ||
      hiddenSections[destination]?.includes(rest[0])
    )
      section = rest[0];
  }
  return { destination, section, params, query, hash: buildHash(destination, section, params, query) };
}

export function buildHash(
  destination: Destination,
  section = "",
  params: Record<string, string> = {},
  query?: URLSearchParams,
) {
  let path = `#/${destination}`;
  if (destination === "plan" && params.planId) path += `/${encodeURIComponent(params.planId)}`;
  else if (destination === "insights" && params.id) path += `/inv/${encodeURIComponent(params.id)}`;
  else if (section) path += `/${section}`;
  const q = query?.toString();
  return q ? `${path}?${q}` : path;
}

/** The page title for a route: "Suggestions · Insights" or "Plan". */
export function routeTitle(route: Route) {
  const d = destinations[route.destination];
  const section = d.sections.find((s) => s.key === route.section);
  return section && section.key ? `${section.label} · ${d.label}` : d.label;
}

// ---------------------------------------------------------------- store
const listeners = new Set<() => void>();
let current: Route | null = null;
let currentHash = "";

function snapshot(): Route {
  const hash = typeof window === "undefined" ? "" : window.location.hash;
  if (!current || hash !== currentHash) {
    currentHash = hash;
    current = parseHash(hash);
  }
  return current;
}
function emit() {
  listeners.forEach((l) => l());
}
function subscribe(listener: () => void) {
  listeners.add(listener);
  if (listeners.size === 1) {
    window.addEventListener("hashchange", emit);
    window.addEventListener("popstate", emit);
  }
  return () => {
    listeners.delete(listener);
    if (listeners.size === 0) {
      window.removeEventListener("hashchange", emit);
      window.removeEventListener("popstate", emit);
    }
  };
}

/** The current route; re-renders when the URL changes (links, Back, Forward or navigate()). */
export function useRoute(): Route {
  return useSyncExternalStore(subscribe, snapshot, snapshot);
}

/**
 * Goes to a route: a hash ("#/plan"), a path ("/plan") or an old page name ("Recommendations").
 * replace swaps the current history entry instead of adding one (used for redirects).
 */
export function navigate(target: string, options: { replace?: boolean } = {}) {
  const hash = toHash(target);
  if (hash === window.location.hash) return;
  const url = `${window.location.pathname}${window.location.search}${hash}`;
  if (options.replace) window.history.replaceState(window.history.state, "", url);
  else window.history.pushState(null, "", url);
  emit();
}

/** Rewrites the address bar to the canonical form of the current route (old names, missing hash) without a new entry. */
export function canonicalise() {
  const route = snapshot();
  if (window.location.hash !== route.hash) navigate(route.hash, { replace: true });
}
