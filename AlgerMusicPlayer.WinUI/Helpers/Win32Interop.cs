using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AlgerMusicPlayer.WinUI.Helpers;

/// <summary>
/// Win32 原生 API 互操作辅助类（用于无边框零白边窗口、DWM 逐像素透明、桌面歌词鼠标穿透、全局热键、窗口置顶等）
/// </summary>
public static class Win32Interop
{
    public const int GWL_EXSTYLE = -20;
    public const int GWL_STYLE = -16;

    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TOPMOST = 0x00000008;

    public const int WS_CAPTION = 0x00C00000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_SYSMENU = 0x00080000;
    public const int WS_POPUP = unchecked((int)0x80000000);

    public const uint LWA_COLORKEY = 0x00000001;
    public const uint LWA_ALPHA = 0x00000002;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public const int WM_NCCALCSIZE = 0x0083;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int WM_ERASEBKGND = 0x0014;
    public const int WM_HOTKEY = 0x0312;
    public const int HTCAPTION = 2;

    // DWM 属性常量
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWCP_ROUND = 2;
    public const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    // 热键修饰符
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fEnable;
        public IntPtr hRgnBlur;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fTransitionOnMaximized;
    }

    public const uint DWM_BB_ENABLE = 0x00000001;
    public const uint DWM_BB_BLURREGION = 0x00000002;

    private static readonly SubclassProc FramelessSubclassDelegate = FramelessWndProc;

    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    public delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("comctl32.dll", SetLastError = true)]
    public static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    public static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    public static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [DllImport("dwmapi.dll")]
    public static extern int DwmEnableBlurBehindWindow(IntPtr hWnd, ref DWM_BLURBEHIND pBlurBehind);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// 彻底移除 WinUI 3 窗口顶部的 7px 白色非客户区边框，并配置圆角与无边框属性
    /// </summary>
    public static void MakeFramelessOverlayWindow(IntPtr hWnd, bool roundCorners = true, bool toolWindow = false, bool perPixelTransparent = false)
    {
        int style = GetWindowLong(hWnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU);
        SetWindowLong(hWnd, GWL_STYLE, style);

        int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOPMOST;
        if (toolWindow)
        {
            exStyle |= WS_EX_TOOLWINDOW;
        }
        if (!perPixelTransparent)
        {
            exStyle |= WS_EX_LAYERED;
        }
        else
        {
            // DirectComposition 逐像素透明不能启用 SetLayeredWindowAttributes，否则 DWM 会将透明背景渲染为纯黑
            exStyle &= ~WS_EX_LAYERED;
        }
        SetWindowLong(hWnd, GWL_EXSTYLE, exStyle);

        if (!perPixelTransparent)
        {
            SetLayeredWindowAttributes(hWnd, 0, 255, LWA_ALPHA);
        }
        else
        {
            EnablePerPixelWindowTransparency(hWnd);
        }

        // 挂载 WM_NCCALCSIZE 子类化钩子，消除顶部 7px 白色非客户区标题条
        SetWindowSubclass(hWnd, FramelessSubclassDelegate, new UIntPtr(9001), UIntPtr.Zero);

        uint borderColor = DWMWA_COLOR_NONE;
        _ = DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(uint));

        uint cornerPref = (uint)(roundCorners ? DWMWCP_ROUND : DWMWCP_DONOTROUND);
        _ = DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(uint));

        SetWindowPos(
            hWnd,
            HWND_TOPMOST,
            0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private static IntPtr FramelessWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (uMsg == WM_NCCALCSIZE && wParam != IntPtr.Zero)
        {
            // 返回 0 使客户区 100% 铺满整个窗口矩形，彻底消除 Windows 11 顶部 7px 白色边框
            return IntPtr.Zero;
        }
        if (uMsg == WM_ERASEBKGND)
        {
            return new IntPtr(1);
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    /// <summary>
    /// 启用 DWM 逐像素 Alpha 透明合成（配合 TransparentWindowBackdrop 使桌面歌词背景 100% 透明）
    /// </summary>
    public static void EnablePerPixelWindowTransparency(IntPtr hWnd)
    {
        var margins = new MARGINS
        {
            cxLeftWidth = -1,
            cxRightWidth = -1,
            cyTopHeight = -1,
            cyBottomHeight = -1
        };
        _ = DwmExtendFrameIntoClientArea(hWnd, ref margins);

        IntPtr hRgn = CreateRectRgn(-2, -2, -1, -1);
        var blur = new DWM_BLURBEHIND
        {
            dwFlags = DWM_BB_ENABLE | DWM_BB_BLURREGION,
            fEnable = true,
            hRgnBlur = hRgn,
            fTransitionOnMaximized = false
        };
        _ = DwmEnableBlurBehindWindow(hWnd, ref blur);
        if (hRgn != IntPtr.Zero)
        {
            _ = DeleteObject(hRgn);
        }
    }

    /// <summary>
    /// 设置窗口置顶状态
    /// </summary>
    public static void SetAlwaysOnTop(IntPtr hWnd, bool alwaysOnTop)
    {
        SetWindowPos(
            hWnd,
            alwaysOnTop ? HWND_TOPMOST : HWND_NOTOPMOST,
            0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// 配置窗口为无边框悬浮工具窗并切换鼠标穿透状态 (WS_EX_TRANSPARENT)
    /// </summary>
    public static void ConfigureOverlayWindow(IntPtr hWnd, bool clickThrough, byte alpha = 255)
    {
        int style = GetWindowLong(hWnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU);
        SetWindowLong(hWnd, GWL_STYLE, style);

        int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        exStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        if (clickThrough)
        {
            exStyle |= WS_EX_TRANSPARENT;
        }
        else
        {
            exStyle &= ~WS_EX_TRANSPARENT;
        }

        SetWindowLong(hWnd, GWL_EXSTYLE, exStyle);
        SetLayeredWindowAttributes(hWnd, 0, alpha, LWA_ALPHA);
        SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>
    /// 切换桌面歌词窗口的鼠标穿透状态（保持 DirectComposition 逐像素全透明，不调用 SetLayeredWindowAttributes 以避免黑底）
    /// </summary>
    public static void SetDesktopLyricClickThrough(IntPtr hWnd, bool clickThrough)
    {
        int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        if (clickThrough)
        {
            exStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT;
        }
        else
        {
            exStyle &= ~(WS_EX_LAYERED | WS_EX_TRANSPARENT);
        }
        SetWindowLong(hWnd, GWL_EXSTYLE, exStyle);
        EnablePerPixelWindowTransparency(hWnd);
    }

    /// <summary>
    /// 触发无边框窗口鼠标拖动
    /// </summary>
    public static void BeginDragWindow(IntPtr hWnd)
    {
        ReleaseCapture();
        SendMessage(hWnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
    }

    // ==================== Win32 系统托盘 (Shell_NotifyIconW + 原生右键菜单) ====================

    public const uint WM_TRAYICON = 0x8000 + 105;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_LBUTTONDBLCLK = 0x0203;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_CONTEXTMENU = 0x007B;
    public const int WM_NULL = 0x0000;

    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;
    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;

    public const uint IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x00000010;
    public const uint LR_DEFAULTSIZE = 0x00000040;

    public const uint MF_STRING = 0x00000000;
    public const uint MF_GRAYED = 0x00000001;
    public const uint MF_DISABLED = 0x00000002;
    public const uint MF_SEPARATOR = 0x00000800;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_BOTTOMALIGN = 0x0020;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadImageW(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>
/// WinUI 3 全透明系统背景画刷（清除 XAML DirectComposition 交换链的默认黑底，使桌面歌词在鼠标离开时 100% 透明）
/// </summary>
internal sealed class TransparentWindowBackdrop : SystemBackdrop
{
    private readonly IntPtr _hWnd;
    private static object? _dispatcherQueueController;
    private static Windows.UI.Composition.Compositor? _sharedCompositor;
    private Windows.UI.Composition.CompositionColorBrush? _transparentBrush;

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(
        [In] DispatcherQueueOptions options,
        [In, Out, MarshalAs(UnmanagedType.IUnknown)] ref object? dispatcherQueueController);

    public TransparentWindowBackdrop(IntPtr hWnd)
    {
        _hWnd = hWnd;
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        try
        {
            if (Windows.System.DispatcherQueue.GetForCurrentThread() == null && _dispatcherQueueController == null)
            {
                var options = new DispatcherQueueOptions
                {
                    dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
                    threadType = 2,
                    apartmentType = 2
                };
                _ = CreateDispatcherQueueController(options, ref _dispatcherQueueController);
            }

            _sharedCompositor ??= new Windows.UI.Composition.Compositor();
            _transparentBrush = _sharedCompositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            connectedTarget.SystemBackdrop = _transparentBrush;
            Win32Interop.EnablePerPixelWindowTransparency(_hWnd);
        }
        catch
        {
            // 安全降级
        }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        try
        {
            disconnectedTarget.SystemBackdrop = null;
            _transparentBrush?.Dispose();
            _transparentBrush = null;
        }
        catch
        {
            // 忽略释放异常
        }
        base.OnTargetDisconnected(disconnectedTarget);
    }
}
