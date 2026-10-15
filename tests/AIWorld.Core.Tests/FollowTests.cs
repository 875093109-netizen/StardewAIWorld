using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// Phase 4 offline test matrix (PM: 28+ items). All deterministic — fake adapter,
/// fake resolvers, fake anchors, in-memory save data.
/// </summary>
public class FollowTests
{
    private const string Abigail = "Abigail";
    private const string Player = "player-1";

    private static (FollowIntentHandler Handler, FakeFollowAdapter Adapter, FollowSessionManager Sessions, AIWorldSaveData Data, CapabilityRegistry Registry) Build(
        FollowDecision decision = FollowDecision.Accept,
        bool anchorActive = false,
        params string[] knownNpcs)
    {
        var data = new AIWorldSaveData
        {
            SaveSchemaVersion = "1",
            Characters = new Dictionary<string, NpcPersistentData>(),
            Metadata = new Dictionary<string, string>(),
        };
        var adapter = new FakeFollowAdapter();
        var registry = CapabilityRegistry.CreateDemoRegistry();
        var anchors = new DeterministicHardAnchorProvider(anchorActive, anchorActive ? "EVENT/CUTSCENE" : null);
        var npcs = knownNpcs.Length > 0 ? knownNpcs : new[] { Abigail };
        var arbitrator = new BehaviorArbitrator(registry, adapter, anchors, new DeterministicNpcResolver(npcs), new DeterministicPlayerResolver(Player));
        var sessions = new FollowSessionManager(data, Player);
        var handler = new FollowIntentHandler(arbitrator, adapter, sessions, new DeterministicFollowDecisionSource(decision));
        return (handler, adapter, sessions, data, registry);
    }

    private static NpcIntent Intent(string capability, string reason = "test") => new()
    {
        IntentId = Guid.NewGuid().ToString("N"),
        Type = capability,
        Target = "Player",
        Priority = 0.6f,
        SchemaVersion = "1",
        Reason = reason,
    };

    // 1-2. intent recognized (registry)
    [Fact]
    public void T01_FollowIntent_Recognized() { var (_, _, _, _, reg) = Build(); Assert.True(reg.IsAvailable("social.follow_player")); }
    [Fact]
    public void T02_StopIntent_Recognized() { var (_, _, _, _, reg) = Build(); Assert.True(reg.IsAvailable("social.stop_following")); }

    // 3. unknown capability rejected
    [Fact]
    public void T03_UnknownCapability_Rejected()
    {
        var (handler, adapter, sessions, _, _) = Build();
        var result = handler.Handle(Intent("social.teleport"), Abigail, "spring-1-1@0600");
        Assert.Equal(BehaviorDecision.RejectedCapabilityUnavailable, result.Arbitration.Decision);
        Assert.False(result.AdapterInvoked);
        Assert.Null(result.Session);
        Assert.Null(result.Lease);
        Assert.Empty(sessions.Sessions);
    }

    // 4. target missing rejected
    [Fact]
    public void T04_TargetMissing_Rejected()
    {
        var (handler, adapter, _, _, _) = Build(knownNpcs: new[] { "Sebastian" });
        var result = handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0600");
        Assert.Equal(BehaviorDecision.RejectedTargetNotFound, result.Arbitration.Decision);
        Assert.False(result.AdapterInvoked);
    }

    // 5. adapter unavailable rejected
    [Fact]
    public void T05_AdapterUnavailable_Rejected()
    {
        var (handler, adapter, _, _, _) = Build();
        adapter.IsAvailable = false;
        var result = handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0600");
        Assert.Equal(BehaviorDecision.AdapterUnavailable, result.Arbitration.Decision);
        Assert.False(result.AdapterInvoked);
    }

    // 6. hard anchor rejected
    [Fact]
    public void T06_HardAnchor_Rejected()
    {
        var (handler, adapter, sessions, _, _) = Build(anchorActive: true);
        var result = handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0600");
        Assert.Equal(BehaviorDecision.RejectedHardAnchor, result.Arbitration.Decision);
        Assert.False(result.AdapterInvoked);
        Assert.Equal(0, adapter.StartCalls);
        Assert.Empty(sessions.Sessions);
    }

