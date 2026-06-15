using AirPlayReceiver.Services;
using FFmpeg.AutoGen;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace AirPlayReceiver;

public partial class App : Application
{
    private MainWindow? _window;
    private AirPlayService? _airPlayService;

    public App()
    {
        this.InitializeComponent();

        // Catch and log any unhandled exception so a crash leaves a trace even after
        // the debugger detaches (the process otherwise just exits with 0xffffffff).
        WireCrashLogging();

        // Point FFmpeg.AutoGen at our bundled DLLs before any codec call.
        ffmpeg.RootPath = System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "native", "x64");
    }

    private void WireCrashLogging()
    {
        // UI-thread exceptions (WinUI): log and mark handled so a recoverable error
        // (e.g. a render hiccup when a stream tears down) doesn't kill the process.
        this.UnhandledException += (_, e) =>
        {
            LogCrash("UI", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("AppDomain", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("Task", e.Exception);
            e.SetObserved();
        };
    }

    private static void LogCrash(string source, Exception? ex)
    {
        string msg = $"[Crash:{source}] {DateTime.Now:O}\n{ex}\n";
        System.Diagnostics.Debug.WriteLine(msg);
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.AppContext.BaseDirectory, "airplay-crash.log"), msg);
        }
        catch { /* best-effort */ }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Run in the background: register to launch at sign-in so the receiver is
        // always advertising and can be mirrored to at any time. (Toggle off in
        // Windows Settings → Apps → Startup.)
        StartupManager.Enable();

        _window = new MainWindow();
        _window.ExitRequested += OnExitRequested;
        _window.SessionEndRequested += () => _airPlayService?.EndActiveSessions();
        _window.Activate();

        // When launched at sign-in, come up hidden in the system tray so the
        // receiver runs fully in the background, advertising and ready to connect.
        bool launchMinimized = Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, StartupManager.MinimizedArg, StringComparison.OrdinalIgnoreCase));
        if (launchMinimized)
            _window.HideToTray();

        // Boot the AirPlay service stack (mDNS + RTSP server).
        // Pass the window so the decoder/session can drive the HUD and overlays.
        // Guard startup so a failure (e.g. missing FFmpeg DLLs) leaves the window
        // open and usable instead of crashing the process on launch.
        _airPlayService = new AirPlayService(_window.VideoPresenter, _window);
        try
        {
            await _airPlayService.StartAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] AirPlay service failed to start: {ex}");
        }

    }

    /// <summary>
    /// Real shutdown, requested from the tray menu. Closing the window only hides
    /// it to the tray (see <see cref="MainWindow"/>), so this is the single path
    /// that stops the AirPlay service and exits the process.
    /// </summary>
    private async void OnExitRequested()
    {
        if (_airPlayService is not null)
            await _airPlayService.StopAsync();
        Application.Current.Exit();
    }
}
