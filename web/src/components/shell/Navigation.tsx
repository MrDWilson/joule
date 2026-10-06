import { useEffect, useRef } from "react";
import { Activity, BrainCircuit, Database, LayoutDashboard, Settings2, type LucideIcon } from "lucide-react";
import { BrandLockup } from "../BrandMark";
import { useScrollRow } from "../../lib/scrollRow";
import { buildHash, destinations, primaryDestinations, type Destination, type Route } from "../../lib/router";

export const destinationIcons: Record<Exclude<Destination, "kit">, LucideIcon> = {
  today: LayoutDashboard,
  plan: Activity,
  insights: BrainCircuit,
  energy: Database,
  setup: Settings2,
};

/** "3 waiting for you" for the Insights count, as screen readers hear it. */
const countLabel = (n: number) => `, ${n} waiting for you`;

/**
 * Desktop sidebar: the four everyday destinations, then Setup on its own. The active item has aria-current="page".
 * The version lives in the site footer only.
 */
export function Sidebar({ route, insightsCount }: { route: Route; insightsCount: number }) {
  const link = (d: Exclude<Destination, "kit">) => {
    const Icon = destinationIcons[d];
    const current = route.destination === d;
    return (
      <a
        key={d}
        href={buildHash(d)}
        className="nav-link"
        aria-current={current ? "page" : undefined}
        aria-label={
          d === "insights" && insightsCount > 0 ? `${destinations[d].label}${countLabel(insightsCount)}` : undefined
        }
      >
        <Icon size={18} aria-hidden="true" />
        <span>{destinations[d].label}</span>
        {d === "insights" && insightsCount > 0 && (
          <b className="nav-count" aria-hidden="true">
            {insightsCount}
          </b>
        )}
      </a>
    );
  };
  return (
    <aside className="sidebar">
      <div className="brand">
        <a href="#/today" aria-label="Joule, go to Today">
          <BrandLockup size={30} />
        </a>
      </div>
      <nav aria-label="Main navigation">
        <div className="nav-group">{primaryDestinations.map((d) => link(d as Exclude<Destination, "kit">))}</div>
        <div className="nav-group">{link("setup")}</div>
      </nav>
    </aside>
  );
}

/** Phone and tablet: a bottom tab bar with the four everyday destinations; Setup is the cog in the header. */
export function TabBar({ route, insightsCount }: { route: Route; insightsCount: number }) {
  return (
    <nav aria-label="Main navigation" className="tabbar">
      {primaryDestinations.map((d) => {
        const Icon = destinationIcons[d as Exclude<Destination, "kit">];
        const current = route.destination === d;
        return (
          <a
            key={d}
            href={buildHash(d)}
            aria-current={current ? "page" : undefined}
            aria-label={
              d === "insights" && insightsCount > 0 ? `${destinations[d].label}${countLabel(insightsCount)}` : undefined
            }
          >
            <Icon size={22} aria-hidden="true" />
            <span>{destinations[d].label}</span>
            {d === "insights" && insightsCount > 0 && (
              <b className="tab-count" aria-hidden="true">
                {insightsCount > 99 ? "99+" : insightsCount}
              </b>
            )}
          </a>
        );
      })}
    </nav>
  );
}

/**
 * The tabs inside a destination (Insights, Energy, Setup) as real links. On a phone they scroll sideways: the current
 * tab is scrolled into view, and the edge with more tabs behind it fades.
 */
export function SubNav({ route, counts = {} }: { route: Route; counts?: Record<string, number> }) {
  const d = destinations[route.destination];
  const ref = useRef<HTMLElement>(null);
  useScrollRow(ref, [route.destination, d.sections.length]);
  useEffect(() => {
    const current = ref.current?.querySelector<HTMLElement>('[aria-current="page"]');
    // Only the row scrolls (block: "nearest"), never the page.
    current?.scrollIntoView?.({ inline: "center", block: "nearest" });
  }, [route.destination, route.section]);
  if (!d.sections.length) return null;
  return (
    <nav ref={ref} aria-label={`${d.label} sections`} className="subnav scroll-row">
      {d.sections.map((s) => {
        const current = route.section === s.key;
        const n = counts[s.key] ?? 0;
        return (
          <a
            key={s.key || "index"}
            href={buildHash(d.key, s.key)}
            aria-current={current ? "page" : undefined}
            aria-label={n > 0 ? `${s.label}${countLabel(n)}` : undefined}
          >
            {s.label}
            {n > 0 && (
              <b className="nav-count" aria-hidden="true">
                {n}
              </b>
            )}
          </a>
        );
      })}
    </nav>
  );
}
