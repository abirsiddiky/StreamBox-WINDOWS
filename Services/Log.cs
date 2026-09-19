using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace StreamBox.Services;

/// <summary>
/// Ultra-robust static logger. Used from the FIRST line of Main() (before AppBuilder),
/// so every method swallows its own exceptions and NEVER throws. Writes to
/// %LocalAppData%\StreamBox\logs\startup.log and mirrors to Debug/Console.
/// File I/O is batched on a background thread to avoid per-call disk overhead.
/// </summary>
public static class Log
{
    private static readonly ConcurrentQueue<string> _queue = new();
    private static readonly object _flushGate = new();
    private static string _logDir = "";
    private static string _logFile = "";
    private static bool _ready;

    /// <summary>Directory holding the log files (for fallback dialogs to point at).</summary>
    public static string LogDirectory => _logDir;

    /// <summary>Full path to startup.log (for fallback dialogs to point at).</summary>
    public static string LogFilePath => _logFile;

    static Log()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _logDir = Path.Combine(localAppData, "StreamBox", "logs");
            Directory.CreateDirectory(_logDir);
            _logFile = Path.Combine(_logDir, "startup.log");

            // Roll the log if it grows beyond ~2 MB so it never balloons.
            try
            {
                var fi = new FileInfo(_logFile);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                {
                    var bak = Path.Combine(_logDir, "startup.prev.log");
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(_logFile, bak);
                }
            }
            catch { /* rolling is best-effort */ }

            _ready = true;

            // Start background flush thread
            var flusher = new Thread(FlushLoop)
            {
                IsBackground = true,
                Name = "Log.Flusher"
            };
            flusher.Start();
        }
        catch
        {
            // If even the logger can't init, we degrade to Debug/Console only.
            _ready = false;
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex)
        => Write("ERROR", message + Environment.NewLine + ex);

    /// <summary>Synchronously drain any remaining queued lines to disk.
    /// Call from Program.cs finally block so nothing is lost on exit.</summary>
    public static void Flush()
    {
        DrainQueue();
    }

    /// <summary>Marks a clear session boundary at process start.</summary>
    public static void SessionStart(string version)
    {
        Write("INFO", "==================================================");
        Write("INFO", $"StreamBox session start  v{version}  pid={Environment.ProcessId}");
        Write("INFO", $"OS={Environment.OSVersion}  64bit={Environment.Is64BitProcess}");
        Write("INFO", "==================================================");
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] [t{Environment.CurrentManagedThreadId:D2}] {message}";

        // Always mirror to the debugger/console — helps when the file can't be written.
        try { Debug.WriteLine(line); } catch { }
        try { Console.WriteLine(line); } catch { }

        if (!_ready) return;

        _queue.Enqueue(line + Environment.NewLine);
    }

    /// <summary>Background thread: wakes every 500ms and flushes queued lines to disk in one batch.</summary>
    private static void FlushLoop()
    {
        while (true)
        {
            Thread.Sleep(500);
            DrainQueue();
        }
    }

    /// <summary>Dequeues all pending lines and writes them in a single File.AppendAllText call.</summary>
    private static void DrainQueue()
    {
        if (_queue.IsEmpty) return;

        var sb = new StringBuilder();
        while (_queue.TryDequeue(out var line))
        {
            sb.Append(line);
        }

        if (sb.Length == 0) return;

        lock (_flushGate)
        {
            try
            {
                File.AppendAllText(_logFile, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Never let logging crash the app.
            }
        }
    }
}
