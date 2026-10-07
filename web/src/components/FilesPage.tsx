import { useEffect, useMemo, useRef, useState } from "react";
import { Archive, Eye, FileCog, FileText, RotateCcw, TriangleAlert } from "lucide-react";
import { Button, Chip, Disclosure, Modal } from "./ui";
import { EmptyState, LoadingBlock } from "./ui/States";
import { PlainText } from "./PlainText";
import { EnvSnippet } from "./setup/EnvSnippet";
import { dayTime, when } from "../lib/time";
import { plural } from "../lib/copy";
import { api as callApi } from "../lib/api";
import type { PredbatAppsView } from "../lib/setupApi";
import type { FileDiff, FileInventory, FilesProps, FileVersion, FileView } from "../completion-types";
import { EDIT_VOLUME_LINES, useConfigEditStatus } from "../lib/configEdits";
import { useSetupConfig } from "../lib/setupConfig";
import { ConfigEditSetup } from "./setup/ConfigEditPermission";

interface RestoreDraft {
  target: FileVersion;
  revision: number;
  latestVersion: string;
  hashes: Record<string, string>;
}

const SETUP_LINES = ["ConfigFiles__Root=/predbat-config", "ConfigFiles__AllowedFiles__0=apps.yaml"];

/** Predbat's apps.yaml read through Predbat's MCP (already masked by Predbat; anything secret-looking hidden again). */
function PredbatApps() {
  const [view, setView] = useState<PredbatAppsView | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  return (
    <section className="files-card" aria-labelledby="mcp-apps">
      <div className="files-card-head">
        <span className="files-card-icon" aria-hidden="true">
          <FileText size={18} />
        </span>
        <div>
          <h2 id="mcp-apps">Predbat's apps.yaml, read-only</h2>
          <p className="muted">
            Read through Predbat's MCP connection. Secrets stay hidden; nothing can be changed here.
          </p>
        </div>
      </div>
      {!view && (
        <Button
          variant="secondary"
          disabled={loading}
          onClick={async () => {
            setLoading(true);
            setError("");
            try {
              setView(await callApi<PredbatAppsView>("/setup/predbat-apps"));
            } catch (e) {
              setError((e as Error).message);
            } finally {
              setLoading(false);
            }
          }}
        >
          <Eye size={15} aria-hidden="true" />
          {loading ? "Reading apps.yaml…" : "Show apps.yaml"}
        </Button>
      )}
      {error && (
        <p role="alert" className="sheet-warning">
          <TriangleAlert size={14} aria-hidden="true" />
          {error}
        </p>
      )}
      {view && !view.available && (
        <p className="sheet-warning" role="status">
          <TriangleAlert size={14} aria-hidden="true" />
          <PlainText text={view.reason ?? "Predbat didn't return its apps.yaml."} />
        </p>
      )}
      {view?.available && (
        <>
          <p className="muted">
            Read {dayTime(view.at)}
            {view.truncated ? " · cut short (the file is very long)" : ""}
          </p>
          <pre className="file-text" tabIndex={0} aria-label="Predbat's apps.yaml">
            {view.text}
          </pre>
        </>
      )}
    </section>
  );
}

