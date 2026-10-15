using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIWorld.Core.Providers;

namespace AIWorld.Core.Chat;

public sealed record TalkAttempt(string Model, string Endpoint, string SystemPrompt, string UserPrompt,
    string RequestPayload, string RawResponse, string ProviderStatus, int? HttpStatus, long ElapsedMs,
    int? InputTokens, int? OutputTokens, int? TotalTokens);

public sealed record TalkRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string SessionId { get; init; } = "";
    public string Source { get; init; } = "";
    public string Speaker { get; init; } = "";
    public string Target { get; init; } = "";
    public string[] Participants { get; init; } = Array.Empty<string>();
    public string Trigger { get; init; } = "";
    public string GameTime { get; init; } = "";
    public long? SpokenTick { get; init; }
    public int SettingsRevision { get; init; }
    public string Speech { get; init; } = "";
    public string Validation { get; init; } = "准备中";
    public string Execution { get; init; } = "未执行动作";
    public string IntentId { get; init; } = "";
    public TalkAttempt[] Attempts { get; init; } = Array.Empty<TalkAttempt>();
}

public sealed record TalkUsage(long Calls, long InputTokens, long OutputTokens, long TotalTokens, long UnknownUsageCalls);
public sealed record TalkArchive(TalkRecord[] Records, TalkUsage Usage, long Revision = 0);

