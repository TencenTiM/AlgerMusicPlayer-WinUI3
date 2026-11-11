using System.Collections.Specialized;
using System.ComponentModel;
using AlgerMusicPlayer.WinUI.Helpers;
using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.System;
using AlgerMusicPlayer.WinUI.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using WinRT.Interop;

namespace AlgerMusicPlayer.WinUI.Views;

/// <summary>
/// 悬浮迷你播放器窗口（1:1 还原 src/renderer/layout/MiniLayout.vue + MiniPlayBar.vue + MiniSongItem.vue + window-size.ts）
/// 支持 350x64 紧凑控制栏、底部 3px 进度条点击跳转、滚轮调节音量、330px 展开式迷你播放列表与点击封面直达播放详情页
/// </summary>
public sealed partial class MiniPlayerWindow : Window
{
    private const int MiniWindowWidth = 350;
    private const int MiniCollapsedHeight = 64;
    private const int MiniExpandedHeight = 394;

    private readonly MainViewModel _vm;
    private readonly Action<bool> _restoreCallback;
    private readonly IntPtr _hWnd;
    private readonly AppWindow _appWindow;
    private bool _isPlaylistOpen;
    private double _currentProgressRatio;
    private bool _isDraggingWindow;
    private Win32Interop.POINT _dragStartCursorPos;
    private PointInt32 _dragStartWindowPos;

    public MiniPlayerWindow(MainViewModel vm, Action restoreCallback)
        : this(vm, _ => restoreCallback())
    {
    }

    public MiniPlayerWindow(MainViewModel vm, Action<bool> restoreCallback)
    {
        _vm = vm;
        _restoreCallback = restoreCallback;
        InitializeComponent();

        _hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Resize(new SizeInt32(MiniWindowWidth, MiniCollapsedHeight));

        // 默认定位在主显示器工作区右上角 (对应 src/main/modules/window.ts: workArea.width - width - 20, y: 20)
        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        int posX = workArea.X + workArea.Width - MiniWindowWidth - 20;
        int posY = workArea.Y + 20;
        _appWindow.Move(new PointInt32(posX, posY));

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        // 移除 Windows 11 顶部 7px 白色非客户区边框并启用圆角阴影
        Win32Interop.MakeFramelessOverlayWindow(_hWnd, roundCorners: true, toolWindow: false);

        // 同步主题中心明暗与自定义背景
        ApplyThemeProfile(_vm.Theme.GetEffectiveTheme());

        MiniQueueListView.ItemsSource = _vm.Player.Queue;
        UpdateQueueEmptyState();
        SyncFromViewModel();

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Theme.ThemeApplied += OnThemeApplied;
        _vm.Player.PlayStateChanged += OnPlayStateChanged;
        _vm.Player.SongChanged += OnSongChanged;
        _vm.Player.PositionChanged += OnPositionChanged;
        _vm.Player.Queue.CollectionChanged += OnQueueCollectionChanged;
        ProgressBarTrackGrid.SizeChanged += (_, _) => UpdateProgressFillWidth();

        Closed += OnWindowClosed;
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.Theme.ThemeApplied -= OnThemeApplied;
        _vm.Player.PlayStateChanged -= OnPlayStateChanged;
        _vm.Player.SongChanged -= OnSongChanged;
        _vm.Player.PositionChanged -= OnPositionChanged;
        _vm.Player.Queue.CollectionChanged -= OnQueueCollectionChanged;
    }

    private void OnThemeApplied(CustomThemeProfile profile)
    {
        DispatcherQueue.TryEnqueue(() => ApplyThemeProfile(profile));
    }

