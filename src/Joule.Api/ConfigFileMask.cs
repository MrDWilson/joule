using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Joule;

/// <summary>One line of a masked configuration file. <see cref="Key"/> is the setting path the line belongs to (null for
/// structure such as comments or document markers); <see cref="Raw"/> is the original line and never leaves the server.</summary>
public sealed record MaskedLine(string Text, string? Key, string Raw);
/// <summary>A setting that differs between two copies of a file: added, removed or changed. Values are never included.</summary>
public sealed record ConfigKeyChange(string Key, string Name, string Change);
/// <summary>One line of a review diff (masked text only). Kind: " ", "-", "+" or "…" for unchanged lines left out.</summary>
public sealed record DiffLine(string Kind, int? Old, int? New, string Text);

/// <summary>
/// Shows the shape of a YAML, JSON, TOML or INI configuration file without its contents: keys, nesting and indentation stay,
/// every value and comment becomes •••. Multi-line values (YAML block and quoted scalars, flow collections, TOML triple-quoted
/// strings) are hidden line by line, so text inside them that happens to look like "key: value" is never shown as a key.
/// A line that can't be understood is hidden whole. With <c>keepSafeValues</c> (used for Predbat's own, already masked
/// apps.yaml) ordinary values stay and only values that look secret are hidden.
/// </summary>
public static class ConfigFileMask
{
    public const string Hidden = "•••";
    const int MaxChanges = 200;
    static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(200);
    static readonly Regex YamlKey = new("""^(?<ind>[ \t]*)(?<dash>-[ \t]+)?(?<key>[A-Za-z0-9_][A-Za-z0-9_.\-/ ]{0,120}?|"[^"\n]{1,120}"|'[^'\n]{1,120}')[ \t]*:(?:[ \t]+(?<val>.*?))?[ \t]*$""", RegexOptions.CultureInvariant, RegexBudget);
    static readonly Regex YamlItem = new(@"^(?<ind>[ \t]*)-(?:[ \t]+(?<val>.*?))?[ \t]*$", RegexOptions.CultureInvariant, RegexBudget);
    static readonly Regex IniSection = new("""^(?<ind>[ \t]*)(?<head>\[\[?[A-Za-z0-9_.\- "']{1,120}\]\]?)[ \t]*(?:[#;].*)?$""", RegexOptions.CultureInvariant, RegexBudget);
    static readonly Regex IniKey = new("""^(?<ind>[ \t]*)(?<key>[A-Za-z0-9_][A-Za-z0-9_.\-]{0,120}|"[^"\n]{1,120}")[ \t]*(?<op>[=:])[ \t]*(?<val>.*?)[ \t]*$""", RegexOptions.CultureInvariant, RegexBudget);
    static readonly Regex SecretValue = new("""Bearer\s+\S+|https?://[^\s"'<>]*[@?][^\s"'<>]*|\[redacted|^(?=.*\d)(?=.*[A-Za-z])[A-Za-z0-9+/=\-]{32,}$|^ey[A-Za-z0-9_\-]{16,}\.""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexBudget);

    /// <summary>Masks a whole file; the format follows the extension (.json, .toml/.cfg/.conf, otherwise YAML).</summary>
    public static List<MaskedLine> Mask(string path, string text, bool keepSafeValues = false, bool keepComments = false)
    {
        try
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".json") return Json(text, keepSafeValues) ?? HideAll(text);
            if (extension is ".toml" or ".cfg" or ".conf" or ".ini") return Ini(text, keepSafeValues);
            return Yaml(text, keepSafeValues, keepComments);
        }
        catch (RegexMatchTimeoutException) { return HideAll(text); }
    }

    public static string Render(IEnumerable<MaskedLine> lines) => string.Join('\n', lines.Select(x => x.Text));

    /// <summary>Every non-blank line hidden: the fallback when the format can't be read safely.</summary>
    public static List<MaskedLine> HideAll(string text) =>
        Lines(text).Select(line => new MaskedLine(string.IsNullOrWhiteSpace(line) ? "" : Indent(line) + Hidden, null, line)).ToList();

    static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');
    static string Indent(string line) => line[..(line.Length - line.TrimStart(' ', '\t').Length)];

    /// <summary>"pred_bat.[].inverter" → "pred_bat › inverter": list markers dropped, segments joined.</summary>
    public static string DisplayName(string key) => string.Join(" › ", key.Split('.').Where(s => s is not ("[]" or "")).Select(s => s.Trim('"', '\'')));

    /// <summary>Keys that look like a credential themselves (a long random token used as a name) are hidden too.</summary>
    static string SafeKey(string key)
    {
        var bare = key.Trim('"', '\'');
        return bare.Length >= 24 && !bare.Contains('_') && !bare.Contains(' ') && bare.Any(char.IsDigit) && bare.Any(char.IsLetter) ? Hidden : key;
    }

    /// <summary>A Home Assistant "!secret name" reference names an entry in secrets.yaml; it is not the credential itself.</summary>
    static readonly Regex SecretReference = new(@"^!secret\s+[A-Za-z0-9_.\-]+$", RegexOptions.CultureInvariant, RegexBudget);
    static bool LooksSecret(string? key, string value) =>
        !SecretReference.IsMatch(value.Trim()) &&
        ((key is not null && PredbatMcpSafety.SensitiveKey(key.Split('.').Last().Trim('"', '\''))) || SecretValue.IsMatch(value.Trim().Trim('"', '\'')));

    static string ShowValue(string? key, string value, bool keepSafeValues)
    {
        if (!keepSafeValues) return Hidden;
        var v = StripComment(value).Trim();
        return v.Length == 0 ? "" : LooksSecret(key, v) ? Hidden : v;
    }

    static string StripComment(string value)
    {
        var quote = '\0';
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (quote != '\0') { if (c == quote && (quote == '\'' || i == 0 || value[i - 1] != '\\')) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '#' && (i == 0 || char.IsWhiteSpace(value[i - 1]))) return value[..i];
        }
        return value;
    }

    /// <summary>Unescaped quote characters of one kind: odd means the quoted value carries on to the next line.</summary>
    static bool OpensQuote(string value)
    {
        var v = StripComment(value).Trim();
        if (v.Length == 0 || v[0] is not ('"' or '\'')) return false;
        var q = v[0];
        if (q == '\'') return v.Replace("''", "").Count(c => c == '\'') % 2 == 1;
        var count = 0;
        for (var i = 0; i < v.Length; i++) if (v[i] == '"' && (i == 0 || v[i - 1] != '\\')) count++;
        return count % 2 == 1;
    }
    static int Depth(string value)
    {
        var depth = 0; var quote = '\0';
        foreach (var c in value)
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c is '[' or '{') depth++;
            else if (c is ']' or '}') depth--;
        }
        return depth;
    }

    // ------------------------------------------------------------------ YAML
    static List<MaskedLine> Yaml(string text, bool keepSafeValues, bool keepComments = false)
    {
        var result = new List<MaskedLine>();
        var stack = new List<(int Column, string Name)>();
        // Multi-line value state: a block scalar ends when a line is no deeper than its key; quotes and flows at their close.
        int? blockColumn = null; char quote = '\0'; var flow = 0; string? owner = null;
        string Path(string name) => string.Join('.', stack.Select(s => s.Name).Append(name));
        void Pop(int column) { while (stack.Count > 0 && stack[^1].Column >= column) stack.RemoveAt(stack.Count - 1); }
        foreach (var line in Lines(text))
        {
            var indent = Indent(line);
            if (blockColumn is { } bc)
            {
                if (string.IsNullOrWhiteSpace(line)) { result.Add(new("", owner, line)); continue; }
                if (indent.Length > bc) { result.Add(new(indent + Hidden, owner, line)); continue; }
                blockColumn = null;
            }
            if (quote != '\0')
            {
                result.Add(new(string.IsNullOrWhiteSpace(line) ? "" : indent + Hidden, owner, line));
                var closes = quote == '\'' ? line.Replace("''", "").Contains('\'') : Regex.IsMatch(line, @"(^|[^\\])""", RegexOptions.None, RegexBudget);
                if (closes) quote = '\0';
                continue;
            }
            if (flow > 0)
            {
                result.Add(new(string.IsNullOrWhiteSpace(line) ? "" : indent + Hidden, owner, line));
                flow += Depth(line);
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) { result.Add(new("", null, line)); continue; }
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#')) { result.Add(new(indent + (keepComments ? Comment(trimmed) : "# " + Hidden), null, line)); continue; }
            if (trimmed is "---" or "..." && indent.Length == 0) { stack.Clear(); result.Add(new(trimmed, null, line)); continue; }

            string? value; string key; string prefix; int keyColumn;
            var k = YamlKey.Match(line);
            if (k.Success)
            {
                var dash = k.Groups["dash"].Value;
                var dashColumn = k.Groups["ind"].Value.Length;
                keyColumn = dashColumn + dash.Length;
                if (dash.Length > 0) { Pop(dashColumn); stack.Add((dashColumn, "[]")); }
                else Pop(keyColumn);
                var name = k.Groups["key"].Value;
                key = Path(name);
                value = k.Groups["val"].Success ? k.Groups["val"].Value : null;
                prefix = k.Groups["ind"].Value + dash + SafeKey(name) + ":";
                if (string.IsNullOrEmpty(value) || StripComment(value).Trim().Length == 0) { stack.Add((keyColumn, name)); result.Add(new(prefix, key, line)); continue; }
            }
            else if (YamlItem.Match(line) is { Success: true } item)
            {
                var dashColumn = item.Groups["ind"].Value.Length;
                Pop(dashColumn + 1);
                keyColumn = dashColumn;
                key = Path("[]");
                value = item.Groups["val"].Success ? item.Groups["val"].Value : null;
                prefix = item.Groups["ind"].Value + "-";
                if (string.IsNullOrEmpty(value)) { stack.Add((dashColumn, "[]")); result.Add(new(prefix, key, line)); continue; }
            }
            else
            {
                // Not a key or a list item (for example a wrapped plain value): hide the whole line.
                result.Add(new(indent + Hidden, stack.Count > 0 ? string.Join('.', stack.Select(s => s.Name)) : null, line));
                continue;
            }
            var v = StripComment(value).Trim();
            var multiLine = true;
            if (v.Length > 0 && v[0] is '|' or '>') blockColumn = keyColumn;
            else if (OpensQuote(value)) quote = v[0];
            else if (v.Length > 0 && v[0] is '[' or '{' && Depth(v) > 0) flow = Depth(v);
            else multiLine = false;
            if (multiLine) owner = key;
            result.Add(new(prefix + " " + (multiLine ? Hidden : YamlValue(key, value, keepSafeValues)), key, line));
        }
        return result;
    }
    /// <summary>A comment shown for review: anything that reads like a credential ("token: abc", a bearer value) is redacted, and a
    /// long random-looking word (a key pasted into a comment) is hidden too.</summary>
    static string Comment(string comment)
    {
        var text = PredbatMcpSafety.CleanText(comment, []);
        return Regex.Replace(text, @"(?<![\w.])(?=[A-Za-z0-9+/=\-]*\d)(?=[A-Za-z0-9+/=\-]*[A-Za-z])[A-Za-z0-9+/=\-]{32,}", Hidden, RegexOptions.None, RegexBudget);
    }
    static string YamlValue(string key, string value, bool keepSafeValues) => ShowValue(key, value, keepSafeValues) is { Length: > 0 } shown ? shown : Hidden;

    // ------------------------------------------------------------------ TOML / INI
    static List<MaskedLine> Ini(string text, bool keepSafeValues)
    {
        var result = new List<MaskedLine>();
        var section = ""; string? multi = null; var flow = 0; string? owner = null;
        foreach (var line in Lines(text))
        {
            var indent = Indent(line);
            if (multi is not null)
            {
                result.Add(new(string.IsNullOrWhiteSpace(line) ? "" : indent + Hidden, owner, line));
                if (line.Contains(multi, StringComparison.Ordinal)) multi = null;
                continue;
            }
            if (flow > 0) { result.Add(new(string.IsNullOrWhiteSpace(line) ? "" : indent + Hidden, owner, line)); flow += Depth(line); continue; }
            if (string.IsNullOrWhiteSpace(line)) { result.Add(new("", null, line)); continue; }
            var trimmed = line.Trim();
            if (trimmed[0] is '#' or ';') { result.Add(new(indent + trimmed[0] + " " + Hidden, null, line)); continue; }
            if (IniSection.Match(line) is { Success: true } s)
            {
                var head = s.Groups["head"].Value;
                section = head.Trim('[', ']').Trim();
                result.Add(new(s.Groups["ind"].Value + head, section, line));
                continue;
            }
            if (IniKey.Match(line) is { Success: true } k)
            {
                var name = k.Groups["key"].Value; var key = section.Length > 0 ? $"{section}.{name}" : name;
                var value = k.Groups["val"].Value; var v = StripComment(value).Trim();
                var prefix = $"{k.Groups["ind"].Value}{SafeKey(name)} {k.Groups["op"].Value} ";
                if (v.StartsWith("\"\"\"", StringComparison.Ordinal) || v.StartsWith("'''", StringComparison.Ordinal))
                {
                    var delimiter = v[..3];
                    if (v.Length < 6 || !v[3..].Contains(delimiter, StringComparison.Ordinal)) { multi = delimiter; owner = key; }
                    result.Add(new(prefix + Hidden, key, line));
                    continue;
                }
                if (v.Length > 0 && v[0] is '[' or '{' && Depth(v) > 0) { flow = Depth(v); owner = key; result.Add(new(prefix + Hidden, key, line)); continue; }
                result.Add(new(prefix + (ShowValue(key, value, keepSafeValues) is { Length: > 0 } shown ? shown : Hidden), key, line));
                continue;
            }
            result.Add(new(indent + Hidden, section.Length > 0 ? section : null, line));
        }
        return result;
    }

    // ------------------------------------------------------------------ JSON
    static List<MaskedLine>? Json(string text, bool keepSafeValues)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 64 }); }
        catch (JsonException) { return null; }
        using (document)
        {
            var result = new List<MaskedLine>();
            void Write(JsonElement e, string path, string indent, string prefix, bool comma)
            {
                var end = comma ? "," : "";
                switch (e.ValueKind)
                {
                    case JsonValueKind.Object:
                        var props = e.EnumerateObject().ToList();
                        if (props.Count == 0) { result.Add(new(indent + prefix + "{}" + end, path.Length > 0 ? path : null, path + "={}")); return; }
                        result.Add(new(indent + prefix + "{", path.Length > 0 ? path : null, path + "{"));
                        for (var i = 0; i < props.Count; i++)
                        {
                            var p = props[i];
                            var name = JsonSerializer.Serialize(SafeKey(p.Name) == Hidden ? Hidden : p.Name);
                            Write(p.Value, path.Length > 0 ? $"{path}.{p.Name}" : p.Name, indent + "  ", name + ": ", i < props.Count - 1);
                        }
                        result.Add(new(indent + "}" + end, null, path + "}"));
                        return;
                    case JsonValueKind.Array:
                        var items = e.EnumerateArray().ToList();
                        if (items.Count == 0) { result.Add(new(indent + prefix + "[]" + end, path.Length > 0 ? path : null, path + "=[]")); return; }
                        result.Add(new(indent + prefix + "[", path.Length > 0 ? path : null, path + "["));
                        for (var i = 0; i < items.Count; i++) Write(items[i], $"{path}.[]", indent + "  ", "", i < items.Count - 1);
                        result.Add(new(indent + "]" + end, null, path + "]"));
                        return;
                    default:
                        var raw = e.GetRawText();
                        var shown = keepSafeValues && !LooksSecret(path, e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : raw) ? raw : Hidden;
                        result.Add(new(indent + prefix + shown + end, path.Length > 0 ? path : null, path + "=" + raw));
                        return;
                }
            }
            Write(document.RootElement, "", "", "", false);
            return result;
        }
    }

    // ------------------------------------------------------------------ comparison
    /// <summary>The settings that differ between two masked copies, by key path. Values are compared on the server only.</summary>
    public static List<ConfigKeyChange> Changes(IReadOnlyList<MaskedLine> before, IReadOnlyList<MaskedLine> after)
    {
        static Dictionary<string, string> Values(IReadOnlyList<MaskedLine> lines)
        {
            var map = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
            foreach (var l in lines.Where(l => l.Key is not null))
            {
                if (!map.TryGetValue(l.Key!, out var b)) map[l.Key!] = b = new();
                b.Append(l.Raw.Trim()).Append('\n');
            }
            return map.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.Ordinal);
        }
        var a = Values(before); var b = Values(after);
        var changes = new List<ConfigKeyChange>();
        foreach (var key in b.Keys.Where(k => !a.ContainsKey(k))) changes.Add(new(key, DisplayName(key), "added"));
        foreach (var key in a.Keys.Where(k => !b.ContainsKey(k))) changes.Add(new(key, DisplayName(key), "removed"));
        foreach (var key in a.Keys.Where(k => b.TryGetValue(k, out var v) && v != a[k])) changes.Add(new(key, DisplayName(key), "changed"));
        // A container whose children changed isn't listed itself; its children are.
        var result = changes.Where(c => c.Change != "changed" || !changes.Any(o => o.Key.StartsWith(c.Key + ".", StringComparison.Ordinal)))
            .GroupBy(c => (c.Name, c.Change)).Select(g => g.First())
            .OrderBy(c => c.Key, StringComparer.Ordinal).Take(MaxChanges).ToList();
        return result;
    }

    /// <summary>Both copies, masked, with "- " / "+ " in front of lines that differ (a line-by-line LCS on the original text).</summary>
    public static (string Before, string After) MarkedDiff(IReadOnlyList<MaskedLine> before, IReadOnlyList<MaskedLine> after)
    {
        var (keepBefore, keepAfter) = Common(before.Select(x => x.Raw).ToArray(), after.Select(x => x.Raw).ToArray());
        return (string.Join('\n', before.Select((l, i) => (keepBefore[i] ? "  " : "- ") + l.Text)), string.Join('\n', after.Select((l, i) => (keepAfter[i] ? "  " : "+ ") + l.Text)));
    }

    /// <summary>
    /// The lines that differ between two masked copies with up to <paramref name="context"/> unchanged lines around each change, like a
    /// unified diff. Kind is " " (unchanged), "-" (removed), "+" (added) or "…" (unchanged lines left out). Line numbers are 1-based.
    /// </summary>
    public static List<DiffLine> Hunks(IReadOnlyList<MaskedLine> before, IReadOnlyList<MaskedLine> after, int context = 3, int maxLines = 400)
    {
        var (keepA, keepB) = Common(before.Select(x => x.Raw).ToArray(), after.Select(x => x.Raw).ToArray());
        var ops = new List<DiffLine>();
        for (int i = 0, j = 0; i < before.Count || j < after.Count;)
        {
            if (i < before.Count && !keepA[i]) { ops.Add(new("-", i + 1, null, before[i].Text)); i++; }
            else if (j < after.Count && !keepB[j]) { ops.Add(new("+", null, j + 1, after[j].Text)); j++; }
            else if (i < before.Count && j < after.Count) { ops.Add(new(" ", i + 1, j + 1, after[j].Text)); i++; j++; }
            else break;
        }
        var changed = ops.Select((o, index) => (o, index)).Where(x => x.o.Kind != " ").Select(x => x.index).ToList();
        var result = new List<DiffLine>();
        var shownUpTo = -1;
        for (var index = 0; index < ops.Count && result.Count < maxLines; index++)
        {
            if (!changed.Any(c => Math.Abs(c - index) <= context)) continue;
            if (index > shownUpTo + 1) result.Add(new("…", null, null, ""));
            result.Add(ops[index]); shownUpTo = index;
        }
        if (shownUpTo >= 0 && shownUpTo < ops.Count - 1) result.Add(new("…", null, null, ""));
        return result;
    }

    static (bool[] A, bool[] B) Common(string[] a, string[] b)
    {
        var keepA = new bool[a.Length]; var keepB = new bool[b.Length];
        if ((long)a.Length * b.Length > 4_000_000)
        {
            // Too large for a full comparison: fall back to matching line positions.
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++) keepA[i] = keepB[i] = a[i] == b[i];
            return (keepA, keepB);
        }
        var table = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                table[i, j] = a[i] == b[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
        for (int i = 0, j = 0; i < a.Length && j < b.Length;)
        {
            if (a[i] == b[j]) { keepA[i] = keepB[j] = true; i++; j++; }
            else if (table[i + 1, j] >= table[i, j + 1]) i++;
            else j++;
        }
        return (keepA, keepB);
    }
}
