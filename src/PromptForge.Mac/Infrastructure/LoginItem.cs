using PromptForge.Mac.Native;

namespace PromptForge.Mac.Infrastructure;

/// <summary>
/// Start-at-login through SMAppService (macOS 13+): the app appears under System Settings → General →
/// Login Items, where you can also turn it off. No LaunchAgent files or admin rights.
/// </summary>
internal static class LoginItem
{
    private const long StatusEnabled = 1;

    public static bool IsEnabled()
    {
        try
        {
            using var pool = new AutoreleasePool();
            return (long)ObjC.Send(Service(), "status") == StatusEnabled;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Returns an error message, or null on success.</summary>
    public static unsafe string? Set(bool enabled)
    {
        if (enabled == IsEnabled()) return null;
        if (AppPaths.BundlePath is null) return "Open at login only works when PromptForge runs from PromptForge.app.";
        using var pool = new AutoreleasePool();
        IntPtr error = IntPtr.Zero;
        var ok = ObjC.SendBool(Service(), enabled ? "registerAndReturnError:" : "unregisterAndReturnError:", (IntPtr)(&error));
        if (ok) return null;
        var description = ObjC.ToManagedString(ObjC.Send(error, "localizedDescription")) ?? "unknown error";
        Log.Warn($"login item {(enabled ? "register" : "unregister")} failed: {description}");
        return description;
    }

    private static IntPtr Service()
    {
        var cls = ObjC.Class("SMAppService");
        if (cls == IntPtr.Zero) throw new EntryPointNotFoundException("SMAppService needs macOS 13 or later.");
        return ObjC.Send(cls, "mainAppService");
    }
}
