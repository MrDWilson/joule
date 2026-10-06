import { lazy, Suspense, useEffect, useState } from "react";
import { Check, Copy } from "lucide-react";
import { useApp } from "../context/AppContext";
import { Button, Chip, Disclosure, Modal } from "../components/ui";
import { LoadingBlock } from "../components/ui/States";
import { RichText } from "../components/InvestigationText";
import { copyText } from "../components/ConfigFileChangeCard";
import { FriendlyDiff } from "./RecommendationsPage";
import { modeLabel } from "../lib/copy";
import { changeView, isStale, savingText, staleKeys, suggestedBy } from "../lib/insights";
import { buildHash, navigate } from "../lib/router";
import { dayLabel } from "../lib/time";
import type { Proposal } from "../types";

// The before/after chart loads with the dialog, not with the app.
const ImpactPreviewPanel = lazy(() =>
  import("../components/ImpactPreview").then((m) => ({ default: m.ImpactPreviewPanel })),
);

/** Why you're declining, in one tap: sent as the decline note so later checks know. All optional. */
const DECLINE_REASONS = ["We need that energy", "Not now"] as const;
const OTHER_REASON = "Something else…";

/** Calibration evidence: what the setting is about, measured over recent comparable windows, next to what Predbat assumes. */
function Calibration({ p }: { p: Proposal }) {
  const c = p.calibration;
  if (!c?.points?.length) return null;
  const values = c.points.map((x) => x.value);
  const min = Math.min(...values),
    max = Math.max(...values);
  const span = min === max ? `${min} ${c.unit}` : `${min}–${max} ${c.unit}`;
  return (
    <div className="review-calibration">
      <h4>What Joule measured</h4>
      <p>
        {c.quantity}: <strong>{span}</strong> over the last {c.points.length} comparable{" "}
        {c.points.length === 1 ? "window" : "windows"}
        {c.assumed != null && (
          <>
            {" "}
            · Predbat assumes{" "}
            <strong>
              {c.assumed} {c.unit}
            </strong>
          </>
        )}
      </p>
      <ul className="calibration-points" aria-label="Measured values">
        {c.points.slice(-10).map((x) => (
          <li key={x.at} title={dayLabel(x.at)}>
            {x.value}
          </li>
        ))}
      </ul>
      {c.note && (
        <p className="muted">
          <RichText text={c.note} />
        </p>
      )}
    </div>
  );
}

/**
 * Reviewing one suggested setting change, as a sheet: what changes, the saving honestly, the tradeoff, then how it's
 * checked. The footer sticks: Apply change (or, on a read-only install, Mark as done once you've made it in Predbat),
 * Not now (just closes) and Decline. Allowing future automatic changes is offered only where Joule may make them.
 */
