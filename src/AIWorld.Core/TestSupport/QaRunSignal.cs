using System.Text.Json;

namespace AIWorld.Core.TestSupport;

/// <summary>
/// GATE R1.4 §1 — QA run-owner-backed stage-done signal writer. The runId embedded in
/// stage-a-done.signal is read ONLY from the QA run owner (qa-run-owner.json) that the runner
/// writes into the deployed AIWorld.Demo before the game launches. Owner missing / unreadable /
/// empty runId ⇒ the signal is NOT written (explicit failure) — an unowned signal would be
/// foreign to the runner's cleanup and could never be deleted. Lives in Core (net6.0;net8.0)
/// so the SAME logic runs in the Demo mod (net6.0) and is exercised by the offline test project
/// (net8.0). mtime is never ownership; only the content runId proves this run wrote the signal.
/// </summary>
public static class QaRunSignal
{
    public const string OwnerFileName = "qa-run-owner.json";
    public const string SignalFileName = "stage-a-done.signal";

    /// <summary>Read the runId from the QA run owner. Returns null for missing / unreadable /
    /// empty runId — never an unknown placeholder.</summary>
    public static string? ReadOwnerRunId(string modDir)
    {
        var ownerPath = Path.Combine(modDir, OwnerFileName);
        if (!File.Exists(ownerPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ownerPath));
            if (doc.RootElement.TryGetProperty("runId", out var rid))
            {
                var s = rid.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Write stage-a-done.signal ONLY when the QA run owner is present and carries a
    /// non-empty runId. Returns false + a concrete reason when the owner is missing/unreadable/
    /// empty — the signal is never written without a provable content runId.</summary>
    public static bool TryWriteStageDone(string modDir, out string error)
    {
        var runId = ReadOwnerRunId(modDir);
        if (string.IsNullOrEmpty(runId))
        {
            error = "QA_RUN_OWNER_MISSING_OR_EMPTY runId=<empty> (refusing to write stage-a-done.signal — the runner must create qa-run-owner.json before launch)";
            return false;
        }
        var payload = "{\"runId\":\"" + runId + "\",\"writtenAtUtc\":\"" + DateTime.UtcNow.ToString("o") + "\"}";
        File.WriteAllText(Path.Combine(modDir, SignalFileName), payload);
        error = "";
        return true;
    }
}
