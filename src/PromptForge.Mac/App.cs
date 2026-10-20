using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace PromptForge.Mac;

internal sealed class App : Application
{
    private MenuBarController? _controller;

    public override void Initialize()
    {
        Name = "PromptForge";
        RequestedThemeVariant = ThemeVariant.Default; // follow the macOS light/dark setting
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _controller = new MenuBarController(this, desktop);
            desktop.Exit += (_, _) => _controller.Dispose();
        }
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen) _controller?.OnReopen();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
