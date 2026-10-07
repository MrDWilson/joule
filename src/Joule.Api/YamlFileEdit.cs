using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Joule;

/// <summary>Joule won't make this edit; the message says why in plain words (shown to the homeowner as is).</summary>
public sealed class YamlEditRefused(string message) : DomainException(message, 409);

/// <summary>The edited file and what changed. FirstLine is 1-based; Placement says where the lines go, in words.</summary>
public sealed record YamlEditPlan(string Text, List<ConfigKeyChange> Keys, int FirstLine, int RemovedLines, int AddedLines, string Placement);

/// <summary>
/// Makes one suggested edit to a YAML configuration file (Predbat's apps.yaml) the way a careful person would: only the lines the
/// edit is about change, every other line (comments, blank lines, quoting, line endings) stays byte for byte. A replacement needs
/// its <c>before</c> lines to appear exactly once; an addition goes under the section its location names (for example
/// <c>pred_bat</c>, optionally "after import_today"). The result must parse as YAML and may only change the keys the edit names,
/// otherwise the edit is refused with a plain reason. "!secret name" references are kept; a value that was hidden for safety
/// ([redacted], xxx, •••) is filled back in from the file's own line for the same key, never guessed.
/// </summary>
public static class YamlFileEdit
{
    static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(200);
    static readonly Regex KeyLine = new("""^(?<ind>[ ]*)(?<dash>-[ ]+)?(?<key>[A-Za-z0-9_][A-Za-z0-9_.\-/ ]{0,120}?|"[^"\n]{1,120}"|'[^'\n]{1,120}')[ ]*:(?<rest>[ ].*|)$""", RegexOptions.CultureInvariant, Budget);
    static readonly Regex MaskedValue = new("""^[ ]*["']?(?:\[redacted[^\]]*\]|xxx+|•••|\*{3,})["']?[ ]*(?:#.*)?$""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, Budget);
    static readonly Regex Anchor = new("""\b(?<dir>after|below|before|above)\s+(?:the\s+)?(?:key\s+)?[`'"‘“]?(?<key>[A-Za-z0-9_][A-Za-z0-9_\-]*)""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, Budget);
    static readonly Regex PathToken = new("""[A-Za-z_][A-Za-z0-9_\-]*(?:\s*(?:\.|›|>|/|→)\s*[A-Za-z0-9_][A-Za-z0-9_\-]*)*""", RegexOptions.CultureInvariant, Budget);
    static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "after", "below", "before", "above", "under", "in", "at", "the", "end", "of", "add", "insert", "section", "key", "top", "level", "file",
        "apps", "yaml", "yml", "replace", "to", "a", "an", "and", "existing", "list", "block", "inside", "within", "line", "lines", "new",
        "entry", "root", "mapping", "config", "configuration", "with", "for", "on", "or", "it", "its", "this", "here",
    };

    public static YamlEditPlan Plan(string original, string location, string snippet, string? before, string file = "apps.yaml")
    {
        var lines = new FileLines(original);
        var originalYaml = Parse(original, file, edited: false);
        var add = Clean(snippet);
        if (add.Count == 0) throw new YamlEditRefused("The suggestion has no lines to add.");
        if (add.Any(l => l.Length - l.TrimStart(' ', '\t').Length != Indent(l)))
            throw new YamlEditRefused("The suggested lines are indented with tabs, which YAML doesn't allow. Ask for the edit again.");
        var old = before is null ? [] : Clean(before);

        int start, count; string owner; string placement;
        if (old.Count > 0)
        {
            start = FindBefore(lines, old, file);
            count = old.Count;
            var region = lines.Content.Skip(start).Take(count).ToList();
            add = Reindent(add, MinIndent(region));
            add = FillMasked(add, region, file);
            owner = Owner(originalYaml, lines, start, MinIndent(region));
            placement = count == 1 ? $"Replaces line {start + 1}" : $"Replaces lines {start + 1}–{start + count}";
        }
        else
        {
            if (add.Any(MaskedLine)) throw new YamlEditRefused($"Part of this edit was hidden for safety (a password or key). Joule won't guess it: add that line to {file} yourself, using a !secret reference.");
            var target = Locate(originalYaml, lines, location, add, file);
            add = Reindent(add, target.ChildIndent);
            start = target.InsertAt; count = 0; owner = target.Path;
            var duplicate = BaseKeys(add).FirstOrDefault(k => target.Children.Contains(k));
            if (duplicate is not null)
                throw new YamlEditRefused($"{file} already has {duplicate}{(target.Path.Length > 0 ? $" under {Display(target.Path)}" : "")}. Adding it again would make a duplicate key; the suggestion needs to replace the existing lines instead.");
            placement = target.Description;
        }

        var result = lines.Replace(start, count, add);
        if (result == original) throw new YamlEditRefused($"{file} already reads exactly like this, so there is nothing to change.");
        var editedYaml = Parse(result, file, edited: true);
        var keys = CheckScope(originalYaml, editedYaml, owner, add.Concat(old).ToList(), file);
        return new(result, keys, start + 1, count, add.Count, placement);
    }

    // ------------------------------------------------------------------ lines

    /// <summary>The file as lines, each keeping its own line ending, so untouched lines are written back exactly.</summary>
    sealed class FileLines
    {
        public readonly List<string> Raw = [];
        public readonly List<string> Content = [];
        readonly string newline;
        readonly bool finalNewline;
        public FileLines(string text)
        {
            var parts = text.Split('\n');
            finalNewline = text.EndsWith('\n');
            var count = finalNewline ? parts.Length - 1 : parts.Length;
            if (text.Length == 0) count = 0;
            for (var i = 0; i < count; i++)
            {
                Raw.Add(parts[i] + (i < parts.Length - 1 ? "\n" : ""));
                Content.Add(parts[i].TrimEnd('\r'));
            }
            var crlf = Regex.Count(text, "\r\n", RegexOptions.None, Budget);
            newline = crlf > 0 && crlf * 2 >= text.Count(c => c == '\n') ? "\r\n" : "\n";
        }
        public string Replace(int start, int count, List<string> insert)
        {
            var prefix = Raw.Take(start).ToList();
            // Adding after the last line of a file without a final newline: end that line first.
            if (start == Raw.Count && prefix.Count > 0 && !prefix[^1].EndsWith('\n')) prefix[^1] += newline;
            var middle = insert.Select(l => l + newline).ToList();
            var suffix = Raw.Skip(start + count).ToList();
            // The file didn't end with a newline and the new lines are now its end: keep it that way.
            if (suffix.Count == 0 && !finalNewline && middle.Count > 0 && count > 0) middle[^1] = insert[^1];
            return string.Concat(prefix.Concat(middle).Concat(suffix));
        }
    }

    static List<string> Clean(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    static int Indent(string line) => line.Length - line.TrimStart(' ').Length;
    static int MinIndent(IEnumerable<string> lines) => lines.Where(l => l.Trim().Length > 0).Select(Indent).DefaultIfEmpty(0).Min();

    /// <summary>Moves a block so its least-indented line starts at <paramref name="indent"/>, keeping the relative layout.</summary>
    static List<string> Reindent(List<string> block, int indent)
    {
        var shift = indent - MinIndent(block);
        if (shift == 0) return block;
        return block.Select(l => l.Trim().Length == 0 ? "" : shift > 0 ? new string(' ', shift) + l : l[Math.Min(-shift, Indent(l))..]).ToList();
    }

    static bool MaskedLine(string line) => KeyLine.Match(line) is { Success: true } m && m.Groups["rest"].Value.Trim().Length > 0 && MaskedValue.IsMatch(m.Groups["rest"].Value);
    static string? KeyOf(string line) => KeyLine.Match(line) is { Success: true } m ? m.Groups["key"].Value.Trim('"', '\'') : null;

    /// <summary>The keys on a block's least-indented lines (not list items).</summary>
    static List<string> BaseKeys(List<string> block)
    {
        var indent = MinIndent(block);
        return block.Where(l => l.Trim().Length > 0 && Indent(l) == indent && !l.TrimStart().StartsWith('#') && !l.TrimStart().StartsWith('-'))
            .Select(KeyOf).OfType<string>().Distinct().ToList();
    }
    static bool BaseIsList(List<string> block)
    {
        var indent = MinIndent(block);
        return block.Any(l => Indent(l) == indent && (l.TrimStart().StartsWith("- ", StringComparison.Ordinal) || l.Trim() == "-"));
    }

    // ------------------------------------------------------------------ replacing existing lines

    static int FindBefore(FileLines lines, List<string> old, string file)
    {
        bool Same(string fileLine, string wanted, int shift)
        {
            var expected = shift == 0 || wanted.Trim().Length == 0 ? wanted : new string(' ', shift) + wanted;
            if (fileLine.TrimEnd() == expected) return true;
            // A value hidden for safety in the suggestion matches the file's line for the same key.
            return MaskedLine(expected) && Indent(fileLine) == Indent(expected) && KeyOf(fileLine) is { } key && key == KeyOf(expected);
        }
        List<int> Matches(int shift)
        {
            var found = new List<int>();
            for (var i = 0; i + old.Count <= lines.Content.Count; i++)
                if (old.Select((w, j) => Same(lines.Content[i + j], w, shift)).All(x => x)) found.Add(i);
            return found;
        }
        var exact = Matches(0);
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) throw new YamlEditRefused($"The lines this edit replaces appear {exact.Count} times in {file}, so Joule can't tell which ones to change.");
        // The suggestion may quote the lines without their leading indentation: accept one unambiguous match at a constant offset.
        if (MinIndent(old) == 0)
        {
            var shifted = Enumerable.Range(1, 16).Select(s => Matches(s)).Where(x => x.Count > 0).ToList();
            if (shifted.Count == 1 && shifted[0].Count == 1) return shifted[0][0];
            if (shifted.Count > 0) throw new YamlEditRefused($"The lines this edit replaces appear more than once in {file}, so Joule can't tell which ones to change.");
        }
        throw new YamlEditRefused($"The lines this edit replaces aren't in {file} exactly as the suggestion quotes them, so Joule can't place it safely. The file may have changed since the check.");
    }

    /// <summary>Fills each hidden value in the new lines from the replaced line with the same key, so a credential is kept as it is.</summary>
    static List<string> FillMasked(List<string> add, List<string> region, string file) => add.Select(line =>
    {
        if (!MaskedLine(line)) return line;
        var key = KeyOf(line);
        var source = region.FirstOrDefault(r => KeyOf(r) == key && !MaskedLine(r));
        if (source is null) throw new YamlEditRefused($"Part of this edit was hidden for safety (the value of {key}). Joule won't guess it: make that change in {file} yourself.");
        var m = KeyLine.Match(line); var s = KeyLine.Match(source);
        return line[..m.Groups["rest"].Index] + s.Groups["rest"].Value;
    }).ToList();

    // ------------------------------------------------------------------ adding new lines

    sealed record Target(string Path, int InsertAt, int ChildIndent, HashSet<string> Children, string Description);

    static Target Locate(YamlStream yaml, FileLines lines, string location, List<string> add, string file)
    {
        var root = yaml.Documents.FirstOrDefault()?.RootNode as YamlMappingNode;
        var anchorMatch = Anchor.Match(location);
        var anchorKey = anchorMatch.Success ? anchorMatch.Groups["key"].Value : null;
        var insertBefore = anchorMatch.Success && anchorMatch.Groups["dir"].Value.ToLowerInvariant() is "before" or "above";
        var text = anchorMatch.Success ? location.Remove(anchorMatch.Index, anchorMatch.Length) : location;
        var candidates = PathToken.Matches(text).Select(m => Regex.Replace(m.Value, @"\s*(?:›|>|/|→)\s*", ".", RegexOptions.None, Budget).Trim())
            .Select(p => string.Join('.', p.Split('.').Where(s => !StopWords.Contains(s)))).Where(p => p.Length > 0).Distinct().ToList();
        var snippetKeys = BaseKeys(add);
        (YamlNode Node, string Path, YamlNode? Key)? parent = null;
        foreach (var candidate in candidates)
        {
            if (Resolve(root, candidate) is { } found) { parent = found; break; }
            // "pred_bat.export_today" naming the key being added: its parent is the section.
            var cut = candidate.LastIndexOf('.');
            if (cut > 0 && snippetKeys.Contains(candidate[(cut + 1)..]) && Resolve(root, candidate[..cut]) is { } up) { parent = up; break; }
        }
        if (parent is null && candidates.Count == 0 && anchorKey is not null && root is not null)
        {
            // Only "after import_today": the section is whichever one holds that key, when exactly one does.
            var holders = new List<(YamlNode, string, YamlNode?)>();
            void Find(YamlMappingNode map, string path, YamlNode? key)
            {
                if (map.Children.Keys.Any(k => KeyText(k) == anchorKey)) holders.Add((map, path, key));
                foreach (var (k, v) in map.Children) if (v is YamlMappingNode child) Find(child, Join(path, KeyText(k)), k);
            }
            Find(root, "", null);
            if (holders.Count == 1) parent = holders[0];
        }
        if (parent is null)
        {
            var topLevel = Regex.IsMatch(location, @"\b(top[ -]level|root|end of (the )?file)\b", RegexOptions.IgnoreCase, Budget);
            if (root is not null && (topLevel || candidates.Count == 0 && anchorKey is null)) parent = (root, "", null);
            else throw new YamlEditRefused(candidates.Count > 0
                ? $"{file} has no {candidates[0]} section, so Joule can't tell where this edit goes."
                : anchorKey is not null ? $"Joule couldn't find {anchorKey} in {file}, where this edit says it goes." : $"Joule couldn't find where this edit goes in {file}.");
        }
        var (node, path, keyNode) = parent.Value;
        var parentIndent = keyNode is null ? -1 : (int)keyNode.Start.Column - 1;
        var parentLine = keyNode is null ? -1 : (int)keyNode.Start.Line - 1;
        int childIndent; HashSet<string> children = new(StringComparer.Ordinal); int insertAt;
        switch (node)
        {
            case YamlMappingNode map when map.Children.Count > 0:
                childIndent = (int)map.Children.First().Key.Start.Column - 1;
                foreach (var k in map.Children.Keys) children.Add(KeyText(k));
                insertAt = BlockEnd(lines, parentLine, parentIndent, false) + 1;
                if (anchorKey is not null)
                {
                    var entry = map.Children.FirstOrDefault(c => KeyText(c.Key) == anchorKey);
                    if (entry.Key is null) throw new YamlEditRefused($"Joule couldn't find {anchorKey}{(path.Length > 0 ? $" under {Display(path)}" : "")} in {file}, where this edit says it goes.");
                    var entryLine = (int)entry.Key.Start.Line - 1;
                    var entryIndent = (int)entry.Key.Start.Column - 1;
                    insertAt = insertBefore ? LeadingComments(lines, entryLine, entryIndent) : BlockEnd(lines, entryLine, entryIndent, CompactList(lines, entry.Value, entryIndent)) + 1;
                }
                break;
            case YamlSequenceNode seq when seq.Children.Count > 0:
                childIndent = LineIndent(lines, seq.Children[0].Start.Line - 1);
                insertAt = BlockEnd(lines, parentLine, parentIndent, CompactList(lines, seq, parentIndent)) + 1;
                if (anchorKey is not null) throw new YamlEditRefused($"{Display(path)} in {file} is a list, so Joule can't place the edit after {anchorKey}.");
                break;
            case YamlScalarNode { Value: "" } when keyNode is not null:
                // An empty section ("pred_bat:" with nothing under it yet).
                childIndent = parentIndent + 2; insertAt = parentLine + 1;
                if (anchorKey is not null) throw new YamlEditRefused($"Joule couldn't find {anchorKey} under {Display(path)} in {file}, where this edit says it goes.");
                break;
            default:
                throw new YamlEditRefused(path.Length == 0 ? $"{file} has no sections to add to." : $"{Display(path)} in {file} holds a single value, not a section, so Joule can't add lines under it.");
        }
        var where = path.Length == 0 ? "at the top level" : $"under {Display(path)}";
        var what = $"Adds {add.Count} line{(add.Count == 1 ? "" : "s")} {where}";
        return new(path, insertAt, childIndent, children, anchorKey is null ? what : $"{what}, {(insertBefore ? "before" : "after")} {anchorKey}");
    }

    static (YamlNode Node, string Path, YamlNode? Key)? Resolve(YamlMappingNode? root, string dotted)
    {
        if (root is null) return null;
        YamlNode current = root; YamlNode? key = null; var path = new List<string>();
        foreach (var segment in dotted.Split('.'))
        {
            if (current is not YamlMappingNode map) return null;
            var match = map.Children.Where(c => KeyText(c.Key) == segment).ToList();
            if (match.Count == 0)
            {
                // Case and punctuation only when unambiguous ("Predbat" for pred_bat).
                static string Loose(string x) => new(x.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
                match = map.Children.Where(c => Loose(KeyText(c.Key)) == Loose(segment)).ToList();
                if (match.Count != 1) return null;
            }
            key = match[0].Key; current = match[0].Value; path.Add(KeyText(key));
        }
        return (current, string.Join('.', path), key);
    }

    static int LineIndent(FileLines lines, long index) => index >= 0 && index < lines.Content.Count ? Indent(lines.Content[(int)index]) : 0;

    /// <summary>A list written level with its key ("key:" then "- item" at the same indent), which YAML allows.</summary>
    static bool CompactList(FileLines lines, YamlNode value, int keyIndent) =>
        value is YamlSequenceNode { Children.Count: > 0 } seq && LineIndent(lines, seq.Children[0].Start.Line - 1) == keyIndent;

    /// <summary>The last line of the block under the key on <paramref name="ownerLine"/>: its last deeper line (content or comment),
    /// stopping at the next content line at or above the key's level. Comments at the key's level don't end the block.</summary>
    static int BlockEnd(FileLines lines, int ownerLine, int ownerIndent, bool compactList)
    {
        var last = ownerLine;
        for (var i = ownerLine + 1; i < lines.Content.Count; i++)
        {
            var l = lines.Content[i]; var t = l.TrimStart();
            if (t.Length == 0) continue;
            var indent = Indent(l);
            if (t.StartsWith('#')) { if (indent > ownerIndent) last = i; continue; }
            if (t is "---" or "..." && indent == 0) break;
            if (indent > ownerIndent || (compactList && indent == ownerIndent && (t.StartsWith("- ", StringComparison.Ordinal) || t == "-"))) { last = i; continue; }
            break;
        }
        return last;
    }

    /// <summary>Where to insert "before" a key: above any comment lines that introduce it.</summary>
    static int LeadingComments(FileLines lines, int keyLine, int indent)
    {
        var at = keyLine;
        while (at > 0 && lines.Content[at - 1].TrimStart().StartsWith('#') && Indent(lines.Content[at - 1]) == indent) at--;
        return at;
    }

    /// <summary>The path of the section that holds lines starting at <paramref name="line"/> (0-based) with the given indent.</summary>
    static string Owner(YamlStream yaml, FileLines lines, int line, int indent)
    {
        var best = "";
        void Walk(YamlNode node, string path)
        {
            switch (node)
            {
                case YamlMappingNode map:
                    foreach (var (k, v) in map.Children)
                        if (k.Start.Line - 1 < line && k.Start.Column - 1 < indent && Covers(lines, v, line)) { best = Join(path, KeyText(k)); Walk(v, best); }
                    break;
                case YamlSequenceNode seq:
                    for (var i = 0; i < seq.Children.Count; i++)
                    {
                        var item = seq.Children[i];
                        if (item.Start.Line - 1 < line && item.Start.Column - 1 < indent && Covers(lines, item, line)) { best = $"{path}[{i}]"; Walk(item, best); }
                    }
                    break;
            }
        }
        if (yaml.Documents.FirstOrDefault()?.RootNode is { } root) Walk(root, "");
        return best;
    }

    /// <summary>The node spans the line (0-based). A collection's own end mark is its start, so its extent is its last child's.</summary>
    static bool Covers(FileLines lines, YamlNode node, int line) => node.Start.Line - 1 <= line && LastLine(node) >= line;
    static long LastLine(YamlNode node) => node switch
    {
        YamlMappingNode map => map.Children.Select(c => Math.Max(LastLine(c.Key), LastLine(c.Value))).DefaultIfEmpty(map.Start.Line - 1).Max(),
        YamlSequenceNode seq => seq.Children.Select(LastLine).DefaultIfEmpty(seq.Start.Line - 1).Max(),
        // A block scalar ends at the start of the line after it.
        _ => node.End.Column == 1 && node.End.Line > node.Start.Line ? node.End.Line - 2 : node.End.Line - 1,
    };

    // ------------------------------------------------------------------ checking the result

    static YamlStream Parse(string text, string file, bool edited)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            foreach (var document in stream.Documents) Flatten(document.RootNode, "", new Dictionary<string, string>(StringComparer.Ordinal), file, edited);
            return stream;
        }
        catch (YamlException e) when (Regex.Match(e.Message, @"Duplicate key (?<key>.{1,120})$", RegexOptions.None, Budget) is { Success: true } duplicate)
        {
            var key = duplicate.Groups["key"].Value.Trim();
            throw new YamlEditRefused(edited ? $"With this edit {file} would have {key} twice. Nothing was changed." : $"{file} has {key} twice, so Joule won't edit it. Fix it by hand first.");
        }
        catch (Exception e) when (e is YamlException or InvalidOperationException or ArgumentException)
        {
            var where = e is YamlException { Start.Line: > 0 } y ? $" (line {y.Start.Line})" : "";
            var reason = e is YamlException ye ? Reason(ye) : "the structure doesn't parse";
            throw new YamlEditRefused(edited
                ? $"With this edit {file} wouldn't be valid YAML{where}: {reason}. Nothing was changed."
                : $"{file} isn't valid YAML as it stands{where}, so Joule won't edit it. Fix it by hand first.");
        }
    }
    static string Reason(YamlException e)
    {
        var message = (e.InnerException?.Message ?? e.Message).Trim();
        message = Regex.Replace(message, @"^\(Line: \d+, Col: \d+, Idx: \d+\) - \(Line: \d+, Col: \d+, Idx: \d+\):\s*", "", RegexOptions.None, Budget).Trim().TrimEnd('.');
        if (message.Length == 0) return "the structure doesn't parse";
        message = char.ToLowerInvariant(message[0]) + message[1..];
        return message.Length > 160 ? message[..160] + "…" : message;
    }

    static string KeyText(YamlNode key) => key is YamlScalarNode s ? s.Value ?? "" : key.ToString();
    static string Join(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";
    static string Display(string path) => ConfigFileMask.DisplayName(Regex.Replace(path, @"\[\d+\]", ".[]", RegexOptions.None, Budget));

    /// <summary>Every value in the file by path ("pred_bat.import_today[0]"), with its tag and whether it was quoted.</summary>
    static Dictionary<string, string> Flatten(YamlNode node, string path, Dictionary<string, string> into, string file, bool edited)
    {
        switch (node)
        {
            case YamlMappingNode map:
                if (map.Children.Count == 0) into[path] = "{}";
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (k, v) in map.Children)
                {
                    var key = KeyText(k);
                    if (!seen.Add(key)) throw new YamlEditRefused(edited ? $"With this edit {file} would have {key} twice. Nothing was changed." : $"{file} has {key} twice, so Joule won't edit it. Fix it by hand first.");
                    Flatten(v, Join(path, key), into, file, edited);
                }
                break;
            case YamlSequenceNode seq:
                if (seq.Children.Count == 0) into[path] = "[]";
                for (var i = 0; i < seq.Children.Count; i++) Flatten(seq.Children[i], $"{path}[{i}]", into, file, edited);
                break;
            case YamlScalarNode scalar:
                into[path] = $"{(scalar.Tag.IsEmpty ? "" : scalar.Tag.Value)}|{(scalar.Style is ScalarStyle.Plain or ScalarStyle.Any ? "" : "q")}|{scalar.Value}";
                break;
        }
        return into;
    }

    static bool Under(string path, string key) => path == key || path.StartsWith(key + ".", StringComparison.Ordinal) || path.StartsWith(key + "[", StringComparison.Ordinal);

    /// <summary>Refuses an edit that changes anything outside the keys it names; returns those keys' changes for the review.</summary>
    static List<ConfigKeyChange> CheckScope(YamlStream before, YamlStream after, string owner, List<string> edited, string file)
    {
        Dictionary<string, string> All(YamlStream s)
        {
            var all = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var d = 0; d < s.Documents.Count; d++) Flatten(s.Documents[d].RootNode, d == 0 ? "" : $"#{d}", all, file, true);
            return all;
        }
        var a = All(before); var b = All(after);
        var changed = a.Keys.Union(b.Keys).Where(k => !a.TryGetValue(k, out var x) || !b.TryGetValue(k, out var y) || x != y).ToList();
        if (changed.Count == 0) throw new YamlEditRefused($"This edit wouldn't change any setting in {file}, so there is nothing to apply.");
        var prefix = owner.Length == 0 ? "" : owner + ".";
        var named = BaseKeys(edited).Select(k => prefix + k).ToList();
        var list = BaseIsList(edited) ? owner + "[" : null;
        string? Group(string path)
        {
            foreach (var n in named) if (Under(path, n)) return n;
            if (list is not null && path.StartsWith(list, StringComparison.Ordinal)) return owner;
            // An empty section gaining its first key ("pred_bat:" becoming a mapping).
            if (owner.Length > 0 && path == owner && (a.GetValueOrDefault(path) is null or "||" or "{}" or "[]")) return owner;
            return null;
        }
        var outside = changed.Where(p => Group(p) is null).ToList();
        if (outside.Count > 0)
            throw new YamlEditRefused($"This edit would also change {Display(outside[0])}{(outside.Count > 1 ? $" and {outside.Count - 1} other setting{(outside.Count == 2 ? "" : "s")}" : "")}, which it doesn't mention (usually a wrong indent). Nothing was changed.");
        return changed.GroupBy(p => Group(p)!).Select(g =>
        {
            var existed = a.Keys.Any(k => Under(k, g.Key));
            var remains = b.Keys.Any(k => Under(k, g.Key));
            return new ConfigKeyChange(g.Key, Display(g.Key), !existed ? "added" : !remains ? "removed" : "changed");
        }).OrderBy(c => c.Key, StringComparer.Ordinal).ToList();
    }
}
