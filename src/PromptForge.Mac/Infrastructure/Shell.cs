using System.Diagnostics;

namespace PromptForge.Mac.Infrastructure;

/// <summary>Opens web pages and folders with /usr/bin/open. Arguments are passed directly, never through a shell.</summary>
internal static class Shell
{
    public static void Open(string target)
    {
        var isWeb = target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var isSystemSettings = target.StartsWith("x-apple.systempreferences:", StringComparison.Ordinal);
        var isLocal = Path.IsPathFullyQualified(target) && (Directory.Exists(target) || File.Exists(target));
        if (!isWeb && !isSystemSettings && !isLocal)
        {
            Log.Warn("refused to open an unexpected target");
            return;
        }
        Run("/usr/bin/open", target);
    }

    /// <summary>Selects a file or folder in a Finder window.</summary>
    public static void Reveal(string path)
    {
        if (Path.IsPathFullyQualified(path)) Run("/usr/bin/open", "-R", path);
    }

    private static void Run(string program, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(program) { UseShellExecute = false };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            using var _ = Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("open failed", ex);
        }
    }
}
