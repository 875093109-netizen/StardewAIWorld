namespace AIWorld.Core.Persistence;

/// <summary>
/// P3 Memory storage bounds (PM §15) — prevents unbounded save growth.
/// Concrete limits for the Demo scale; documented and enforced by MemoryIngestionService.
/// </summary>
public static class MemoryLimits
{
    /// <summary>Hard cap on persisted episodic+semantic+relationship memories per NPC.</summary>
    public const int MaxPersistedMemoriesPerNpc = 100;

    /// <summary>Hard cap on runtime-only recent context per NPC (not persisted).</summary>
    public const int MaxRecentContextPerNpc = 20;

    /// <summary>Max serialized size of a single memory summary (chars).</summary>
    public const int MaxMemorySummaryChars = 200;

    /// <summary>Max serialized size of a single memory record (approx chars, JSON).</summary>
    public const int MaxMemoryRecordChars = 1024;

    /// <summary>Max total persisted memories across all NPCs.</summary>
    public const int MaxTotalPersistedMemories = 2000;
}
