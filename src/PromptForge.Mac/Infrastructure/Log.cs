using System.Globalization;
using PromptForge.Mac.Infrastructure;

namespace PromptForge.Mac;

/// <summary>
/// Plain-text log in ~/Library/Logs/PromptForge (readable only by you), kept for 7 days.
/// Records events, timings and error types only: never prompt text, transcripts or API keys.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static bool _initialised;

    /// <summary>Set after "Delete all data" so shutting down doesn't recreate the logs folder.</summary>
    public static bool Disabled { get; set; }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        if (Disabled) return;
        try
        {
            lock (Gate)
            {
                if (!_initialised)
                {
                    AppPaths.EnsurePrivateDirectory(AppPaths.LogDir);
                    foreach (var old in Directory.GetFiles(AppPaths.LogDir, "promptforge-*.log")
                                 .Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)))
                        File.Delete(old);
                    _initialised = true;
                }
                var file = Path.Combine(AppPaths.LogDir, $"promptforge-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Logging must never break the app.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
