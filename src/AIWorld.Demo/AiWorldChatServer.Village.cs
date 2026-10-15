using System.Text.Json;
using AIWorld.Core.Chat;
using AIWorld.Core.Pipeline;

namespace AIWorld.Demo;

public sealed partial class AiWorldChatServer
{
    public Task<string> PlayerSayAsync(string npc, string text) => ProcessChatRequestAsync(JsonSerializer.Serialize(new
    {
        messages = new[] { new { role = "user", content = "PLAYER'S MESSAGE: \"" + text + "\"\nRESPONSE GUIDANCE:\nRespond naturally as " + npc + " based on the current context." } }
    }));

    /// <summary>One current speaker's private context only. Ambient speech has no action authority.</summary>
    public async Task<bool> VillageTurnAsync(string sessionId, string speaker, string[] members, string heardHistory,
        Func<bool> stillValid, Action<string> commitSpeech, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var token = timeout.Token;
        if (!await _chatLock.WaitAsync(0, token)) return false;
        string trace = "";
        try
        {
            var prepared = await OnGameThread(() =>
            {
                token.ThrowIfCancellationRequested();
                if (!_repository.IsLoaded || !stillValid() || _isScriptedSceneActive?.Invoke() == true) return null;
                EnsureConversationSave();
                var settings = Settings;
                trace = Journal.Start("VillageChat", speaker, string.Join(", ", members.Where(m => m != speaker)),
                    heardHistory.Length == 0 ? "附近村民相遇" : "接续在场交谈", _gameTimeProvider().ToString(), sessionId, members, settings.Revision);
                var sys = _contextAssembler.BuildSystemPrompt(speaker, _repository.Data!,
                    _memory.GetRelevantMemories(speaker, heardHistory, settings.RecentMemories),
                    _selfStateProvider?.Invoke(speaker), _worldContextProvider(), heardHistory);
                if (settings.IncludeEnvironment) sys += PerceptionContext(CapturePerception(speaker));
                if (settings.IncludeHistory) sys += Conversations.Prompt(speaker);
                sys = WritingPreferences(speaker, sys) + "\nVILLAGER CONVERSATION: Speak ONLY as the assigned character, addressing the present villagers, NOT the player. "
                    + "Participants' names do not reveal their private memories or thoughts. Use only your own knowledge and what you actually heard. "
                    + "Do not claim an action happened. This is speech only: intent=null, decision=null, memoryCandidates=[], stateDelta=[]. "
                    + "No new speaker, no dialogue markup. One short natural turn, maximum 240 characters, in the configured OUTPUT LANGUAGE. "
                    + "You can address the group or an existing participant. Do not force everyone to speak in one response.";
                var user = JsonSerializer.Serialize(new { Speaker = speaker, Participants = members,
                    HeardConversation = ContextBudget.Clip(heardHistory, 1800),
                    PublicActivity = members.Select(m => new { Id = m, Activity = _selfStateProvider?.Invoke(m)?.CurrentActivity.ToString() }),
                    Task = heardHistory.Length == 0 ? "根据当前所见开启简短聊天。" : "自然回应刚才听见的发言。" }, TalkJson);
                return new PreparedChat(speaker, user, sys, user, null,
                    StructuredResponseSemanticValidator.CreateLiveForLocations(Array.Empty<string>(), knownNpcs: members),
                    Save: _repository.Data, TraceId: trace, Locale: Language,
                    StateToken: _selfStateProvider?.Invoke(speaker)?.Job?.Fingerprint ?? "");
            }).WaitAsync(token);
            if (prepared is null) return false;
            var result = await new StructuredResponsePipeline(new JournalLanguageModel(_llm, Journal, trace), prepared.Validator)
                .RunAsync(prepared.SystemPrompt, prepared.UserPrompt, token, maxSchemaCorrectionRetries: 0, retryOnParseError: false).WaitAsync(token);
            return await OnGameThread(() =>
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(prepared.Save, _repository.Data) || prepared.Locale != Language
                    || prepared.StateToken != (_selfStateProvider?.Invoke(speaker)?.Job?.Fingerprint ?? "")
                    || !stillValid() || _isScriptedSceneActive?.Invoke() == true)
                { Journal.Update(trace, validation: "场景已变化 / 丢弃回复"); return false; }
                var reply = result.Response;
                if (result.Status != StructuredResponseStatus.SUCCESS || reply is null || reply.Intent is not null
                    || reply.Decision is not null || reply.MemoryCandidates?.Length > 0 || reply.StateDelta?.Length > 0
                    || reply.Speech.Length > 240 || !DialoguePresentationPolicy.IsPlainSpeech(reply.Speech))
                { Journal.Update(trace, validation: Verdict(result) + " / 自由交谈输出未通过验证"); return false; }
                commitSpeech(reply.Speech);
                foreach (var listener in members)
                    Conversations.Observe(listener, speaker + ": " + reply.Speech, trace, "heard-village-conversation");
                Journal.Update(trace, speech: reply.Speech, validation: "已显示 / 发言者、在场和结构验证通过", execution: "仅对白 / 无世界动作", spokenTick: GameTick());
                return true;
            }).WaitAsync(token);
        }
        catch (OperationCanceledException) { Journal.Update(trace, validation: "取消或超时 / 无发言"); return false; }
        catch (Exception ex) { Journal.Update(trace, validation: "失败 / " + ex.GetType().Name); return false; }
        finally { _chatLock.Release(); }
    }
}
