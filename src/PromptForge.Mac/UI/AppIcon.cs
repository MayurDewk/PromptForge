using Avalonia.Controls;

namespace PromptForge.Mac.UI;

internal static class AppIcon
{
    private static WindowIcon? _icon;

    public static WindowIcon Get()
    {
        if (_icon is not null) return _icon;
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("PromptForge.Mac.Assets.PromptForge.png")
                           ?? throw new InvalidOperationException("App icon resource is missing.");
        _icon = new WindowIcon(stream);
        return _icon;
    }
}
