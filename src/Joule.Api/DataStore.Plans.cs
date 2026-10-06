using System.Text.Json;
using System.Text.Json.Nodes;

namespace Joule;

/// <summary>Plan-slot detail columns, glossary normalisation of stored actions, and the backfill of Predbat's full plan rows
/// (split states, targets, reasons…) from retained source snapshots.</summary>
public partial class DataStore
{
    /// <summary>Bump when ParsePlan learns new fields, so stored plans are re-derived from their retained source snapshots.</summary>
    internal const int PlanDetailVersion = 1;
    /// <summary>Stored plan payloads omit null fields: most slots have no split, override or rate type.</summary>
    internal static readonly JsonSerializerOptions PlanPayloadOptions = new(JsonDefaults.Options) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    static readonly string[] SlotColumns = ["snapshot_id", "captured_at", "time", "load_forecast", "load_actual", "pv_forecast", "pv_actual", "soc_forecast", "soc_actual", "import_rate", "export_rate", "action", "cost", "duration_minutes",
        "raw_action", "action_key", "primary_action", "secondary_action", "split_time", "target_percent", "soc_forecast_end", "reason_text", "reasons_json", "override", "import_rate_type", "export_rate_type", "car_kwh", "iboost_kwh", "pv10", "load10", "total_cost"];

    void InitializePlanDetails()
    {
        Execute("""
            ALTER TABLE plan_slots ADD COLUMN IF NOT EXISTS raw_action VARCHAR;
            ALTER TABLE plan_slots ADD COLUMN IF NOT EXISTS action_key VARCHAR;
            ALTER TABLE plan_slots ADD COLUMN IF NOT EXISTS primary_action VARCHAR;
            ALTER TABLE plan_slots ADD COLUMN IF NOT EXISTS secondary_action VARCHAR;
            ALTER TABLE plan_slots ADD COLUMN IF NOT EXISTS split_time VARCHAR;
            ALTER TABLE plan_slots ADD COLUMN IF NOT EXISTS target_percent DOUBLE;
            ALTER TABLE plan_slots ADD COLUMN IF NOT EXISTS soc_forecast_end DOUBLE;
            ALTER TABLE plan_slots ADD COLUMN IF NOT EXISTS reason_text VARCHAR;
            ALTER TABLE plan_slots ADD COLUMN IF NOT EXISTS reasons_json VARCHAR;
            ALTER TABLE plan_slots ADD COLUMN IF NOT EXISTS override VARCHAR;
            ALTER TABLE plan_slots ADD COLUMN IF NOT EXISTS import_rate_type VARCHAR;
            ALTER TABLE plan_slots ADD COLUMN IF NOT EXISTS export_rate_type VARCHAR;
            ALTER TABLE plan_slots ADD COLUMN IF NOT EXISTS car_kwh DOUBLE;
            ALTER TABLE plan_slots ADD COLUMN IF NOT EXISTS iboost_kwh DOUBLE;
            ALTER TABLE plan_slots ADD COLUMN IF NOT EXISTS pv10 DOUBLE;
            ALTER TABLE plan_slots ADD COLUMN IF NOT EXISTS load10 DOUBLE;
            ALTER TABLE plan_slots ADD COLUMN IF NOT EXISTS total_cost DOUBLE;
            ALTER TABLE plans ADD COLUMN IF NOT EXISTS detail_version INTEGER DEFAULT 0;
            """);
        // Stored actions mixed Predbat codes and two hand-mapped names ("Chrg", "Charge", "FrzExp"…). Keep the stored text as
        // raw_action and store the one glossary key in action/action_key, so counts and comparisons see one spelling.
        Migrate("plan-actions-v1", () =>
        {
            var stored = new List<string>();
            using (var c = Command("SELECT DISTINCT action FROM plan_slots WHERE action IS NOT NULL AND action_key IS NULL"))
            using (var r = c.ExecuteReader()) while (r.Read()) stored.Add(r.GetString(0));
            foreach (var action in stored)
            {
                var key = PredbatGlossary.Key(action);
                Execute("UPDATE plan_slots SET raw_action=coalesce(raw_action, action), action_key=?, primary_action=?, action=? WHERE action=? AND action_key IS NULL",
                    key, key == PredbatGlossary.UnknownKey ? null : key, key == PredbatGlossary.UnknownKey ? action : key, action);
            }
        });
    }

