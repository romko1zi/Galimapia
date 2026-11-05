using Avalonia;
using System;
using System.Runtime.CompilerServices;
using Galimapia.Canvas;

namespace Galimapia;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (CrashWatch.TryRun(args))
            return;

        Run(args);
    }

    // Not inlined, so the crash watcher process never loads Avalonia.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run(string[] args)
    {
        SessionLog.Start();
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            SessionLog.Action("exit");
        }
        catch (Exception ex)
        {
            SessionLog.Fault("main", ex);
            throw;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
