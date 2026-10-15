using System.Text.Json;

namespace AIWorld.Core.TestSupport;

/// <summary>
/// TRAVEL RELIABILITY MATRIX GATE M1 — machine-readable cycle evidence writer.
///
/// Every matrix cycle (offline test loop or runtime driver iteration) emits one record in the
/// schema frozen by Gate M1 §6. Records land as JSONL under the directory named by
/// AIWORLD_MATRIX_OUTPUT_DIR (set by the runner so the aggregator can validate and aggregate
/// them). When that env var is absent the recorder is a no-op — tests still assert all
/// invariants in-process, they just skip emitting external evidence.
///
/// Self-verification: after appending a record the file is reopened and confirmed to contain
/// the cycle's evidenceMarker. A record whose marker cannot be found after append is evidence
/// of a corrupt write and must not be counted. Evidence path is the JSONL file that actually
/// holds the record (a本轮 test artifact, not a pre-existing file); the aggregator re-opens
/// every evidencePath and re-checks the marker (Gate M1 §6: no evidencePath→marker mismatch).
///
/// Offline test loops pass a per-source filename (cycles-offline-&lt;category&gt;.jsonl) so parallel
/// xUnit classes never interleave into one file. The runtime driver uses its own source name.
/// </summary>
public sealed record MatrixCycleRecord
{
    public string SchemaVersion { get; init; } = "1";
    public string MatrixRunId { get; init; } = "";
    public string CycleId { get; init; } = "";
    public string Category { get; init; } = "";   // SUCCESS|REJECT|UNREACHABLE|INTERRUPTION|ISOLATION|SAVE_RELOAD|COLD_RESTART|LOCAL_SEMANTIC
    public string ActorNpcId { get; init; } = "";
    public string Target { get; init; } = "";
    public string ExecutionKind { get; init; } = "OFFLINE"; // RUNTIME|OFFLINE|LOCAL_QWEN
    public string Result { get; init; } = "PASS"; // PASS|FAIL
    public int AdapterInvocations { get; init; }
    public int ArrivalObservationCount { get; init; }
    public int ActiveLeaseCountAfter { get; init; }
    public string EvidenceMarker { get; init; } = "";
    public string EvidencePath { get; init; } = "";
}

public static class MatrixCycleRecorder
{
    public const string EnvOutputDir = "AIWORLD_MATRIX_OUTPUT_DIR";
    public const string EnvRunId = "AIWORLD_MATRIX_RUN_ID";

    // The aggregator (Invoke-MatrixEvidence.py) reads camelCase keys — keep the emitted JSON
    // aligned so evidencePath/evidenceMarker/category/... resolve without case games.
    private static readonly JsonSerializerOptions _camelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly object _lock = new();

    /// <summary>Full path a record for <paramref name="sourceFile"/> would land at, or null when
    /// the output dir is not configured. Used by recorders to pre-populate EvidencePath so the
    /// emitted record already carries the exact file that holds it (aggregator re-verifies).</summary>
    public static string? CycleEvidencePath(string sourceFile)
    {
        var dir = Environment.GetEnvironmentVariable(EnvOutputDir);
        if (string.IsNullOrWhiteSpace(dir)) return null;
        return Path.GetFullPath(Path.Combine(dir, sourceFile));
    }

    /// <summary>Writes a cycle record to the per-source JSONL under the matrix output dir.
    /// Returns the evidence path (the file that now contains the record), or null when the
    /// output dir is not configured (in-process assertions only).</summary>
    public static string? Record(MatrixCycleRecord record, string sourceFile)
    {
        var path = CycleEvidencePath(sourceFile);
        if (path is null) return null;

        var matrixRunId = Environment.GetEnvironmentVariable(EnvRunId) ?? "offline-standalone";
        record = record with { MatrixRunId = matrixRunId };

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(record, _camelCase);

        lock (_lock)
        {
            File.AppendAllText(path, json + Environment.NewLine);
        }

        // Self-verify: reopen the exact file and confirm the marker is present. A write that
        // cannot be re-read is a harness defect — surface it rather than counting the cycle.
        var marker = record.EvidenceMarker;
        if (!string.IsNullOrWhiteSpace(marker))
        {
            var text = File.ReadAllText(path);
            if (!text.Contains(marker, StringComparison.Ordinal))
                throw new InvalidOperationException($"MATRIX_CYCLE_SELFVERIFY_FAILED cycleId={record.CycleId} marker not found after append in {path}");
        }
        return path;
    }
}
