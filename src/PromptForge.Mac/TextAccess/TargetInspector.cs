using PromptForge.Mac.Native;

namespace PromptForge.Mac.TextAccess;

/// <summary>The app the user was typing in when they pressed a hotkey.</summary>
internal sealed record TargetInfo(
    int Pid,
    string BundleId,
    string AppName,
    string? AppLabel,
    bool IsTerminal,
    bool IsPasswordField,
    bool IsOwnApp)
{
    public bool IsAlive => Apps.IsRunning(Pid);
}

internal static class TargetInspector
{
    private static readonly HashSet<string> Terminals = new(StringComparer.OrdinalIgnoreCase)
    {
        "com.apple.Terminal", "com.googlecode.iterm2", "org.alacritty", "io.alacritty", "net.kovidgoyal.kitty", "com.github.wez.wezterm",
        "dev.warp.Warp-Stable", "dev.warp.Warp", "co.zeit.hyper", "com.mitchellh.ghostty", "org.tabby", "com.termius-dmg.mac",
        "com.panic.Prompt3",
    };

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "com.apple.Safari", "com.apple.SafariTechnologyPreview", "com.google.Chrome", "com.google.Chrome.canary", "org.mozilla.firefox",
        "com.microsoft.edgemac", "com.brave.Browser", "com.operasoftware.Opera", "com.vivaldi.Vivaldi", "company.thebrowser.Browser",
        "app.zen-browser.zen", "org.chromium.Chromium", "com.openai.atlas",
    };

    private static readonly HashSet<string> CodeEditors = new(StringComparer.OrdinalIgnoreCase)
    {
        "com.microsoft.VSCode", "com.microsoft.VSCodeInsiders", "com.todesktop.230313mzl4w4u92", "com.exafunction.windsurf",
        "com.vscodium", "dev.zed.Zed", "com.apple.dt.Xcode", "com.google.antigravity",
    };

    /// <summary>The frontmost app right now.</summary>
    public static TargetInfo InspectFrontmost()
    {
        var (pid, bundleId, name) = Apps.Frontmost();
        return Inspect(pid, bundleId, name);
    }

    public static TargetInfo Inspect(int pid)
    {
        var (bundleId, name) = Apps.Describe(pid);
        return Inspect(pid, bundleId, name);
    }

    private static TargetInfo Inspect(int pid, string bundleId, string name)
    {
        var own = pid == Environment.ProcessId;
        var focus = own || pid <= 0 ? default : InspectFocusedElement(pid);
        var isTerminal = Terminals.Contains(bundleId) || focus.LooksLikeTerminal;
        // Secure input is switched on by password fields (and by terminals' Secure Keyboard Entry, but
        // terminals never receive keystrokes from PromptForge anyway).
        var isPassword = focus.IsPassword || (!isTerminal && !own && Carbon.SecureInputActive);
        return new TargetInfo(pid, bundleId, name, Label(bundleId, focus.WindowTitle ?? "", isTerminal), isTerminal, isPassword, own);
    }

    /// <summary>
    /// Accessibility look at the focused control: password fields are refused, and terminals embedded in
    /// editors (VS Code's xterm.js) are treated as terminals. Bounded by a messaging timeout because an
    /// unresponsive app would otherwise block the call.
    /// </summary>
    private static FocusFacts InspectFocusedElement(int pid)
    {
        if (!AX.IsTrusted) return default;
        var app = AX.AXUIElementCreateApplication(pid);
        var focused = IntPtr.Zero;
        var window = IntPtr.Zero;
        try
        {
            AX.AXUIElementSetMessagingTimeout(app, 0.4f);
            window = AX.CopyAttribute(app, "AXFocusedWindow");
            focused = AX.CopyAttribute(app, "AXFocusedUIElement");
            var subrole = AX.StringAttribute(focused, "AXSubrole") ?? "";
            var description = AX.StringAttribute(focused, "AXDescription") ?? AX.StringAttribute(focused, "AXTitle") ?? "";
            var role = AX.StringAttribute(focused, "AXRoleDescription") ?? "";
            var looksLikeTerminal = description.StartsWith("Terminal ", StringComparison.Ordinal)
                                    || role.Contains("terminal", StringComparison.OrdinalIgnoreCase);
            return new FocusFacts(subrole == "AXSecureTextField", looksLikeTerminal, AX.StringAttribute(window, "AXTitle"));
        }
        finally
        {
            CF.Release(focused);
            CF.Release(window);
            CF.Release(app);
        }
    }

    private static string? Label(string bundleId, string title, bool isTerminal)
    {
        if (isTerminal) return "a terminal (possibly a coding agent CLI)";
        if (Browsers.Contains(bundleId))
        {
            if (Has(title, "ChatGPT")) return "ChatGPT (browser)";
            if (Has(title, "Claude")) return "Claude (browser)";
            if (Has(title, "Gemini")) return "Gemini (browser)";
            if (Has(title, "Perplexity")) return "Perplexity (browser)";
            if (Has(title, "Copilot")) return "Microsoft Copilot (browser)";
            if (Has(title, "Gmail") || Has(title, "Outlook")) return "an email client (browser)";
            if (Has(title, "Google Docs") || Has(title, "Notion")) return "a document editor (browser)";
            return "a web page";
        }
        if (CodeEditors.Contains(bundleId) || bundleId.StartsWith("com.jetbrains.", StringComparison.OrdinalIgnoreCase))
            return "a code editor (likely its AI chat panel)";
        return bundleId.ToLowerInvariant() switch
        {
            "com.anthropic.claudefordesktop" => "the Claude desktop app",
            "com.openai.chat" => "the ChatGPT desktop app",
            "com.apple.mail" or "com.microsoft.outlook" or "org.mozilla.thunderbird" => "an email client",
            "com.microsoft.word" or "com.apple.iwork.pages" or "notion.id" or "md.obsidian" or "com.apple.notes" => "a document editor",
            "com.apple.textedit" or "com.barebones.bbedit" or "com.coteditor.coteditor" => "a plain text editor",
            _ => null,
        };
    }

    private static bool Has(string title, string word) => title.Contains(word, StringComparison.OrdinalIgnoreCase);

    private readonly record struct FocusFacts(bool IsPassword, bool LooksLikeTerminal, string? WindowTitle);
}

