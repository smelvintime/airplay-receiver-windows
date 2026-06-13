using AirPlayReceiver.Services;
using FFmpeg.AutoGen;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace AirPlayReceiver;

public partial class App : Application
{
    private MainWindow? _window;
    private AirPlayService? _airPlayService;

    public App()
    {
        this.InitializeComponent();

        // Point FFmpeg.AutoGen at our bundled DLLs before any codec call.
        ffmpeg.RootPath = System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "native", "x64");
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        // Boot the AirPlay service stack (mDNS + RTSP server).
        // Pass the window so the decoder/session can drive the HUD and overlays.
        _airPlayService = new AirPlayService(_window.VideoPresenter, _window);
        await _airPlayService.StartAsync();

        _window.Closed += async (_, _) =>
        {
            if (_airPlayService is not null)
                await _airPlayService.StopAsync();
        };
    }
}