export function ProposalReviewDialog({
  proposal: given,
  setProposal,
}: {
  proposal: Proposal | null;
  setProposal: (p: Proposal | null) => void;
}) {
  const { api, mutate, busy, data, notify: toast } = useApp();
  const s = data.state;
  // Follow the live record, so a reply or a settings change shows up while the sheet is open.
  const proposal = given ? (s.proposals.find((p) => p.id === given.id) ?? given) : null;
  const [allowFuture, setAllowFuture] = useState(false),
    [copied, setCopied] = useState(""),
    [declining, setDeclining] = useState(false),
    [reason, setReason] = useState(""),
    [otherReason, setOtherReason] = useState("");
  useEffect(() => {
    setAllowFuture(false);
    setCopied("");
    setDeclining(false);
    setReason("");
    setOtherReason("");
  }, [given?.id]);
  const close = () => setProposal(null);
  const writesOff = !data.connection.demo && !data.connection.writesEnabled;
  const pending = proposal?.status === "Pending";
  const stale = proposal ? isStale(proposal) : false;
  const blocked = s.mode === "Monitor" || s.pendingFileReload || s.writeUncertain;
  const settings = s.settings;
  const autoEligible =
    !!proposal?.changes.length && proposal.changes.every((c) => settings.find((x) => x.key === c.key)?.autoEligible);
  const saving = proposal ? savingText(proposal) : null;
  // "Change: medium confidence", so it never reads as the confidence of the check's finding ("Finding: …").
  const confidence =
    proposal && /^(high|medium|low)$/i.test(proposal.confidence)
      ? `Change: ${proposal.confidence.toLowerCase()} confidence`
      : null;

  async function decline(p: Proposal) {
    const note = (reason === OTHER_REASON ? otherReason : reason).trim();
    const message = "Declined. Joule won't suggest it again unless something changes.";
    if (!(await mutate(`/proposals/${p.id}/deny`, note ? { note } : {}, message))) return;
    // The same message again replaces the plain toast with one you can undo from.
    toast(message, {
      label: "Undo",
      onClick: () => void mutate(`/proposals/${p.id}/reopen`, {}, "Back on your list."),
    });
    close();
  }

  const footer =
    proposal &&
    pending &&
    (declining ? (
      <>
        <div className="review-decline-reasons" role="group" aria-label="Why are you declining? (optional)">
          <p>Why are you declining? (optional)</p>
          <div className="review-reason-chips">
            {[...DECLINE_REASONS, OTHER_REASON].map((r) => (
              <button
                key={r}
                type="button"
                className="suggestion-chip"
                aria-pressed={reason === r}
                onClick={() => setReason(reason === r ? "" : r)}
              >
                {r}
              </button>
            ))}
          </div>
          {reason === OTHER_REASON && (
            <label className="field review-reason-other">
              <span className="sr-only">Your reason</span>
              <input
                maxLength={300}
                value={otherReason}
                placeholder="Tell Joule why"
                autoFocus
                onChange={(e) => setOtherReason(e.target.value)}
              />
            </label>
          )}
        </div>
        <Button variant="ghost" className="review-decline" onClick={() => setDeclining(false)}>
          Back
        </Button>
        <Button variant="danger" disabled={busy} onClick={() => void decline(proposal)}>
          Decline
        </Button>
      </>
    ) : (
      <>
        <Button variant="ghost" className="review-decline" disabled={busy} onClick={() => setDeclining(true)}>
          Decline…
        </Button>
        <Button variant="secondary" onClick={close}>
          Not now
        </Button>
        {writesOff ? (
          <Button
            disabled={busy}
            onClick={async () => {
              if (
                await mutate(
                  `/proposals/${proposal.id}/done`,
                  {},
                  "Marked as done. Joule will see the new value at the next read.",
                )
              )
                close();
            }}
          >
            <Check size={15} aria-hidden="true" />
            Mark as done
          </Button>
        ) : (
          <Button
            disabled={busy || stale || blocked}
            onClick={async () => {
              const message = "Change applied. Joule is tracking it as a trial.";
              if (
                await mutate(`/proposals/${proposal.id}/approve`, { allowFuture: autoEligible && allowFuture }, message)
              ) {
                toast(message, { label: "See trial", onClick: () => navigate("#/insights/experiments") });
                close();
              }
            }}
          >
            Apply change
          </Button>
        )}
      </>
    ));

  return (
    <Modal
      open={!!proposal}
      onOpenChange={(v) => {
        if (!v) close();
      }}
      title="Review this change"
      description={proposal ? suggestedBy(proposal) : ""}
      footer={footer || undefined}
    >
      {proposal && (
        <div className="review-sheet">
          <h3 className="dialog-title">
            <RichText text={proposal.title} />
          </h3>
          <section aria-label="What changes">
            <FriendlyDiff changes={proposal.changes} settings={settings} />
          </section>

          {pending && stale && (
            <div className="callout callout-warn" role="status">
              <strong>
                {staleKeys(proposal)
                  .map((k) => changeView({ key: k, before: "", after: "" }, settings).name)
                  .join(", ")}{" "}
                changed since this was suggested
              </strong>
              <p>Check it again against your current settings before applying.</p>
              <Button
                variant="secondary"
                size="sm"
                disabled={busy || data.ai.running}
                onClick={async () => {
                  if (
                    await mutate(
                      "/investigations/run",
                      {
                        question: `Check this earlier suggestion again against the current settings: ${proposal.title}. ${proposal.summary}`,
                      },
                      "Checking it again.",
                    )
                  ) {
                    close();
                    navigate("#/insights");
                  }
                }}
              >
                Check it again
              </Button>
            </div>
          )}

          <section className="review-saving" aria-label="Saving">
            <p>
              <strong>{saving?.headline}</strong>
              {confidence && <Chip tone="neutral">{confidence}</Chip>}
            </p>
            {saving?.reason && (
              <p className="muted">
                <RichText text={saving.reason} />
              </p>
            )}
            {proposal.expectedEffect && (
              <p>
                <RichText text={proposal.expectedEffect} />
              </p>
            )}
          </section>

          {proposal.tradeoff && (
            <section aria-label="Tradeoff">
              <h4>The tradeoff</h4>
              <p>
                <RichText text={proposal.tradeoff} />
              </p>
            </section>
          )}

          <Calibration p={proposal} />

          {pending && writesOff && (
            <section className="review-manual" aria-label="Change this in Predbat yourself">
              <h4>Change this in Predbat yourself</h4>
              <p className="muted">
                Joule can't change Predbat on this install (live writes are off). Set it in Predbat or Home Assistant,
                then mark it done.
              </p>
              <ul className="manual-steps">
                {proposal.changes.map((c) => {
                  const v = changeView(c, settings);
                  const entity = settings.find((x) => x.key === c.key)?.entityId;
                  return (
                    <li key={c.key}>
                      <span>
                        Set <strong>{v.name}</strong>
                        {entity && <code>{entity}</code>} to <strong>{c.after}</strong>
                      </span>
                      <Button
                        variant="secondary"
                        size="sm"
                        onClick={async () => setCopied((await copyText(c.after)) ? c.key : "")}
                        aria-label={`Copy the new value for ${v.name}`}
                      >
                        {copied === c.key ? (
                          <Check size={14} aria-hidden="true" />
                        ) : (
                          <Copy size={14} aria-hidden="true" />
                        )}
                        {copied === c.key ? "Copied" : "Copy value"}
                      </Button>
                    </li>
                  );
                })}
              </ul>
            </section>
          )}

          {pending && !writesOff && blocked && (
            <p className="callout" role="status">
              {s.mode === "Monitor"
                ? `Joule is set to ${modeLabel("Monitor")}, so it can't apply changes. Choose ${modeLabel("Recommend")} in Setup › Predbat settings.`
                : "Deal with the warning at the top of the page before applying changes."}
            </p>
          )}

          {pending && !writesOff && autoEligible && (
            <label className="review-allow">
              <input type="checkbox" checked={allowFuture} onChange={(e) => setAllowFuture(e.target.checked)} />
              <span>
                Let Joule adjust {proposal.changes.length === 1 ? "this setting" : "these settings"} itself in future
                <small>
                  Small steps only, within the limits in Setup › Predbat settings. You can turn this off any time.
                </small>
              </span>
            </label>
          )}

          <Disclosure summary="How it's checked">
            <p className="body-copy">
              Joule compares the {proposal.reviewDays} days after the change with the days before, starting{" "}
              {dayLabel(Date.now())}, and shows the result under Trials. Nothing is undone on its own unless you allow
              it, the figures are good enough and nothing else changed at the same time.
            </p>
            <Suspense fallback={<LoadingBlock height={200} label="Loading the expected effect" />}>
              <ImpactPreviewPanel proposalId={proposal.id} api={api} />
            </Suspense>
          </Disclosure>

          {proposal.evidence.length > 0 && (
            <Disclosure summary="Why Joule suggested it" count={proposal.evidence.length}>
              <ul className="review-evidence">
                {proposal.evidence.map((e, i) => (
                  <li key={i}>
                    <RichText text={e} />
                  </li>
                ))}
              </ul>
              {proposal.investigationId && (
                <Button
                  variant="link"
                  size="sm"
                  onClick={() => {
                    close();
                    navigate(buildHash("insights", "", { id: proposal.investigationId }));
                  }}
                >
                  Open the check behind this
                </Button>
              )}
            </Disclosure>
          )}
          {!pending && <p className="muted">This suggestion is closed ({proposal.status.toLowerCase()}).</p>}
        </div>
      )}
    </Modal>
  );
}
