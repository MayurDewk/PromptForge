using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using PromptForge.Core.Updates;
using PromptForge.Mac.Infrastructure;
using PromptForge.Mac.UI;

namespace PromptForge.Mac.Flows;

/// <summary>
/// Looks for a newer release once a day (if enabled) and on request from the menu bar. Never installs
/// on its own: the user clicks the notification, the zip for this Mac's processor is downloaded and
/// checked against SHA256SUMS.txt, and then <see cref="MacUpdateInstaller"/> verifies its code signature
/// before anything is replaced.
/// </summary>
internal sealed class UpdateController : IDisposable
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TimerInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);

    private readonly AppServices _services;
    private readonly UpdateChecker _checker;
    private readonly DispatcherTimer _timer = new();
    private bool _busy;

    public UpdateController(AppServices services)
    {
        _services = services;
        _checker = new UpdateChecker(services.Http, UpdateChecker.DefaultRepository, AssetName);
        _timer.Interval = FirstCheckDelay;
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimerInterval;
            await CheckAsync(manual: false);
        };
    }

    /// <summary>The release asset for this processor, e.g. PromptForge-macOS-arm64.zip.</summary>
    public static string AssetName => $"PromptForge-macOS-{(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64")}.zip";

    /// <summary>The newer release found by the last check, if any.</summary>
    public ReleaseInfo? Available { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="Available"/> changes.</summary>
    public event Action? AvailableChanged;

    /// <summary>Set by the menu bar controller: quits so the updated copy can start.</summary>
    public Action? Restart { get; set; }

    public static string CurrentVersion => typeof(UpdateController).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public void Start() => _timer.Start();

    public async Task CheckAsync(bool manual)
    {
        var settings = _services.Settings;
        if (_busy) return;
        if (!manual)
        {
            if (!settings.CheckForUpdates) return;
            if (settings.LastUpdateCheckUtc is { } last && DateTime.UtcNow - last < CheckEvery) return;
        }

        _busy = true;
        try
        {
            var release = await _checker.CheckAsync(CurrentVersion);
            settings.LastUpdateCheckUtc = DateTime.UtcNow;
            _services.SaveSettings();
            Log.Info($"update check current={CurrentVersion} latest={release?.Tag ?? "none newer"} manual={manual}");

            Available = release;
            AvailableChanged?.Invoke();
            if (release is not null)
                _services.NotifyClickable($"PromptForge {release.Version} is available. Click here to update.", NotifyKind.Info, () => _ = InstallAsync());
            else if (manual)
                _services.Notify($"You're on the latest version ({CurrentVersion}).", NotifyKind.Info);
        }
        catch (UpdateException ex)
        {
            Log.Warn("update check failed: " + ex.Message);
            if (manual) _services.Notify(ex.Message, NotifyKind.Warning);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Downloads the available release, verifies it, and installs it (or hands it to the user).</summary>
    public async Task InstallAsync()
    {
        if (Available is not { } release || _busy) return;
        if (!MacDialogs.Confirm($"Update PromptForge to {release.Version}?", "Your settings and API keys are kept. PromptForge restarts when the update is installed.", "Update", "Not now"))
            return;
        _busy = true;
        DirectoryInfo? work = null;
        try
        {
            _services.Notify($"Downloading PromptForge {release.Version}…", NotifyKind.Info);
            // A fresh folder only this user can open, so nothing can be swapped in between checks.
            work = Directory.CreateTempSubdirectory("PromptForge-update-");
            var zip = await _checker.DownloadVerifiedAsync(release, work.FullName, CurrentVersion);
            Log.Info($"update downloaded and checksum verified version={release.Version}");

            var outcome = await Task.Run(() => MacUpdateInstaller.Install(zip, work.FullName));
            switch (outcome.Kind)
            {
                case MacUpdateInstaller.OutcomeKind.Installed:
                    Log.Info($"update installed version={release.Version}");
                    work = null; // the relaunch helper cleans up after us
                    Restart?.Invoke();
                    break;
                case MacUpdateInstaller.OutcomeKind.Manual:
                    work = null; // keep the verified app for the user to drag into place
                    Shell.Reveal(outcome.NewAppPath!);
                    MacDialogs.Show($"PromptForge {release.Version} is ready", outcome.Message, MacDialogs.Style.Informational);
                    break;
            }
        }
        catch (UpdateException ex)
        {
            Log.Warn("update failed: " + ex.Message);
            _services.Notify(ex.Message, NotifyKind.Warning);
        }
        finally
        {
            if (work is not null) TryDelete(work.FullName);
            _busy = false;
        }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => _timer.Stop();
}

/// <summary>
/// Installs a downloaded, checksum-verified PromptForge.app. The checksum only proves the zip is the one
/// published with the release; the code signature proves who built it. The bundle is replaced only when
/// the new app passes a strict codesign check and is signed by the same Apple Developer team, with the
/// same bundle identifier, as the running copy. Unsigned (ad-hoc) builds can't prove that, so the user
/// installs those by hand, exactly as with a manual download.
/// </summary>
internal static class MacUpdateInstaller
{
    public enum OutcomeKind { Installed, Manual }

    public sealed record Outcome(OutcomeKind Kind, string Message, string? NewAppPath = null);

    public static Outcome Install(string zipPath, string workDir)
    {
        var extracted = Path.Combine(workDir, "extracted");
        Directory.CreateDirectory(extracted);
        if (Run("/usr/bin/ditto", "-x", "-k", zipPath, extracted).Exit != 0)
            throw new UpdateException("The update couldn't be unpacked.");

        // Only PromptForge.app (plus the resource-fork folder some zip tools add) may be in the archive.
        var newApp = Path.Combine(extracted, "PromptForge.app");
        var unexpected = Directory.GetFileSystemEntries(extracted).Where(e => e != newApp && Path.GetFileName(e) != "__MACOSX");
        if (unexpected.Any() || !File.Exists(Path.Combine(newApp, "Contents", "MacOS", "PromptForge")))
            throw new UpdateException("The update doesn't contain PromptForge.app, so it wasn't installed.");
        if (new FileInfo(newApp).LinkTarget is not null)
            throw new UpdateException("The update is malformed, so it wasn't installed.");

        var verify = Run("/usr/bin/codesign", "--verify", "--deep", "--strict", newApp);
        if (verify.Exit != 0)
            throw new UpdateException("The update's code signature is invalid, so it wasn't installed.");

        var current = AppPaths.BundlePath;
        var running = current is null ? default : Signature(current);
        var incoming = Signature(newApp);
        const string manual = "It has been checked and is selected in Finder. Quit PromptForge, then drag the new PromptForge.app into your Applications folder and replace the old one.";
        if (current is null || running.Team is null)
            return new Outcome(OutcomeKind.Manual, manual, newApp);
        if (incoming.Team != running.Team || incoming.Identifier != running.Identifier)
            throw new UpdateException("The update isn't signed by the same developer as this copy, so it wasn't installed.");

        var backup = Path.Combine(workDir, "previous.app");
        try
        {
            Directory.Move(current, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"couldn't move the running app aside: {ex.GetType().Name}");
            return new Outcome(OutcomeKind.Manual, manual, newApp);
        }
        try
        {
            Directory.Move(newApp, current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Directory.Move(backup, current); // put the working copy back
            Log.Warn($"couldn't move the new app into place: {ex.GetType().Name}");
            return new Outcome(OutcomeKind.Manual, manual, newApp);
        }

        // Start the new copy once this one has quit, then remove the downloaded files. Paths are passed
        // as arguments ($1, $2), never spliced into the script, so they can't be run as commands.
        var relaunch = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (var arg in new[] { "-c", "sleep 2; /usr/bin/open \"$1\"; /bin/rm -rf \"$2\"", "sh", current, workDir })
            relaunch.ArgumentList.Add(arg);
        using var _ = Process.Start(relaunch);
        return new Outcome(OutcomeKind.Installed, "");
    }

    /// <summary>The signing team and bundle identifier from codesign; Team is null for unsigned or ad-hoc builds.</summary>
    private static (string? Team, string? Identifier) Signature(string app)
    {
        var (exit, _, error) = Run("/usr/bin/codesign", "-dv", "--verbose=2", app);
        if (exit != 0) return (null, null);
        string? team = null, identifier = null;
        foreach (var line in error.Split('\n'))
        {
            if (line.StartsWith("TeamIdentifier=", StringComparison.Ordinal)) team = line["TeamIdentifier=".Length..].Trim();
            else if (line.StartsWith("Identifier=", StringComparison.Ordinal)) identifier = line["Identifier=".Length..].Trim();
        }
        return (team is null or "not set" ? null : team, identifier);
    }

    private static (int Exit, string Output, string Error) Run(string program, params string[] args)
    {
        var info = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new UpdateException($"Couldn't run {Path.GetFileName(program)}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill();
            throw new UpdateException($"{Path.GetFileName(program)} took too long.");
        }
        return (process.ExitCode, output.Result, error.Result);
    }
}
