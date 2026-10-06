import { useMemo, useState } from "react";
import { ChevronRight, Search, X } from "lucide-react";
import { useApp } from "../context/AppContext";
import { Button, Switch } from "../components/ui";
import { EmptyState } from "../components/ui/States";
import { PermissionBounds } from "../components/PermissionBounds";
import { ModePicker, limitsText } from "../components/setup/ModePicker";
import { SettingSheet } from "../components/setup/SettingSheet";
import { modeLabel, plural, type Mode } from "../lib/copy";
import {
  applyFilter,
  bySection,
  changedFromDefault,
  displayValue,
  editLock,
  filterLabels,
  isTunable,
  matchesQuery,
  riskBadge,
  statusGroups,
  statusValue,
  type SettingsFilter,
} from "../lib/settings";
import type { Setting } from "../types";
import "./setup.css";

const FILTERS: SettingsFilter[] = ["common", "changed", "ai", "all"];

function SettingRow({ setting, onOpen }: { setting: Setting; onOpen: (s: Setting) => void }) {
  const risk = riskBadge(setting);
  const value = displayValue(setting, setting.value);
  const changed = changedFromDefault(setting);
  return (
    <li>
      <button type="button" className="setting-line" aria-haspopup="dialog" onClick={() => onOpen(setting)}>
        <span className="setting-line-name">
          <span className="setting-line-title">{setting.name}</span>
          {(risk || changed || setting.autoAllowed) && (
            <span className="setting-line-tags">
              {risk && <span className={`setting-tag ${risk.tone}`}>{risk.label}</span>}
              {changed && <span className="setting-tag">Changed from default</span>}
              {setting.autoAllowed && <span className="setting-tag ai">AI may change</span>}
            </span>
          )}
        </span>
        <span className="value-chip" title={value.length > 24 ? value : undefined}>
          {value}
        </span>
        <ChevronRight size={16} aria-hidden="true" className="setting-line-chevron" />
      </button>
    </li>
  );
}

