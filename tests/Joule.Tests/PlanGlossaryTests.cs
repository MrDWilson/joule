using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>The one Predbat state glossary: every code ever stored maps to a plain label with the documented meaning.</summary>
public class PlanGlossaryTests
{
    // Every distinct action string found in the 871 live plans (GET /api/plans/{id}, 5 Oct 2026), plus the demo's legacy value.
    [Theory]
    [InlineData("Demand", "demand", "Power your home")]
    [InlineData("Charge", "charge", "Charge from the grid")]
    [InlineData("Export", "export", "Export battery to the grid")]
    [InlineData("Chrg", "charge", "Charge from the grid")]
    [InlineData("Exp", "export", "Export battery to the grid")]
    [InlineData("FrzExp", "freeze-export", "Export solar, don't charge battery")]
    [InlineData("HoldChrg", "hold-charge", "Hold at charge target")]
    [InlineData("HoldExp", "hold-export", "Export paused at minimum level")]
    [InlineData("Self-use", "demand", "Power your home")]
    public void EveryLiveCodeHasAPlainLabel(string code, string key, string label)
    {
        Assert.Equal(key, PredbatGlossary.Key(code));
        Assert.Equal(label, PredbatGlossary.Label(code));
        Assert.DoesNotContain("Predbat state", PredbatGlossary.Label(code));
        Assert.NotEqual(code, PredbatGlossary.Label(code));
    }

    [Theory]
    [InlineData("FrzChrg", "freeze-charge")]
    [InlineData("FrzChg", "freeze-charge")]
    [InlineData("HoldChg", "hold-charge")]
    [InlineData("NoChrg", "no-charge")]
    [InlineData("NoChg", "no-charge")]
    [InlineData("Chg", "charge")]
    [InlineData("Chrg↗", "charge")]
    [InlineData("Chrg&nearr;", "charge")]
    [InlineData("Exp&searr;", "export")]
    [InlineData("FrzExp&rarr;", "freeze-export")]
    [InlineData("Chrg ⅎ", "charge")]
    [InlineData("Chrg 70%", "charge")]
    [InlineData("Charge↗ 70%", "charge")]
    [InlineData("Exp↘ 4%", "export")]
    [InlineData("Chrg/Exp", "charge-export")]
    [InlineData("Demand/Exp", "export")]
    [InlineData("Chrg/FrzExp", "charge")]
    [InlineData("Freeze charging", "freeze-charge")]
    [InlineData("Freeze exporting", "freeze-export")]
    [InlineData("Hold exporting", "hold-export")]
    [InlineData("No Charge", "no-charge")]
    [InlineData("Demand (Holiday)", "demand")]
    [InlineData("Demand [Freeze exporting]", "freeze-export")]
    [InlineData("Exporting [Alert]", "export")]
    [InlineData("Charging [Manual SoC]", "charge")]
    [InlineData("Hold for car", "demand")]
    [InlineData("Demand, Hold for car", "demand")]
    [InlineData("Hold for iBoost", "demand")]
    [InlineData("Charging, Hold for car", "charge")]
    [InlineData("Cross-charging", "charge-export")]
    [InlineData("Both", "charge-export")]
    [InlineData("freeze-export", "freeze-export")]
    [InlineData("charge-export", "charge-export")]
    public void DocumentedSpellingsAndStatusesNormalise(string code, string key) => Assert.Equal(key, PredbatGlossary.Key(code));

    [Fact]
    public void StatusesThatAreNotPlanActionsStillReadPlainly()
    {
        foreach (var status in new[] { "Read-Only", "Read-Only (Axle)", "Calibration", "Error" })
        {
            var entry = Assert.IsType<GlossaryEntry>(PredbatGlossary.Find(status));
            Assert.Null(entry.Key);
            Assert.Equal(PredbatGlossary.UnknownKey, PredbatGlossary.Key(status));
            Assert.DoesNotContain("Other Predbat state", PredbatGlossary.Label(status));
        }
        Assert.Equal("Hold battery for the car", PredbatGlossary.Label("Demand, Hold for car"));
        Assert.Equal("Hold battery for iBoost", PredbatGlossary.Label("Hold for iBoost"));
    }

    [Fact]
    public void UnknownCodesNeverEchoTheCodeAsTheLabel()
    {
        Assert.Equal(PredbatGlossary.UnknownKey, PredbatGlossary.Key("Mystery"));
        Assert.Equal("Other Predbat state", PredbatGlossary.Label("Mystery"));
        Assert.Contains("Mystery", PredbatGlossary.Description("Mystery"));
    }

