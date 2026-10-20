using System.Text.Json;
using System.Text.Json.Serialization;
using PromptForge.Core.Llm;
using PromptForge.Core.Optimization;

namespace PromptForge.Core.Settings;

public sealed class EndpointSettings
{
    public string Provider { get; set; } = ProviderPresets.Groq.Id;
    public string Model { get; set; } = ProviderPresets.Groq.DefaultModel;

    /// <summary>Only used by the "custom" and "ollama" providers; others use the preset URL.</summary>
    public string? BaseUrl { get; set; }

    public bool Enabled { get; set; } = true;
}

public sealed class HotkeySettings
{
    /// <summary>Selects all text in the focused field, optimises it, replaces it on accept.</summary>
    public string Optimize { get; set; } = "Ctrl+Alt+P";

    /// <summary>Optimises only the current selection; for documents and editors where select-all is too much.</summary>
    public string OptimizeSelection { get; set; } = "Ctrl+Alt+Shift+P";

    /// <summary>Tap to start/stop dictation, or hold to talk and release to finish.</summary>
    public string Voice { get; set; } = "Ctrl+Alt+D";
}

public sealed class VoiceSettings
{
    public bool Enabled { get; set; } = true;
    public string Provider { get; set; } = ProviderPresets.Groq.Id;
    public string Model { get; set; } = ProviderPresets.Groq.DefaultTranscriptionModel!;
    public string? BaseUrl { get; set; }

    /// <summary>ISO-639-1 code such as "en"; empty lets the model detect the language.</summary>
    public string Language { get; set; } = "";

    /// <summary>Send the transcript straight to the optimiser and review it, instead of typing it in.</summary>
    public bool AutoOptimize { get; set; }

    public int MaxSeconds { get; set; } = 120;
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public EndpointSettings Primary { get; set; } = new();
    public EndpointSettings Fallback { get; set; } = new() { Model = ProviderPresets.Groq.DefaultFallbackModel };

    /// <summary>
    /// The model that grades rewrites in the quality check. A different provider from the optimiser,
    /// so the judge isn't grading its own model family. Uses the API key saved for that provider.
    /// </summary>
    public EndpointSettings Judge { get; set; } = new() { Provider = ProviderPresets.Gemini.Id, Model = ProviderPresets.Gemini.DefaultModel };
    public OptimizeMode DefaultMode { get; set; } = OptimizeMode.Standard;

    /// <summary>When false, accepted results replace the text immediately (still never sends it).</summary>
    public bool ReviewBeforeReplace { get; set; } = true;

    /// <summary>Optional standing background, e.g. "Year 3 CS student; I mostly use C#, Java and Python."</summary>
    public string PersonalContext { get; set; } = "";

    /// <summary>Tell the model which kind of app the draft came from (e.g. "Claude (browser)").</summary>
    public bool SendAppHint { get; set; } = true;

    public bool RepairDroppedDetails { get; set; } = true;

    /// <summary>Check GitHub Releases for a newer version once a day. Sends no prompt text, settings or keys.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>When the last automatic update check ran, so restarts don't check again within a day.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }
    public int TimeoutSeconds { get; set; } = 45;
    public HotkeySettings Hotkeys { get; set; } = new();
    public VoiceSettings Voice { get; set; } = new();
    public bool FirstRunComplete { get; set; }
}

public static class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads settings; a missing file gives defaults, a corrupt one is kept aside as .bad and replaced by defaults.</summary>
    public static AppSettings Load(string path, Action<string>? log = null)
    {
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
            settings.Primary ??= new EndpointSettings();
            settings.Fallback ??= new EndpointSettings { Enabled = false };
            settings.Hotkeys ??= new HotkeySettings();
            settings.Voice ??= new VoiceSettings();
            settings.Judge ??= new AppSettings().Judge;
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            log?.Invoke($"settings file unreadable ({ex.GetType().Name}); using defaults");
            try { File.Copy(path, path + ".bad", overwrite: true); } catch (IOException) { }
            return new AppSettings();
        }
    }

    public static void Save(string path, AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        // On macOS and Linux new files are world-readable by default; settings can hold personal context.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }
}
