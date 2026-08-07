using System;
using System.Globalization;
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
///   • Retention: on startup, log files beyond the 10 most recent are deleted,
///     and one run's file stops growing at MaxLogFileBytes.
///   • Every message is sanitised: attacker-supplied header and plist values are
///     logged verbatim by design, so control characters are neutralised here.
/// </summary>
public static class Log
{
    private const int MaxLogFiles = 10;

    /// <summary>
    /// Ceiling on one run's log file.
    ///
    /// Retention was count-based only, which bounds how many files exist but not
    /// how large the current one gets — and header and plist values arrive from an
    /// unauthenticated LAN sender, are logged verbatim on purpose, and are not
    /// rate-limited. Without this, filling the disk is a request loop. At the cap
    /// we say so once and stop writing to the file; Debug output continues.
    /// </summary>
    private const long MaxLogFileBytes = 64L * 1024 * 1024;

    /// <summary>
    /// What a newline inside a message is replaced with.
    ///
    /// Multi-line dumps (plists, header blocks, stack traces) are the point of
    /// this log, so newlines are re-indented rather than escaped away — but a
    /// continuation line now starts with this marker instead of at column 0, so
    /// nothing injected into a value can be mistaken for a real
    /// "HH:mm:ss.fff [level] [tag]" line.
    /// </summary>
    private const string ContinuationPrefix = "\n         | ";

    private static readonly object Sync = new();
    private static StreamWriter? _writer;
    private static bool _initialized;
    private static bool _sizeCapReported;

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

                // Pid suffix: two instances starting in the same second must not
                // fight over one filename (the loser would silently lose all logs).
                string path = Path.Combine(dir, $"airplay-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
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
    {
        string full = msg;
        if (ex is not null)
        {
            // ex.ToString() can itself throw (exotic exception types); the
            // crash handler in App.xaml.cs depends on this never propagating.
            try { full = $"{msg}\n{ex}"; }
            catch { full = $"{msg}\n(ex.ToString() failed: {ex.GetType().Name})"; }
        }
        Write("error", tag, full);
    }

    // ── Implementation ────────────────────────────────────────────────────────

    private static void Write(string level, string tag, string msg)
    {
        try
        {
            if (!_initialized) Init();

            // Sanitised here, once, rather than at the call sites: every call site
            // that logs a header or plist value is one that can be forgotten, and
            // LogVideoHeaders/DumpPlist log network input verbatim by design.
            string line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] [{tag}] {Sanitize(msg)}";
            System.Diagnostics.Debug.WriteLine(line);

            lock (Sync)
            {
                try
                {
                    if (_writer is null) return;

                    _writer.WriteLine(line);

                    // AutoFlush is on, so the stream length is exact after the write.
                    if (_writer.BaseStream.Length >= MaxLogFileBytes && !_sizeCapReported)
                    {
                        _sizeCapReported = true;
                        _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [error] [log] This file reached the " +
                                          $"{MaxLogFileBytes} byte cap — no further lines will be written to disk " +
                                          "this run (Debug output continues).");
                        _writer.Dispose();
                        _writer = null;
                    }
                }
                catch { /* logging must never crash the app */ }
            }
        }
        catch { /* formatting/init failure — logging must never crash the app */ }
    }

    /// <summary>
    /// Neutralises the control characters an attacker-supplied value could use to
    /// forge log lines or corrupt a terminal.
    ///
    /// Newlines survive as indented continuations (see
    /// <see cref="ContinuationPrefix"/>) because multi-line dumps are the point of
    /// this log; a lone CR and every other C0/C1 control becomes a visible
    /// <c>\xNN</c> escape, so nothing is silently dropped either. Tab is left
    /// alone — it only ever comes from our own formatting.
    /// </summary>
    private static string Sanitize(string msg)
    {
        bool needsWork = false;
        foreach (char c in msg)
        {
            if (c != '\t' && char.IsControl(c)) { needsWork = true; break; }
        }
        if (!needsWork) return msg;

        var sb = new StringBuilder(msg.Length + 16);
        for (int i = 0; i < msg.Length; i++)
        {
            char c = msg[i];
            if (c == '\n')
            {
                sb.Append(ContinuationPrefix);
            }
            else if (c == '\r')
            {
                // CRLF collapses to one continuation; a lone CR is shown.
                if (i + 1 < msg.Length && msg[i + 1] == '\n') continue;
                sb.Append("\\r");
            }
            else if (c != '\t' && char.IsControl(c))
            {
                sb.Append("\\x").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
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
