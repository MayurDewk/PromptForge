using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace PromptForge.Mac.UI;

internal enum NotifyKind
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// A notification card in the top-right corner, shown without taking focus. PromptForge draws its own
/// instead of using Notification Centre, which needs extra permissions and drops clicks for unsigned apps.
/// Clicking it runs the optional action; it fades on its own after a few seconds.
/// </summary>
internal sealed class Toast : Window
{
    private static Toast? _current;
    private readonly DispatcherTimer _timer;
    private readonly Action? _onClick;

    private Toast(string message, NotifyKind kind, Action? onClick, TimeSpan duration)
    {
        _onClick = onClick;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        Width = 360;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        if (onClick is not null) Cursor = new Cursor(StandardCursorType.Hand);

        var accent = kind switch
        {
            NotifyKind.Error => Color.FromRgb(225, 70, 70),
            NotifyKind.Warning => Color.FromRgb(230, 150, 30),
            _ => Color.FromRgb(109, 74, 255),
        };
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(36, 36, 40)),
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = "PromptForge", FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, FontSize = 13 },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(225, 225, 230)), FontSize = 13 },
                },
            },
        };

        PointerPressed += (_, _) =>
        {
            Close();
            _onClick?.Invoke();
        };
        _timer = new DispatcherTimer { Interval = duration };
        _timer.Tick += (_, _) => Close();
    }

    public static void Show(string message, NotifyKind kind, Action? onClick = null)
    {
        _current?.Close();
        var toast = new Toast(message, kind, onClick, TimeSpan.FromSeconds(onClick is null ? 5 : 10));
        _current = toast;
        toast.Closed += (_, _) =>
        {
            toast._timer.Stop();
            if (ReferenceEquals(_current, toast)) _current = null;
        };
        toast.Opened += (_, _) => toast.PlaceTopRight();
        toast.Show();
        toast._timer.Start();
    }

    private void PlaceTopRight()
    {
        var screen = Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        Position = new PixelPoint(area.Right - (int)((Width + 16) * scale), area.Y + (int)(12 * scale));
    }
}
