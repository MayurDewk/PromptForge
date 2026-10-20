using PromptForge.Core.Voice;
using PromptForge.Mac.Native;

namespace PromptForge.Mac.TextAccess;

internal enum CaptureMode
{
    /// <summary>Select everything in the focused field (⌘A), copy it, and replace all of it on accept.</summary>
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
/// pasteboard and synthetic ⌘A / ⌘C / ⌘V. This is the one mechanism that works across browsers,
/// Electron apps and native apps without per-app integrations. The user's clipboard is restored
/// afterwards. Return is never sent, so nothing is ever submitted.
/// </summary>
internal static class TextBridge
{
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMilliseconds(700);

    public const string AccessibilityHelp =
        "PromptForge needs the Accessibility permission to copy and paste for you. Turn it on in System Settings → Privacy & Security → Accessibility, then try again.";

    public static async Task<CaptureResult> CaptureAsync(TargetInfo target, CaptureMode mode)
    {
        if (mode == CaptureMode.Clipboard)
        {
            var clip = Pasteboard.ReadText();
            return string.IsNullOrWhiteSpace(clip)
                ? CaptureResult.Fail("In terminals, copy your prompt first (select it, then ⌘C), then press the shortcut.")
                : new CaptureResult(clip, null);
        }
        if (!AX.IsTrusted) return CaptureResult.Fail(AccessibilityHelp);

        var snapshot = PasteboardSnapshot.Capture();
        try
        {
            var before = Pasteboard.ChangeCount;
            if (mode == CaptureMode.WholeField)
            {
                Keyboard.SelectAll();
                await Task.Delay(40);
            }
            Keyboard.Copy();

            if (!await Pasteboard.WaitForChangeAsync(before, CopyTimeout))
            {
                return CaptureResult.Fail(mode == CaptureMode.Selection
                    ? "Nothing was selected. Select the text first, or use the main optimise shortcut."
                    : "Couldn't read text here. The field may be empty, or this app doesn't support copy.");
            }

            var text = Pasteboard.ReadText();
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
    /// Puts the accepted text into the original field. Falls back to copying it when the app has quit,
    /// focus can't be restored, the field changed while the user was reviewing, or the paste can't be
    /// confirmed. "Replaced" is only reported after the field has been re-read and matches.
    /// </summary>
    public static async Task<ReplaceResult> ReplaceAsync(TargetInfo target, CaptureMode mode, string original, string replacement)
    {
        if (mode == CaptureMode.Clipboard)
            return CopyInstead(replacement, "Copied. Paste it into the terminal yourself; PromptForge never pastes into terminals because a multi-line paste can run commands.");

        if (!target.IsAlive)
            return CopyInstead(replacement, "The original app was closed, so the prompt was copied instead.");
        if (!AX.IsTrusted)
            return CopyInstead(replacement, "The prompt was copied instead. " + AccessibilityHelp);

        // The user may still be holding ⌘ from ⌘↩.
        await Keyboard.WaitForModifiersReleasedAsync(null, TimeSpan.FromMilliseconds(800));

        if (!await Apps.BringToFrontAsync(target.Pid))
            return CopyInstead(replacement, "Couldn't switch back to the original app, so the prompt was copied instead.");

        var snapshot = PasteboardSnapshot.Capture();
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
                if (Apps.FrontmostPid() != target.Pid)
                {
                    snapshot.Restore();
                    return CopyInstead(replacement, "Focus moved to another app, so the prompt was copied instead.");
                }

                await PasteAsync(replacement, slow: attempt > 1);
                if (mode != CaptureMode.WholeField)
                {
                    Log.Info($"paste attempt={attempt} verified=n/a");
                    snapshot.Restore();
                    return new ReplaceResult(ReplaceOutcome.Replaced);
                }

                // Confirm by reading the field back (this also re-selects it, ready for a second attempt).
                var after = await CopyFieldAsync();
                var verified = after is not null && SameText(after, replacement);
                Log.Info($"paste attempt={attempt} verified={verified}");
                if (verified)
                {
                    Keyboard.CollapseSelectionToEnd();
                    snapshot.Restore();
                    return new ReplaceResult(ReplaceOutcome.Replaced);
                }
            }

            Keyboard.CollapseSelectionToEnd();
            snapshot.Restore();
            return CopyInstead(replacement, "This app didn't accept the pasted text, so the improved prompt is on your clipboard. Paste it with ⌘V.");
        }
        finally
        {
            snapshot.Restore(); // no-op if already restored
        }
    }

    /// <summary>Types text at the cursor of an app (used for dictation).</summary>
    public static async Task<ReplaceResult> InsertAsync(int pid, string text, bool singleLine)
    {
        text = TranscriptSanitizer.Clean(text, singleLine);
        if (!AX.IsTrusted)
            return CopyInstead(text, "The text was copied instead. " + AccessibilityHelp);
        if (!Apps.IsRunning(pid) || !await Apps.BringToFrontAsync(pid))
            return CopyInstead(text, "Couldn't return to the text box, so the text was copied. Paste it with ⌘V.");

        var snapshot = PasteboardSnapshot.Capture();
        try
        {
            await PasteAsync(text, slow: false);
            Log.Info("insert done");
            return new ReplaceResult(ReplaceOutcome.Replaced);
        }
        finally
        {
            snapshot.Restore();
        }
    }

    /// <summary>Pastes text, then gives the app time to read it before the caller restores the clipboard.</summary>
    private static async Task PasteAsync(string text, bool slow)
    {
        Pasteboard.WritePrivateText(text);
        await Task.Delay(slow ? 120 : 40);
        Keyboard.Paste();
        await Task.Delay(slow ? 900 : 350);
    }

    /// <summary>Select-all + copy in the focused field; returns its text, or null if nothing could be copied.</summary>
    private static async Task<string?> CopyFieldAsync()
    {
        var before = Pasteboard.ChangeCount;
        Keyboard.SelectAll();
        await Task.Delay(40);
        Keyboard.Copy();
        return await Pasteboard.WaitForChangeAsync(before, CopyTimeout) ? Pasteboard.ReadText() : null;
    }

    /// <summary>Compares ignoring whitespace and punctuation differences that editors introduce (smart quotes, bullets, CRLF).</summary>
    internal static bool SameText(string a, string b) => LettersAndDigits(a) == LettersAndDigits(b);

    private static string LettersAndDigits(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());

    private static ReplaceResult CopyInstead(string text, string reason)
    {
        Pasteboard.WriteText(text);
        return new ReplaceResult(ReplaceOutcome.CopiedInstead, reason);
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();
}