    [Fact]
    public void FreezeExportMeansNoChargingButDischargeForTheHouseIsExpected()
    {
        var action = PredbatGlossary.Actions["freeze-export"];
        Assert.Contains("battery won't charge; it may still discharge for the house; spare solar is exported", action.Description, StringComparison.OrdinalIgnoreCase);
        Assert.False(action.MayCharge);
        Assert.True(action.MayDischarge);
        Assert.DoesNotContain("hold", action.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expected", action.Battery);
        // Freeze charge is the opposite: no discharge.
        Assert.False(PredbatGlossary.Actions["freeze-charge"].MayDischarge);
        Assert.False(PredbatGlossary.Actions["charge"].MayDischarge);
        Assert.False(PredbatGlossary.Actions["export"].MayCharge);
    }

    [Fact]
    public void TargetsAndCarHoldsParseFromPlanText()
    {
        var target = PredbatGlossary.Parse("Chrg&nearr; 70%");
        Assert.Equal("charge", target.Entry!.Key); Assert.Equal(70, target.TargetPercent);
        var car = PredbatGlossary.Parse("&#128663;");
        Assert.True(car.HoldForCar); Assert.Equal("hold-for-car", car.Entry!.Id);
        var forced = PredbatGlossary.Parse("Exp&searr; &#8526;");
        Assert.True(forced.Forced); Assert.Equal("export", forced.Entry!.Key);
        var demandPart = PredbatGlossary.Parse(" &searr;");
        Assert.Null(demandPart.Entry); Assert.Equal("", demandPart.Code);
    }

    [Theory]
    [InlineData("demand", "export", "export")]
    [InlineData("charge", "export", "charge-export")]
    [InlineData("freeze-charge", "export", "export")]
    [InlineData("charge", "freeze-export", "charge")]
    [InlineData("hold-charge", "hold-export", "hold-charge")]
    [InlineData(null, "freeze-export", "freeze-export")]
    public void SplitSlotsCombineByWhatTheBatteryDoes(string? first, string second, string expected) => Assert.Equal(expected, PredbatGlossary.Combine(first, second));

    [Fact]
    public void ProseCodesAreReplacedWithLabels()
    {
        var text = PredbatGlossary.ReplaceCodes("During the next FrzExp slot the battery fell. FrzExp is fine; Chrg/Exp too.");
        Assert.Equal("During the next export solar, don't charge battery slot the battery fell. Export solar, don't charge battery is fine; charge, then export too.", text);
        Assert.Equal("No codes here.", PredbatGlossary.ReplaceCodes("No codes here."));
        Assert.Equal("Expected", PredbatGlossary.ReplaceCodes("Expected"));
    }

    [Fact]
    public void ReasonsRenderWithThePlansTemplatesThenJoulesWording()
    {
        var reasons = new List<PlanReason> { new("charge_low_rate", new() { ["target_percent"] = "100", ["rate"] = "6.67", ["rate_kw"] = "5.00" }), new("unknown_code", []) };
        Assert.Equal("Charging to 100% at 5.00 kW (6.67p/kWh).", PredbatGlossary.RenderReasons(reasons));
        var own = new Dictionary<string, string> { ["charge_low_rate"] = "Up to {target_percent}% now." };
        Assert.Equal("Up to 100% now.", PredbatGlossary.RenderReasons(reasons, own));
        Assert.Null(PredbatGlossary.RenderReasons([new("charge_low_rate", [])]));
    }

    [Fact]
    public void RateTypesSayWhichPricesAreEstimates()
    {
        Assert.True(PredbatGlossary.RateEstimated("copy"));
        Assert.True(PredbatGlossary.RateEstimated("future"));
        Assert.False(PredbatGlossary.RateEstimated("saving"));
        Assert.False(PredbatGlossary.RateEstimated(null));
        Assert.Contains("previous day", PredbatGlossary.RateTypeLabel("copy"));
    }

    [Fact]
    public void LegendNamesEveryActionInPlainEnglish()
    {
        var legend = PredbatGlossary.Legend();
        foreach (var action in PredbatGlossary.Actions.Values) Assert.Contains(action.Label, legend);
        Assert.DoesNotContain("hold while exporting", legend, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WebMirrorIsByteIdentical()
    {
        var root = RepositoryRoot();
        var canonical = File.ReadAllText(Path.Combine(root, "src", "Joule.Api", "Knowledge", "predbat-states.json"));
        var mirror = File.ReadAllText(Path.Combine(root, "web", "src", "lib", "predbat-states.json"));
        Assert.True(canonical == mirror, "web/src/lib/predbat-states.json differs from Knowledge/predbat-states.json. Run scripts/sync-glossary.sh.");
        Assert.Equal(canonical, PredbatGlossary.SourceJson);
    }

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Joule.Api", "Joule.Api.csproj"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
