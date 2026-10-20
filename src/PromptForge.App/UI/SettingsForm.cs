using System.Diagnostics;
using System.Text.Json;
using PromptForge.App.Hotkeys;
using PromptForge.App.Infrastructure;
using PromptForge.Core.Llm;
using PromptForge.Core.Optimization;
using PromptForge.Core.Settings;

namespace PromptForge.App.UI;

/// <summary>Settings window. Works on a copy; the caller applies the result only when the user clicks Save.</summary>
internal sealed class SettingsForm : Form
{
    private static readonly string[] KeyProviders = ["groq", "gemini", "custom"];

    private readonly AppSettings _s;
    private readonly ISecretStore _secrets;
    private readonly HotkeyManager _hotkeys;
    private readonly ILlmClient _client;

    // General
    private readonly ComboBox _defaultMode = Combo(Enum.GetNames<OptimizeMode>());
    private readonly CheckBox _review = new() { Text = "Show the review window before replacing text (recommended)", AutoSize = true };
    private readonly CheckBox _startup = new() { Text = "Start PromptForge when Windows starts", AutoSize = true };
    private readonly CheckBox _appHint = new() { Text = "Tell the optimiser what kind of app I'm typing in (e.g. \"Claude (browser)\")", AutoSize = true };
    private readonly CheckBox _updates = new() { Text = "Check for updates once a day (contacts GitHub; sends no prompt text, settings or keys)", AutoSize = true };
    private readonly CheckBox _repair = new() { Text = "If exact details (paths, numbers, names) get dropped, ask the model to fix it (one extra call)", AutoSize = true };
    private readonly TextBox _personal = new() { Multiline = true, Height = 70, Width = 560, ScrollBars = ScrollBars.Vertical, PlaceholderText = "e.g. Year 3 computer science student. I mostly use C#, Java and Python on Windows." };

    // Model
    private readonly ComboBox _primaryProvider = ProviderCombo();
    private readonly TextBox _primaryModel = new() { Width = 260 };
    private readonly TextBox _primaryUrl = new() { Width = 360, PlaceholderText = "Base URL (custom/Ollama only)" };
    private readonly CheckBox _fallbackOn = new() { Text = "Use a fallback when the main model is rate-limited or down", AutoSize = true };
    private readonly ComboBox _fallbackProvider = ProviderCombo();
    private readonly TextBox _fallbackModel = new() { Width = 260 };
    private readonly TextBox _fallbackUrl = new() { Width = 360, PlaceholderText = "Base URL (custom/Ollama only)" };
    private readonly Dictionary<string, TextBox> _keys = [];
    // Hosted providers only: there's no base-URL box for the judge. A custom or local judge can still be set in settings.json.
    private readonly ComboBox _judgeProvider = Combo([ProviderPresets.Gemini.Id, ProviderPresets.Groq.Id]);
    private readonly TextBox _judgeModel = new() { Width = 260 };
    private readonly Label _testResult = new() { AutoSize = true, MaximumSize = new Size(680, 0) };
    private readonly Label _providerNotes = new() { AutoSize = true, MaximumSize = new Size(680, 0), ForeColor = SystemColors.GrayText };

    // Hotkeys
    private readonly HotkeyBox _hkOptimize = new() { Width = 200 };
    private readonly HotkeyBox _hkSelection = new() { Width = 200 };
    private readonly HotkeyBox _hkVoice = new() { Width = 200 };

    // Voice
    private readonly CheckBox _voiceOn = new() { Text = "Enable voice dictation", AutoSize = true };
    private readonly ComboBox _voiceProvider = Combo(["groq", "custom"]);
    private readonly TextBox _voiceModel = new() { Width = 260 };
    private readonly TextBox _voiceLanguage = new() { Width = 60, PlaceholderText = "auto" };
    private readonly CheckBox _voiceAuto = new() { Text = "After dictating, send the text to the optimiser and review it (instead of typing it in)", AutoSize = true };
    private readonly NumericUpDown _voiceMax = new() { Minimum = 10, Maximum = 600, Width = 70 };