    public void ApplyThemeProfile(CustomThemeProfile profile)
    {
        if (!profile.SyncMiniPlayer)
        {
            RootGrid.RequestedTheme = _vm.Config.Settings.AutoTheme
                ? ElementTheme.Default
                : (_vm.Config.Settings.ManualTheme == "Light" ? ElementTheme.Light : ElementTheme.Dark);
            MiniBarContainer.ClearValue(Grid.BackgroundProperty);
            PlaylistPanelBorder.ClearValue(Border.BackgroundProperty);
            return;
        }

        RootGrid.RequestedTheme = profile.BaseMode switch
        {
            "Light" => ElementTheme.Light,
            "System" => ElementTheme.Default,
            _ => ElementTheme.Dark
        };

        if (profile.EnableCustomMiniPlayerBg)
        {
            var baseCol = ThemeService.ParseColorOrDefault(profile.MiniPlayerBgHex, Windows.UI.Color.FromArgb(255, 17, 24, 39));
            var bgBrush = new SolidColorBrush(ThemeService.WithOpacity(baseCol, profile.MiniPlayerOpacity));
            MiniBarContainer.Background = bgBrush;
            PlaylistPanelBorder.Background = bgBrush;
            RootGrid.Background = bgBrush;
        }
        else
        {
            MiniBarContainer.ClearValue(Grid.BackgroundProperty);
            PlaylistPanelBorder.ClearValue(Border.BackgroundProperty);
            RootGrid.ClearValue(Grid.BackgroundProperty);
        }

        if (_isPlaylistOpen)
        {
            TogglePlaylistIcon.Foreground = (Brush)Application.Current.Resources["BrandAccentBrush"];
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(SyncFromViewModel);
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
        DispatcherQueue.TryEnqueue(SyncFromViewModel);
    }

    private void OnPositionChanged(TimeSpan pos, TimeSpan dur)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _currentProgressRatio = dur.TotalSeconds > 0.1
                ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0.0, 1.0)
                : 0.0;
            UpdateProgressFillWidth();
        });
    }

    private void OnQueueCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(UpdateQueueEmptyState);
    }

    private void UpdateQueueEmptyState()
    {
        bool empty = _vm.Player.Queue.Count == 0;
        MiniQueueEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        MiniQueueListView.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SyncFromViewModel()
    {
        var currentSong = _vm.Player.CurrentSong;
        SongTitleText.Text = currentSong?.Name ?? _vm.CurrentSongName;
        ArtistText.Text = currentSong?.ArtistText ?? _vm.CurrentArtistName;
        PlayPauseIcon.Glyph = _vm.Player.IsPlaying ? "\uE769" : "\uE768";

        bool isLiked = currentSong != null && _vm.Config.IsFavorite(currentSong.Id);
        LikeIcon.Glyph = isLiked ? "\uEB52" : "\uEB51";
        LikeIcon.Foreground = isLiked
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 239, 68, 68))
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        var coverUrl = currentSong?.CoverThumbnailUrl ?? _vm.CurrentCoverUrl;
        if (!string.IsNullOrWhiteSpace(coverUrl))
        {
            try
            {
                if (coverUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    CoverImg.Source = new BitmapImage(new Uri(coverUrl));
                }
                else if (File.Exists(coverUrl))
                {
                    CoverImg.Source = new BitmapImage(new Uri(coverUrl));
                }
            }
            catch
            {
                // 忽略封面加载异常
            }
        }

        var dur = _vm.Player.Duration;
        var pos = _vm.Player.Position;
        _currentProgressRatio = dur.TotalSeconds > 0.1
            ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0.0, 1.0)
            : 0.0;
        UpdateProgressFillWidth();
    }

    private void UpdateProgressFillWidth()
    {
        double totalWidth = ProgressBarTrackGrid.ActualWidth;
        if (totalWidth <= 0) totalWidth = MiniWindowWidth;
        ProgressFillBorder.Width = Math.Clamp(totalWidth * _currentProgressRatio, 0, totalWidth);
    }

    // ==================== 拖拽、滚轮音量与进度条点击跳转 ====================

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(MiniBarContainer).Position.Y > 64) return;
        var props = e.GetCurrentPoint(RootGrid).Properties;
        if (!props.IsLeftButtonPressed) return;
        if (!Win32Interop.GetCursorPos(out _dragStartCursorPos)) return;

        _dragStartWindowPos = _appWindow.Position;
        _isDraggingWindow = true;
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
    }

    private void OnMiniBarPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(RootGrid).Properties.MouseWheelDelta;
        if (delta == 0) return;

        float step = delta > 0 ? 0.05f : -0.05f;
        float nextVol = Math.Clamp(_vm.Config.Settings.Volume + step, 0f, 1f);
        _vm.Player.SetVolume(nextVol);
        e.Handled = true;
    }

    private void OnProgressBarPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        double width = ProgressBarTrackGrid.ActualWidth;
        var dur = _vm.Player.Duration;
        if (width <= 0 || dur.TotalSeconds <= 0.1) return;

        var pt = e.GetCurrentPoint(ProgressBarTrackGrid).Position;
        double ratio = Math.Clamp(pt.X / width, 0.0, 1.0);
        _currentProgressRatio = ratio;
        UpdateProgressFillWidth();
        _vm.Player.Seek(TimeSpan.FromSeconds(dur.TotalSeconds * ratio));
    }

    private void OnProgressBarPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ProgressTrackBgBorder.Height = 4;
        ProgressFillBorder.Height = 4;
    }

    private void OnProgressBarPointerExited(object sender, PointerRoutedEventArgs e)
    {
        ProgressTrackBgBorder.Height = 2.5;
        ProgressFillBorder.Height = 2.5;
    }

    // ==================== 顶部按钮与展开播放列表交互 ====================

    private void OnOpenMusicFullFromMiniClick(object sender, RoutedEventArgs e)
    {
        _restoreCallback.Invoke(true);
        Close();
    }

    private async void OnPrevClick(object sender, RoutedEventArgs e) => await _vm.Player.PreviousAsync();

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => _vm.Player.TogglePlayPause();

    private void OnStopClick(object sender, RoutedEventArgs e) => _vm.Player.StopPlayback();

    private async void OnNextClick(object sender, RoutedEventArgs e) => await _vm.Player.NextAsync();

    private async void OnToggleLikeCurrentClick(object sender, RoutedEventArgs e)
    {
        var current = _vm.Player.CurrentSong;
        if (current == null) return;
        var liked = await _vm.Config.ToggleFavoriteAsync(current);
        _vm.IsCurrentSongLiked = liked;
        _vm.RefreshHistoryAndFavorites();
        SyncFromViewModel();
    }

    private void OnTogglePlaylistClick(object sender, RoutedEventArgs e)
    {
        _isPlaylistOpen = !_isPlaylistOpen;
        PlaylistPanelBorder.Visibility = _isPlaylistOpen ? Visibility.Visible : Visibility.Collapsed;
        TogglePlaylistIcon.Foreground = _isPlaylistOpen
            ? (Brush)Application.Current.Resources["BrandAccentBrush"]
            : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

        int targetHeight = _isPlaylistOpen ? MiniExpandedHeight : MiniCollapsedHeight;
        _appWindow.Resize(new SizeInt32(MiniWindowWidth, targetHeight));
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        _restoreCallback.Invoke(false);
        Close();
    }

    // ==================== 迷你播放列表项交互 (MiniSongItem.vue) ====================

    private async void OnMiniQueueRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is SongItem song)
        {
            e.Handled = true;
            await _vm.Player.PlaySongAsync(song);
        }
    }

    private async void OnQueueItemPlayClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SongItem song) return;
        if (_vm.Player.CurrentSong?.Id == song.Id)
        {
            _vm.Player.TogglePlayPause();
        }
        else
        {
            await _vm.Player.PlaySongAsync(song);
        }
    }

    private async void OnQueueItemLikeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SongItem song) return;
        var liked = await _vm.Config.ToggleFavoriteAsync(song);
        if (_vm.Player.CurrentSong?.Id == song.Id)
        {
            _vm.IsCurrentSongLiked = liked;
        }
        _vm.RefreshHistoryAndFavorites();
        SyncFromViewModel();
    }

    private async void OnQueueItemRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SongItem song) return;
        _vm.Player.Queue.Remove(song);
        await _vm.Config.SaveQueueAsync(_vm.Player.Queue);
    }
}
