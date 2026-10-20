namespace PromptForge.Mac.Infrastructure;

internal static class AppPaths
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string DataDir { get; } = Path.Combine(Home, "Library", "Application Support", "PromptForge");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LockFile => Path.Combine(DataDir, "instance.lock");
    public static string LogDir { get; } = Path.Combine(Home, "Library", "Logs", "PromptForge");

    /// <summary>The PromptForge.app bundle this process runs from, or null when run outside a bundle (dotnet run).</summary>
    public static string? BundlePath
    {
        get
        {
            // .../PromptForge.app/Contents/MacOS/PromptForge
            var macOs = Path.GetDirectoryName(Environment.ProcessPath ?? "");
            var contents = Path.GetDirectoryName(macOs ?? "");
            var bundle = Path.GetDirectoryName(contents ?? "");
            return bundle is not null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? bundle : null;
        }
    }

    /// <summary>Creates a folder only this user can open (other accounts on the Mac can't list or read it).</summary>
    public static void EnsurePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path, OwnerOnly);
        File.SetUnixFileMode(path, OwnerOnly); // also tighten a folder left by an older version
    }
}
