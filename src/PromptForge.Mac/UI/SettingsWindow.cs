using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PromptForge.Core.Llm;
using PromptForge.Core.Optimization;
using PromptForge.Core.Settings;
using PromptForge.Mac.Hotkeys;
using PromptForge.Mac.Infrastructure;
using PromptForge.Mac.Native;

namespace PromptForge.Mac.UI;

/// <summary>Settings window. Works on a copy; the caller applies the result only when the user clicks Save.</summary>
internal sealed class SettingsWindow : Window
{
    private static readonly string[] KeyProviders = ["groq", "gemini", "custom"];
    private static readonly IBrush Grey = new SolidColorBrush(Color.FromRgb(130, 130, 138));

    private readonly AppSettings _s;
    private readonly ISecretStore _secrets;
    private readonly HotkeyManager _hotkeys;
    private readonly ILlmClient _client;

    // General
    private readonly ComboBox _defaultMode = Combo(Enum.GetNames<OptimizeMode>());
    private readonly CheckBox _review = Check("Show the review window before replacing text (recommended)");
    private readonly CheckBox _startup = Check("Open PromptForge when you log in");
    private readonly CheckBox _appHint = Check("Tell the optimiser what kind of app I'm typing in (e.g. \"Claude (browser)\")");
    private readonly CheckBox _updates = Check("Check for updates once a day (contacts GitHub; sends no prompt text, settings or keys)");
    private readonly CheckBox _repair = Check("If exact details (paths, numbers, names) get dropped, ask the model to fix it (one extra call)");
    private readonly TextBox _personal = new() { AcceptsReturn = true, Height = 80, TextWrapping = TextWrapping.Wrap, Watermark = "e.g. Year 3 computer science student. I mostly use Swift, Java and Python on a Mac." };
    private readonly TextBlock _permission = Hint("");

