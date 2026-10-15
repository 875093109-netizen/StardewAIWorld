namespace AIWorld.Core.Persistence;

/// <summary>
/// P3 Save Schema Migration. Current version "1". The loader architecture supports
/// future migrations: each migration step transforms an older supported schema into
/// the next. Unknown NEWER schemas are handled by AIWorldSaveRepository (fail-safe
/// protection) — never by this migrator.
/// </summary>
public sealed class SaveSchemaMigrator
{
    /// <summary>Migrate an older-but-supported save to the current schema. Currently identity (v1 is current).</summary>
    public AIWorldSaveData Migrate(AIWorldSaveData data)
    {
        // Future migrations chain here, e.g.: if (data.SaveSchemaVersion == "1") → migrate to "2".
        return data;
    }

    /// <summary>Whether the loader can handle this version (supported = ≤ current).</summary>
    public static bool IsSupported(string version)
    {
        if (!int.TryParse(version, out var v)) return false;
        return v <= int.Parse(AIWorldSaveRepository.CurrentSaveSchemaVersion);
    }
}
