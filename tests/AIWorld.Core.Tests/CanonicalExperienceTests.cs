using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>CHARACTER AWARENESS & CANONICAL EXPERIENCE — deterministic offline tests.
/// FIX 1 (PM 2026-08-12): ExperienceId = hash(CanonicalSourceId, CanonicalEventId, OccurrenceKey).
/// OccurredAt/Facts/Location are METADATA, never identity → same event across scan times and
/// across provenance (LIVE vs SAVE_HISTORY) resolves to the SAME ExperienceId.</summary>
public class CanonicalExperienceTests
{
    private static AIWorldSaveData NewData() => new() { SaveSchemaVersion = "1" };

    private static CanonicalExperienceService NewService(AIWorldSaveData data, string date = "spring-10-1@0600")
        => new(data, new NpcStateRepository(data), new MemoryRepository(data), date);

    private static CanonicalExperienceEvent HeartEvent(
        string actor = "Abigail",
        string date = "spring-10-1@0600",
        ExperienceProvenance prov = ExperienceProvenance.LIVE_OBSERVED,
        string occurrenceKey = "once",
        string? eventId = "1") => new()
    {
        ExperienceId = "ignored", // recomputed on ingest
        Type = "HEART_EVENT",
        Source = "VANILLA",
        CanonicalSourceId = "vanilla.heartEvent",
        CanonicalEventId = eventId,
        OccurrenceKey = occurrenceKey,
        Actor = actor,
        Participants = new List<string> { actor },
        Facts = new List<string> { "Abigail and Player played Prairie King together" },
        OccurredAt = date,
        Location = "SeedShop",
        KnowledgeEffects = new Dictionary<string, string> { ["heartEvent.playedPrairieKing"] = "VERIFIED" },
        MemoryImportance = 0.8f,
        Provenance = prov,
        SchemaVersion = "1",
    };

    private static CanonicalExperienceEvent MailEvent(string actor = "Penny", string date = "spring-11-1@0900") => new()
    {
        ExperienceId = "ignored",
        Type = "MAIL_SENT",
        Source = "VANILLA",
        CanonicalSourceId = "vanilla.mail",
        CanonicalEventId = "pennyLetter1",
        OccurrenceKey = "once",
        Actor = actor,
        Participants = new List<string> { actor },
        Facts = new List<string> { "Penny sent a letter to Player" },
        OccurredAt = date,
        Location = "Trailer",
        KnowledgeEffects = new Dictionary<string, string> { ["mail.sentToPlayer"] = "VERIFIED" },
        MemoryImportance = 0.6f,
        Provenance = ExperienceProvenance.LIVE_OBSERVED,
        SchemaVersion = "1",
    };

    // --- FIX 1: identity is source identity, NOT OccurredAt/Facts ---

