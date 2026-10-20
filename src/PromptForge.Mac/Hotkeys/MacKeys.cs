using PromptForge.Core.Settings;

namespace PromptForge.Mac.Hotkeys;

/// <summary>
/// Maps the key names stored in settings ("P", "D1", "F8", "Space"; the same names on Windows and in
/// Avalonia) to macOS virtual key codes, and formats chords the Mac way (⌃⌥⇧⌘P). In a chord, Ctrl is
/// Control, Alt is Option and Win is Command.
/// </summary>
internal static class MacKeys
{
    public const ushort A = 0x00;
    public const ushort C = 0x08;
    public const ushort V = 0x09;
    public const ushort RightArrow = 0x7C;

    private static readonly Dictionary<string, ushort> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = 0x00, ["S"] = 0x01, ["D"] = 0x02, ["F"] = 0x03, ["H"] = 0x04, ["G"] = 0x05, ["Z"] = 0x06, ["X"] = 0x07,
        ["C"] = 0x08, ["V"] = 0x09, ["B"] = 0x0B, ["Q"] = 0x0C, ["W"] = 0x0D, ["E"] = 0x0E, ["R"] = 0x0F, ["Y"] = 0x10,
        ["T"] = 0x11, ["O"] = 0x1F, ["U"] = 0x20, ["I"] = 0x22, ["P"] = 0x23, ["L"] = 0x25, ["J"] = 0x26, ["K"] = 0x28,
        ["N"] = 0x2D, ["M"] = 0x2E,
        ["D1"] = 0x12, ["D2"] = 0x13, ["D3"] = 0x14, ["D4"] = 0x15, ["D6"] = 0x16, ["D5"] = 0x17, ["D9"] = 0x19,
        ["D7"] = 0x1A, ["D8"] = 0x1C, ["D0"] = 0x1D,
        ["OemPlus"] = 0x18, ["OemMinus"] = 0x1B, ["OemCloseBrackets"] = 0x1E, ["Oem6"] = 0x1E, ["OemOpenBrackets"] = 0x21, ["Oem4"] = 0x21,
        ["OemQuotes"] = 0x27, ["Oem7"] = 0x27, ["OemSemicolon"] = 0x29, ["Oem1"] = 0x29, ["OemPipe"] = 0x2A, ["Oem5"] = 0x2A,
        ["OemComma"] = 0x2B, ["OemQuestion"] = 0x2C, ["Oem2"] = 0x2C, ["OemPeriod"] = 0x2F, ["OemTilde"] = 0x32, ["Oem3"] = 0x32,
        ["Space"] = 0x31, ["Tab"] = 0x30, ["Return"] = 0x24, ["Enter"] = 0x24,
        ["F1"] = 0x7A, ["F2"] = 0x78, ["F3"] = 0x63, ["F4"] = 0x76, ["F5"] = 0x60, ["F6"] = 0x61, ["F7"] = 0x62, ["F8"] = 0x64,
        ["F9"] = 0x65, ["F10"] = 0x6D, ["F11"] = 0x67, ["F12"] = 0x6F, ["F13"] = 0x69, ["F14"] = 0x6B, ["F15"] = 0x71,
        ["Home"] = 0x73, ["End"] = 0x77, ["PageUp"] = 0x74, ["Prior"] = 0x74, ["PageDown"] = 0x79, ["Next"] = 0x79,
        ["Left"] = 0x7B, ["Right"] = 0x7C, ["Down"] = 0x7D, ["Up"] = 0x7E,
    };

    private static readonly Dictionary<string, string> Symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = "Space", ["Return"] = "↩", ["Enter"] = "↩", ["Tab"] = "⇥", ["Left"] = "←", ["Right"] = "→", ["Up"] = "↑", ["Down"] = "↓",
        ["OemPlus"] = "=", ["OemMinus"] = "-", ["OemComma"] = ",", ["OemPeriod"] = ".", ["OemQuestion"] = "/", ["OemSemicolon"] = ";",
        ["OemQuotes"] = "'", ["OemOpenBrackets"] = "[", ["OemCloseBrackets"] = "]", ["OemPipe"] = "\\", ["OemTilde"] = "`",
    };

    public static bool TryGetKeyCode(HotkeyChord chord, out ushort code) => Codes.TryGetValue(chord.Key, out code);

    /// <summary>Why this chord can't be a global shortcut on a Mac, or null if it can.</summary>
    public static string? Problem(HotkeyChord chord)
    {
        if (string.IsNullOrEmpty(chord.Key)) return "Press a key together with Control or Command.";
        if (!chord.Ctrl && !chord.Win)
            return chord.Alt
                ? $"{Display(chord)} types a character on a Mac. Include Control (⌃) or Command (⌘)."
                : "Include Control (⌃) or Command (⌘) so normal typing can't trigger it.";
        if (!TryGetKeyCode(chord, out _)) return $"\"{chord.Key}\" isn't a key PromptForge recognises.";
        return null;
    }

    /// <summary>"Ctrl+Alt+P" → "⌃⌥P", in Apple's modifier order.</summary>
    public static string Display(HotkeyChord chord) =>
        (chord.Ctrl ? "⌃" : "") + (chord.Alt ? "⌥" : "") + (chord.Shift ? "⇧" : "") + (chord.Win ? "⌘" : "") + DisplayKey(chord.Key);

    public static string Display(string? text) =>
        HotkeyChord.TryParse(text, out var chord) ? Display(chord) : text ?? "";

    private static string DisplayKey(string key) =>
        Symbols.TryGetValue(key, out var symbol) ? symbol
        : key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) ? key[1..]
        : key;
}