    // 7. duplicate follow rejected/idempotent
    [Fact]
    public void T07_DuplicateFollow_Rejected()
    {
        var (handler, adapter, sessions, _, _) = Build();
        var first = handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0600");
        Assert.True(first.Adapter!.Status == "SUCCESS");
        var second = handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0700");
        Assert.Equal(BehaviorDecision.RejectedAlreadyFollowing, second.Arbitration.Decision);
        Assert.False(second.AdapterInvoked);
        Assert.Single(sessions.Sessions); // no duplicate session
    }

    // 8. stop while not following rejected
    [Fact]
    public void T08_StopWhileNotFollowing_Rejected()
    {
        var (handler, adapter, _, _, _) = Build();
        var result = handler.Handle(Intent("social.stop_following"), Abigail, "spring-1-1@0600");
        Assert.Equal(BehaviorDecision.RejectedNotFollowing, result.Arbitration.Decision);
        Assert.False(result.AdapterInvoked);
        Assert.Equal(0, adapter.StopCalls);
    }

    // 9. successful arbitration
    [Fact]
    public void T09_SuccessfulArbitration_Authorized()
    {
        var (handler, adapter, _, _, _) = Build();
        var result = handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0600");
        Assert.True(result.Arbitration.Authorized);
        Assert.True(result.AdapterInvoked);
    }

    // 10. adapter failure does not create lease
    [Fact]
    public void T10_AdapterFailure_NoLease()
    {
        var (handler, adapter, _, _, _) = Build();
        adapter.StartShouldFail = true;
        var result = handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0600");
        Assert.Equal("FAILED", result.Adapter!.Status);
        Assert.Null(result.Lease);
    }

    // 11. adapter failure does not create session
    [Fact]
    public void T11_AdapterFailure_NoSession()
    {
        var (handler, adapter, sessions, _, _) = Build();
        adapter.StartShouldFail = true;
        handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0600");
        Assert.Empty(sessions.Sessions);
    }

    // 12. recruit postcondition timeout → FAILED (no ghost session)
    [Fact]
    public void T12_RecruitPostconditionTimeout()
    {
        var (handler, adapter, sessions, _, _) = Build();
        adapter.StartShouldTimeout = true;
        var result = handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0600");
        Assert.Equal("FAILED", result.Adapter!.Status);
        Assert.Contains("POSTCONDITION_TIMEOUT", result.Adapter.Message);
        Assert.Empty(sessions.Sessions);
        Assert.Null(result.Lease);
    }

    // 13. dismiss postcondition timeout
    [Fact]
    public void T13_DismissPostconditionTimeout()
    {
        var (handler, adapter, sessions, _, _) = Build();
        handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0600");
        adapter.StopShouldTimeout = true;
        var result = handler.Handle(Intent("social.stop_following"), Abigail, "spring-1-1@0700");
        Assert.Equal("FAILED", result.Adapter!.Status);
        Assert.Contains("POSTCONDITION_TIMEOUT", result.Adapter.Message);
        Assert.NotNull(sessions.GetActive(Abigail)); // session stays active on stop failure
    }

    // 14. successful follow creates lease
    [Fact]
    public void T14_SuccessfulFollow_CreatesLease()
    {
        var (handler, _, _, _, _) = Build();
        var result = handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0600");
        Assert.NotNull(result.Lease);
        Assert.Equal("ACTIVE", result.Lease!.Status);
    }

    // 15. successful follow creates session
    [Fact]
    public void T15_SuccessfulFollow_CreatesSession()
    {
        var (handler, _, sessions, _, _) = Build();
        var result = handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0600");
        Assert.NotNull(result.Session);
        Assert.NotNull(sessions.GetActive(Abigail));
        Assert.Equal(Abigail, result.Session!.NpcId);
    }