/** Setup › Files: saved copies of Predbat's configuration files, compared by setting and restorable as a whole. */
export function FilesPage({
  api,
  mutate,
  busy,
  revision,
  revisions,
  demo,
  pendingReload,
  mode,
  writeUncertain,
  onHistory,
  mcpConfigured = false,
}: FilesProps & { mcpConfigured?: boolean }) {
  const [contentRetry, setContentRetry] = useState(0),
    [inventory, setInventory] = useState<FileInventory | null>(null),
    [error, setError] = useState(""),
    [loading, setLoading] = useState(false),
    [selected, setSelected] = useState(""),
    [file, setFile] = useState(""),
    [compare, setCompare] = useState(""),
    [view, setView] = useState<FileView | null>(null),
    [diff, setDiff] = useState<FileDiff | null>(null),
    [capture, setCapture] = useState<{ revision: number } | null>(null),
    [reason, setReason] = useState(""),
    [restore, setRestore] = useState<RestoreDraft | null>(null),
    [notes, setNotes] = useState(""),
    [confirmed, setConfirmed] = useState(false),
    [reconcile, setReconcile] = useState<{ revision: number } | null>(null),
    [reload, setReload] = useState<{ revision: number } | null>(null),
    [showAll, setShowAll] = useState(false);
  const viewer = useRef<HTMLElement>(null);
  const editStatus = useConfigEditStatus();
  const { config: setupConfig } = useSetupConfig();
  // Only the latest inventory request is applied, so overlapping loads can't refetch the file view twice.
  const loadRequest = useRef(0);
  async function load() {
    const request = ++loadRequest.current;
    setLoading(true);
    setError("");
    try {
      const r = await api<FileInventory>("/files");
      if (request !== loadRequest.current) return;
      setInventory(r);
      setSelected((old) => old || r.status.latestVersion || "");
      setFile((old) => old || r.versions.at(-1)?.files[0]?.path || "");
      setContentRetry((n) => n + 1);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  }
  useEffect(() => {
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, revision]);
  useEffect(() => {
    let active = true;
    setView(null);
    setDiff(null);
    if (!selected || !file) return;
    setError("");
    const path = `/files/${encodeURIComponent(selected)}`;
    const params = new URLSearchParams({ file });
    if (compare) params.set("other", compare);
    (compare ? api<FileDiff>(`${path}/diff?${params}`) : api<FileView>(`${path}/view?${params}`))
      .then((r) => {
        if (active) {
          if ("before" in r) setDiff(r);
          else setView(r);
        }
      })
      .catch((e: Error) => {
        if (active) setError(e.message);
      });
    return () => {
      active = false;
    };
  }, [api, selected, file, compare, contentRetry]);

  const versions = useMemo(() => [...(inventory?.versions ?? [])].reverse(), [inventory]);
  const version = inventory?.versions.find((v) => v.id === selected),
    latest = inventory?.versions.find((v) => v.id === inventory.status.latestVersion);
  const guarded = inventory?.status.quarantined || !inventory?.status.enabled;
  const restoreLock = writeUncertain
    ? "Re-read Predbat's settings first"
    : mode === "Monitor"
      ? "Restoring is locked while Joule is in Watch only"
      : "";
  const earlier = (v: FileVersion) =>
    inventory?.versions.filter(
      (x) => x.id !== v.id && Date.parse(x.at) < Date.parse(v.at) && x.files.some((f) => f.path === file),
    ) ?? [];

  if (!inventory && loading) return <LoadingBlock lines={4} label="Loading saved files" />;
  if (!inventory && error)
    return (
      <EmptyState title="Couldn't load the saved files">
        {error}{" "}
        <Button variant="secondary" size="sm" onClick={() => void load()}>
          Retry files
        </Button>
      </EmptyState>
    );

  if (inventory && !inventory.status.enabled)
    return (
      <div className="files-page">
        <section className="files-card files-setup" aria-labelledby="files-setup">
          <div className="files-card-head">
            <span className="files-card-icon" aria-hidden="true">
              <FileCog size={18} />
            </span>
            <div>
              <h2 id="files-setup">Keep copies of apps.yaml</h2>
              <p className="muted">
                Joule can save a copy of Predbat's configuration files whenever they change, so you can see which
                settings changed, and roll a file back if an edit goes wrong. Values stay on the server; you see keys
                and layout only.
              </p>
            </div>
            <Chip tone="neutral">Not set up</Chip>
          </div>
          <ol className="files-steps">
            <li>
              Mount Predbat's configuration folder (the one holding apps.yaml) into Joule's container, in Joule's
              docker-compose service:
            </li>
          </ol>
          <EnvSnippet lines={EDIT_VOLUME_LINES} label="Compose volume for Predbat's config folder" />
          <ol className="files-steps" start={2}>
            <li>Add these lines to Joule's environment and restart Joule:</li>
          </ol>
          <EnvSnippet lines={SETUP_LINES} label="File copy settings" />
          <p className="muted">
            Add <code className="entity-id">ConfigFiles__AllowEdits=true</code> too and Joule can also make the AI's
            apps.yaml edits for you, keeping a copy it can put back.
          </p>
        </section>
        {mcpConfigured && <PredbatApps />}
      </div>
    );

  return (
    <div className="files-page">
      {error && (
        <p role="alert" className="sheet-warning">
          <TriangleAlert size={14} aria-hidden="true" />
          <span>{error}</span>
          <Button variant="secondary" size="sm" onClick={() => void load()}>
            Retry files
          </Button>
        </p>
      )}
      {inventory?.status.quarantined && (
        <div role="alert" className="files-alert">
          <TriangleAlert size={16} aria-hidden="true" />
          <div>
            <strong>A file restore didn't finish cleanly</strong>
            <p>
              <PlainText text={inventory.status.reason} /> Check the files and Predbat itself, then confirm.
            </p>
          </div>
          <Button
            variant="secondary"
            size="sm"
            disabled={busy}
            onClick={() => {
              setReconcile({ revision });
              setNotes("");
              setConfirmed(false);
            }}
          >
            Confirm files are OK
          </Button>
        </div>
      )}
      {pendingReload && (
        <div role="status" className="files-alert">
          <TriangleAlert size={16} aria-hidden="true" />
          <div>
            <strong>Restart Predbat to use the restored files</strong>
            <p>
              Joule can't tell whether Predbat has picked them up yet. Restart it, re-read its settings, then confirm.
            </p>
          </div>
          <div className="files-alert-actions">
            <Button
              variant="secondary"
              size="sm"
              disabled={busy}
              onClick={() => void mutate("/collect", {}, "Predbat’s settings re-read. Check them before confirming.")}
            >
              Re-read Predbat settings
            </Button>
            <Button
              size="sm"
              disabled={busy}
              onClick={() => {
                setReload({ revision });
                setNotes("");
                setConfirmed(false);
              }}
            >
              Confirm Predbat restarted
            </Button>
          </div>
        </div>
      )}

      <div className="files-status">
        <span className="files-card-icon" aria-hidden="true">
          <Archive size={18} />
        </span>
        <p>
          <strong>Keeping copies of {inventory?.status.allowedFiles.join(", ")}</strong>
          <span className="muted">
            {demo ? "Demo: copies of the demo's settings file and its sample apps.yaml. " : ""}
            {plural(versions.length, "copy", "copies")}
            {latest ? ` · latest ${dayTime(latest.at)}` : ""}
          </span>
        </p>
        <Button
          variant="secondary"
          disabled={busy || loading || guarded || writeUncertain}
          onClick={() => {
            setCapture({ revision });
            setReason("");
          }}
        >
          Save a copy now
        </Button>
      </div>

      {editStatus?.configured && (
        <section className="files-card" aria-labelledby="config-edits">
          <ConfigEditSetup status={editStatus} config={setupConfig} />
        </section>
      )}

      <section aria-labelledby="files-copies">
        <h2 id="files-copies" className="files-heading">
          Saved copies
        </h2>
        {!versions.length && (
          <p className="muted">No copies saved yet. Joule saves one whenever Predbat's files change.</p>
        )}
        <ol className="file-versions">
          {(showAll ? versions : versions.slice(0, 8)).map((v) => (
            <li className="file-version" key={v.id} data-version={v.id}>
              <div className="file-version-main">
                <p className="file-version-title">
                  {dayTime(v.at)}
                  {v.id === latest?.id && <Chip tone="success">Latest</Chip>}
                  {v.id === selected && <Chip tone="info">Showing</Chip>}
                </p>
                <p className="file-version-reason">
                  <PlainText text={v.reason} />
                </p>
                <Disclosure summary="Technical details" className="file-tech">
                  <p className="muted">
                    Copy <code>{v.id}</code> · saved with settings snapshot {v.runtimeRevision}
                    {v.restoredFrom ? ` · restored from ${v.restoredFrom}` : ""}
                  </p>
                  <ul className="file-hashes">
                    {v.files.map((f) => (
                      <li key={f.path}>
                        {f.path} · {f.bytes} bytes · SHA-256 <code className="hash">{f.hash}</code>
                      </li>
                    ))}
                  </ul>
                  {revisions.some((r) => r.id === v.runtimeRevision) && (
                    <Button variant="link" size="sm" onClick={onHistory}>
                      See the settings changes from then
                    </Button>
                  )}
                </Disclosure>
              </div>
              <div className="file-version-actions">
                <Button
                  variant="secondary"
                  size="sm"
                  onClick={() => {
                    setSelected(v.id);
                    setFile(v.files[0]?.path || "");
                    setCompare("");
                    viewer.current?.scrollIntoView({ behavior: "smooth", block: "start" });
                  }}
                >
                  View
                </Button>
                {latest && v.id !== latest.id && (
                  <Button
                    variant="ghost"
                    size="sm"
                    disabled={busy || loading || guarded || !!restoreLock}
                    title={restoreLock || undefined}
                    onClick={() => {
                      setRestore({
                        target: v,
                        revision,
                        latestVersion: latest.id,
                        hashes: Object.fromEntries(latest.files.map((f) => [f.path, f.hash])),
                      });
                      setNotes("");
                      setConfirmed(false);
                    }}
                  >
                    <RotateCcw size={14} aria-hidden="true" />
                    Restore these files
                  </Button>
                )}
              </div>
            </li>
          ))}
        </ol>
        {!showAll && versions.length > 8 && (
          <Button variant="secondary" onClick={() => setShowAll(true)}>
            Show {plural(versions.length - 8, "older copy", "older copies")}
          </Button>
        )}
        {restoreLock && versions.length > 1 && <p className="muted">{restoreLock}.</p>}
      </section>

      {versions.length > 0 && (
        <section ref={viewer} className="files-card files-viewer" aria-labelledby="files-viewer">
          <div className="files-viewer-head">
            <h2 id="files-viewer">{version ? `Copy from ${dayTime(version.at)}` : "Choose a copy"}</h2>
            <div className="files-viewer-controls">
              {(version?.files.length ?? 0) > 1 && (
                <label className="field">
                  File
                  <select aria-label="Archived file" value={file} onChange={(e) => setFile(e.target.value)}>
                    {version?.files.map((f) => (
                      <option key={f.path}>{f.path}</option>
                    ))}
                  </select>
                </label>
              )}
              {version && earlier(version).length > 0 && (
                <label className="field">
                  Compare with
                  <select
                    aria-label="Compare file version"
                    value={compare}
                    onChange={(e) => setCompare(e.target.value)}
                  >
                    <option value="">Nothing: just show this copy</option>
                    {[...earlier(version)].reverse().map((v) => (
                      <option key={v.id} value={v.id}>
                        The copy from {dayTime(v.at)}
                      </option>
                    ))}
                  </select>
                </label>
              )}
            </div>
          </div>
          {view && (
            // The masked file is for checking the layout, not reading values: closed until asked for.
            <Disclosure summary="Show file layout" className="files-layout">
              <p className="muted">{view.redaction}</p>
              <pre className="file-text" tabIndex={0} aria-label={`Saved copy of ${file}`}>
                {view.text}
              </pre>
            </Disclosure>
          )}
          {diff && (
            <>
              {!diff.changed ? (
                <p className="files-same">The two copies are identical.</p>
              ) : diff.keys?.length ? (
                <div>
                  <h3 className="sheet-subhead">{plural(diff.keys.length, "setting")} changed since that copy</h3>
                  <ul className="change-lines confirm">
                    {diff.keys.map((k) => (
                      <li key={`${k.key}-${k.change}`}>
                        <span className="change-line-name">{k.name}</span>
                        <span className={`key-change ${k.change}`}>{k.change}</span>
                      </li>
                    ))}
                  </ul>
                </div>
              ) : (
                <p className="files-same">The files differ only in layout or comments.</p>
              )}
              <Disclosure summary="Show both copies, line by line">
                <p className="muted">{diff.redaction}</p>
                <div className="files-compare">
                  <div>
                    <h4>Earlier</h4>
                    <pre className="file-text" tabIndex={0} aria-label={`${file} before`}>
                      {diff.before}
                    </pre>
                  </div>
                  <div>
                    <h4>This copy</h4>
                    <pre className="file-text" tabIndex={0} aria-label={`${file} in the selected copy`}>
                      {diff.after}
                    </pre>
                  </div>
                </div>
              </Disclosure>
            </>
          )}
        </section>
      )}
      {mcpConfigured && <PredbatApps />}

      <Modal
        open={!!reload}
        onOpenChange={(o) => {
          if (!o) setReload(null);
        }}
        title="Confirm Predbat restarted"
        description="Only confirm once Predbat has restarted and its settings match the restored files."
      >
        <form
          onSubmit={async (e) => {
            e.preventDefault();
            if (
              reload &&
              (await mutate(
                "/files/reload-acknowledge",
                { revision: reload.revision, notes },
                "Your reload verification was recorded.",
              ))
            ) {
              setReload(null);
              void load();
            }
          }}
        >
          <p className="body-copy">
            Restart Predbat the way you normally do, then re-read its settings and look over Predbat settings. This only
            records your check; Joule doesn't restart Predbat.
          </p>
          <label className="field">
            What did you check?
            <textarea required maxLength={4000} value={notes} onChange={(e) => setNotes(e.target.value)} />
          </label>
          <label className="check-field">
            <input type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} />I restarted
            Predbat and checked its settings.
          </label>
          <div className="dialog-actions">
            <Button variant="ghost" type="button" onClick={() => setReload(null)}>
              Cancel
            </Button>
            <Button disabled={busy || !confirmed || !notes.trim()}>Record my check</Button>
          </div>
        </form>
      </Modal>
      <Modal
        open={!!capture}
        onOpenChange={(o) => {
          if (!o) setCapture(null);
        }}
        title="Save a copy of the files"
        description="Keeps an exact copy of each file, which can't be changed later."
      >
        <form
          onSubmit={async (e) => {
            e.preventDefault();
            if (
              capture &&
              (await mutate("/files/capture", { revision: capture.revision, reason }, "A copy of the files was saved."))
            ) {
              setCapture(null);
              void load();
            }
          }}
        >
          <label className="field">
            Why are you saving a copy?
            <textarea required maxLength={4000} value={reason} onChange={(e) => setReason(e.target.value)} />
          </label>
          <div className="dialog-actions">
            <Button type="button" variant="ghost" onClick={() => setCapture(null)}>
              Cancel
            </Button>
            <Button disabled={busy || !reason.trim()}>Save copy</Button>
          </div>
        </form>
      </Modal>
      <Modal
        open={!!restore}
        onOpenChange={(o) => {
          if (!o) setRestore(null);
        }}
        title="Restore these files?"
        description="Every kept file is replaced. Restart Predbat afterwards and check its settings."
      >
        {restore && (
          <form
            onSubmit={async (e) => {
              e.preventDefault();
              if (
                await mutate(
                  `/files/${restore.target.id}/restore`,
                  {
                    revision: restore.revision,
                    expectedVersion: restore.latestVersion,
                    expectedHashes: restore.hashes,
                    notes,
                  },
                  "Files restored. Restart Predbat and check its settings.",
                )
              ) {
                setRestore(null);
                void load();
              }
            }}
          >
            <p className="body-copy">
              Puts back {plural(restore.target.files.length, "file")} (
              {restore.target.files.map((f) => f.path).join(", ")}) as they were on {when(restore.target.at)}. This only
              goes ahead if nothing has changed since you opened it. Predbat won't use them until it restarts.
            </p>
            <label className="field">
              Restore notes
              <textarea required value={notes} maxLength={4000} onChange={(e) => setNotes(e.target.value)} />
            </label>
            <label className="check-field">
              <input type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} />I understand
              every kept file will be replaced and Predbat needs a restart and a check.
            </label>
            <div className="dialog-actions">
              <Button type="button" variant="ghost" onClick={() => setRestore(null)}>
                Cancel
              </Button>
              <Button variant="danger" disabled={busy || !!restoreLock || !confirmed || !notes.trim()}>
                Confirm file restore
              </Button>
            </div>
          </form>
        )}
      </Modal>
      <Modal
        open={!!reconcile}
        onOpenChange={(o) => {
          if (!o) setReconcile(null);
        }}
        title="Confirm the files are OK"
        description="Only confirm after looking at the files and checking Predbat itself."
      >
        <form
          onSubmit={async (e) => {
            e.preventDefault();
            if (
              reconcile &&
              (await mutate("/files/reconcile", { revision: reconcile.revision, notes }, "Files confirmed as OK."))
            ) {
              setReconcile(null);
              void load();
            }
          }}
        >
          <label className="field">
            What did you verify?
            <textarea required value={notes} onChange={(e) => setNotes(e.target.value)} />
          </label>
          <label className="check-field">
            <input type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} />I checked the
            files and Predbat, and I’m happy with them as they are.
          </label>
          <div className="dialog-actions">
            <Button type="button" variant="ghost" onClick={() => setReconcile(null)}>
              Cancel
            </Button>
            <Button disabled={busy || !confirmed || !notes.trim()}>Confirm files are OK</Button>
          </div>
        </form>
      </Modal>
    </div>
  );
}
