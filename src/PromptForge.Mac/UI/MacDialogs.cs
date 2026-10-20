using PromptForge.Mac.Native;

namespace PromptForge.Mac.UI;

/// <summary>Native NSAlert and NSOpenPanel, so dialogs look and behave like every other Mac app.</summary>
internal static class MacDialogs
{
    private const long FirstButton = 1000;
    private const long ModalOk = 1;

    public enum Style
    {
        Warning = 0,
        Informational = 1,
        Critical = 2,
    }

    /// <summary>Shows a message with one or more buttons; returns the index of the button pressed (0 = first).</summary>
    public static int Show(string message, string? details = null, Style style = Style.Informational, params string[] buttons)
    {
        ObjC.ActivateSelf();
        using var pool = new AutoreleasePool();
        var alert = ObjC.Send(ObjC.Send(ObjC.Class("NSAlert"), "alloc"), "init");
        try
        {
            ObjC.Send(alert, "setMessageText:", ObjC.NSString(message));
            if (details is not null) ObjC.Send(alert, "setInformativeText:", ObjC.NSString(details));
            ObjC.Send(alert, "setAlertStyle:", (IntPtr)(long)style);
            foreach (var button in buttons.Length == 0 ? ["OK"] : buttons) ObjC.Send(alert, "addButtonWithTitle:", ObjC.NSString(button));
            return (int)((long)ObjC.Send(alert, "runModal") - FirstButton);
        }
        finally
        {
            ObjC.Send(alert, "release");
        }
    }

    public static bool Confirm(string message, string? details, string yes, string no, Style style = Style.Informational) =>
        Show(message, details, style, yes, no) == 0;

    /// <summary>Lets the user pick one existing file; returns its path, or null if cancelled.</summary>
    public static string? PickFile(string message)
    {
        ObjC.ActivateSelf();
        using var pool = new AutoreleasePool();
        var panel = ObjC.Send(ObjC.Class("NSOpenPanel"), "openPanel");
        ObjC.Send(panel, "setCanChooseFiles:", 1);
        ObjC.Send(panel, "setCanChooseDirectories:", 0);
        ObjC.Send(panel, "setAllowsMultipleSelection:", 0);
        ObjC.Send(panel, "setMessage:", ObjC.NSString(message));
        if ((long)ObjC.Send(panel, "runModal") != ModalOk) return null;
        return ObjC.ToManagedString(ObjC.Send(ObjC.Send(panel, "URL"), "path"));
    }
}