    // Model
    private readonly ComboBox _primaryProvider = ProviderCombo();
    private readonly TextBox _primaryModel = new() { Width = 260 };
    private readonly TextBox _primaryUrl = new() { Width = 360, Watermark = "Base URL (custom/Ollama only)" };
    private readonly CheckBox _fallbackOn = Check("Use a fallback when the main model is rate-limited or down");
    private readonly ComboBox _fallbackProvider = ProviderCombo();
    private readonly TextBox _fallbackModel = new() { Width = 260 };
    private readonly TextBox _fallbackUrl = new() { Width = 360, Watermark = "Base URL (custom/Ollama only)" };
    private readonly Dictionary<string, TextBox> _keys = [];
    // Hosted providers only: there's no base-URL box for the judge. A custom or local judge can still be set in settings.json.
    private readonly ComboBox _judgeProvider = Combo([ProviderPresets.Gemini.Id, ProviderPresets.Groq.Id]);
    private readonly TextBox _judgeModel = new() { Width = 260 };
    private readonly TextBlock _testResult = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520 };
    private readonly TextBlock _providerNotes = Hint("");

    // Hotkeys
    private readonly HotkeyBox _hkOptimize = new();
    private readonly HotkeyBox _hkSelection = new();
    private readonly HotkeyBox _hkVoice = new();

    // Voice
    private readonly CheckBox _voiceOn = Check("Enable voice dictation");
    private readonly ComboBox _voiceProvider = Combo(["groq", "custom"]);
    private readonly TextBox _voiceModel = new() { Width = 260 };
    private readonly TextBox _voiceLanguage = new() { Width = 70, Watermark = "auto" };
    private readonly CheckBox _voiceAuto = Check("After dictating, send the text to the optimiser and review it (instead of typing it in)");
    private readonly NumericUpDown _voiceMax = new() { Minimum = 10, Maximum = 600, Increment = 10, FormatString = "0", Width = 130 };

    private readonly Action? _runQualityCheck;
    private readonly Action _deleteAllData;

    public SettingsWindow(AppSettings current, ISecretStore secrets, HotkeyManager hotkeys, ILlmClient client, string? initialTab, Action? runQualityCheck, Action deleteAllData)
    {
        _runQualityCheck = runQualityCheck;
        _deleteAllData = deleteAllData;
        _s = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(current, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
        _secrets = secrets;
        _hotkeys = hotkeys;
        _client = client;

        Title = "PromptForge settings";
        Icon = AppIcon.Get();
        Width = 780;
        Height = 660;
        MinWidth = 700;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var pages = new[]
        {
            new TabItem { Header = "General", Content = Page(BuildGeneral()) },
            new TabItem { Header = "Model", Content = Page(BuildModel()) },
            new TabItem { Header = "Shortcuts", Content = Page(BuildHotkeys()) },
            new TabItem { Header = "Voice", Content = Page(BuildVoice()) },
            new TabItem { Header = "Data", Content = Page(BuildData()) },
        };
        var tabs = new TabControl { ItemsSource = pages, Margin = new Thickness(8, 4) };
        if (initialTab is not null) tabs.SelectedItem = pages.FirstOrDefault(p => (string?)p.Header == initialTab) ?? pages[0];

        var save = new Button { Content = "Save", Classes = { "accent" }, IsDefault = false };
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close();
        save.Click += (_, _) => Save();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(12),
            Children = { cancel, save },
        };
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(tabs);
        Content = root;

        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && e.Source is not HotkeyBox) { Close(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        LoadValues();
    }

    /// <summary>The edited settings, set only when the user saved.</summary>
    public AppSettings? Result { get; private set; }

    private Control BuildGeneral()
    {
        var grantAccess = new Button { Content = "Open Accessibility settings" };
        grantAccess.Click += (_, _) =>
        {
            AX.PromptForTrust();
            Shell.Open("x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility");
            UpdatePermission();
        };
        return Stack(
            Row(Label("Default mode", 100), _defaultMode),
            Hint("Light = minimal edits · Standard = balanced · Detailed = full structure for complex tasks"),
            _review, _startup, _appHint, _repair, _updates,
            Label("About me (optional, used only when relevant; never used to invent task details):", 680),
            _personal,
            Heading("Permissions"),
            _permission,
            grantAccess);
    }

    private Control BuildModel()
    {
        var keyRows = new List<Control> { Heading("API keys (stored in your macOS Keychain; never written to settings or logs)") };
        foreach (var id in KeyProviders)
        {
            var preset = ProviderPresets.Get(id);
            var box = new TextBox { Width = 280, PasswordChar = '•' };
            _keys[id] = box;
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) =>
            {
                _secrets.Set(id, null);
                box.Text = "";
                box.Watermark = "not set";
            };
            var controls = new List<Control> { Label(preset.DisplayName, 200), box, remove };
            if (preset.KeyUrl is { } url)
            {
                var link = new Button { Content = "Get a key", Classes = { "link" }, Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(109, 74, 255)) };
                link.Click += (_, _) => Shell.Open(url);
                controls.Add(link);
            }
            keyRows.Add(Row([.. controls]));
        }

        var test = new Button { Content = "Test connection" };
        test.Click += async (_, _) => await TestAsync(test);

        _primaryProvider.SelectionChanged += (_, _) => OnProviderChanged(_primaryProvider, _primaryModel, fallback: false);
        _fallbackProvider.SelectionChanged += (_, _) => OnProviderChanged(_fallbackProvider, _fallbackModel, fallback: true);

        return Stack(
            Heading("Main model"),
            Row(Label("Provider"), _primaryProvider, Label("Model"), _primaryModel),
            Row(Label(""), _primaryUrl),
            _providerNotes,
            _fallbackOn,
            Row(Label("Provider"), _fallbackProvider, Label("Model"), _fallbackModel),
            Row(Label(""), _fallbackUrl),
            Stack([.. keyRows]),
            Row(test, _testResult));
    }

    private Control BuildHotkeys() => Stack(
        Heading("Click a box, then press the shortcut you want. Shortcuts must include Control (⌃) or Command (⌘)."),
        Row(Label("Optimise the text box", 220), _hkOptimize),
        Hint("Selects everything in the text box you're typing in, optimises it, and replaces it when you accept."),
        Row(Label("Optimise selected text", 220), _hkSelection),
        Hint("Only the selected text. Use this in documents and code editors, where select-all would take the whole file."),
        Row(Label("Voice (tap to toggle, or hold)", 220), _hkVoice),
        Hint("Tap once to start and again to stop, or hold the keys while you talk and release to finish."),
        Hint("In terminals, PromptForge never sends keystrokes: copy your prompt first, press the optimise shortcut, then paste the result."));

    private Control BuildVoice() => Stack(
        _voiceOn,
        Row(Label("Provider"), _voiceProvider, Label("Model"), _voiceModel),
        Hint("Groq's free plan includes Whisper transcription (2,000 requests/day) with the same API key."),
        Row(Label("Language"), _voiceLanguage, Hint("ISO code such as en or ms; leave empty to auto-detect")),
        Row(Label("Stop after"), _voiceMax, Label("seconds")),
        _voiceAuto,
        Hint("macOS also has free built-in Dictation (press the microphone key, or Fn twice) that works in most text boxes, if you'd rather not send audio to a cloud service."));

    private Control BuildData()
    {
        var openData = new Button { Content = "Show data folder" };
        openData.Click += (_, _) => { AppPaths.EnsurePrivateDirectory(AppPaths.DataDir); Shell.Open(AppPaths.DataDir); };
        var openLogs = new Button { Content = "Show logs" };
        openLogs.Click += (_, _) => { AppPaths.EnsurePrivateDirectory(AppPaths.LogDir); Shell.Open(AppPaths.LogDir); };
        var wipe = new Button { Content = "Delete all PromptForge data…" };
        wipe.Click += (_, _) =>
        {
            if (!MacDialogs.Confirm("Delete your settings, saved API keys and logs?", "PromptForge will quit. Your API keys are removed from the Keychain.",
                    "Delete", "Cancel", MacDialogs.Style.Critical)) return;
            _deleteAllData();
        };
        var quality = new Button { Content = "Run quality check on a test set…", IsEnabled = _runQualityCheck is not null };
        quality.Click += (_, _) => _runQualityCheck?.Invoke();
        _judgeProvider.SelectionChanged += (_, _) =>
        {
            var preset = ProviderPresets.Get((string?)_judgeProvider.SelectedItem);
            var known = ProviderPresets.All.SelectMany(p => new[] { p.DefaultModel, p.DefaultFallbackModel }).ToHashSet();
            if (string.IsNullOrWhiteSpace(_judgeModel.Text) || known.Contains(_judgeModel.Text)) _judgeModel.Text = preset.DefaultModel;
        };
        return Stack(
            Heading("Quality check"),
            Hint("Runs the optimiser over a set of test prompts (the repo's eval/cases.json) with your saved key and writes a report next to it. Each prompt runs 3 times, so a full run takes about an hour on the free tier."),
            Row(Label("Judge"), _judgeProvider, Label("Model"), _judgeModel),
            Hint("Grades each rewrite. Pick a different provider from the main model, so it isn't grading its own model family, and save that provider's key in the Model tab. Without a key, the main model's fallback grades instead and the report says it isn't independent."),
            quality,
            Heading("What PromptForge stores"),
            Hint("Settings: ~/Library/Application Support/PromptForge/settings.json, readable only by your account. API keys: your login Keychain."),
            Hint("Logs: ~/Library/Logs/PromptForge. Events, timings and error types for 7 days. Never prompt text, transcripts or keys."),
            Hint("No prompt history is kept. Only the last original text is held in memory so you can restore it from the menu bar."),
            Heading("What leaves your Mac"),
            Hint("Only when you press a shortcut: the text you chose to optimise goes to the model provider you configured, and dictated audio goes to the voice provider. Free tiers may use data to improve their services (Google says so for Gemini's free tier)."),
            Row(openData, openLogs, wipe));
    }

    private void LoadValues()
    {
        _defaultMode.SelectedItem = _s.DefaultMode.ToString();
        _review.IsChecked = _s.ReviewBeforeReplace;
        _startup.IsChecked = LoginItem.IsEnabled();
        _appHint.IsChecked = _s.SendAppHint;
        _repair.IsChecked = _s.RepairDroppedDetails;
        _updates.IsChecked = _s.CheckForUpdates;
        _personal.Text = _s.PersonalContext;
        UpdatePermission();

        _primaryProvider.SelectedItem = ProviderPresets.Get(_s.Primary.Provider).Id;
        _primaryModel.Text = _s.Primary.Model;
        _primaryUrl.Text = _s.Primary.BaseUrl ?? "";
        _fallbackOn.IsChecked = _s.Fallback.Enabled;
        _fallbackProvider.SelectedItem = ProviderPresets.Get(_s.Fallback.Provider).Id;
        _fallbackModel.Text = _s.Fallback.Model;
        _fallbackUrl.Text = _s.Fallback.BaseUrl ?? "";
        foreach (var (id, box) in _keys) box.Watermark = _secrets.Get(id) is null ? "not set" : "saved (type to replace)";
        _judgeProvider.SelectedItem = _s.Judge.Provider is "gemini" or "groq" ? _s.Judge.Provider : ProviderPresets.Gemini.Id;
        _judgeModel.Text = _s.Judge.Model;
        UpdateUrlBoxes();

        _hkOptimize.Chord = _s.Hotkeys.Optimize;
        _hkSelection.Chord = _s.Hotkeys.OptimizeSelection;
        _hkVoice.Chord = _s.Hotkeys.Voice;

        _voiceOn.IsChecked = _s.Voice.Enabled;
        _voiceProvider.SelectedItem = _s.Voice.Provider == "custom" ? "custom" : "groq";
        _voiceModel.Text = _s.Voice.Model;
        _voiceLanguage.Text = _s.Voice.Language;
        _voiceAuto.IsChecked = _s.Voice.AutoOptimize;
        _voiceMax.Value = Math.Clamp(_s.Voice.MaxSeconds, 10, 600);
    }

    private void UpdatePermission() =>
        _permission.Text = AX.IsTrusted
            ? "✓ Accessibility is allowed, so PromptForge can copy and paste in other apps."
            : "Accessibility isn't allowed yet. PromptForge needs it to copy your text and paste the result (System Settings → Privacy & Security → Accessibility).";

    private void OnProviderChanged(ComboBox provider, TextBox model, bool fallback)
    {
        var preset = ProviderPresets.Get((string?)provider.SelectedItem);
        var known = ProviderPresets.All.SelectMany(p => new[] { p.DefaultModel, p.DefaultFallbackModel }).ToHashSet();
        if (string.IsNullOrWhiteSpace(model.Text) || known.Contains(model.Text))
            model.Text = fallback ? preset.DefaultFallbackModel : preset.DefaultModel;
        UpdateUrlBoxes();
    }

    private void UpdateUrlBoxes()
    {
        _primaryUrl.IsEnabled = (string?)_primaryProvider.SelectedItem is "custom" or "ollama";
        _fallbackUrl.IsEnabled = (string?)_fallbackProvider.SelectedItem is "custom" or "ollama";
        _providerNotes.Text = ProviderPresets.Get((string?)_primaryProvider.SelectedItem).Notes.Replace("on your PC", "on your Mac");
    }

    private void ApplyValues()
    {
        _s.DefaultMode = Enum.Parse<OptimizeMode>((string)_defaultMode.SelectedItem!);
        _s.ReviewBeforeReplace = _review.IsChecked == true;
        _s.SendAppHint = _appHint.IsChecked == true;
        _s.RepairDroppedDetails = _repair.IsChecked == true;
        _s.CheckForUpdates = _updates.IsChecked == true;
        _s.PersonalContext = (_personal.Text ?? "").Trim();
        _s.Primary = new EndpointSettings { Provider = (string)_primaryProvider.SelectedItem!, Model = (_primaryModel.Text ?? "").Trim(), BaseUrl = NullIfEmpty(_primaryUrl.Text) };
        _s.Fallback = new EndpointSettings { Provider = (string)_fallbackProvider.SelectedItem!, Model = (_fallbackModel.Text ?? "").Trim(), BaseUrl = NullIfEmpty(_fallbackUrl.Text), Enabled = _fallbackOn.IsChecked == true };
        _s.Judge = new EndpointSettings
        {
            Provider = (string)_judgeProvider.SelectedItem!,
            Model = (_judgeModel.Text ?? "").Trim(),
        };
        _s.Hotkeys = new HotkeySettings { Optimize = _hkOptimize.Chord, OptimizeSelection = _hkSelection.Chord, Voice = _hkVoice.Chord };
        _s.Voice = new VoiceSettings
        {
            Enabled = _voiceOn.IsChecked == true,
            Provider = (string)_voiceProvider.SelectedItem!,
            Model = (_voiceModel.Text ?? "").Trim(),
            BaseUrl = (string)_voiceProvider.SelectedItem! == "custom" ? NullIfEmpty(_primaryUrl.Text) : null,
            Language = (_voiceLanguage.Text ?? "").Trim(),
            AutoOptimize = _voiceAuto.IsChecked == true,
            MaxSeconds = (int)(_voiceMax.Value ?? 120),
        };
        _s.FirstRunComplete = true;
    }

    private void Save()
    {
        ApplyValues();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(_s.Primary.Model)) errors.Add("Enter a model name for the main model.");
        foreach (var endpoint in new[] { _s.Primary, _s.Fallback }.Where(e => e.Enabled && e.Provider is "custom" or "ollama" && e.BaseUrl is not null))
        {
            var hasKey = !string.IsNullOrWhiteSpace(_keys.GetValueOrDefault(endpoint.Provider)?.Text) || _secrets.Get(endpoint.Provider) is not null;
            if (EndpointResolver.UrlProblem(endpoint.BaseUrl!, hasKey) is { } problem) errors.Add(problem);
        }
        var chords = new[] { ("Optimise", _s.Hotkeys.Optimize), ("Optimise selection", _s.Hotkeys.OptimizeSelection), ("Voice", _s.Hotkeys.Voice) };
        foreach (var (name, text) in chords.Where(c => !string.IsNullOrWhiteSpace(c.Item2)))
        {
            if (_hotkeys.Probe(text) is { } problem) errors.Add($"{name}: {problem}");
        }
        var duplicates = chords.Where(c => !string.IsNullOrWhiteSpace(c.Item2)).GroupBy(c => c.Item2, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1);
        foreach (var d in duplicates) errors.Add($"{MacKeys.Display(d.Key)} is assigned to more than one action.");
        if (errors.Count > 0)
        {
            MacDialogs.Show("Some settings need fixing", string.Join("\n", errors), MacDialogs.Style.Warning);
            return;
        }

        try
        {
            foreach (var (id, box) in _keys.Where(k => !string.IsNullOrWhiteSpace(k.Value.Text)))
                _secrets.Set(id, box.Text!.Trim());
        }
        catch (IOException ex)
        {
            Log.Error("saving a key failed", ex);
            MacDialogs.Show("Couldn't save your API key", ex.Message, MacDialogs.Style.Warning);
            return;
        }
        if (LoginItem.Set(_startup.IsChecked == true) is { } loginProblem)
            MacDialogs.Show("Couldn't change Open at Login", loginProblem, MacDialogs.Style.Warning);
        Result = _s;
        Close();
    }

    private async Task TestAsync(Button button)
    {
        ApplyValues();
        // Use keys typed but not yet saved.
        var typed = new OverlaySecrets(_secrets, _keys.Where(k => !string.IsNullOrWhiteSpace(k.Value.Text)).ToDictionary(k => k.Key, k => k.Value.Text!.Trim()));
        button.IsEnabled = false;
        _testResult.Foreground = null;
        _testResult.Text = "Testing…";
        try
        {
            var endpoint = EndpointResolver.Resolve(_s, typed).First();
            var sw = Stopwatch.StartNew();
            await _client.CompleteAsync(endpoint,
                [new ChatMessage("user", "Reply with the single word OK.")],
                new ChatOptions { MaxTokens = 300, JsonMode = false, Mode = OptimizeMode.Light, Timeout = TimeSpan.FromSeconds(20) },
                CancellationToken.None);
            _testResult.Foreground = new SolidColorBrush(Color.FromRgb(38, 150, 80));
            _testResult.Text = $"Connected to {endpoint.Label} in {sw.ElapsedMilliseconds} ms.";
        }
        catch (Exception ex) when (ex is LlmException or EndpointConfigException or InvalidOperationException)
        {
            _testResult.Foreground = new SolidColorBrush(Color.FromRgb(200, 60, 60));
            _testResult.Text = ex is LlmException llm ? $"{llm.UserMessage} ({llm.Message})" : ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static ComboBox Combo(string[] items) => new() { ItemsSource = items, Width = 140 };

    private static ComboBox ProviderCombo() => Combo(ProviderPresets.All.Select(p => p.Id).ToArray());

    private static CheckBox Check(string text) => new() { Content = text };

    private static TextBlock Label(string text, double width = 70) =>
        new() { Text = text, Width = width, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 2), TextWrapping = TextWrapping.Wrap, MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left };

    private static TextBlock Hint(string text) =>
        new() { Text = text, Foreground = Grey, TextWrapping = TextWrapping.Wrap, MaxWidth = 680, Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };

    private static StackPanel Row(params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2) };
        row.Children.AddRange(controls);
        return row;
    }

    private static StackPanel Stack(params Control[] controls)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.AddRange(controls);
        return stack;
    }

    private static ScrollViewer Page(Control content) => new() { Content = new Border { Padding = new Thickness(12), Child = content } };

    private sealed class OverlaySecrets(ISecretStore inner, Dictionary<string, string> overrides) : ISecretStore
    {
        public string? Get(string providerId) => overrides.TryGetValue(providerId, out var v) ? v : inner.Get(providerId);
        public void Set(string providerId, string? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
    }
}