/// <summary>Running applications, via NSWorkspace and NSRunningApplication.</summary>
internal static class Apps
{
    private const nint ActivateAllWindows = 1;
    private const nint ActivateIgnoringOtherApps = 2;

    public static (int Pid, string BundleId, string Name) Frontmost()
    {
        using var pool = new AutoreleasePool();
        var app = ObjC.Send(ObjC.Send(ObjC.Class("NSWorkspace"), "sharedWorkspace"), "frontmostApplication");
        if (app == IntPtr.Zero) return (0, "", "");
        return ((int)ObjC.Send(app, "processIdentifier"), ObjC.ToManagedString(ObjC.Send(app, "bundleIdentifier")) ?? "",
            ObjC.ToManagedString(ObjC.Send(app, "localizedName")) ?? "");
    }

    public static int FrontmostPid() => Frontmost().Pid;

    public static (string BundleId, string Name) Describe(int pid)
    {
        using var pool = new AutoreleasePool();
        var app = Running(pid);
        return (ObjC.ToManagedString(ObjC.Send(app, "bundleIdentifier")) ?? "", ObjC.ToManagedString(ObjC.Send(app, "localizedName")) ?? "");
    }

    public static bool IsRunning(int pid)
    {
        using var pool = new AutoreleasePool();
        var app = Running(pid);
        return app != IntPtr.Zero && !ObjC.SendBool(app, "isTerminated");
    }

    /// <summary>Activates an app and waits until it is frontmost.</summary>
    public static async Task<bool> BringToFrontAsync(int pid)
    {
        if (pid <= 0) return false;
        using (new AutoreleasePool())
        {
            var app = Running(pid);
            if (app == IntPtr.Zero) return false;
            ObjC.SendBool(app, "activateWithOptions:", ActivateAllWindows | ActivateIgnoringOtherApps);
        }

        for (var i = 0; i < 25; i++)
        {
            if (FrontmostPid() == pid)
            {
                await Task.Delay(80); // let the app restore focus to its text box
                return true;
            }
            await Task.Delay(20);
        }
        return false;
    }

    private static IntPtr Running(int pid) =>
        pid <= 0 ? IntPtr.Zero : ObjC.Send(ObjC.Class("NSRunningApplication"), "runningApplicationWithProcessIdentifier:", pid);
}