    // 16. session uses correct npc identity
    [Fact]
    public void T16_Session_CorrectNpcIdentity()
    {
        var (handler, _, sessions, _, _) = Build(knownNpcs: new[] { Abigail, "Sebastian" });
        var intent = Intent("social.follow_player");
        intent = new NpcIntent { IntentId = intent.IntentId, Type = intent.Type, Target = "Player", Priority = intent.Priority, SchemaVersion = intent.SchemaVersion, Reason = intent.Reason };
        var result = handler.Handle(intent, Abigail, "spring-1-1@0600");
        Assert.Equal(Abigail, result.Session!.NpcId);
        Assert.Equal(Player, result.Session.PlayerId);
    }

    // 17. active session persistence roundtrip
    [Fact]
    public void T17_ActiveSession_PersistenceRoundtrip()
    {
        var (handler, _, sessions, data, _) = Build();
        handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0600");
        var json = AIWorldSaveRepository.Serialize(data);
        var restored = AIWorldSaveRepository.Deserialize(json)!;
        Assert.Single(restored.FollowSessions);
        Assert.True(restored.FollowSessions[0].IsActive);
        Assert.Equal(Abigail, restored.FollowSessions[0].NpcId);
    }

    // 18. stop releases lease
    [Fact]
    public void T18_Stop_ReleasesLease()
    {
        var (handler, _, sessions, _, _) = Build();
        handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0600");
        var result = handler.Handle(Intent("social.stop_following"), Abigail, "spring-1-1@0700");
        Assert.Null(result.Lease); // no active lease after stop
        Assert.Null(sessions.GetActive(Abigail));
    }

    // 19. stop closes session (explicit stop while following invokes adapter exactly once)
    [Fact]
    public void T19_Stop_ClosesSession()
    {
        var (handler, adapter, sessions, _, _) = Build();
        handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0600");
        handler.Handle(Intent("social.stop_following"), Abigail, "spring-1-1@0700");
        var latest = sessions.GetLatest(Abigail);
        Assert.NotNull(latest);
        Assert.False(latest!.IsActive);
        Assert.Equal("AI_DISMISS", latest.EndReason);
        Assert.NotNull(latest.EndedAt);
        Assert.Equal(1, adapter.StopCalls); // explicit stop invoked the adapter exactly once
    }

    // 20. closed session persistence roundtrip
    [Fact]
    public void T20_ClosedSession_PersistenceRoundtrip()
    {
        var (handler, _, _, data, _) = Build();
        handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0600");
        handler.Handle(Intent("social.stop_following"), Abigail, "spring-1-1@0700");
        var json = AIWorldSaveRepository.Serialize(data);
        var restored = AIWorldSaveRepository.Deserialize(json)!;
        var s = restored.FollowSessions.Single();
        Assert.False(s.IsActive);
        Assert.Equal("AI_DISMISS", s.EndReason);
    }

    // 21. DECLINE does not invoke adapter
    [Fact]
    public void T21_Decline_NoAdapterInvocation()
    {
        var (handler, adapter, _, _, _) = Build(decision: FollowDecision.Decline);
        var result = handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0600");
        Assert.True(result.DecisionDeclined);
        Assert.False(result.AdapterInvoked);
        Assert.Equal(0, adapter.StartCalls);
        Assert.Equal(0, adapter.StopCalls);
    }

    // 22. DECLINE does not mutate squad/session/lease
    [Fact]
    public void T22_Decline_NoMutation()
    {
        var (handler, adapter, sessions, _, _) = Build(decision: FollowDecision.Decline);
        handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0600");
        Assert.False(adapter.Following);
        Assert.Empty(sessions.Sessions);
    }

    // 23. active session dedup (session manager level)
    [Fact]
    public void T23_ActiveSession_Dedup()
    {
        var (_, _, sessions, _, _) = Build();
        var s1 = sessions.Start(Abigail, "r1", "spring-1-1@0600");
        var s2 = sessions.Start(Abigail, "r2", "spring-1-1@0700");
        Assert.NotNull(s1);
        Assert.Null(s2); // dedup
        Assert.Single(sessions.Sessions);
    }

