using Avalonia.Media;
using Avalonia.Threading;
using PromptForge.Core.Llm;
using PromptForge.Core.Settings;
using PromptForge.Core.Voice;
using PromptForge.Mac.Hotkeys;
using PromptForge.Mac.TextAccess;
using PromptForge.Mac.UI;
using PromptForge.Mac.Voice;

namespace PromptForge.Mac.Flows;

/// <summary>
/// One voice shortcut, two behaviours: tap it to start and tap again to stop, or hold it while
/// talking and release to finish. The transcript is typed at the cursor, or optionally sent to the
/// optimiser for review first. Dictation and optimisation stay separate unless the user opts in.
/// </summary>
internal sealed class VoiceController(AppServices services, OptimizeController optimizer)
{
    private static readonly TimeSpan TapThreshold = TimeSpan.FromMilliseconds(350);
    private static readonly Color Red = Color.FromRgb(225, 55, 55);
    private static readonly Color Blue = Color.FromRgb(80, 140, 255);

    private enum State { Idle, Recording, Transcribing }

    private State _state = State.Idle;
    private VoiceRecorder? _recorder;
    private StatusPill? _pill;
    private DispatcherTimer? _limit;
    private DateTime _startedAt;
    private bool _toggleMode;
    private bool _cancelled;
    private int _targetPid;
    private LlmEndpoint? _endpoint;

    public void OnPressed()
    {
        switch (_state)
        {
            case State.Recording when _toggleMode:
                _ = StopAsync();
                return;
            case State.Recording or State.Transcribing:
                return;
        }

        if (!services.Settings.Voice.Enabled)
        {
            services.Notify("Voice input is turned off in Settings → Voice.", NotifyKind.Info);
            return;
        }
        if (optimizer.IsBusy) return;
        _endpoint = EndpointResolver.ResolveVoice(services.Settings, services.Secrets);
        if (_endpoint is null)
        {
            services.Notify("Add your Groq API key in Settings → Model to use voice input (custom voice providers need an https:// address).", NotifyKind.Warning);
            return;
        }

        _targetPid = Apps.FrontmostPid();
        _cancelled = false;
        _toggleMode = false;
        _recorder = new VoiceRecorder();
        try
        {
            _recorder.Start();
        }
        catch (InvalidOperationException ex)
        {
            Log.Error("microphone start failed", ex);
            services.Notify("Couldn't start the microphone: " + ex.Message + " Check System Settings → Privacy & Security → Microphone.", NotifyKind.Error);
            Cleanup();
            return;
        }

        _state = State.Recording;
        _startedAt = DateTime.UtcNow;
        _pill = new StatusPill();
        _pill.Clicked += Cancel;
        _pill.ShowMessage("Listening… release the keys to finish · click to cancel", Red);
        _limit = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(services.Settings.Voice.MaxSeconds, 10, 600)) };
        _limit.Tick += (_, _) => _ = StopAsync();
        _limit.Start();
        Log.Info("voice recording started");
    }

    /// <summary>Carbon reports the shortcut's release: a quick tap switches to toggle mode, a hold finishes.</summary>
    public void OnReleased()
    {
        if (_state != State.Recording || _toggleMode) return;
        if (DateTime.UtcNow - _startedAt < TapThreshold)
        {
            _toggleMode = true;
            _pill?.ShowMessage($"Listening… press {MacKeys.Display(services.Settings.Hotkeys.Voice)} again to finish · click to cancel", Red);
        }
        else
        {
            _ = StopAsync();
        }
    }

    private void Cancel()
    {
        if (_state != State.Recording) return;
        _cancelled = true;
        _ = StopAsync();
    }

    private async Task StopAsync()
    {
        if (_state != State.Recording || _recorder is null) return;
        _state = State.Transcribing;
        _limit?.Stop();
        try
        {
            var audio = await _recorder.StopAsync();
            var duration = _recorder.Duration;
            if (_cancelled)
            {
                Log.Info("voice cancelled");
                return;
            }
            if (duration < TimeSpan.FromMilliseconds(400))
            {
                services.Notify("That recording was too short.", NotifyKind.Info);
                return;
            }

            // Speech-to-text invents words from silence ("Thank you."), so silent clips are never sent.
            var level = SpeechDetector.Analyze(_recorder.LastPcm, VoiceRecorder.SampleRate);
            if (level != AudioLevel.HasSound)
            {
                Log.Info($"voice skipped level={level} seconds={duration.TotalSeconds:0.0}");
                services.Notify(level == AudioLevel.Muted
                    ? "The microphone recorded nothing. Allow PromptForge in System Settings → Privacy & Security → Microphone, and check the input isn't muted."
                    : "Didn't hear any speech, so nothing was typed. Check your microphone isn't muted, or speak a little closer to it.",
                    NotifyKind.Info);
                return;
            }

            _pill?.ShowMessage("Transcribing…", Blue);
            var voice = services.Settings.Voice;
            var text = await services.Client.TranscribeAsync(_endpoint!, audio, "speech.wav",
                string.IsNullOrWhiteSpace(voice.Language) ? null : voice.Language, TimeSpan.FromSeconds(60), CancellationToken.None);
            Log.Info($"voice transcribed seconds={duration.TotalSeconds:0.0} chars={text.Length}");
            if (string.IsNullOrWhiteSpace(text))
            {
                services.Notify("Didn't catch any speech. Try again a little closer to the microphone.", NotifyKind.Info);
                return;
            }

            ClosePill();
            await Keyboard.WaitForModifiersReleasedAsync(null, TimeSpan.FromSeconds(1));

            if (voice.AutoOptimize)
            {
                await optimizer.ReviewDictationAsync(text, _targetPid);
                return;
            }

            var target = TargetInspector.InspectFrontmost();
            if (target.IsOwnApp || target.Pid <= 0) target = TargetInspector.Inspect(_targetPid);
            if (target.IsPasswordField)
            {
                Pasteboard.WriteText(text);
                services.Notify("That's a password field, so the text was copied instead of typed.", NotifyKind.Info);
                return;
            }
            var result = await TextBridge.InsertAsync(target.Pid, text, singleLine: target.IsTerminal);
            if (result.Outcome == ReplaceOutcome.CopiedInstead) services.Notify(result.Reason!, NotifyKind.Info);
        }
        catch (LlmException ex)
        {
            Log.Error("transcription failed", ex);
            services.Notify("Transcription failed: " + ex.UserMessage, NotifyKind.Warning);
        }
        catch (Exception ex)
        {
            Log.Error("voice flow failed", ex);
            services.Notify("Voice input failed: " + ex.Message, NotifyKind.Error);
        }
        finally
        {
            Cleanup();
        }
    }

    private void ClosePill()
    {
        _pill?.Close();
        _pill = null;
    }

    private void Cleanup()
    {
        _limit?.Stop();
        _limit = null;
        ClosePill();
        _recorder?.Dispose();
        _recorder = null;
        _state = State.Idle;
    }
}
