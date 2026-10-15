using System.Net;
using System.Text;
using System.Text.Json;
using AIWorld.Adapters.Local;
using AIWorld.Core.Providers;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// PM Local Provider Routing Gate — LocalLanguageModel transport tests.
/// Uses a stub HttpMessageHandler (no real network). Verifies request shape
/// (model, NO response_format json_schema, /no_think appended), response parsing
/// (content vs reasoning_content), and error mapping.
/// </summary>
public class LocalLanguageModelTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return Responder(request);
        }
    }

    private static LocalLanguageModel Make(StubHandler handler, string? baseUrl = null, string? model = null, bool noThink = true)
        => new(baseUrl, model, noThink, new HttpClient(handler));

    private static string ChatResponse(string content, string? reasoning = null)
    {
        var msg = new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = content };
        if (reasoning is not null) msg["reasoning_content"] = reasoning;
        return JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = msg } },
        });
    }

    [Fact]
    public async Task GenerateRawAsync_PostsToConfiguredEndpoint_WithModel()
    {
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ChatResponse("ok"), Encoding.UTF8, "application/json"),
            },
        };
        var llm = Make(handler, "http://127.0.0.1:9999/v1", "test-model-123");

        var r = await llm.GenerateRawAsync("sys", "user");

        Assert.Equal(ProviderStatus.Success, r.Status);
        Assert.Equal("ok", r.Text);
        Assert.Equal("http://127.0.0.1:9999/v1/chat/completions", handler.LastRequest!.RequestUri!.AbsoluteUri);
        Assert.Equal("test-model-123", llm.Model);
        Assert.Equal("local/test-model-123", llm.ModelName);
    }

    [Fact]
    public async Task GenerateRawAsync_DoesNotSendResponseFormatJsonSchema()
    {
        // LOCAL structured response mode: MUST NOT depend on response_format=json_schema
        // (llama.cpp JSON Schema grammar corrupts Chinese — infra runtime evidence).
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ChatResponse("{}"), Encoding.UTF8, "application/json"),
            },
        };
        var llm = Make(handler);

        await llm.GenerateRawAsync("sys", "user");

        Assert.NotNull(handler.LastBody);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("response_format", out _), "LocalProvider must not send response_format");
        Assert.Equal("aiworld-local-qwen3-8b", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task GenerateRawAsync_AppendsNoThinkToSystemPrompt()
    {
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ChatResponse("x"), Encoding.UTF8, "application/json"),
            },
        };
        var llm = Make(handler);

        await llm.GenerateRawAsync("You are a QA model.", "hello");

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var system = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains(LocalLanguageModel.NoThinkTag, system);
    }

    [Fact]
    public async Task GenerateRawAsync_NoThinkDisabled_WhenConfiguredOff()
    {
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ChatResponse("x"), Encoding.UTF8, "application/json"),
            },
        };
        var llm = Make(handler, noThink: false);

        await llm.GenerateRawAsync("sys", "hello");

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var system = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.DoesNotContain(LocalLanguageModel.NoThinkTag, system);
    }

    [Fact]
    public async Task GenerateRawAsync_ReadsContent_IgnoresReasoningContent()
    {
        // LM Studio returns reasoning_content alongside content for thinking-mode traces;
        // provider must surface ONLY content.
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ChatResponse("final answer", "thinking trace"), Encoding.UTF8, "application/json"),
            },
        };
        var llm = Make(handler);

        var r = await llm.GenerateRawAsync("sys", "user");

        Assert.Equal(ProviderStatus.Success, r.Status);
        Assert.Equal("final answer", r.Text);
        Assert.DoesNotContain("thinking trace", r.Text);
    }

    [Fact]
    public async Task GenerateRawAsync_HttpError_MapsToProviderError()
    {
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
        };
        var llm = Make(handler);

        var r = await llm.GenerateRawAsync("sys", "user");

        Assert.Equal(ProviderStatus.ProviderError, r.Status);
        Assert.Contains("LOCAL_HTTP_503", r.Error);
    }

    [Fact]
    public async Task GenerateRawAsync_ConnectionFailure_MapsToLocalProviderUnavailable()
    {
        var handler = new StubHandler
        {
            Responder = _ => throw new HttpRequestException("connection refused"),
        };
        var llm = Make(handler);

        var r = await llm.GenerateRawAsync("sys", "user");

        Assert.Equal(ProviderStatus.ProviderError, r.Status);
        Assert.Contains("LOCAL_PROVIDER_UNAVAILABLE", r.Error);
    }

    [Fact]
    public async Task IsAvailableAsync_True_OnOk()
    {
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK),
        };
        var llm = Make(handler);

        Assert.True(await llm.IsAvailableAsync());
        Assert.Equal("http://127.0.0.1:1234/v1/models", handler.LastRequest!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task IsAvailableAsync_False_OnError()
    {
        var handler = new StubHandler
        {
            Responder = _ => throw new HttpRequestException("refused"),
        };
        var llm = Make(handler);

        Assert.False(await llm.IsAvailableAsync());
    }

    [Fact]
    public async Task GenerateRawAsync_EmptyContent_MapsToProviderError()
    {
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ChatResponse(""), Encoding.UTF8, "application/json"),
            },
        };
        var llm = Make(handler);

        var r = await llm.GenerateRawAsync("sys", "user");

        Assert.Equal(ProviderStatus.ProviderError, r.Status);
        Assert.Contains("LOCAL_EMPTY_CONTENT", r.Error);
    }
}
