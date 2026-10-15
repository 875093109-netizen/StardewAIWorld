using System.Text.Json;

namespace AIWorld.Core.Pipeline;

/// <summary>
/// P2-02 Strict JSON schema validation for NpcStructuredResponse.
/// Enforces: required fields, allowed enums, nullability, types, unknown-field policy,
/// size/length constraints. Runs on the raw parsed JsonElement BEFORE binding to DTO.
/// </summary>
public static class StructuredResponseSchemaValidator
{
    /// <summary>Allowed intent type values (Demo Phase 2 registry, contracts §2.1).
    /// travel.to_location was MISSING here while the semantic validator (CreateDemoDefault /
    /// CreateLiveForLocations) already registers it — an LLM proposing a travel intent was
    /// rejected at the SCHEMA stage before semantic validation ever ran (Gate M1 defect).</summary>
    public static readonly HashSet<string> AllowedIntentTypes = new(StringComparer.Ordinal)
    {
        "social.follow_player",
        "social.stop_following",
        "magic.observe",
        "magic.investigate",
        "magic.learn",
        "travel.to_location",
        "environment.observe",
        "INTENT_NONE",
    };

    /// <summary>Allowed emotional tone values (contracts §2.5).</summary>
    public static readonly HashSet<string> AllowedEmotionalTones = new(StringComparer.Ordinal)
    {
        "positive", "neutral", "negative", "ambivalent",
    };

    private const int MaxSpeechLength = 2000;
    private const int MaxSummaryLength = 200;
    private const int MaxReasonLength = 500;
    private const int MaxMemoryCandidates = 10;
    private const int MaxStateDeltas = 10;
    private const int MaxParamsSize = 50;

    /// <summary>Top-level allowed fields. Unknown fields → SCHEMA_ERROR (strict policy).</summary>
    private static readonly HashSet<string> TopLevelFields = new(StringComparer.Ordinal)
    {
        "speech", "intent", "memoryCandidates", "stateDelta", "decision", "schemaVersion",
    };

    private static readonly HashSet<string> DecisionFields = new(StringComparer.Ordinal)
    {
        "requestId", "npcId", "termsHash", "outcome", "proposedTerms", "flavorDialogue", "schemaVersion",
    };

    private static readonly HashSet<string> AllowedDecisionOutcomes = new(StringComparer.Ordinal)
    {
        "ACCEPT", "DECLINE", "UNCLEAR", "COUNTERPROPOSE",
    };

    private static readonly HashSet<string> IntentFields = new(StringComparer.Ordinal)
    {
        "intentId", "type", "target", "priority", "schemaVersion", "reason", "expiresAt", "params",
    };

    private static readonly HashSet<string> MemoryFields = new(StringComparer.Ordinal)
    {
        "npcId", "sourceObservationId", "summary", "emotionalTone", "importance", "schemaVersion",
        "relatedFactIds", "tags",
    };

    private static readonly HashSet<string> StateDeltaFields = new(StringComparer.Ordinal)
    {
        "npcId", "path", "value", "reason", "schemaVersion", "confidence",
    };

    public static List<string> Validate(JsonElement root)
    {
        var errors = new List<string>();

        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add("root must be a JSON object");
            return errors;
        }

        // Unknown top-level field policy (strict).
        foreach (var prop in root.EnumerateObject())
            if (!TopLevelFields.Contains(prop.Name))
                errors.Add($"unknown top-level field '{prop.Name}'");

        // schemaVersion required.
        if (!root.TryGetProperty("schemaVersion", out var sv) || sv.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(sv.GetString()))
            errors.Add("missing or invalid 'schemaVersion'");

        // speech required, non-empty, bounded.
        if (!root.TryGetProperty("speech", out var speech) || speech.ValueKind != JsonValueKind.String)
            errors.Add("missing or non-string 'speech'");
        else if (string.IsNullOrWhiteSpace(speech.GetString()))
            errors.Add("'speech' must be non-empty");
        else if (speech.GetString()!.Length > MaxSpeechLength)
            errors.Add($"'speech' exceeds {MaxSpeechLength} chars");

