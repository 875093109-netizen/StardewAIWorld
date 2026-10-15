using System.Net;
using System.Text.Json;
using AIWorld.Adapters.DeepSeek;
using AIWorld.Core.Providers;
using Xunit;

namespace AIWorld.Core.Tests;

public sealed class DeepSeekConfigurationTests
{
    [Fact] public async Task UsesFlashAndNonThinkingJsonWithoutSendingCredentialInBody()
    {
        var transport = new Transport();
        var model = new DeepSeekLanguageModel(new HttpClient(transport), n => n == "DEEPSEEK_API_KEY" ? "fake-test-credential" : null);
        var result = await model.GenerateRawAsync("Return JSON.", "test");
        Assert.Equal(ProviderStatus.Success, result.Status);
        Assert.Equal("deepseek/deepseek-flash", model.ModelName);
        Assert.Equal("https://api.deepseek.com/chat/completions", transport.Endpoint);
        using var payload = JsonDocument.Parse(transport.Body!);
        Assert.Equal("deepseek-flash", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal("disabled", payload.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("json_object", payload.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.DoesNotContain("fake-test-credential", transport.Body!);
        Assert.Equal("Bearer", transport.Scheme);
    }
    [Fact] public async Task MissingKeyFailsWithoutHttp()
    {
        var transport = new Transport();
        var model = new DeepSeekLanguageModel(new HttpClient(transport), _ => null);
        Assert.Equal(ProviderStatus.ProviderError, (await model.GenerateRawAsync("JSON", "test")).Status);
        Assert.Null(transport.Body);
    }
    [Fact] public async Task ModelSettingIsUsedInActualRequest()
    {
        var transport = new Transport();
        var model = new DeepSeekLanguageModel(new HttpClient(transport), n => n == "DEEPSEEK_MODEL" ? " custom-model " : "fake-test-credential");
        await model.GenerateRawAsync("JSON", "test");
        using var payload = JsonDocument.Parse(transport.Body!);
        Assert.Equal("custom-model", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal("deepseek/custom-model", model.ModelName);
    }
    private sealed class Transport : HttpMessageHandler
    {
        public string? Body, Endpoint, Scheme;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Endpoint = request.RequestUri!.ToString(); Scheme = request.Headers.Authorization?.Scheme;
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}") };
        }
    }
}