    /// <summary>Fills glossary fields on plans stored before they existed: Action becomes the canonical key, RawAction keeps the
    /// stored text. Mutates and returns the same snapshot.</summary>
    internal static PlanSnapshot NormalisePlan(PlanSnapshot plan)
    {
        for (var i = 0; i < plan.Slots.Count; i++)
        {
            var slot = plan.Slots[i];
            if (slot.ActionKey is not null && slot.ActionLabel is not null) continue;
            var entry = PredbatGlossary.Find(slot.ActionKey ?? slot.Action);
            var key = entry?.Key ?? PredbatGlossary.UnknownKey;
            plan.Slots[i] = slot with
            {
                RawAction = slot.RawAction ?? slot.Action,
                ActionKey = key,
                ActionId = slot.ActionId ?? entry?.Id ?? key,
                ActionLabel = slot.ActionLabel ?? entry?.Label ?? PredbatGlossary.UnknownLabel,
                PrimaryAction = slot.PrimaryAction ?? (key == PredbatGlossary.UnknownKey ? null : key),
                Action = key == PredbatGlossary.UnknownKey ? slot.Action : key,
            };
        }
        return plan;
    }

    void InsertPlanSlots(PlanSnapshot p)
    {
        foreach (var chunk in p.Slots.Chunk(50))
        {
            var row = "(" + string.Join(",", SlotColumns.Select(_ => "?")) + ")";
            var values = chunk.SelectMany(x => new object?[]
            {
                p.Id, p.CollectedAt, x.Time, x.LoadForecast, x.LoadActual, x.PvForecast, x.PvActual, x.SocForecast, x.SocActual, x.ImportRate, x.ExportRate, x.Action, x.Cost, x.DurationMinutes,
                x.RawAction, x.ActionKey, x.PrimaryAction, x.SecondaryAction, x.SplitTime, x.TargetPercent, x.SocForecastEnd, x.ReasonText,
                x.Reasons is null ? null : JsonSerializer.Serialize(x.Reasons, JsonDefaults.Options), x.Override, x.ImportRateType, x.ExportRateType, x.CarKwh, x.IBoostKwh, x.Pv10, x.Load10, x.TotalCost,
            }).ToArray();
            Execute($"INSERT INTO plan_slots ({string.Join(",", SlotColumns)}) VALUES {string.Join(",", chunk.Select(_ => row))}", values);
        }
    }

    /// <summary>Copies Predbat's detail fields from a re-parsed plan onto a stored one, slot by slot (matched on time). Stored
    /// forecasts, rates and actuals are kept exactly as captured.</summary>
    internal static PlanSnapshot MergePlanDetails(PlanSnapshot stored, PlanSnapshot parsed)
    {
        var byTime = parsed.Slots.GroupBy(x => x.Time).ToDictionary(x => x.Key, x => x.First());
        for (var i = 0; i < stored.Slots.Count; i++)
        {
            var s = stored.Slots[i];
            if (!byTime.TryGetValue(s.Time, out var p)) continue;
            stored.Slots[i] = s with
            {
                Action = p.Action, RawAction = p.RawAction, ActionKey = p.ActionKey, ActionId = p.ActionId, ActionLabel = p.ActionLabel, PrimaryAction = p.PrimaryAction, SecondaryAction = p.SecondaryAction,
                State2 = p.State2, SplitTime = p.SplitTime, TargetPercent = p.TargetPercent, SocForecastEnd = p.SocForecastEnd, SocChangeKwh = p.SocChangeKwh, Reasons = p.Reasons,
                ReasonText = p.ReasonText, Override = p.Override, MixedStates = p.MixedStates, ImportRateType = p.ImportRateType, ExportRateType = p.ExportRateType, RateEstimated = p.RateEstimated,
                ImportRateAdjusted = p.ImportRateAdjusted, ExportRateAdjusted = p.ExportRateAdjusted, CarKwh = p.CarKwh, IBoostKwh = p.IBoostKwh, Pv10 = p.Pv10, Load10 = p.Load10,
                ClippedKwh = p.ClippedKwh, TotalCost = p.TotalCost,
            };
        }
        return stored;
    }

