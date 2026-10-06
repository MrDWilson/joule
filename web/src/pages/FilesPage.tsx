import { useApp } from "../context/AppContext";
import { FilesPage as Files } from "../components/FilesPage";
import { useSetupStatus } from "../lib/setupApi";
import "./setup.css";

/** Setup › Files: saved copies of Predbat's configuration files, and Predbat's own apps.yaml through MCP. */
export default function FilesPage() {
  const { api, mutate, busy, data, go } = useApp();
  const s = data.state;
  const { status } = useSetupStatus([]);
  return (
    <Files
      api={api}
      mutate={mutate}
      busy={busy}
      revision={s.revision}
      revisions={s.revisions}
      demo={data.connection.demo}
      pendingReload={s.pendingFileReload}
      mode={s.mode}
      writeUncertain={s.writeUncertain}
      onHistory={() => go("History")}
      mcpConfigured={status?.mcp.configured ?? false}
    />
  );
}
