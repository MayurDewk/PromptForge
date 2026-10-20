using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace PromptForge.Mac.UI;

/// <summary>
/// Small always-on-top status pill ("Listening…", "Transcribing…") near the bottom of the screen.
/// It never takes focus, so typing continues in the user's app. Clicking it raises Clicked (used to cancel).
/// </summary>
internal sealed class StatusPill : Window
{
    private readonly TextBlock _text = new() { Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _dot = new() { Width = 12, Height = 12, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };

    public StatusPill()
    {
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Cursor = new Cursor(StandardCursorType.Hand);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(32, 32, 36)),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(16, 10, 20, 10),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _dot, _text } },
        };
        PointerPressed += (_, _) => Clicked?.Invoke();
    }

    public event Action? Clicked;

    public void ShowMessage(string message, Color dot)
    {
        _text.Text = message;
        _dot.Fill = new SolidColorBrush(dot);
        if (!IsVisible) Show();
        PlaceAtBottomCentre();
    }

    private void PlaceAtBottomCentre()
    {
        var screen = Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var size = DesiredSize.Width > 0 ? DesiredSize : new Size(320, 40);
        var x = area.X + (int)((area.Width - size.Width * scale) / 2);
        var y = area.Bottom - (int)((size.Height + 48) * scale);
        Position = new PixelPoint(x, y);
    }
}
