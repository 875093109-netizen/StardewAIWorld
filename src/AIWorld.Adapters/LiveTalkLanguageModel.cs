using System.Security.Cryptography;
using System.Text;
using AIWorld.Adapters.DeepSeek;
using AIWorld.Core.Chat;
using AIWorld.Core.Providers;

namespace AIWorld.Adapters;

/// <summary>Atomically replaced preferences. Each request captures its own immutable snapshot.</summary>
public sealed class LiveTalkLanguageModel : IAIWorldLanguageModel, IDisposable
{
    private TalkOptions _options;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
    public LiveTalkLanguageModel(TalkOptions options) => _options = options.Normalize();
    public TalkOptions Options => Volatile.Read(ref _options).Normalize();
    public void Update(TalkOptions options) => Volatile.Write(ref _options, options.Normalize());
    public static string CredentialVariable(string endpoint) => TalkOptions.IsDeepSeek(endpoint) ? "DEEPSEEK_API_KEY"
        : "AIWORLD_KEY_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(new Uri(endpoint).GetLeftPart(UriPartial.Authority))))[..12];
    public static string? ReadSetting(string name)
    {
        var user = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) : null;
        return string.IsNullOrWhiteSpace(user) ? Environment.GetEnvironmentVariable(name) : user;
    }
    private DeepSeekLanguageModel Create(TalkOptions options) => new(_http, name => name == "DEEPSEEK_API_KEY"
        ? ReadSetting(CredentialVariable(options.Endpoint)) : TalkOptions.IsDeepSeek(options.Endpoint) ? ReadSetting(name) : null, options);
    public string ModelName => TalkOptions.IsAllowedEndpoint(_options.Endpoint) ? Create(Options).ModelName : "invalid-endpoint";
    public bool HasCredential => TalkOptions.IsAllowedEndpoint(_options.Endpoint) && !string.IsNullOrWhiteSpace(ReadSetting(CredentialVariable(_options.Endpoint)));
    public Task<ProviderResponse> GenerateRawAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        var options = Options;
        if (!TalkOptions.IsAllowedEndpoint(options.Endpoint)) return Task.FromResult(new ProviderResponse { Status = ProviderStatus.ProviderError, Error = "Invalid endpoint" });
        return Create(options).GenerateRawAsync(systemPrompt, userPrompt, cancellationToken);
    }
    public void Dispose() => _http.Dispose();
}
