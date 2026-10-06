import { ArrowUpRight } from "lucide-react";
import { useApp } from "../context/AppContext";
import { BrandLockup } from "../components/BrandMark";
import { Panel } from "../components/ui/Panel";
import { DOCS_URL, ISSUES_URL } from "../components/shell/SiteFooter";
import { disclaimer } from "../lib/copy";
import { dayTime } from "../lib/time";
import { predbatVersion } from "../lib/changes";
import { useSetupStatus } from "../lib/setupApi";

/** Setup › About: versions, documentation and the small print. */
export default function AboutPage() {
  const { about, data } = useApp();
  const demo = data.connection.demo;
  // The setup status reads Predbat's version from its settings when the shell's /about doesn't have it yet.
  const { status } = useSetupStatus([data.state.lastCollection]);
  return (
    <div className="two-columns about-page">
      <Panel title="About Joule">
        <div className="about-lockup">
          <BrandLockup size={40} />
        </div>
        <p className="body-copy">
          Joule watches the plan of the Predbat home-battery optimiser, measures what really happened with your Home
          Assistant meters, and uses AI to explain it in plain English and suggest improvements.
        </p>
        <p className="body-copy">{disclaimer.project}</p>
        <p className="body-copy">{disclaimer.ai}</p>
        <dl className="connection-details">
          <div>
            <dt>Joule version</dt>
            <dd>{about.version}</dd>
          </div>
          <div>
            <dt>Predbat version</dt>
            <dd>
              {predbatVersion(about.predbatVersion ?? status?.predbat.version) ?? (demo ? "Demo" : "Not reported")}
            </dd>
          </div>
          <div>
            <dt>Last read from Predbat</dt>
            <dd>{data.state.lastCollection ? dayTime(data.state.lastCollection) : "Not yet"}</dd>
          </div>
        </dl>
      </Panel>
      <Panel title="Help">
        <ul className="link-list">
          <li>
            <a href={DOCS_URL} target="_blank" rel="noreferrer">
              Joule documentation <ArrowUpRight size={14} aria-hidden="true" />
              <span className="sr-only"> (opens in a new tab)</span>
            </a>
          </li>
          <li>
            <a href="https://springfall2008.github.io/batpred/" target="_blank" rel="noreferrer">
              Predbat documentation <ArrowUpRight size={14} aria-hidden="true" />
              <span className="sr-only"> (opens in a new tab)</span>
            </a>
          </li>
          <li>
            <a href={ISSUES_URL} target="_blank" rel="noreferrer">
              Report a problem <ArrowUpRight size={14} aria-hidden="true" />
              <span className="sr-only"> (opens in a new tab)</span>
            </a>
          </li>
        </ul>
      </Panel>
    </div>
  );
}
