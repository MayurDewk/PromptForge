using PromptForge.Mac.Hotkeys;
using PromptForge.Mac.Native;

namespace PromptForge.Mac.TextAccess;

/// <summary>Synthetic keystrokes. Only ever sends select-all, copy, paste and Right arrow; never Return.</summary>
internal static class Keyboard
{
    public static void SendCommandChord(ushort keyCode, char character)
    {
        // A private event source keeps the user's physically held keys out of our events.
        var source = CG.CGEventSourceCreate(CG.StatePrivate);
        try
        {
            Post(source, keyCode, character, down: true, CG.FlagCommand);
            Post(source, keyCode, character, down: false, CG.FlagCommand);
        }
        finally
        {
            CF.Release(source);
        }
    }

    public static void SelectAll() => SendCommandChord(MacKeys.A, 'a');
    public static void Copy() => SendCommandChord(MacKeys.C, 'c');
    public static void Paste() => SendCommandChord(MacKeys.V, 'v');

    /// <summary>Right arrow collapses a select-all to the end, so the next keystroke can't overwrite the text.</summary>
    public static void CollapseSelectionToEnd()
    {
        var source = CG.CGEventSourceCreate(CG.StatePrivate);
        try
        {
            Post(source, MacKeys.RightArrow, null, down: true, 0);
            Post(source, MacKeys.RightArrow, null, down: false, 0);
        }
        finally
        {
            CF.Release(source);
        }
    }

    public static bool AnyModifierDown() => (CG.CGEventSourceFlagsState(CG.StateHidSystem) & CG.ModifierMask) != 0;

    /// <summary>Waits for the user to let go of the hotkey so held modifiers don't combine with our keystrokes.</summary>
    public static async Task<bool> WaitForModifiersReleasedAsync(ushort? mainKey, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!AnyModifierDown() && (mainKey is not { } key || !CG.IsKeyDown(key))) return true;
            await Task.Delay(15);
        }
        return false;
    }

    private static unsafe void Post(IntPtr source, ushort keyCode, char? character, bool down, ulong flags)
    {
        var e = CG.CGEventCreateKeyboardEvent(source, keyCode, down ? (byte)1 : (byte)0);
        if (e == IntPtr.Zero)
        {
            Log.Warn("couldn't create a keyboard event");
            return;
        }
        try
        {
            CG.CGEventSetFlags(e, flags);
            if (character is { } c)
            {
                // Apps match shortcuts by character, so this makes ⌘V mean paste on Dvorak, AZERTY and other layouts too.
                CG.CGEventKeyboardSetUnicodeString(e, 1, &c);
            }
            CG.MarkAsOurs(e);
            CG.CGEventPost(CG.HidEventTap, e);
        }
        finally
        {
            CF.Release(e);
        }
    }
}