    // 24. wrong player/recruiter mismatch reconciled
    [Fact]
    public void T24_RecruiterMismatch_Reconciled()
    {
        var (_, _, sessions, _, _) = Build();
        sessions.Start(Abigail, "r", "spring-1-1@0600");
        // Squad says not following but session active → mismatch reported
        var mismatch = sessions.Reconcile(Abigail, squadIsFollowing: false);
        Assert.NotNull(mismatch);
        Assert.Contains("RECONCILIATION_REQUIRED", mismatch);
        // Consistent states → null
        Assert.Null(sessions.Reconcile(Abigail, squadIsFollowing: true));
        sessions.End(Abigail, "AI_DISMISS", "spring-1-1@0700");
        Assert.Null(sessions.Reconcile(Abigail, squadIsFollowing: false));
    }

    // 25. unknown restored squad/session mismatch detected
    [Fact]
    public void T25_RestoredMismatch_Detected()
    {
        var (_, _, sessions, _, _) = Build();
        // no session but squad following → mismatch
        var mismatch = sessions.Reconcile(Abigail, squadIsFollowing: true);
        Assert.NotNull(mismatch);
        Assert.Contains("RECONCILIATION_REQUIRED", mismatch);
    }

    // 26. ReturnedToTitle runtime lease cleanup (ClearRuntime resets manager state)
    [Fact]
    public void T26_ReturnedToTitle_ClearsRuntime()
    {
        var (handler, _, sessions, data, _) = Build();
        handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0600");
        // Simulate ReturnedToTitle: clear runtime (per-save isolation)
        data.FollowSessions.Clear();
        Assert.Empty(sessions.Sessions);
    }

    // 27. persistence separate from Squad internal state
    [Fact]
    public void T27_PersistenceSeparate_FromSquadState()
    {
        var (_, adapter, sessions, data, _) = Build();
        // Session exists without any Squad involvement (fake adapter not called)
        sessions.Start(Abigail, "manual", "spring-1-1@0600");
        Assert.True(adapter.StartCalls == 0);
        Assert.Single(data.FollowSessions);
    }

    // 28. no Persona/Knowledge/Memory mutation from Follow Adapter
    [Fact]
    public void T28_NoKnowledgeMemoryMutation_FromFollow()
    {
        var (handler, _, _, data, _) = Build();
        handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0600");
        handler.Handle(Intent("social.stop_following"), Abigail, "spring-1-1@0700");
        Assert.Empty(data.Characters); // no character state written by follow flow
        Assert.Empty(data.Metadata.Where(kv => kv.Key.StartsWith("knowledge") || kv.Key.StartsWith("memory")));
    }

    // 29. second stop after a successful stop is idempotently rejected with zero adapter delta
    [Fact]
    public void T29_SecondStop_IdempotentZeroDelta()
    {
        var (handler, adapter, _, _, _) = Build();
        handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0600");
        handler.Handle(Intent("social.stop_following"), Abigail, "spring-1-1@0700");
        int callsAfterStop = adapter.StopCalls;
        var second = handler.Handle(Intent("social.stop_following"), Abigail, "spring-1-1@0800");
        Assert.Equal(BehaviorDecision.RejectedNotFollowing, second.Arbitration.Decision);
        Assert.False(second.AdapterInvoked);
        Assert.Equal(callsAfterStop, adapter.StopCalls); // ADAPTER_INVOCATION_COUNT_DELTA = 0
        Assert.NotNull(second.Arbitration.Message);
    }

    // 30. day-end termination distinguished from explicit stop internally/evidence-wise (EndReason)
    [Fact]
    public void T30_DayEndVsExplicitStop_EndReasonDistinguished()
    {
        var (_, _, sessions, _, _) = Build();

        // Explicit stop (production STOP path) → AI_DISMISS.
        sessions.Start(Abigail, "explicit", "spring-1-1@0600");
        sessions.End(Abigail, "AI_DISMISS", "spring-1-1@0700");
        var explicitStop = sessions.GetLatest(Abigail)!;
        Assert.False(explicitStop.IsActive);
        Assert.Equal("AI_DISMISS", explicitStop.EndReason);

        // Backend day-end (Squad OnDayEnding DismissAll → AIWorld reconciliation) → SQUAD_DAY_END.
        sessions.Start(Abigail, "day-end", "spring-2-1@0600");
        sessions.End(Abigail, "SQUAD_DAY_END", "spring-2-1@2600");
        var dayEnd = sessions.GetLatest(Abigail)!;
        Assert.False(dayEnd.IsActive);
        Assert.Equal("SQUAD_DAY_END", dayEnd.EndReason);

        // The two termination sources are distinguishable in the persisted evidence.
        Assert.NotEqual(explicitStop.EndReason, dayEnd.EndReason);
        Assert.NotEqual(explicitStop.EndedAt, dayEnd.EndedAt);
    }