    private readonly Action? _runQualityCheck;

    public SettingsForm(AppSettings current, ISecretStore secrets, HotkeyManager hotkeys, ILlmClient client, string? initialTab = null, Action? runQualityCheck = null)
    {
        _runQualityCheck = runQualityCheck;
        _s = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(current, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
        _secrets = secrets;
        _hotkeys = hotkeys;
        _client = client;

        Text = "PromptForge settings";
        Icon = AppIcon.Get();
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(780, 660);
        MinimumSize = new Size(700, 560);
        Font = new Font("Segoe UI", 9.5f);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page("General", BuildGeneral()));
        tabs.TabPages.Add(Page("Model", BuildModel()));
        tabs.TabPages.Add(Page("Shortcuts", BuildHotkeys()));
        tabs.TabPages.Add(Page("Voice", BuildVoice()));
        tabs.TabPages.Add(Page("Data", BuildData()));
        if (initialTab is not null)
            tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().FirstOrDefault(p => p.Text == initialTab) ?? tabs.TabPages[0];

        var save = new Button { Text = "Save", AutoSize = true };
        // Settings is shown modeless (Show, not ShowDialog), so a button's DialogResult alone
        // doesn't close it: Cancel and Esc must close explicitly.
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        save.Click += (_, _) => Save();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        buttons.Controls.AddRange([cancel, save]);
        CancelButton = cancel;

        Controls.Add(tabs);
        Controls.Add(buttons);
        LoadValues();
    }

    public AppSettings Result => _s;

