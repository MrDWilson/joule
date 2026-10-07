import { FilePen } from "lucide-react";
import { Chip } from "../ui";
import { PlainText } from "../PlainText";
import { EnvSnippet } from "./EnvSnippet";
import { SwitchSetting } from "./SetupForms";
import { EDIT_ENV_LINES, EDIT_VOLUME_LINES, type ConfigEditStatus } from "../../lib/configEdits";
import type { SetupConfig } from "../../lib/setupConfig";

/** On, off, or what's missing, in a few words (the chip and the checklist summary). */
export function configEditSummary(status: ConfigEditStatus | null) {
  if (!status) return { label: "Checking…", tone: "neutral" as const };
  if (status.demo) return { label: "On in the demo", tone: "success" as const };
  if (!status.configured) return { label: "Not set up", tone: "neutral" as const };
  if (!status.writable) return { label: "Can't write apps.yaml", tone: "warn" as const };
  if (!status.allowed) return { label: "Off", tone: "neutral" as const };
  if (status.quarantined) return { label: "Paused", tone: "warn" as const };
  return { label: "On", tone: "success" as const };
}

/**
 * Setup's "Let Joule edit Predbat's config files": what it does, then whatever is still missing: the folder mount, write access, or
 * the switch itself. Without it the AI's apps.yaml suggestions stay copy-it-yourself.
 */
export function ConfigEditSetup({
  status,
  config,
  heading = true,
}: {
  status: ConfigEditStatus | null;
  config: SetupConfig | null;
  heading?: boolean;
}) {
  const summary = configEditSummary(status);
  const file = status?.file ?? "apps.yaml";
  return (
    <div className="config-edit-setup">
      {heading && (
        <div className="files-card-head">
          <span className="files-card-icon" aria-hidden="true">
            <FilePen size={18} />
          </span>
          <div>
            <h2 id="config-edits">Let Joule edit Predbat's config files</h2>
          </div>
          <Chip tone={summary.tone}>{summary.label}</Chip>
        </div>
      )}
      <p className="body-copy">
        When the AI suggests a change to {file}, Joule can make it for you instead of asking you to copy a snippet. It
        shows you the exact change first, keeps a copy of the file, checks the result, watches Predbat reload it, and
        puts the copy back if Predbat has a problem.
      </p>
      {status?.demo && <p className="muted">The demo edits its own sample apps.yaml, so you can try it safely.</p>}
      {status && !status.demo && !status.configured && (
        <>
          <ol className="files-steps">
            <li>
              Mount Predbat's config folder (the one holding apps.yaml) into Joule's container. In Joule's
              docker-compose service:
            </li>
          </ol>
          <EnvSnippet label="Compose volume for Predbat's config folder" lines={EDIT_VOLUME_LINES} />
          <ol className="files-steps" start={2}>
            <li>Add these lines to Joule's environment, then restart Joule:</li>
          </ol>
          <EnvSnippet label="Settings to let Joule edit apps.yaml" lines={EDIT_ENV_LINES} />
          <p className="muted">
            Leave out the last line to have Joule keep copies of apps.yaml without editing it; you can switch editing on
            here later.
          </p>
        </>
      )}
      {status && !status.demo && status.configured && !status.writable && (
        <>
          <p className="body-copy">
            Joule can read {file}
            {status.root ? ` in ${status.root}` : ""} but can't write it. Joule's container runs as user 1654. Give that
            user write access to the file on the host, for example:
          </p>
          <EnvSnippet label="Give Joule write access" lines={[`sudo setfacl -m u:1654:rw ./predbat/config/${file}`]} />
          <p className="muted">
            Joule keeps the file's owner and permissions when it edits it. Check the mount isn't read-only (no{" "}
            <code>:ro</code> at the end of the volume line).
          </p>
        </>
      )}
      {status && !status.demo && status.configured && status.writable && (
        <>
          {status.allowedSource === "environment" ? (
            <p className="muted">
              {status.allowed ? "Switched on" : "Switched off"} by{" "}
              <code className="entity-id">ConfigFiles__AllowEdits</code> in Joule's environment, which always wins.
            </p>
          ) : config?.canSave ? (
            <SwitchSetting
              config={config}
              settingKey="ConfigFiles:AllowEdits"
              on={status.allowed}
              onLabel="Let Joule edit apps.yaml"
              offLabel="Stop Joule editing apps.yaml"
            />
          ) : (
            !status.allowed && <EnvSnippet label="Let Joule edit apps.yaml" lines={["ConfigFiles__AllowEdits=true"]} />
          )}
        </>
      )}
      {status?.quarantined && status.reason && (
        <p className="muted">
          <PlainText text={status.reason} />
        </p>
      )}
    </div>
  );
}
