namespace Joule;

/// <summary>
/// A representative sample household. Settings go through the same catalogue as live data (sections, plain names, kinds),
/// including Predbat's own controls; the plan uses Predbat's real state codes with split slots, freeze and hold states,
/// targets and reasons; history includes an external Predbat update, a manual override set and cleared, a kept trial and one
/// still running, one failed run and an open follow-up, so every part of the UI has something honest to show.
/// </summary>
public static class DemoData
{
    public const string Version = "v9.3.5 Bug fixes cloud inverters & Misc";
    /// <summary>The model name on the sample checks' usage records: no AI was called for them.</summary>
    public const string SampleModel = "Sample check (no AI used)";
    /// <summary>The sample household's Predbat apps.yaml, kept in the demo's own folder so "Apply for me" can be tried for real.
    /// It lists no export_today sensor: the seeded file edit adds one.</summary>
    public const string AppsYaml = """
        # Sample Predbat configuration for the Joule demo. Nothing here talks to a real inverter.
        pred_bat:
          module: predbat
          class: PredBat

          # Home Assistant connection
          prefix: predbat
          timezone: Europe/London
          ha_key: !secret ha_token

          # Inverter
          inverter_type: GE
          num_inverters: 1
          inverter_limit:
            - 3600
          battery_rate_max_scaling: 1.0

          # Daily energy counters
          load_today:
            - sensor.demo_inverter_load_today
          import_today:
            - sensor.demo_inverter_import_today
          pv_today:
            - sensor.demo_inverter_pv_today

          # Tariff
          metric_octopus_import: re:(sensor.(octopus_energy_|)electricity_[0-9a-z]+_[0-9a-z]+_current_rate)
          octopus_api_key: !secret octopus_api_key

        """;
    /// <summary>What earlier sample checks remembered about the house ("What Joule knows about your home").</summary>
    public static readonly string[] MemoryFacts =
    [
        "The house has a 13.5 kWh battery and about 4.5 kW of solar panels.",
        "The car charges overnight from 00:30, and tops up between 19:00 and 20:30 every other evening.",
    ];
    /// <summary>The sample household. With <paramref name="history"/> (the running demo) it also carries a month of history: a
    /// kept trial and one still running with their revisions, two remembered AI claims, today's sample checks counted against the
    /// allowance, and automatic checks on. Without it (a plain fixture for tests) there is one starting revision and no trials.</summary>
    public static AppState Create(bool history = false)
    {
        var s = new AppState();
        var now = DateTimeOffset.UtcNow;
        string[] manualSlots = ["off", "Mon 10:30", "Mon 11:00", "Sun 15:00", "Sun 15:30"];
        s.Settings = [
            // Tunable plan settings (values from a real install, rounded).
            Number("load_scaling", "1.08", .5, 1.5),
            Number("pv_scaling", history ? "0.95" : "1.00", .5, 1.5),
            Number("load_scaling10", "1.10", 0, 2),
            Number("pv_metric10_weight", "0.15", 0, 1),
            Number("battery_rate_max_scaling", "1.00", .5, 1),
            Number("battery_rate_max_scaling_discharge", "1.05", 0, 2),
            Number("battery_loss", "0.04", 0, 1),
            Number("metric_min_improvement", "0.00", 0, 100, .1),
            Number("metric_min_improvement_export", "0.10", -50, 50, .1),
            Number("metric_battery_cycle", "0.0", -50, 50, .1),
            Number("best_soc_min", "0.50", 0, 13.5),
            Number("best_soc_keep", "1.00", 0, 13.5),
            Number("set_reserve_min", "4", 0, 100, 1),
            Toggle("car_charging_from_battery", "off"),
            Toggle("calculate_inday_adjustment", "on"),
            Toggle("load_filter_modal", "on"),
            Toggle("combine_charge_slots", "off"),
            Toggle("set_export_freeze", "on"),
            Toggle("calculate_export_oncharge", "on"),
            Toggle("octopus_intelligent_charging", "on"),
            Select("car_charging_plan_time", "07:00:00", ["06:00:00", "06:30:00", "07:00:00", "07:30:00", "08:00:00"]),
            // Predbat's own controls: shown read-only, recorded as History events, never restored or trialled.
            Select("update", Version, ["main", Version, "v9.3.4 IOG started-dispatch fix", "v9.3.3 Cloud inverter fixes"]),
            Toggle("auto_update", "off"),
            Select("mode", "Control charge & discharge", ["Monitor", "Control SOC only", "Control charge", "Control charge & discharge"]),
            Toggle("set_read_only", "off"),
            Select("manual_charge", "off", manualSlots),
            Select("manual_freeze_export", "off", manualSlots),
            Number("holiday_days_left", "0", 0, 28, 1),
            Toggle("debug_enable", "off"),
            Toggle("set_status_notify", "on"),
        ];
        foreach (var setting in s.Settings) PredbatSettingsCatalogue.Apply(setting);
        var values = ChangeEngine.TunableValues(s.Settings);
        if (!history) s.Revisions.Add(new ConfigRevision { Id = 1, At = now.AddDays(-7), Source = "Initial snapshot", Reason = "Demo configuration imported", Values = values });
        else AddTrials(s, now, values);
        // Predbat updated itself two days ago, and a manual charge was set and cleared yesterday.
        s.SettingEvents.Add(new SettingEvent { At = now.AddDays(-2), Kind = SettingKind.Software, Key = "update", Name = "Predbat version", Before = "v9.3.4 IOG started-dispatch fix", After = Version, Title = "Predbat updated to v9.3.5" });
        s.SettingEvents.Add(new SettingEvent { At = now.AddDays(-1).AddHours(-3), Kind = SettingKind.Override, Key = "manual_charge", Name = "Manual charge slots", Before = "off", After = "+Sun 15:00", Title = "Manual charge slots set for Sun 15:00 (cleared after 20 min)", RevertedAt = now.AddDays(-1).AddHours(-3).AddMinutes(20) });
        s.SettingKindsVersion = 1;

        // The seeded checks are automatic ones, so none reads "Check you started".
        var scheduled = new AnalysisRequest(Scheduled: true);
        var finding = new Investigation { Title = "Evening load is consistently overestimated", Summary = "The sample household uses less energy after 21:00 than its forecast. Lowering the load multiplier slightly is worth a seven-day trial; Joule measures the cost before and after rather than promising a saving.", Category = "Forecast accuracy", Confidence = "Medium", Provider = "Demo", Verdict = "opportunity", At = now.AddMinutes(-24), Request = scheduled, Evidence = ["Illustrative 10-day comparison: 8 evenings above actual consumption.", "Sample excess evening forecast: 0.58 kWh/day.", "These figures are seeded demo evidence, not measurements from your home."], Steps = ["Compared frozen forecasts with sample actuals.", "Checked evening residuals and tariff periods.", "Prepared a reversible, seven-day trial."] };
        s.Investigations.Add(finding);
        s.Investigations.Add(new Investigation { Title = "Solar forecast tracks the midday peak", Summary = history ? "Since the solar forecast was trimmed to 95% four days ago, planned and measured solar agree closely around the peak. Nothing to change: the trial carries on until its review." : "Planned and measured solar agree closely around the peak. Keep collecting evidence across cloudy days before changing solar scaling.", Category = "Solar generation", Confidence = "Medium", Provider = "Demo", Verdict = "no_change", At = now.AddHours(-2), Request = scheduled, Evidence = ["Illustrative solar profile; no live measurements connected."], Steps = ["Reviewed sample generation and prediction curves."] });
        s.Investigations.Add(new Investigation
        {
            Title = "Predbat can't see today's export: apps.yaml has no export_today sensor", Category = "Configuration", Confidence = "Medium", Provider = "Demo", Verdict = "problem", At = now.AddHours(-3), Request = scheduled,
            Summary = "apps.yaml lists no export_today sensor, so Predbat can't learn from what the house exported today. Joule still reads the export meter in Home Assistant, so the figures here are unaffected. Add the sensor under pred_bat; the next check confirms Predbat sees export history.",
            Evidence = ["Sample apps.yaml lists import_today and load_today but no export_today.", "Seeded demo evidence, not your configuration."],
            Steps = ["Reviewed the sample apps.yaml sensor list."],
            FileChanges = [new ConfigFileChange
            {
                Id = Guid.NewGuid().ToString("N"), File = "apps.yaml", Location = "pred_bat, after import_today",
                Summary = "Add export_today so Predbat can compare planned and actual export",
                Snippet = "  export_today:\n    - sensor.demo_inverter_export_today",
                Reason = "Without export_today Predbat has no export history, so its export forecasts and plan-versus-actual export stay blank."
            }],
            NextSteps = [new InvestigationNextStep
            {
                Id = "demo-follow-up-export-sensor", Title = "Find your inverter's daily export sensor in Home Assistant",
                Rationale = "The file edit needs the entity that counts today's export; the sample name sensor.demo_inverter_export_today will differ on your install.",
                SuggestedAction = "In Home Assistant, open Settings › Entities, search for \"export\" and pick the daily total in kWh that resets at midnight.",
                Verification = "With that entity in apps.yaml, the next check finds export history in Predbat.",
                Uncertainty = "Seeded demo to-do; your inverter integration may name it differently."
            }]
        });
        s.Investigations.Add(new Investigation
        {
            Title = "Check didn't finish", Headline = "The AI service didn't answer", Plain = "The AI service stopped responding part-way through this automatic check. Nothing was changed; the next check runs as normal.", Summary = "The AI provider stopped responding part-way through this scheduled check, so no findings were recorded. The next scheduled check runs as normal.",
            Category = "Didn't finish", Confidence = "Unavailable", Provider = "Demo", Status = "Failed", Verdict = null, FailureKind = "provider_busy", At = now.AddDays(-1).AddHours(-2),
            Request = scheduled, Evidence = ["Seeded demo failure: no model reply was available to record."], Steps = ["Loaded the plan and recent measurements.", "Provider request timed out."]
        });
        s.Proposals.Add(new Proposal { Title = "Bring the evening load forecast closer to reality", Summary = "Reduce the load multiplier slightly and watch whether the battery retains less unused energy overnight.", ExpectedEffect = "Around 7% lower forecast load. Potentially more energy available for export before the cheap overnight period.", Tradeoff = "Unexpected evening demand could require extra grid import. Whole-house scaling also affects other periods.", Confidence = finding.Confidence, Source = "Demo", EstimatedMonthlySavingGbp = null, BaseRevision = s.Revision, InvestigationId = finding.Id, CreatedAt = finding.At.AddMinutes(1), Changes = [new Change("load_scaling", "1.08", "1.00")], Evidence = finding.Evidence });
        s.Proposals[^1].SavingEstimate = ProposalEstimates.SavingEstimate(s.Proposals[^1]);
        s.Activities.Add(new Activity(now.AddMinutes(-24), "analysis", "Demo investigation completed · 1 recommendation ready for review."));
        s.LastCollection = now;
        s.LastAnalysis = finding.At;
        if (!history) return s;
        // What earlier checks set out to test: one still open, one confirmed.
        s.Claims.Add(new AiClaim { Text = "Evening use after 21:00 runs about 0.5 kWh below the forecast", Test = "Measured against forecast home use, 21:00–23:30, over the next three evenings", CreatedAt = finding.At, InvestigationId = finding.Id });
        s.Claims.Add(new AiClaim { Text = "The overnight charge reaches its target before 05:00", Test = "Battery level at 05:00 against the 94% target", CreatedAt = now.AddDays(-6), Status = "confirmed", ResolvedAt = now.AddHours(-2), ResolvedBy = "check", Reason = "Reached 93% by 04:30 on each of the last seven nights." });
        // Automatic checks are on; today's sample checks count against the allowance like real ones, labelled as samples.
        s.Ai.Scheduled = true;
        foreach (var check in s.Investigations.Where(i => i.Status != "Failed"))
            s.Usage.Add(new UsageRecord(check.At, "Demo", SampleModel, 0, 0, null, "Completed"));
        s.LastAnalysisAttemptAt = finding.At;
        return s;
    }

