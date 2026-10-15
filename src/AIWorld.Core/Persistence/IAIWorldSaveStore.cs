namespace AIWorld.Core.Persistence;

/// <summary>
/// P3-05 IAIWorldSaveStore — persistence abstraction.
/// AIWorld.Core depends ONLY on this interface (no SMAPI dependency).
/// The SMAPI runtime adapter (AIWorld.Demo) maps Helper.Data.ReadSaveData/WriteSaveData onto it.
/// </summary>
public interface IAIWorldSaveStore
{
    /// <summary>Read the AIWorld save root. Returns null when no data exists yet (first play / empty).</summary>
    AIWorldSaveData? Read();

    /// <summary>Write the AIWorld save root (full replace).</summary>
    void Write(AIWorldSaveData data);

    /// <summary>Delete/clear stored data (used on ReturnedToTitle cleanup if required).</summary>
    void Clear();
}

/// <summary>In-memory store for offline tests (two slots simulated by separate instances).</summary>
public sealed class InMemorySaveStore : IAIWorldSaveStore
{
    private AIWorldSaveData? _data;

    public AIWorldSaveData? Read() => _data;

    public void Write(AIWorldSaveData data) => _data = data;

    public void Clear() => _data = null;
}
