using System.Reflection;
using System.Text.Json;
using StardewModdingAPI;

namespace AIWorld.Demo;

/// <summary>
/// SCENARIO A — SocialValley runtime integration (zero upstream modification, zero Harmony).
///
/// Injections (runtime-only, no source change to SocialValley):
///   1. In-memory config: reflectively mutate `SocialValley.ModEntry.ConfigManager.Config`
///      (static public property, verified source) → SelectedProvider=OpenAI,
///      OpenAIApiKey="aiworld-local", OpenAISelectedModel=DeepSeek default model in memory only.
///      This takes effect immediately because SocialValley reads
///      its config object on every GetResponse call.
///   2. Endpoint: reflectively set `AIProviderRegistry.Defs[AIProvider.OpenAI].EndpointUrl`
///      → http://localhost:8617/v1/chat/completions. Verified SOURCE_VERIFIED that
///      GetEndpointUrl() reads the static Defs dictionary on every call (no caching).
///
/// Upstream SocialValley stays clean; no Harmony (PM §二十一).
/// </summary>
public sealed class SocialValleyBridgeInjector
{
    private const string ModEntryTypeName = "SocialValley.ModEntry";
    private const string RegistryTypeName = "SocialValley.AIProviderRegistry";
    private const string ConfigManagerTypeName = "SocialValley.ConfigManager";
    private const string LocalApiKey = "aiworld-local"; // AIWorld server does not validate this

    private readonly IMonitor _monitor;
    private readonly string _aiWorldEndpoint;
    private bool _injected;

    public SocialValleyBridgeInjector(IMonitor monitor, string aiWorldEndpoint)
    {
        _monitor = monitor;
        _aiWorldEndpoint = aiWorldEndpoint;
    }

    public bool IsInjected => _injected;