    /// <summary>Two earlier suggestions were tried: a battery-loss change kept three weeks ago, and a solar trim four days into its
    /// week. Seeded trials are marked so the evaluator leaves their illustrative figures alone.</summary>
    static void AddTrials(AppState s, DateTimeOffset now, Dictionary<string, string> values)
    {
        Dictionary<string, string> With(params (string Key, string Value)[] changes) { var v = new Dictionary<string, string>(values); foreach (var (k, x) in changes) v[k] = x; return v; }
        const string keptTitle = "Match the battery loss to what the meters measure", runningTitle = "Trim the solar forecast on cloudy afternoons";
        s.Revisions.Add(new ConfigRevision { Id = 1, At = now.AddDays(-30), Source = "Initial snapshot", Reason = "Demo configuration imported", Values = With(("battery_loss", "0.05"), ("pv_scaling", "1.00")) });
        s.Revisions.Add(new ConfigRevision { Id = 2, At = now.AddDays(-21), Source = "Approved by you", Reason = keptTitle, Changes = [new Change("battery_loss", "0.05", "0.04")], Values = With(("pv_scaling", "1.00")) });
        s.Revisions.Add(new ConfigRevision { Id = 3, At = now.AddDays(-4), Source = "Approved by you", Reason = runningTitle, Changes = [new Change("pv_scaling", "1.00", "0.95")], Values = values });
        s.Experiments.Add(new Experiment
        {
            Title = keptTitle, Source = "Approved by you", RevisionId = 2, StartedAt = now.AddDays(-21), ReviewAt = now.AddDays(-14), Status = "Kept", Seeded = true,
            Hypothesis = "The meters show the battery losing about 4% on a round trip, not the 5% Predbat assumed, so it was holding back charge it didn't need.",
            Result = "Kept after the seven-day review. The evening battery level tracked the plan more closely and the cost per day stayed level (£1.58 against £1.63 before). A measured comparison, not a proven saving.",
            BaselineError = .19, CurrentError = .17, BaselineForecastCoverage = .998, CurrentForecastCoverage = .997, BaselineCostGbpPerDay = 1.63, CurrentCostGbpPerDay = 1.58, BaselineCostCoverage = .996, CurrentCostCoverage = .995,
            ForecastEvidenceVersion = Experiment.CurrentForecastEvidenceVersion, Decisions = [new(now.AddDays(-14), "keep", "Kept after the seven-day review.")]
        });
        s.Experiments.Add(new Experiment
        {
            Title = runningTitle, Source = "Approved by you", RevisionId = 3, StartedAt = now.AddDays(-4), ReviewAt = now.AddDays(3), Status = "Running", Seeded = true,
            Hypothesis = "Solar has come in about 5% under the forecast on cloudy afternoons, leaving the battery short before the evening export. Forecasting 5% less should keep the plan closer to what happens.",
            Result = "Four days in: the solar forecast is out by 0.18 kWh a half-hour, down from 0.24, and the cost per day is £1.39 against £1.47 before. Joule decides at the review; nothing is proven yet.",
            BaselineError = .24, CurrentError = .18, BaselineForecastCoverage = .995, CurrentForecastCoverage = .993, BaselineCostGbpPerDay = 1.47, CurrentCostGbpPerDay = 1.39, BaselineCostCoverage = .996, CurrentCostCoverage = .994,
            ForecastEvidenceVersion = Experiment.CurrentForecastEvidenceVersion
        });
    }
    static Setting Number(string key, string value, double min, double max, double step = .01) => new() { Key = key, Name = key, Value = value, Min = min, Max = max, Step = step, EntityId = $"input_number.predbat_{key}" };
    static Setting Toggle(string key, string value) => new() { Key = key, Name = key, Value = value, Type = "boolean", Step = 0, EntityId = $"switch.predbat_{key}", Editable = key != "active" };
    static Setting Select(string key, string value, IEnumerable<string> options) => new() { Key = key, Name = key, Value = value, Type = "select", Step = 0, Options = [.. options], EntityId = $"select.predbat_{key}" };

