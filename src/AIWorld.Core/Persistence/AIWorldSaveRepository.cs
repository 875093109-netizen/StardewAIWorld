using System.Text.Json;

namespace AIWorld.Core.Persistence;

/// <summary>
/// P3 Save lifecycle orchestrator — load/validate/migrate/hydrate; dirty tracking;
/// save isolation (per-store); ReturnedToTitle cleanup; fail-safe future-schema protection.
/// </summary>
public sealed class AIWorldSaveRepository
{
    /// <summary>SAVE_SCHEMA_VERSION — current persistence format. Independent of transport schemaVersion.</summary>
    public const string CurrentSaveSchemaVersion = "1";

    private readonly IAIWorldSaveStore _store;
    private readonly SaveSchemaMigrator _migrator;

    private NpcStateRepository? _state;
    private MemoryRepository? _memory;
    private AIWorldSaveData? _active;
    private bool _dataDirty;

    public AIWorldSaveRepository(IAIWorldSaveStore store, SaveSchemaMigrator? migrator = null)
    {
        _store = store;
        _migrator = migrator ?? new SaveSchemaMigrator();
    }

    public bool IsLoaded => _active is not null;
    public NpcStateRepository? State => _state;
    public MemoryRepository? Memory => _memory;

    /// <summary>Mark save root (metadata/followSessions) as modified → persisted on next save.</summary>
    public void MarkDataDirty() => _dataDirty = true;

    /// <summary>Active save root (null until Load() succeeds with Loaded/Empty).</summary>
    public AIWorldSaveData? Data => _active;

    public enum LoadStatus
    {
        Loaded,
        Empty,
        VersionUnsupported,
        Corrupt,
    }

    public sealed class LoadResult
    {
        public required LoadStatus Status { get; init; }
        public string? Message { get; init; }
        public AIWorldSaveData? Data { get; init; }
    }

    /// <summary>Load + hydrate from store. Never throws on corrupt data (safe fallback).</summary>
    public LoadResult Load()
    {
        AIWorldSaveData? data;
        try
        {
            data = _store.Read();
        }
        catch (Exception ex)
        {
            return new LoadResult { Status = LoadStatus.Corrupt, Message = $"read failed: {ex.GetType().Name} — using read-only fallback" };
        }

        if (data is null)
        {
            data = NewEmpty();
            _active = data;
            _state = new NpcStateRepository(data);
            _memory = new MemoryRepository(data);
            return new LoadResult { Status = LoadStatus.Empty, Data = data };
        }

        // Future-schema protection: never treat unknown newer format as empty.
        if (!int.TryParse(data.SaveSchemaVersion, out var version) || version > CurrentSaveVersionInt)
        {
            return new LoadResult
            {
                Status = LoadStatus.VersionUnsupported,
                Message = $"PERSISTENCE_VERSION_UNSUPPORTED — saveSchemaVersion '{data.SaveSchemaVersion}' > current '{CurrentSaveSchemaVersion}'; READ/WRITE PROTECTION (no overwrite of unknown future format)",
            };
        }

        // Migrate older supported schemas (v1 → current; currently identity).
        try
        {
            data = _migrator.Migrate(data);
        }
        catch (Exception ex)
        {
            return new LoadResult { Status = LoadStatus.Corrupt, Message = $"migration failed: {ex.GetType().Name}" };
        }

        _active = data;
        _state = new NpcStateRepository(data);
        _memory = new MemoryRepository(data);
        return new LoadResult { Status = LoadStatus.Loaded, Data = data };
    }

    private static readonly int CurrentSaveVersionInt = int.Parse(CurrentSaveSchemaVersion);

    /// <summary>True when any repository is dirty (state/memory changed since last save).</summary>
    public bool IsDirty => (_state?.Dirty ?? false) || (_memory?.Dirty ?? false) || _dataDirty;

    /// <summary>Persist via store. On failure, dirty remains true (PM §23). Returns success.</summary>
    public bool Save()
    {
        if (_active is null) return false;
        try
        {
            _store.Write(_active);
        }
        catch (Exception)
        {
            return false; // dirty stays true for retry/diagnostics
        }
        _state?.MarkClean();
        _memory?.MarkClean();
        _dataDirty = false; // metadata/followSessions root was written by this save — clean now
        return true;
    }

    /// <summary>Clear all runtime state (ReturnedToTitle). Per-save isolation.</summary>
    public void ClearRuntime()
    {
        _active = null;
        _state = null;
        _memory = null;
        _dataDirty = false;
    }

    private static AIWorldSaveData NewEmpty() => new()
    {
        SaveSchemaVersion = CurrentSaveSchemaVersion,
        Characters = new Dictionary<string, NpcPersistentData>(StringComparer.Ordinal),
        Metadata = new Dictionary<string, string>(StringComparer.Ordinal),
    };

    // ---- Serialization helpers (tests + SMAPI adapter) ----

    public static string Serialize(AIWorldSaveData data) => JsonSerializer.Serialize(data);

    public static AIWorldSaveData? Deserialize(string json)
    {
        try
        {
            var data = JsonSerializer.Deserialize<AIWorldSaveData>(json);
            return data is null ? null : data;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