/// <summary>Bounded, thread-safe, per-save diagnostics. Records are never a world-state source.</summary>
public sealed class TalkJournal
{
    private readonly object _gate = new();
    private readonly List<TalkRecord> _records = new();
    private TalkUsage _usage = new(0, 0, 0, 0, 0);
    public const int Capacity = 160;
    public long Version { get; private set; }
    public TalkUsage Usage { get { lock (_gate) return _usage; } }
    public TalkRecord[] Snapshot() { lock (_gate) return _records.ToArray(); }
    public TalkArchive Archive() { lock (_gate) return new(_records.ToArray(), _usage, Version); }
    public void Clear() { lock (_gate) { _records.Clear(); _usage = new(0, 0, 0, 0, 0); Version++; } }
    public void Restore(TalkArchive archive)
    {
        lock (_gate) { _records.Clear(); _records.AddRange(archive.Records.TakeLast(Capacity)); _usage = archive.Usage; Version++; }
    }
    public string Start(string source, string speaker, string target, string trigger, string gameTime = "",
        string session = "", IEnumerable<string>? participants = null, int revision = 1)
    {
        lock (_gate)
        {
            var record = new TalkRecord { Source = source, Speaker = speaker, Target = target, Trigger = Clean(trigger),
                GameTime = gameTime, SessionId = session, Participants = participants?.ToArray() ?? new[] { speaker, target }, SettingsRevision = revision };
            _records.Add(record); while (_records.Count > Capacity) _records.RemoveAt(0); Version++; return record.Id;
        }
    }
    public void Update(string id, string? speech = null, string? validation = null, string? execution = null, string? intentId = null, long? spokenTick = null)
    {
        lock (_gate)
        {
            var index = _records.FindIndex(r => r.Id == id); if (index < 0) return;
            var r = _records[index];
            _records[index] = r with { Speech = speech is null ? r.Speech : Clean(speech), Validation = validation is null ? r.Validation : Clean(validation),
                Execution = execution is null ? r.Execution : Clean(execution), IntentId = intentId ?? r.IntentId, SpokenTick = spokenTick ?? r.SpokenTick }; Version++;
        }
    }
    public void Execution(string intentId, string outcome)
    {
        if (string.IsNullOrEmpty(intentId)) return;
        foreach (var record in Snapshot().Where(r => r.IntentId == intentId)) Update(record.Id, execution: outcome);
    }
    public void AddAttempt(string id, TalkAttempt attempt)
    {
        lock (_gate)
        {
            var i = _records.FindIndex(r => r.Id == id);
            if (i < 0) return; // Late response from another save, or an evicted trace.
            _usage = new(_usage.Calls + 1, _usage.InputTokens + (attempt.InputTokens ?? 0),
                _usage.OutputTokens + (attempt.OutputTokens ?? 0), _usage.TotalTokens + (attempt.TotalTokens ?? 0),
                _usage.UnknownUsageCalls + (attempt.TotalTokens is null ? 1 : 0));
            if (i >= 0) _records[i] = _records[i] with { Attempts = _records[i].Attempts.Append(attempt with
                { SystemPrompt = Clean(attempt.SystemPrompt), UserPrompt = Clean(attempt.UserPrompt),
                    RequestPayload = Clean(attempt.RequestPayload), RawResponse = Clean(attempt.RawResponse) }).TakeLast(4).ToArray() };
            Version++;
        }
    }
    public static string Clean(string? text)
    {
        var value = text ?? "";
        value = Regex.Replace(value, @"\bsk-[A-Za-z0-9_-]{8,}", "[REDACTED]");
        value = Regex.Replace(value, @"(?i)Bearer\s+[A-Za-z0-9._~+/-]+=*", "Bearer [REDACTED]");
        return value.Length <= 100_000 ? value : value[..100_000] + "\n[记录超过 100000 字符，已截断]";
    }
    public static string Report(TalkRecord record, string? language = null)
    {
        string T(string key) => UiText.Get(language ?? "en", key);
        var b = new StringBuilder($"=== {T("report.title")} ===\nTimestamp: {record.Timestamp:O}\nId: {record.Id}\nSession: {record.SessionId}\nPawn: {record.Speaker}\nTarget: {record.Target}\nParticipants: {string.Join(", ", record.Participants)}\nInteractionType: {record.Source}\nGameTime: {record.GameTime}\nSpokenTick: {record.SpokenTick?.ToString() ?? T("report.unknown")}\nSettingsRevision: {record.SettingsRevision}\nTrigger: {record.Trigger}\nValidation: {record.Validation}\nExecution: {record.Execution}\n\n=== {T("report.response")} ===\n{record.Speech}\n");
        for (int i = 0; i < record.Attempts.Length; i++)
        {
            var a = record.Attempts[i];
            b.Append($"\n=== API REPORT {i + 1} ===\nURL: {a.Endpoint}\nModel: {a.Model}\nStatus: {a.ProviderStatus} / HTTP {a.HttpStatus?.ToString() ?? T("report.unknown")}\nElapsedMs: {a.ElapsedMs}\nInputTokens: {a.InputTokens?.ToString() ?? T("report.unknown")}\nOutputTokens: {a.OutputTokens?.ToString() ?? T("report.unknown")}\nTokenCount: {a.TotalTokens?.ToString() ?? T("report.unknown")}\n\n=== {T("report.prompt")} ===\n{a.SystemPrompt}\n\n=== {T("report.trigger")} ===\n{a.UserPrompt}\n\n--- {T("report.request")} ---\n{a.RequestPayload}\n\n--- {T("report.raw")} ---\n{a.RawResponse}\n");
        }
        return b.ToString();
    }
}

/// <summary>One instance per logical turn; correction attempts retain the same trace ID.</summary>
public sealed class JournalLanguageModel(IAIWorldLanguageModel inner, TalkJournal journal, string traceId) : IAIWorldLanguageModel
{
    public string ModelName => inner.ModelName;
    public async Task<ProviderResponse> GenerateRawAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        ProviderResponse response;
        try { response = await inner.GenerateRawAsync(systemPrompt, userPrompt, cancellationToken); }
        catch (OperationCanceledException) { response = new() { Status = ProviderStatus.Cancelled }; }
        catch (Exception ex) { response = new() { Status = ProviderStatus.ProviderError, Error = ex.GetType().Name }; }
        journal.AddAttempt(traceId, new(response.Model ?? ModelName, response.Endpoint ?? "", response.SentSystemPrompt ?? systemPrompt, response.SentUserPrompt ?? userPrompt,
            response.RequestPayload ?? "（此提供方未提供原始请求）", response.RawResponse ?? response.Text ?? response.Error ?? "",
            response.Status.ToString(), response.HttpStatus, watch.ElapsedMilliseconds,
            response.InputTokens, response.OutputTokens, response.TotalTokens));
        return response;
    }
}
