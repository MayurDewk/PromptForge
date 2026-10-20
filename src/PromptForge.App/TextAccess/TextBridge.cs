using PromptForge.App.Native;
using PromptForge.Core.Voice;

namespace PromptForge.App.TextAccess;

internal enum CaptureMode
{
    /// <summary>Select everything in the focused field (Ctrl+A), copy it, and replace all of it on accept.</summary>
    WholeField,
    /// <summary>Copy only the current selection and paste over it on accept.</summary>
    Selection,
    /// <summary>Use what's already on the clipboard; the result goes back to the clipboard only. Used for terminals.</summary>
    Clipboard,
    /// <summary>Nothing captured (e.g. dictated text); the result is pasted at the cursor.</summary>
    InsertAtCursor,
}

internal sealed record CaptureResult(string? Text, string? Problem)
{
    public bool Ok => Text is not null;
    public static CaptureResult Fail(string problem) => new(null, problem);
}

internal enum ReplaceOutcome
{
    Replaced,
    CopiedInstead,
}

internal sealed record ReplaceResult(ReplaceOutcome Outcome, string? Reason = null);

/// <summary>
/// Reads text from, and writes text back into, whatever app the user is typing in, using the
/// clipboard and synthetic Ctrl+A / Ctrl+C / Ctrl+V. This is the one mechanism that works across
/// browsers, Electron apps and native apps without per-app integrations. The user's clipboard is
/// restored afterwards. Enter is never sent, so nothing is ever submitted.
/// </summary>
internal static class TextBridge
{
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMilliseconds(700);
    private static DelayedClipboard? _delayedClipboard;

    /// <summary>Created lazily on the UI thread (it owns a hidden window that answers clipboard requests).</summary>
    private static DelayedClipboard DelayedClipboardInstance => _delayedClipboard ??= new DelayedClipboard();

    public static async Task<CaptureResult> CaptureAsync(TargetInfo target, CaptureMode mode)
    {
        if (mode == CaptureMode.Clipboard)
        {
            var clip = ClipboardText.Read();
            return string.IsNullOrWhiteSpace(clip)
                ? CaptureResult.Fail("In terminals, copy your prompt first (select it, then Ctrl+Shift+C or right-click), then press the hotkey.")
                : new CaptureResult(clip, null);
        }

        var snapshot = ClipboardSnapshot.Capture();
        try
        {
            var before = ClipboardText.Sequence;
            if (mode == CaptureMode.WholeField)
            {
                Keyboard.SendCtrlChord('A');
                await Task.Delay(40);
            }
            Keyboard.SendCtrlChord('C');

            if (!await ClipboardText.WaitForChangeAsync(before, CopyTimeout))
            {
                return CaptureResult.Fail(mode == CaptureMode.Selection
                    ? "Nothing was selected. Select the text first, or use the main optimise hotkey."
                    : "Couldn't read text here. The field may be empty, or this app (for example one running as administrator) blocks other programs.");
            }

            var text = ClipboardText.Read();
            if (string.IsNullOrWhiteSpace(text))
                return CaptureResult.Fail("The field is empty.");
            return new CaptureResult(text, null);
        }
        finally
        {
            snapshot.Restore();
        }
    }

    /// <summary>
    /// Puts the accepted text into the original field. Falls back to copying it when the window has
    /// gone, focus can't be restored, the field changed while the user was reviewing, or the paste
    /// can't be confirmed. "Replaced" is only reported after the field has been re-read and matches.
    /// </summary>
    public static async Task<ReplaceResult> ReplaceAsync(TargetInfo target, CaptureMode mode, string original, string replacement)
    {
        if (mode == CaptureMode.Clipboard)
            return CopyInstead(replacement, "Copied. Paste it into the terminal yourself; PromptForge never pastes into terminals because a multi-line paste can run commands.");

        if (!target.IsAlive)
            return CopyInstead(replacement, "The original window was closed, so the prompt was copied instead.");

        // The user may still be holding Ctrl from Ctrl+Enter.
        if (!await Keyboard.WaitForModifiersReleasedAsync(0, TimeSpan.FromMilliseconds(800)))
            Keyboard.ReleaseStuckModifiers();

        if (!await Foreground.BringToFrontAsync(target.Hwnd))
            return CopyInstead(replacement, "Couldn't switch back to the original window, so the prompt was copied instead.");

        var snapshot = ClipboardSnapshot.Capture();
        try
        {
            if (mode == CaptureMode.WholeField)
            {
                // Re-read the field: if it changed during review, don't overwrite the user's new text.
                var current = await CopyFieldAsync();
                if (current is null || Normalize(current) != Normalize(original))
                {
                    Keyboard.CollapseSelectionToEnd(); // our select-all must not leave the user's new text exposed
                    snapshot.Restore();
                    return CopyInstead(replacement, "The text in the field changed while you were reviewing, so it wasn't replaced. The improved prompt is on your clipboard.");
                }
            }

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                if (Win32.GetForegroundWindow() != target.Hwnd)
                {
                    snapshot.Restore();
                    return CopyInstead(replacement, "Focus moved to another window, so the prompt was copied instead.");
                }

                var consumed = await PasteAsync(replacement, slow: attempt > 1);
                if (mode != CaptureMode.WholeField)
                {
                    Log.Info($"paste attempt={attempt} consumed={consumed} verified=n/a");
                    snapshot.Restore();
                    return new ReplaceResult(ReplaceOutcome.Replaced);
                }

                // Confirm by reading the field back (this also re-selects it, ready for a second attempt).
                var after = await CopyFieldAsync();
                var verified = after is not null && SameText(after, replacement);
                Log.Info($"paste attempt={attempt} consumed={consumed} verified={verified}");
                if (verified)
                {
                    Keyboard.CollapseSelectionToEnd();
                    snapshot.Restore();
                    return new ReplaceResult(ReplaceOutcome.Replaced);
                }
            }

            Keyboard.CollapseSelectionToEnd();
            snapshot.Restore();
            return CopyInstead(replacement, "This app didn't accept the pasted text, so the improved prompt is on your clipboard. Paste it with Ctrl+V.");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
        {
            Log.Error("replace failed", ex);
            return CopyInstead(replacement, "The clipboard was busy, so the prompt was copied instead.");
        }
    }

