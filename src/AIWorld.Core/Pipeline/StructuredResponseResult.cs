using AIWorld.Core.Contracts;

namespace AIWorld.Core.Pipeline;

/// <summary>Result of the full structured pipeline (parse → schema → semantic). Immutable.</summary>
public sealed class StructuredResponseResult
{
    public required StructuredResponseStatus Status { get; init; }

    /// <summary>Populated when Status == SUCCESS.</summary>
    public NpcStructuredResponse? Response { get; init; }

    /// <summary>Stage at which failure occurred (Parse / Schema / Semantic), when applicable.</summary>
    public string? FailureStage { get; init; }

    /// <summary>Human-readable failure detail. MUST NOT contain secrets or raw API keys.</summary>
    public string? Message { get; init; }

    /// <summary>Warnings collected during semantic validation (non-fatal).</summary>
    public string[]? Warnings { get; init; }

    public static StructuredResponseResult Ok(NpcStructuredResponse response, string[]? warnings = null) =>
        new() { Status = StructuredResponseStatus.SUCCESS, Response = response, Warnings = warnings };

    public static StructuredResponseResult Fail(StructuredResponseStatus status, string stage, string message) =>
        new() { Status = status, FailureStage = stage, Message = message };
}
