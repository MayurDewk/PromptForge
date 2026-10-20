using PromptForge.Core.Llm;
using PromptForge.Core.Optimization;
using PromptForge.Core.Settings;
using PromptForge.Mac.Infrastructure;
using PromptForge.Mac.UI;

namespace PromptForge.Mac;

/// <summary>Shared services for the menu bar app. One instance for the app's lifetime.</summary>
internal sealed class AppServices : IDisposable
{
    public AppServices()
    {
        AppPaths.EnsurePrivateDirectory(AppPaths.DataDir);
        Settings = SettingsStore.Load(AppPaths.SettingsFile, Log.Warn);
        Secrets = new KeychainSecretStore();
        // Per-request timeouts are applied by the client. Replies are small; the cap stops a bad endpoint exhausting memory.
        Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 16 * 1024 * 1024 };
        Client = new OpenAiCompatibleClient(Http);
        Optimizer = new PromptOptimizer(Client);
    }

    public AppSettings Settings { get; set; }
    public ISecretStore Secrets { get; }
    public HttpClient Http { get; }
    public ILlmClient Client { get; }
    public PromptOptimizer Optimizer { get; }

    /// <summary>Shows a notification. Set by the menu bar controller.</summary>
    public Action<string, NotifyKind> Notify { get; set; } = (_, _) => { };

    /// <summary>Shows a notification that runs an action when clicked. Set by the menu bar controller.</summary>
    public Action<string, NotifyKind, Action> NotifyClickable { get; set; } = (_, _, _) => { };

    public OptimizerOptions OptimizerOptions => new()
    {
        Timeout = TimeSpan.FromSeconds(Math.Clamp(Settings.TimeoutSeconds, 10, 180)),
        RepairDroppedDetails = Settings.RepairDroppedDetails,
    };

    public void SaveSettings() => SettingsStore.Save(AppPaths.SettingsFile, Settings);

    public void Dispose() => Http.Dispose();
}
