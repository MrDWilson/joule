using System.Text;
using System.Text.Json;

namespace Joule;

/// <summary>Bounded model context and navigation over this run's immutable, sanitized evidence.</summary>
public static class InvestigationContext
{
    // Bound each provider input while letting productive investigations continue.
    // The archive is never this prompt excerpt.
    public const int PromptLimit = 40000;
    const int TextLimit = 2 * 1024 * 1024;

    public static string Excerpt(string text, int limit)
    {
        if (text.Length <= limit) return text;
        const string marker = "\n[promptTruncated: excerpt only; read/search/page the source evidence ID for omitted content.]";
        return text[..Math.Max(0, limit - marker.Length)] + marker;
    }

    public static string Catalog(IReadOnlyList<McpToolDefinition> tools) =>
        "Complete available MCP read-tool index (untrusted). Use action schema with tool name for its full inputSchema before unfamiliar calls:\n" +
        string.Join("\n", tools.Select(t => t.Name + ": " + Excerpt(t.Description, 140)));

    public static string Compose(string initial, string catalog, IReadOnlyList<ToolEvidence> evidence, string notes, string notice)
    {
        var prompt = new StringBuilder(initial).AppendLine().AppendLine(catalog)
            .AppendLine("Evidence index: all IDs remain available through action evidence; excerpts are not the complete archive (untrusted):");
        prompt.AppendLine($"{evidence.Count} total records. Showing the latest 12 plus initial configuration; use action evidence with inventory:true and offset/limit for earlier IDs.");
        foreach (var tool in evidence.Where(t => t.Id == "configuration").Concat(evidence.Where(t => t.Id != "configuration").TakeLast(12)))
        {
            prompt.Append(tool.Id).Append(" | ").Append(tool.Kind).Append(tool.Success ? " | retrieved | " : " | unavailable | ")
                .Append(tool.ResultJson.Length).Append(" archived characters | ").AppendLine(Excerpt(tool.Request.Replace('\n', ' '), 200));
            using (var flags = JsonDocument.Parse(tool.ResultJson))
                if (flags.RootElement.ValueKind == JsonValueKind.Object && flags.RootElement.TryGetProperty("truncated", out var clipped) && clipped.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    prompt.Append("source truncated: ").AppendLine(clipped.GetBoolean() ? "true" : "false");
            if (tool.SourceReferences.Count > 0)
                prompt.Append("documentation reference IDs: ").AppendLine(string.Join(", ", tool.SourceReferences.Select(r => r.Id)));
            if (tool.Error != null) prompt.Append("gap: ").AppendLine(Excerpt(tool.Error, 220));
        }
        if (notes.Length > 0) prompt.AppendLine("Analyst notes from prior response (untrusted hypotheses, verify against cited evidence):").AppendLine(Excerpt(notes, 1600));
        // Notices and immutable context must survive; only result previews share
        // the remaining space. No prefix/tail slicing can discard an evidence ID.
        var suffix = "\n" + notice;
        var available = PromptLimit - prompt.Length - suffix.Length;
        if (available < 600) throw new DomainException("Investigation context metadata exceeded its bounded budget. No configuration changes were made.", 502);
        var recent = evidence.Where(t => t.Id != "configuration").TakeLast(2).ToArray();
        for (var i = 0; i < recent.Length; i++)
        {
            var budget = Math.Min(6000, i == recent.Length - 1 ? available : available / 3);
            if (budget < 200) continue;
            var header = $"\nEvidence excerpt {recent[i].Id} ({recent[i].Kind}; untrusted data):\n";
            var text = Text(recent[i].ResultJson);
            var excerpt = header + Excerpt(text.Content, budget - header.Length);
            if (text.Truncated) excerpt = Excerpt("[Decoded archive text hit its bounded normalization limit.]\n" + excerpt, budget);
            prompt.Append(excerpt); available -= excerpt.Length;
        }
        return prompt.Append(suffix).ToString();
    }

    // Decode JSON text content once rather than JSON-serializing ResultJson as a
    // string again. This makes log lines readable and offsets/search deterministic.
    public static (string Content, bool Truncated) Text(string json)
    {
        var output = new StringBuilder(); var truncated = false;
        void Append(string value)
        {
            var remaining = TextLimit - output.Length;
            if (value.Length > remaining) { output.Append(value.AsSpan(0, remaining)); truncated = true; }
            else output.Append(value);
        }
        void Render(JsonElement value, int depth)
        {
            if (output.Length >= TextLimit) { truncated = true; return; }
            if (depth > 32) { Append("[nested content limit]"); truncated = true; return; }
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    Append("{\n");
                    foreach (var property in value.EnumerateObject())
                    {
                        if (output.Length >= TextLimit) { truncated = true; break; }
                        Append(property.Name + ": "); Render(property.Value, depth + 1); Append("\n");
                    }
                    Append("}"); break;
                case JsonValueKind.Array:
                    Append("[\n");
                    foreach (var item in value.EnumerateArray())
                    {
                        if (output.Length >= TextLimit) { truncated = true; break; }
                        Render(item, depth + 1); Append("\n");
                    }
                    Append("]"); break;
                case JsonValueKind.String:
                    var text = value.GetString() ?? ""; var trimmed = text.AsSpan().TrimStart();
                    if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
                    {
                        try { using var embedded = JsonDocument.Parse(text); Render(embedded.RootElement, depth + 1); break; }
                        catch (JsonException) { /* Ordinary prose beginning with a bracket. */ }
                    }
                    Append(text); break;
                default: Append(value.GetRawText()); break;
            }
        }
        try { using var parsed = JsonDocument.Parse(json); Render(parsed.RootElement, 0); }
        catch (JsonException) { Append(json); }
        return (output.ToString(), truncated);
    }