/** Predbat's settings, grouped as Predbat's docs group them, and what the AI may change. */
export default function SettingsPage() {
  const { api, mutate, busy, data, route } = useApp();
  const s = data.state,
    c = data.connection;
  const tunables = useMemo(() => s.settings.filter(isTunable), [s.settings]);
  const hasCommon = tunables.some((x) => x.commonlyTuned);
  const [filter, setFilter] = useState<SettingsFilter>(hasCommon ? "common" : "all");
  const [query, setQuery] = useState("");
  const [openKey, setOpenKey] = useState(() => route.query.get("setting") ?? "");
  const [limits, setLimits] = useState<Setting | null>(null);
  const lock = editLock(data);
  const writesOff = !c.demo && !c.writesEnabled;
  const searching = query.trim().length > 0;

  const counts = useMemo(
    () =>
      Object.fromEntries(FILTERS.map((f) => [f, applyFilter(s.settings, f).length])) as Record<SettingsFilter, number>,
    [s.settings],
  );
  const shown = useMemo(
    () => (searching ? tunables.filter((x) => matchesQuery(x, query)) : applyFilter(s.settings, filter)),
    [searching, tunables, query, s.settings, filter],
  );
  const sections = bySection(shown);
  const status = useMemo(
    () =>
      statusGroups(s.settings)
        .map((g) => ({ ...g, settings: g.settings.filter((x) => !searching || matchesQuery(x, query)) }))
        .filter((g) => g.settings.length),
    [s.settings, searching, query],
  );
  const statusCount = status.reduce((n, g) => n + g.settings.length, 0);
  const open = s.settings.find((x) => x.key === openKey) ?? null;
  // Collapsed by default only in "All"; a search or a narrow filter shows everything it found.
  const expanded = searching || filter !== "all";
  // "AI may change" lists every setting the AI could be allowed to change, each with its own switch.
  const aiView = !searching && filter === "ai";
  const eligible = useMemo(() => tunables.filter((x) => x.autoEligible), [tunables]);

  return (
    <div className="settings-page">
      <ModePicker
        mode={s.mode}
        settings={s.settings}
        busy={busy}
        writesOff={writesOff}
        onChange={(m: Mode) => mutate("/mode", { mode: m }, `The AI is now set to ${modeLabel(m)}.`)}
      />
      <div className="settings-toolbar">
        <label className="search settings-search">
          <Search size={16} aria-hidden="true" />
          <input
            type="search"
            aria-label="Search settings"
            placeholder={`Search ${counts.all} settings`}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
          {searching && (
            <button type="button" className="search-clear" aria-label="Clear search" onClick={() => setQuery("")}>
              <X size={14} aria-hidden="true" />
            </button>
          )}
        </label>
        <div className="filter-chips scroll-row" role="group" aria-label="Show settings">
          {FILTERS.filter((f) => f !== "common" || hasCommon).map((f) => (
            <button
              key={f}
              type="button"
              className="filter-chip"
              aria-pressed={!searching && filter === f}
              onClick={() => {
                setFilter(f);
                setQuery("");
              }}
            >
              {filterLabels[f]}
              {/* What the AI may change only counts in Automatic mode; a number there otherwise suggests it's active. */}
              {(f !== "ai" || s.mode === "Auto") && <span className="filter-chip-count">{counts[f]}</span>}
            </button>
          ))}
        </div>
      </div>

      {searching && (
        <p className="muted settings-result" role="status">
          {plural(shown.length + statusCount, "setting")} {shown.length + statusCount === 1 ? "matches" : "match"} “
          {query.trim()}”
        </p>
      )}

      {aiView && (
        <section className="auto-permissions" aria-labelledby="auto-permissions-title">
          <h2 id="auto-permissions-title" className="sr-only">
            Settings the AI may change by itself
          </h2>
          <p className="auto-permissions-note">
            The AI only ever changes these low-risk numbers by itself, within limits you set. These only apply when “How
            the AI helps” is set to Automatic
            {s.mode === "Auto" ? "." : `, and it's on ${modeLabel(s.mode)} now.`}
          </p>
          {eligible.length ? (
            <ul className="setting-lines auto-permission-list">
              {eligible.map((x) => (
                <li key={x.key} className="auto-permission">
                  <button
                    type="button"
                    className="auto-permission-name"
                    aria-haspopup="dialog"
                    onClick={() => setOpenKey(x.key)}
                  >
                    <span className="setting-line-title">{x.name}</span>
                    <span className="auto-permission-detail">
                      {displayValue(x, x.value)}
                      {x.autoAllowed ? ` · ${limitsText(x)}` : ""}
                    </span>
                  </button>
                  <label className="auto-permission-switch">
                    <span className="sr-only">Allow automatic changes to {x.name}</span>
                    <span aria-hidden="true">{x.autoAllowed ? "Allowed" : "Off"}</span>
                    <Switch
                      checked={x.autoAllowed}
                      label={`Allow automatic changes to ${x.name}`}
                      disabled={busy}
                      onCheckedChange={(on) => {
                        if (on) setLimits(x);
                        else void mutate(`/permissions/${x.key}`, { allowed: false }, "Automatic changes turned off.");
                      }}
                    />
                  </label>
                </li>
              ))}
            </ul>
          ) : (
            <p className="muted">None of Predbat's settings are low-risk numbers the AI could change by itself.</p>
          )}
        </section>
      )}

      {!aiView &&
        sections.map(({ section, settings }) => (
          <details key={`${section}-${filter}-${searching}`} className="settings-section" open={expanded || undefined}>
            <summary>
              <span className="settings-section-title">{section}</span>
              <span className="disclosure-count">{settings.length}</span>
              <ChevronRight size={16} aria-hidden="true" className="settings-section-chevron" />
            </summary>
            <ul className="setting-lines">
              {settings.map((x) => (
                <SettingRow key={x.key} setting={x} onOpen={(st) => setOpenKey(st.key)} />
              ))}
            </ul>
          </details>
        ))}

      {!aiView && !sections.length && (
        <EmptyState title={searching ? "No settings match" : "Nothing here"}>
          {searching
            ? "Try a shorter word, or the name Predbat uses."
            : filter === "changed"
              ? "Every setting Joule knows a default for is at Predbat's default."
              : s.settings.length
                ? "No settings in this view."
                : "Predbat's settings appear here after Joule first reads Predbat."}
        </EmptyState>
      )}
      {!searching && filter !== "all" && counts.all > shown.length && (
        <Button variant="link" className="settings-more" onClick={() => setFilter("all")}>
          Show all {counts.all} settings
        </Button>
      )}

      {statusCount > 0 && (
        <details key={`status-${searching}`} className="settings-section settings-status" open={searching || undefined}>
          <summary>
            <span className="settings-section-title">
              Predbat status <span className="muted">· read-only</span>
            </span>
            <span className="disclosure-count">{statusCount}</span>
            <ChevronRight size={16} aria-hidden="true" className="settings-section-chevron" />
          </summary>
          <p className="muted settings-status-note">
            Predbat's own controls: its mode, version, manual overrides and diagnostics. Joule shows them but never
            changes or restores them; changes appear on the Changes timeline.
          </p>
          {status.map((g) => (
            <div key={g.kind} className="status-group">
              <h3>{g.label}</h3>
              <ul className="setting-lines">
                {g.settings.map((x) => {
                  const { value: v, detail } = statusValue(x, x.value);
                  return (
                    <li key={x.key} className="setting-line static">
                      <span className="setting-line-name">
                        <span className="setting-line-title">{x.name}</span>
                        {detail && <span className="setting-line-detail">{detail}</span>}
                      </span>
                      <span className="value-chip quiet" title={v.length > 24 ? v : undefined}>
                        {v}
                      </span>
                    </li>
                  );
                })}
              </ul>
            </div>
          ))}
        </details>
      )}

      <p className="muted settings-footnote">
        <span>Settings version {s.revision}</span> ·{" "}
        <a href="#/setup/changes" className="text-link">
          See every change
        </a>
      </p>

      {open && (
        <SettingSheet
          key={open.key}
          setting={open}
          revision={s.revision}
          revisions={s.revisions}
          busy={busy}
          lock={!open.editable ? "This setting is read-only in Predbat" : lock}
          mutate={mutate}
          onClose={() => setOpenKey("")}
          onLimits={(x) => {
            // One dialog at a time: the limits replace the sheet, and closing them comes back to it.
            setOpenKey("");
            setLimits(x);
          }}
        />
      )}
      {limits && (
        <PermissionBounds
          api={api}
          mutate={mutate}
          busy={busy}
          setting={limits}
          close={() => {
            setOpenKey(limits.key);
            setLimits(null);
          }}
        />
      )}
    </div>
  );
}