    /// <summary>Types text at the cursor of the current foreground window (used for dictation).</summary>
    public static async Task<ReplaceResult> InsertAsync(IntPtr hwnd, string text, bool singleLine)
    {
        text = TranscriptSanitizer.Clean(text, singleLine);
        if (!Win32.IsWindow(hwnd) || !await Foreground.BringToFrontAsync(hwnd))
            return CopyInstead(text, "Couldn't return to the text box, so the text was copied. Paste it with Ctrl+V.");

        var snapshot = ClipboardSnapshot.Capture();
        try
        {
            var consumed = await PasteAsync(text, slow: false);
            Log.Info($"insert consumed={consumed}");
            return new ReplaceResult(ReplaceOutcome.Replaced);
        }
        finally
        {
            snapshot.Restore();
        }
    }

    /// <summary>
    /// Pastes text and waits until the target app has actually read it from the clipboard. Some apps
    /// (Windows 11 Notepad, other WinUI apps) read the clipboard asynchronously after Ctrl+V; restoring
    /// the user's clipboard too early makes them paste the old contents instead.
    /// </summary>
    private static async Task<bool> PasteAsync(string text, bool slow)
    {
        var clipboard = DelayedClipboardInstance;
        if (!clipboard.Offer(text))
        {
            // Clipboard locked by another app: fall back to an immediate copy.
            ClipboardText.WritePrivate(text);
            await Task.Delay(slow ? 120 : 40);
            Keyboard.SendCtrlChord('V');
            await Task.Delay(slow ? 1500 : 900);
            return false;
        }
        await Task.Delay(slow ? 120 : 40);
        var consumedTask = clipboard.Arm();
        Keyboard.SendCtrlChord('V');
        var consumed = await Task.WhenAny(consumedTask, Task.Delay(slow ? 3000 : 2000)) == consumedTask;
        await Task.Delay(consumed ? 150 : 400);
        return consumed;
    }

    /// <summary>Select-all + copy in the focused field; returns its text, or null if nothing could be copied.</summary>
    private static async Task<string?> CopyFieldAsync()
    {
        var before = ClipboardText.Sequence;
        Keyboard.SendCtrlChord('A');
        await Task.Delay(40);
        Keyboard.SendCtrlChord('C');
        return await ClipboardText.WaitForChangeAsync(before, CopyTimeout) ? ClipboardText.Read() : null;
    }

    /// <summary>Compares ignoring whitespace and punctuation differences that editors introduce (smart quotes, bullets, CRLF).</summary>
    internal static bool SameText(string a, string b) => LettersAndDigits(a) == LettersAndDigits(b);

    private static string LettersAndDigits(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());

    private static ReplaceResult CopyInstead(string text, string reason)
    {
        try
        {
            ClipboardText.Write(text);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Log.Error("copy fallback failed", ex);
            return new ReplaceResult(ReplaceOutcome.CopiedInstead, "Couldn't access the clipboard. Copy the prompt from the review window.");
        }
        return new ReplaceResult(ReplaceOutcome.CopiedInstead, reason);
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();
}

internal static class Foreground
{
    /// <summary>Activates a window, using the attach-thread-input fallback when Windows refuses a plain request.</summary>
    public static async Task<bool> BringToFrontAsync(IntPtr hwnd)
    {
        if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
        if (!Win32.SetForegroundWindow(hwnd))
        {
            var foreground = Win32.GetForegroundWindow();
            var foregroundThread = Win32.GetWindowThreadProcessId(foreground, out _);
            var ourThread = Win32.GetCurrentThreadId();
            if (foregroundThread != ourThread) Win32.AttachThreadInput(ourThread, foregroundThread, true);
            Win32.BringWindowToTop(hwnd);
            Win32.SetForegroundWindow(hwnd);
            if (foregroundThread != ourThread) Win32.AttachThreadInput(ourThread, foregroundThread, false);
        }

        for (var i = 0; i < 20; i++)
        {
            if (Win32.GetForegroundWindow() == hwnd)
            {
                await Task.Delay(60); // let the app restore focus to its text box
                return true;
            }
            await Task.Delay(20);
        }
        return false;
    }
}
