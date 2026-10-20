using Avalonia.Input;
using Avalonia.Interactivity;
using PromptForge.Core.Settings;
using PromptForge.Mac.Hotkeys;
using TextBox = Avalonia.Controls.TextBox;

namespace PromptForge.Mac.UI;

/// <summary>A box that records the next key combination pressed in it, shown as e.g. "⌃⌥P" and stored as "Ctrl+Alt+P".</summary>
internal sealed class HotkeyBox : TextBox
{
    private string _chord = "";

    public HotkeyBox()
    {
        IsReadOnly = true;
        Width = 200;
        Watermark = "Click, then press a shortcut";
        Cursor = new Cursor(StandardCursorType.Hand);
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <summary>The chord in settings form ("Ctrl+Alt+P"), or empty.</summary>
    public string Chord
    {
        get => _chord;
        set
        {
            _chord = value ?? "";
            Text = MacKeys.Display(_chord);
        }
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        // Capture combinations instead of acting on them; Tab still moves focus.
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None) return;
        e.Handled = true;
        if (e.Key is Key.Back or Key.Delete && e.KeyModifiers == KeyModifiers.None)
        {
            Chord = "";
            return;
        }
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.None)
            return;

        var chord = new HotkeyChord(
            e.KeyModifiers.HasFlag(KeyModifiers.Control),
            e.KeyModifiers.HasFlag(KeyModifiers.Alt),
            e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            e.KeyModifiers.HasFlag(KeyModifiers.Meta),
            e.Key.ToString());
        Chord = chord.ToString();
    }
}