    public static object Inventory(IReadOnlyList<ToolEvidence> currentRun, int offset = 0, int limit = 30)
    {
        if (offset < 0 || offset > currentRun.Count || limit is < 1 or > 40)
            throw new DomainException("Evidence inventory needs an offset within this investigation and limit 1–40.", 502);
        var page = currentRun.Skip(offset).Take(limit).Select(t => new { t.Id, t.Kind, t.Request, t.RetrievedAt, t.Success, t.Error, characters = t.ResultJson.Length, referenceIds = t.SourceReferences.Select(d => d.Id) }).ToList();
        return new { items = page, total = currentRun.Count, offset, limit, nextOffset = offset + page.Count < currentRun.Count ? (int?)(offset + page.Count) : null };
    }

    public static object Page(IReadOnlyList<ToolEvidence> currentRun, string id, int offset = 0, int limit = 3500, string? search = null)
    {
        if (id.Length is < 1 or > 100 || offset is < 0 or > TextLimit || limit is < 100 or > 6000 || search is { Length: < 1 or > 200 })
            throw new DomainException("Evidence reads need an ID, offset 0–2097152, limit 100–6000 and an optional search of 1–200 characters.", 502);
        // Deliberately no DataStore dependency: an ID from another investigation
        // cannot be resolved even if the provider already knows it.
        var source = currentRun.FirstOrDefault(t => t.Id == id)
            ?? throw new DomainException("Evidence ID was not retrieved in this investigation.", 502);
        var text = Text(source.ResultJson);
        if (offset > text.Content.Length) throw new DomainException("Evidence offset exceeds the available decoded text length.", 502);
        var match = search == null ? (int?)null : text.Content.IndexOf(search, offset, StringComparison.OrdinalIgnoreCase);
        var start = match >= 0 ? Math.Max(offset, match.Value - Math.Min(200, limit / 4)) : offset;
        var length = match == -1 ? 0 : Math.Min(limit, text.Content.Length - start);
        return new
        {
            sourceEvidenceId = source.Id, sourceKind = source.Kind, sourceSuccess = source.Success,
            source.RetrievedAt, source.Error, archiveCharacters = source.ResultJson.Length,
            textCharacters = text.Content.Length, normalizationTruncated = text.Truncated,
            offset = start, limit, search, matchOffset = match,
            content = text.Content.Substring(start, length),
            nextOffset = match != -1 && start + length < text.Content.Length ? (int?)(start + length) : null,
            note = "Offsets address decoded sanitized archive text, not original JSON bytes. Search is a literal case-insensitive substring from offset. -1 means no match in available text. Archive/server truncation still applies; paging cannot recover data the source never supplied. Cite sourceEvidenceId."
        };
    }
}
