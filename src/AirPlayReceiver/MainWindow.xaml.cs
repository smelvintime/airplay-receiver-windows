using AirPlayReceiver.Rendering;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
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
    public Microsoft.UI.Xaml.Controls.SwapChainPanel VideoPresenter => _videoPresenter;

    // ── Private state ─────────────────────────────────────────────────────────

    private readonly AppWindow _appWindow;
    private bool _isFullscreen;

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

        // Expose a SwapChainPanel reference under the expected field name
        // so XAML x:Name binding is accessible here.
        _videoPresenter = VideoPresenter;
    }

    // XAML x:Name back-reference (compiler generates field for x:Name="VideoPresenter"
    // but we alias it so code is self-documenting).
    private readonly Microsoft.UI.Xaml.Controls.SwapChainPanel _videoPresenter;

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
            IdleOverlay.Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>
    /// Called by AirPlayService when a session ends (shows idle overlay).
    /// </summary>
    public void OnSessionEnded()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
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
