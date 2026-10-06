using Joule;
using Xunit;
namespace Joule.Tests;

public class ConfigFileMaskTests : IDisposable
{
    readonly string path = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "predbat-mask-" + Guid.NewGuid().ToString("N"));
    ConfigFileArchive Archive(params string[] files) { Directory.CreateDirectory(Path.Combine(path, "config")); return new(new() { Root = Path.Combine(path, "config"), ArchiveDirectory = Path.Combine(path, "archive"), AllowedFiles = files }); }
    public void Dispose() { if (Directory.Exists(path)) Directory.Delete(path, true); }

    const string AppsYaml = """
        pred_bat:
          module: predbat
          class: PredBat
          # Home Assistant token: abc123
          ha_key: eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.secret-part
          load_today:
            - sensor.givtcp_ab1234_load_energy_today_kwh
          inverter_type: GE
          rates_import:
            - start: "00:30"
              end: "05:30"
              rate: 7.5
          notes: |
            looks_like_a_key: but is text
            second line
          battery_rate_max_scaling: 0.95
        """;

    [Fact]
    public void YamlKeepsKeysAndIndentationAndHidesEveryValueAndComment()
    {
        var text = ConfigFileMask.Render(ConfigFileMask.Mask("apps.yaml", AppsYaml));
        var lines = text.Split('\n');
        Assert.Equal("pred_bat:", lines[0]);
        Assert.Equal("  module: •••", lines[1]);
        Assert.Equal("  # •••", lines[3]);
        Assert.Equal("  ha_key: •••", lines[4]);
        Assert.Equal("  load_today:", lines[5]);
        Assert.Equal("    - •••", lines[6]);
        Assert.Equal("    - start: •••", lines[9]);
        Assert.Equal("      rate: •••", lines[11]);
        Assert.Equal("  notes: •••", lines[12]);
        Assert.Equal("    •••", lines[13]);
        Assert.Equal("  battery_rate_max_scaling: •••", lines[15]);
        foreach (var leak in new[] { "eyJ", "secret", "abc123", "givtcp", "GE", "7.5", "00:30", "looks_like_a_key", "0.95", "PredBat" })
            Assert.DoesNotContain(leak, text);
    }

    [Fact]
    public void SecretReferencesStayVisibleInSafeValuesMode()
    {
        const string yaml = """
            pred_bat:
              mcp_secret: !secret predbat_mcp_secret
              ha_key: !secret ha_token
              api_key: plain-text-key-1234567890abcdef
              password: "!secret but quoted literally with spaces"
            """;
        var text = ConfigFileMask.Render(ConfigFileMask.Mask("apps.yaml", yaml, keepSafeValues: true));
        // A !secret reference names an entry in secrets.yaml; the homeowner needs to see it to follow a suggested edit.
        Assert.Contains("  mcp_secret: !secret predbat_mcp_secret", text);
        Assert.Contains("  ha_key: !secret ha_token", text);
        Assert.Contains("  api_key: •••", text);
        Assert.Contains("  password: •••", text);
        Assert.DoesNotContain("plain-text-key", text);
        // Without safe values everything is still hidden.
        Assert.DoesNotContain("!secret", ConfigFileMask.Render(ConfigFileMask.Mask("apps.yaml", yaml)));
    }

    [Fact]
    public void SafeValuesModeKeepsOrdinaryValuesButHidesSecrets()
    {
        var text = ConfigFileMask.Render(ConfigFileMask.Mask("apps.yaml", AppsYaml, keepSafeValues: true));
        Assert.Contains("  inverter_type: GE", text);
        Assert.Contains("    - sensor.givtcp_ab1234_load_energy_today_kwh", text);
        Assert.Contains("  battery_rate_max_scaling: 0.95", text);
        Assert.Contains("  ha_key: •••", text);
        Assert.DoesNotContain("eyJ", text);
        Assert.DoesNotContain("looks_like_a_key", text);
        Assert.DoesNotContain("abc123", text);
    }

    [Theory]
    [InlineData("url: http://user:pass@example.com/x")]
    [InlineData("webhook: https://example.com/hook?token=abc")]
    [InlineData("anything: Bearer abcdef")]
    [InlineData("blob: Zm9vYmFyYmF6cXV4cXV1eHF1dXhxdXV4cXV1eHF1dXg=")]
    [InlineData("password: hunter2")]
    [InlineData("octopus_api_key: sk_live_12345")]
    public void SafeValuesModeStillHidesCredentialShapedValues(string line)
    {
        var text = ConfigFileMask.Render(ConfigFileMask.Mask("apps.yaml", line, keepSafeValues: true));
        Assert.EndsWith(": •••", text);
    }

    [Fact]
    public void JsonIsRenderedWithKeysAndMaskedValues()
    {
        var json = "{\"load_scaling\":\"1.08\",\"nested\":{\"token\":\"abc\",\"list\":[1,2]},\"empty\":{}}";
        var text = ConfigFileMask.Render(ConfigFileMask.Mask("runtime-settings.json", json));
        Assert.Contains("\"load_scaling\": •••,", text);
        Assert.Contains("  \"nested\": {", text);
        Assert.Contains("      •••,", text);
        Assert.Contains("\"empty\": {}", text);
        Assert.DoesNotContain("1.08", text);
        Assert.DoesNotContain("abc", text);
    }

    [Fact]
    public void UnreadableJsonFallsBackToHidingEveryLine()
    {
        var text = ConfigFileMask.Render(ConfigFileMask.Mask("x.json", "{ not json: secret\n}"));
        Assert.Equal("•••\n•••", text);
    }

    [Fact]
    public void TomlSectionsAndKeysStayAndTripleQuotedValuesAreHidden()
    {
        var toml = "[server]\nhost = \"10.0.0.1\"\nnote = \"\"\"\ninner_key: value\n\"\"\"\nport = 80 # comment\n";
        var text = ConfigFileMask.Render(ConfigFileMask.Mask("apps.toml", toml));
        Assert.StartsWith("[server]\nhost = •••\nnote = •••\n•••\n•••\nport = •••", text);
        Assert.DoesNotContain("inner_key", text);
        Assert.DoesNotContain("10.0.0.1", text);
    }

    [Fact]
    public void UnrecognisedLinesAreHiddenWhole()
    {
        var text = ConfigFileMask.Render(ConfigFileMask.Mask("apps.yaml", "key: value\nthis is not yaml: really: no\n  plain continuation"));
        Assert.DoesNotContain("plain", text);
        Assert.DoesNotContain("continuation", text);
    }

    [Fact]
    public void KeysThatLookLikeTokensAreHidden()
    {
        var text = ConfigFileMask.Render(ConfigFileMask.Mask("apps.yaml", "Ab12Cd34Ef56Gh78Ij90Kl12Mn: x"));
        Assert.Equal("•••: •••", text);
    }

    [Fact]
    public void ChangesNameAddedRemovedAndChangedKeysWithoutValues()
    {
        var before = ConfigFileMask.Mask("apps.yaml", "pred_bat:\n  load_scaling: 1.08\n  password: one\n  old_key: x\n  notes: |\n    a\n");
        var after = ConfigFileMask.Mask("apps.yaml", "pred_bat:\n  load_scaling: 0.99\n  password: one\n  new_key: y\n  notes: |\n    b\n");
        var changes = ConfigFileMask.Changes(before, after);
        Assert.Contains(changes, c => c is { Name: "pred_bat › load_scaling", Change: "changed" });
        Assert.Contains(changes, c => c is { Name: "pred_bat › new_key", Change: "added" });
        Assert.Contains(changes, c => c is { Name: "pred_bat › old_key", Change: "removed" });
        Assert.Contains(changes, c => c is { Name: "pred_bat › notes", Change: "changed" });
        Assert.DoesNotContain(changes, c => c.Name.Contains("password"));
        Assert.DoesNotContain(changes, c => c.Name == "pred_bat");
        Assert.DoesNotContain(changes, c => c.Key.Contains("1.08") || c.Key.Contains("0.99"));
    }

    [Fact]
    public void MarkedDiffFollowsInsertionsInsteadOfLinePositions()
    {
        var before = ConfigFileMask.Mask("apps.yaml", "a: 1\nb: 2\nc: 3");
        var after = ConfigFileMask.Mask("apps.yaml", "a: 1\nnew: 9\nb: 2\nc: 3");
        var (b, a) = ConfigFileMask.MarkedDiff(before, after);
        Assert.Equal("  a: •••\n  b: •••\n  c: •••", b);
        Assert.Equal("  a: •••\n+ new: •••\n  b: •••\n  c: •••", a);
    }

    [Fact]
    public void ArchiveViewShowsStructureAndDiffListsChangedKeys()
    {
        var a = Archive("apps.yaml"); var file = Path.Combine(path, "config/apps.yaml");
        File.WriteAllText(file, "pred_bat:\n  load_scaling: 1.08\n  ha_key: secret-one\n");
        var first = a.Capture(1, "first");
        File.WriteAllText(file, "pred_bat:\n  load_scaling: 0.99\n  ha_key: secret-one\n  pv_scaling: 1.0\n");
        var second = a.Capture(2, "second");
        var view = a.View(first.Id, "apps.yaml");
        Assert.Equal("pred_bat:\n  load_scaling: •••\n  ha_key: •••\n", view.Text);
        Assert.Contains("value", view.Redaction);
        var diff = a.Diff(first.Id, second.Id, "apps.yaml");
        Assert.True(diff.Changed);
        Assert.Equal(["pred_bat › load_scaling:changed", "pred_bat › pv_scaling:added"], diff.Keys!.Select(k => $"{k.Name}:{k.Change}").Order().ToArray());
        Assert.DoesNotContain("secret", diff.Before + diff.After);
    }
}
