using AIWorld.Core.TestSupport;
using StardewModdingAPI;

namespace AIWorld.Demo;

/// <summary>
/// GATE R1.4 §1 — DEV_QA stage-done signal writer. The signal is the Demo → Host handshake
/// ("all scenarios complete, now do the real save"). Ownership must be provable from CONTENT:
/// the runId embedded in stage-a-done.signal is read ONLY from the QA run owner
/// (qa-run-owner.json, written by the runner into the deployed AIWorld.Demo before launch).
/// Owner missing / unreadable / empty runId ⇒ the signal is NOT written and an explicit
/// InvalidOperationException surfaces (the runner always creates the owner, so a missing owner
/// is a harness bug that must not silently degrade into an unowned signal). Test-infra only;
/// the host checks File.Exists, so the content shape never affects the save trigger. The fixed
/// filename is kept (stage-a-done.signal); only the content carries runId.
/// </summary>
public static class QaSignal
{
    /// <summary>Write the stage-done signal for the current run, embedding its runId (from the
    /// QA run owner) so the external runner can prove ownership before deleting.</summary>
    public static void WriteStageDone(IModHelper helper)
    {
        if (!QaRunSignal.TryWriteStageDone(helper.DirectoryPath, out var error))
        {
            throw new InvalidOperationException(error);
        }
    }
}
