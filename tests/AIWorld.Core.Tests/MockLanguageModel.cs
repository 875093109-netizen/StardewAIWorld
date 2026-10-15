using AIWorld.Core.Providers;

namespace AIWorld.Core.Tests;

/// <summary>
/// P2-03 Mock language model: completely offline, no network.
/// Returns a scripted raw text (or failure state) per invocation.
/// </summary>
public sealed class MockLanguageModel : IAIWorldLanguageModel
{
    private readonly Queue<Func<ProviderResponse>> _responses = new();
    public int InvocationCount { get; private set; }
    public string ModelName => "mock/test-model";

    public MockLanguageModel Enqueue(string rawText) { _responses.Enqueue(() => new ProviderResponse { Status = ProviderStatus.Success, Text = rawText }); return this; }
    public MockLanguageModel Enqueue(ProviderStatus status, string? error = null) { _responses.Enqueue(() => new ProviderResponse { Status = status, Error = error }); return this; }
    public MockLanguageModel Enqueue(Func<ProviderResponse> factory) { _responses.Enqueue(factory); return this; }

    public Task<ProviderResponse> GenerateRawAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        InvocationCount++;
        if (_responses.Count == 0)
            return Task.FromResult(new ProviderResponse { Status = ProviderStatus.ProviderError, Error = "no scripted response" });
        return Task.FromResult(_responses.Dequeue()());
    }
}

public static class Fixtures
{
    public const string ValidSpeechOnly =
        """{"speech":"Hello farmer.","intent":null,"memoryCandidates":[],"stateDelta":[],"schemaVersion":"1"}""";

    public const string ValidNullIntent =
        """{"speech":"I wonder about the wizard.","intent":null,"schemaVersion":"1"}""";

    public const string ValidFollowPlayerIntent =
        """{"speech":"I'll walk with you.","intent":{"intentId":"00000000-0000-0000-0000-000000000001","type":"social.follow_player","target":"Player","priority":0.7,"schemaVersion":"1","reason":"Player invited me"},"memoryCandidates":[],"stateDelta":[],"schemaVersion":"1"}""";

    public const string ValidMagicInvestigate =
        """{"speech":"That tower... I should investigate.","intent":{"intentId":"00000000-0000-0000-0000-000000000002","type":"magic.investigate","target":"WizardHouse","priority":0.6,"schemaVersion":"1"},"schemaVersion":"1"}""";

    // Travel Reliability Gate M1 defect fix regression: travel.to_location must be schema-allowed.
    // Target "Mine" is a CreateDemoDefault-registry location so semantic validation accepts it.
    public const string ValidTravelToLocation =
        """{"speech":"好的，我们一起去矿洞吧","intent":{"intentId":"trv1","type":"travel.to_location","target":"Mine","priority":0.9,"schemaVersion":"1"},"schemaVersion":"1"}""";

    public const string ValidMemoryCandidates =
        """{"speech":"You were reckless in the mines.","intent":null,"memoryCandidates":[{"npcId":"Abigail","sourceObservationId":"obs-001","summary":"Player nearly died in the mines.","emotionalTone":"negative","importance":0.8,"schemaVersion":"1"}],"schemaVersion":"1"}""";

    public const string ValidStateDeltaProposal =
        """{"speech":"I feel calmer now.","intent":null,"stateDelta":[{"npcId":"Abigail","path":"state.mood","value":"calm","reason":"after conversation","schemaVersion":"1"}],"schemaVersion":"1"}""";

    // ---- Invalid fixtures ----
    public const string InvalidJson = "this is not json at all { broken";

    public const string TruncatedJson =
        """{"speech":"Hello farmer.","intent":null,"memoryCandidates":[],"stateDelta":[],"schemaVersion":""";

    public const string MissingSpeech = """{"intent":null,"schemaVersion":"1"}""";

    public const string WrongFieldType = """{"speech":123,"intent":null,"schemaVersion":"1"}""";

    public const string UnknownIntentType = """{"speech":"hi","intent":{"intentId":"id1","type":"teleport.to_pelican_town","target":"Town","priority":0.5,"schemaVersion":"1"},"schemaVersion":"1"}""";

    public const string InvalidTarget = """{"speech":"hi","intent":{"intentId":"id2","type":"social.follow_player","target":"NotARealTarget","priority":0.5,"schemaVersion":"1"},"schemaVersion":"1"}""";

    public const string IllegalStateDeltaPath = """{"speech":"hi","intent":null,"stateDelta":[{"npcId":"Abigail","path":"persona.core_values","value":"evil","reason":"nope","schemaVersion":"1"}],"schemaVersion":"1"}""";

    public const string UnexpectedField = """{"speech":"hi","intent":null,"evilCommand":"warp Abigail home","schemaVersion":"1"}""";

    public const string MarkdownFence =
        "Sure! Here is the response:\n```json\n" + ValidSpeechOnly + "\n```\nHope that helps.";

    public const string ProseAroundJson =
        "Of course, here you go: " + ValidSpeechOnly + " Let me know if you need more.";

    public const string EmptyResponse = "";

    public static readonly string OversizedResponse = new string('x', 20_000) + ValidSpeechOnly;

    public const string InvalidPriority = """{"speech":"hi","intent":{"intentId":"id3","type":"magic.observe","target":"WizardHouse","priority":1.7,"schemaVersion":"1"},"schemaVersion":"1"}""";

    public const string InvalidEmotionalTone = """{"speech":"hi","intent":null,"memoryCandidates":[{"npcId":"Abigail","sourceObservationId":"obs-1","summary":"x","emotionalTone":"furious","importance":0.5,"schemaVersion":"1"}],"schemaVersion":"1"}""";

    public const string INTENT_NONE_withTarget = """{"speech":"hi","intent":{"intentId":"id4","type":"INTENT_NONE","target":"Player","priority":0.5,"schemaVersion":"1"},"schemaVersion":"1"}""";
}
