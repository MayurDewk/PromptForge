using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PromptForge.Core.Diffing;
using PromptForge.Core.Optimization;
using PromptForge.Core.Settings;
using PromptForge.Mac.TextAccess;

namespace PromptForge.Mac.UI;

internal enum ReviewAction
{
    Accept,
    Copy,
    Reject,
}

internal sealed record ReviewDecision(ReviewAction Action, string Text);

internal sealed record ReviewContext
{
    public required string Original { get; init; }
    public required CaptureMode Mode { get; init; }
    public string? TargetLabel { get; init; }
    public OptimizeMode InitialMode { get; init; } = OptimizeMode.Standard;
    public TaskType InitialTask { get; init; } = TaskType.Auto;

    /// <summary>Runs the optimiser with the chosen mode, task type and any extra details the user typed.</summary>
    public required Func<OptimizeMode, TaskType, string?, CancellationToken, Task<OptimizeResult>> Run { get; init; }

    /// <summary>Shown immediately instead of calling the model (e.g. the text is PromptForge's own last output).</summary>
    public OptimizeResult? Preloaded { get; init; }
    public string? PreloadedNote { get; init; }
}

/// <summary>
/// The review window: improved prompt (editable), word-level changes, original, the optimiser's
/// questions, and Replace / Copy / Keep original. Opens immediately in a working state so the hotkey
/// always gives instant feedback; Esc cancels at any point.
/// </summary>
internal sealed class ReviewWindow : Window
{
    private static readonly IBrush Green = new SolidColorBrush(Color.FromRgb(38, 150, 80));
    private static readonly IBrush Red = new SolidColorBrush(Color.FromRgb(210, 65, 65));
    private static readonly IBrush Amber = new SolidColorBrush(Color.FromRgb(196, 120, 0));

