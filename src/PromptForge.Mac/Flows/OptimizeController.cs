using Avalonia.Media;
using PromptForge.Core.Optimization;
using PromptForge.Core.Settings;
using PromptForge.Mac.Native;
using PromptForge.Mac.TextAccess;
using PromptForge.Mac.UI;

namespace PromptForge.Mac.Flows;

/// <summary>
/// The core workflow: capture text from the app in front → open the review window (which runs the
/// optimiser) → on accept, put the result back. The original text is never changed until the user
/// accepts, and the result is never submitted.
/// </summary>
internal sealed class OptimizeController(AppServices services)
{
    private ReviewWindow? _activeWindow;
    private bool _busy;
    private string? _lastAcceptedOutput;

    /// <summary>The draft before the most recent accepted replacement, for "Copy last original text".</summary>
    public string? LastOriginal { get; private set; }

    public event Action? LastOriginalChanged;

    public bool IsBusy => _busy;

    public async Task OnHotkeyAsync(CaptureMode requestedMode, ushort? mainKey)
    {
        if (_activeWindow is { IsVisible: true })
        {
            ObjC.ActivateSelf();
            _activeWindow.Activate();
            return;
        }
        if (_busy) return;
        _busy = true;
        try
        {
            await RunAsync(requestedMode, mainKey);
        }
        catch (Exception ex)
        {
            Log.Error("optimise flow failed", ex);
            services.Notify("Something went wrong: " + ex.Message, NotifyKind.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RunAsync(CaptureMode requestedMode, ushort? mainKey)
    {
        var target = TargetInspector.InspectFrontmost();
        if (target.Pid <= 0 || target.IsOwnApp) return;
        if (target.IsPasswordField)
        {
            services.Notify("PromptForge doesn't read password fields (secure input is on).", NotifyKind.Info);
            return;
        }

        var mode = target.IsTerminal ? CaptureMode.Clipboard : requestedMode;
        await Keyboard.WaitForModifiersReleasedAsync(mainKey, TimeSpan.FromMilliseconds(1200));

        var capture = await TextBridge.CaptureAsync(target, mode);
        if (!capture.Ok)
        {
            services.Notify(capture.Problem!, capture.Problem == TextBridge.AccessibilityHelp ? NotifyKind.Warning : NotifyKind.Info);
            if (capture.Problem == TextBridge.AccessibilityHelp) AX.PromptForTrust();
            return;
        }
        var text = capture.Text!;
        Log.Info($"captured mode={mode} app={target.BundleId} chars={text.Length} terminal={target.IsTerminal}");

        var decision = await ReviewAsync(text, mode, target.AppLabel);
        await ApplyDecisionAsync(decision, target, mode, text);
    }

    /// <summary>Review flow for text that didn't come from a field (dictation); the result is inserted at the cursor.</summary>
    public async Task ReviewDictationAsync(string transcript, int targetPid)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var target = TargetInspector.Inspect(targetPid);
            var mode = target.IsTerminal ? CaptureMode.Clipboard : CaptureMode.InsertAtCursor;
            var decision = await ReviewAsync(transcript, mode, target.AppLabel);
            if (decision is null) return;
            switch (decision.Action)
            {
                case ReviewAction.Accept when mode == CaptureMode.InsertAtCursor:
                    var result = await TextBridge.InsertAsync(targetPid, decision.Text, singleLine: false);
                    if (result.Outcome == ReplaceOutcome.CopiedInstead) services.Notify(result.Reason!, NotifyKind.Info);
                    break;
                case ReviewAction.Accept or ReviewAction.Copy:
                    Pasteboard.WriteText(decision.Text);
                    services.Notify("Copied. Paste it with ⌘V.", NotifyKind.Info);
                    break;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<ReviewDecision?> ReviewAsync(string text, CaptureMode mode, string? appLabel)
    {
        var settings = services.Settings;
        var context = new ReviewContext
        {
            Original = text,
            Mode = mode,
            TargetLabel = appLabel,
            InitialMode = settings.DefaultMode,
            Run = (optimizeMode, task, details, ct) => OptimizeAsync(text, optimizeMode, task, details, appLabel, ct),
            Preloaded = IsOwnOutput(text) ? Unchanged(text) : null,
            PreloadedNote = IsOwnOutput(text) ? "This is the prompt PromptForge just produced, so it wasn't sent again. Press ⌘R to optimise it anyway." : null,
        };

        if (!settings.ReviewBeforeReplace && context.Preloaded is null)
        {
            // Instant mode: replace directly unless something needs a human look.
            var pill = new StatusPill();
            pill.ShowMessage("Optimising…", Color.FromRgb(80, 140, 255));
            try
            {
                var result = await OptimizeAsync(text, settings.DefaultMode, TaskType.Auto, null, appLabel, CancellationToken.None);
                if (result.Warnings.Count == 0 && !result.IsUnchanged)
                    return new ReviewDecision(ReviewAction.Accept, result.Prompt);
                context = context with { Preloaded = result };
            }
            catch (Exception ex) when (ex is OptimizeException or EndpointConfigException)
            {
                services.Notify(ex.Message, NotifyKind.Warning);
                return null;
            }
            finally
            {
                pill.Close();
            }
        }

        var window = new ReviewWindow(context);
        _activeWindow = window;
        ObjC.ActivateSelf();
        window.Show();
        window.Activate();
        try
        {
            return await window.Completion;
        }
        finally
        {
            _activeWindow = null;
        }
    }

    private async Task<OptimizeResult> OptimizeAsync(string text, OptimizeMode mode, TaskType task, string? details, string? appLabel, CancellationToken ct)
    {
        var settings = services.Settings;
        var endpoints = EndpointResolver.Resolve(settings, services.Secrets);
        var request = new OptimizeRequest
        {
            Text = details is null ? text : $"{text.TrimEnd()}\n\nAdditional details: {details}",
            Mode = mode,
            TaskOverride = task,
            PersonalContext = string.IsNullOrWhiteSpace(settings.PersonalContext) ? null : settings.PersonalContext,
            WrittenIn = settings.SendAppHint ? appLabel : null,
        };
        var result = await services.Optimizer.OptimizeAsync(request, endpoints, services.OptimizerOptions, ct);
        Log.Info($"optimised task={result.TaskType} verdict={result.Verdict} model={result.Model} ms={result.Elapsed.TotalMilliseconds:0} " +
                 $"tokens={result.PromptTokens}/{result.CachedTokens}c/{result.CompletionTokens} attempts={result.Attempts} warnings={result.Warnings.Count}");
        return details is null ? result : result with { Original = text };
    }

    private async Task ApplyDecisionAsync(ReviewDecision? decision, TargetInfo target, CaptureMode mode, string original)
    {
        if (decision is null) return;
        var unchanged = Normalize(decision.Text) == Normalize(original);

        switch (decision.Action)
        {
            case ReviewAction.Accept when unchanged && mode != CaptureMode.Clipboard:
            case ReviewAction.Reject:
                if (mode == CaptureMode.WholeField && await Apps.BringToFrontAsync(target.Pid))
                    Keyboard.CollapseSelectionToEnd(); // the capture left everything selected; don't let the next keystroke replace it
                break;

            case ReviewAction.Accept:
                var result = await TextBridge.ReplaceAsync(target, mode, original, decision.Text);
                Log.Info($"accept outcome={result.Outcome}");
                if (result.Outcome == ReplaceOutcome.CopiedInstead) services.Notify(result.Reason!, NotifyKind.Info);
                LastOriginal = original;
                LastOriginalChanged?.Invoke();
                _lastAcceptedOutput = decision.Text;
                break;

            case ReviewAction.Copy:
                Pasteboard.WriteText(decision.Text);
                _lastAcceptedOutput = decision.Text;
                if (await Apps.BringToFrontAsync(target.Pid) && mode == CaptureMode.WholeField)
                    Keyboard.CollapseSelectionToEnd();
                services.Notify("Improved prompt copied. Paste it with ⌘V.", NotifyKind.Info);
                break;
        }
    }

    private bool IsOwnOutput(string text) => _lastAcceptedOutput is not null && Normalize(text) == Normalize(_lastAcceptedOutput);

    private static OptimizeResult Unchanged(string text) => new()
    {
        Original = text,
        Prompt = text,
        TaskType = TaskType.General,
        Verdict = Verdict.Unchanged,
        Model = "not sent",
    };

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();
}
