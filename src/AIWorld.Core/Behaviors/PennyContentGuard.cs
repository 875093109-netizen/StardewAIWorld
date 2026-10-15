using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIWorld.Core.Behaviors;

/// <summary>Verified hashes of installed vanilla translations. Modified meanings degrade to unknown.</summary>
public static class PennyContentGuard
{
    private static readonly Lazy<Dictionary<string, string[]>> Hashes = new(() =>
    {
        using var stream = typeof(PennyContentGuard).Assembly.GetManifestResourceStream("AIWorld.Core.Behaviors.penny-signatures.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)!;
    });
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static bool Matches(string key, string? text) => text is not null && Hashes.Value.TryGetValue(key, out var values) && values.Contains(Hash(text), StringComparer.Ordinal);
}