    private Control BuildGeneral() => Stack(
        Row(new Label { Text = "Default mode", AutoSize = true, Padding = new Padding(0, 4, 0, 0) }, _defaultMode,
            new Label { Text = "Light = minimal edits · Standard = balanced · Detailed = full structure for complex tasks", AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 4, 0, 0) }),
        _review, _startup, _appHint, _repair, _updates,
        new Label { Text = "About me (optional, used only when relevant; never used to invent task details):", AutoSize = true, Padding = new Padding(0, 10, 0, 0) },
        _personal);

    private Control BuildModel()
    {
        var keyRows = new List<Control> { Heading("API keys (stored encrypted with Windows DPAPI; never written to settings or logs)") };
        foreach (var id in KeyProviders)
        {
            var preset = ProviderPresets.Get(id);
            var box = new TextBox { Width = 280, UseSystemPasswordChar = true };
            _keys[id] = box;
            var remove = new Button { Text = "Remove", AutoSize = true };
            remove.Click += (_, _) => { _secrets.Set(id, null); box.Text = ""; box.PlaceholderText = "not set"; };
            var controls = new List<Control> { new Label { Text = preset.DisplayName, Width = 200, Padding = new Padding(0, 4, 0, 0) }, box, remove };
            if (preset.KeyUrl is { } url)
            {
                var link = new LinkLabel { Text = "Get a key", AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
                link.LinkClicked += (_, _) => OpenUrl(url);
                controls.Add(link);
            }
            keyRows.Add(Row([.. controls]));
        }

        var test = new Button { Text = "Test connection", AutoSize = true };
        test.Click += async (_, _) => await TestAsync(test);

        _primaryProvider.SelectedIndexChanged += (_, _) => OnProviderChanged(_primaryProvider, _primaryModel, _primaryUrl, fallback: false);
        _fallbackProvider.SelectedIndexChanged += (_, _) => OnProviderChanged(_fallbackProvider, _fallbackModel, _fallbackUrl, fallback: true);

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
        Heading("Click a box, then press the shortcut you want. Shortcuts must include Ctrl or Alt."),
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
        Row(Label("Language"), _voiceLanguage, new Label { Text = "ISO code such as en or ms; leave empty to auto-detect", AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 4, 0, 0) }),
        Row(Label("Stop after"), _voiceMax, new Label { Text = "seconds", AutoSize = true, Padding = new Padding(0, 4, 0, 0) }),
        _voiceAuto,
        Hint("Windows also has free built-in voice typing (Win+H) that works in most text boxes, if you'd rather not send audio to a cloud service."));

    private Control BuildData()
    {
        var openData = new Button { Text = "Open data folder", AutoSize = true };
        openData.Click += (_, _) => { Directory.CreateDirectory(AppPaths.DataDir); OpenUrl(AppPaths.DataDir); };
        var openLogs = new Button { Text = "Open logs", AutoSize = true };
        openLogs.Click += (_, _) => { Directory.CreateDirectory(AppPaths.LogDir); OpenUrl(AppPaths.LogDir); };
        var wipe = new Button { Text = "Delete all PromptForge data…", AutoSize = true };
        wipe.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Delete your settings, saved API keys and logs? PromptForge will close.", "PromptForge",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            _secrets.Clear();
            StartupRegistration.Set(false);
            try { Directory.Delete(AppPaths.DataDir, recursive: true); } catch (IOException) { }
            Application.Exit();
        };
        var quality = new Button { Text = "Run quality check on a test set…", AutoSize = true, Enabled = _runQualityCheck is not null };
        quality.Click += (_, _) => _runQualityCheck?.Invoke();
        _judgeProvider.SelectedIndexChanged += (_, _) =>
        {
            var preset = ProviderPresets.Get((string?)_judgeProvider.SelectedItem);
            var known = ProviderPresets.All.SelectMany(p => new[] { p.DefaultModel, p.DefaultFallbackModel }).ToHashSet();
            if (string.IsNullOrWhiteSpace(_judgeModel.Text) || known.Contains(_judgeModel.Text)) _judgeModel.Text = preset.DefaultModel;
        };
        return Stack(
            Heading("Quality check"),
            Hint("Runs the optimiser over a set of test prompts (the repo's eval\\cases.json) with your saved key and writes a report next to it. Each prompt runs 3 times, so a full run takes about an hour on the free tier."),
            Row(Label("Judge"), _judgeProvider, Label("Model"), _judgeModel),
            Hint("Grades each rewrite. Pick a different provider from the main model, so it isn't grading its own model family, and save that provider's key in the Model tab. Without a key, the main model's fallback grades instead and the report says it isn't independent."),
            quality,
            Heading("What PromptForge stores"),
            Hint("Settings: %APPDATA%\\PromptForge\\settings.json. API keys: secrets.dat, encrypted for your Windows account."),
            Hint("Logs: events, timings and error types for 7 days. Never prompt text, transcripts or keys."),
            Hint("No prompt history is kept. Only the last original text is held in memory so you can restore it from the tray menu."),
            Heading("What leaves your PC"),
            Hint("Only when you press a shortcut: the text you chose to optimise goes to the model provider you configured, and dictated audio goes to the voice provider. Free tiers may use data to improve their services (Google says so for Gemini's free tier)."),
            Row(openData, openLogs, wipe));
    }

    private void LoadValues()
    {
        _defaultMode.SelectedItem = _s.DefaultMode.ToString();
        _review.Checked = _s.ReviewBeforeReplace;
        _startup.Checked = StartupRegistration.IsEnabled();
        _appHint.Checked = _s.SendAppHint;
        _repair.Checked = _s.RepairDroppedDetails;
        _updates.Checked = _s.CheckForUpdates;
        _personal.Text = _s.PersonalContext;

        _primaryProvider.SelectedItem = ProviderPresets.Get(_s.Primary.Provider).Id;
        _primaryModel.Text = _s.Primary.Model;
        _primaryUrl.Text = _s.Primary.BaseUrl ?? "";
        _fallbackOn.Checked = _s.Fallback.Enabled;
        _fallbackProvider.SelectedItem = ProviderPresets.Get(_s.Fallback.Provider).Id;
        _fallbackModel.Text = _s.Fallback.Model;
        _fallbackUrl.Text = _s.Fallback.BaseUrl ?? "";
        foreach (var (id, box) in _keys) box.PlaceholderText = _secrets.Get(id) is null ? "not set" : "saved (type to replace)";
        _judgeProvider.SelectedItem = _s.Judge.Provider is "gemini" or "groq" ? _s.Judge.Provider : ProviderPresets.Gemini.Id;
        _judgeModel.Text = _s.Judge.Model;
        UpdateUrlBoxes();

        _hkOptimize.Text = _s.Hotkeys.Optimize;
        _hkSelection.Text = _s.Hotkeys.OptimizeSelection;
        _hkVoice.Text = _s.Hotkeys.Voice;

        _voiceOn.Checked = _s.Voice.Enabled;
        _voiceProvider.SelectedItem = _s.Voice.Provider == "custom" ? "custom" : "groq";
        _voiceModel.Text = _s.Voice.Model;
        _voiceLanguage.Text = _s.Voice.Language;
        _voiceAuto.Checked = _s.Voice.AutoOptimize;
        _voiceMax.Value = Math.Clamp(_s.Voice.MaxSeconds, 10, 600);
    }

    private void OnProviderChanged(ComboBox provider, TextBox model, TextBox url, bool fallback)
    {
        var preset = ProviderPresets.Get((string?)provider.SelectedItem);
        var known = ProviderPresets.All.SelectMany(p => new[] { p.DefaultModel, p.DefaultFallbackModel }).ToHashSet();
        if (string.IsNullOrWhiteSpace(model.Text) || known.Contains(model.Text))
            model.Text = fallback ? preset.DefaultFallbackModel : preset.DefaultModel;
        if (!fallback) _providerNotes.Text = preset.Notes;
        UpdateUrlBoxes();
    }

    private void UpdateUrlBoxes()
    {
        _primaryUrl.Enabled = (string?)_primaryProvider.SelectedItem is "custom" or "ollama";
        _fallbackUrl.Enabled = (string?)_fallbackProvider.SelectedItem is "custom" or "ollama";
        _providerNotes.Text = ProviderPresets.Get((string?)_primaryProvider.SelectedItem).Notes;
    }

    private void ApplyValues()
    {
        _s.DefaultMode = Enum.Parse<OptimizeMode>((string)_defaultMode.SelectedItem!);
        _s.ReviewBeforeReplace = _review.Checked;
        _s.SendAppHint = _appHint.Checked;
        _s.RepairDroppedDetails = _repair.Checked;
        _s.CheckForUpdates = _updates.Checked;
        _s.PersonalContext = _personal.Text.Trim();
        _s.Primary = new EndpointSettings { Provider = (string)_primaryProvider.SelectedItem!, Model = _primaryModel.Text.Trim(), BaseUrl = NullIfEmpty(_primaryUrl.Text) };
        _s.Fallback = new EndpointSettings { Provider = (string)_fallbackProvider.SelectedItem!, Model = _fallbackModel.Text.Trim(), BaseUrl = NullIfEmpty(_fallbackUrl.Text), Enabled = _fallbackOn.Checked };
        _s.Judge = new EndpointSettings
        {
            Provider = (string)_judgeProvider.SelectedItem!,
            Model = _judgeModel.Text.Trim(),
        };
        _s.Hotkeys = new HotkeySettings { Optimize = _hkOptimize.Text, OptimizeSelection = _hkSelection.Text, Voice = _hkVoice.Text };
        _s.Voice = new VoiceSettings
        {
            Enabled = _voiceOn.Checked,
            Provider = (string)_voiceProvider.SelectedItem!,
            Model = _voiceModel.Text.Trim(),
            BaseUrl = (string)_voiceProvider.SelectedItem! == "custom" ? NullIfEmpty(_primaryUrl.Text) : null,
            Language = _voiceLanguage.Text.Trim(),
            AutoOptimize = _voiceAuto.Checked,
            MaxSeconds = (int)_voiceMax.Value,
        };
        _s.FirstRunComplete = true;
    }

    private void Save()
    {
        ApplyValues();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(_s.Primary.Model)) errors.Add("Enter a model name for the main model.");
        var chords = new[] { ("Optimise", _s.Hotkeys.Optimize), ("Optimise selection", _s.Hotkeys.OptimizeSelection), ("Voice", _s.Hotkeys.Voice) };
        foreach (var (name, text) in chords.Where(c => !string.IsNullOrWhiteSpace(c.Item2)))
        {
            if (_hotkeys.Probe(text) is { } problem) errors.Add($"{name}: {problem}");
        }
        var duplicates = chords.Where(c => !string.IsNullOrWhiteSpace(c.Item2)).GroupBy(c => c.Item2, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1);
        foreach (var d in duplicates) errors.Add($"{d.Key} is assigned to more than one action.");
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", errors), "PromptForge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var conflicts = chords
            .Where(c => !string.IsNullOrWhiteSpace(c.Item2) && HotkeyChord.TryParse(c.Item2, out _))
            .Select(c => { HotkeyChord.TryParse(c.Item2, out var chord); return HotkeyManager.AltGrConflict(chord); })
            .OfType<string>()
            .ToList();
        if (conflicts.Count > 0 &&
            MessageBox.Show(this, string.Join("\n\n", conflicts) + "\n\nSave these shortcuts anyway?", "PromptForge",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        foreach (var (id, box) in _keys.Where(k => !string.IsNullOrWhiteSpace(k.Value.Text)))
            _secrets.Set(id, box.Text.Trim());
        try
        {
            StartupRegistration.Set(_startup.Checked);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Error("startup registration failed", ex);
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private async Task TestAsync(Button button)
    {
        ApplyValues();
        // Use keys typed but not yet saved.
        var typed = new OverlaySecrets(_secrets, _keys.Where(k => !string.IsNullOrWhiteSpace(k.Value.Text)).ToDictionary(k => k.Key, k => k.Value.Text.Trim()));
        button.Enabled = false;
        _testResult.ForeColor = SystemColors.ControlText;
        _testResult.Text = "Testing…";
        try
        {
            var endpoint = EndpointResolver.Resolve(_s, typed).First();
            var sw = Stopwatch.StartNew();
            var reply = await _client.CompleteAsync(endpoint,
                [new ChatMessage("user", "Reply with the single word OK.")],
                new ChatOptions { MaxTokens = 300, JsonMode = false, Mode = OptimizeMode.Light, Timeout = TimeSpan.FromSeconds(20) },
                CancellationToken.None);
            _testResult.ForeColor = Color.FromArgb(38, 150, 80);
            _testResult.Text = $"Connected to {endpoint.Label} in {sw.ElapsedMilliseconds} ms.";
        }
        catch (Exception ex) when (ex is LlmException or EndpointConfigException or InvalidOperationException)
        {
            _testResult.ForeColor = Color.FromArgb(200, 60, 60);
            _testResult.Text = ex is LlmException llm ? $"{llm.UserMessage} ({llm.Message})" : ex.Message;
        }
        finally
        {
            button.Enabled = true;
        }
    }

    private static void OpenUrl(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Error("open failed", ex); }
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static ComboBox Combo(string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        c.Items.AddRange(items);
        return c;
    }

    private static ComboBox ProviderCombo() => Combo(ProviderPresets.All.Select(p => p.Id).ToArray());

    private static Label Label(string text, int width = 70) => new() { Text = text, Width = width, Padding = new Padding(0, 4, 0, 0) };

    private static Label Heading(string text) => new() { Text = text, AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Padding = new Padding(0, 8, 0, 2), MaximumSize = new Size(680, 0) };

    private static Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(680, 0), Padding = new Padding(0, 0, 0, 6) };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        row.Controls.AddRange(controls);
        return row;
    }

    private static FlowLayoutPanel Stack(params Control[] controls)
    {
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Top };
        stack.Controls.AddRange(controls);
        return stack;
    }

    private static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(12), AutoScroll = true };
        page.Controls.Add(content);
        return page;
    }

    private sealed class OverlaySecrets(ISecretStore inner, Dictionary<string, string> overrides) : ISecretStore
    {
        public string? Get(string providerId) => overrides.TryGetValue(providerId, out var v) ? v : inner.Get(providerId);
        public void Set(string providerId, string? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
    }
}
