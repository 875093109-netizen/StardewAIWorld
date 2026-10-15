using System.Text.Json;

namespace AIWorld.Core.Pipeline;

/// <summary>
/// P2-03 Raw text → structured payload extraction + JSON parse.
/// Handles: LLM wrapping in markdown code fences, explanatory prose before/after JSON,
/// empty/truncated input. Does NOT bind to DTO (schema validator does that).
/// </summary>
public static class StructuredResponseParser
{
    /// <summary>Max accepted raw payload length (oversize guard).</summary>
    public const int MaxRawPayloadLength = 16_384;

    public sealed class ParseOutcome
    {
        public required bool Ok { get; init; }
        public JsonElement? Payload { get; init; }
        public string? Error { get; init; }
        public bool Truncated { get; init; }
    }

    public static ParseOutcome Parse(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return new ParseOutcome { Ok = false, Error = "empty response" };

        if (rawText.Length > MaxRawPayloadLength)
            return new ParseOutcome { Ok = false, Error = $"response oversized (> {MaxRawPayloadLength} chars)", Truncated = true };

        string candidate = ExtractJson(rawText);

        try
        {
            using var doc = JsonDocument.Parse(candidate, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            var root = doc.RootElement.Clone();
            return new ParseOutcome { Ok = true, Payload = root };
        }
        catch (JsonException ex)
        {
            // OVERSIZED is reserved for length overflow only. A short unbalanced
            // JSON text is a plain parse error (likely truncated by the model, but
            // we cannot distinguish reliably — do not mislabel it as OVERSIZED).
            bool truncated = candidate.Length >= MaxRawPayloadLength;
            return new ParseOutcome { Ok = false, Error = $"JSON parse failed: {ex.Message}", Truncated = truncated };
        }
    }

    /// <summary>
    /// Extract the JSON object from raw LLM text: strips markdown code fences
    /// (```json ... ``` or ``` ... ```) and leading/trailing prose.
    /// Strategy: prefer the first balanced {...} region; if a fence is present, use fence content.
    /// </summary>
    private static string ExtractJson(string raw)
    {
        // 1) If wrapped in a code fence, take the fenced block.
        var fence = ExtractFencedBlock(raw);
        if (fence != null)
            return TrimToJson(fence);

        // 2) Otherwise find the first '{' and try to match the balanced object,
        //    trimming prose before/after.
        return TrimToJson(raw);
    }

    private static string? ExtractFencedBlock(string raw)
    {
        int fenceStart = raw.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart < 0)
            return null;

        int contentStart = fenceStart + 3;
        // Skip optional language tag on the same line.
        int newline = raw.IndexOf('\n', contentStart);
        if (newline >= 0)
            contentStart = newline + 1;

        int fenceEnd = raw.IndexOf("```", contentStart, StringComparison.Ordinal);
        if (fenceEnd < 0)
            return raw.Substring(contentStart); // unclosed fence: take remainder (likely truncated)

        return raw.Substring(contentStart, fenceEnd - contentStart);
    }

    /// <summary>Trim to the outermost balanced JSON object region, or return trimmed input.</summary>
    private static string TrimToJson(string s)
    {
        int firstBrace = s.IndexOf('{');
        if (firstBrace < 0)
            return s;

        int depth = 0;
        bool inString = false;
        bool escaped = false;
        int last = -1;
        for (int i = firstBrace; i < s.Length; i++)
        {
            char c = s[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) { last = i; break; }
            }
        }
        if (last >= 0)
            return s.Substring(firstBrace, last - firstBrace + 1);
        return s.Substring(firstBrace); // unbalanced: take remainder (likely truncated)
    }
}
