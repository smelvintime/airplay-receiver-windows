using AirPlayReceiver.Rendering;
using AirPlayReceiver.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace AirPlayReceiver;

/// <summary>
/// Main application window.
/// Responsibilities:
///   - Hosts the SwapChainPanel used by VideoPresenter for D3D11 rendering.
///   - Manages window state: normal, maximised, and true borderless fullscreen.
///   - Maintains aspect ratio letterboxing when the window is resized.
///   - Exposes the VideoPresenter property so App.xaml.cs can hand it to AirPlayService.
/// </summary>
public sealed partial class MainWindow : Window
{
    // ── Public surface ────────────────────────────────────────────────────────

    /// <summary>The SwapChainPanel that receives rendered video frames.</summary>
    /// <remarks>
    /// Backed by the <c>x:Name="VideoSurface"</c> element. The name is kept
    /// distinct from this property so the XAML-generated field doesn't collide.
    /// </remarks>
    public Microsoft.UI.Xaml.Controls.SwapChainPanel VideoPresenter => VideoSurface;

    // ── Private state ─────────────────────────────────────────────────────────

    private readonly AppWindow _appWindow;
    private bool _isFullscreen;

    // System-tray integration for background operation.
    private TrayIcon? _tray;
    private bool      _allowClose;
    private bool      _sessionActive;   // a phone is currently mirroring

    /// <summary>Raised when the user chooses Exit from the tray menu (real shutdown).</summary>
    public event Action? ExitRequested;

    /// <summary>Raised when the window is closed during a live session — end it (disconnect the phone).</summary>
    public event Action? SessionEndRequested;

    // Last stream dimensions reported by the decoder (default 16:9 until known).
    private double _streamWidth  = 1920;
    private double _streamHeight = 1080;

    // ── Construction ──────────────────────────────────────────────────────────

    public MainWindow()
    {
        this.InitializeComponent();

        // ── Window chrome ────────────────────────────────────────────────────
        _appWindow = this.AppWindow;
        _appWindow.Title = "AirPlay Receiver";
        _appWindow.Resize(new SizeInt32(1280, 720));
        ConfigureCustomTitleBar();

        // ── Keyboard shortcuts ───────────────────────────────────────────────
        // Register F11 for fullscreen toggle at the window level.
        if (Content is FrameworkElement root)
        {
            root.KeyDown += Root_KeyDown;
        }

        // ── Layout ───────────────────────────────────────────────────────────
        RootGrid.SizeChanged += RootGrid_SizeChanged;

        // ── System tray (background operation) ───────────────────────────────
        // Closing the window hides it to the tray instead of quitting, so the
        // AirPlay service keeps advertising. Only the tray's Exit shuts down.
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        _tray = new TrayIcon(hwnd, "AirPlay Receiver", iconPath, ShowFromTray, RequestExit);

        _appWindow.Closing += OnAppWindowClosing;
    }

    // ── Tray / background lifecycle ────────────────────────────────────────────

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;          // real exit via the tray menu
        args.Cancel = true;               // otherwise: keep running in the background
        if (_sessionActive)
            SessionEndRequested?.Invoke(); // disconnect the phone (end the session)
        _appWindow.Hide();                // drop back to the tray, still advertising
    }

    /// <summary>Restores the window from the tray and brings it to the foreground.</summary>
    public void ShowFromTray()
    {
        _appWindow.Show();
        if (_appWindow.Presenter is OverlappedPresenter p) p.Restore();
        this.Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    /// <summary>Hides the window to the tray; the AirPlay service keeps running.</summary>
    public void HideToTray() => _appWindow.Hide();

    private void RequestExit()
    {
        _allowClose = true;
        _tray?.Dispose();
        _tray = null;
        ExitRequested?.Invoke();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // ── Custom title bar ──────────────────────────────────────────────────────

    private void ConfigureCustomTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarGrid);

        // Hide the default caption buttons so we can style our own if desired.
        // (They still work — Windows renders them on the right side automatically
        //  when ExtendsContentIntoTitleBar is true.)
    }

    // ── Aspect-ratio letterboxing ─────────────────────────────────────────────

    /// <summary>
    /// Called by VideoDecoder when the first frame reveals the stream dimensions.
    /// Thread-safe: dispatches to the UI thread.
    /// </summary>
    public void UpdateStreamDimensions(int width, int height)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _streamWidth  = width;
            _streamHeight = height;
            ApplyLetterbox(RootGrid.ActualWidth, RootGrid.ActualHeight);
        });
    }

    /// <summary>
    /// Called by VideoDecoder to update the HUD text (resolution, codec, fps).
    /// </summary>
    public void UpdateHud(string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            InfoText.Text   = text;
            InfoHud.Visibility = Visibility.Visible;
        });
    }

    /// <summary>
    /// Called by AirPlayService when a session starts (hides idle overlay).
    /// </summary>
    public void OnSessionStarted()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _sessionActive = true;
            IdleOverlay.Visibility = Visibility.Collapsed;
            ShowFromTray();   // auto-open so the mirrored screen is visible on connect
        });
    }

    /// <summary>
    /// Called by AirPlayService when a session ends (shows idle overlay).
    /// </summary>
    public void OnSessionEnded()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _sessionActive = false;
            IdleOverlay.Visibility = Visibility.Visible;
            InfoHud.Visibility     = Visibility.Collapsed;
        });
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyLetterbox(e.NewSize.Width, e.NewSize.Height);

    /// <summary>
    /// Sizes the SwapChainPanel to the largest rectangle that fits the
    /// stream's aspect ratio inside the given container dimensions.
    /// Centres the result with equal margins on the letterbox sides.
    /// </summary>
    private void ApplyLetterbox(double containerW, double containerH)
    {
        if (_streamWidth <= 0 || _streamHeight <= 0 || containerW <= 0 || containerH <= 0)
            return;

        double aspectRatio  = _streamWidth / _streamHeight;
        double panelW, panelH;

        if (containerW / containerH > aspectRatio)
        {
            // Container is wider than the stream: pillarbox (vertical bars).
            panelH = containerH;
            panelW = containerH * aspectRatio;
        }
        else
        {
            // Container is taller than the stream: letterbox (horizontal bars).
            panelW = containerW;
            panelH = containerW / aspectRatio;
        }

        VideoPresenter.Width  = panelW;
        VideoPresenter.Height = panelH;
    }

    // ── Fullscreen toggle ─────────────────────────────────────────────────────

    private void VideoPresenter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        => ToggleFullscreen();

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.F11)
            ToggleFullscreen();
    }

    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;

        if (_isFullscreen)
            EnterFullscreen();
        else
            ExitFullscreen();
    }

    private void EnterFullscreen()
    {
        // AppWindowPresenterKind.FullScreen hides the taskbar, removes the
        // window border entirely, and expands to the full monitor.
        _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

        // Hide the title bar drag region so it doesn't overlay the video.
        TitleBarGrid.Visibility = Visibility.Collapsed;
    }

    private void ExitFullscreen()
    {
        _appWindow.SetPresenter(AppWindowPresenterKind.Default);
        TitleBarGrid.Visibility = Visibility.Visible;
    }
}
