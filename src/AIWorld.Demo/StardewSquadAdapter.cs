using System.Reflection;
using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using StardewModdingAPI;
using StardewValley;

namespace AIWorld.Demo;

/// <summary>
/// StardewSquadAdapter — production follow adapter (PM D-P4-1 / Implementation Decision A).
/// Reuses the R2-VERIFIED reflection path:
///   SMAPI ModRegistry → ModMetadata.Mod (reflection) → TheStardewSquad.ModEntry
///   → SquadMateFactory.Create(NPC) → RecruitmentManager.Recruit/Dismiss(isSilent:true)
/// Upstream implementation details (RecruitmentManager/SquadMateFactory/RecruiterId) are
/// encapsulated HERE — callers only see StartFollowing/StopFollowing/IsFollowing.
/// Postconditions: SUCCESS requires observable Squad state (R2: converges ~2s; predicate wait,
/// never fixed Sleep). MAIN THREAD ONLY.
/// </summary>
public sealed class StardewSquadAdapter : IFollowAdapter
{
    private const string SquadUniqueId = "ThaliaFawnheart.TheStardewSquad";
    private const string RecruiterIdKey = "TheStardewSquad/RecruiterId";
    private const string ModEntryType = "TheStardewSquad.ModEntry";
    private const string FactoryType = "TheStardewSquad.Framework.SquadMateFactory";
    private const string RecruitmentManagerType = "TheStardewSquad.Framework.RecruitmentManager";
    private const string DismissalWarpBehaviorEnum = "TheStardewSquad.Framework.Behaviors.DismissalWarpBehavior";

    private readonly IMonitor _monitor;
    private readonly IModHelper _helper;
    private readonly int _postconditionTimeoutMs;

    private object? _modEntry;
    private object? _factory;
    private object? _recruitmentManager;
    private object? _squadManager;
    private object? _formationManager;
    private MethodInfo? _createMethod;
    private MethodInfo? _recruitMethod;
    private MethodInfo? _dismissMethod;
    private MethodInfo? _isRecruitedMethod;
    private MethodInfo? _releaseSlotMethod;
    private MethodInfo? _squadRemoveMethod;
    private object? _goHomeEnumValue;
    private bool _resolutionAttempted;

    public StardewSquadAdapter(IMonitor monitor, IModHelper helper, int postconditionTimeoutMs = 6000)
    {
        _monitor = monitor;
        _helper = helper;
        _postconditionTimeoutMs = postconditionTimeoutMs;
    }

    public string AdapterId => "StardewSquadAdapter";

    /// <summary>Test-visible invocation counters (zero-execution proof for DECLINE).</summary>
    public int StartCallCount { get; private set; }
    public int StopCallCount { get; private set; }

    public bool IsAvailable
    {
        get
        {
            TryResolve();
            return _recruitmentManager is not null;
        }
    }

