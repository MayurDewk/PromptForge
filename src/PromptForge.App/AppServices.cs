using PromptForge.App.Infrastructure;
using PromptForge.Core.Llm;
using PromptForge.Core.Optimization;
using PromptForge.Core.Settings;

namespace PromptForge.App;

/// <summary>Shared services for the tray app. One instance for the app's lifetime.</summary>
internal sealed class AppServices : IDisposable
{
    public AppServices()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        Settings = SettingsStore.Load(AppPaths.SettingsFile, Log.Warn);
        Secrets = new DpapiSecretStore(AppPaths.SecretsFile);
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

    /// <summary>Shows a tray notification. Set by the tray context.</summary>
    public Action<string, ToolTipIcon> Notify { get; set; } = (_, _) => { };

    /// <summary>Shows a tray notification that runs an action when clicked. Set by the tray context.</summary>
    public Action<string, ToolTipIcon, Action> NotifyClickable { get; set; } = (_, _, _) => { };

    public OptimizerOptions OptimizerOptions => new()
    {
        Timeout = TimeSpan.FromSeconds(Math.Clamp(Settings.TimeoutSeconds, 10, 180)),
        RepairDroppedDetails = Settings.RepairDroppedDetails,
    };

    public void SaveSettings() => SettingsStore.Save(AppPaths.SettingsFile, Settings);

    public void Dispose() => Http.Dispose();
}
