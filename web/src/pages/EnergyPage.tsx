import { useEffect, useState } from "react";
import { useApp } from "../context/AppContext";
import { EnergyFigures, SensorHealth } from "../components/EnergyEvidence";
import { SavedReports } from "../components/ReportsPage";
import type { EnergyPeriod } from "../lib/period";
import type { EnergyReport } from "../completion-types";
import "./energy.css";

/**
 * Energy: what the meters recorded for a period, the sensors' health and the saved reports, on one page (#/energy).
 * #/energy/reports opens the same page at the saved reports; ?report=<id> opens one.
 */
export default function EnergyPage() {
  const { api, load, measured, route, reportError, notify, data } = useApp();
  const [saving, setSaving] = useState(false);
  // The report to open and scroll to; `n` changes on every request, so opening the same report twice still scrolls.
  const [focus, setFocus] = useState<{ id: string; n: number } | null>(null);
  // The figures load first and push the reports down, so #/energy/reports scrolls once they are on screen.
  const [ready, setReady] = useState(false);

  useEffect(() => {
    if (!ready || route.section !== "reports" || route.query.get("report")) return;
    requestAnimationFrame(() => document.getElementById("saved-reports")?.scrollIntoView({ block: "start" }));
  }, [ready, route.section, route.query]);

  const open = (id: string) => setFocus((f) => ({ id, n: (f?.n ?? 0) + 1 }));

  async function saveReport(p: EnergyPeriod) {
    setSaving(true);
    try {
      const kind = p.days === 1 ? "Daily" : "Custom";
      const known = new Set(data.state.reports.map((r) => r.id));
      // The server returns the report already saved for exactly this period rather than making a second one.
      const report = await api<EnergyReport>("/reports/generate", {
        kind,
        from: p.from.toISOString(),
        to: p.to.toISOString(),
      });
      await load();
      open(report.id);
      notify(known.has(report.id) ? "You'd already saved these dates. Here's that report." : "Report saved.");
    } catch (e) {
      reportError((e as Error).message);
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="energy-page">
      <EnergyFigures
        onSaveReport={(p) => void saveReport(p)}
        onOpenReport={open}
        saving={saving}
        onReady={() => setReady(true)}
      />
      <SensorHealth status={measured.telemetry} />
      <SavedReports highlight={focus?.id ?? null} focusKey={focus?.n} ready={ready} />
    </div>
  );
}
