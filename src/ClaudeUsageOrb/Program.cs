using System;
using System.Threading;
using System.Windows;

namespace ClaudeUsageOrb;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--preview")
        {
            var previewApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            previewApp.Startup += (_, _) =>
            {
                try
                {
                    Preview.Render(args[1]);
                }
                catch (Exception ex)
                {
                    Log.Error("Preview failed", ex);
                }
                previewApp.Shutdown();
            };
            return previewApp.Run();
        }

        using var reveal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClaudeUsageOrb.Reveal");
        using var mutex = new Mutex(true, @"Local\ClaudeUsageOrb.SingleInstance", out bool firstInstance);
        if (!firstInstance)
        {
            // Already running: ask that instance to show its orb (it may be hidden or click-through).
            reveal.Set();
            return 0;
        }

        System.Windows.Forms.Application.EnableVisualStyles();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppController? controller = null;

        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("Unhandled UI exception", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        app.Startup += (_, _) =>
        {
            controller = new AppController(app);
            controller.Start();
            ThreadPool.RegisterWaitForSingleObject(reveal, (_, _) => app.Dispatcher.BeginInvoke(() => controller?.Reveal()), null, Timeout.Infinite, executeOnlyOnce: false);
        };
        app.Exit += (_, _) => controller?.Dispose();
        return app.Run();
    }
}
