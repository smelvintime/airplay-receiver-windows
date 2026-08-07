using System;
using System.IO;
using System.Linq;
using System.Text;

namespace AirPlayReceiver.Services;

/// <summary>
/// Minimal thread-safe static file logger, so diagnostics are visible in
/// Release builds (Debug.WriteLine compiles away outside the debugger).
///
///   • One file per app run: %LOCALAPPDATA%\AirPlayReceiver\logs\airplay-{yyyyMMdd-HHmmss}.log
///   • Line format: "HH:mm:ss.fff [level] [tag] msg"
///   • Every write also goes to System.Diagnostics.Debug.WriteLine so the
///     debugger Output window experience is unchanged.
///   • AutoFlush: a crash right after a write still leaves the line on disk.
///   • Logging must never crash the app — all IO errors are swallowed; if the
///     file can't be opened, writes silently fall back to Debug output only.
///   • Retention: on startup, log files beyond the 10 most recent are deleted.
/// </summary>
public static class Log
{
    private const int MaxLogFiles = 10;

    private static readonly object Sync = new();
    private static StreamWriter? _writer;
    private static bool _initialized;

    /// <summary>Full path of this run's log file, or null when file logging is unavailable.</summary>
    public static string? CurrentLogFile { get; private set; }

    /// <summary>
    /// Opens this run's log file and writes the startup banner + environment
    /// check. Called explicitly from App's constructor; any earlier Log call
    /// triggers it lazily, so lines are never dropped.
    /// </summary>
    public static void Init()
    {
        lock (Sync)
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AirPlayReceiver", "logs");
                Directory.CreateDirectory(dir);
                PruneOldLogs(dir);

                string path = Path.Combine(dir, $"airplay-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                _writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
                CurrentLogFile = path;
            }
            catch
            {
                // No file logging (disk full, ACLs, …) — Debug output still works.
                _writer        = null;
                CurrentLogFile = null;
            }

            WriteStartupBanner();
        }
    }

    public static void Info(string tag, string msg) => Write("info", tag, msg);

    public static void Warn(string tag, string msg) => Write("warn", tag, msg);

    public static void Error(string tag, string msg, Exception? ex = null)
        => Write("error", tag, ex is null ? msg : $"{msg}\n{ex}");

    // ── Implementation ────────────────────────────────────────────────────────

    private static void Write(string level, string tag, string msg)
    {
        if (!_initialized) Init();

        string line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] [{tag}] {msg}";
        System.Diagnostics.Debug.WriteLine(line);

        lock (Sync)
        {
            try { _writer?.WriteLine(line); }
            catch { /* logging must never crash the app */ }
        }
    }

    /// <summary>
    /// Deletes all but the newest (MaxLogFiles − 1) existing log files, so that
    /// including this run's new file at most MaxLogFiles remain on disk.
    /// </summary>
    private static void PruneOldLogs(string dir)
    {
        try
        {
            foreach (FileInfo old in new DirectoryInfo(dir)
                         .GetFiles("airplay-*.log")
                         .OrderByDescending(f => f.LastWriteTimeUtc)
                         .Skip(MaxLogFiles - 1))
            {
                try { old.Delete(); } catch { /* in use / gone — ignore */ }
            }
        }
        catch { /* retention is best-effort */ }
    }

    /// <summary>
    /// App/OS versions plus an environment check for every native DLL the video
    /// pipeline depends on. One loud line per DLL: a missing avcodec or playfair
    /// is the difference between video and a black window, so it must be the
    /// first thing visible when a user sends in a log.
    /// </summary>
    private static void WriteStartupBanner()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly().GetName();
            Info("env", $"AirPlayReceiver {asm.Version} starting (pid {Environment.ProcessId})");
            Info("env", $"OS: {Environment.OSVersion.VersionString}  " +
                        $".NET: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}  " +
                        $"arch: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
            Info("env", $"BaseDirectory: {AppContext.BaseDirectory}");
            Info("env", $"Log file: {CurrentLogFile ?? "(unavailable — Debug output only)"}");

            CheckNativeDll(Path.Combine("native", "x64", "avcodec-61.dll"),
                           "video decode will be unavailable");
            CheckNativeDll(Path.Combine("native", "x64", "avutil-59.dll"),
                           "video decode will be unavailable");
            CheckNativeDll(Path.Combine("native", "x64", "swscale-8.dll"),
                           "AirPlay Video (URL playback) frame conversion will be unavailable");
            CheckNativeDll(Path.Combine("native", "x64", "swresample-5.dll"),
                           "audio resampling will be unavailable");
            CheckNativeDll("playfair.dll",
                           "FairPlay key unwrap will fail — mirror video will be BLACK");
        }
        catch { /* the banner is best-effort */ }
    }

    private static void CheckNativeDll(string relativePath, string missingConsequence)
    {
        try
        {
            string full = Path.Combine(AppContext.BaseDirectory, relativePath);
            string name = Path.GetFileName(relativePath);
            if (File.Exists(full))
            {
                long bytes = new FileInfo(full).Length;
                string size = bytes >= 1024 * 1024
                    ? $"{bytes / (1024.0 * 1024.0):F1}MB"
                    : $"{bytes / 1024.0:F0}KB";
                Info("env", $"{name}: PRESENT ({size})");
            }
            else
            {
                Error("env", $"{name}: MISSING — {missingConsequence}");
            }
        }
        catch { /* best-effort */ }
    }
}
