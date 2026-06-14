using System;
using System.Runtime.InteropServices;

namespace AirPlayReceiver.Services;

/// <summary>
/// Minimal Windows system-tray (notification area) icon via Win32
/// <c>Shell_NotifyIcon</c>, so the receiver can run in the background with its
/// window hidden while still advertising AirPlay — the way UxPlay stays resident.
///
/// WinUI 3 has no built-in tray support, so we subclass the host window (the real
/// top-level HWND) with <c>SetWindowSubclass</c> to receive the tray's callback
/// message and the context-menu <c>WM_COMMAND</c>s. Everything runs on the UI
/// thread that owns the window, so the Open/Exit callbacks are safe to touch UI.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    // ── Win32 constants ───────────────────────────────────────────────────────
    private const int WM_APP            = 0x8000;
    private const int WM_TRAYICON       = WM_APP + 1;
    private const int WM_LBUTTONUP      = 0x0202;
    private const int WM_LBUTTONDBLCLK  = 0x0203;
    private const int WM_RBUTTONUP      = 0x0205;
    private const int WM_COMMAND        = 0x0111;

    private const uint NIM_ADD    = 0x0;
    private const uint NIM_DELETE = 0x2;
    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON    = 0x2;
    private const uint NIF_TIP     = 0x4;

    private const uint IMAGE_ICON     = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint MF_STRING       = 0x0;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const int IDM_OPEN = 1;
    private const int IDM_EXIT = 2;

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly IntPtr        _hwnd;
    private readonly uint          _id = 1;
    private readonly SUBCLASSPROC  _subclassProc; // kept alive against GC
    private readonly Action        _onOpen;
    private readonly Action        _onExit;
    private IntPtr _hIcon;
    private bool   _added;
    private bool   _disposed;

    public TrayIcon(IntPtr hwnd, string tooltip, string iconPath, Action onOpen, Action onExit)
    {
        _hwnd   = hwnd;
        _onOpen = onOpen;
        _onExit = onExit;

        _hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
        if (_hIcon == IntPtr.Zero)
            _hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION fallback

        // Intercept the tray callback + menu commands on the window's message loop.
        _subclassProc = SubclassProc;
        SetWindowSubclass(_hwnd, _subclassProc, IntPtr.Zero, IntPtr.Zero);

        var data = new NOTIFYICONDATA
        {
            cbSize           = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd             = _hwnd,
            uID              = _id,
            uFlags           = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon            = _hIcon,
            szTip            = tooltip,
        };
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uId, IntPtr dwRef)
    {
        switch (msg)
        {
            case WM_TRAYICON:
                int evt = (int)(lParam.ToInt64() & 0xFFFF);
                if (evt is WM_LBUTTONUP or WM_LBUTTONDBLCLK) _onOpen();
                else if (evt == WM_RBUTTONUP)                ShowContextMenu();
                return IntPtr.Zero;

            case WM_COMMAND:
                switch ((int)(wParam.ToInt64() & 0xFFFF))
                {
                    case IDM_OPEN: _onOpen(); return IntPtr.Zero;
                    case IDM_EXIT: _onExit(); return IntPtr.Zero;
                }
                break;
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        IntPtr menu = CreatePopupMenu();
        AppendMenu(menu, MF_STRING, IDM_OPEN, "Open AirPlay Receiver");
        AppendMenu(menu, MF_STRING, IDM_EXIT, "Exit");

        GetCursorPos(out POINT pt);
        // Required so the menu dismisses correctly when clicking elsewhere.
        SetForegroundWindow(_hwnd);
        TrackPopupMenuEx(menu, TPM_RIGHTBUTTON, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_added)
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd   = _hwnd,
                uID    = _id,
            };
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }

        RemoveWindowSubclass(_hwnd, _subclassProc, IntPtr.Zero);
        if (_hIcon != IntPtr.Zero) { DestroyIcon(_hIcon); _hIcon = IntPtr.Zero; }
    }

    // ── Interop ───────────────────────────────────────────────────────────────

    private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint   cbSize;
        public IntPtr hWnd;
        public uint   uID;
        public uint   uFlags;
        public uint   uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint   dwState;
        public uint   dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint   uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]  public string szInfoTitle;
        public uint   dwInfoFlags;
        public Guid   guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, IntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
}
