namespace AIWorld.Core.Behaviors;

/// <summary>
/// Hard-anchor protection (Phase 1.5 accepted policy; PM D-P4-1).
/// Core interface so offline tests can inject deterministic states; the SMAPI host
/// implements it against real Game1 state (event/cutscene, sleep, festival).
/// Unknown anchors are NOT guessed — they are simply not reported as blocking.
/// </summary>
public interface IHardAnchorProvider
{
    /// <summary>True when a hard anchor is currently blocking AI behavior (event/cutscene/festival/sleep).</summary>
    bool IsHardAnchorActive(string npcId);

    /// <summary>Which anchor is active (for evidence/logging), or null.</summary>
    string? ActiveAnchor(string npcId);
}

/// <summary>Deterministic provider for offline tests.</summary>
public sealed class DeterministicHardAnchorProvider : IHardAnchorProvider
{
    private readonly bool _active;
    private readonly string? _anchor;

    public DeterministicHardAnchorProvider(bool active = false, string? anchor = null)
    {
        _active = active;
        _anchor = anchor;
    }

    public bool IsHardAnchorActive(string npcId) => _active;
    public string? ActiveAnchor(string npcId) => _active ? _anchor : null;
}

/// <summary>No anchors ever block (used when anchor detection is not configured).</summary>
public sealed class NoHardAnchorsProvider : IHardAnchorProvider
{
    public bool IsHardAnchorActive(string npcId) => false;
    public string? ActiveAnchor(string npcId) => null;
}
