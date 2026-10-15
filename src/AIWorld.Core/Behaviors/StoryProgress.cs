using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;

namespace AIWorld.Core.Behaviors;

public enum StoryStage { Unknown, NotStarted, Invited, InProgress, Completed }
public enum StoryOutcome { Unknown, Accepted, Rejected }

/// <summary>Saved evidence, not a script for the LLM to advance. Current activity is sampled separately.</summary>
public sealed class StoryProgress
{
    public string StoryId { get; set; } = "";
    public string NpcId { get; set; } = "";
    public StoryStage Stage { get; set; }
    public StoryOutcome Outcome { get; set; }
    public bool InvitationSent { get; set; }
    public string Evidence { get; set; } = "";
}

public sealed record PennyBathEvidence(bool Compatible, bool InvitationQueued, bool InvitationDelivered,
    bool InvitationRead, bool EventSeen, bool InEvent, StoryOutcome ObservedOutcome = StoryOutcome.Unknown,
    bool Reconstructed = false);

/// <summary>Only the audited pennySpa/38 provider may supply evidence. No relationship-level inference.</summary>
public sealed class PennyBathStory
{
    public const string Id = "vanilla.penny.bathhouse";
    private readonly AIWorldSaveData _data;
    private readonly CanonicalExperienceService _experiences;
    public PennyBathStory(AIWorldSaveData data, CanonicalExperienceService experiences)
        => (_data, _experiences) = (data, experiences);

    public bool Apply(PennyBathEvidence e, string stamp)
    {
        var countBefore = _data.CanonicalExperiences.Count;
        if (!_data.Stories.TryGetValue(Id, out var state))
            _data.Stories[Id] = state = new StoryProgress { StoryId = Id, NpcId = "Penny" };
        var before = System.Text.Json.JsonSerializer.Serialize(state);
        if (!e.Compatible)
        {
            // A patched asset must not inherit the vanilla meaning just because its ID matches.
            state.Evidence = "unsupported-content";
            return before != System.Text.Json.JsonSerializer.Serialize(state);
        }
        state.Evidence = e.Reconstructed ? "save-history" : "game-state";
        var sent = e.InvitationQueued || e.InvitationDelivered || e.InvitationRead;
        if (sent)
        {
            state.InvitationSent = true;
            Write("invitation", "INVITATION_SENT", new[] { "Penny" },
                "潘妮给玩家写了纸条，邀请天黑后到澡堂见面；发出邀请不代表玩家已赴约。", e, stamp);
        }
        if (e.InvitationRead)
            Write("invitation-read", "MAIL_READ", new[] { "Player" },
                "玩家读过潘妮邀请天黑后在澡堂见面的纸条；潘妮是否知道玩家已读信没有证据。", e, stamp);
        if (e.EventSeen)
        {
            state.Stage = StoryStage.Completed;
            // A completed/skip flag cannot tell us which answer was selected.
            if (state.Outcome == StoryOutcome.Unknown && !e.Reconstructed && e.ObservedOutcome != StoryOutcome.Unknown)
                state.Outcome = e.ObservedOutcome;
            Write("meeting", "HEART_EVENT", new[] { "Penny", "Player" },
                "游戏记录了潘妮与玩家在澡堂见面的剧情。单凭完成标记不能确定具体选择或未观察到的细节。", e, stamp);
            if (state.Outcome != StoryOutcome.Unknown)
                Write("outcome", "STORY_CHOICE", new[] { "Penny", "Player" },
                    state.Outcome == StoryOutcome.Accepted
                        ? "澡堂相处时，玩家回应了潘妮的感情。" : "澡堂相处时，玩家没有接受潘妮的感情。", e, stamp);
        }
        else if (state.Stage != StoryStage.Completed)
            state.Stage = e.InEvent ? StoryStage.InProgress : state.InvitationSent ? StoryStage.Invited : StoryStage.NotStarted;
        return countBefore != _data.CanonicalExperiences.Count || before != System.Text.Json.JsonSerializer.Serialize(state);
    }

    private void Write(string key, string type, string[] participants, string fact, PennyBathEvidence evidence, string stamp)
    {
        _experiences.Ingest(new CanonicalExperienceEvent
        {
            ExperienceId = "", CanonicalSourceId = Id, CanonicalEventId = key, OccurrenceKey = "once",
            Actor = key == "invitation-read" ? "Player" : "Penny", Participants = participants.ToList(), Source = "VANILLA", Type = type,
            Facts = new() { fact }, OccurredAt = evidence.Reconstructed ? "historical-date-unknown" : stamp,
            Location = key == "invitation" || key == "invitation-read" ? null : "BathHouse_Pool",
            Provenance = evidence.Reconstructed ? ExperienceProvenance.SAVE_HISTORY_RECONSTRUCTED : ExperienceProvenance.LIVE_OBSERVED,
            MemoryImportance = 0.9f, SchemaVersion = "1"
        });
    }

    public static string Context(AIWorldSaveData data, string npc)
    {
        if (npc != "Penny" || !data.Stories.TryGetValue(Id, out var s)) return "";
        if (s.Evidence == "unsupported-content") return "潘妮澡堂剧情：内容已被修改，原版阶段和结果语义未知；不要猜测。";
        var text = s.Stage switch
        {
            StoryStage.NotStarted => "尚无邀请或赴约记录。不能把原作将来可能发生的剧情当作共同经历。",
            StoryStage.Invited => "已发出天黑后在澡堂见面的邀请，尚无赴约完成记录；不知道玩家是否读信。",
            StoryStage.InProgress => "澡堂剧情正在进行，不能预先说出后续结果。",
            StoryStage.Completed => s.Outcome switch
            {
                StoryOutcome.Accepted => "澡堂剧情已结束，已观察到玩家回应你的感情。",
                StoryOutcome.Rejected => "澡堂剧情已结束，已观察到玩家拒绝你的感情。",
                _ => "澡堂剧情已被游戏记为结束；具体选择未知，不要猜测接受、拒绝或亲密举动。"
            },
            _ => "是否发生未知；不要猜测。"
        };
        return "潘妮澡堂剧情（当前权威状态，优先于旧记忆）：" + text;
    }
}
