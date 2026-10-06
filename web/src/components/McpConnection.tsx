import { useCallback, useEffect, useState } from "react";
import { Plug } from "lucide-react";
import type { Api } from "../completion-types";
import type { McpDiscoveryRecord } from "../types";
import { Button, Chip, Disclosure } from "./ui";
import { clock, dayTime } from "../lib/time";

interface McpStatus {
  configured: boolean;
  connected: boolean;
  checkedAt: string | null;
  tools: { name: string; description: string }[];
  error: string | null;
}

/**
 * Predbat's live tools (MCP), in one line: "Working · 9 tools · last used 10:26". The live client's own status is
 * combined with what the last check recorded, so a server that hasn't been checked by hand still reads as working when
 * checks are using it.
 */
export function McpConnection({
  api,
  demo,
  recorded,
  lastUsed,
}: {
  api: Api;
  demo: boolean;
  /** What the last check learned (state.ai.mcp). */
  recorded?: McpDiscoveryRecord | null;
  /** When a check last read Predbat's tools successfully. */
  lastUsed?: string | null;
}) {
  const [status, setStatus] = useState<McpStatus | null>(null);
  const [error, setError] = useState("");
  const [checking, setChecking] = useState(false);
  const load = useCallback(async () => {
    try {
      setStatus(await api<McpStatus>("/mcp/status"));
      setError("");
    } catch (e) {
      setError((e as Error).message);
    }
  }, [api]);
  useEffect(() => {
    void load();
  }, [load]);
  async function check() {
    setChecking(true);
    setError("");
    try {
      setStatus(await api<McpStatus>("/mcp/discover", {}));
    } catch (e) {
      // A failed check means Joule can't reach the tools right now, whatever an earlier check found.
      setStatus((previous) =>
        previous
          ? { ...previous, connected: false, tools: [], checkedAt: new Date().toISOString(), error: null }
          : previous,
      );
      setError((e as Error).message);
    } finally {
      setChecking(false);
    }
  }
  const configured = status?.configured ?? recorded?.configured ?? false;
  const liveChecked = !!status?.checkedAt;
  const connected = liveChecked ? !!status?.connected : (recorded?.connected ?? false);
  const toolNames = liveChecked && status?.tools.length ? status.tools.map((t) => t.name) : (recorded?.tools ?? []);
  const working = connected || (!liveChecked && !!lastUsed);
  const problem = (liveChecked ? status?.error : recorded?.error) || "";
  const line = !configured
    ? "Not set up"
    : working
      ? [
          "Working",
          toolNames.length ? `${toolNames.length} ${toolNames.length === 1 ? "tool" : "tools"}` : null,
          lastUsed ? `last used ${dayTime(lastUsed).replace(/^Today /, "")}` : null,
        ]
          .filter(Boolean)
          .join(" · ")
      : liveChecked || recorded
        ? "Not reachable"
        : "Not checked yet";
  return (
    <section className="ai-card mcp-card" aria-labelledby="mcp-heading">
      <div className="ai-card-head">
        <h3 id="mcp-heading">
          <Plug size={16} aria-hidden="true" />
          Predbat's live tools
        </h3>
        <Chip tone={!configured ? "neutral" : working ? "success" : "warn"} dot>
          {line}
        </Chip>
      </div>
      <p className="ai-card-copy">
        Lets each check read Predbat's log, plan and settings while it works. Read only: changes always go through you.
        {demo && " Demo checks use sample data and don't call these tools."}
      </p>
      {problem && !working && (
        <p className="callout" role="status">
          {problem}
        </p>
      )}
      {error && (
        <p className="callout" role="alert">
          {error}
        </p>
      )}
      {!configured && (
        <p className="ai-card-copy">
          To set it up, turn on MCP in Predbat, then set <code>Predbat__McpToken</code> in Joule's environment. Joule
          uses your Predbat host on port 8199 unless <code>Predbat__McpUrl</code> says otherwise.
        </p>
      )}
      {configured && connected && toolNames.length === 0 && (
        <p className="callout" role="status">
          Predbat answered, but offered no tools a check can read.
        </p>
      )}
      {toolNames.length > 0 && (
        <Disclosure summary="Tools a check can use" count={toolNames.length}>
          <ul className="tool-names">
            {(liveChecked && status?.tools.length
              ? status.tools
              : toolNames.map((name) => ({ name, description: "" }))
            ).map((tool) => (
              <li key={tool.name}>
                <code>{tool.name}</code>
                {tool.description && <span className="muted"> {tool.description}</span>}
              </li>
            ))}
          </ul>
        </Disclosure>
      )}
      <div className="card-action-row">
        {status?.checkedAt && <span className="card-action-meta">Checked {clock(status.checkedAt)}</span>}
        {!status && error ? (
          <Button size="sm" variant="secondary" onClick={() => void load()}>
            Try again
          </Button>
        ) : (
          <Button size="sm" variant="secondary" onClick={() => void check()} disabled={checking || !configured}>
            {checking ? "Checking…" : "Check now"}
          </Button>
        )}
      </div>
    </section>
  );
}
