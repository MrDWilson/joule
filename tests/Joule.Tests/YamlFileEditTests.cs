using Joule;
using Xunit;
namespace Joule.Tests;

public class YamlFileEditTests
{
    const string Apps = """
        # Predbat configuration
        pred_bat:
          module: predbat
          class: PredBat

          # Where Predbat finds Home Assistant
          prefix: predbat
          ha_url: 'http://homeassistant.local:8123'
          ha_key: !secret ha_token
          currency_symbols:
            - '£'
            - 'p'
          import_today:
            - sensor.givtcp_{geserial}_import_energy_today_kwh
          load_today:
            - sensor.givtcp_{geserial}_load_energy_today_kwh
        # an old note at column 0, still inside pred_bat
          inverter_limit:
            - 3600
          battery_rate_max_scaling: 1.0  # measured in October
          octopus_api_key: !secret octopus_api_key
          inverter:
            - name: main
              charge_rate: 3000

        # Something else entirely
        other_app:
          module: other

        """;

    static string Lf(string s) => s.Replace("\r\n", "\n");
    static YamlEditPlan Plan(string location, string snippet, string? before = null, string file = Apps) => YamlFileEdit.Plan(Lf(file), location, snippet, before);
    static string Refused(string location, string snippet, string? before = null, string file = Apps) =>
        Assert.Throws<YamlEditRefused>(() => Plan(location, snippet, before, file)).Message;

    /// <summary>The result equals the original with exactly these lines inserted at a 0-based index.</summary>
    static void Inserted(string result, int at, params string[] lines)
    {
        var expected = Lf(Apps).Split('\n').ToList(); expected.InsertRange(at, lines);
        Assert.Equal(string.Join('\n', expected), result);
    }
    static int LineOf(string text) => Lf(Apps).Split('\n').ToList().FindIndex(l => l == text);

    [Fact]
    public void AddsAfterTheNamedKeyAndLeavesEveryOtherLineAlone()
    {
        var plan = Plan("pred_bat, after import_today", "  export_today:\n    - sensor.givtcp_{geserial}_export_energy_today_kwh");
        Inserted(plan.Text, LineOf("  load_today:"), "  export_today:", "    - sensor.givtcp_{geserial}_export_energy_today_kwh");
        var key = Assert.Single(plan.Keys);
        Assert.Equal(("pred_bat.export_today", "added"), (key.Key, key.Change));
        Assert.Equal("Adds 2 lines under pred_bat, after import_today", plan.Placement);
    }

    [Fact]
    public void ReindentsASnippetWrittenWithoutItsIndent()
    {
        var plan = Plan("under pred_bat below import_today", "export_today:\n  - sensor.export");
        Inserted(plan.Text, LineOf("  load_today:"), "  export_today:", "    - sensor.export");
    }

    [Fact]
    public void AddsAtTheEndOfTheSectionBeforeTheNextOne()
    {
        var plan = Plan("pred_bat", "  pv_today:\n    - sensor.pv_today");
        Inserted(plan.Text, LineOf("      charge_rate: 3000") + 1, "  pv_today:", "    - sensor.pv_today");
    }

    [Fact]
    public void AnchorOnlyFindsTheSectionThatHoldsIt()
    {
        var plan = Plan("after load_today", "  export_today:\n    - sensor.export");
        // load_today's block ends at its list item; the column-0 note after it stays where it was.
        Inserted(plan.Text, LineOf("  load_today:") + 2, "  export_today:", "    - sensor.export");
    }

    [Fact]
    public void InsertsBeforeAKeyAboveItsComment()
    {
        var plan = Plan("pred_bat, before prefix", "  timezone: Europe/London");
        Inserted(plan.Text, LineOf("  # Where Predbat finds Home Assistant"), "  timezone: Europe/London");
    }

    [Fact]
    public void LooseSectionNameMatchesWhenUnambiguous()
    {
        var plan = Plan("the Predbat section", "  timezone: Europe/London");
        Assert.Contains("\n  timezone: Europe/London\n", plan.Text);
    }