    [Fact]
    public void ExperienceId_SameEvent_DifferentScanTimes_SameIdentity()
    {
        var e1 = HeartEvent(date: "spring-10-1@0600");
        var e2 = HeartEvent(date: "spring-10-1@1400"); // same event scanned later same day

        var id1 = CanonicalExperienceService.ComputeExperienceId(e1);
        var id2 = CanonicalExperienceService.ComputeExperienceId(e2);

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void ExperienceId_SameEvent_DifferentDays_SameIdentity()
    {
        var e1 = HeartEvent(date: "spring-10-1@0600");
        var e2 = HeartEvent(date: "summer-20-2@0900"); // same event scanned much later

        var id1 = CanonicalExperienceService.ComputeExperienceId(e1);
        var id2 = CanonicalExperienceService.ComputeExperienceId(e2);

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void ExperienceId_LiveAndHydration_SameIdentity()
    {
        var live = HeartEvent(prov: ExperienceProvenance.LIVE_OBSERVED, date: "spring-10-1@0600");
        var hydrated = HeartEvent(prov: ExperienceProvenance.SAVE_HISTORY_RECONSTRUCTED, date: "spring-10-1@0600");

        Assert.Equal(
            CanonicalExperienceService.ComputeExperienceId(live),
            CanonicalExperienceService.ComputeExperienceId(hydrated));
    }

    [Fact]
    public void ExperienceId_DifferentFacts_SameIdentity()
    {
        // Facts are metadata — different wording must NOT change identity.
        var e1 = HeartEvent();
        // Create a variant with different facts using a fresh event
        var e2b = new CanonicalExperienceEvent
        {
            ExperienceId = "ignored",
            Type = e1.Type,
            Source = e1.Source,
            CanonicalSourceId = e1.CanonicalSourceId,
            CanonicalEventId = e1.CanonicalEventId,
            OccurrenceKey = e1.OccurrenceKey,
            Actor = e1.Actor,
            Participants = new List<string>(e1.Participants),
            Facts = new List<string> { "completely different wording of the same event" },
            OccurredAt = e1.OccurredAt,
            Location = e1.Location,
            KnowledgeEffects = new Dictionary<string, string>(e1.KnowledgeEffects),
            MemoryImportance = e1.MemoryImportance,
            Provenance = e1.Provenance,
            SchemaVersion = "1",
        };

        Assert.Equal(
            CanonicalExperienceService.ComputeExperienceId(e1),
            CanonicalExperienceService.ComputeExperienceId(e2b));
    }

    [Fact]
    public void ExperienceId_DifferentEvent_SameSource_DifferentIdentity()
    {
        var e1 = HeartEvent(eventId: "1");
        var e2 = HeartEvent(eventId: "2"); // different heart event

        Assert.NotEqual(
            CanonicalExperienceService.ComputeExperienceId(e1),
            CanonicalExperienceService.ComputeExperienceId(e2));
    }

    [Fact]
    public void ExperienceId_DistinctRepeatableOccurrences_Distinguishable()
    {
        var e1 = HeartEvent(occurrenceKey: "quest-1", eventId: "questEvent");
        var e2 = HeartEvent(occurrenceKey: "quest-2", eventId: "questEvent");

        Assert.NotEqual(
            CanonicalExperienceService.ComputeExperienceId(e1),
            CanonicalExperienceService.ComputeExperienceId(e2));
    }

    // --- FIX 1: repeated scan across different times → no duplicate memory ---

    [Fact]
    public void Ingest_SameEvent_DifferentScanTimes_NoDuplicate()
    {
        var data = NewData();
        var service = NewService(data);

        var first = service.Ingest(HeartEvent(date: "spring-10-1@0600"));
        var second = service.Ingest(HeartEvent(date: "spring-10-1@1400")); // scan later same day
        var third = service.Ingest(HeartEvent(date: "summer-20-2@0900"));  // scan much later

        Assert.True(first.Accepted);
        Assert.True(second.Duplicate);
        Assert.True(third.Duplicate);
        Assert.Single(data.CanonicalExperiences);
        Assert.Single(data.Characters["Abigail"].Memories);
    }

    [Fact]
    public void Ingest_SameEvent_LiveThenHydration_NoDuplicate()
    {
        var data = NewData();
        var service = NewService(data);

        var live = service.Ingest(HeartEvent(prov: ExperienceProvenance.LIVE_OBSERVED));
        var hydrated = service.Ingest(HeartEvent(prov: ExperienceProvenance.SAVE_HISTORY_RECONSTRUCTED));

        Assert.True(live.Accepted);
        Assert.True(hydrated.Duplicate);
        Assert.Single(data.CanonicalExperiences);
        Assert.Single(data.Characters["Abigail"].Memories);
        // Provenance of the persisted event is the FIRST writer's provenance.
        Assert.Equal(ExperienceProvenance.LIVE_OBSERVED, data.CanonicalExperiences.Single().Provenance);
    }

    [Fact]
    public void Ingest_Duplicate_ReturnsExistingCanonicalEvent_StableIdentity()
    {
        // Duplicate ingestion must expose the PERSISTED event (real ExperienceId),
        // not the caller's raw event whose ExperienceId field is unset ("ignored").
        var data = NewData();
        var service = NewService(data);

        var first = service.Ingest(HeartEvent());
        var second = service.Ingest(HeartEvent());

        Assert.True(second.Duplicate);
        Assert.False(second.Accepted);
        Assert.NotNull(second.Event);
        Assert.Equal(first.Event!.ExperienceId, second.Event.ExperienceId);
        Assert.Equal(data.CanonicalExperiences.Single().ExperienceId, second.Event.ExperienceId);
        Assert.NotEqual("ignored", second.Event.ExperienceId);
    }

    [Fact]
    public void Ingest_DistinctRepeatableOccurrences_BothPersisted()
    {
        var data = NewData();
        var service = NewService(data);

        var first = service.Ingest(HeartEvent(occurrenceKey: "quest-1", eventId: "questEvent"));
        var second = service.Ingest(HeartEvent(occurrenceKey: "quest-2", eventId: "questEvent"));

        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        Assert.Equal(2, data.CanonicalExperiences.Count);
    }

    // --- Idempotency (save/reload must not duplicate) ---

    [Fact]
    public void Ingest_SameEventTwice_IsIdempotent()
    {
        var data = NewData();
        var service = NewService(data);

        var first = service.Ingest(HeartEvent());
        var second = service.Ingest(HeartEvent());

        Assert.True(first.Accepted);
        Assert.False(first.Duplicate);
        Assert.False(second.Accepted);
        Assert.True(second.Duplicate);
        Assert.Single(data.CanonicalExperiences);
        Assert.Single(data.Characters["Abigail"].Memories);
    }

    // --- Participant-scoped knowledge (no broadcast) ---

    [Fact]
    public void Ingest_WritesOnlyToParticipants_NotUnrelatedNpc()
    {
        var data = NewData();
        var service = NewService(data);

        service.Ingest(HeartEvent());

        Assert.Equal("VERIFIED", data.Characters["Abigail"].Knowledge["heartEvent.playedPrairieKing"].State);
        Assert.False(data.Characters.ContainsKey("Sebastian"));
    }

    [Fact]
    public void Ingest_UnrelatedNpc_NeverContaminated()
    {
        var data = NewData();
        var service = NewService(data);

        service.Ingest(HeartEvent(actor: "Abigail"));
        service.Ingest(MailEvent(actor: "Penny"));

        Assert.Equal("VERIFIED", data.Characters["Abigail"].Knowledge["heartEvent.playedPrairieKing"].State);
        Assert.Equal("VERIFIED", data.Characters["Penny"].Knowledge["mail.sentToPlayer"].State);
        // Sebastian was never a participant → no character entry, no memory, no knowledge.
        Assert.False(data.Characters.ContainsKey("Sebastian"));
        Assert.Equal(2, data.CanonicalExperiences.Count);
    }

    // --- Actor vs recipient temporal distinction ---

    [Fact]
    public void Ingest_MailSent_ActorKnows_RecipientDoesNot()
    {
        var data = NewData();
        var service = NewService(data);

        service.Ingest(MailEvent());

        Assert.True(data.Characters.ContainsKey("Penny"));
        Assert.Equal("VERIFIED", data.Characters["Penny"].Knowledge["mail.sentToPlayer"].State);
        Assert.False(data.Characters.ContainsKey("Player"));
        Assert.False(data.CanonicalExperiences.Single().Participants.Contains("Player"));
    }

    // --- Negative precondition: event did NOT occur → nothing created ---

    [Fact]
    public void NoEvent_NoMemory_NoKnowledge()
    {
        var data = NewData();
        var service = NewService(data);

        Assert.Empty(data.CanonicalExperiences);
        Assert.Empty(data.Characters);
    }

    // --- Missing identity → rejected (no fabrication) ---

    [Fact]
    public void Ingest_MissingCanonicalIdentity_Rejected()
    {
        var data = NewData();
        var service = NewService(data);

        var bad = new CanonicalExperienceEvent
        {
            ExperienceId = "ignored",
            Type = "HEART_EVENT",
            Source = "VANILLA",
            CanonicalSourceId = "", // missing → reject
            CanonicalEventId = "1",
            OccurrenceKey = "once",
            Actor = "Abigail",
            Participants = new List<string> { "Abigail" },
            Facts = new List<string> { "fact" },
            OccurredAt = "spring-10-1@0600",
            Location = "SeedShop",
            KnowledgeEffects = new Dictionary<string, string> { ["k"] = "VERIFIED" },
            MemoryImportance = 0.8f,
            Provenance = ExperienceProvenance.LIVE_OBSERVED,
            SchemaVersion = "1",
        };
        var r = service.Ingest(bad);

        Assert.False(r.Accepted);
        Assert.Equal("missing canonicalSourceId", r.Error);
        Assert.Empty(data.CanonicalExperiences);
    }

    // --- Canonical memory content: evidence-backed, no invented detail ---

    [Fact]
    public void Ingest_CreatesCanonicalEpisodicMemory_WithEvidence()
    {
        var data = NewData();
        var service = NewService(data);

        service.Ingest(HeartEvent());

        var mem = data.Characters["Abigail"].Memories.Single();
        Assert.Equal(MemoryLayer.Episodic, mem.Layer);
        Assert.Contains("Prairie King", mem.Summary);
        Assert.Equal("CanonicalExperienceService", mem.InternalMetadata["ingestedBy"]);
        Assert.Equal("VANILLA", mem.InternalMetadata["canonicalSource"]);
        Assert.Equal("LIVE_OBSERVED", mem.InternalMetadata["provenance"]);
    }

    // --- Hydration: conservative, provenance tracked, idempotent ---

    [Fact]
    public void Hydrate_ReconstructsProvenEvents_WithSaveHistoryProvenance()
    {
        var data = NewData();
        var service = NewService(data);
        var hydrator = new CanonicalHistoryHydrator(service);

        var result = hydrator.Hydrate(new[] { HeartEvent(prov: ExperienceProvenance.SAVE_HISTORY_RECONSTRUCTED) });

        Assert.Equal(1, result.Attempted);
        Assert.Equal(1, result.Accepted);
        Assert.Equal(0, result.Duplicates);
        var mem = data.Characters["Abigail"].Memories.Single();
        Assert.Equal("SAVE_HISTORY_RECONSTRUCTED", mem.InternalMetadata["provenance"]);
    }

    [Fact]
    public void Hydrate_SameEventTwice_DoesNotDuplicate()
    {
        var data = NewData();
        var service = NewService(data);
        var hydrator = new CanonicalHistoryHydrator(service);

        hydrator.Hydrate(new[] { HeartEvent(prov: ExperienceProvenance.SAVE_HISTORY_RECONSTRUCTED) });
        var second = hydrator.Hydrate(new[] { HeartEvent(prov: ExperienceProvenance.SAVE_HISTORY_RECONSTRUCTED) });

        Assert.Equal(1, second.Duplicates);
        Assert.Single(data.CanonicalExperiences);
        Assert.Single(data.Characters["Abigail"].Memories);
    }

    [Fact]
    public void Hydrate_RejectsEventWithoutEvidence()
    {
        var data = NewData();
        var service = NewService(data);
        var hydrator = new CanonicalHistoryHydrator(service);

        var noEvidence = HeartEvent();
        noEvidence.Facts.Clear();
        noEvidence.KnowledgeEffects.Clear();

        var result = hydrator.Hydrate(new[] { noEvidence });

        Assert.Equal(0, result.Accepted);
        Assert.Single(result.RejectedReasons);
        Assert.Empty(data.CanonicalExperiences);
    }

    // --- CharacterId collision safety ---

    [Fact]
    public void CharacterId_BuildAndParse()
    {
        Assert.Equal("Abigail", CharacterId.Build("Abigail"));
        Assert.Equal("Abigail@SVE", CharacterId.Build("Abigail", "SVE"));
        Assert.Equal("Abigail", CharacterId.Name("Abigail@SVE"));
        Assert.Equal("SVE", CharacterId.ModId("Abigail@SVE"));
        Assert.Equal("Abigail@SVE", CharacterId.EnsureQualified("Abigail", "SVE"));
        Assert.Equal("Abigail@SVE", CharacterId.EnsureQualified("Abigail@SVE", "SVE"));
        Assert.True(CharacterId.IsValid("Abigail@SomeModWithLongName"));
        Assert.False(CharacterId.IsValid(""));
    }

    // --- SelfState describe ---

    [Fact]
    public void SelfState_Describe_IsDeterministic()
    {
        var s = new SelfState
        {
            CharacterId = "Abigail",
            CurrentLocation = "SeedShop",
            CurrentTime = "spring-10-1@0600",
            CurrentActivity = CurrentActivity.CANONICAL,
            BehaviorOwner = "CANONICAL",
            FollowingPlayer = false,
        };
        var d1 = s.Describe;
        var d2 = new SelfState
        {
            CharacterId = "Abigail",
            CurrentLocation = "SeedShop",
            CurrentTime = "spring-10-1@0600",
            CurrentActivity = CurrentActivity.CANONICAL,
            BehaviorOwner = "CANONICAL",
            FollowingPlayer = false,
        }.Describe;
        Assert.Equal(d1, d2);
        Assert.Contains("activity=CANONICAL", d1);
        Assert.Contains("following=False", d1);
    }
}
