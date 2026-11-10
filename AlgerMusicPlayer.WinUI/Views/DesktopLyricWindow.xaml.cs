using System.ComponentModel;
using System.Globalization;
using AlgerMusicPlayer.WinUI.Helpers;
using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace AlgerMusicPlayer.WinUI.Views;

/// <summary>
/// 独立置顶桌面歌词窗口（1:1 还原 src/renderer/views/lyric/index.vue + ThemeColorPanel.vue + src/main/lyric.ts）
/// 支持全透明背景、无白边子类化、卡拉OK逐字渐变、滚动/单行/双行模式、主题色面板与锁定穿透自动感应解锁按钮
/// </summary>
public sealed partial class DesktopLyricWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly IntPtr _hWnd;
    private readonly AppWindow _appWindow;
    private readonly DispatcherQueueTimer _lockedCursorPollTimer;
    private DateTime _lockedControlsShowUntil = DateTime.MinValue;
    private bool _isLocked;
    private bool _isPointerHovered;
    private bool _isTemporarilyClickableForUnlock;
    private bool _appliedClickThroughState;
    private bool _isUpdatingCustomHexBox;
    private bool _hasUserDragged;
    private bool _isDraggingWindow;
    private Win32Interop.POINT _dragStartCursorPos;
    private PointInt32 _dragStartWindowPos;

    // 实时卡拉OK逐字进度渐变画刷
    private readonly LinearGradientBrush _karaokeActiveBrush;
    private readonly GradientStop _karaokeStopHighlightStart;
    private readonly GradientStop _karaokeStopHighlightEnd;
    private readonly GradientStop _karaokeStopBaseStart;
    private readonly GradientStop _karaokeStopBaseEnd;

    public DesktopLyricWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();

        _hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        var cfg = _vm.Config.Settings.DesktopLyric;
        int width = Math.Clamp((int)cfg.WindowWidth, 780, 1600);
        int height = Math.Clamp(Math.Max(215, (int)cfg.WindowHeight), 215, 420);
        _appWindow.Resize(new SizeInt32(width, height));

        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        int defaultX = workArea.X + (workArea.Width - width) / 2;
        int defaultY = workArea.Y + workArea.Height - height - 48;

        if (cfg.HasCustomPosition &&
            cfg.WindowX >= workArea.X && cfg.WindowX <= workArea.X + workArea.Width - 200 &&
            cfg.WindowY >= workArea.Y && cfg.WindowY <= workArea.Y + workArea.Height - 100)
        {
            _appWindow.Move(new PointInt32((int)cfg.WindowX, (int)cfg.WindowY));
        }
        else
        {
            _appWindow.Move(new PointInt32(defaultX, defaultY));
        }

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = cfg.IsTop;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        // 移除 Windows 11 顶部 7px 白色非客户区边框并启用 DirectComposition 逐像素全透明背景
        Win32Interop.MakeFramelessOverlayWindow(_hWnd, roundCorners: true, toolWindow: true, perPixelTransparent: true);
        SystemBackdrop = new TransparentWindowBackdrop(_hWnd);

        // 初始化卡拉OK水平渐变画刷
        _karaokeStopHighlightStart = new GradientStop { Offset = 0.0 };
        _karaokeStopHighlightEnd = new GradientStop { Offset = 0.6 };
        _karaokeStopBaseStart = new GradientStop { Offset = 0.601 };
        _karaokeStopBaseEnd = new GradientStop { Offset = 1.0 };
        _karaokeActiveBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops =
            {
                _karaokeStopHighlightStart,
                _karaokeStopHighlightEnd,
                _karaokeStopBaseStart,
                _karaokeStopBaseEnd
            }
        };

        // 鼠标位置轮询计时器 (50ms 间隔，对应原版 src/main/lyric.ts checkMousePosition)
        _lockedCursorPollTimer = DispatcherQueue.CreateTimer();
        _lockedCursorPollTimer.Interval = TimeSpan.FromMilliseconds(50);
        _lockedCursorPollTimer.Tick += OnLockedCursorPollTick;
        _lockedCursorPollTimer.Start();

        ApplyConfigToVisuals();
        SyncSongAndLyricContent();

        if (cfg.IsLocked)
        {
            SetClickThroughLock(true, saveConfig: false);
        }
        else
        {
            UpdateHoverBackgroundVisual();
        }

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.Player.PlayStateChanged += OnPlayStateChanged;
        _vm.Player.SongChanged += OnSongChanged;
        _vm.Player.LyricLoaded += OnLyricLoaded;
        _vm.Player.LyricLineChanged += OnLyricLineChanged;
        _vm.Player.PositionChanged += OnPositionChanged;
        _appWindow.Changed += OnAppWindowChanged;

        Closed += OnWindowClosed;
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _lockedCursorPollTimer.Stop();
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.Player.PlayStateChanged -= OnPlayStateChanged;
        _vm.Player.SongChanged -= OnSongChanged;
        _vm.Player.LyricLoaded -= OnLyricLoaded;
        _vm.Player.LyricLineChanged -= OnLyricLineChanged;
        _vm.Player.PositionChanged -= OnPositionChanged;
        _appWindow.Changed -= OnAppWindowChanged;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange && _hasUserDragged)
        {
            var cfg = _vm.Config.Settings.DesktopLyric;
            cfg.WindowX = sender.Position.X;
            cfg.WindowY = sender.Position.Y;
            cfg.HasCustomPosition = true;
            _ = _vm.Config.SaveSettingsAsync();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CurrentLyricText)
            or nameof(MainViewModel.CurrentLyricTranslation)
            or nameof(MainViewModel.CurrentSongName))
        {
            DispatcherQueue.TryEnqueue(SyncSongAndLyricContent);
        }
    }

    private void OnPlayStateChanged(bool isPlaying)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            PlayPauseIcon.Glyph = isPlaying ? "\uE769" : "\uE768";
        });
    }

    private void OnSongChanged(SongItem? song)
    {
        DispatcherQueue.TryEnqueue(SyncSongAndLyricContent);
    }

    private void OnLyricLoaded(ParsedLyric lyric)
    {
        DispatcherQueue.TryEnqueue(SyncSongAndLyricContent);
    }

    private void OnLyricLineChanged(LyricLine? line)
    {
        DispatcherQueue.TryEnqueue(SyncSongAndLyricContent);
    }

    private void OnPositionChanged(TimeSpan pos, TimeSpan dur)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            double progress = _vm.Player.CurrentLyricLine?.WordProgress ?? 1.0;
            UpdateKaraokeGradientProgress(progress);
        });
    }

    // ==================== 视觉样式与卡拉OK渐变 ====================

    private Windows.UI.Color GetActiveHighlightColor()
    {
        var cfg = _vm.Config.Settings.DesktopLyric;
        var hex = !string.IsNullOrWhiteSpace(cfg.HighlightColorHex)
            ? cfg.HighlightColorHex
            : cfg.PlayedColorHex;

        if (TryParseHexColor(hex, out var parsed))
        {
            return parsed;
        }

        // 默认 Spotify 绿 (#1ed760)
        return Windows.UI.Color.FromArgb(255, 30, 215, 96);
    }

    private Windows.UI.Color GetBaseLyricTextColor()
    {
        var cfg = _vm.Config.Settings.DesktopLyric;
        return cfg.Theme == "light"
            ? Windows.UI.Color.FromArgb(255, 51, 51, 51)
            : Windows.UI.Color.FromArgb(255, 255, 255, 255);
    }

    private void UpdateKaraokeGradientProgress(double progress)
    {
        double clamped = Math.Clamp(progress, 0.0, 1.0);
        var highlightColor = GetActiveHighlightColor();
        var baseColor = GetBaseLyricTextColor();

        _karaokeStopHighlightStart.Color = highlightColor;
        _karaokeStopHighlightEnd.Color = highlightColor;
        _karaokeStopHighlightEnd.Offset = clamped;

        _karaokeStopBaseStart.Color = baseColor;
        _karaokeStopBaseStart.Offset = Math.Min(1.0, clamped + 0.002);
        _karaokeStopBaseEnd.Color = baseColor;
    }

    private void ApplyConfigToVisuals()
    {
        var cfg = _vm.Config.Settings.DesktopLyric;
        var highlightColor = GetActiveHighlightColor();
        var highlightBrush = new SolidColorBrush(highlightColor);
        var baseColor = GetBaseLyricTextColor();
        var baseBrush = new SolidColorBrush(baseColor);
        var secondaryBrush = new SolidColorBrush(cfg.Theme == "light"
            ? Windows.UI.Color.FromArgb(215, 30, 41, 59)
            : Windows.UI.Color.FromArgb(215, 229, 231, 235));
        var iconDefaultBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(215, 255, 255, 255));

        PlayPauseIcon.Glyph = _vm.Player.IsPlaying ? "\uE769" : "\uE768";

        // 1. 顶部工具栏按钮高亮态
        ThemeToggleIcon.Glyph = cfg.Theme == "light" ? "\uE706" : "\uE708";
        ThemeColorPanelBtn.Foreground = ThemeColorPopupBorder.Visibility == Visibility.Visible ? highlightBrush : iconDefaultBrush;
        TranslationToggleBtn.Foreground = cfg.ShowTranslation ? highlightBrush : iconDefaultBrush;
        DisplayModeToggleIcon.Glyph = cfg.DisplayMode switch
        {
            "single" => "\uE738",
            "double" => "\uE8A9",
            _ => "\uE8FD"
        };
        LockIcon.Glyph = _isLocked ? "\uE72E" : "\uE785";

        // 2. 切换三种显示模式面板可见性
        ScrollModePanel.Visibility = cfg.DisplayMode == "scroll" ? Visibility.Visible : Visibility.Collapsed;
        SingleModePanel.Visibility = cfg.DisplayMode == "single" ? Visibility.Visible : Visibility.Collapsed;
        DoubleModePanel.Visibility = cfg.DisplayMode == "double" ? Visibility.Visible : Visibility.Collapsed;

        // 3. 字号缩放（范围 12 ~ 48）
        double mainFontSize = Math.Clamp(cfg.FontSize, 12, 48);
        double subFontSize = Math.Max(12, Math.Round(mainFontSize * 0.68));
        double transFontSize = Math.Max(12, Math.Round(mainFontSize * 0.62));

        ScrollCurrentLineText.FontSize = mainFontSize;
        ScrollCurrentShadowText.FontSize = mainFontSize;
        ScrollPrevLineText.FontSize = subFontSize;
        ScrollPrevShadowText.FontSize = subFontSize;
        ScrollNextLineText.FontSize = subFontSize;
        ScrollNextShadowText.FontSize = subFontSize;
        ScrollCurrentTransText.FontSize = transFontSize;
        ScrollCurrentTransShadowText.FontSize = transFontSize;

        SingleCurrentLineText.FontSize = mainFontSize + 2;
        SingleCurrentShadowText.FontSize = mainFontSize + 2;
        SingleCurrentTransText.FontSize = transFontSize;
        SingleCurrentTransShadowText.FontSize = transFontSize;

        DoubleLine1Text.FontSize = mainFontSize;
        DoubleLine1ShadowText.FontSize = mainFontSize;
        DoubleLine1TransText.FontSize = transFontSize;
        DoubleLine2Text.FontSize = mainFontSize;
        DoubleLine2ShadowText.FontSize = mainFontSize;
        DoubleLine2TransText.FontSize = transFontSize;

        // 4. 绑定颜色与卡拉OK画刷
        UpdateKaraokeGradientProgress(_vm.Player.CurrentLyricLine?.WordProgress ?? 1.0);
        ScrollCurrentLineText.Foreground = _karaokeActiveBrush;
        SingleCurrentLineText.Foreground = _karaokeActiveBrush;

        ScrollPrevLineText.Foreground = secondaryBrush;
        ScrollNextLineText.Foreground = secondaryBrush;
        ScrollCurrentTransText.Foreground = secondaryBrush;
        SingleCurrentTransText.Foreground = secondaryBrush;
        DoubleLine1TransText.Foreground = secondaryBrush;
        DoubleLine2TransText.Foreground = secondaryBrush;

        // 5. 同步主题色面板状态與 60% 卡拉OK预览
        UpdateThemeColorPanelVisuals(highlightColor, baseColor);
        UpdateHoverBackgroundVisual();
    }

    private void UpdateThemeColorPanelVisuals(Windows.UI.Color highlightColor, Windows.UI.Color baseColor)
    {
        var hex = $"#{highlightColor.R:x2}{highlightColor.G:x2}{highlightColor.B:x2}";
        CustomColorSwatchBorder.Background = new SolidColorBrush(highlightColor);

        if (!_isUpdatingCustomHexBox && !string.Equals(CustomColorHexBox.Text, hex, StringComparison.OrdinalIgnoreCase))
        {
            _isUpdatingCustomHexBox = true;
            CustomColorHexBox.Text = hex;
            _isUpdatingCustomHexBox = false;
        }

        // 预设按钮选中对勾状态
        Button[] presetBtns =
        [
            PresetColorBtn0,
            PresetColorBtn1,
            PresetColorBtn2,
            PresetColorBtn3,
            PresetColorBtn4,
            PresetColorBtn5
        ];

        foreach (var btn in presetBtns)
        {
            bool isMatch = string.Equals(btn.Tag as string, hex, StringComparison.OrdinalIgnoreCase);
            btn.BorderBrush = new SolidColorBrush(isMatch
                ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
                : Windows.UI.Color.FromArgb(77, 255, 255, 255));
            btn.Content = isMatch
                ? new FontIcon
                {
                    Glyph = "\uE73E",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255))
                }
                : null;
        }

        // 60% 卡拉OK效果预览
        ThemeColorPreviewText.Foreground = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops =
            {
                new GradientStop { Color = highlightColor, Offset = 0.0 },
                new GradientStop { Color = highlightColor, Offset = 0.6 },
                new GradientStop { Color = baseColor, Offset = 0.602 },
                new GradientStop { Color = baseColor, Offset = 1.0 }
            }
        };
    }

    private void SyncSongAndLyricContent()
    {
        var cfg = _vm.Config.Settings.DesktopLyric;
        var song = _vm.Player.CurrentSong;
        TopSongNameText.Text = song != null ? $"{song.Name} - {song.ArtistText}" : _vm.CurrentSongName;

        var lines = _vm.Player.CurrentLyric?.Lines;
        var currentLine = _vm.Player.CurrentLyricLine;

        if (lines == null || lines.Count == 0)
        {
            string fallbackMain = !string.IsNullOrWhiteSpace(_vm.CurrentLyricText)
                ? _vm.CurrentLyricText
                : (song?.Name ?? "Alger Music Player");
            string fallbackTrans = cfg.ShowTranslation ? (_vm.CurrentLyricTranslation ?? string.Empty) : string.Empty;

            SetScrollLines(string.Empty, fallbackMain, fallbackTrans, string.Empty);
            SetSingleLine(fallbackMain, fallbackTrans);
            SetDoubleLines(fallbackMain, fallbackTrans, "暂无更多歌词", string.Empty, isFirstActive: true);
            UpdateKaraokeGradientProgress(1.0);
            return;
        }

        int currentIndex = currentLine != null ? lines.IndexOf(currentLine) : 0;
        if (currentIndex < 0) currentIndex = 0;

        string prevText = currentIndex > 0 ? lines[currentIndex - 1].Text : string.Empty;
        string currText = lines[currentIndex].Text;
        string currTrans = cfg.ShowTranslation ? (lines[currentIndex].TranslationText ?? string.Empty) : string.Empty;
        string nextText = currentIndex + 1 < lines.Count ? lines[currentIndex + 1].Text : string.Empty;

        SetScrollLines(prevText, currText, currTrans, nextText);
        SetSingleLine(currText, currTrans);

        // 双行模式：按偶数对 (0-1, 2-3, 4-5) 固定分组，当前活动行使用卡拉OK渐变，配对行半透明
        int groupStart = (currentIndex / 2) * 2;
        var line1 = groupStart < lines.Count ? lines[groupStart] : null;
        var line2 = (groupStart + 1) < lines.Count ? lines[groupStart + 1] : null;
        bool isFirstActive = (currentIndex % 2) == 0;

        SetDoubleLines(
            line1?.Text ?? string.Empty,
            cfg.ShowTranslation ? (line1?.TranslationText ?? string.Empty) : string.Empty,
            line2?.Text ?? string.Empty,
            cfg.ShowTranslation ? (line2?.TranslationText ?? string.Empty) : string.Empty,
            isFirstActive);

        UpdateKaraokeGradientProgress(currentLine?.WordProgress ?? 1.0);
    }

    private void SetScrollLines(string prev, string current, string trans, string next)
    {
        ScrollPrevLineText.Text = prev;
        ScrollPrevShadowText.Text = prev;

        ScrollCurrentLineText.Text = current;
        ScrollCurrentShadowText.Text = current;

        ScrollCurrentTransText.Text = trans;
        ScrollCurrentTransShadowText.Text = trans;
        ScrollCurrentTransGroup.Visibility = string.IsNullOrWhiteSpace(trans) ? Visibility.Collapsed : Visibility.Visible;

        ScrollNextLineText.Text = next;
        ScrollNextShadowText.Text = next;
    }

    private void SetSingleLine(string current, string trans)
    {
        SingleCurrentLineText.Text = current;
        SingleCurrentShadowText.Text = current;

        SingleCurrentTransText.Text = trans;
        SingleCurrentTransShadowText.Text = trans;
        SingleCurrentTransGroup.Visibility = string.IsNullOrWhiteSpace(trans) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetDoubleLines(string l1, string t1, string l2, string t2, bool isFirstActive)
    {
        var baseBrush = new SolidColorBrush(GetBaseLyricTextColor());

        DoubleLine1Text.Text = l1;
        DoubleLine1ShadowText.Text = l1;
        DoubleLine1TransText.Text = t1;
        DoubleLine1TransText.Visibility = string.IsNullOrWhiteSpace(t1) ? Visibility.Collapsed : Visibility.Visible;

        DoubleLine2Text.Text = l2;
        DoubleLine2ShadowText.Text = l2;
        DoubleLine2TransText.Text = t2;
        DoubleLine2TransText.Visibility = string.IsNullOrWhiteSpace(t2) ? Visibility.Collapsed : Visibility.Visible;

        DoubleLine1Text.Foreground = isFirstActive ? _karaokeActiveBrush : baseBrush;
        DoubleLine2Text.Foreground = !isFirstActive ? _karaokeActiveBrush : baseBrush;

        DoubleLine1Group.Opacity = isFirstActive ? 1.0 : 0.62;
        DoubleLine2Group.Opacity = !isFirstActive ? 1.0 : 0.62;
    }

    // ==================== 悬停背景与锁定鼠标穿透交互 ====================

    private void UpdateHoverBackgroundVisual()
    {
        bool wantClickThrough = _isLocked && !_isTemporarilyClickableForUnlock;
        if (wantClickThrough != _appliedClickThroughState)
        {
            _appliedClickThroughState = wantClickThrough;
            Win32Interop.SetDesktopLyricClickThrough(_hWnd, wantClickThrough);
        }

        if (_isLocked || !_isPointerHovered)
        {
            HoverBackgroundBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            HoverBackgroundBorder.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ControlBarBorder.Opacity = 0;
            ControlBarBorder.IsHitTestVisible = false;
            return;
        }

        var cfg = _vm.Config.Settings.DesktopLyric;
        // 对应 src/renderer/views/lyric/index.vue:
        // dark: rgba(44, 44, 44, 0.466) -> #772C2C2C
        // light: rgba(0, 0, 0, 0.434) -> #6F000000
        HoverBackgroundBorder.Background = new SolidColorBrush(cfg.Theme == "light"
            ? Windows.UI.Color.FromArgb(111, 0, 0, 0)
            : Windows.UI.Color.FromArgb(119, 44, 44, 44));
        HoverBackgroundBorder.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(38, 255, 255, 255));
        ControlBarBorder.Opacity = 1;
        ControlBarBorder.IsHitTestVisible = true;
    }

    public void SetClickThroughLock(bool locked, bool saveConfig = true)
    {
        _isLocked = locked;
        _isTemporarilyClickableForUnlock = false;

        if (locked)
        {
            ThemeColorPopupBorder.Visibility = Visibility.Collapsed;
            ControlBarBorder.Visibility = Visibility.Collapsed;
            LockedUnlockFloatingBtn.Visibility = Visibility.Visible;
            _lockedControlsShowUntil = DateTime.UtcNow.AddMilliseconds(2500);
        }
        else
        {
            LockedUnlockFloatingBtn.Visibility = Visibility.Collapsed;
            ControlBarBorder.Visibility = Visibility.Visible;
        }

        var cfg = _vm.Config.Settings.DesktopLyric;
        cfg.IsLocked = locked;
        ApplyConfigToVisuals();
        if (saveConfig)
        {
            _ = _vm.Config.SaveSettingsAsync();
        }
    }

    private void OnLockedCursorPollTick(DispatcherQueueTimer sender, object args)
    {
        if (!Win32Interop.GetCursorPos(out var pt) || !Win32Interop.GetWindowRect(_hWnd, out var rect)) return;

        bool insideWindow = pt.X >= rect.Left && pt.X <= rect.Right && pt.Y >= rect.Top && pt.Y <= rect.Bottom;

        if (!_isLocked)
        {
            bool shouldHover = _isDraggingWindow || insideWindow || ThemeColorPopupBorder.Visibility == Visibility.Visible;
            if (shouldHover != _isPointerHovered)
            {
                _isPointerHovered = shouldHover;
                UpdateHoverBackgroundVisual();
            }
            return;
        }

        if (!insideWindow)
        {
            LockedUnlockFloatingBtn.Visibility = Visibility.Collapsed;
            if (_isTemporarilyClickableForUnlock)
            {
                _isTemporarilyClickableForUnlock = false;
                UpdateHoverBackgroundVisual();
            }
            return;
        }

        // 检查鼠标是否位于顶部右侧解锁按钮区域 (Right - 135 ~ Right - 36, Top ~ Top + 54)
        bool overUnlockBtnArea = pt.X >= (rect.Right - 135) && pt.X <= (rect.Right - 36) &&
                                 pt.Y >= rect.Top && pt.Y <= (rect.Top + 54);

        if (overUnlockBtnArea)
        {
            LockedUnlockFloatingBtn.Visibility = Visibility.Visible;
            _lockedControlsShowUntil = DateTime.UtcNow.AddMilliseconds(2500);
            if (!_isTemporarilyClickableForUnlock)
            {
                _isTemporarilyClickableForUnlock = true;
                UpdateHoverBackgroundVisual();
            }
        }
        else
        {
            if (_isTemporarilyClickableForUnlock)
            {
                _isTemporarilyClickableForUnlock = false;
                UpdateHoverBackgroundVisual();
            }

            if (DateTime.UtcNow <= _lockedControlsShowUntil)
            {
                LockedUnlockFloatingBtn.Visibility = Visibility.Visible;
            }
            else
            {
                // 鼠标刚进入窗口时唤醒 2.5s 解锁按钮显示
                if (LockedUnlockFloatingBtn.Visibility == Visibility.Collapsed)
                {
                    LockedUnlockFloatingBtn.Visibility = Visibility.Visible;
                    _lockedControlsShowUntil = DateTime.UtcNow.AddMilliseconds(2500);
                }
                else
                {
                    LockedUnlockFloatingBtn.Visibility = Visibility.Collapsed;
                }
            }
        }
    }

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isLocked) return;
        var props = e.GetCurrentPoint(RootGrid).Properties;
        if (!props.IsLeftButtonPressed) return;
        if (!Win32Interop.GetCursorPos(out _dragStartCursorPos)) return;

        _dragStartWindowPos = _appWindow.Position;
        _isDraggingWindow = true;
        _hasUserDragged = true;
        RootGrid.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingWindow) return;

        var props = e.GetCurrentPoint(RootGrid).Properties;
        if (!props.IsLeftButtonPressed)
        {
            EndWindowDrag(e.Pointer);
            return;
        }

        if (!Win32Interop.GetCursorPos(out var currentCursor)) return;

        int newX = _dragStartWindowPos.X + (currentCursor.X - _dragStartCursorPos.X);
        int newY = _dragStartWindowPos.Y + (currentCursor.Y - _dragStartCursorPos.Y);

        Win32Interop.SetWindowPos(
            _hWnd,
            Win32Interop.HWND_TOPMOST,
            newX,
            newY,
            0,
            0,
            Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE);
        e.Handled = true;
    }

    private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDrag(e.Pointer);
    }

    private void OnRootPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDrag(e.Pointer);
    }

    private void EndWindowDrag(Pointer? pointer)
    {
        if (!_isDraggingWindow) return;
        _isDraggingWindow = false;

        if (pointer != null)
        {
            try
            {
                RootGrid.ReleasePointerCapture(pointer);
            }
            catch
            {
                // 忽略已释放的指针捕获
            }
        }

        var cfg = _vm.Config.Settings.DesktopLyric;
        cfg.WindowX = _appWindow.Position.X;
        cfg.WindowY = _appWindow.Position.Y;
        cfg.HasCustomPosition = true;
        _ = _vm.Config.SaveSettingsAsync();
    }

    private void OnRootPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerHovered = true;
        UpdateHoverBackgroundVisual();
    }

    private void OnRootPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingWindow) return;
        _isPointerHovered = false;
        if (ThemeColorPopupBorder.Visibility != Visibility.Visible)
        {
            UpdateHoverBackgroundVisual();
        }
    }

    // ==================== 顶部控制栏与主题色面板事件 ====================

    private async void OnPrevClick(object sender, RoutedEventArgs e) => await _vm.Player.PreviousAsync();

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => _vm.Player.TogglePlayPause();

    private async void OnNextClick(object sender, RoutedEventArgs e) => await _vm.Player.NextAsync();

    private void OnFontSmallerClick(object sender, RoutedEventArgs e)
    {
        var cfg = _vm.Config.Settings.DesktopLyric;
        cfg.FontSize = Math.Max(12, cfg.FontSize - 2);
        ApplyConfigToVisuals();
        _ = _vm.Config.SaveSettingsAsync();
    }

    private void OnFontLargerClick(object sender, RoutedEventArgs e)
    {
        var cfg = _vm.Config.Settings.DesktopLyric;
        cfg.FontSize = Math.Min(48, cfg.FontSize + 2);
        ApplyConfigToVisuals();
        _ = _vm.Config.SaveSettingsAsync();
    }

    private void OnToggleThemeClick(object sender, RoutedEventArgs e)
    {
        var cfg = _vm.Config.Settings.DesktopLyric;
        cfg.Theme = cfg.Theme == "dark" ? "light" : "dark";
        ApplyConfigToVisuals();
        SyncSongAndLyricContent();
        _ = _vm.Config.SaveSettingsAsync();
    }

    private void OnToggleThemeColorPanelClick(object sender, RoutedEventArgs e)
    {
        ThemeColorPopupBorder.Visibility = ThemeColorPopupBorder.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        ApplyConfigToVisuals();
    }

    private void OnCloseThemeColorPanelClick(object sender, RoutedEventArgs e)
    {
        ThemeColorPopupBorder.Visibility = Visibility.Collapsed;
        ApplyConfigToVisuals();
    }

    private void OnPresetColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string hex) return;
        ApplyHighlightColorHex(hex);
    }

    private void OnCustomColorHexTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingCustomHexBox) return;
        var text = CustomColorHexBox.Text.Trim();
        if (TryParseHexColor(text, out _))
        {
            ApplyHighlightColorHex(text.StartsWith('#') ? text : $"#{text}");
        }
    }

    private void OnResetThemeColorClick(object sender, RoutedEventArgs e)
    {
        ApplyHighlightColorHex("#1ed760");
    }

    private void ApplyHighlightColorHex(string hex)
    {
        var cfg = _vm.Config.Settings.DesktopLyric;
        cfg.HighlightColorHex = hex;
        cfg.PlayedColorHex = hex;
        ApplyConfigToVisuals();
        SyncSongAndLyricContent();
        _ = _vm.Config.SaveSettingsAsync();
    }

    private void OnToggleTranslationClick(object sender, RoutedEventArgs e)
    {
        var cfg = _vm.Config.Settings.DesktopLyric;
        cfg.ShowTranslation = !cfg.ShowTranslation;
        ApplyConfigToVisuals();
        SyncSongAndLyricContent();
        _ = _vm.Config.SaveSettingsAsync();
    }

    private void OnCycleDisplayModeClick(object sender, RoutedEventArgs e)
    {
        var cfg = _vm.Config.Settings.DesktopLyric;
        cfg.DisplayMode = cfg.DisplayMode switch
        {
            "scroll" => "single",
            "single" => "double",
            _ => "scroll"
        };
        ApplyConfigToVisuals();
        SyncSongAndLyricContent();
        _ = _vm.Config.SaveSettingsAsync();
    }

    private void OnToggleLockClick(object sender, RoutedEventArgs e)
    {
        SetClickThroughLock(!_isLocked);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _vm.IsDesktopLyricOpen = false;
        Close();
    }

    private static bool TryParseHexColor(string? hex, out Windows.UI.Color color)
    {
        color = Windows.UI.Color.FromArgb(255, 30, 215, 96);
        if (string.IsNullOrWhiteSpace(hex)) return false;

        var clean = hex.Trim().TrimStart('#');
        if (clean.Length == 6 &&
            byte.TryParse(clean[0..2], NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(clean[2..4], NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(clean[4..6], NumberStyles.HexNumber, null, out var b))
        {
            color = Windows.UI.Color.FromArgb(255, r, g, b);
            return true;
        }
        return false;
    }
}