    /// <summary>
    /// Locate the SocialValley ModEntry type via its assembly (loaded by SMAPI at game start).
    /// </summary>
    private static Type? FindSocialValleyType(string typeName)
    {
        var direct = Type.GetType($"{typeName}, SocialValley");
        if (direct is not null) return direct;
        return AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => { try { return a.GetType(typeName); } catch { return null; } })
            .FirstOrDefault(t => t is not null);
    }

    /// <summary>Mutate SocialValley's in-memory config without overwriting the player's saved provider settings.</summary>
    private bool MutateConfig()
    {
        try
        {
            var modEntryType = FindSocialValleyType(ModEntryTypeName);
            if (modEntryType is null) { _monitor.Log("[S2] SV_CONFIG_FAIL — ModEntry type not found", LogLevel.Warn); return false; }
            var configManagerProp = modEntryType.GetProperty("ConfigManager", BindingFlags.Public | BindingFlags.Static);
            if (configManagerProp is null) { _monitor.Log("[S2] SV_CONFIG_FAIL — ConfigManager property not found", LogLevel.Warn); return false; }
            var configManager = configManagerProp.GetValue(null);
            if (configManager is null) { _monitor.Log("[S2] SV_CONFIG_FAIL — ConfigManager instance null", LogLevel.Warn); return false; }

            var configProp = configManager.GetType().GetProperty("Config");
            if (configProp is null) { _monitor.Log("[S2] SV_CONFIG_FAIL — Config property not found", LogLevel.Warn); return false; }
            var config = configProp.GetValue(configManager);
            if (config is null) { _monitor.Log("[S2] SV_CONFIG_FAIL — Config instance null", LogLevel.Warn); return false; }

            // AIProvider.OpenAI enum value = 4 (Player2=0, Ollama=1, OpenRouter=2, Google=3, OpenAI=4)
            var configType = config.GetType();
            var providerProp = configType.GetProperty("SelectedProvider");
            if (providerProp is not null)
                providerProp.SetValue(config, Enum.ToObject(configType.GetProperty("SelectedProvider")!.PropertyType, 4));
            var keyProp = configType.GetProperty("OpenAIApiKey");
            if (keyProp is not null) keyProp.SetValue(config, LocalApiKey);
            var modelProp = configType.GetProperty("OpenAISelectedModel");
            if (modelProp is not null) modelProp.SetValue(config, AIWorld.Adapters.DeepSeek.DeepSeekLanguageModel.DefaultModel);

            _monitor.Log($"[S2] SV_CONFIG_MUTATED SelectedProvider=OpenAI key=aiworld-local model={AIWorld.Adapters.DeepSeek.DeepSeekLanguageModel.DefaultModel}", LogLevel.Info);
            return true;
        }
        catch (Exception ex)
        {
            _monitor.Log($"[S2] SV_CONFIG_FAIL {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    /// <summary>
    /// Reflectively set AIProviderRegistry.Defs[OpenAI].EndpointUrl to the AIWorld local server.
    /// ProviderDef is a STRUCT — TryGetValue returns a COPY, so mutating the copy's fields does
    /// NOT affect the dictionary. Fix: construct a new ProviderDef (same Label/ListModelsUrl/
    /// RequiresApiKey, new EndpointUrl) and REPLACE the dictionary entry via its indexer.
    /// Verified: GetEndpointUrl() reads Defs via TryGetValue on every call (no caching).
    /// </summary>
    private bool InjectEndpoint()
    {
        try
        {
            var registryType = FindSocialValleyType(RegistryTypeName);
            if (registryType is null) { _monitor.Log("[S2] SV_INJECT_FAIL — AIProviderRegistry type not found", LogLevel.Warn); return false; }

            var defsField = registryType.GetField("Defs", BindingFlags.Public | BindingFlags.Static);
            if (defsField is null) { _monitor.Log("[S2] SV_INJECT_FAIL — Defs field not found", LogLevel.Warn); return false; }

            var defs = defsField.GetValue(null);
            if (defs is null) return false;
            var dictType = defs.GetType();

            var openAiEnum = Enum.ToObject(dictType.GenericTypeArguments[0], 4);
            var tryGet = dictType.GetMethod("TryGetValue");
            var args = new[] { openAiEnum, null };
            var found = (bool)tryGet!.Invoke(defs, args)!;
            if (!found || args[1] is null) { _monitor.Log("[S2] SV_INJECT_FAIL — OpenAI entry missing", LogLevel.Warn); return false; }

            var original = args[1]!;
            var defType = original.GetType();

            // Build a NEW ProviderDef preserving Label/ListModelsUrl/RequiresApiKey/ExtraHeaders,
            // replacing EndpointUrl. (ProviderDef is a struct — the copy is discarded; the new
            // instance is written back via the dictionary indexer.)
            var label = defType.GetField("Label")?.GetValue(original);
            var listModelsUrl = defType.GetField("ListModelsUrl")?.GetValue(original);
            var extraHeaders = defType.GetField("ExtraHeaders")?.GetValue(original);
            var requiresKey = (bool)(defType.GetField("RequiresApiKey")?.GetValue(original) ?? false);

            var replacement = Activator.CreateInstance(defType)!;
            defType.GetField("Label")?.SetValue(replacement, label);
            defType.GetField("EndpointUrl")?.SetValue(replacement, _aiWorldEndpoint);
            defType.GetField("ListModelsUrl")?.SetValue(replacement, listModelsUrl);
            defType.GetField("ExtraHeaders")?.SetValue(replacement, extraHeaders);
            defType.GetField("RequiresApiKey")?.SetValue(replacement, requiresKey);

            // Replace the dictionary entry via indexer: Defs[AIProvider.OpenAI] = replacement.
            var indexer = dictType.GetProperty("Item");
            indexer?.SetValue(defs, replacement, new object[] { openAiEnum });

            _monitor.Log($"[S2] SV_ENDPOINT_INJECTED OpenAI → {_aiWorldEndpoint} (struct-replace via indexer)", LogLevel.Info);
            return true;
        }
        catch (Exception ex)
        {
            _monitor.Log($"[S2] SV_INJECT_FAIL {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    /// <summary>
    /// Run both injections. Called at GameLaunched (SocialValley assemblies + ModEntry static
    /// singletons available, before any save loads). Idempotent.
    /// </summary>
    public bool InjectAll()
    {
        if (_injected) return true;
        bool cfg = MutateConfig();
        bool ep = InjectEndpoint();
        _injected = cfg && ep;
        return _injected;
    }
}
