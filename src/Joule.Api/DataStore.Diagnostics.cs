using System.Text.Json;
using System.Text.Json.Nodes;

namespace Joule;

public partial class DataStore
{
    // A small manifest prevents the analyst from guessing entity IDs, sample
    // status literals, or how many days of data this installation has retained.
    public object ReadEvidenceCoverage(CancellationToken ct = default) => new
    {
        snapshots = Query("SELECT min(recorded_at) AS first_at, max(recorded_at) AS last_at, count(*) AS samples FROM observations", ct),
        plans = Query("SELECT min(recorded_at) AS first_generated_at, max(recorded_at) AS last_generated_at, min(collected_at) AS first_collected_at, count(*) AS snapshots FROM plans", ct),
        metrics = Query("SELECT metric, status, min(time) AS first_at, max(time) AS last_at, count(*) AS samples FROM telemetry_samples GROUP BY metric, status ORDER BY metric, status LIMIT 64", ct),
        entities = Query("SELECT entity_id, min(recorded_at) AS first_at, max(recorded_at) AS last_at FROM observations WHERE recorded_at >= now() - INTERVAL '2 days' GROUP BY entity_id ORDER BY CASE WHEN entity_id LIKE '%status%' OR entity_id LIKE '%charge%' OR entity_id LIKE '%battery%' THEN 0 ELSE 1 END, entity_id LIMIT 80", ct),
        note = "Inventory is bounded to 64 metric/status groups and 80 entity IDs (entities seen in the last two days). These spans describe retained evidence, not uninterrupted coverage. Use SQL to narrow other periods or discover additional entities."
    };

    // Raw source tables remain forbidden to arbitrary model SQL. This typed,
    // read-only selector exposes one already-retained Predbat snapshot at a time,
    // with credential redaction repeated at the boundary.
    public object ReadDiagnosticSnapshots(DateTimeOffset from, DateTimeOffset to,
        string? snapshotId = null, IReadOnlyList<string>? entityIds = null,
        int offset = 0, int limit = 12, IConfiguration? configuration = null,
        CancellationToken ct = default)
    {
        if (from >= to || to - from > TimeSpan.FromDays(31) || offset < 0 || offset > 100000 || limit is < 1 or > 50
            || snapshotId?.Length > 100 || entityIds?.Count > 10
            || entityIds?.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 200) == true)
            throw new DomainException("Snapshot reads require an ordered window of at most 31 days, offset 0–100000, limit 1–50 and at most ten exact entity IDs.", 400);
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            if (snapshotId is null)
            {
                using var command = Command("SELECT id,recorded_at FROM source_snapshots WHERE recorded_at >= ? AND recorded_at < ? ORDER BY recorded_at,id LIMIT ? OFFSET ?", from, to, limit + 1, offset);
                using var cancel = deadline.Token.Register(command.Cancel);
                using var rows = command.ExecuteReader();
                var items = new List<object>();
                while (rows.Read()) items.Add(new { id = rows.GetString(0), capturedAt = Stamp(rows.GetValue(1)) });
                var more = items.Count > limit;
                return new { items = items.Take(limit).ToArray(), offset, limit, nextOffset = more ? (int?)(offset + limit) : null, truncated = more, diagnosticOnly = true,
                    note = "CapturedAt is receipt time, not generation or execution time. Read an ID within this same window to inspect its frozen plan and selected Predbat state attributes." };
            }

            using var cmd = Command("SELECT recorded_at,state_json,plan_json,state_gz,plan_gz FROM source_snapshots WHERE id=? AND recorded_at >= ? AND recorded_at < ?", snapshotId, from, to);
            using var interrupt = deadline.Token.Register(cmd.Cancel);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) throw new DomainException("No retained source snapshot with this ID exists within the requested window.", 404);
            var capturedAt = Stamp(reader.GetValue(0));
            var rawState = SnapshotText(reader, 1, 3); var rawPlan = SnapshotText(reader, 2, 4);
            // Collection already bounds source payloads. Reject pathological old
            // records before JSON allocation; never silently substitute a prefix.
            if (rawState.Length > 4_000_000 || rawPlan.Length > 2_000_000)
                throw new DomainException("This retained snapshot exceeds the diagnostic read limit. Select another snapshot or use narrower plan/observation SQL.", 413);
            var secrets = PredbatMcpSafety.Secrets(configuration);
            using var state = JsonDocument.Parse(rawState);
            using var plan = JsonDocument.Parse(rawPlan);
            var selected = new JsonObject();
            var missing = new List<string>();
            var available = new List<string>();
            var inventoryTruncated = false;
            if (state.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (entityIds is null || entityIds.Count == 0)
                {
                    available = state.RootElement.EnumerateObject().Where(x => !PredbatMcpSafety.SensitiveKey(x.Name)).Select(x => PredbatMcpSafety.CleanText(x.Name, secrets)).Take(201).ToList();
                    inventoryTruncated = available.Count > 200;
                    if (inventoryTruncated) available.RemoveAt(200);
                }
                else foreach (var entity in entityIds.Distinct(StringComparer.Ordinal))
                {
                    var cleanId = PredbatMcpSafety.CleanText(entity, secrets);
                    if (!PredbatMcpSafety.SensitiveKey(entity) && state.RootElement.TryGetProperty(entity, out var value))
                        selected[cleanId] = PredbatMcpSafety.Clean(value, secrets);
                    else missing.Add(cleanId);
                }
            }
            var cleanPlan = PredbatMcpSafety.Clean(plan.RootElement, secrets);
            var content = (cleanPlan?.ToJsonString() ?? "") + selected.ToJsonString();
            var contentTruncated = content.Contains("[omitted: nesting limit]", StringComparison.Ordinal) || content.Contains("[redacted: complex content]", StringComparison.Ordinal);
            var result = new { id = snapshotId, capturedAt, plan = cleanPlan, entities = selected, availableEntityIds = available, missingEntityIds = missing,
                diagnosticOnly = true, truncated = contentTruncated || inventoryTruncated, contentTruncated, inventoryTruncated,
                note = "Frozen source diagnostics, not verified meter actuals. Preserve the plan's own generation time separately from capturedAt. Status, reasons, targets and commands describe intended or reported operation; corroborate actual inverter behavior with logs and measured states. Entity inventory is limited to 200 IDs; request exact known IDs to read attributes." };
            if (JsonSerializer.Serialize(result, JsonDefaults.Options).Length > 512000)
                throw new DomainException("Selected snapshot is larger than the diagnostic result limit. Request fewer entities, another snapshot, or narrow plan/observation SQL; no content was silently discarded.", 413);
            return result;
        }
    }
}
