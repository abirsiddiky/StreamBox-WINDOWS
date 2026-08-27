using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using StreamBox.Models;
using StreamBox.Services;

namespace StreamBox.Native;

public sealed class MpvClient : IDisposable
{
    private static readonly object NativeGate = new();
    private static nint _module;
    private static bool _nativeLoaded;

    private readonly nint _handle;
    private readonly Thread _eventThread;
    private readonly CancellationTokenSource _disposeCts = new();
    private bool _disposed;

    public event EventHandler<MpvEventArgs>? MpvEventReceived;

    public MpvClient(nint hostHandle)
    {
        EnsureNativeLoaded();

        _handle = Native.mpv_create();
        if (_handle == nint.Zero)
        {
            throw new InvalidOperationException("mpv_create returned null.");
        }

        Check(Native.mpv_set_option_string(_handle, "terminal", "no"), "set terminal");
        Check(Native.mpv_set_option_string(_handle, "config", "no"), "set config");
        Check(Native.mpv_set_option_string(_handle, "input-default-bindings", "no"), "set input-default-bindings");
        Check(Native.mpv_set_option_string(_handle, "osc", "no"), "set osc");
        Check(Native.mpv_set_option_string(_handle, "keep-open", "no"), "set keep-open");
        Check(Native.mpv_set_option_string(_handle, "keepaspect", "no"), "set keepaspect");
        Check(Native.mpv_set_option_string(_handle, "idle", "yes"), "set idle");
        Check(Native.mpv_set_option_string(_handle, "network-timeout", "30"), "set network-timeout");
        Check(Native.mpv_set_option_string(_handle, "force-window", "yes"), "set force-window");
        Check(Native.mpv_set_option_string(_handle, "wid", hostHandle.ToInt64().ToString()), "set wid");
        Check(Native.mpv_set_option_string(_handle, "user-agent", DefaultUserAgent), "set default user-agent");
        // Hardware decoding: auto-safe tries HW first, falls back to SW automatically.
        // Works across Intel/AMD/NVIDIA without hardcoding a specific vendor.
        Check(Native.mpv_set_option_string(_handle, "hwdec", "auto-safe"), "set hwdec");

        Check(Native.mpv_initialize(_handle), "initialize");
        Check(Native.mpv_observe_property(_handle, 1, "idle-active", MpvFormat.Flag), "observe idle-active");

        _eventThread = new Thread(EventLoop)
        {
            IsBackground = true,
            Name = "StreamBox.mpv.event-loop"
        };
        _eventThread.Start();
    }

    public static string DefaultUserAgent => "StreamBox/1.0 (Windows NT 10.0; Win64; x64) libmpv";

