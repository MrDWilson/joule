import { useState } from "react";
import { ShieldCheck } from "lucide-react";
import { BrandMark } from "../BrandMark";
import { Button } from "../ui/Button";
import { disclaimer } from "../../lib/copy";

export const DOCS_URL = "https://github.com/MrDWilson/joule#readme";
export const ISSUES_URL = "https://github.com/MrDWilson/joule/issues";

/** The small print on every page: version, independence from Predbat, documentation and where to report a problem. */
export function SiteFooter({ version, lastRead }: { version?: string; lastRead?: string }) {
  return (
    <footer className="site-footer">
      <span>
        <BrandMark size={14} variant="mono" decorative /> Joule{version ? ` ${version}` : ""} · {disclaimer.project}
        {lastRead && <> · Last read from Predbat {lastRead}</>}
      </span>
      <nav aria-label="About Joule">
        <a href="#/setup/about">About</a>
        <a href={DOCS_URL} target="_blank" rel="noreferrer">
          Docs<span className="sr-only"> (opens in a new tab)</span>
        </a>
        <a href={ISSUES_URL} target="_blank" rel="noreferrer">
          Report a problem<span className="sr-only"> (opens in a new tab)</span>
        </a>
      </nav>
    </footer>
  );
}

const FIRST_RUN_KEY = "joule.firstRunSeen";

/** Shown once per browser: AI suggestions can be wrong, and nothing changes without your approval. */
export function FirstRunNotice() {
  const [seen, setSeen] = useState(() => localStorage.getItem(FIRST_RUN_KEY) === "1");
  if (seen) return null;
  return (
    <aside className="first-run" aria-label="Before you start">
      <div className="first-run-body">
        <strong className="first-run-title">
          <ShieldCheck size={18} aria-hidden="true" />
          Welcome to Joule.
        </strong>
        {/* Phones get one sentence; the rest is on Setup › About. */}
        <p className="first-run-long">
          Joule watches Predbat's plan and checks it against your meters. {disclaimer.ai} {disclaimer.project}
        </p>
        <p className="first-run-short">
          Joule checks Predbat's plan against your meters, and nothing changes without your approval.
        </p>
      </div>
      <Button
        variant="secondary"
        size="sm"
        className="first-run-dismiss"
        onClick={() => {
          localStorage.setItem(FIRST_RUN_KEY, "1");
          setSeen(true);
        }}
      >
        Got it
      </Button>
    </aside>
  );
}
