using PromptForge.Core.Llm;

namespace PromptForge.Core.Settings;

/// <summary>Stores API keys by provider id. The Windows app implements this with DPAPI, the Mac app with the Keychain.</summary>
public interface ISecretStore
{
    string? Get(string providerId);
    void Set(string providerId, string? value);
    void Clear();
}

/// <summary>An endpoint can't be used as configured. The message is written for the user.</summary>
public abstract class EndpointConfigException(string message) : Exception(message);

public sealed class MissingKeyException(string providerName, string? keyUrl)
    : EndpointConfigException($"Add your {providerName} API key in Settings → Model." + (keyUrl is null ? "" : $" Get a free one at {keyUrl}"))
{
    public string? KeyUrl { get; } = keyUrl;
}

/// <summary>A base URL that isn't http(s), or that would send an API key unencrypted to another machine.</summary>
public sealed class InsecureEndpointException(string message) : EndpointConfigException(message);

public static class EndpointResolver
{
    /// <summary>Primary first, then the fallback if it's enabled and different. Endpoints without a needed key are skipped.</summary>
    public static IReadOnlyList<LlmEndpoint> Resolve(AppSettings settings, ISecretStore secrets)
    {
        var list = new List<LlmEndpoint>();
        EndpointConfigException? missing = null;

        foreach (var cfg in new[] { settings.Primary, settings.Fallback })
        {
            if (cfg is null || !cfg.Enabled || string.IsNullOrWhiteSpace(cfg.Model)) continue;
            var endpoint = Build(cfg.Provider, cfg.Model, cfg.BaseUrl, secrets, out var keyProblem);
            if (endpoint is null)
            {
                missing ??= keyProblem;
                continue;
            }
            if (!list.Any(e => e.BaseUrl == endpoint.BaseUrl && e.Model == endpoint.Model))
                list.Add(endpoint);
        }

        if (list.Count == 0 && missing is not null) throw missing;
        return list;
    }

    /// <summary>
    /// The quality-check judge: the configured judge model when its key is saved, otherwise the
    /// optimiser's fallback (or main) model. Independent is false when the judge shares a provider
    /// with the optimiser, because then it grades its own model family.
    /// </summary>
    public static (LlmEndpoint Endpoint, bool Independent) ResolveJudge(AppSettings settings, ISecretStore secrets, IReadOnlyList<LlmEndpoint> optimiser)
    {
        if (optimiser.Count == 0) throw new ArgumentException("No optimiser endpoint.", nameof(optimiser));
        var cfg = settings.Judge;
        if (cfg is { Enabled: true } && !string.IsNullOrWhiteSpace(cfg.Model) &&
            Build(cfg.Provider, cfg.Model, cfg.BaseUrl, secrets, out _) is { } judge)
        {
            return (judge, !optimiser.Any(e => SameService(e, judge)));
        }
        return (optimiser.Count > 1 ? optimiser[1] : optimiser[0], false);
    }

    // Groq and Gemini are one service each whatever the URL; custom and local endpoints are told apart by URL.
    private static bool SameService(LlmEndpoint a, LlmEndpoint b) =>
        a.Preset.Id == b.Preset.Id &&
        (a.Preset.Id is not ("custom" or "ollama") || a.BaseUrl.TrimEnd('/').Equals(b.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    public static LlmEndpoint? ResolveVoice(AppSettings settings, ISecretStore secrets) =>
        Build(settings.Voice.Provider, settings.Voice.Model, settings.Voice.BaseUrl, secrets, out _);

    public static LlmEndpoint? Build(string providerId, string model, string? baseUrl, ISecretStore secrets, out EndpointConfigException? keyProblem)
    {
        keyProblem = null;
        var preset = ProviderPresets.Get(providerId);
        var url = string.IsNullOrWhiteSpace(baseUrl) || preset.Id is not ("custom" or "ollama") ? preset.BaseUrl : baseUrl.Trim();
        if (string.IsNullOrWhiteSpace(url)) return null;
        var key = secrets.Get(preset.Id);
        if (preset.RequiresKey && string.IsNullOrWhiteSpace(key))
        {
            keyProblem = new MissingKeyException(preset.DisplayName, preset.KeyUrl);
            return null;
        }
        if (UrlProblem(url, hasKey: !string.IsNullOrWhiteSpace(key)) is { } problem)
        {
            keyProblem = new InsecureEndpointException(problem);
            return null;
        }
        return new LlmEndpoint(preset, url, model.Trim(), key);
    }

    /// <summary>
    /// Why a base URL can't be used, or null. Only http(s) is allowed, and an API key is never sent over
    /// plain http except to this machine (a local Ollama or LM Studio), where it can't be read off the network.
    /// </summary>
    public static string? UrlProblem(string url, bool hasKey)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || string.IsNullOrEmpty(uri.Host))
            return $"\"{url}\" isn't a valid http:// or https:// address.";
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "Don't put a username or password in the base URL; save the key in Settings → Model instead.";
        if (uri.Scheme == "http" && hasKey && !uri.IsLoopback)
            return $"{uri.Host} uses http://, which would send your API key unencrypted. Use an https:// address.";
        return null;
    }
}