    private readonly ReviewContext _ctx;
    private readonly TaskCompletionSource<ReviewDecision> _decision = new();
    private CancellationTokenSource? _runCts;
    private OptimizeResult? _result;
    private bool _decided;
    private bool _settingText;

    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ComboBox _mode = new() { Width = 120 };
    private readonly ComboBox _task = new() { Width = 130 };
    private readonly Button _rerun = new() { Content = "Re-run" };
    private readonly TabControl _tabs = new();
    private readonly TextBox _improved = NewTextBox(readOnly: false);
    private readonly SelectableTextBlock _changes = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(4) };
    private readonly TextBox _originalBox = NewTextBox(readOnly: true);
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 4, MinHeight = 4 };
    private readonly SelectableTextBlock _notes = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _details = new() { Watermark = "Optional: answer the questions or add details, then Re-run (⌘R)" };
    private readonly Button _accept = new() { Classes = { "accent" } };
    private readonly Button _copy = new() { Content = "Copy (⇧⌘C)" };
    private readonly Button _undoEdits = new() { Content = "Undo my edits", IsEnabled = false };
    private readonly Button _reject = new() { Content = "Keep original (Esc)" };
    private readonly Control _notesRow;

    public ReviewWindow(ReviewContext ctx)
    {
        _ctx = ctx;
        Title = "PromptForge — review";
        Icon = AppIcon.Get();
        Width = 820;
        Height = 600;
        MinWidth = 560;
        MinHeight = 400;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _mode.ItemsSource = Enum.GetNames<OptimizeMode>();
        _mode.SelectedItem = ctx.InitialMode.ToString();
        _task.ItemsSource = Enum.GetNames<TaskType>().Select(n => n == "Auto" ? "Auto-detect" : n).ToArray();
        _task.SelectedIndex = (int)ctx.InitialTask;
        _accept.Content = ctx.Mode switch
        {
            CaptureMode.Clipboard => "Copy (⌘↩)",
            CaptureMode.InsertAtCursor => "Insert (⌘↩)",
            _ => "Replace (⌘↩)",
        };
        if (ctx.Mode == CaptureMode.Clipboard) _copy.IsVisible = false;

        var header = new DockPanel { Margin = new Thickness(12, 10, 12, 0), LastChildFill = true };
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { Caption("Mode"), _mode, Caption("Task"), _task, _rerun },
        };
        DockPanel.SetDock(controls, Dock.Right);
        header.Children.Add(controls);
        header.Children.Add(_status);

        _tabs.ItemsSource = new[]
        {
            new TabItem { Header = "Improved", Content = _improved },
            new TabItem { Header = "Changes", Content = new ScrollViewer { Content = _changes } },
            new TabItem { Header = "Original", Content = _originalBox },
        };

        _notesRow = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Height = 170,
            Margin = new Thickness(12, 0),
            Children =
            {
                new ScrollViewer { Content = _notes },
                _details,
            },
        };
        Grid.SetRow(_details, 1);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(12, 8, 12, 12),
            Children = { _undoEdits, _reject, _copy, _accept },
        };

        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_notesRow, Dock.Bottom);
        DockPanel.SetDock(_progress, Dock.Top);
        _tabs.Margin = new Thickness(12, 4, 12, 4);
        body.Children.Add(header);
        body.Children.Add(buttons);
        body.Children.Add(_notesRow);
        body.Children.Add(_progress);
        body.Children.Add(_tabs);
        Content = body;

        _accept.Click += (_, _) => Decide(ReviewAction.Accept);
        _copy.Click += (_, _) => Decide(ReviewAction.Copy);
        _reject.Click += (_, _) => Decide(ReviewAction.Reject);
        _rerun.Click += (_, _) => StartRun();
        _undoEdits.Click += (_, _) => { if (_result is not null) SetImproved(_result.Prompt); };
        _improved.TextChanged += (_, _) =>
        {
            if (!_settingText) _undoEdits.IsEnabled = _result is not null && _improved.Text != Unix(_result.Prompt);
        };
        _originalBox.Text = Unix(ctx.Original);
        // Tunnel so the editor doesn't swallow ⌘↩ and friends; Return alone stays a newline.
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        SetWorking(false);
        _accept.IsEnabled = _copy.IsEnabled = false;
    }

    public Task<ReviewDecision> Completion => _decision.Task;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Activate();
        if (_ctx.Preloaded is not null) ShowResult(_ctx.Preloaded, _ctx.PreloadedNote);
        else StartRun();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        var cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.Key == Key.Escape) Decide(ReviewAction.Reject);
        else if (cmd && e.Key == Key.Enter && _accept.IsEnabled) Decide(ReviewAction.Accept);
        else if (cmd && shift && e.Key == Key.C && _copy.IsEnabled && _copy.IsVisible) Decide(ReviewAction.Copy);
        else if (cmd && e.Key == Key.R) StartRun();
        else if (cmd && e.Key is Key.D1 or Key.D2 or Key.D3) _tabs.SelectedIndex = e.Key - Key.D1;
        else return;
        e.Handled = true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _runCts?.Cancel();
        if (!_decided)
        {
            _decided = true;
            _decision.TrySetResult(new ReviewDecision(ReviewAction.Reject, _ctx.Original));
        }
        base.OnClosing(e);
    }

    private async void StartRun()
    {
        _runCts?.Cancel();
        var cts = _runCts = new CancellationTokenSource();
        var mode = Enum.Parse<OptimizeMode>((string)_mode.SelectedItem!);
        var task = (TaskType)_task.SelectedIndex;
        var details = string.IsNullOrWhiteSpace(_details.Text) ? null : _details.Text.Trim();
        SetWorking(true);
        try
        {
            var result = await _ctx.Run(mode, task, details, cts.Token);
            if (cts.IsCancellationRequested || _decided) return;
            ShowResult(result, null);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by Esc, Re-run or closing.
        }
        catch (Exception ex) when (ex is OptimizeException or EndpointConfigException)
        {
            if (!cts.IsCancellationRequested && !_decided) ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("optimise failed unexpectedly", ex);
            if (!cts.IsCancellationRequested && !_decided) ShowError("Something went wrong: " + ex.Message);
        }
        finally
        {
            if (!_decided && ReferenceEquals(_runCts, cts)) SetWorking(false);
        }
    }

    private void SetWorking(bool working)
    {
        _progress.IsVisible = working;
        _rerun.IsEnabled = !working;
        _mode.IsEnabled = _task.IsEnabled = !working;
        if (working)
        {
            _status.Text = "Optimising… (Esc to cancel)";
            _accept.IsEnabled = _copy.IsEnabled = false;
        }
    }

    private void ShowResult(OptimizeResult result, string? note)
    {
        _result = result;
        SetImproved(result.Prompt);
        _accept.IsEnabled = _copy.IsEnabled = true;
        if (result.IsUnchanged && _ctx.Mode != CaptureMode.Clipboard && _ctx.Mode != CaptureMode.InsertAtCursor)
            _accept.Content = "Keep as is (⌘↩)";

        var seconds = result.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        _status.Text = result.IsUnchanged
            ? $"Already clear: no changes suggested · {seconds} s"
            : $"{result.TaskType} · {Describe(result.Verdict)} · {seconds} s";
        ToolTip.SetTip(_status, $"{result.Model} · {result.PromptTokens + result.CompletionTokens:N0} tokens · {result.Attempts} call(s)");

        BuildDiff(TextDiff.Compute(result.Original, result.Prompt));
        RenderNotes(result, note);
        _tabs.SelectedIndex = 0;
        _improved.Focus();
        _improved.CaretIndex = 0;
    }

    private void ShowError(string message)
    {
        _status.Text = "Couldn't optimise";
        var inlines = new InlineCollection
        {
            new Run(message) { Foreground = Red, FontWeight = FontWeight.Bold },
            new Run("\nYour original text hasn't been touched. Press ⌘R to retry, or Esc to close."),
        };
        _notes.Inlines = inlines;
        _notesRow.IsVisible = true;
        _accept.IsEnabled = false;
        _copy.IsEnabled = false;
    }

    private void RenderNotes(OptimizeResult result, string? note)
    {
        var inlines = new InlineCollection();
        if (note is not null) inlines.Add(new Run(note + "\n") { FontWeight = FontWeight.Bold });
        foreach (var warning in result.Warnings) inlines.Add(new Run("⚠ " + warning + "\n") { Foreground = Amber });
        if (result.Questions.Count > 0)
        {
            inlines.Add(new Run("To get a better answer, consider adding:\n") { FontWeight = FontWeight.Bold });
            foreach (var q in result.Questions) inlines.Add(new Run("  • " + q + "\n"));
        }
        if (result.Changes.Count > 0)
        {
            inlines.Add(new Run("What changed: ") { FontWeight = FontWeight.Bold });
            inlines.Add(new Run(string.Join(" · ", result.Changes) + "\n"));
        }
        _notes.Inlines = inlines;
        _notesRow.IsVisible = inlines.Count > 0;
    }

    /// <summary>The Changes tab: insertions green and underlined, deletions red and struck through.</summary>
    private void BuildDiff(IReadOnlyList<DiffSegment> segments)
    {
        var inlines = new InlineCollection();
        foreach (var segment in segments)
        {
            var text = Unix(segment.Text);
            inlines.Add(segment.Kind switch
            {
                DiffKind.Inserted => new Run(text) { Foreground = Green, TextDecorations = TextDecorations.Underline },
                DiffKind.Deleted => new Run(text) { Foreground = Red, TextDecorations = TextDecorations.Strikethrough },
                _ => new Run(text),
            });
        }
        _changes.Inlines = inlines;
    }

    private void Decide(ReviewAction action)
    {
        if (_decided) return;
        if (action != ReviewAction.Reject && _result is null) return;
        _runCts?.Cancel();
        _decided = true;
        var text = action == ReviewAction.Reject ? _ctx.Original : WithOriginalNewlines(_improved.Text ?? "", _ctx.Original);
        _decision.TrySetResult(new ReviewDecision(action, text));
        Close();
    }

    private void SetImproved(string text)
    {
        _settingText = true;
        _improved.Text = Unix(text);
        _settingText = false;
        _undoEdits.IsEnabled = false;
    }

    private static string Describe(Verdict verdict) => verdict switch
    {
        Verdict.LightEdit => "light edit",
        Verdict.Rewritten => "rewritten",
        _ => "unchanged",
    };

    private static TextBlock Caption(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };

    private static TextBox NewTextBox(bool readOnly) => new()
    {
        AcceptsReturn = true,
        AcceptsTab = false,
        TextWrapping = TextWrapping.Wrap,
        IsReadOnly = readOnly,
        FontSize = 14,
    };

    private static string Unix(string s) => s.Replace("\r\n", "\n");

    /// <summary>Returns the edited text using the original draft's newline convention.</summary>
    private static string WithOriginalNewlines(string s, string original) =>
        original.Contains("\r\n", StringComparison.Ordinal) ? s.Replace("\r\n", "\n").Replace("\n", "\r\n") : s;
}
