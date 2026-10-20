using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using PromptForge.Core.Optimization;
using PromptForge.Core.Settings;
using PromptForge.Mac.Flows;
using PromptForge.Mac.Hotkeys;
using PromptForge.Mac.Infrastructure;
using PromptForge.Mac.Native;
using PromptForge.Mac.TextAccess;
using PromptForge.Mac.UI;

namespace PromptForge.Mac;

/// <summary>The app lives in the menu bar: no Dock icon or main window, just shortcuts and a menu.</summary>
internal sealed class MenuBarController : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly AppServices _services = new();
    private readonly HotkeyManager _hotkeys = new();
    private readonly TrayIcon _tray;
    private readonly OptimizeController _optimize;
    private readonly VoiceController _voice;
    private readonly UpdateController _updates;
    private readonly NativeMenuItem _updateItem = new("Check for Updates…");
    private readonly NativeMenuItem _pause = new("Pause Shortcuts") { ToggleType = NativeMenuItemToggleType.CheckBox };
    private readonly NativeMenuItem _restore = new("Copy Last Original Text") { IsEnabled = false };
    private readonly NativeMenuItem _shortcutsInfo = new("") { IsEnabled = false };
    private readonly Dictionary<OptimizeMode, NativeMenuItem> _modeItems = [];
    private SettingsWindow? _settingsWindow;
    private bool _evalRunning;

    public MenuBarController(Application app, IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
        _optimize = new OptimizeController(_services);
        _voice = new VoiceController(_services, _optimize);
        _updates = new UpdateController(_services) { Restart = Quit };
        _tray = new TrayIcon { Icon = AppIcon.Get(), ToolTipText = "PromptForge", Menu = BuildMenu(), IsVisible = true };
        TrayIcon.SetIcons(app, [_tray]);

        _services.Notify = (message, kind) => Toast.Show(message, kind);
        _services.NotifyClickable = (message, kind, action) => Toast.Show(message, kind, action);
        _updates.AvailableChanged += () =>
        {
            var release = _updates.Available;
            _updateItem.Header = release is null ? "Check for Updates…" : $"Update to {release.Version}…";
        };
        _optimize.LastOriginalChanged += () => _restore.IsEnabled = _optimize.LastOriginal is not null;
        _hotkeys.Pressed += OnHotkeyPressed;
        _hotkeys.Released += action => { if (action == HotkeyAction.Voice) _voice.OnReleased(); };

        RegisterHotkeys(reportProblems: true);
        Log.Info($"started version={UpdateController.CurrentVersion} accessibility={AX.IsTrusted}");
        _updates.Start();

        if (!HasUsableKey())
        {
            OpenSettings("Model");
            _services.Notify("Welcome! Add a free Groq API key to start (Settings → Model → Get a key).", NotifyKind.Info);
        }
        if (!AX.IsTrusted)
        {
            // Shows macOS's own "allow accessibility" prompt; needed to copy and paste in other apps.
            AX.PromptForTrust();
        }
    }

    /// <summary>Called when the user opens PromptForge again (Finder, Spotlight) while it's already running.</summary>
    public void OnReopen() => OpenSettings();

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();
        menu.Add(new NativeMenuItem("PromptForge") { IsEnabled = false });
        menu.Add(_shortcutsInfo);
        menu.Add(new NativeMenuItemSeparator());

        var modes = new NativeMenu();
        foreach (var mode in Enum.GetValues<OptimizeMode>())
        {
            var item = new NativeMenuItem(mode.ToString()) { ToggleType = NativeMenuItemToggleType.Radio, IsChecked = _services.Settings.DefaultMode == mode };
            item.Click += (_, _) => SetMode(mode);
            _modeItems[mode] = item;
            modes.Add(item);
        }
        menu.Add(new NativeMenuItem("Default Mode") { Menu = modes });

        _restore.Click += (_, _) =>
        {
            if (_optimize.LastOriginal is { } original)
            {
                Pasteboard.WriteText(original);
                _services.Notify("Your last original text is on the clipboard.", NotifyKind.Info);
            }
        };
        menu.Add(_restore);

        _pause.Click += (_, _) =>
        {
            _pause.IsChecked = !_pause.IsChecked;
            if (_pause.IsChecked) _hotkeys.UnregisterAll();
            else RegisterHotkeys(reportProblems: true);
            _tray.ToolTipText = _pause.IsChecked ? "PromptForge (paused)" : "PromptForge";
        };
        menu.Add(_pause);
        menu.Add(new NativeMenuItemSeparator());

        var settings = new NativeMenuItem("Settings…");
        settings.Click += (_, _) => OpenSettings();
        menu.Add(settings);
        _updateItem.Click += async (_, _) =>
        {
            if (_updates.Available is not null) await _updates.InstallAsync();
            else await _updates.CheckAsync(manual: true);
        };
        menu.Add(_updateItem);

        var quality = new NativeMenuItem("Run Quality Check on a Test Set…");
        quality.Click += async (_, _) => await RunEvalAsync();
        menu.Add(new NativeMenuItem("Developer") { Menu = new NativeMenu { quality } });
        var logs = new NativeMenuItem("Show Logs");
        logs.Click += (_, _) =>
        {
            AppPaths.EnsurePrivateDirectory(AppPaths.LogDir);
            Shell.Open(AppPaths.LogDir);
        };
        menu.Add(logs);
        menu.Add(new NativeMenuItemSeparator());
        var quit = new NativeMenuItem("Quit PromptForge");
        quit.Click += (_, _) => Quit();
        menu.Add(quit);
        return menu;
    }

    private async Task RunEvalAsync()
    {
        if (_evalRunning)
        {
            _services.Notify("A quality check is already running.", NotifyKind.Info);
            return;
        }
        var file = MacDialogs.PickFile("Choose the test set (eval/cases.json)");
        if (file is null) return;
        if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            _services.Notify("Choose a .json test set.", NotifyKind.Info);
            return;
        }
        var judge = MacDialogs.Confirm("Also grade each rewrite with the judge model?",
            $"The judge (Settings → Data) checks for invented details, dropped requirements, changed intent, added requests and replies written in the AI's voice.\n\nEach case runs {EvalCommand.DefaultRepeat} times so results can be told apart from noise. The judge doubles the number of requests.",
            "Use the judge", "Skip");
        var outDir = Path.Combine(Path.GetDirectoryName(file)!, "results", DateTime.Now.ToString("yyyyMMdd-HHmm"));
        _evalRunning = true;
        _services.Notify($"Quality check started. Results will be in {outDir}", NotifyKind.Info);
        try
        {
            var ok = await EvalCommand.RunAsync(_services, file, outDir, judge, TimeSpan.FromSeconds(8));
            _services.Notify(ok ? "Quality check finished. Open report.md in the results folder." : "Quality check failed; see progress.txt in the results folder.",
                ok ? NotifyKind.Info : NotifyKind.Warning);
        }
        finally
        {
            _evalRunning = false;
        }
    }

    private void SetMode(OptimizeMode mode)
    {
        _services.Settings.DefaultMode = mode;
        _services.SaveSettings();
        foreach (var (m, item) in _modeItems) item.IsChecked = m == mode;
    }

    private void RegisterHotkeys(bool reportProblems)
    {
        var hk = _services.Settings.Hotkeys;
        var problems = new List<string>();
        void Try(HotkeyAction action, string chord)
        {
            if (string.IsNullOrWhiteSpace(chord)) return;
            if (_hotkeys.Register(action, chord) is { } problem) problems.Add(problem);
        }
        Try(HotkeyAction.Optimize, hk.Optimize);
        Try(HotkeyAction.OptimizeSelection, hk.OptimizeSelection);
        if (_services.Settings.Voice.Enabled) Try(HotkeyAction.Voice, hk.Voice);

        _shortcutsInfo.Header = $"{MacKeys.Display(hk.Optimize)} optimise · {MacKeys.Display(hk.OptimizeSelection)} selection"
                                + (_services.Settings.Voice.Enabled ? $" · {MacKeys.Display(hk.Voice)} voice" : "");
        if (problems.Count > 0)
        {
            Log.Warn("hotkey problems: " + string.Join(" | ", problems));
            if (reportProblems) _services.Notify(string.Join("\n", problems), NotifyKind.Warning);
        }
    }

    private async void OnHotkeyPressed(HotkeyAction action)
    {
        try
        {
            ushort? mainKey = _hotkeys.Registered.GetValueOrDefault(action) is { } chord && MacKeys.TryGetKeyCode(chord, out var code) ? code : null;
            switch (action)
            {
                case HotkeyAction.Optimize:
                    await _optimize.OnHotkeyAsync(CaptureMode.WholeField, mainKey);
                    break;
                case HotkeyAction.OptimizeSelection:
                    await _optimize.OnHotkeyAsync(CaptureMode.Selection, mainKey);
                    break;
                case HotkeyAction.Voice:
                    _voice.OnPressed();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("hotkey handling failed", ex);
        }
    }

    private void OpenSettings(string? tab = null)
    {
        ObjC.ActivateSelf();
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        // Shortcuts are paused while settings are open so pressing one records it instead of triggering it.
        _hotkeys.UnregisterAll();
        _settingsWindow = new SettingsWindow(_services.Settings, _services.Secrets, _hotkeys, _services.Client, tab, () => _ = RunEvalAsync(), DeleteAllData);
        _settingsWindow.Closed += (_, _) =>
        {
            if (_settingsWindow?.Result is { } result)
            {
                _services.Settings = result;
                _services.SaveSettings();
                Log.Info("settings saved");
            }
            _settingsWindow = null;
            if (!_pause.IsChecked) RegisterHotkeys(reportProblems: true);
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void DeleteAllData()
    {
        _services.Secrets.Clear();
        LoginItem.Set(false);
        Log.Disabled = true;
        foreach (var dir in new[] { AppPaths.DataDir, AppPaths.LogDir })
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        // Don't let a later settings save recreate the folder.
        _settingsWindow = null;
        _lifetime.Shutdown();
    }

    private bool HasUsableKey()
    {
        try
        {
            return EndpointResolver.Resolve(_services.Settings, _services.Secrets).Count > 0;
        }
        catch (EndpointConfigException)
        {
            return false;
        }
    }

    private void Quit() => _lifetime.Shutdown();

    public void Dispose()
    {
        Log.Info("exiting");
        _tray.IsVisible = false;
        _tray.Dispose();
        _hotkeys.Dispose();
        _updates.Dispose();
        _services.Dispose();
    }
}
