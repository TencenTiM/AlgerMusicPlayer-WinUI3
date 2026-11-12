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

    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowText(IntPtr hWnd, string lpString);

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

    public const int SW_RESTORE = 9;
    public const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    // ==================== Win32 原生现代文件/文件夹对话框（彻底解决未打包/提权/输入帧重入下无响应问题） ====================

    private const int OFN_OVERWRITEPROMPT = 0x00000002;
    private const int OFN_NOCHANGEDIR = 0x00000008;
    private const int OFN_ENABLEHOOK = 0x00000020;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_EXPLORER = 0x00080000;
    private const int OFN_ENABLESIZING = 0x00800000;
    private const uint WM_INITDIALOG = 0x0110;

    private const uint FOS_OVERWRITEPROMPT = 0x00000002;
    private const uint FOS_NOCHANGEDIR = 0x00000008;
    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_PATHMUSTEXIST = 0x00000800;
    private const uint FOS_FILEMUSTEXIST = 0x00001000;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int HR_ERROR_CANCELLED = unchecked((int)0x800704C7);

    private static int _fileDialogBusyFlag;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate IntPtr OfnHookProc(IntPtr hdlg, uint uiMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("comdlg32.dll")]
    private static extern uint CommDlgExtendedError();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAMEW
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COMDLG_FILTERSPEC
    {
        public IntPtr pszName;
        public IntPtr pszSpec;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAMEW lpofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSaveFileNameW(ref OPENFILENAMEW lpofn);

    [ComImport]
    [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class NativeFileOpenDialogCom
    {
    }

    [ComImport]
    [Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B")]
    private class NativeFileSaveDialogCom
    {
    }

    [ComImport]
    [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INativeFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(INativeShellItem psi);
        void SetFolder(INativeShellItem psi);
        void GetFolder(out INativeShellItem ppsi);
        void GetCurrentSelection(out INativeShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out INativeShellItem ppsi);
        void AddPlace(INativeShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INativeShellItem
    {
        void BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
        void GetParent(out INativeShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(INativeShellItem psi, uint hint, out int piOrder);
    }

    /// <summary>
    /// 异步弹出 Windows 原生现代文件打开对话框：
    /// 1) 自动退出 WinUI 3 PointerReleased 同步输入帧；
    /// 2) 优先走现代 COM IFileOpenDialog；
    /// 3) 遇到线程/输入同步限制时自动切到独立干净 STA 线程并兜底 GetOpenFileNameW，非用户取消错误显式抛出以便提示。
    /// </summary>
    public static async Task<string?> PickOpenFileAsync(IntPtr ownerHwnd, string title, string filter)
    {
        if (Interlocked.Exchange(ref _fileDialogBusyFlag, 1) == 1)
        {
            BringExistingProcessDialogToFront();
            return null;
        }

        try
        {
            await Task.Yield();
            await Task.Delay(25);
            ReleaseCapture();

            IntPtr resolvedOwner = ResolveValidOwnerHwnd(ownerHwnd);
            var (handled, path, comHr) = TryShowComDialogCore(
                isSave: false,
                pickFolders: false,
                ownerHwnd: resolvedOwner,
                title: title,
                filter: filter,
                defaultExt: null,
                suggestedFileName: null);
            if (handled)
            {
                return path;
            }

            return await RunOnDedicatedStaThreadAsync(() =>
            {
                if (resolvedOwner != IntPtr.Zero)
                {
                    var ownedCom = TryShowComDialogCore(
                        isSave: false,
                        pickFolders: false,
                        ownerHwnd: resolvedOwner,
                        title: title,
                        filter: filter,
                        defaultExt: null,
                        suggestedFileName: null);
                    if (ownedCom.Handled)
                    {
                        return ownedCom.Path;
                    }

                    var ownedOfn = TryShowLegacyOfnCore(isSave: false, resolvedOwner, title, filter, null, null);
                    if (ownedOfn.Handled)
                    {
                        return ownedOfn.Path;
                    }
                }

                IntPtr tempOwner = CreateTopmostStaOwnerWindow(resolvedOwner);
                try
                {
                    var staCom = TryShowComDialogCore(
                        isSave: false,
                        pickFolders: false,
                        ownerHwnd: tempOwner,
                        title: title,
                        filter: filter,
                        defaultExt: null,
                        suggestedFileName: null);
                    if (staCom.Handled)
                    {
                        return staCom.Path;
                    }

                    var legacy = TryShowLegacyOfnCore(isSave: false, tempOwner, title, filter, null, null);
                    if (legacy.Handled)
                    {
                        return legacy.Path;
                    }

                    throw new InvalidOperationException(
                        $"系统文件对话框未能打开 (COM=0x{(staCom.ErrorHr != 0 ? staCom.ErrorHr : comHr):X8}, CommDlg=0x{legacy.DlgErr:X4})");
                }
                finally
                {
                    if (tempOwner != IntPtr.Zero)
                    {
                        _ = DestroyWindow(tempOwner);
                    }
                }
            });
        }
        finally
        {
            Interlocked.Exchange(ref _fileDialogBusyFlag, 0);
        }
    }

    /// <summary>
    /// 异步弹出 Windows 原生现代文件保存对话框
    /// </summary>
    public static async Task<string?> PickSaveFileAsync(IntPtr ownerHwnd, string title, string filter, string defaultExt, string suggestedFileName)
    {
        if (Interlocked.Exchange(ref _fileDialogBusyFlag, 1) == 1)
        {
            BringExistingProcessDialogToFront();
            return null;
        }

        try
        {
            await Task.Yield();
            await Task.Delay(25);
            ReleaseCapture();

            IntPtr resolvedOwner = ResolveValidOwnerHwnd(ownerHwnd);
            var (handled, path, comHr) = TryShowComDialogCore(
                isSave: true,
                pickFolders: false,
                ownerHwnd: resolvedOwner,
                title: title,
                filter: filter,
                defaultExt: defaultExt,
                suggestedFileName: suggestedFileName);
            if (handled)
            {
                return path;
            }

            return await RunOnDedicatedStaThreadAsync(() =>
            {
                if (resolvedOwner != IntPtr.Zero)
                {
                    var ownedCom = TryShowComDialogCore(
                        isSave: true,
                        pickFolders: false,
                        ownerHwnd: resolvedOwner,
                        title: title,
                        filter: filter,
                        defaultExt: defaultExt,
                        suggestedFileName: suggestedFileName);
                    if (ownedCom.Handled)
                    {
                        return ownedCom.Path;
                    }

                    var ownedOfn = TryShowLegacyOfnCore(isSave: true, resolvedOwner, title, filter, defaultExt, suggestedFileName);
                    if (ownedOfn.Handled)
                    {
                        return ownedOfn.Path;
                    }
                }

                IntPtr tempOwner = CreateTopmostStaOwnerWindow(resolvedOwner);
                try
                {
                    var staCom = TryShowComDialogCore(
                        isSave: true,
                        pickFolders: false,
                        ownerHwnd: tempOwner,
                        title: title,
                        filter: filter,
                        defaultExt: defaultExt,
                        suggestedFileName: suggestedFileName);
                    if (staCom.Handled)
                    {
                        return staCom.Path;
                    }

                    var legacy = TryShowLegacyOfnCore(isSave: true, tempOwner, title, filter, defaultExt, suggestedFileName);
                    if (legacy.Handled)
                    {
                        return legacy.Path;
                    }

                    throw new InvalidOperationException(
                        $"系统保存对话框未能打开 (COM=0x{(staCom.ErrorHr != 0 ? staCom.ErrorHr : comHr):X8}, CommDlg=0x{legacy.DlgErr:X4})");
                }
                finally
                {
                    if (tempOwner != IntPtr.Zero)
                    {
                        _ = DestroyWindow(tempOwner);
                    }
                }
            });
        }
        finally
        {
            Interlocked.Exchange(ref _fileDialogBusyFlag, 0);
        }
    }

    /// <summary>
    /// 异步弹出 Windows 现代文件夹选择对话框（IFileOpenDialog + FOS_PICKFOLDERS）
    /// </summary>
    public static async Task<string?> PickFolderAsync(IntPtr ownerHwnd, string title)
    {
        if (Interlocked.Exchange(ref _fileDialogBusyFlag, 1) == 1)
        {
            BringExistingProcessDialogToFront();
            return null;
        }

        try
        {
            await Task.Yield();
            await Task.Delay(25);
            ReleaseCapture();

            IntPtr resolvedOwner = ResolveValidOwnerHwnd(ownerHwnd);
            var (handled, path, comHr) = TryShowComDialogCore(
                isSave: false,
                pickFolders: true,
                ownerHwnd: resolvedOwner,
                title: title,
                filter: null,
                defaultExt: null,
                suggestedFileName: null);
            if (handled)
            {
                return path;
            }

            return await RunOnDedicatedStaThreadAsync(() =>
            {
                if (resolvedOwner != IntPtr.Zero)
                {
                    var ownedCom = TryShowComDialogCore(
                        isSave: false,
                        pickFolders: true,
                        ownerHwnd: resolvedOwner,
                        title: title,
                        filter: null,
                        defaultExt: null,
                        suggestedFileName: null);
                    if (ownedCom.Handled)
                    {
                        return ownedCom.Path;
                    }
                }

                IntPtr tempOwner = CreateTopmostStaOwnerWindow(resolvedOwner);
                try
                {
                    var staCom = TryShowComDialogCore(
                        isSave: false,
                        pickFolders: true,
                        ownerHwnd: tempOwner,
                        title: title,
                        filter: null,
                        defaultExt: null,
                        suggestedFileName: null);
                    if (staCom.Handled)
                    {
                        return staCom.Path;
                    }

                    throw new InvalidOperationException(
                        $"系统文件夹对话框未能打开 (COM=0x{(staCom.ErrorHr != 0 ? staCom.ErrorHr : comHr):X8})");
                }
                finally
                {
                    if (tempOwner != IntPtr.Zero)
                    {
                        _ = DestroyWindow(tempOwner);
                    }
                }
            });
        }
        finally
        {
            Interlocked.Exchange(ref _fileDialogBusyFlag, 0);
        }
    }

    private static IntPtr ResolveValidOwnerHwnd(IntPtr ownerHwnd)
    {
        if (ownerHwnd != IntPtr.Zero && IsWindow(ownerHwnd))
        {
            return ownerHwnd;
        }

        IntPtr active = GetActiveWindow();
        if (active != IntPtr.Zero && IsWindow(active))
        {
            return active;
        }

        IntPtr fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && IsWindow(fg))
        {
            GetWindowThreadProcessId(fg, out uint pid);
            if (pid == (uint)Environment.ProcessId)
            {
                return fg;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 当已有文件对话框正在运行时用户再次点击按钮，主动将该对话框置顶并切到前台，防止对话框被窗口遮挡后死锁
    /// </summary>
    private static void BringExistingProcessDialogToFront()
    {
        uint selfPid = (uint)Environment.ProcessId;
        var sb = new System.Text.StringBuilder(64);
        _ = EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != selfPid)
            {
                return true;
            }

            sb.Clear();
            if (GetClassNameW(hWnd, sb, sb.Capacity) > 0 && sb.ToString() == "#32770")
            {
                if (IsIconic(hWnd))
                {
                    ShowWindow(hWnd, SW_RESTORE);
                }
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
                return false;
            }
            return true;
        }, IntPtr.Zero);
    }

    private static IntPtr CreateTopmostStaOwnerWindow(IntPtr refHwnd)
    {
        int x = 200;
        int y = 200;
        if (refHwnd != IntPtr.Zero && GetWindowRect(refHwnd, out RECT rc))
        {
            x = rc.Left + Math.Max(40, (rc.Right - rc.Left) / 4);
            y = rc.Top + Math.Max(40, (rc.Bottom - rc.Top) / 4);
        }

        IntPtr hwnd = CreateWindowExW(
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
            "STATIC",
            string.Empty,
            WS_POPUP,
            x, y, 1, 1,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (hwnd != IntPtr.Zero)
        {
            _ = SetWindowPos(hwnd, HWND_TOPMOST, x, y, 1, 1, SWP_SHOWWINDOW);
            _ = SetForegroundWindow(hwnd);
        }
        return hwnd;
    }

    private static (bool Handled, string? Path, int ErrorHr) TryShowComDialogCore(
        bool isSave,
        bool pickFolders,
        IntPtr ownerHwnd,
        string? title,
        string? filter,
        string? defaultExt,
        string? suggestedFileName)
    {
        INativeFileDialog? dialog = null;
        try
        {
            dialog = isSave
                ? (INativeFileDialog)new NativeFileSaveDialogCom()
                : (INativeFileDialog)new NativeFileOpenDialogCom();

            dialog.GetOptions(out uint opts);
            uint addOpts = FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR;
            if (pickFolders)
            {
                addOpts |= FOS_PICKFOLDERS | FOS_PATHMUSTEXIST;
            }
            else if (isSave)
            {
                addOpts |= FOS_PATHMUSTEXIST | FOS_OVERWRITEPROMPT;
            }
            else
            {
                addOpts |= FOS_PATHMUSTEXIST | FOS_FILEMUSTEXIST;
            }
            dialog.SetOptions(opts | addOpts);

            if (!string.IsNullOrWhiteSpace(title))
            {
                dialog.SetTitle(title);
            }

            if (!pickFolders && !string.IsNullOrWhiteSpace(filter))
            {
                ApplyComFilterSpecs(dialog, filter);
            }

            if (isSave)
            {
                if (!string.IsNullOrWhiteSpace(suggestedFileName))
                {
                    dialog.SetFileName(suggestedFileName);
                }
                if (!string.IsNullOrWhiteSpace(defaultExt))
                {
                    dialog.SetDefaultExtension(defaultExt.TrimStart('.'));
                }
            }

            IntPtr validOwner = ownerHwnd != IntPtr.Zero && IsWindow(ownerHwnd) ? ownerHwnd : IntPtr.Zero;
            int hr = dialog.Show(validOwner);

            if (hr == HR_ERROR_CANCELLED)
            {
                return (true, null, 0);
            }

            if (hr != 0)
            {
                return (false, null, hr);
            }

            dialog.GetResult(out INativeShellItem item);
            item.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr pathPtr);
            try
            {
                string? selected = Marshal.PtrToStringUni(pathPtr);
                return (true, string.IsNullOrWhiteSpace(selected) ? null : selected, 0);
            }
            finally
            {
                if (pathPtr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pathPtr);
                }
            }
        }
        catch (COMException comEx)
        {
            if (comEx.HResult == HR_ERROR_CANCELLED)
            {
                return (true, null, 0);
            }
            return (false, null, comEx.HResult);
        }
        catch (Exception ex)
        {
            return (false, null, ex.HResult != 0 ? ex.HResult : -1);
        }
        finally
        {
            if (dialog != null)
            {
                Marshal.ReleaseComObject(dialog);
            }
        }
    }

    private static void ApplyComFilterSpecs(INativeFileDialog dialog, string filter)
    {
        string[] parts = filter.Split('|');
        int count = parts.Length / 2;
        if (count <= 0)
        {
            return;
        }

        int specSize = Marshal.SizeOf<COMDLG_FILTERSPEC>();
        IntPtr arrayPtr = Marshal.AllocCoTaskMem(count * specSize);
        var stringPtrs = new List<IntPtr>(count * 2);
        try
        {
            for (int i = 0; i < count; i++)
            {
                IntPtr namePtr = Marshal.StringToCoTaskMemUni(parts[i * 2]);
                IntPtr patternPtr = Marshal.StringToCoTaskMemUni(parts[i * 2 + 1]);
                stringPtrs.Add(namePtr);
                stringPtrs.Add(patternPtr);

                var spec = new COMDLG_FILTERSPEC
                {
                    pszName = namePtr,
                    pszSpec = patternPtr
                };
                Marshal.StructureToPtr(spec, arrayPtr + (i * specSize), fDeleteOld: false);
            }

            dialog.SetFileTypes((uint)count, arrayPtr);
            dialog.SetFileTypeIndex(1);
        }
        finally
        {
            foreach (IntPtr ptr in stringPtrs)
            {
                if (ptr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(ptr);
                }
            }
            Marshal.FreeCoTaskMem(arrayPtr);
        }
    }

    private static (bool Handled, string? Path, uint DlgErr) TryShowLegacyOfnCore(
        bool isSave,
        IntPtr ownerHwnd,
        string? title,
        string? filter,
        string? defaultExt,
        string? suggestedFileName)
    {
        const int maxChars = 4096;
        string rawFilter = (string.IsNullOrWhiteSpace(filter) ? "所有文件 (*.*)|*.*" : filter).Replace('|', '\0') + "\0\0";
        IntPtr filterPtr = Marshal.StringToHGlobalUni(rawFilter);
        IntPtr fileBufPtr = Marshal.AllocHGlobal(maxChars * 2);
        bool dialogShown = false;

        OfnHookProc hook = (hdlg, msg, wParam, lParam) =>
        {
            if (msg == WM_INITDIALOG)
            {
                dialogShown = true;
                IntPtr parentDlg = GetParent(hdlg);
                IntPtr target = parentDlg != IntPtr.Zero ? parentDlg : hdlg;
                BringWindowToTop(target);
                SetForegroundWindow(target);
            }
            return IntPtr.Zero;
        };

        try
        {
            unsafe
            {
                new Span<byte>((void*)fileBufPtr, maxChars * 2).Clear();
                if (isSave && !string.IsNullOrEmpty(suggestedFileName))
                {
                    ReadOnlySpan<char> src = suggestedFileName.AsSpan();
                    int copyLen = Math.Min(src.Length, maxChars - 2);
                    src[..copyLen].CopyTo(new Span<char>((void*)fileBufPtr, maxChars));
                }
            }

            var ofn = new OPENFILENAMEW
            {
                lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                hwndOwner = ownerHwnd != IntPtr.Zero && IsWindow(ownerHwnd) ? ownerHwnd : IntPtr.Zero,
                lpstrFilter = filterPtr,
                nFilterIndex = 1,
                lpstrFile = fileBufPtr,
                nMaxFile = maxChars,
                lpstrTitle = title,
                lpstrDefExt = isSave && !string.IsNullOrWhiteSpace(defaultExt) ? defaultExt.TrimStart('.') : null,
                lpfnHook = Marshal.GetFunctionPointerForDelegate(hook),
                Flags = isSave
                    ? (OFN_EXPLORER | OFN_ENABLEHOOK | OFN_ENABLESIZING | OFN_PATHMUSTEXIST | OFN_OVERWRITEPROMPT | OFN_NOCHANGEDIR)
                    : (OFN_EXPLORER | OFN_ENABLEHOOK | OFN_ENABLESIZING | OFN_PATHMUSTEXIST | OFN_FILEMUSTEXIST | OFN_NOCHANGEDIR)
            };

            bool ok = isSave ? GetSaveFileNameW(ref ofn) : GetOpenFileNameW(ref ofn);
            GC.KeepAlive(hook);

            if (ok)
            {
                string? selected = Marshal.PtrToStringUni(fileBufPtr);
                return (true, string.IsNullOrWhiteSpace(selected) ? null : selected, 0);
            }

            uint err = CommDlgExtendedError();
            if (dialogShown && err == 0)
            {
                return (true, null, 0);
            }

            return (false, null, err != 0 ? err : 0xFFFFu);
        }
        finally
        {
            GC.KeepAlive(hook);
            Marshal.FreeHGlobal(fileBufPtr);
            Marshal.FreeHGlobal(filterPtr);
        }
    }

    private static Task<T> RunOnDedicatedStaThreadAsync<T>(Func<T> func)
    {
        _ = AllowSetForegroundWindow(ASFW_ANY);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "AlgerNativeFileDialogSTA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
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