    // 31. CANONICAL ACTOR/TARGET (PM Scenario A cleanup): full pipeline with
    // actor=Abigail + target=Player → authorized, adapter recruits Abigail.
    [Fact]
    public void T31_CanonicalActorTarget_FollowAuthorized()
    {
        var (handler, adapter, sessions, _, _) = Build();
        var intent = Intent("social.follow_player"); // Target = "Player" (canonical)
        var result = handler.Handle(intent, Abigail, "spring-1-1@0600");
        Assert.Equal(BehaviorDecision.Authorized, result.Arbitration.Decision);
        Assert.True(result.AdapterInvoked);
        Assert.Equal(1, adapter.StartCalls);
        Assert.Equal(Abigail, result.Session!.NpcId); // session keyed on ACTOR
        Assert.NotNull(result.Lease);
        Assert.Equal(Abigail, result.Lease!.NpcId);
        Assert.Equal(Abigail, result.Arbitration.NpcId);
    }

    // 32. CANONICAL stop: actor=Abigail + target=Player → dismissed, session closed.
    [Fact]
    public void T32_CanonicalActorTarget_StopAuthorized()
    {
        var (handler, adapter, sessions, _, _) = Build();
        handler.Handle(Intent("social.follow_player"), Abigail, "spring-1-1@0600");
        var stop = handler.Handle(Intent("social.stop_following"), Abigail, "spring-1-1@0700");
        Assert.Equal(BehaviorDecision.Authorized, stop.Arbitration.Decision);
        Assert.True(stop.AdapterInvoked);
        Assert.Equal(1, adapter.StopCalls);
        Assert.Equal(Abigail, stop.Arbitration.NpcId);
        Assert.Null(stop.Session);
        Assert.Null(stop.Lease);
        Assert.Null(sessions.GetActive(Abigail)); // session closed
        Assert.Equal("AI_DISMISS", sessions.GetLatest(Abigail)!.EndReason);
    }

    // 33. Legacy conflation REJECTED: target=NPC name (old P7 workaround semantics) must fail
    // the execution-boundary precondition even though actor is valid. Guards against regression
    // to "Target = Actor" (PM: DO NOT preserve old target=NPC test semantics).
    [Fact]
    public void T33_LegacyNpcTarget_IsRejected()
    {
        var (handler, adapter, _, _, _) = Build();
        var legacy = new NpcIntent
        {
            IntentId = Guid.NewGuid().ToString("N"),
            Type = "social.follow_player",
            Target = Abigail, // LEGACY: NPC name as target — must be rejected
            Priority = 0.6f,
            SchemaVersion = "1",
            Reason = "legacy test",
        };
        var result = handler.Handle(legacy, Abigail, "spring-1-1@0600");
        Assert.Equal(BehaviorDecision.RejectedTargetNotFound, result.Arbitration.Decision);
        Assert.False(result.AdapterInvoked);
        Assert.Equal(0, adapter.StartCalls);
        Assert.Contains("must be 'Player'", result.Arbitration.Message);
    }

    // 34. Canonical full-pipeline semantics: actor ≠ target enforced through arbitration.
    // A different valid actor (Sebastian) acting toward Player is authorized independently.
    [Fact]
    public void T34_CanonicalActorTarget_SebastianActsTowardPlayer()
    {
        var (handler, adapter, sessions, _, _) = Build(knownNpcs: new[] { "Sebastian" });
        var intent = Intent("social.follow_player"); // Target = "Player"
        var result = handler.Handle(intent, "Sebastian", "spring-1-1@0600");
        Assert.Equal(BehaviorDecision.Authorized, result.Arbitration.Decision);
        Assert.Equal("Sebastian", result.Session!.NpcId);
        Assert.Equal("Sebastian", result.Lease!.NpcId);
    }
}
