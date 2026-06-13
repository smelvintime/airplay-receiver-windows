using Microsoft.Win32;
using System;

namespace AirPlayReceiver.Services;

/// <summary>
/// Registers the receiver to launch automatically when the user signs in, so it
/// runs in the background and can be mirrored to at any time. Implemented via the
/// per-user <c>Run</c> registry key (no admin rights needed). The autostart entry
/// passes <c>--minimized</c> so it comes up out of the way.
///
/// Users can always turn this off in Windows Settings → Apps → Startup.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName  = "AirPlayReceiver";
    public const  string MinimizedArg = "--minimized";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Startup] query failed: {ex.Message}");
            return false;
        }
    }

    public static void Enable()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key?.SetValue(ValueName, $"\"{exe}\" {MinimizedArg}");
            System.Diagnostics.Debug.WriteLine("[Startup] enabled (launches at sign-in, minimized).");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Startup] enable failed: {ex.Message}");
        }
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Startup] disable failed: {ex.Message}");
        }
    }
}
