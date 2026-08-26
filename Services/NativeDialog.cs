using System;
using System.Runtime.InteropServices;

namespace StreamBox.Services;

/// <summary>
/// Minimal Win32 MessageBox wrapper for FATAL fallback dialogs during startup.
/// Deliberately does NOT depend on Avalonia — it must work even if Avalonia itself
/// failed to initialize (the whole point of the startup safety net).
/// </summary>
public static class NativeDialog
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x0;
    private const uint MB_ICONERROR = 0x10;
    private const uint MB_TOPMOST = 0x40000;

    /// <summary>Shows a blocking, top-most error box. Never throws.</summary>
    public static void ShowError(string caption, string text)
    {
        try
        {
            MessageBoxW(IntPtr.Zero, text, caption, MB_OK | MB_ICONERROR | MB_TOPMOST);
        }
        catch
        {
            // Absolute last resort — nothing else we can do.
        }
    }
}
