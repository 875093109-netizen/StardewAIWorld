using AIWorld.Core.TestSupport;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// GATE R1.4 §1 — the stage-done signal's runId is read ONLY from the QA run owner
/// (qa-run-owner.json). P3_SIGNAL_CONTENT_HAS_CURRENT_RUN_ID proves the written signal carries
/// the owner's runId; P3_OWNER_MISSING_DOES_NOT_WRITE_SIGNAL proves a missing/unreadable/empty
/// owner REFUSES the write (no unowned signal is ever produced). This is the SAME class the Demo
/// mod (net6.0) calls through QaSignal.WriteStageDone.
/// </summary>
public class QaRunSignalTests
{
    private static string NewModDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiworld-qasignal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void P3_SIGNAL_CONTENT_HAS_CURRENT_RUN_ID()
    {
        var dir = NewModDir();
        try
        {
            var runId = "run-abc123def456";
            File.WriteAllText(Path.Combine(dir, QaRunSignal.OwnerFileName),
                "{\"runId\":\"" + runId + "\",\"profile\":\"DevQA\"}");
            Assert.True(QaRunSignal.TryWriteStageDone(dir, out var err), "write refused: " + err);
            var signalPath = Path.Combine(dir, QaRunSignal.SignalFileName);
            Assert.True(File.Exists(signalPath), "stage-a-done.signal not written");
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(signalPath));
            Assert.Equal(runId, doc.RootElement.GetProperty("runId").GetString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void P3_OWNER_MISSING_DOES_NOT_WRITE_SIGNAL()
    {
        var dir = NewModDir();
        try
        {
            var ok = QaRunSignal.TryWriteStageDone(dir, out var err);
            Assert.False(ok, "signal must NOT be written without a QA run owner");
            Assert.Contains("QA_RUN_OWNER_MISSING_OR_EMPTY", err);
            Assert.False(File.Exists(Path.Combine(dir, QaRunSignal.SignalFileName)),
                "stage-a-done.signal must not exist when the owner is missing");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void P3_OWNER_EMPTY_RUN_ID_DOES_NOT_WRITE_SIGNAL()
    {
        var dir = NewModDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, QaRunSignal.OwnerFileName),
                "{\"runId\":\"\",\"profile\":\"DevQA\"}");
            var ok = QaRunSignal.TryWriteStageDone(dir, out var err);
            Assert.False(ok, "signal must NOT be written with an empty owner runId");
            Assert.Contains("QA_RUN_OWNER_MISSING_OR_EMPTY", err);
            Assert.False(File.Exists(Path.Combine(dir, QaRunSignal.SignalFileName)),
                "stage-a-done.signal must not exist for an empty owner runId");
        }
        finally { Directory.Delete(dir, true); }
    }
}