    /// <summary>
    /// The sample plan in Predbat's own codes. Like a live Predbat plan, today's runs 48 hours from the half-hour holding "now" (the
    /// past comes from earlier plans and the meters); an older one covers its day and the next, from local midnight. It has an
    /// overnight charge to a target, a charge-then-export split slot, freeze charging and
    /// hold-at-target in the morning, solar into the battery, freeze export over the solar peak, a demand-then-export split before the
    /// evening export, an export paused at the minimum level, a car hold on car evenings and the evening on the battery.
    /// Battery levels come from <see cref="DemoHouse"/>: elapsed slots carry what the demo meters measured, and from the current
    /// slot on the plan starts again from the measured level, as Predbat does.
    /// </summary>
    public static PlanSnapshot Plan(int daysAgo = 0) => Plan(daysAgo, DateTimeOffset.UtcNow);
    internal static PlanSnapshot Plan(int daysAgo, DateTimeOffset now)
    {
        var zone = DemoHouse.Zone;
        var date = DemoHouse.LocalDate(now, zone).AddDays(-daysAgo);
        var day = DemoHouse.Midnight(date, zone);
        var plan = new PlanSnapshot { At = daysAgo == 0 ? now : day.AddHours(7), Id = $"demo-{date:yyyyMMdd}", Source = "Demo" };
        // Rates are published up to 23:30 tonight until 16:00, then up to 23:30 tomorrow; later slots copy known prices.
        var capturedLocal = TimeZoneInfo.ConvertTime(plan.At, zone);
        var knownUntil = DemoHouse.Midnight(DemoHouse.LocalDate(plan.At, zone).AddDays(capturedLocal.Hour >= 16 ? 1 : 0), zone).AddHours(23.5);
        var from = daysAgo == 0 ? day.AddMinutes(Math.Floor((now - day).TotalMinutes / 30) * 30) : day;
        var end = daysAgo == 0 ? from.AddHours(48) : DemoHouse.Midnight(date.AddDays(2), zone);
        double level = 0, rate = 0;
        for (var d = date; DemoHouse.Midnight(d, zone) < end; d = d.AddDays(1))
        {
            var midnight = DemoHouse.Midnight(d, zone);
            var measured = DemoHouse.Measured(d);
            if (d == date) { level = measured.Levels[0]; rate = measured.ChargeRateBefore; }
            // Planned levels: from midnight with the forecast; from the slot holding "now", again from the measured level.
            var levels = new double[49]; levels[0] = level;
            var nextRate = DemoHouse.Run(d, 0, levels, rate, forecast: true);
            var current = now >= midnight && now < DemoHouse.Midnight(d.AddDays(1), zone) ? Math.Clamp((int)Math.Floor((now - midnight).TotalMinutes / 30), 0, 47) : -1;
            if (current >= 0)
            {
                levels[current] = measured.Levels[current];
                nextRate = DemoHouse.Run(d, current, levels, measured.ChargeRateBefore, forecast: true);
            }
            var dayRate = rate;
            level = levels[48]; rate = nextRate;
            // A clock-change day has 46 or 50 half-hours: the extra hour in October repeats the last slot.
            var count = (int)Math.Round((DemoHouse.Midnight(d.AddDays(1), zone) - midnight).TotalMinutes / 30);
            for (var n = 0; n < count; n++)
            {
                var i = Math.Min(n, 47);
                var time = midnight.AddMinutes(n * 30);
                var start = n < 48 ? levels[i] : levels[48]; var finish = n < 48 ? levels[i + 1] : levels[48];
                if (time < from) continue;
                if (time >= end) break;
                var local = TimeZoneInfo.ConvertTime(time, zone);
                var h = local.Hour + local.Minute / 60d;
                var importRate = DemoHouse.ImportRate(h); var exportRate = DemoHouse.ExportRate(h);
                var elapsed = time.AddMinutes(30) <= now;
                var loadForecast = DemoHouse.ForecastLoadKwh(i); var pvForecast = DemoHouse.ForecastPvKwh(d, i);
                var (_, _, import, export) = DemoHouse.Balance(loadForecast, DemoHouse.CarKwh(d, i), pvForecast, start, finish);
                var (code, label, target, reason, state2, split) = SlotState(d, i, start, finish, loadForecast, pvForecast, i < 9 ? dayRate : nextRate);
                var key = PredbatGlossary.Key(code);
                var actionKey = state2 is null ? key : PredbatGlossary.Combine(key, PredbatGlossary.Key(state2));
                var entry = PredbatGlossary.Find(label is "Hold battery for the car" ? "Hold for car" : code);
                var estimated = time >= knownUntil;
                plan.Slots.Add(new PlanSlot(time, loadForecast, elapsed ? measured.Load[i] : null, pvForecast, elapsed ? measured.Pv[i] : null, start, elapsed ? measured.Levels[Math.Min(n + 1, 48)] : null, importRate, exportRate, actionKey, Math.Round((import * importRate - export * exportRate) / 100, 3))
                {
                    RawAction = state2 is null ? code : state2,
                    ActionKey = actionKey,
                    ActionId = state2 is null ? entry?.Id ?? actionKey : actionKey,
                    ActionLabel = label ?? (state2 is null ? entry?.Label : null) ?? PredbatGlossary.Actions[actionKey].Label,
                    PrimaryAction = key,
                    SecondaryAction = state2 is null ? null : PredbatGlossary.Key(state2),
                    State2 = state2,
                    SplitTime = split,
                    TargetPercent = target,
                    ReasonText = reason,
                    ImportRateType = estimated ? "copy" : null,
                    RateEstimated = estimated ? true : null,
                    SocForecastEnd = finish,
                    SocChangeKwh = Math.Round((finish - start) / 100 * DemoHouse.CapacityKwh, 3),
                });
            }
        }
        return plan;
    }

