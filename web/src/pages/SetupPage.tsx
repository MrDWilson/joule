import { useState } from "react";
import { ArrowRight, BellRing, Bot, Database, FileCog, History, Info, PlayCircle, Settings2 } from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { useApp } from "../context/AppContext";
import { Chip, type Tone } from "../components/ui/Chip";
import { Disclosure } from "../components/ui";
import { LoadingBlock } from "../components/ui/States";
import { SetupChecklist } from "../components/setup/SetupChecklist";
import { EnvSnippet } from "../components/setup/EnvSnippet";
import { useRoute } from "../lib/router";
import { modeLabel, plural } from "../lib/copy";
import { providerLabel } from "../lib/labels";
import { buildTimeline, predbatVersion } from "../lib/changes";
import { isTunable } from "../lib/settings";
import { checklistCounts, useSetupStatus } from "../lib/setupApi";
import { generateAccessKey, useSetupConfig, type SetupConfig } from "../lib/setupConfig";
import { GoLiveForm } from "../components/setup/SetupForms";
import { dayTime } from "../lib/time";
import SettingsPage from "./SettingsPage";
import AiCostsPage from "./AiCostsPage";
import SensorsPage from "./SensorsPage";
import NotificationsPage from "./NotificationsPage";
import FilesPage from "./FilesPage";
import HistoryPage from "./HistoryPage";
import AboutPage from "./AboutPage";
import "./setup.css";

/** Setup: connections and configuration. #/setup is the checklist and overview; each area has its own page. */
export default function SetupPage() {
  const route = useRoute();
  switch (route.section) {
    case "settings":
      return <SettingsPage />;
    case "ai":
      return <AiCostsPage />;
    case "sensors":
      return <SensorsPage />;
    case "notifications":
      return <NotificationsPage />;
    case "files":
      return <FilesPage />;
    case "changes":
      return <HistoryPage />;
    case "about":
      return <AboutPage />;
    default:
      return <SetupOverview />;
  }
}

interface AreaCard {
  href: string;
  icon: LucideIcon;
  title: string;
  status: string;
  tone: Tone;
  detail: string;
}

/** Environment lines for people who'd rather configure Joule in compose.yaml or .env than in this page. */
function EnvironmentAlternative({ open = false }: { open?: boolean }) {
  const [key] = useState(() => generateAccessKey());
  return (
    <Disclosure summary="Prefer environment variables?" defaultOpen={open}>
      <p className="muted">
        Add these to Joule's <code className="entity-id">.env</code> (or under{" "}
        <code className="entity-id">environment:</code> in compose.yaml), then run{" "}
        <code className="entity-id">docker compose up -d</code>. Environment variables always win over what this page
        saves.
      </p>
      <EnvSnippet
        label="Go live"
        lines={["App__Demo=false", "Predbat__BaseUrl=http://predbat:5052", `App__AccessKey=${key}`]}
      />
    </Disclosure>
  );
}

/** Demo mode: what the demo shows, and how to switch to your own Predbat, right here. */
function ConnectMyPredbat({ config, error }: { config: SetupConfig | null; error: string }) {
  return (
    <section className="setup-hero" aria-labelledby="connect-heading">
      <div className="setup-hero-head">
        <span className="files-card-icon" aria-hidden="true">
          <PlayCircle size={18} />
        </span>
        <div>
          <h2 id="connect-heading">You're looking at the demo</h2>
          <p className="muted">
            A made-up house, plan and meters, so you can try everything safely. Approvals here are saved locally and
            never sent anywhere. When you're ready, connect your own Predbat below.
          </p>
        </div>
      </div>
      <Disclosure summary="Connect my Predbat" defaultOpen>
        {config ? (
          config.canSave ? (
            <>
              <GoLiveForm config={config} />
              <EnvironmentAlternative />
            </>
          ) : (
            <>
              <p className="sheet-warning" role="status">
                {config.locked}
              </p>
              <EnvironmentAlternative open />
            </>
          )
        ) : error ? (
          <>
            <p className="sheet-warning" role="alert">
              {error}
            </p>
            <EnvironmentAlternative open />
          </>
        ) : (
          <LoadingBlock lines={3} label="Loading Joule's settings" />
        )}
      </Disclosure>
    </section>
  );
}

