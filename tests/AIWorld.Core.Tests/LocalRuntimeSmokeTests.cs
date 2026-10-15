using AIWorld.Adapters.Local;
using AIWorld.Core.Pipeline;
using AIWorld.Core.Providers;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// PM Local Provider Routing Gate — LOCAL RUNTIME SMOKE (minimal transport verification).
///
/// Requires the local LM Studio service (aiworld-local-qwen3-8b on 127.0.0.1:1234).
/// Tagged Category=Smoke so the ordinary offline suite EXCLUDES these tests via
///   dotnet test --filter "Category!=Smoke"
/// Run the smoke explicitly:
///   dotnet test --filter "Category=Smoke"
///
/// These tests are NOT counted as offline assertions. They genuinely execute the
/// localhost transport when run.
/// </summary>
[Trait("Category", "Smoke")]
public class LocalRuntimeSmokeTests
{
    [Fact]
    public async Task LocalTransport_ChineseResponse_JsonParsed_SchemaValid()
    {
        var llm = new LocalLanguageModel(); // defaults: 127.0.0.1:1234/v1, aiworld-local-qwen3-8b
        Assert.True(await llm.IsAvailableAsync(), "localhost LocalProvider must be available (start-local-llm.ps1)");

        // Minimal prompt: ask for the canonical StructuredResponse JSON only (Chinese context).
        // The model must return a single JSON object; pipeline parses + schema-validates.
        var system = "You are Abigail from Stardew Valley. Respond ONLY with the exact JSON object below. " +
                     "Do not explain. Do not use markdown. Do not add extra fields.\n" +
                     "Schema: {\"speech\": string, \"intent\": null, \"memoryCandidates\": [], \"stateDelta\": [], \"schemaVersion\": \"1\"}";
        var user = "玩家问：今天天气不错，要一起去矿洞探险吗？";

        var pipeline = new StructuredResponsePipeline(llm, StructuredResponseSemanticValidator.CreateDemoDefault());
        var result = await pipeline.RunAsync(system, user, CancellationToken.None,
            maxSchemaCorrectionRetries: 2, retryOnParseError: true);

        Assert.Equal(StructuredResponseStatus.SUCCESS, result.Status);
        Assert.NotNull(result.Response);
        Assert.False(string.IsNullOrWhiteSpace(result.Response.Speech));
        // Chinese must survive the transport round-trip (llama.cpp JSON Schema grammar corruption
        // is avoided because LocalProvider does NOT use response_format=json_schema).
        Assert.Matches(@"[\u4e00-\u9fff]", result.Response.Speech);
        Assert.Equal("1", result.Response.SchemaVersion);
    }

    [Fact]
    public async Task LocalTransport_ModelName_And_ProviderKind()
    {
        var llm = new LocalLanguageModel();
        Assert.Equal("aiworld-local-qwen3-8b", llm.Model);
        Assert.Equal("local/aiworld-local-qwen3-8b", llm.ModelName);
        var r = await llm.GenerateRawAsync("Reply with exactly: OK", "ping");
        Assert.Equal(ProviderStatus.Success, r.Status);
        Assert.False(string.IsNullOrWhiteSpace(r.Text));
    }
}