    public void ApplyChannelHeaders(Channel channel)
    {
        ThrowIfDisposed();

        var userAgent = string.IsNullOrWhiteSpace(channel.UserAgent)
            ? DefaultUserAgent
            : channel.UserAgent!;

        Check(Native.mpv_set_property_string(_handle, "user-agent", userAgent), "set user-agent");

        string? referrer = null;
        string? headerFields = null;

        if (channel.ExtraHeaders is { Count: > 0 })
        {
            var headers = new List<string>();
            foreach (var pair in channel.ExtraHeaders)
            {
                if (pair.Key.Equals("referrer", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.Equals("referer", StringComparison.OrdinalIgnoreCase))
                {
                    referrer = pair.Value;
                    continue;
                }

                headers.Add($"{NormalizeHeaderName(pair.Key)}: {pair.Value}");
            }

            headerFields = headers.Count == 0 ? null : string.Join(",", headers);
        }

        Check(Native.mpv_set_property_string(_handle, "referrer", referrer ?? string.Empty), "set referrer");
        Check(Native.mpv_set_property_string(_handle, "http-header-fields", headerFields ?? string.Empty), "set http-header-fields");
    }

    public void LoadFile(string url)
    {
        ThrowIfDisposed();
        Command("loadfile", url, "replace");
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Command("stop");
        }
        catch (Exception ex)
        {
            Log.Warn($"Ignoring mpv stop failure during teardown: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCts.Cancel();

        try
        {
            Native.mpv_terminate_destroy(_handle);
        }
        catch
        {
            // Best-effort during dispose.
        }

        try
        {
            if (!_eventThread.Join(1000))
            {
                Log.Warn("mpv event thread did not exit within 1000ms");
            }
        }
        catch
        {
            // Ignore join failures.
        }

        _disposeCts.Dispose();
    }

    private void EventLoop()
    {
        while (!_disposeCts.IsCancellationRequested)
        {
            try
            {
                var eventPtr = Native.mpv_wait_event(_handle, 0.1);
                if (eventPtr == nint.Zero)
                {
                    continue;
                }

                var mpvEvent = Marshal.PtrToStructure<mpv_event>(eventPtr);
                switch (mpvEvent.event_id)
                {
                    case MpvEventId.None:
                        continue;

                    case MpvEventId.Shutdown:
                        RaiseEvent(new MpvEventArgs(MpvEventKind.Shutdown));
                        return;

                    case MpvEventId.FileLoaded:
                        RaiseEvent(new MpvEventArgs(MpvEventKind.FileLoaded));
                        break;

                    case MpvEventId.PlaybackRestart:
                        RaiseEvent(new MpvEventArgs(MpvEventKind.PlaybackRestart));
                        break;

                    case MpvEventId.EndFile:
                    {
                        var endFile = mpvEvent.data != nint.Zero
                            ? Marshal.PtrToStructure<mpv_event_end_file>(mpvEvent.data)
                            : default;
                        RaiseEvent(new MpvEventArgs(MpvEventKind.EndFile, ErrorCode: endFile.error, ReasonCode: (int)endFile.reason));
                        break;
                    }

                    case MpvEventId.PropertyChange:
                    {
                        if (mpvEvent.data == nint.Zero)
                        {
                            break;
                        }

                        var property = Marshal.PtrToStructure<mpv_event_property>(mpvEvent.data);
                        var name = Marshal.PtrToStringUTF8(property.name) ?? string.Empty;
                        var flagValue = property.data != nint.Zero && Marshal.ReadInt32(property.data) != 0;
                        RaiseEvent(new MpvEventArgs(MpvEventKind.PropertyChange, PropertyName: name, FlagValue: flagValue));
                        break;
                    }

                    case MpvEventId.LogMessage:
                    {
                        if (mpvEvent.data == nint.Zero)
                        {
                            break;
                        }

                        var logMessage = Marshal.PtrToStructure<mpv_event_log_message>(mpvEvent.data);
                        var prefix = Marshal.PtrToStringUTF8(logMessage.prefix) ?? "mpv";
                        var level = Marshal.PtrToStringUTF8(logMessage.level) ?? "info";
                        var text = Marshal.PtrToStringUTF8(logMessage.text)?.Trim() ?? string.Empty;
                        RaiseEvent(new MpvEventArgs(MpvEventKind.Log, Message: $"{prefix}/{level}: {text}"));
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                RaiseEvent(new MpvEventArgs(MpvEventKind.Log, Message: $"event loop error: {ex.Message}"));
            }
        }
    }

    private void RaiseEvent(MpvEventArgs args)
    {
        try
        {
            MpvEventReceived?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            Log.Error("mpv event handler dispatch failed", ex);
        }
    }

    private void Command(params string[] command)
    {
        var allocatedStrings = new List<nint>();
        var commandPointers = new nint[command.Length + 1];

        try
        {
            for (var i = 0; i < command.Length; i++)
            {
                allocatedStrings.Add(StringToUtf8(command[i]));
                commandPointers[i] = allocatedStrings[^1];
            }

            var argsPtr = Marshal.AllocHGlobal(IntPtr.Size * commandPointers.Length);
            try
            {
                Marshal.Copy(commandPointers, 0, argsPtr, commandPointers.Length);
                Check(Native.mpv_command(_handle, argsPtr), $"command {string.Join(' ', command)}");
            }
            finally
            {
                Marshal.FreeHGlobal(argsPtr);
            }
        }
        finally
        {
            foreach (var ptr in allocatedStrings)
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
    }

    private static nint StringToUtf8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + '\0');
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    private static string NormalizeHeaderName(string key)
    {
        return key.Equals("cookie", StringComparison.OrdinalIgnoreCase)
            ? "Cookie"
            : key.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                ? "Authorization"
                : key.Equals("origin", StringComparison.OrdinalIgnoreCase)
                    ? "Origin"
                    : key;
    }

    private static void Check(int errorCode, string operation)
    {
        if (errorCode < 0)
        {
            throw new InvalidOperationException($"mpv {operation} failed with error code {errorCode}.");
        }
    }

    private static void EnsureNativeLoaded()
    {
        if (_nativeLoaded)
        {
            return;
        }

        lock (NativeGate)
        {
            if (_nativeLoaded)
            {
                return;
            }

            var baseDir = AppContext.BaseDirectory;
            var candidatePaths = new[]
            {
                Path.Combine(baseDir, "mpv", "win-x64", "libmpv-2.dll"),
                Path.Combine(baseDir, "mpv", "win-x64", "mpv-2.dll"),
                Path.Combine(baseDir, "mpv", "win-x64", "mpv-1.dll")
            };

            var chosenPath = candidatePaths.FirstOrDefault(File.Exists);
            if (chosenPath is null)
            {
                throw new FileNotFoundException(
                    "Could not find libmpv in the publish/install directory.",
                    string.Join(Environment.NewLine, candidatePaths));
            }

            _module = NativeLibrary.Load(chosenPath);
            Native.Bind(_module);
            _nativeLoaded = true;
            Log.Info($"Loaded libmpv from {chosenPath}");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private enum MpvFormat
    {
        None = 0,
        String = 1,
        OsdString = 2,
        Flag = 3,
        Int64 = 4
    }

    private enum MpvEventId
    {
        None = 0,
        Shutdown = 1,
        LogMessage = 2,
        StartFile = 6,
        EndFile = 7,
        FileLoaded = 8,
        Idle = 11,
        PlaybackRestart = 21,
        PropertyChange = 22
    }

    private enum MpvEndFileReason
    {
        Eof = 0,
        Stop = 2,
        Quit = 3,
        Error = 4,
        Redirect = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct mpv_event
    {
        public MpvEventId event_id;
        public int error;
        public ulong reply_userdata;
        public nint data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct mpv_event_property
    {
        public nint name;
        public MpvFormat format;
        public nint data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct mpv_event_end_file
    {
        public MpvEndFileReason reason;
        public int error;
        public long playlist_entry_id;
        public long playlist_insert_id;
        public int playlist_insert_num_entries;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct mpv_event_log_message
    {
        public nint prefix;
        public nint level;
        public nint text;
        public nint log_level;
    }

    private static class Native
    {
        internal delegate nint mpv_create_delegate();
        internal delegate int mpv_initialize_delegate(nint ctx);
        internal delegate void mpv_terminate_destroy_delegate(nint ctx);
        internal delegate int mpv_set_option_string_delegate(nint ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        internal delegate int mpv_set_property_string_delegate(nint ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        internal delegate int mpv_command_delegate(nint ctx, nint args);
        internal delegate nint mpv_wait_event_delegate(nint ctx, double timeout);
        internal delegate int mpv_observe_property_delegate(nint ctx, ulong replyUserdata, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, MpvFormat format);

        internal static mpv_create_delegate mpv_create = default!;
        internal static mpv_initialize_delegate mpv_initialize = default!;
        internal static mpv_terminate_destroy_delegate mpv_terminate_destroy = default!;
        internal static mpv_set_option_string_delegate mpv_set_option_string = default!;
        internal static mpv_set_property_string_delegate mpv_set_property_string = default!;
        internal static mpv_command_delegate mpv_command = default!;
        internal static mpv_wait_event_delegate mpv_wait_event = default!;
        internal static mpv_observe_property_delegate mpv_observe_property = default!;

        internal static void Bind(nint module)
        {
            mpv_create = Bind<mpv_create_delegate>(module, nameof(mpv_create));
            mpv_initialize = Bind<mpv_initialize_delegate>(module, nameof(mpv_initialize));
            mpv_terminate_destroy = Bind<mpv_terminate_destroy_delegate>(module, nameof(mpv_terminate_destroy));
            mpv_set_option_string = Bind<mpv_set_option_string_delegate>(module, nameof(mpv_set_option_string));
            mpv_set_property_string = Bind<mpv_set_property_string_delegate>(module, nameof(mpv_set_property_string));
            mpv_command = Bind<mpv_command_delegate>(module, nameof(mpv_command));
            mpv_wait_event = Bind<mpv_wait_event_delegate>(module, nameof(mpv_wait_event));
            mpv_observe_property = Bind<mpv_observe_property_delegate>(module, nameof(mpv_observe_property));
        }

        private static T Bind<T>(nint module, string exportName) where T : Delegate
        {
            var proc = NativeLibrary.GetExport(module, exportName);
            return Marshal.GetDelegateForFunctionPointer<T>(proc);
        }
    }
}

public enum MpvEventKind
{
    Shutdown,
    FileLoaded,
    PlaybackRestart,
    EndFile,
    PropertyChange,
    Log
}

public sealed record MpvEventArgs(
    MpvEventKind Kind,
    string? PropertyName = null,
    bool? FlagValue = null,
    string? Message = null,
    int? ErrorCode = null,
    int? ReasonCode = null);