function SetupOverview() {
  const { data, measured, health, mutate, busy, about } = useApp();
  const s = data.state,
    c = data.connection,
    t = measured.telemetry;
  const { status, error, refresh } = useSetupStatus([
    s.revision,
    s.lastCollection,
    s.collectionError,
    t?.lastCollection,
  ]);
  const { config, error: configError } = useSetupConfig();
  const counts = status ? checklistCounts(status.progress) : null;
  const predbatRow = health.rows.find((r) => r.key === "predbat")!;
  const sensorRow = health.rows.find((r) => r.key === "sensors")!;
  const mapped = t ? Object.keys(t.entityMappings ?? {}).length : 0;
  const missing = t?.missingMappings?.length ?? 0;
  const changes = buildTimeline({
    revisions: s.revisions,
    settings: s.settings,
    experiments: s.experiments,
    settingEvents: s.settingEvents,
  });
  const tunable = s.settings.filter(isTunable).length;
  const predbat = predbatVersion(about.predbatVersion ?? status?.predbat.version);
  const cards: AreaCard[] = [
    {
      href: "#/setup/settings",
      icon: Settings2,
      title: "Predbat settings",
      status: c.demo
        ? "Demo"
        : !c.predbatConfigured
          ? "Not set up"
          : s.collectionError
            ? "Can't reach"
            : c.writesEnabled
              ? "Connected"
              : "Read-only",
      tone: predbatRow.tone,
      detail: `${plural(tunable, "setting")} you can tune · AI: ${modeLabel(s.mode)}.`,
    },
    {
      href: "#/setup/sensors",
      icon: Database,
      title: "Sensors",
      status: t?.demo
        ? "Demo"
        : !t?.configured
          ? "Not set up"
          : missing
            ? `${mapped} of ${mapped + missing} mapped`
            : "Mapped",
      tone: sensorRow.tone,
      detail: sensorRow.detail,
    },
    {
      href: "#/setup/ai",
      icon: Bot,
      title: "AI checks",
      status: providerLabel(s.ai.provider),
      tone: "info",
      detail: s.ai.scheduled ? `Checks by itself, at most ${s.ai.maxRunsPerDay} a day.` : "Automatic checks off.",
    },
    {
      href: "#/setup/notifications",
      icon: BellRing,
      title: "Notifications",
      status: "",
      tone: "neutral",
      detail: "Get what needs you on your phone: ntfy, Pushover, Home Assistant, Telegram and more.",
    },
    {
      href: "#/setup/changes",
      icon: History,
      title: "Changes",
      status: changes.length ? plural(changes.length, "change") : "",
      tone: "neutral",
      detail: changes.length
        ? `Last: ${changes[0].title.replace(/\.$/, "")} · ${dayTime(changes[0].at)}.`
        : "Every settings change appears here.",
    },
    {
      href: "#/setup/files",
      icon: FileCog,
      title: "Files",
      status: s.lastFileVersionId ? "Copies saved" : "Not set up",
      tone: s.lastFileVersionId ? "success" : "neutral",
      detail: s.lastFileVersionId
        ? "Copies of apps.yaml, compared setting by setting."
        : "Optional · keep copies of apps.yaml",
    },
    {
      href: "#/setup/about",
      icon: Info,
      title: "About",
      status: "",
      tone: "neutral",
      detail: `Joule ${about.version}${predbat ? ` · Predbat ${predbat}` : ""} · documentation and help.`,
    },
  ];

  return (
    <div className="setup-page">
      {c.demo ? (
        <ConnectMyPredbat config={config} error={configError} />
      ) : (
        <section className="setup-hero" aria-labelledby="checklist-heading">
          <div className="setup-hero-head">
            <div>
              <h2 id="checklist-heading">{counts?.requiredDone ? "Joule is set up" : "Get Joule running"}</h2>
              <p className="muted">
                {counts
                  ? counts.requiredDone
                    ? `${counts.done} of ${counts.total} done. The rest are optional extras.`
                    : `${counts.done} of ${counts.total} done. Joule opens here until the first ${status!.progress.steps.filter((x) => x.required && x.key !== "collecting").length} steps are done.`
                  : "Checking what's connected…"}
              </p>
            </div>
            {counts && (
              <span className="setup-progress" role="img" aria-label={`${counts.done} of ${counts.total} steps done`}>
                <span style={{ width: `${(counts.done / counts.total) * 100}%` }} />
              </span>
            )}
          </div>
          {status ? (
            counts?.requiredDone ? (
              <Disclosure summary="Show the checklist" count={`${counts.done}/${counts.total}`}>
                <SetupChecklist
                  status={status}
                  data={data}
                  mutate={mutate}
                  busy={busy}
                  onRefresh={refresh}
                  config={config}
                />
              </Disclosure>
            ) : (
              <SetupChecklist
                status={status}
                data={data}
                mutate={mutate}
                busy={busy}
                onRefresh={refresh}
                config={config}
              />
            )
          ) : error ? (
            <p className="sheet-warning" role="alert">
              {error}
            </p>
          ) : (
            <LoadingBlock lines={4} label="Loading the setup checklist" />
          )}
        </section>
      )}

      <div className="setup-grid">
        {cards.map((card) => {
          const Icon = card.icon;
          return (
            <a key={card.href} href={card.href} className="setup-card">
              <span className="setup-card-icon" aria-hidden="true">
                <Icon size={20} />
              </span>
              <span className="setup-card-copy">
                <span className="setup-card-title">
                  <strong>{card.title}</strong>
                  {card.status && (
                    <Chip tone={card.tone} dot>
                      {card.status}
                    </Chip>
                  )}
                </span>
                <span className="setup-card-detail">{card.detail}</span>
              </span>
              <ArrowRight size={18} aria-hidden="true" className="setup-card-arrow" />
            </a>
          );
        })}
      </div>
    </div>
  );
}
