import { useState } from "react";
import { CalendarClock, ChevronRight, Gauge, Sparkles } from "lucide-react";
import { Button, ButtonLink } from "../ui";
import { disclaimer } from "../../lib/copy";
import "./demo-tour.css";

/** The same key as the welcome notice: whichever one a person dismisses, neither shows again in this browser. */
const SEEN_KEY = "joule.firstRunSeen";

const CARDS = [
  {
    icon: CalendarClock,
    title: "What Predbat plans",
    text: "When the battery charges, holds and exports, and why.",
    href: "#/plan",
  },
  {
    icon: Gauge,
    title: "What your meters recorded",
    text: "Day by day: what the house used, what solar made and what it cost.",
    href: "#/energy",
  },
  {
    icon: Sparkles,
    title: "What the AI found",
    text: "Plain-English checks and suggestions you approve.",
    href: "#/insights",
  },
];

/**
 * Demo mode's first-visit tour: three places to look and a way to connect your own Predbat. Shown once per browser.
 * On a phone it stays short (under about 300px): the three places become one row that scrolls sideways, and the small
 * print shares a line with "Got it".
 */
export function DemoTour() {
  const [seen, setSeen] = useState(() => localStorage.getItem(SEEN_KEY) === "1");
  if (seen) return null;
  return (
    <aside className="demo-tour" aria-label="Before you start">
      <strong className="demo-tour-title">
        Welcome to Joule. This is the demo: a made-up house, so you can explore safely.
      </strong>
      <p className="demo-tour-note">
        <span className="demo-tour-note-long">
          {disclaimer.ai} {disclaimer.project}
        </span>
        <span className="demo-tour-note-short">AI can be wrong; nothing changes without you. Not part of Predbat.</span>
      </p>
      <ButtonLink href="#/setup" variant="primary" size="sm" className="demo-tour-connect">
        Connect my Predbat
      </ButtonLink>
      <Button
        variant="secondary"
        size="sm"
        className="demo-tour-got"
        onClick={() => {
          localStorage.setItem(SEEN_KEY, "1");
          setSeen(true);
        }}
      >
        Got it
      </Button>
      <ol className="demo-tour-cards">
        {CARDS.map(({ icon: Icon, title, text, href }, i) => (
          <li key={title}>
            <a href={href}>
              <span className="demo-tour-step" aria-hidden="true">
                {i + 1}
              </span>
              <Icon size={18} aria-hidden="true" />
              <strong>{title}</strong>
              <span className="demo-tour-text">{text}</span>
              <ChevronRight size={16} aria-hidden="true" className="demo-tour-chevron" />
            </a>
          </li>
        ))}
      </ol>
    </aside>
  );
}