    /// <summary>The half-hour slot (0–47) of its local day that contains <paramref name="at"/>.</summary>
    internal static int SlotAt(DateTimeOffset at) => DemoHouse.SlotAt(at);

    /// <summary>Measured battery level (%) at each half-hour boundary of a local day: 49 values from 00:00 to the next 00:00, exactly
    /// what the demo battery sensor reports.</summary>
    internal static double[] ActualSocLevels(DateOnly date) => DemoHouse.Measured(date).Levels;

    static string Kw(double percentPerSlot) => (Math.Abs(percentPerSlot) / 100 * DemoHouse.CapacityKwh * 2).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    static (string Code, string? Label, double? Target, string? Reason, string? State2, string? Split) SlotState(DateOnly date, int slot, double start, double end, double load, double pv, double chargeRate)
    {
        var change = end - start;
        return DemoHouse.StateOf(date, slot) switch
        {
            DemoHouse.State.Charge => ("Chrg", null, DemoHouse.ChargeTarget, $"Charging to 94% at {Kw(chargeRate)} kW (7.00p/kWh).", null, null),
            DemoHouse.State.ChargeExport => ("Chrg", "Charge, then export", DemoHouse.MorningTarget, "Charging to 94% (7.00p/kWh), then exporting down to 90% (15.00p/kWh).", "Exp", null),
            DemoHouse.State.HoldCharge => ("HoldChrg", null, DemoHouse.MorningTarget, "The battery is already at the 90% target, so it is held there.", null, null),
            DemoHouse.State.FreezeCharge => ("FrzChrg", null, null, "The battery is held at its level rather than charged from the grid (25.40p/kWh against a 9.00p/kWh threshold); spare solar can still top it up.", null, null),
            DemoHouse.State.FreezeExport => ("FrzExp", null, null, "Solar covers the house and the rest goes to the grid; the battery isn't charged or discharged to export this slot.", null, null),
            DemoHouse.State.DemandExport => ("Demand", "Power your home until 16:10, then export battery to the grid", DemoHouse.ExportTarget, $"Until 16:10 the battery should run the house. Exporting down to 60% at {Kw(change * 1.5)} kW (18.00p/kWh).", "Exp", "16:10"),
            DemoHouse.State.Export => ("Exp", null, DemoHouse.ExportTarget, $"Exporting down to 60% at {Kw(change)} kW (18.00p/kWh).", null, null),
            DemoHouse.State.HoldExport => ("HoldExp", null, DemoHouse.ExportTarget, "Export window, but the battery isn't expected to be above 60%, so it doesn't export.", null, null),
            DemoHouse.State.CarHold => ("Demand", "Hold battery for the car", null, "The battery is held so it won't discharge into the charging car.", null, null),
            _ when end >= 99.9 && pv > load => ("Demand", null, null, "The battery is full, so spare solar goes to the grid; no charging or exporting is planned.", null, null),
            _ when change > .05 => ("Demand", null, null, "The battery should rise from solar; no charging or exporting is planned.", null, null),
            _ => ("Demand", null, null, "The battery should discharge to run the house; no charging or exporting is planned.", null, null),
        };
    }
}
