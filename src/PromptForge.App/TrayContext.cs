using System.Diagnostics;
using PromptForge.App.Flows;
using PromptForge.App.Hotkeys;
using PromptForge.App.Infrastructure;
using PromptForge.App.TextAccess;
using PromptForge.App.UI;
using PromptForge.Core.Llm;
using PromptForge.Core.Optimization;
using PromptForge.Core.Settings;

namespace PromptForge.App;

/// <summary>The app lives in the notification area: no main window, just hotkeys and a tray menu.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly AppServices _services = new();
    private readonly HotkeyManager _hotkeys = new();
    private readonly NotifyIcon _tray;
    private readonly OptimizeController _optimize;
    private readonly VoiceController _voice;
    private readonly UpdateController _updates;
    private readonly ToolStripMenuItem _updateItem = new("Check for updates");
    private Action? _balloonAction;
    private readonly ToolStripMenuItem _pause = new("Pause shortcuts") { CheckOnClick = true };
    private readonly ToolStripMenuItem _restore = new("Copy last original text") { Enabled = false };
    private readonly ToolStripMenuItem _shortcutsInfo = new() { Enabled = false };
    private readonly Dictionary<OptimizeMode, ToolStripMenuItem> _modeItems = [];
    private SettingsForm? _settingsForm;

    public TrayContext(bool startedInBackground)
    {
        _optimize = new OptimizeController(_services);
        _voice = new VoiceController(_services, _optimize);
        _updates = new UpdateController(_services);
        _tray = new NotifyIcon { Icon = AppIcon.Get(), Text = "PromptForge", Visible = true, ContextMenuStrip = BuildMenu() };
        _tray.DoubleClick += (_, _) => OpenSettings();
        _tray.MouseUp += (_, e) =>
        {
            // Left-click opens the menu too (WinForms only does this for right-click).
            if (e.Button == MouseButtons.Left)
                typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(_tray, null);
        };
        _services.Notify = (message, icon) =>
        {
            _balloonAction = null;
            _tray.ShowBalloonTip(4000, "PromptForge", message, icon);
        };
        _services.NotifyClickable = (message, icon, action) =>
        {
            _balloonAction = action;
            _tray.ShowBalloonTip(10000, "PromptForge", message, icon);
        };
        _tray.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonAction;
            _balloonAction = null;
            action?.Invoke();
        };
        _updates.AvailableChanged += () =>
        {
            var release = _updates.Available;
            _updateItem.Text = release is null ? "Check for updates" : $"Update to {release.Version}…";
        };
        _hotkeys.Pressed += OnHotkey;

        RegisterHotkeys(reportProblems: true);
        Log.Info($"started version={Application.ProductVersion} background={startedInBackground}");
        _updates.Start();

        if (!HasUsableKey())
        {
            OpenSettings("Model");
            _services.Notify("Welcome! Add a free Groq API key to start (Settings → Model → Get a key).", ToolTipIcon.Info);
        }
        else if (!startedInBackground)
        {
            _services.Notify($"Running in the tray. Press {_services.Settings.Hotkeys.Optimize} in any text box to optimise it.", ToolTipIcon.Info);
        }
    }

    /// <summary>Called when the user launches PromptForge again while it's already running.</summary>
    public void OnSecondInstance() => OpenSettings();

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("PromptForge") { Enabled = false, Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(_shortcutsInfo);
        menu.Items.Add(new ToolStripSeparator());

        var modes = new ToolStripMenuItem("Default mode");
        foreach (var mode in Enum.GetValues<OptimizeMode>())
        {
            var item = new ToolStripMenuItem(mode.ToString()) { Checked = _services.Settings.DefaultMode == mode };
            item.Click += (_, _) => SetMode(mode);
            _modeItems[mode] = item;
            modes.DropDownItems.Add(item);
        }
        menu.Items.Add(modes);

        _restore.Click += (_, _) =>
        {
            if (_optimize.LastOriginal is { } original)
            {
                ClipboardText.Write(original);
                _services.Notify("Your last original text is on the clipboard.", ToolTipIcon.Info);
            }
        };
        menu.Items.Add(_restore);

        _pause.CheckedChanged += (_, _) =>
        {
            if (_pause.Checked) _hotkeys.UnregisterAll();
            else RegisterHotkeys(reportProblems: true);
            _tray.Text = _pause.Checked ? "PromptForge (paused)" : "PromptForge";
        };
        menu.Items.Add(_pause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        _updateItem.Click += async (_, _) =>
        {
            if (_updates.Available is not null) await _updates.InstallAsync();
            else await _updates.CheckAsync(manual: true);
        };
        menu.Items.Add(_updateItem);
        var developer = new ToolStripMenuItem("Developer");
        developer.DropDownItems.Add("Run quality check on a test set…", null, async (_, _) => await RunEvalAsync());
        menu.Items.Add(developer);
        menu.Items.Add("Open logs folder", null, (_, _) =>
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            Process.Start(new ProcessStartInfo(AppPaths.LogDir) { UseShellExecute = true });
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => _restore.Enabled = _optimize.LastOriginal is not null;
        return menu;
    }

    private bool _evalRunning;

    private async Task RunEvalAsync()
    {
        if (_evalRunning)
        {
            _services.Notify("A quality check is already running.", ToolTipIcon.Info);
            return;
        }
        using var dialog = new OpenFileDialog
        {
            Title = "Choose the test set (eval\\cases.json)",
            Filter = "Test sets (*.json)|*.json",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        var judge = MessageBox.Show(
            $"Also grade each rewrite with the judge model (Settings → Data)? It checks for invented details, dropped requirements, changed intent, added requests and replies written in the AI's voice.\n\nEach case runs {EvalCommand.DefaultRepeat} times so results can be told apart from noise. The judge doubles the number of requests.",
            "PromptForge quality check", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        var outDir = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, "results", DateTime.Now.ToString("yyyyMMdd-HHmm"));
        _evalRunning = true;
        _services.Notify($"Quality check started. Results will be in {outDir}", ToolTipIcon.Info);
        try
        {
            var ok = await EvalCommand.RunAsync(_services, dialog.FileName, outDir, judge, TimeSpan.FromSeconds(8));
            _services.Notify(ok ? "Quality check finished. Open report.md in the results folder." : "Quality check failed; see progress.txt in the results folder.",
                ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
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
        foreach (var (m, item) in _modeItems) item.Checked = m == mode;
    }

    private void RegisterHotkeys(bool reportProblems)
    {
        var hk = _services.Settings.Hotkeys;
        var problems = new List<string>();
        void Try(HotkeyAction action, string chord)
        {
            if (string.IsNullOrWhiteSpace(chord)) return;
            if (_hotkeys.Register(action, chord) is { } problem) problems.Add(problem);
            else if (HotkeyChord.TryParse(chord, out var parsed) && HotkeyManager.AltGrConflict(parsed) is { } conflict) problems.Add(conflict);
        }
        Try(HotkeyAction.Optimize, hk.Optimize);
        Try(HotkeyAction.OptimizeSelection, hk.OptimizeSelection);
        if (_services.Settings.Voice.Enabled) Try(HotkeyAction.Voice, hk.Voice);

        _shortcutsInfo.Text = $"{hk.Optimize}: optimise · {hk.OptimizeSelection}: selection" + (_services.Settings.Voice.Enabled ? $" · {hk.Voice}: voice" : "");
        if (problems.Count > 0)
        {
            Log.Warn("hotkey problems: " + string.Join(" | ", problems));
            if (reportProblems) _services.Notify(string.Join("\n", problems), ToolTipIcon.Warning);
        }
    }

    private async void OnHotkey(HotkeyAction action)
    {
        var chord = _hotkeys.Registered.GetValueOrDefault(action);
        var mainKey = chord is not null && HotkeyManager.TryGetVirtualKey(chord, out var key) ? key : Keys.None;
        switch (action)
        {
            case HotkeyAction.Optimize:
                await _optimize.OnHotkeyAsync(CaptureMode.WholeField, mainKey);
                break;
            case HotkeyAction.OptimizeSelection:
                await _optimize.OnHotkeyAsync(CaptureMode.Selection, mainKey);
                break;
            case HotkeyAction.Voice:
                _voice.OnHotkey(mainKey);
                break;
        }
    }

    private void OpenSettings(string? tab = null)
    {
        if (_settingsForm is { IsDisposed: false })
        {
            if (_settingsForm.WindowState == FormWindowState.Minimized) _settingsForm.WindowState = FormWindowState.Normal;
            _ = Foreground.BringToFrontAsync(_settingsForm.Handle);
            return;
        }
        // Shortcuts are paused while settings are open so pressing one records it instead of triggering it.
        _hotkeys.UnregisterAll();
        _settingsForm = new SettingsForm(_services.Settings, _services.Secrets, _hotkeys, _services.Client, tab, () => _ = RunEvalAsync());
        _settingsForm.FormClosed += (_, _) =>
        {
            if (_settingsForm.DialogResult == DialogResult.OK)
            {
                _services.Settings = _settingsForm.Result;
                _services.SaveSettings();
                Log.Info("settings saved");
            }
            _settingsForm = null;
            if (!_pause.Checked) RegisterHotkeys(reportProblems: true);
        };
        _settingsForm.Show();
        _ = Foreground.BringToFrontAsync(_settingsForm.Handle);
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

    protected override void ExitThreadCore()
    {
        Log.Info("exiting");
        _tray.Visible = false;
        _tray.Dispose();
        _hotkeys.Dispose();
        _updates.Dispose();
        _services.Dispose();
        base.ExitThreadCore();
    }
}
