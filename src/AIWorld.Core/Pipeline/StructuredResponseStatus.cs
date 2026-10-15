namespace AIWorld.Core.Pipeline;

/// <summary>
/// P2 Graceful Failure — unified outcome states per PM Phase 2 §13.
/// Mirrors AdapterResult semantics (contracts v0.2 §2.9) for the structured pipeline.
/// </summary>
public enum StructuredResponseStatus
{
    /// <summary>Parsed, schema-valid, semantic-valid. LOG ONLY.</summary>
    SUCCESS,

    /// <summary>Provider unavailable / auth failure / unexpected provider response.</summary>
    PROVIDER_ERROR,

    /// <summary>HTTP timeout.</summary>
    TIMEOUT,

    /// <summary>Extracted payload is not valid JSON / truncated / empty.</summary>
    PARSE_ERROR,

    /// <summary>JSON parses but violates schema (missing fields, wrong types, bad enums).</summary>
    SCHEMA_ERROR,

    /// <summary>Schema-valid but semantically invalid (unknown capability, illegal state delta path, bad target).</summary>
    SEMANTIC_ERROR,

    /// <summary>Request cancelled (CancellationToken).</summary>
    CANCELLED,

    /// <summary>Response oversized beyond configured limit.</summary>
    OVERSIZED,
}