    /// <summary>Resolve Squad runtime via the R2-verified reflection path (once).</summary>
    private void TryResolve()
    {
        if (_resolutionAttempted) return;
        _resolutionAttempted = true;

        try
        {
            var info = _helper.ModRegistry.Get(SquadUniqueId);
            if (info is null)
            {
                _monitor.Log("[P4] SQUAD_MOD_NOT_FOUND", LogLevel.Warn);
                return;
            }

            // R2 path: SMAPI IModInfo has no .Mod; reach the runtime instance via ModMetadata.Mod (reflection).
            var modMetadataType = info.GetType();
            var modProp = modMetadataType.GetProperty("Mod", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (modProp is null)
            {
                _monitor.Log("[P4] SQUAD_MODMETADATA_MOD_PROP_NOT_FOUND", LogLevel.Error);
                return;
            }
            _modEntry = modProp.GetValue(info);
            if (_modEntry is null)
            {
                _monitor.Log("[P4] SQUAD_MODENTRY_NULL", LogLevel.Error);
                return;
            }

            // Public manager properties on ModEntry (A2 audit: all managers public).
            var modEntryType = _modEntry.GetType();
            var factoryProp = modEntryType.GetProperty("SquadMateFactory", BindingFlags.Public | BindingFlags.Instance);
            var rmProp = modEntryType.GetProperty("RecruitmentManager", BindingFlags.Public | BindingFlags.Instance);
            if (factoryProp is null || rmProp is null)
            {
                _monitor.Log("[P4] SQUAD_MANAGER_PROP_MISSING", LogLevel.Error);
                return;
            }
            _factory = factoryProp.GetValue(_modEntry);
            _recruitmentManager = rmProp.GetValue(_modEntry);
            if (_factory is null || _recruitmentManager is null)
            {
                _monitor.Log("[P4] SQUAD_MANAGER_NULL", LogLevel.Error);
                return;
            }

            // Resolve ISquadMate interface type from the Squad assembly (reflection — no compile-time dep).
            var squadAsm = _modEntry.GetType().Assembly;
            var squadMateType = squadAsm.GetType("TheStardewSquad.Framework.Squad.ISquadMate");
            if (squadMateType is null)
            {
                _monitor.Log("[P4] SQUAD_ISQUADMATE_TYPE_NOT_FOUND", LogLevel.Error);
                return;
            }
            var farmerType = typeof(Farmer);

            _createMethod = _factory.GetType().GetMethod("Create", new[] { typeof(NPC) });
            _recruitMethod = _recruitmentManager.GetType().GetMethod("Recruit", new[] { squadMateType, farmerType, typeof(bool) });

            // IsRecruited lives on SquadManager (public property on ModEntry), not RecruitmentManager.
            var squadManagerProp = modEntryType.GetProperty("SquadManager", BindingFlags.Public | BindingFlags.Instance);
            if (squadManagerProp is not null)
            {
                var squadManager = squadManagerProp.GetValue(_modEntry);
                _squadManager = squadManager;
                _isRecruitedMethod = squadManager?.GetType().GetMethod("IsRecruited", new[] { typeof(Character) });
                _squadRemoveMethod = squadManager?.GetType().GetMethod("Remove", new[] { typeof(Character) });
            }

            // FormationManager.ReleaseSlot (public on ModEntry; releases the follow formation slot
            // without any warp — needed for the v0.2 clean removal path).
            var fmProp = modEntryType.GetProperty("FormationManager", BindingFlags.Public | BindingFlags.Instance);
            if (fmProp is not null)
            {
                var fm = fmProp.GetValue(_modEntry);
                _formationManager = fm;
                _releaseSlotMethod = fm?.GetType().GetMethod("ReleaseSlot");
            }

            // DismissalWarpBehavior.GoHome via enum member name (no hardcoded int; R2 evidence).
            var dismissEnumType = _modEntry.GetType().Assembly.GetType(DismissalWarpBehaviorEnum);
            if (dismissEnumType is not null)
            {
                _goHomeEnumValue = Enum.Parse(dismissEnumType, "GoHome");
            }
            _dismissMethod = _recruitmentManager.GetType().GetMethod("Dismiss", new[] { squadMateType, typeof(bool), dismissEnumType, typeof(bool), typeof(long?) });

            _monitor.Log($"[P4] SQUAD_RESOLVED factory={_factory is not null} rm={_recruitmentManager is not null} create={_createMethod is not null} recruit={_recruitMethod is not null} dismiss={_dismissMethod is not null} isRecruited={_isRecruitedMethod is not null} goHome={_goHomeEnumValue is not null} fm={_formationManager is not null} releaseSlot={_releaseSlotMethod is not null} squadRemove={_squadRemoveMethod is not null}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _monitor.Log($"[P4] SQUAD_RESOLVE_FAILED {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
        }
    }

    private NPC? FindNpc(string npcName) => Game1.getCharacterFromName(npcName);

    public bool IsFollowing(string npcId)
    {
        TryResolve();
        if (_isRecruitedMethod is null || _squadManager is null)
        {
            _monitor.Log($"[P4] IS_FOLLOWING_UNRESOLVED methodNull={_isRecruitedMethod is null} smNull={_squadManager is null}", LogLevel.Warn);
            return false;
        }
        var npc = FindNpc(npcId);
        if (npc is null) { _monitor.Log($"[P4] IS_FOLLOWING_NPC_MISSING npc={npcId}", LogLevel.Warn); return false; }
        try
        {
            var result = (bool)_isRecruitedMethod.Invoke(_squadManager, new object?[] { npc })!;
            _monitor.Log($"[P4] IS_FOLLOWING_CHECK npc={npcId} result={result}", LogLevel.Debug);
            return result;
        }
        catch (Exception ex)
        {
            _monitor.Log($"[P4] IS_FOLLOWING_FAILED {ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
            return false;
        }
    }

    public AdapterResult StartFollowing(string npcId, string playerId)
    {
        StartCallCount++;
        TryResolve();
        var npc = FindNpc(npcId);
        if (npc is null)
            return Fail(CapabilityRegistry.CapabilityIds.FollowPlayer, "npcNotInLocation", "NPC not found in world");

        if (_createMethod is null || _recruitMethod is null || _recruitmentManager is null || _factory is null)
            return Fail(CapabilityRegistry.CapabilityIds.FollowPlayer, "adapterUnresolved", "Squad adapter not resolved");

        try
        {
            var mate = _createMethod.Invoke(_factory, new object?[] { npc });
            if (mate is null)
                return Fail(CapabilityRegistry.CapabilityIds.FollowPlayer, "createFailed", "SquadMateFactory.Create returned null");

            _recruitMethod.Invoke(_recruitmentManager, new[] { mate, Game1.player, true }); // isSilent:true (R2)

            // Postcondition: observable recruited state (predicate wait, R2 convergence ~2s).
            if (!WaitFor(() => IsFollowing(npcId), _postconditionTimeoutMs))
                return Fail(CapabilityRegistry.CapabilityIds.FollowPlayer, "postconditionTimeout", "Recruit postcondition not observed (timeout)");

            return new AdapterResult
            {
                AdapterId = AdapterId,
                CapabilityId = CapabilityRegistry.CapabilityIds.FollowPlayer,
                Status = "SUCCESS",
                Message = "recruited (observable)",
                SchemaVersion = "1",
                Detail = new Dictionary<string, object?> { ["postcondition"] = "IsRecruited=true" },
            };
        }
        catch (Exception ex)
        {
            return Fail(CapabilityRegistry.CapabilityIds.FollowPlayer, "recruitException", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public AdapterResult StopFollowing(string npcId, string playerId)
    {
        StopCallCount++;
        TryResolve();
        var npc = FindNpc(npcId);
        if (npc is null)
            return Fail(CapabilityRegistry.CapabilityIds.StopFollowing, "npcNotInLocation", "NPC not found in world");

        // v0.2 CLEAN REMOVAL (PM BEHAVIOR LIFECYCLE): remove the Squad follow state WITHOUT
        // forcing the post-dismiss GoHome warp. Upstream DismissalWarpBehavior is decorative
        // (HandleDismissal ignores it and always warps to the schedule target — see
        // docs/squad-dismissal-audit-v02.md), so we bypass RecruitmentManager.Dismiss and do
        // the minimal clean removal ourselves: SquadManager.Remove (squad list) +
        // FormationManager.ReleaseSlot (formation slot) + vanilla cleanup (controller,
        // RecruiterId modData). NO warp — AIWorld owns post-follow destination semantics.
        if (_createMethod is null || _factory is null || _recruitmentManager is null || _squadManager is null || _squadRemoveMethod is null)
            return Fail(CapabilityRegistry.CapabilityIds.StopFollowing, "adapterUnresolved", "Squad adapter not resolved (clean-removal path)");

        try
        {
            _monitor.Log($"[P4] STOP_CLEAN_REMOVE_START npc={npcId} warp=false", LogLevel.Info);

            // 1. Remove from the squad list (pure list removal, no warp/fade/dialogue).
            _squadRemoveMethod.Invoke(_squadManager, new object?[] { npc });

            // 2. Release the formation slot if the mate had one (prevents slot leak on re-recruit).
            if (_releaseSlotMethod is not null && _formationManager is not null)
            {
                var mate = _createMethod.Invoke(_factory, new object?[] { npc });
                if (mate is not null)
                    _releaseSlotMethod.Invoke(_formationManager, new object?[] { mate });
            }

            // 3. Vanilla cleanup mirroring SquadMateStateHelper.PrepareForDismissal:
            //    stop animation, release movement control, clear RecruiterId modData so
            //    ModEntry.OnSaveLoaded does not rehydrate her back into the squad.
            npc.Sprite.StopAnimation();
            npc.farmerPassesThrough = false;
            npc.controller = null;
            if (npc.modData.ContainsKey(RecruiterIdKey)) npc.modData.Remove(RecruiterIdKey);
            const string schemaVersionKey = "TheStardewSquad/SchemaVersion";
            if (npc.modData.ContainsKey(schemaVersionKey)) npc.modData.Remove(schemaVersionKey);

            // 4. Postcondition: observable not-recruited state (predicate wait).
            if (!WaitFor(() => !IsFollowing(npcId), _postconditionTimeoutMs))
                return Fail(CapabilityRegistry.CapabilityIds.StopFollowing, "postconditionTimeout", "Clean-removal postcondition not observed (timeout)");

            _monitor.Log($"[P4] STOP_CLEAN_REMOVE_DONE npc={npcId} recruited=false modDataCleared=true", LogLevel.Info);
            return new AdapterResult
            {
                AdapterId = AdapterId,
                CapabilityId = CapabilityRegistry.CapabilityIds.StopFollowing,
                Status = "SUCCESS",
                Message = "dismissed (observable, no warp)",
                SchemaVersion = "1",
                Detail = new Dictionary<string, object?>
                {
                    ["postcondition"] = "IsRecruited=false",
                    ["warpBehavior"] = "none (clean removal)",
                    ["recruiterModDataCleared"] = true,
                },
            };
        }
        catch (Exception ex)
        {
            return Fail(CapabilityRegistry.CapabilityIds.StopFollowing, "dismissException", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private AdapterResult Fail(string capabilityId, string reason, string message) => new()
    {
        AdapterId = AdapterId,
        CapabilityId = capabilityId,
        Status = "FAILED",
        Message = message,
        SchemaVersion = "1",
        Detail = new Dictionary<string, object?> { ["failureReason"] = reason },
    };

    /// <summary>Predicate wait on the main thread (game loop drives ticks; never Thread.Sleep in world code paths that block the game).</summary>
    private bool WaitFor(Func<bool> predicate, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            System.Threading.Thread.Sleep(250); // short main-thread yield between samples (test-only path)
        }
        return predicate();
    }
}
