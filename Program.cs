using System;
using System.Reflection;
using System.Threading;
using Avalonia;
using StreamBox.Services;

namespace StreamBox;

internal static class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        Log.SessionStart(version);
        Log.Info("Main() entered");

        try
        {
            const string mutexName = @"Local\StreamBox.Singleton";
            _singleInstanceMutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);

            if (!createdNew)
            {
                Log.Warn("Another instance is already running");
                NativeDialog.ShowError("StreamBox", "StreamBox is already running.");
                return 0;
            }

            Log.Info("mutex acquired");

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            Log.Info("AppBuilder exited normally");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Fatal startup error in Main()", ex);
            NativeDialog.ShowError(
                "StreamBox Startup Error",
                "StreamBox could not start.\n\n" +
                $"Details were written to:\n{Log.LogFilePath}");
            return 1;
        }
        finally
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = null;
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Log.Error("AppDomain.CurrentDomain.UnhandledException", ex);
        }
        else
        {
            Log.Error($"AppDomain.CurrentDomain.UnhandledException: {e.ExceptionObject}");
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }
}
