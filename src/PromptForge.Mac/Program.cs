using System.Runtime.Versioning;
using Avalonia;
using PromptForge.Mac.Infrastructure;

[assembly: SupportedOSPlatform("macos13.0")]

namespace PromptForge.Mac;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--eval")
            return EvalCommand.Run(args[1..]);
        if (args.Length > 0 && args[0] == "--eval-compare")
            return EvalCommand.Compare(args[1..]);

        if (!OperatingSystem.IsMacOSVersionAtLeast(13))
        {
            Console.Error.WriteLine("This build of PromptForge needs macOS 13 (Ventura) or later. On Windows, use PromptForge.exe.");
            return 1;
        }

        // One copy at a time. On macOS, FileShare.None takes an exclusive lock that is released when the
        // process ends, however it ends. Opening the app again from Finder doesn't start a second copy:
        // macOS re-activates this one, which shows Settings (see App.OnFrameworkInitializationCompleted).
        AppPaths.EnsurePrivateDirectory(AppPaths.DataDir);
        FileStream instanceLock;
        try
        {
            instanceLock = new FileStream(AppPaths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("unobserved task exception", e.Exception);
            e.SetObserved();
        };

        using (instanceLock)
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .LogToTrace();
}