    [Fact]
    public void ReplacesExactlyTheQuotedLinesKeepingTheRest()
    {
        var plan = Plan("pred_bat", "  battery_rate_max_scaling: 0.67  # measured in October", "  battery_rate_max_scaling: 1.0  # measured in October");
        Assert.Equal(Lf(Apps).Replace("battery_rate_max_scaling: 1.0", "battery_rate_max_scaling: 0.67"), plan.Text);
        Assert.Equal(("pred_bat.battery_rate_max_scaling", "changed"), (plan.Keys.Single().Key, plan.Keys.Single().Change));
        Assert.Equal($"Replaces line {LineOf("  battery_rate_max_scaling: 1.0  # measured in October") + 1}", plan.Placement);
    }

    [Fact]
    public void BeforeQuotedWithoutIndentationStillMatchesOncePlaced()
    {
        var plan = Plan("pred_bat", "inverter_limit:\n  - 3000", "inverter_limit:\n  - 3600");
        Assert.Equal(Lf(Apps).Replace("    - 3600", "    - 3000"), plan.Text);
    }

    [Fact]
    public void RefusesWhenTheQuotedLinesAreMissing()
    {
        var message = Refused("pred_bat", "  battery_rate_max_scaling: 0.5", "  battery_rate_max_scaling: 0.9");
        Assert.Contains("aren't in apps.yaml exactly as the suggestion quotes them", message);
    }

    [Fact]
    public void RefusesWhenTheQuotedLinesAppearTwice()
    {
        var file = "pred_bat:\n  a:\n    - sensor.x\n  b:\n    - sensor.x\n";
        Assert.Contains("appear 2 times", Refused("pred_bat", "    - sensor.y", "    - sensor.x", file));
    }

    [Fact]
    public void RefusesAMissingAnchorOrSection()
    {
        Assert.Contains("couldn't find export_today under pred_bat", Refused("pred_bat, after export_today", "  pv_today: x"));
        Assert.Contains("has no predbat_config section", Refused("predbat_config", "  pv_today: x"));
    }

    [Fact]
    public void RefusesToAddAKeyThatIsAlreadyThere()
    {
        var message = Refused("pred_bat", "  load_today:\n    - sensor.other");
        Assert.Contains("already has load_today under pred_bat", message);
    }

    [Fact]
    public void RefusesAReplacementThatWouldDuplicateAKey()
    {
        var message = Refused("pred_bat", "  battery_rate_max_scaling: 0.9\n  inverter_limit:\n    - 3000", "  battery_rate_max_scaling: 1.0  # measured in October");
        Assert.Contains("twice", message);
    }

    [Fact]
    public void RefusesAnEditThatChangesKeysItDoesNotName()
    {
        var file = "pred_bat:\n  rate: &rate 1.0\n  other: *rate\n";
        var message = Refused("pred_bat", "  rate: &rate 0.5", "  rate: &rate 1.0", file);
        Assert.Contains("would also change pred_bat › other", message);
    }

    [Fact]
    public void RefusesInvalidYaml()
    {
        Assert.Contains("wouldn't be valid YAML", Refused("pred_bat, after import_today", "  export_today: [sensor.unclosed"));
        Assert.Contains("isn't valid YAML as it stands", Refused("pred_bat", "  x: 1", null, "pred_bat:\n  a: [1\n"));
    }

    [Fact]
    public void RefusesTabsAndNoOps()
    {
        Assert.Contains("tabs", Refused("pred_bat", "\texport_today: 1"));
        Assert.Contains("wouldn't change any setting", Refused("pred_bat", "  battery_rate_max_scaling: 1.0", "  battery_rate_max_scaling: 1.0  # measured in October"));
    }

    [Fact]
    public void KeepsSecretReferencesExactly()
    {
        var plan = Plan("pred_bat, after octopus_api_key", "  solcast_api_key: !secret solcast_key");
        Assert.Contains("\n  octopus_api_key: !secret octopus_api_key\n  solcast_api_key: !secret solcast_key\n", plan.Text);
    }

    [Fact]
    public void FillsAHiddenValueFromTheLineItReplaces()
    {
        var plan = Plan("pred_bat", "  battery_rate_max_scaling: 0.8\n  octopus_api_key: [redacted]",
            "  battery_rate_max_scaling: 1.0  # measured in October\n  octopus_api_key: xxx");
        Assert.Contains("\n  battery_rate_max_scaling: 0.8\n  octopus_api_key: !secret octopus_api_key\n", plan.Text);
        Assert.DoesNotContain("redacted", plan.Text);
    }

    [Fact]
    public void RefusesToInventAHiddenValue()
    {
        Assert.Contains("hidden for safety", Refused("pred_bat", "  solcast_api_key: [redacted]"));
        Assert.Contains("hidden for safety", Refused("pred_bat", "  battery_rate_max_scaling: 0.8\n  solcast_api_key: •••", "  battery_rate_max_scaling: 1.0  # measured in October"));
    }

    [Fact]
    public void KeepsWindowsLineEndings()
    {
        var crlf = Lf(Apps).Replace("\n", "\r\n");
        var plan = YamlFileEdit.Plan(crlf, "pred_bat, after import_today", "  export_today:\n    - sensor.export", null);
        Assert.Contains("\r\n  export_today:\r\n    - sensor.export\r\n  load_today:\r\n", plan.Text);
        Assert.DoesNotContain("\r\r", plan.Text);
        Assert.Equal(crlf.Split("\r\n").Length + 2, plan.Text.Split("\r\n").Length);
        Assert.DoesNotMatch("[^\r]\n", plan.Text);
        var replaced = YamlFileEdit.Plan(crlf, "pred_bat", "  battery_rate_max_scaling: 0.9", "  battery_rate_max_scaling: 1.0  # measured in October");
        Assert.Equal(crlf.Replace("  battery_rate_max_scaling: 1.0  # measured in October", "  battery_rate_max_scaling: 0.9"), replaced.Text);
    }

    [Fact]
    public void AppendsToAListIncludingOneWrittenLevelWithItsKey()
    {
        var plan = Plan("pred_bat.inverter_limit", "    - 3000");
        Assert.Contains("  inverter_limit:\n    - 3600\n    - 3000\n  battery_rate_max_scaling", plan.Text);
        Assert.Equal("pred_bat.inverter_limit", plan.Keys.Single().Key);
        var compact = "pred_bat:\n  import_today:\n  - sensor.a\n  load_today: x\n";
        Assert.Equal("pred_bat:\n  import_today:\n  - sensor.a\n  - sensor.b\n  load_today: x\n", Plan("pred_bat.import_today", "- sensor.b", null, compact).Text);
    }

    [Fact]
    public void FillsAnEmptySection()
    {
        var file = "pred_bat:\nother:\n  a: 1\n";
        Assert.Equal("pred_bat:\n  export_today:\n    - sensor.e\nother:\n  a: 1\n", Plan("pred_bat", "export_today:\n  - sensor.e", null, file).Text);
    }

    [Fact]
    public void KeepsAMissingFinalNewline()
    {
        var file = "pred_bat:\n  a: 1\n  b: 2";
        Assert.Equal("pred_bat:\n  a: 1\n  b: 3", Plan("pred_bat", "  b: 3", "  b: 2", file).Text);
        Assert.Equal("pred_bat:\n  a: 1\n  b: 2\n  c: 3\n", Plan("pred_bat", "  c: 3", null, file).Text);
    }

    [Fact]
    public void ReplacesListItemsInsideANestedSection()
    {
        var plan = Plan("pred_bat.import_today", "    - sensor.grid_import_today", "    - sensor.givtcp_{geserial}_import_energy_today_kwh");
        Assert.Equal(Lf(Apps).Replace("sensor.givtcp_{geserial}_import_energy_today_kwh", "sensor.grid_import_today"), plan.Text);
        Assert.Equal("pred_bat.import_today", plan.Keys.Single().Key);
    }

    [Fact]
    public void AddsAtTheTopLevelWhenAsked()
    {
        var plan = Plan("top level", "new_app:\n  module: x");
        Assert.EndsWith("other_app:\n  module: other\nnew_app:\n  module: x\n", plan.Text);
    }
}
