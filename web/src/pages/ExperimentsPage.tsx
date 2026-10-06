import { useApp } from "../context/AppContext";
import { Disclosure } from "../components/ui";
import { ExperimentCard } from "../components/ExperimentCard";
import { isOpenTrial } from "../lib/insights";
import "./insights.css";

/** Trials: each change being tried out, the ones due a decision first; closed trials fold away. */
export default function ExperimentsPage() {
  const { data } = useApp();
  const s = data.state,
    runtimeWritesBlocked = !data.connection.demo && !data.connection.writesEnabled;
  const now = Date.now();
  const open = s.experiments
    .filter(isOpenTrial)
    .sort(
      (a, b) =>
        Number(Date.parse(b.reviewAt) <= now) - Number(Date.parse(a.reviewAt) <= now) ||
        Date.parse(b.startedAt) - Date.parse(a.startedAt),
    );
  const closed = s.experiments
    .filter((e) => !isOpenTrial(e))
    .sort((a, b) => Date.parse(b.startedAt) - Date.parse(a.startedAt));
  const card = (e: (typeof s.experiments)[number]) => (
    <ExperimentCard key={e.id} experiment={e} revision={s.revision} runtimeWritesBlocked={runtimeWritesBlocked} />
  );
  return (
    <div className="insights-page trials-page">
      {open.length ? (
        <div className="trial-grid">{open.map(card)}</div>
      ) : (
        <p className="inbox-empty">
          <strong>No trials running.</strong> When you apply a suggestion or change a setting, Joule tracks the days
          before and after here.
        </p>
      )}
      {closed.length > 0 && (
        <Disclosure summary="Closed trials" count={closed.length} className="trials-closed">
          <div className="trial-rows">{closed.map(card)}</div>
        </Disclosure>
      )}
    </div>
  );
}
