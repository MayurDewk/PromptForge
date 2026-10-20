using System.Globalization;
using System.Text;
using PromptForge.App.TextAccess;
using PromptForge.Core.Diffing;
using PromptForge.Core.Optimization;
using PromptForge.Core.Settings;

namespace PromptForge.App.UI;

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
internal sealed class ReviewForm : Form
{
    private readonly ReviewContext _ctx;
    private readonly TaskCompletionSource<ReviewDecision> _decision = new();
    private CancellationTokenSource? _runCts;
    private OptimizeResult? _result;
    private bool _decided;

    private readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) };
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly ComboBox _task = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly Button _rerun = new() { Text = "Re-run", AutoSize = true };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TextBox _improved = NewTextBox(readOnly: false);
    private readonly RichTextBox _changes = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = false };
    private readonly TextBox _originalBox = NewTextBox(readOnly: true);
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Dock = DockStyle.Top, Height = 4, MarqueeAnimationSpeed = 25 };
    private readonly RichTextBox _notes = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical };
    private readonly TextBox _details = new() { Dock = DockStyle.Fill, PlaceholderText = "Optional: answer the questions or add details, then Re-run (Ctrl+R)" };
    private readonly Button _accept = new() { AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
    private readonly Button _copy = new() { Text = "Copy", AutoSize = true };
    private readonly Button _undoEdits = new() { Text = "Undo my edits", AutoSize = true, Enabled = false };
    private readonly Button _reject = new() { Text = "Keep original (Esc)", AutoSize = true };
    private readonly TableLayoutPanel _notesRow;
    private readonly ToolTip _tooltip = new();

    public ReviewForm(ReviewContext ctx)
    {
        _ctx = ctx;
        Text = "PromptForge — review";
        Icon = AppIcon.Get();
        StartPosition = FormStartPosition.Manual;
        Size = new Size(820, 600);
        MinimumSize = new Size(560, 400);
        KeyPreview = true;
        TopMost = true;
        Font = new Font("Segoe UI", 9.5f);
        ShowInTaskbar = true;

        _mode.Items.AddRange(Enum.GetNames<OptimizeMode>());
        _mode.SelectedItem = ctx.InitialMode.ToString();
        _task.Items.AddRange(Enum.GetNames<TaskType>().Select(n => n == "Auto" ? "Auto-detect" : n).ToArray<object>());
        _task.SelectedIndex = (int)ctx.InitialTask;
        _accept.Text = ctx.Mode switch
        {
            CaptureMode.Clipboard => "Copy (Ctrl+Enter)",
            CaptureMode.InsertAtCursor => "Insert (Ctrl+Enter)",
            _ => "Replace (Ctrl+Enter)",
        };
        if (ctx.Mode == CaptureMode.Clipboard) _copy.Visible = false;

        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        top.Controls.AddRange([new Label { Text = "Mode", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _mode,
            new Label { Text = "Task", AutoSize = true, Padding = new Padding(8, 6, 0, 0) }, _task, _rerun]);

        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8, 6, 8, 0) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(_status, 0, 0);
        header.Controls.Add(top, 1, 0);

        _tabs.TabPages.Add(Page("Improved", _improved));
        _tabs.TabPages.Add(Page("Changes", _changes));
        _tabs.TabPages.Add(Page("Original", _originalBox));
        var tabsHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 6, 8, 4) };
        tabsHost.Controls.Add(_tabs);
        tabsHost.Controls.Add(_progress);

        _notesRow = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 170, ColumnCount = 1, RowCount = 2, Padding = new Padding(8, 0, 8, 0) };
        _notesRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _notesRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        _notesRow.Controls.Add(_notes, 0, 0);
        _notesRow.Controls.Add(_details, 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 6, 8, 8) };
        buttons.Controls.AddRange([_accept, _copy, _reject, _undoEdits]);

        Controls.Add(tabsHost);
        Controls.Add(_notesRow);
        Controls.Add(buttons);
        Controls.Add(header);

        AcceptButton = null; // Enter must stay a newline in the editor; Ctrl+Enter accepts.
        _accept.Click += (_, _) => Decide(ReviewAction.Accept);
        _copy.Click += (_, _) => Decide(ReviewAction.Copy);
        _reject.Click += (_, _) => Decide(ReviewAction.Reject);
        _rerun.Click += (_, _) => StartRun();
        _undoEdits.Click += (_, _) => { if (_result is not null) _improved.Text = _result.Prompt; };
        _improved.TextChanged += (_, _) => _undoEdits.Enabled = _result is not null && _improved.Text != _result.Prompt;
        _originalBox.Text = ctx.Original.Replace("\r\n", "\n").Replace("\n", "\r\n");
        SetWorking(false);
        _accept.Enabled = _copy.Enabled = false;
    }

    public Task<ReviewDecision> Completion => _decision.Task;

    public void PlaceNear(IntPtr targetHwnd)
    {
        var screen = Screen.FromHandle(targetHwnd == IntPtr.Zero ? Handle : targetHwnd);
        var area = screen.WorkingArea;
        var rect = Native.Win32.GetWindowRect(targetHwnd, out var r) ? r.ToRectangle() : area;
        if (rect.Width < 200 || rect.Height < 150) rect = area;
        var x = rect.Left + (rect.Width - Width) / 2;
        var y = rect.Top + Math.Max(40, (rect.Height - Height) / 3);
        Location = new Point(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - Width)), Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        if (_ctx.Preloaded is not null) ShowResult(_ctx.Preloaded, _ctx.PreloadedNote);
        else StartRun();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) { Decide(ReviewAction.Reject); e.Handled = e.SuppressKeyPress = true; }
        else if (e.Control && e.KeyCode == Keys.Enter && _accept.Enabled) { Decide(ReviewAction.Accept); e.Handled = e.SuppressKeyPress = true; }
        else if (e.Control && e.Shift && e.KeyCode == Keys.C && _copy.Enabled && _copy.Visible) { Decide(ReviewAction.Copy); e.Handled = e.SuppressKeyPress = true; }
        else if (e.Control && e.KeyCode == Keys.R) { StartRun(); e.Handled = e.SuppressKeyPress = true; }
        else if (e.Control && e.KeyCode is Keys.D1 or Keys.D2 or Keys.D3) { _tabs.SelectedIndex = e.KeyCode - Keys.D1; e.Handled = e.SuppressKeyPress = true; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _runCts?.Cancel();
        if (!_decided) { _decided = true; _decision.TrySetResult(new ReviewDecision(ReviewAction.Reject, _ctx.Original)); }
        base.OnFormClosing(e);
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
            if (cts.IsCancellationRequested || IsDisposed) return;
            ShowResult(result, null);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by Esc, Re-run or closing.
        }
        catch (Exception ex) when (ex is OptimizeException or EndpointConfigException)
        {
            if (!cts.IsCancellationRequested && !IsDisposed) ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("optimise failed unexpectedly", ex);
            if (!cts.IsCancellationRequested && !IsDisposed) ShowError("Something went wrong: " + ex.Message);
        }
        finally
        {
            if (!IsDisposed && ReferenceEquals(_runCts, cts)) SetWorking(false);
        }
    }

    private void SetWorking(bool working)
    {
        _progress.Visible = working;
        _rerun.Enabled = !working;
        _mode.Enabled = _task.Enabled = !working;
        if (working)
        {
            _status.Text = "Optimising… (Esc to cancel)";
            _accept.Enabled = _copy.Enabled = false;
        }
    }

    private void ShowResult(OptimizeResult result, string? note)
    {
        _result = result;
        _improved.Text = ToWindowsNewlines(result.Prompt);
        _undoEdits.Enabled = false;
        _accept.Enabled = _copy.Enabled = true;
        if (result.IsUnchanged && _ctx.Mode != CaptureMode.Clipboard && _ctx.Mode != CaptureMode.InsertAtCursor)
            _accept.Text = "Keep as is (Ctrl+Enter)";

        var seconds = result.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        _status.Text = result.IsUnchanged
            ? $"Already clear: no changes suggested · {seconds} s"
            : $"{result.TaskType} · {Describe(result.Verdict)} · {seconds} s";
        _tooltip.SetToolTip(_status, $"{result.Model} · {result.PromptTokens + result.CompletionTokens:N0} tokens · {result.Attempts} call(s)");

        _changes.Rtf = BuildDiffRtf(TextDiff.Compute(result.Original, result.Prompt), _changes.Font, _changes.ForeColor);
        RenderNotes(result, note);
        _tabs.SelectedIndex = 0;
        _improved.Focus();
        _improved.Select(0, 0);
    }

    private void ShowError(string message)
    {
        _status.Text = "Couldn't optimise";
        _notes.Clear();
        AppendNote(message, Color.FromArgb(200, 60, 60), bold: true);
        AppendNote("\nYour original text hasn't been touched. Press Ctrl+R to retry, or Esc to close.", null, bold: false);
        _notesRow.Visible = true;
        _accept.Enabled = false;
        _copy.Enabled = false;
    }

    private void RenderNotes(OptimizeResult result, string? note)
    {
        _notes.Clear();
        if (note is not null) AppendNote(note + "\n", null, bold: true);
        foreach (var warning in result.Warnings) AppendNote("⚠ " + warning + "\n", Color.FromArgb(196, 120, 0), bold: false);
        if (result.Questions.Count > 0)
        {
            AppendNote("To get a better answer, consider adding:\n", null, bold: true);
            foreach (var q in result.Questions) AppendNote("  • " + q + "\n", null, bold: false);
        }
        if (result.Changes.Count > 0)
        {
            AppendNote("What changed: ", null, bold: true);
            AppendNote(string.Join(" · ", result.Changes) + "\n", null, bold: false);
        }
        _notesRow.Visible = _notes.TextLength > 0 || result.Questions.Count > 0;
        _notes.Select(0, 0);
        _notes.ScrollToCaret();
    }

    private void AppendNote(string text, Color? color, bool bold)
    {
        _notes.SelectionStart = _notes.TextLength;
        _notes.SelectionLength = 0;
        _notes.SelectionColor = color ?? _notes.ForeColor;
        _notes.SelectionFont = new Font(_notes.Font, bold ? FontStyle.Bold : FontStyle.Regular);
        _notes.AppendText(text);
    }

    private void Decide(ReviewAction action)
    {
        if (_decided) return;
        if (action != ReviewAction.Reject && _result is null) return;
        _runCts?.Cancel();
        _decided = true;
        var text = action == ReviewAction.Reject ? _ctx.Original : FromWindowsNewlines(_improved.Text, _ctx.Original);
        _decision.TrySetResult(new ReviewDecision(action, text));
        Close();
    }

    private static string Describe(Verdict verdict) => verdict switch
    {
        Verdict.LightEdit => "light edit",
        Verdict.Rewritten => "rewritten",
        _ => "unchanged",
    };

    private static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(4) };
        page.Controls.Add(content);
        return page;
    }

    private static TextBox NewTextBox(bool readOnly) => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        AcceptsReturn = true,
        ScrollBars = ScrollBars.Vertical,
        WordWrap = true,
        ReadOnly = readOnly,
        BorderStyle = BorderStyle.None,
        Font = new Font("Segoe UI", 10.5f),
        MaxLength = 0,
    };

    private static string ToWindowsNewlines(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");

    /// <summary>Returns the edited text using the original draft's newline convention.</summary>
    private static string FromWindowsNewlines(string s, string original) =>
        original.Contains("\r\n", StringComparison.Ordinal) ? s : s.Replace("\r\n", "\n");

    /// <summary>RTF for the Changes tab: insertions green and underlined, deletions red and struck through.</summary>
    internal static string BuildDiffRtf(IReadOnlyList<DiffSegment> segments, Font font, Color text)
    {
        var sb = new StringBuilder();
        sb.Append(@"{\rtf1\ansi\deff0{\fonttbl{\f0 ").Append(font.FontFamily.Name).Append(";}}");
        sb.Append(@"{\colortbl;")
          .Append(CultureInfo.InvariantCulture, $@"\red{text.R}\green{text.G}\blue{text.B};")
          .Append(@"\red38\green150\blue80;\red210\green65\blue65;}");
        sb.Append(CultureInfo.InvariantCulture, $@"\f0\fs{(int)Math.Round(font.SizeInPoints * 2)}\cf1 ");
        foreach (var segment in segments)
        {
            var escaped = EscapeRtf(segment.Text);
            sb.Append(segment.Kind switch
            {
                DiffKind.Inserted => $@"{{\cf2\ul {escaped}}}",
                DiffKind.Deleted => $@"{{\cf3\strike {escaped}}}",
                _ => escaped,
            });
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string EscapeRtf(string s)
    {
        var sb = new StringBuilder(s.Length + 16);
        foreach (var c in s.Replace("\r\n", "\n"))
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '{': sb.Append(@"\{"); break;
                case '}': sb.Append(@"\}"); break;
                case '\n': sb.Append(@"\par "); break;
                case '\t': sb.Append(@"\tab "); break;
                default:
                    if (c > 127) sb.Append(CultureInfo.InvariantCulture, $@"\u{(short)c}?");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
