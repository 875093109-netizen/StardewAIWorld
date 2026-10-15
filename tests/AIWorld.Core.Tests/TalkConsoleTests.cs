using System.Net;
using System.Text.Json;
using AIWorld.Adapters;
using AIWorld.Adapters.DeepSeek;
using AIWorld.Core.Chat;
using AIWorld.Core.Providers;
using Xunit;

namespace AIWorld.Core.Tests;

public class TalkConsoleTests
{
    private static VillageParticipant P(string id, int x = 0, string location = "Town", bool available = true) => new(id, location, x, 0, available);
    [Fact] public void ThreeParticipantsEachGetATurnWithoutInventedNames()
    {
        var session = new VillageConversation(new[] { P("A"), P("B", 1), P("C", 2) }, 4);
        for (int i = 0; i < 3; i++) session.Commit(session.NextSpeaker()!, "hello");
        Assert.Equal(new[] { "A", "B", "C" }, session.Lines.Select(l => l.Speaker));
        Assert.Throws<InvalidOperationException>(() => session.Commit("Ghost", "fake"));
    }
    [Fact] public void NewArrivalDoesNotReceiveEarlierPrivateConversation()
    {
        var session = new VillageConversation(new[] { P("A"), P("B", 1) }, 4);
        session.Commit("A", "earlier secret");
        session.Refresh(new[] { P("A"), P("B", 1), P("C", 2) }, 4);
        Assert.Empty(session.HistoryFor("C"));
        session.Commit("B", "new public words");
        Assert.DoesNotContain("earlier secret", session.HistoryFor("C"));
        Assert.Contains("new public words", session.HistoryFor("C"));
    }
    [Fact] public void GoneBusyOrDistantParticipantsCannotContinue()
    {
        var session = new VillageConversation(new[] { P("A"), P("B", 1) }, 4);
        session.Refresh(new[] { P("A"), P("B", location: "Beach"), P("C", 50), P("D", available: false) }, 4);
        Assert.Single(session.Members); Assert.Null(session.NextSpeaker());
    }
    [Fact] public void GroupMustBeMutuallyWithinRangeNotJustNearInitiator()
    {
        var session = new VillageConversation(new[] { P("A"), P("B", -6), P("C", 6) }, 4);
        Assert.Equal(2, session.Members.Count);
    }
    [Fact] public void IsolatedFirstNpcDoesNotPreventOtherNearbyVillagersTalking()
    {
        var session = new VillageConversation(new[] { P("A", 50), P("B"), P("C", 1) }, 4);
        Assert.Equal(new[] { "B", "C" }, session.Members);
    }
    [Theory]
    [InlineData("https://api.deepseek.com/chat/completions", true)]
    [InlineData("http://localhost:1234/v1/chat/completions", true)]
    [InlineData("http://192.168.0.2/v1/chat/completions", false)]
    [InlineData("https://user:pass@example.org/chat", false)]
    [InlineData("https://example.org/chat?key=secret", false)]
    [InlineData("file:///tmp/keys", false)]
    public void EndpointValidationDoesNotExposeCredentials(string url, bool allowed) => Assert.Equal(allowed, TalkOptions.IsAllowedEndpoint(url));
    [Fact] public void CustomHostsNeverReuseDeepSeekCredentialSlot()
    {
        Assert.Equal("DEEPSEEK_API_KEY", LiveTalkLanguageModel.CredentialVariable("https://api.deepseek.com/chat/completions"));
        Assert.NotEqual("DEEPSEEK_API_KEY", LiveTalkLanguageModel.CredentialVariable("https://elsewhere.example/chat/completions"));
        Assert.NotEqual(LiveTalkLanguageModel.CredentialVariable("https://a.example/chat"), LiveTalkLanguageModel.CredentialVariable("https://b.example/chat"));
    }
    [Fact] public void OptionsAreBoundedAndSnapshotsDoNotSharePersonaEdits()
    {
        var input = new TalkOptions { MaxParticipants = 100, DailyAmbientCalls = 99999, RecentMemories = -3, Temperature = double.NaN };
        var output = input.Normalize(); output.PersonaNotes["A"] = "changed";
        Assert.Equal(4, output.MaxParticipants); Assert.Equal(200, output.DailyAmbientCalls); Assert.Equal(0, output.RecentMemories);
        Assert.Equal(.7, output.Temperature); Assert.Empty(input.PersonaNotes);
    }
    [Fact] public async Task JournalRecordsActualUsageRawPayloadAndRedactsSecrets()
    {
        var transport = new ReplyTransport("{\"choices\":[{\"message\":{\"content\":\"{}\"}}],\"usage\":{\"prompt_tokens\":30,\"completion_tokens\":7,\"total_tokens\":37}}");
        var model = new DeepSeekLanguageModel(new HttpClient(transport), name => name == "DEEPSEEK_API_KEY" ? "fake-credential-value" : null);
        var journal = new TalkJournal(); var id = journal.Start("Chat", "A", "B", "hello");
        var response = await new JournalLanguageModel(model, journal, id).GenerateRawAsync("JSON fake-credential-value", "hi");
        Assert.Equal(ProviderStatus.Success, response.Status);
        var record = Assert.Single(journal.Snapshot()); var attempt = Assert.Single(record.Attempts);
        Assert.Equal(37, attempt.TotalTokens); Assert.Equal(30, attempt.InputTokens); Assert.Equal(7, attempt.OutputTokens);
        Assert.Contains("choices", attempt.RawResponse); Assert.Contains("messages", attempt.RequestPayload);
        Assert.DoesNotContain("fake-credential-value", TalkJournal.Report(record));
        Assert.Equal(37, journal.Usage.TotalTokens); Assert.Equal(0, journal.Usage.UnknownUsageCalls);
    }
    [Fact] public async Task MissingUsageIsUnknownAndHttpErrorsKeepRawReport()
    {
        var model = new DeepSeekLanguageModel(new HttpClient(new ReplyTransport("{\"error\":\"quota\"}", HttpStatusCode.TooManyRequests)), _ => "fake-key");
        var journal = new TalkJournal(); var id = journal.Start("Chat", "A", "B", "hi");
        var reply = await new JournalLanguageModel(model, journal, id).GenerateRawAsync("JSON", "hi");
        Assert.Equal(ProviderStatus.ProviderError, reply.Status);
        Assert.Equal(429, reply.HttpStatus); Assert.Contains("quota", Assert.Single(journal.Snapshot()).Attempts[0].RawResponse);
        Assert.Equal(1, journal.Usage.UnknownUsageCalls); Assert.Null(reply.TotalTokens);
    }
    [Fact] public async Task CustomTransportOmitsDeepSeekOnlyParametersAndUsesSettings()
    {
        var handler = new ReplyTransport("{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}");
        var model = new DeepSeekLanguageModel(new HttpClient(handler), _ => null, new TalkOptions
            { Endpoint = "http://localhost:1234/v1/chat/completions", Model = "local-test", MaxOutputTokens = 512, Temperature = .2 });
        await model.GenerateRawAsync("JSON", "hi");
        using var payload = JsonDocument.Parse(handler.Request!);
        Assert.False(payload.RootElement.TryGetProperty("thinking", out _));
        Assert.Equal("local-test", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal(512, payload.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(.2, payload.RootElement.GetProperty("temperature").GetDouble());
    }
    [Fact] public void JournalEvictionDoesNotResetUsageAndArchiveRetainsOutcome()
    {
        var journal = new TalkJournal();
        for (int i = 0; i < 170; i++)
        {
            var id = journal.Start("Chat", "A", "B", "hi");
            journal.AddAttempt(id, new("test", "", "", "", "", "", "Success", 200, 12, 2, 3, 5));
            journal.Update(id, speech: "hello", intentId: "intent-" + i);
        }
        journal.Execution("intent-169", "arrived");
        var serialized = JsonSerializer.Serialize(journal.Archive());
        var restored = new TalkJournal(); restored.Restore(JsonSerializer.Deserialize<TalkArchive>(serialized)!);
        Assert.Equal(160, restored.Snapshot().Length); Assert.Equal(850, restored.Usage.TotalTokens);
        Assert.Equal("arrived", restored.Snapshot()[^1].Execution);
    }
    [Fact] public void LateResponseCannotRestoreRecordsOrUsageAfterSaveChange()
    {
        var journal = new TalkJournal(); var id = journal.Start("Chat", "A", "B", "old-save");
        journal.Clear();
        journal.AddAttempt(id, new("old-model", "", "old-private-context", "", "", "", "Success", 200, 10, 2, 3, 5));
        Assert.Empty(journal.Snapshot()); Assert.Equal(0, journal.Usage.Calls);
    }
    private sealed class ReplyTransport(string reply, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Request;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Request = await request.Content!.ReadAsStringAsync(cancellationToken); return new(status) { Content = new StringContent(reply) }; }
    }
}