    /// <summary>Re-derives up to <paramref name="maxPlans"/> stored Predbat plans (newest first) from their retained raw plan, so
    /// history gains split states, targets and reasons. Idempotent; returns the number of plans examined.</summary>
    public int BackfillPlanDetails(int maxPlans = 40)
    {
        lock (gate)
        {
            var pending = new List<(string Id, DateTimeOffset? CollectedAt)>();
            using (var c = Command("SELECT p.id, p.collected_at FROM plans p JOIN source_snapshots s ON s.id=p.id WHERE coalesce(p.detail_version,0)<? AND p.source<>'Demo' ORDER BY p.recorded_at DESC LIMIT ?", PlanDetailVersion, maxPlans))
            using (var r = c.ExecuteReader()) while (r.Read()) pending.Add((r.GetString(0), r.IsDBNull(1) ? null : Stamp(r.GetValue(1))));
            if (pending.Count == 0) return 0;
            Execute("BEGIN TRANSACTION");
            try
            {
                foreach (var (id, collectedAt) in pending)
                {
                    string? payload, rawPlan;
                    using (var c = Command("SELECT p.payload, s.plan_json, s.plan_gz FROM plans p JOIN source_snapshots s ON s.id=p.id WHERE p.id=?", id))
                    using (var r = c.ExecuteReader()) { if (!r.Read()) continue; payload = r.GetString(0); rawPlan = r.IsDBNull(1) && r.IsDBNull(2) ? null : SnapshotText(r, 1, 2); }
                    PlanSnapshot? parsed = null;
                    try { if (rawPlan is not null && JsonNode.Parse(rawPlan) is JsonObject data) parsed = PredbatClient.ParsePlan(data); }
                    catch (Exception e) when (e is JsonException or DomainException or InvalidOperationException or FormatException) { parsed = null; }
                    if (parsed is not null)
                    {
                        var stored = NormalisePlan(JsonSerializer.Deserialize<PlanSnapshot>(payload, JsonDefaults.Options)!);
                        stored.CollectedAt = collectedAt;
                        MergePlanDetails(stored, parsed);
                        Execute("UPDATE plans SET payload=? WHERE id=?", JsonSerializer.Serialize(stored, PlanPayloadOptions), id);
                        Execute("DELETE FROM plan_slots WHERE snapshot_id=?", id);
                        InsertPlanSlots(stored);
                    }
                    Execute("UPDATE plans SET detail_version=? WHERE id=?", PlanDetailVersion, id);
                }
                Execute("COMMIT");
            }
            catch { Execute("ROLLBACK"); throw; }
            return pending.Count;
        }
    }

    /// <summary>Demo reset: drops demo plans and the revision ledger so the sample household starts again. Never used live.</summary>
    internal void ResetDemoData()
    {
        lock (gate)
        {
            Execute("BEGIN TRANSACTION");
            try
            {
                using (var c = Command("SELECT count(*) FROM plans WHERE source<>'Demo'")) if (Convert.ToInt64(c.ExecuteScalar()) > 0) throw new DomainException("This store holds live plans; the demo reset is refused.", 409);
                Execute("DELETE FROM plan_slots WHERE snapshot_id IN (SELECT id FROM plans WHERE source='Demo')");
                Execute("DELETE FROM plans WHERE source='Demo'");
                Execute("DELETE FROM revisions");
                Execute("COMMIT");
            }
            catch { Execute("ROLLBACK"); throw; }
        }
    }

    /// <summary>True once every stored plan with a retained raw plan has been re-derived at the current detail version. Snapshot
    /// retention waits for this, so pruning can never remove the data a backfill still needs.</summary>
    public bool PlanDetailBackfillComplete()
    {
        lock (gate)
        {
            using var c = Command("SELECT count(*) FROM plans p JOIN source_snapshots s ON s.id=p.id WHERE coalesce(p.detail_version,0)<? AND p.source<>'Demo'", PlanDetailVersion);
            return Convert.ToInt64(c.ExecuteScalar()) == 0;
        }
    }
}