        // intent optional; if present must be object or null.
        if (root.TryGetProperty("intent", out var intent))
        {
            if (intent.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                errors.Add("'intent' must be an object or null");
            else if (intent.ValueKind == JsonValueKind.Object)
                ValidateIntent(intent, errors);
        }

        // memoryCandidates optional array.
        if (root.TryGetProperty("memoryCandidates", out var mems))
        {
            if (mems.ValueKind != JsonValueKind.Array)
                errors.Add("'memoryCandidates' must be an array");
            else if (mems.GetArrayLength() > MaxMemoryCandidates)
                errors.Add($"'memoryCandidates' exceeds {MaxMemoryCandidates} items");
            else
                foreach (var m in mems.EnumerateArray())
                    ValidateMemoryCandidate(m, errors);
        }

        // stateDelta optional array.
        if (root.TryGetProperty("stateDelta", out var deltas))
        {
            if (deltas.ValueKind != JsonValueKind.Array)
                errors.Add("'stateDelta' must be an array");
            else if (deltas.GetArrayLength() > MaxStateDeltas)
                errors.Add($"'stateDelta' exceeds {MaxStateDeltas} items");
            else
                foreach (var d in deltas.EnumerateArray())
                    ValidateStateDelta(d, errors);
        }

        // decision optional; if present must be object or null.
        if (root.TryGetProperty("decision", out var decision))
        {
            if (decision.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                errors.Add("'decision' must be an object or null");
            else if (decision.ValueKind == JsonValueKind.Object)
                ValidateDecision(decision, errors);
        }

        return errors;
    }

    private static void ValidateDecision(JsonElement d, List<string> errors)
    {
        foreach (var prop in d.EnumerateObject())
            if (!DecisionFields.Contains(prop.Name))
                errors.Add($"unknown 'decision' field '{prop.Name}'");

        RequireString(d, "requestId", errors, prefix: "decision.");
        RequireString(d, "npcId", errors, prefix: "decision.");
        RequireString(d, "termsHash", errors, prefix: "decision.");
        RequireString(d, "schemaVersion", errors, prefix: "decision.");

        if (!d.TryGetProperty("outcome", out var o) || o.ValueKind != JsonValueKind.String || !AllowedDecisionOutcomes.Contains(o.GetString()!))
            errors.Add($"decision.outcome must be one of {string.Join("|", AllowedDecisionOutcomes)}");

        if (d.TryGetProperty("proposedTerms", out var pt) && pt.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            errors.Add("decision.proposedTerms must be a string or null");
        if (d.TryGetProperty("flavorDialogue", out var fd) && fd.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            errors.Add("decision.flavorDialogue must be a string or null");
    }

    private static void ValidateIntent(JsonElement intent, List<string> errors)
    {
        foreach (var prop in intent.EnumerateObject())
            if (!IntentFields.Contains(prop.Name))
                errors.Add($"unknown 'intent' field '{prop.Name}'");

        RequireString(intent, "intentId", errors, prefix: "intent.");
        RequireString(intent, "type", errors, prefix: "intent.");
        RequireString(intent, "target", errors, prefix: "intent.");
        RequireString(intent, "schemaVersion", errors, prefix: "intent.");

        if (intent.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
        {
            var tv = t.GetString()!;
            if (!AllowedIntentTypes.Contains(tv))
                errors.Add($"intent.type '{tv}' is not an allowed capabilityId");
            if (tv == "INTENT_NONE" && (intent.TryGetProperty("target", out var tn) && tn.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(tn.GetString())))
                errors.Add("INTENT_NONE must not carry a target");
        }

        if (intent.TryGetProperty("priority", out var p))
        {
            if (p.ValueKind != JsonValueKind.Number)
                errors.Add("intent.priority must be a number");
            else if (p.TryGetSingle(out var pv) && (pv < 0f || pv > 1f))
                errors.Add("intent.priority must be in [0,1]");
        }
        else
        {
            errors.Add("missing intent.priority");
        }

        if (intent.TryGetProperty("params", out var prm) && prm.ValueKind == JsonValueKind.Object)
        {
            int count = 0;
            foreach (var _ in prm.EnumerateObject()) count++;
            if (count > MaxParamsSize) errors.Add("intent.params too large");
        }
    }

    private static void ValidateMemoryCandidate(JsonElement m, List<string> errors)
    {
        foreach (var prop in m.EnumerateObject())
            if (!MemoryFields.Contains(prop.Name))
                errors.Add($"unknown memoryCandidates field '{prop.Name}'");

        RequireString(m, "npcId", errors, prefix: "memory.");
        RequireString(m, "sourceObservationId", errors, prefix: "memory.");
        RequireString(m, "summary", errors, prefix: "memory.");
        RequireString(m, "emotionalTone", errors, prefix: "memory.");
        RequireString(m, "schemaVersion", errors, prefix: "memory.");

        if (m.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String)
        {
            if (string.IsNullOrWhiteSpace(s.GetString())) errors.Add("memory.summary must be non-empty");
            else if (s.GetString()!.Length > MaxSummaryLength) errors.Add($"memory.summary exceeds {MaxSummaryLength} chars");
        }

        if (m.TryGetProperty("emotionalTone", out var e) && e.ValueKind == JsonValueKind.String)
        {
            if (!AllowedEmotionalTones.Contains(e.GetString()!))
                errors.Add($"memory.emotionalTone '{e.GetString()}' is not allowed");
        }

        if (m.TryGetProperty("importance", out var imp))
        {
            if (imp.ValueKind != JsonValueKind.Number) errors.Add("memory.importance must be a number");
            else if (imp.TryGetSingle(out var iv) && (iv < 0f || iv > 1f)) errors.Add("memory.importance must be in [0,1]");
        }
        else errors.Add("missing memory.importance");

        if (m.TryGetProperty("relatedFactIds", out var rf) && rf.ValueKind != JsonValueKind.Array) errors.Add("memory.relatedFactIds must be array");
        if (m.TryGetProperty("tags", out var tg) && tg.ValueKind != JsonValueKind.Array) errors.Add("memory.tags must be array");
    }

    private static void ValidateStateDelta(JsonElement d, List<string> errors)
    {
        foreach (var prop in d.EnumerateObject())
            if (!StateDeltaFields.Contains(prop.Name))
                errors.Add($"unknown stateDelta field '{prop.Name}'");

        RequireString(d, "npcId", errors, prefix: "delta.");
        RequireString(d, "path", errors, prefix: "delta.");
        RequireString(d, "reason", errors, prefix: "delta.");
        RequireString(d, "schemaVersion", errors, prefix: "delta.");

        // value required and must be a JSON scalar (object/array forbidden).
        if (!d.TryGetProperty("value", out var val))
            errors.Add("missing delta.value");
        else if (val.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            errors.Add("delta.value must be a JSON scalar");

        if (d.TryGetProperty("confidence", out var cf) && cf.ValueKind == JsonValueKind.Number)
        {
            if (cf.TryGetSingle(out var cv) && (cv < 0f || cv > 1f)) errors.Add("delta.confidence must be in [0,1]");
        }
    }

    private static void RequireString(JsonElement obj, string name, List<string> errors, string prefix = "")
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()))
            errors.Add($"missing or invalid {prefix}{name}");
    }
}
