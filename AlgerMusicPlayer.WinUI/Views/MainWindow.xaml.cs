using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using AlgerMusicPlayer.WinUI.Helpers;
using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.Audio;
using AlgerMusicPlayer.WinUI.Services.System;
using AlgerMusicPlayer.WinUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace AlgerMusicPlayer.WinUI.Views;

/// <summary>
/// 主窗口交互逻辑（1:1 还原原版 AlgerMusicPlayer 导航、HomeHero、私人FM、新碟上架、播客电台、UID免密登录、听歌排行、10段EQ与沉浸歌词）
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private readonly IntPtr _hWnd;
    private readonly AppWindow _appWindow;
    private DesktopLyricWindow? _desktopLyricWindow;
    private MiniPlayerWindow? _miniPlayerWindow;
    private CancellationTokenSource? _qrPollCts;
    private CancellationTokenSource? _toastDismissCts;
    private CancellationTokenSource? _lyricResumeScrollCts;
    private bool _isInternalSeekUpdate;
    private bool _isInternalVolumeUpdate;
    private bool _isInternalEqUpdate;
    private bool _isLoadingLyricSettingsUi;
    private bool _isPointerOverLyrics;
    private bool _isInitialized;
    private float _volumeBeforeMute = 0.8f;
    private Windows.UI.Color _extractedCoverColor = Windows.UI.Color.FromArgb(255, 30, 41, 59);
    private bool _isMusicFullLightText = true;
    private string _activeDownloadTab = "downloaded";
    private bool _isLoadingDownloadDrawer;
    private bool _downloadDrawerModified;
    private readonly List<string> _downloadFormatComponents = ["songName", "artistName"];
    private string _downloadSeparator = " - ";
    private List<ShortcutItem> _shortcutDraftItems = [];
    private ShortcutItem? _recordingShortcutItem;
    private RectInt32 _sessionWindowBounds;
    private bool _hasSessionWindowBounds;
    private CancellationTokenSource? _windowBoundsSaveCts;
    private Win32Interop.SubclassProc? _traySubclassDelegate;
    private IntPtr _trayIconHandle;
    private bool _isTrayIconAdded;
    private bool _isForceExiting;
    private bool _isCloseConfirmDialogOpen;
    private readonly Stack<string> _sectionHistory = new();
    private string _activeSection = "Home";
    private bool _isNavigatingBack;
    private readonly MultichannelManagerService _multichannelService;
    private CancellationTokenSource? _surroundTourCts;

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        _multichannelService = App.Services.GetRequiredService<MultichannelManagerService>();
        InitializeComponent();

        // 挂载落雪音源 JS 脚本隐藏宿主
        LxHostWebView.Loaded += async (_, _) =>
            await App.Services.GetRequiredService<AlgerMusicPlayer.WinUI.Services.Unblock.LxScriptHostService>().AttachAsync(LxHostWebView);

        _hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        // 读取持久化的窗口大小记忆（进程重新启动时始终在屏幕正中央打开，运行期间隐藏/恢复则记忆位置与大小）
        if (ViewModel.Config.Settings.WindowWidth == 1460)
        {
            ViewModel.Config.Settings.WindowWidth = 1560;
            ViewModel.Config.Settings.WindowHeight = Math.Max(920, ViewModel.Config.Settings.WindowHeight);
        }
        int savedW = ViewModel.Config.Settings.WindowWidth >= 1024 ? ViewModel.Config.Settings.WindowWidth : 1560;
        int savedH = ViewModel.Config.Settings.WindowHeight >= 680 ? ViewModel.Config.Settings.WindowHeight : 920;
        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        if (displayArea != null)
        {
            var workArea = displayArea.WorkArea;
            int targetWidth = Math.Clamp(savedW, 1024, Math.Max(1024, workArea.Width - 40));
            int targetHeight = Math.Clamp(savedH, 680, Math.Max(680, workArea.Height - 40));
            int posX = workArea.X + (workArea.Width - targetWidth) / 2;
            int posY = workArea.Y + (workArea.Height - targetHeight) / 2;
            _sessionWindowBounds = new RectInt32(posX, posY, targetWidth, targetHeight);
            _hasSessionWindowBounds = true;
            _appWindow.MoveAndResize(_sessionWindowBounds);
        }
        else
        {
            _appWindow.Resize(new SizeInt32(savedW, savedH));
        }

        if (ViewModel.Config.Settings.IsWindowMaximized && _appWindow.Presenter is OverlappedPresenter overlapped)
        {
            overlapped.Maximize();
        }

        _appWindow.Changed += OnAppWindowChanged;
        _appWindow.Closing += OnAppWindowClosing;
        InitializeSystemTrayIcon();
        SingleInstanceGuard.ExternalActivationRequested += OnExternalActivationRequested;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);

        ViewModel.AttachDispatcher(DispatcherQueue);
        ViewModel.Player.InitializeSmtc(_hWnd);
        ViewModel.Hotkeys.Initialize(_hWnd);
        ViewModel.Hotkeys.HotkeyTriggered += OnGlobalHotkeyTriggered;
        RootLayout.KeyDown += OnRootLayoutKeyDown;

        // 为音量条与均衡器滑块挂载滚轮事件（handledEventsToo=true，确保悬停在 Slider 内部轨道上也能直接滚轮调节）
        VolumeSlider.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnBottomPlayBarVolumeWheel), true);
        SimplePlayBarVolumeSlider.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnSimplePlayBarVolumeWheel), true);
        foreach (var eqSlider in new[] { EqBand0, EqBand1, EqBand2, EqBand3, EqBand4, EqBand5, EqBand6, EqBand7, EqBand8, EqBand9 })
        {
            eqSlider.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnEqBandPointerWheelChanged), true);
        }

        BindCollections();
        LoadInitialSettingsToUi();
        ApplyCollectionTransitions();
        ConfigureTitleBarButtonsTheme();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateTaskbarTitle();
        _ = ViewModel.InitializeAsync();
        _isInitialized = true;
        PlayEntranceAnimation(HomeView, 0, 22, 340);
    }

    private void ConfigureTitleBarButtonsTheme()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var titleBar = _appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        bool isOverlayOpen = ImmersiveLyricOverlay != null && ImmersiveLyricOverlay.Visibility == Visibility.Visible;

        bool isTargetLight = IsCurrentUiLightMode;
        var activeProfile = ThemeService.ResolveModeAdaptedProfile(ViewModel.Theme.GetEffectiveTheme(), isTargetLight);
        if (!isOverlayOpen && activeProfile.EnableCustomTitleBar && ThemeService.TryParseColor(activeProfile.TitleBarTextHex, out var customBtnFg))
        {
            titleBar.ButtonForegroundColor = customBtnFg;
            titleBar.ButtonHoverForegroundColor = customBtnFg;
            titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(28, customBtnFg.R, customBtnFg.G, customBtnFg.B);
            return;
        }

        bool isLight = isOverlayOpen ? !_isMusicFullLightText : isTargetLight;
        var sysBtnFg = isLight ? Windows.UI.Color.FromArgb(255, 15, 23, 42) : Microsoft.UI.Colors.White;
        titleBar.ButtonForegroundColor = sysBtnFg;
        titleBar.ButtonHoverForegroundColor = sysBtnFg;
        titleBar.ButtonHoverBackgroundColor = isLight
            ? Windows.UI.Color.FromArgb(20, 0, 0, 0)
            : Windows.UI.Color.FromArgb(24, 255, 255, 255);
    }

    private void BindCollections()
    {
        // 首页集合
        DailyPreviewItemsControl.ItemsSource = ViewModel.DailyPreviewSongs;
        HomePlaylistsGrid.ItemsSource = ViewModel.RecommendPlaylists;
        HotArtistsGrid.ItemsSource = ViewModel.HotArtists;
        HomeNewAlbumsGrid.ItemsSource = ViewModel.HomeNewAlbums;
        HomeNewSongsGrid.ItemsSource = ViewModel.HomeNewSongs;

        // 独立页面集合
        NewAlbumsPageGrid.ItemsSource = ViewModel.NewAlbums;
        PodcastPreferredGrid.ItemsSource = ViewModel.PodcastTodayPreferred;
        PodcastRadiosGrid.ItemsSource = ViewModel.RecommendedRadios;
        ToplistGrid.ItemsSource = ViewModel.Toplists;
        SquarePlaylistsGrid.ItemsSource = ViewModel.SquarePlaylists;
        DetailSongsListView.ItemsSource = ViewModel.FilteredDetailSongs;
        DetailCompactSongsListView.ItemsSource = ViewModel.FilteredDetailSongs;
        AddToPlaylistTargetListView.ItemsSource = ViewModel.UserCreatedPlaylists;

        // 搜索页集合
        SearchSongsListView.ItemsSource = ViewModel.SearchSongs;
        SearchPlaylistsGrid.ItemsSource = ViewModel.SearchPlaylists;
        SearchAlbumsGrid.ItemsSource = ViewModel.SearchAlbums;
        SearchArtistsGrid.ItemsSource = ViewModel.SearchArtists;
        SearchMvsGrid.ItemsSource = ViewModel.SearchMvs;
        SearchBiliListView.ItemsSource = ViewModel.BilibiliVideos;
        BiliDedicatedListView.ItemsSource = ViewModel.BilibiliVideos;

        // MV、历史收藏、本地音乐（双视图与文件夹树）、下载、QQ音乐独立歌单
        MvGridView.ItemsSource = ViewModel.MvList;
        FavoriteSongsListView.ItemsSource = ViewModel.FavoriteSongs;
        HistorySongsListView.ItemsSource = ViewModel.HistorySongs;
        LocalSongsListView.ItemsSource = ViewModel.FilteredLocalSongs;
        FolderTreeListView.ItemsSource = ViewModel.VisibleFolderNodes;
        CurrentFolderSongsListView.ItemsSource = ViewModel.CurrentFolderSongs;
        ConfiguredLocalFoldersListView.ItemsSource = ViewModel.ConfiguredLocalFolders;
        QqCreatedPlaylistsGrid.ItemsSource = ViewModel.QqCreatedPlaylists;
        QqCollectedPlaylistsGrid.ItemsSource = ViewModel.QqCollectedPlaylists;
        DownloadTasksListView.ItemsSource = ViewModel.Downloader.Tasks;
        CompletedDownloadsListView.ItemsSource = ViewModel.Downloader.CompletedTasks;
        ViewModel.Downloader.StateChanged += () => DispatcherQueue.TryEnqueue(UpdateDownloadViewState);

        // 个人中心集合（创建的歌单、收藏的歌单、收藏的专辑、听歌排行）
        UserCreatedPlaylistsGrid.ItemsSource = ViewModel.UserCreatedPlaylists;
        UserFavoritePlaylistsGrid.ItemsSource = ViewModel.UserFavoritePlaylists;
        UserSubscribedAlbumsGrid.ItemsSource = ViewModel.UserSubscribedAlbums;
        UserPlayRecordListView.ItemsSource = ViewModel.UserPlayRecords;

        // 播放队列与全屏歌词
        QueueListView.ItemsSource = ViewModel.Player.Queue;
        ImmersiveLyricListView.ItemsSource = ViewModel.LyricLines;
        ViewModel.LyricLines.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(SyncLyricLinesTypographyAndEmptyState);

        // 系统设置落雪音源脚本集合
        LxScriptsListView.ItemsSource = ViewModel.LxScriptsList;
    }

    private void LoadInitialSettingsToUi()
    {
        var s = ViewModel.Config.Settings;
        s.Shortcuts = AppSettings.NormalizeShortcuts(s.Shortcuts);

        RootLayout.RequestedTheme = s.AutoTheme
            ? ElementTheme.Default
            : (s.ManualTheme == "Light" ? ElementTheme.Light : ElementTheme.Dark);
        AutoThemeToggle.IsOn = s.AutoTheme;
        InitializeThemeCenterEngine();
        SyncThemeSettingsUi();

        _isInternalVolumeUpdate = true;
        VolumeSlider.Value = Math.Round(s.Volume * 100);
        SimplePlayBarVolumeSlider.Value = Math.Round(s.Volume * 100);
        _isInternalVolumeUpdate = false;
        UpdateVolumeMuteGlyph(s.Volume);
        SyncPlayModeIcons(s.PlayMode);
        LoadMusicFullSettingsToUi();
        UnblockToggle.IsOn = s.EnableMusicUnblock;
        SyncUnblockSettingsUi();
        AutoPlayToggle.IsOn = s.AutoPlay;
        AudioFadeToggle.IsOn = s.EnableAudioFade;
        AudioFadeDurationSlider.Value = Math.Clamp(s.AudioFadeDurationSeconds, 0.5, 5.0);
        AudioFadeDurationLabel.Text = $"{s.AudioFadeDurationSeconds:F1}s";
        AudioFadeDurationPanel.Opacity = s.EnableAudioFade ? 1.0 : 0.4;
        AudioFadeDurationSlider.IsEnabled = s.EnableAudioFade;
        RefreshMultichannelSummaryLabel();
        EmbedTagToggle.IsOn = s.EmbedCoverAndLyricOnDownload;
        AlwaysShowDownloadToggle.IsOn = s.AlwaysShowDownloadButton;
        AlwaysShowDownloadStateLabel.Text = s.AlwaysShowDownloadButton ? "显示" : "隐藏";
        UnlimitedDownloadToggle.IsOn = s.UnlimitedDownload;
        UnlimitedDownloadStateLabel.Text = s.UnlimitedDownload ? "开启" : "关闭";
        DownloadPathBox.Text = s.DownloadPath;
        DownloadPathDisplayText.Text = string.IsNullOrWhiteSpace(s.DownloadPath) ? "选择音乐文件的下载位置" : s.DownloadPath;
        MusicApiPortBox.Text = s.MusicApiPort.ToString();
        UseCustomApiToggle.IsOn = s.UseCustomApiServer;
        CustomApiUrlBox.Text = s.CustomApiBaseUrl;
        CustomUnblockUrlBox.Text = s.CustomUnblockApiUrl;
        ProxyEnableToggle.IsOn = s.Proxy.Enable;
        ProxyHostBox.Text = s.Proxy.Host;
        ProxyPortBox.Text = s.Proxy.Port > 0 ? s.Proxy.Port.ToString() : string.Empty;
        EnableRealIpToggle.IsOn = s.EnableRealIP;
        RealIpBox.Text = s.RealIP;
        RealIpBox.Visibility = s.EnableRealIP ? Visibility.Visible : Visibility.Collapsed;
        CookieInputBox.Text = s.NeteaseCookie;
        if (s.SavedUid > 0)
        {
            UidLoginInputBox.Text = s.SavedUid.ToString();
        }
        SyncCookieSettingsRowUi();

        NoAnimateToggle.IsOn = !s.NoAnimate;
        NoAnimateStateLabel.Text = s.NoAnimate ? "关闭" : "开启";
        AnimationSpeedSlider.IsEnabled = !s.NoAnimate;
        AnimationSpeedSlider.Value = Math.Clamp(s.AnimationSpeed, 0.1, 3.0);
        AnimationSpeedValueText.Text = $"{Math.Round(s.AnimationSpeed, 1):0.#}x";
        GpuAccelerationToggle.IsOn = s.EnableGpuAcceleration;

        // 本地音乐视图模式与登录面板初始化
        SwitchLocalMusicViewModeUi(string.IsNullOrWhiteSpace(s.LocalMusicViewMode) ? "folder" : s.LocalMusicViewMode);
        SwitchLoginProviderUi("netease");

        // 落雪音源脚本与快捷键列表同步
        ViewModel.LxScriptsList.Clear();
        foreach (var lx in s.LxMusicScripts) ViewModel.LxScriptsList.Add(lx);
        ViewModel.ShortcutsList.Clear();
        foreach (var sc in s.Shortcuts) ViewModel.ShortcutsList.Add(sc);

        // 音源卡片状态（包含 migu / kugou / kuwo / pyncmd / gdmusic / lxMusic / custom）
        SyncMusicSourceCardsUi();

        // 音频输出设备加载
        RefreshAudioDevicesComboBox(s.AudioOutputDeviceNumber);

        // 远程控制与均衡器
        RemoteControlToggle.IsOn = s.EnableRemoteControl;
        RemoteControlPortBox.Text = s.RemoteControlPort.ToString();
        RemoteAllowedIpsBox.Text = string.Join(", ", s.RemoteControlAllowedIps);
        EqEnableToggle.IsOn = s.Equalizer.IsEnabled;
        SyncEqSlidersFromConfig();

        RemoteControlUrlText.Text = ViewModel.RemoteControl.IsRunning
            ? ViewModel.RemoteControl.AccessUrl
            : $"http://localhost:{s.RemoteControlPort}/";

        // 磁盘缓存配置
        EnableDiskCacheToggle.IsOn = s.EnableDiskCache;
        DiskCacheMaxSizeBox.Text = s.DiskCacheMaxSizeMB.ToString();
        var cacheDir = string.IsNullOrWhiteSpace(s.DiskCacheDir) ? ViewModel.Config.CacheDirectory : s.DiskCacheDir;
        DiskCacheDirBox.Text = cacheDir;
        DiskCacheDirDisplayText.Text = cacheDir;

        SelectComboItemByTag(QualityComboBox, s.MusicQuality);
        var initialQualityBadge = MusicQualityLevel.GetShortBadgeName(s.MusicQuality);
        BottomQualityBadgeText.Text = initialQualityBadge;
        ImmersiveQualityBadgeText.Text = initialQualityBadge;
        SelectComboItemByTag(LanguageComboBox, s.Language);
        SelectComboItemByTag(TranslationEngineComboBox, s.LyricTranslationEngine);
        SelectComboItemByTag(FontScopeComboBox, s.FontScope);
        SelectComboItemByTag(SystemFontComboBox, s.FontFamily);
        SelectComboItemByTag(CloseActionComboBox, s.CloseAction);
        SelectComboItemByTag(ProxyProtocolComboBox, s.Proxy.Protocol);
        SelectComboItemByTag(DiskCachePolicyComboBox, s.DiskCacheCleanupPolicy);
    }

    private bool _isSyncingThemeToggleUi;

    private void SyncThemeSettingsUi()
    {
        var s = ViewModel.Config.Settings;
        _isSyncingThemeToggleUi = true;
        AutoThemeToggle.IsOn = s.AutoTheme;
        _isSyncingThemeToggleUi = false;
        AutoThemeModeLabel.Text = s.AutoTheme ? "跟随系统" : "手动切换";
        ManualThemeToggleBtn.IsEnabled = !s.AutoTheme;
        ManualThemeToggleBtn.Opacity = s.AutoTheme ? 0.5 : 1.0;
        bool isDark = !IsCurrentUiLightMode;
        ManualThemeToggleIcon.Glyph = isDark ? "\uE708" : "\uE706";
        ManualThemeToggleText.Text = isDark ? "夜间" : "日间";
        SwitchDownloadTabUi(_activeDownloadTab);

        ApplyDynamicAccentButtonState(SettingsTabBasicBtn, SettingsBasicPanel.Visibility == Visibility.Visible);
        ApplyDynamicAccentButtonState(SettingsTabThemeBtn, SettingsThemePanel.Visibility == Visibility.Visible);
        ApplyDynamicAccentButtonState(SettingsTabPlaybackBtn, SettingsPlaybackPanel.Visibility == Visibility.Visible);
        ApplyDynamicAccentButtonState(SettingsTabApplicationBtn, SettingsApplicationPanel.Visibility == Visibility.Visible);
        ApplyDynamicAccentButtonState(SettingsTabNetworkBtn, SettingsNetworkPanel.Visibility == Visibility.Visible);
        ApplyDynamicAccentButtonState(SettingsTabSystemBtn, SettingsSystemPanel.Visibility == Visibility.Visible);
        ApplyDynamicAccentButtonState(SettingsTabAboutBtn, SettingsAboutPanel.Visibility == Visibility.Visible);
        ApplyDynamicAccentButtonState(PageSubModeLocalBtn, PageLocalLibraryCard.Visibility == Visibility.Visible);
        ApplyDynamicAccentButtonState(PageSubModeMarketBtn, PageMarketCard.Visibility == Visibility.Visible);
    }

    private void SyncCookieSettingsRowUi()
    {
        var cookie = ViewModel.Config.Settings.NeteaseCookie;
        bool hasCookie = !string.IsNullOrWhiteSpace(cookie);
        BasicTokenStatusText.Text = $"当前Cookie状态: {(hasCookie ? "已设置" : "未设置")}";
        if (hasCookie)
        {
            BasicTokenPreviewText.Text = cookie.Length > 50 ? $"{cookie[..50]}..." : cookie;
            BasicTokenPreviewText.Visibility = Visibility.Visible;
            OpenCookieModalBtn.Content = "修改Cookie";
            ClearCookieBtn.Visibility = Visibility.Visible;
        }
        else
        {
            BasicTokenPreviewText.Text = string.Empty;
            BasicTokenPreviewText.Visibility = Visibility.Collapsed;
            OpenCookieModalBtn.Content = "设置Cookie";
            ClearCookieBtn.Visibility = Visibility.Collapsed;
        }
    }

    private void SyncUnblockSettingsUi()
    {
        bool enabled = UnblockToggle.IsOn;
        UnblockStateLabel.Text = enabled ? "开启" : "关闭";
        SelectedSourcesSummaryText.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ConfigureMusicSourcesBtn.IsEnabled = enabled;
    }

    private static void SelectComboItemByTag(ComboBox comboBox, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        foreach (var obj in comboBox.Items)
        {
            if (obj is ComboBoxItem item && string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                break;
            }
        }
    }

    private void RefreshAudioDevicesComboBox(int selectedDeviceNum)
    {
        AudioDeviceComboBox.Items.Clear();
        foreach (var (deviceNumber, name) in ViewModel.Player.GetAudioOutputDevices())
        {
            var item = new ComboBoxItem
            {
                Content = name,
                Tag = deviceNumber
            };
            AudioDeviceComboBox.Items.Add(item);
            if (deviceNumber == selectedDeviceNum)
            {
                AudioDeviceComboBox.SelectedItem = item;
            }
        }
        if (AudioDeviceComboBox.SelectedItem == null && AudioDeviceComboBox.Items.Count > 0)
        {
            AudioDeviceComboBox.SelectedIndex = 0;
        }
    }

    private void SyncEqSlidersFromConfig()
    {
        _isInternalEqUpdate = true;
        var eq = ViewModel.Config.Settings.Equalizer;
        var gains = eq.BandGains;
        Slider[] sliders = [EqBand0, EqBand1, EqBand2, EqBand3, EqBand4, EqBand5, EqBand6, EqBand7, EqBand8, EqBand9];
        TextBlock[] labels = [EqBandVal0, EqBandVal1, EqBandVal2, EqBandVal3, EqBandVal4, EqBandVal5, EqBandVal6, EqBandVal7, EqBandVal8, EqBandVal9];
        for (int i = 0; i < sliders.Length && i < gains.Length; i++)
        {
            sliders[i].Value = gains[i];
            sliders[i].IsEnabled = eq.IsEnabled;
            labels[i].Text = $"{gains[i]:0.#}dB";
        }
        UpdateEqPresetTagButtonsUi(eq.ActivePresetName);
        _isInternalEqUpdate = false;
    }

    private void UpdateEqPresetTagButtonsUi(string? activePreset)
    {
        Button[] presetBtns =
        [
            EqPresetFlatBtn, EqPresetPopBtn, EqPresetRockBtn,
            EqPresetClassicalBtn, EqPresetJazzBtn, EqPresetHiphopBtn,
            EqPresetVocalBtn, EqPresetDanceBtn, EqPresetAcousticBtn
        ];
        foreach (var btn in presetBtns)
        {
            bool isSelected = !string.IsNullOrWhiteSpace(activePreset) &&
                              string.Equals(btn.Tag?.ToString(), activePreset, StringComparison.OrdinalIgnoreCase);
            ApplyDynamicAccentButtonState(btn, isSelected);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.CurrentSection):
                    SwitchVisibleSection(ViewModel.CurrentSection);
                    break;
                case nameof(MainViewModel.IsLoading):
                    GlobalLoadingBar.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(MainViewModel.StatusToast):
                    ShowAutoDismissToast(ViewModel.StatusToast);
                    break;
                case nameof(MainViewModel.CurrentSongName):
                    BottomSongTitleText.Text = ViewModel.CurrentSongName;
                    ImmersiveSongTitle.Text = ViewModel.CurrentSongName;
                    ImmersiveHiddenCoverTitle.Text = ViewModel.CurrentSongName;
                    LyricOffsetBadgeText.Text = $"{ViewModel.Player.LyricOffsetMs / 1000.0:+0.0;-0.0;0.0}s";
                    UpdateSystemTrayTooltip();
                    UpdateTaskbarTitle();
                    break;
                case nameof(MainViewModel.CurrentArtistName):
                    BottomArtistText.Text = ViewModel.CurrentArtistName;
                    ImmersiveSongArtist.Text = ViewModel.CurrentArtistName;
                    ImmersiveHiddenCoverArtist.Text = ViewModel.CurrentArtistName;
                    UpdateSystemTrayTooltip();
                    UpdateTaskbarTitle();
                    break;
                case nameof(MainViewModel.CurrentCoverUrl):
                    UpdateCoverImages(ViewModel.CurrentCoverUrl);
                    break;
                case nameof(MainViewModel.CurrentPlatformBadge):
                    BottomPlatformBadgeText.Text = ViewModel.CurrentPlatformBadge;
                    break;
                case nameof(MainViewModel.CurrentQualityBadge):
                    BottomQualityBadgeText.Text = ViewModel.CurrentQualityBadge;
                    ImmersiveQualityBadgeText.Text = ViewModel.CurrentQualityBadge;
                    break;
                case nameof(MainViewModel.IsPlaying):
                    var playGlyph = ViewModel.IsPlaying ? "\uE769" : "\uE768";
                    PlayPauseMainIcon.Glyph = playGlyph;
                    SimplePlayBarPlayIcon.Glyph = playGlyph;
                    UpdateDynamicEnginePowerState();
                    break;
                case nameof(MainViewModel.IsCurrentSongLiked):
                    BottomLikeIcon.Glyph = ViewModel.IsCurrentSongLiked ? "\uEB52" : "\uEB51";
                    if (ViewModel.IsCurrentSongLiked)
                    {
                        BottomLikeIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 244, 63, 94));
                    }
                    else
                    {
                        BottomLikeIcon.ClearValue(IconElement.ForegroundProperty);
                    }
                    break;
                case nameof(MainViewModel.PositionSeconds):
                case nameof(MainViewModel.DurationSeconds):
                    _isInternalSeekUpdate = true;
                    double maxSec = Math.Max(1, ViewModel.DurationSeconds);
                    double posSec = Math.Clamp(ViewModel.PositionSeconds, 0, maxSec);
                    SeekSlider.Maximum = maxSec;
                    SeekSlider.Value = posSec;
                    SimplePlayBarSeekSlider.Maximum = maxSec;
                    SimplePlayBarSeekSlider.Value = posSec;
                    PositionTimeText.Text = ViewModel.PositionText;
                    DurationTimeText.Text = ViewModel.DurationText;
                    SimplePlayBarPositionText.Text = ViewModel.PositionText;
                    SimplePlayBarDurationText.Text = ViewModel.DurationText;
                    _isInternalSeekUpdate = false;
                    break;
                case nameof(MainViewModel.CurrentLyricText):
                    BottomInlineLyricText.Text = ViewModel.CurrentLyricText;
                    ScrollToActiveLyricLine();
                    break;
                case nameof(MainViewModel.DailyCoverUrl):
                case nameof(MainViewModel.DailySongCountText):
                    DailyHeroCountBadge.Text = ViewModel.DailySongCountText;
                    SetImageUrlSafe(DailyHeroCoverImg, ViewModel.DailyCoverUrl);
                    break;
                case nameof(MainViewModel.FmSongName):
                case nameof(MainViewModel.FmArtistName):
                case nameof(MainViewModel.FmCoverUrl):
                    FmHeroSongTitle.Text = ViewModel.FmSongName;
                    FmHeroArtistName.Text = ViewModel.FmArtistName;
                    SetImageUrlSafe(FmHeroCoverImg, ViewModel.FmCoverUrl);
                    break;
                case nameof(MainViewModel.CacheStatsText):
                case nameof(MainViewModel.CacheUsagePercent):
                case nameof(MainViewModel.CacheFilesDetailText):
                    SettingsCacheStatsText.Text = ViewModel.CacheStatsText;
                    CacheUsageProgressBar.Value = ViewModel.CacheUsagePercent;
                    SettingsCacheFilesText.Text = ViewModel.CacheFilesDetailText;
                    break;
                case nameof(MainViewModel.DetailTitle):
                case nameof(MainViewModel.DetailSubtitle):
                case nameof(MainViewModel.DetailDescription):
                case nameof(MainViewModel.DetailCoverUrl):
                case nameof(MainViewModel.DetailBadgeText):
                case nameof(MainViewModel.DetailCreatorName):
                case nameof(MainViewModel.DetailCreatorAvatarUrl):
                case nameof(MainViewModel.DetailSongCountText):
                case nameof(MainViewModel.CanCollectDetail):
                case nameof(MainViewModel.IsDetailCollected):
                    SyncDetailHeaderUi();
                    break;
                case nameof(MainViewModel.CurrentMvTitle):
                case nameof(MainViewModel.CurrentMvArtist):
                case nameof(MainViewModel.CurrentMvResolution):
                case nameof(MainViewModel.IsMvSingleLoop):
                    SyncMvOverlayHeaderUi();
                    break;
                case nameof(MainViewModel.SelectedMvArea):
                    SyncMvAreaButtonsUi();
                    break;
                case nameof(MainViewModel.MvHasMore):
                case nameof(MainViewModel.IsLoadingMoreMv):
                    SyncMvLoadMoreButtonUi();
                    break;
                case nameof(MainViewModel.IsMvOverlayOpen):
                    if (ViewModel.IsMvOverlayOpen)
                    {
                        MvPlayerOverlay.Visibility = Visibility.Visible;
                        SyncMvOverlayHeaderUi();
                        PlayEntranceAnimation(MvPlayerOverlay, 0, 20, 260);
                    }
                    else
                    {
                        try
                        {
                            MvMediaPlayer.MediaPlayer?.Pause();
                            MvMediaPlayer.Source = null;
                        }
                        catch
                        {
                            // 忽略关闭时的媒体释放异常
                        }
                        MvPlayerOverlay.Visibility = Visibility.Collapsed;
                    }
                    break;
                case nameof(MainViewModel.CurrentMvStreamUrl):
                    if (!string.IsNullOrWhiteSpace(ViewModel.CurrentMvStreamUrl))
                    {
                        SyncMvOverlayHeaderUi();
                        MvMediaPlayer.Source = MediaSource.CreateFromUri(new Uri(ViewModel.CurrentMvStreamUrl));
                        EnsureMvMediaEndedHooked();
                    }
                    else
                    {
                        MvMediaPlayer.Source = null;
                    }
                    break;
                case nameof(MainViewModel.CurrentUser):
                case nameof(MainViewModel.LoginStatusText):
                    BasicTokenStatusText.Text = ViewModel.LoginStatusText;
                    SyncLoginPanelsVisibilityUi();
                    break;
                case nameof(MainViewModel.LocalMusicCountBadgeText):
                    LocalSongsCountText.Text = ViewModel.LocalMusicCountBadgeText;
                    break;
                case nameof(MainViewModel.SelectedFolderNode):
                    var folder = ViewModel.SelectedFolderNode;
                    SelectedFolderTitleText.Text = folder != null
                        ? $"{folder.Name}（直属 {folder.Songs.Count} 首）"
                        : "请从左侧选择文件夹";
                    SelectedFolderPathText.Text = folder?.Path ?? string.Empty;
                    if (folder != null && FolderTreeListView.SelectedItem != folder)
                    {
                        FolderTreeListView.SelectedItem = folder;
                    }
                    PlayEntranceAnimation(CurrentFolderSongsListView, 0, 14, 240);
                    break;
                case nameof(MainViewModel.CurrentQqUser):
                case nameof(MainViewModel.QqLoginStatusText):
                    QqPageStatusText.Text = ViewModel.QqLoginStatusText;
                    QqPageNicknameText.Text = ViewModel.CurrentQqUser?.Nickname ?? "QQ 音乐歌单中心";
                    SetImageUrlSafe(QqPageAvatarImg, ViewModel.CurrentQqUser?.DisplayAvatarUrl);
                    SyncLoginPanelsVisibilityUi();
                    break;
            }
        });
    }

    private static void SetImageUrlSafe(Image target, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            target.Source = null;
            return;
        }
        try
        {
            if (url.StartsWith("http") || File.Exists(url))
            {
                target.Source = new BitmapImage(new Uri(url));
            }
            else
            {
                target.Source = null;
            }
        }
        catch
        {
            target.Source = null;
        }
    }

    private void UpdateCoverImages(string url)
    {
        SetImageUrlSafe(BottomCoverImg, url);
        SetImageUrlSafe(ImmersiveCoverImg, url);
        _ = ExtractAndApplyCoverThemeAsync(url);
    }

    private void ScrollToActiveLyricLine(bool force = false)
    {
        if (!force && _isPointerOverLyrics) return;
        var active = ViewModel.Player.CurrentLyricLine;
        if (active == null || ImmersiveLyricOverlay.Visibility != Visibility.Visible) return;

        ImmersiveLyricListView.ScrollIntoView(active, ScrollIntoViewAlignment.Default);
        DispatcherQueue.TryEnqueue(() =>
        {
            var sv = FindDescendantScrollViewer(ImmersiveLyricListView);
            if (sv != null && ImmersiveLyricListView.ContainerFromItem(active) is FrameworkElement container)
            {
                try
                {
                    var transform = container.TransformToVisual(ImmersiveLyricListView);
                    var pt = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                    double targetOffset = sv.VerticalOffset + pt.Y - (ImmersiveLyricListView.ActualHeight * 0.38) + (container.ActualHeight * 0.5);
                    sv.ChangeView(null, Math.Max(0, targetOffset), null, disableAnimation: false);
                }
                catch
                {
                    // 忽略容器尚未布局完成时的坐标转换异常
                }
            }
        });
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var found = FindDescendantScrollViewer(child);
            if (found != null) return found;
        }
        return null;
    }

    // ==================== 全局过渡动画引擎与顶部悬浮自动消失气泡 (对齐 animate.css / fadeInUp / n-message) ====================

    private async void ShowAutoDismissToast(string? message)
    {
        _toastDismissCts?.Cancel();
        if (string.IsNullOrWhiteSpace(message))
        {
            FloatingToastBorder.Visibility = Visibility.Collapsed;
            return;
        }

        _toastDismissCts = new CancellationTokenSource();
        var token = _toastDismissCts.Token;

        StatusBannerText.Text = message;
        FloatingToastBorder.Visibility = Visibility.Visible;
        PlayEntranceAnimation(FloatingToastBorder, 0, -14, 200);

        try
        {
            await Task.Delay(2400, token);
            await PlayExitAnimationAsync(FloatingToastBorder, 0, -10, 180);
            if (!token.IsCancellationRequested)
            {
                FloatingToastBorder.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException)
        {
            // 被新一条提示打断时直接由新提示接管
        }
    }

    private void ApplyCollectionTransitions()
    {
        bool enableAnimate = !ViewModel.Config.Settings.NoAnimate;
        ListViewBase[] collections =
        [
            HomePlaylistsGrid,
            HotArtistsGrid,
            HomeNewAlbumsGrid,
            HomeNewSongsGrid,
            NewAlbumsPageGrid,
            PodcastPreferredGrid,
            PodcastRadiosGrid,
            ToplistGrid,
            SquarePlaylistsGrid,
            DetailSongsListView,
            DetailCompactSongsListView,
            SearchSongsListView,
            SearchPlaylistsGrid,
            SearchAlbumsGrid,
            SearchArtistsGrid,
            SearchBiliListView,
            BiliDedicatedListView,
            MvGridView,
            FavoriteSongsListView,
            HistorySongsListView,
            LocalSongsListView,
            CurrentFolderSongsListView,
            QqCreatedPlaylistsGrid,
            QqCollectedPlaylistsGrid,
            DownloadTasksListView,
            CompletedDownloadsListView,
            UserCreatedPlaylistsGrid,
            UserFavoritePlaylistsGrid,
            UserSubscribedAlbumsGrid,
            UserPlayRecordListView
        ];

        foreach (var col in collections)
        {
            if (col == null) continue;
            if (enableAnimate)
            {
                col.ItemContainerTransitions =
                [
                    new EntranceThemeTransition { IsStaggeringEnabled = true, FromVerticalOffset = 18 },
                    new RepositionThemeTransition()
                ];
            }
            else
            {
                col.ItemContainerTransitions = [];
            }
        }
    }

    /// <summary>
    /// 全局 DWM 合成器原生高刷入场动画（运行于独立系统合成线程，自动跑满 120Hz / 144Hz / 165Hz / 240Hz+ 物理刷新率）
    /// </summary>
    private void PlayEntranceAnimation(UIElement? element, double fromX = 0, double fromY = 20, double baseDurationMs = 280)
    {
        if (element == null) return;

        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        element.Opacity = 1.0;

        if (ViewModel.Config.Settings.NoAnimate)
        {
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Translation");
            visual.StopAnimation("Scale");
            visual.Opacity = 1f;
            visual.Scale = Vector3.One;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            ScheduleAdaptiveWallpaperTextPass(immediate: true);
            return;
        }

        double speed = Math.Clamp(ViewModel.Config.Settings.AnimationSpeed, 0.2, 3.0);
        var moveDur = TimeSpan.FromMilliseconds(Math.Max(70, baseDurationMs / speed));
        var fadeDur = TimeSpan.FromMilliseconds(Math.Max(60, (baseDurationMs * 0.82) / speed));

        var compositor = visual.Compositor;
        var easeOut = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1.0f), new Vector2(0.30f, 1.0f));
        var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);

        var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
        fadeAnim.InsertKeyFrame(0f, 0f);
        fadeAnim.InsertKeyFrame(1f, 1f, easeOut);
        fadeAnim.Duration = fadeDur;
        visual.StartAnimation("Opacity", fadeAnim);

        if (Math.Abs(fromX) > 0.1 || Math.Abs(fromY) > 0.1)
        {
            var moveAnim = compositor.CreateVector3KeyFrameAnimation();
            moveAnim.InsertKeyFrame(0f, new Vector3((float)fromX, (float)fromY, 0f));
            moveAnim.InsertKeyFrame(1f, Vector3.Zero, easeOut);
            moveAnim.Duration = moveDur;
            visual.StartAnimation("Translation", moveAnim);
        }
        else
        {
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }

        batch.End();
        batch.Completed += (_, _) => ScheduleAdaptiveWallpaperTextPass();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ScheduleAdaptiveWallpaperTextPass());
    }

    /// <summary>
    /// 全局 DWM 合成器原生高刷退场动画（异步等待合成器动画完成后再折叠元素，消除掉帧与闪烁）
    /// </summary>
    private Task PlayExitAnimationAsync(UIElement? element, double toX = 0, double toY = 16, double baseDurationMs = 180)
    {
        if (element == null || ViewModel.Config.Settings.NoAnimate)
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource();
        double speed = Math.Clamp(ViewModel.Config.Settings.AnimationSpeed, 0.2, 3.0);
        var duration = TimeSpan.FromMilliseconds(Math.Max(55, baseDurationMs / speed));

        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var easeIn = compositor.CreateCubicBezierEasingFunction(new Vector2(0.38f, 0.0f), new Vector2(1.0f, 1.0f));
        var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);

        var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
        fadeAnim.InsertKeyFrame(0f, visual.Opacity > 0.05f ? visual.Opacity : 1f);
        fadeAnim.InsertKeyFrame(1f, 0f, easeIn);
        fadeAnim.Duration = duration;
        visual.StartAnimation("Opacity", fadeAnim);

        if (Math.Abs(toX) > 0.1 || Math.Abs(toY) > 0.1)
        {
            var moveAnim = compositor.CreateVector3KeyFrameAnimation();
            moveAnim.InsertKeyFrame(0f, Vector3.Zero);
            moveAnim.InsertKeyFrame(1f, new Vector3((float)toX, (float)toY, 0f), easeIn);
            moveAnim.Duration = duration;
            visual.StartAnimation("Translation", moveAnim);
        }

        batch.End();
        batch.Completed += (_, _) =>
        {
            tcs.TrySetResult();
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Translation");
            visual.Opacity = 1f;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        };
        return tcs.Task;
    }

    /// <summary>
    /// 模态弹窗高刷双层入场动画：遮罩平滑淡入 + 弹窗卡片中心微缩放与上浮弹入
    /// </summary>
    private void PlayModalDialogOpenAnimation(UIElement backdrop, FrameworkElement card, double baseDurationMs = 240)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(card, true);
        var bgVisual = ElementCompositionPreview.GetElementVisual(backdrop);
        var cardVisual = ElementCompositionPreview.GetElementVisual(card);
        backdrop.Opacity = 1.0;
        card.Opacity = 1.0;

        if (ViewModel.Config.Settings.NoAnimate)
        {
            bgVisual.StopAnimation("Opacity");
            cardVisual.StopAnimation("Opacity");
            cardVisual.StopAnimation("Translation");
            cardVisual.StopAnimation("Scale");
            bgVisual.Opacity = 1f;
            cardVisual.Opacity = 1f;
            cardVisual.Scale = Vector3.One;
            cardVisual.Properties.InsertVector3("Translation", Vector3.Zero);
            return;
        }

        if (card.ActualWidth <= 0 || card.ActualHeight <= 0)
        {
            backdrop.UpdateLayout();
        }
        float cx = (float)((card.ActualWidth > 0 ? card.ActualWidth : Math.Max(480, card.Width)) * 0.5);
        float cy = (float)((card.ActualHeight > 0 ? card.ActualHeight : 340) * 0.5);
        cardVisual.CenterPoint = new Vector3(cx, cy, 0f);

        double speed = Math.Clamp(ViewModel.Config.Settings.AnimationSpeed, 0.2, 3.0);
        var cardDur = TimeSpan.FromMilliseconds(Math.Max(80, baseDurationMs / speed));
        var fadeDur = TimeSpan.FromMilliseconds(Math.Max(65, (baseDurationMs * 0.78) / speed));

        var compositor = cardVisual.Compositor;
        var springOut = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1.0f), new Vector2(0.30f, 1.0f));
        var fadeEase = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1.0f), new Vector2(0.36f, 1.0f));

        var bgFade = compositor.CreateScalarKeyFrameAnimation();
        bgFade.InsertKeyFrame(0f, 0f);
        bgFade.InsertKeyFrame(1f, 1f, fadeEase);
        bgFade.Duration = fadeDur;
        bgVisual.StartAnimation("Opacity", bgFade);

        var cardFade = compositor.CreateScalarKeyFrameAnimation();
        cardFade.InsertKeyFrame(0f, 0f);
        cardFade.InsertKeyFrame(1f, 1f, fadeEase);
        cardFade.Duration = fadeDur;
        cardVisual.StartAnimation("Opacity", cardFade);

        var cardMove = compositor.CreateVector3KeyFrameAnimation();
        cardMove.InsertKeyFrame(0f, new Vector3(0f, 18f, 0f));
        cardMove.InsertKeyFrame(1f, Vector3.Zero, springOut);
        cardMove.Duration = cardDur;
        cardVisual.StartAnimation("Translation", cardMove);

        var cardScale = compositor.CreateVector3KeyFrameAnimation();
        cardScale.InsertKeyFrame(0f, new Vector3(0.955f, 0.955f, 1f));
        cardScale.InsertKeyFrame(1f, Vector3.One, springOut);
        cardScale.Duration = cardDur;
        cardVisual.StartAnimation("Scale", cardScale);
    }

    /// <summary>
    /// 模态弹窗高刷双层退场动画：卡片中心微缩下沉淡出 + 遮罩平滑消散
    /// </summary>
    private Task PlayModalDialogCloseAnimationAsync(UIElement backdrop, FrameworkElement card, double baseDurationMs = 175)
    {
        if (ViewModel.Config.Settings.NoAnimate)
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource();
        ElementCompositionPreview.SetIsTranslationEnabled(card, true);
        var bgVisual = ElementCompositionPreview.GetElementVisual(backdrop);
        var cardVisual = ElementCompositionPreview.GetElementVisual(card);

        float cx = (float)((card.ActualWidth > 0 ? card.ActualWidth : Math.Max(480, card.Width)) * 0.5);
        float cy = (float)((card.ActualHeight > 0 ? card.ActualHeight : 340) * 0.5);
        cardVisual.CenterPoint = new Vector3(cx, cy, 0f);

        double speed = Math.Clamp(ViewModel.Config.Settings.AnimationSpeed, 0.2, 3.0);
        var duration = TimeSpan.FromMilliseconds(Math.Max(55, baseDurationMs / speed));

        var compositor = cardVisual.Compositor;
        var easeIn = compositor.CreateCubicBezierEasingFunction(new Vector2(0.40f, 0.0f), new Vector2(1.0f, 1.0f));
        var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);

        var bgFade = compositor.CreateScalarKeyFrameAnimation();
        bgFade.InsertKeyFrame(0f, bgVisual.Opacity > 0.05f ? bgVisual.Opacity : 1f);
        bgFade.InsertKeyFrame(1f, 0f, easeIn);
        bgFade.Duration = duration;
        bgVisual.StartAnimation("Opacity", bgFade);

        var cardFade = compositor.CreateScalarKeyFrameAnimation();
        cardFade.InsertKeyFrame(0f, cardVisual.Opacity > 0.05f ? cardVisual.Opacity : 1f);
        cardFade.InsertKeyFrame(1f, 0f, easeIn);
        cardFade.Duration = duration;
        cardVisual.StartAnimation("Opacity", cardFade);

        var cardMove = compositor.CreateVector3KeyFrameAnimation();
        cardMove.InsertKeyFrame(0f, Vector3.Zero);
        cardMove.InsertKeyFrame(1f, new Vector3(0f, 14f, 0f), easeIn);
        cardMove.Duration = duration;
        cardVisual.StartAnimation("Translation", cardMove);

        var cardScale = compositor.CreateVector3KeyFrameAnimation();
        cardScale.InsertKeyFrame(0f, Vector3.One);
        cardScale.InsertKeyFrame(1f, new Vector3(0.96f, 0.96f, 1f), easeIn);
        cardScale.Duration = duration;
        cardVisual.StartAnimation("Scale", cardScale);

        batch.End();
        batch.Completed += (_, _) =>
        {
            tcs.TrySetResult();
            bgVisual.StopAnimation("Opacity");
            cardVisual.StopAnimation("Opacity");
            cardVisual.StopAnimation("Translation");
            cardVisual.StopAnimation("Scale");
            bgVisual.Opacity = 1f;
            cardVisual.Opacity = 1f;
            cardVisual.Scale = Vector3.One;
            cardVisual.Properties.InsertVector3("Translation", Vector3.Zero);
        };
        return tcs.Task;
    }

    private void SwitchVisibleSection(string section)
    {
        if (!string.Equals(_activeSection, section, StringComparison.OrdinalIgnoreCase))
        {
            if (!_isNavigatingBack && !string.IsNullOrWhiteSpace(_activeSection))
            {
                if (_sectionHistory.Count == 0 || !string.Equals(_sectionHistory.Peek(), _activeSection, StringComparison.OrdinalIgnoreCase))
                {
                    _sectionHistory.Push(_activeSection);
                }
            }
            _activeSection = section;
        }

        if (string.Equals(section, "Home", StringComparison.OrdinalIgnoreCase))
        {
            _sectionHistory.Clear();
        }

        // 1:1 对应原版 SearchBar.vue showBackButton：仅在详情页/搜索结果/下载/设置等二级子页且存在返回历史时在主内容区左上显示返回按钮
        bool isBackEligibleSection = section is "Detail" or "Downloads" or "Settings" or "Search";
        bool shouldShowBack = isBackEligibleSection && _sectionHistory.Count > 0;
        if (TopBackButton != null && TopBackButton.Visibility != (shouldShowBack ? Visibility.Visible : Visibility.Collapsed))
        {
            TopBackButton.Visibility = shouldShowBack ? Visibility.Visible : Visibility.Collapsed;
            ScheduleUpdateTitleBarPassthroughRegions();
        }

        HomeView.Visibility = section == "Home" ? Visibility.Visible : Visibility.Collapsed;
        AlbumView.Visibility = section == "Album" ? Visibility.Visible : Visibility.Collapsed;
        PodcastView.Visibility = section == "Podcast" ? Visibility.Visible : Visibility.Collapsed;
        ToplistView.Visibility = section == "Toplist" ? Visibility.Visible : Visibility.Collapsed;
        PlaylistsView.Visibility = section == "Playlists" ? Visibility.Visible : Visibility.Collapsed;
        DetailView.Visibility = section == "Detail" ? Visibility.Visible : Visibility.Collapsed;
        if (section == "Detail")
        {
            SyncDetailHeaderUi();
            if (DetailSearchBox.Text != ViewModel.DetailSearchKeyword)
            {
                DetailSearchBox.Text = ViewModel.DetailSearchKeyword;
            }
            DetailBatchActionPanel.Visibility = ViewModel.IsDetailSelecting ? Visibility.Visible : Visibility.Collapsed;
            DetailStartSelectBtn.Visibility = ViewModel.IsDetailSelecting ? Visibility.Collapsed : Visibility.Visible;
            DetailPageScrollViewer.ChangeView(null, 0, null, true);
        }
        SearchView.Visibility = section == "Search" ? Visibility.Visible : Visibility.Collapsed;
        MvView.Visibility = section == "Mv" ? Visibility.Visible : Visibility.Collapsed;
        BilibiliView.Visibility = section == "Bilibili" ? Visibility.Visible : Visibility.Collapsed;
        HistoryFavoriteView.Visibility = section == "HistoryFavorite" ? Visibility.Visible : Visibility.Collapsed;
        LocalMusicView.Visibility = section == "LocalMusic" ? Visibility.Visible : Visibility.Collapsed;
        QqMusicView.Visibility = section == "QqMusic" ? Visibility.Visible : Visibility.Collapsed;
        DownloadsView.Visibility = section == "Downloads" ? Visibility.Visible : Visibility.Collapsed;
        UserView.Visibility = section == "User" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = section == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        UIElement? targetView = section switch
        {
            "Home" => HomeView,
            "Album" => AlbumView,
            "Podcast" => PodcastView,
            "Toplist" => ToplistView,
            "Playlists" => PlaylistsView,
            "Detail" => DetailView,
            "Search" => SearchView,
            "Mv" => MvView,
            "Bilibili" => BilibiliView,
            "HistoryFavorite" => HistoryFavoriteView,
            "LocalMusic" => LocalMusicView,
            "QqMusic" => QqMusicView,
            "Downloads" => DownloadsView,
            "User" => UserView,
            "Settings" => SettingsView,
            _ => null
        };
        PlayEntranceAnimation(targetView, 0, 22, 320);

        // 同步 NavigationView 选中态高亮条
        foreach (var obj in MainNavView.MenuItems.Concat(MainNavView.FooterMenuItems))
        {
            if (obj is NavigationViewItem navItem && string.Equals(navItem.Tag?.ToString(), section, StringComparison.OrdinalIgnoreCase))
            {
                MainNavView.SelectedItem = navItem;
                break;
            }
        }
    }

    // ==================== 标题栏交互穿透 (Passthrough)、搜索框 I-Beam 光标与返回按钮事件 ====================

    private void OnAppTitleBarLoaded(object sender, RoutedEventArgs e)
    {
        SetProtectedCursorSafe(TopBackButton, InputSystemCursorShape.Hand);
        ScheduleUpdateTitleBarPassthroughRegions();
    }

    private void OnAppTitleBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ScheduleUpdateTitleBarPassthroughRegions();
    }

    private void OnMainNavViewPaneStateChanged(NavigationView sender, object args)
    {
        ScheduleUpdateTitleBarPassthroughRegions();
    }

    private void OnTopSearchBoxLoaded(object sender, RoutedEventArgs e)
    {
        ConfigureSearchBoxCursors(TopSearchBox);
        ScheduleUpdateTitleBarPassthroughRegions();
    }

    private static void SetProtectedCursorSafe(UIElement? element, InputSystemCursorShape shape)
    {
        if (element == null) return;
        try
        {
            var prop = typeof(UIElement).GetProperty(
                "ProtectedCursor",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            prop?.SetValue(element, InputSystemCursor.Create(shape));
        }
        catch
        {
            // 忽略光标反射设置异常
        }
    }

    private void ConfigureSearchBoxCursors(DependencyObject root)
    {
        if (root is UIElement rootElement)
        {
            SetProtectedCursorSafe(rootElement, InputSystemCursorShape.IBeam);
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button btn)
            {
                SetProtectedCursorSafe(btn, InputSystemCursorShape.Hand);
                continue;
            }

            if (child is TextBox innerTextBox)
            {
                SetProtectedCursorSafe(innerTextBox, InputSystemCursorShape.IBeam);
                innerTextBox.SizeChanged -= OnSearchInnerTextBoxSizeChanged;
                innerTextBox.SizeChanged += OnSearchInnerTextBoxSizeChanged;
            }

            ConfigureSearchBoxCursors(child);
        }
    }

    private void OnSearchInnerTextBoxSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is DependencyObject tb)
        {
            // 当输入文字后出现懒加载的清除按钮 (DeleteButton) 时，确保按钮为手型光标，其余输入区为 IBeam 文本光标
            int count = VisualTreeHelper.GetChildrenCount(tb);
            for (int i = 0; i < count; i++)
            {
                ConfigureSearchBoxButtonsOnly(VisualTreeHelper.GetChild(tb, i));
            }
        }
    }

    private static void ConfigureSearchBoxButtonsOnly(DependencyObject node)
    {
        if (node is Button btn)
        {
            SetProtectedCursorSafe(btn, InputSystemCursorShape.Hand);
            return;
        }
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            ConfigureSearchBoxButtonsOnly(VisualTreeHelper.GetChild(node, i));
        }
    }

    private void ScheduleUpdateTitleBarPassthroughRegions()
    {
        UpdateTitleBarPassthroughRegions();
        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateTitleBarPassthroughRegions);
    }

    private void UpdateTitleBarPassthroughRegions()
    {
        try
        {
            if (RootLayout?.XamlRoot == null || _appWindow == null) return;
            var nonClientInputSrc = InputNonClientPointerSource.GetForWindowId(_appWindow.Id);

            if (ImmersiveLyricOverlay != null && ImmersiveLyricOverlay.Visibility == Visibility.Visible)
            {
                nonClientInputSrc.ClearRegionRects(NonClientRegionKind.Passthrough);
                return;
            }

            double scale = RootLayout.XamlRoot.RasterizationScale;
            var rects = new List<RectInt32>(3);

            void AddElementRect(FrameworkElement? elem)
            {
                if (elem == null || elem.Visibility != Visibility.Visible || elem.ActualWidth <= 0 || elem.ActualHeight <= 0)
                {
                    return;
                }
                var transform = elem.TransformToVisual(null);
                var bounds = transform.TransformBounds(new Windows.Foundation.Rect(0, 0, elem.ActualWidth, elem.ActualHeight));
                int x = Math.Max(0, (int)Math.Floor(bounds.X * scale));
                int y = Math.Max(0, (int)Math.Floor(bounds.Y * scale));
                int w = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
                int h = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
                rects.Add(new RectInt32(x, y, w, h));
            }

            AddElementRect(TopBackButton);
            AddElementRect(TopSearchBox);
            AddElementRect(TitleBarRightToolbar);
            if (ThemeCenterModalOverlay?.Visibility == Visibility.Visible)
            {
                AddElementRect(ThemeCenterModalCard);
            }

            if (rects.Count > 0)
            {
                nonClientInputSrc.SetRegionRects(NonClientRegionKind.Passthrough, rects.ToArray());
            }
            else
            {
                nonClientInputSrc.ClearRegionRects(NonClientRegionKind.Passthrough);
            }
        }
        catch
        {
            // 忽略窗口初始化/销毁阶段的句柄状态异常
        }
    }

    private void OnTopBackButtonPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var accent = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 34, 197, 94));
        TopBackButton.BorderBrush = accent;
        TopBackButtonIcon.Foreground = accent;
    }

    private void OnTopBackButtonPointerExited(object sender, PointerRoutedEventArgs e)
    {
        TopBackButton.ClearValue(Control.BorderBrushProperty);
        TopBackButtonIcon.ClearValue(IconElement.ForegroundProperty);
    }

    private async void OnTopBackButtonClick(object sender, RoutedEventArgs e)
    {
        await GoBackSectionAsync();
    }

    // ==================== WinUI 3 NavigationView 与 SelectorBar 导航事件 ====================

    private async void OnNavViewBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        await GoBackSectionAsync();
    }

    private async Task GoBackSectionAsync()
    {
        if (ImmersiveLyricOverlay.Visibility == Visibility.Visible)
        {
            await CloseMusicFullOverlayAsync(animate: true);
            return;
        }

        string target = "Home";
        while (_sectionHistory.Count > 0)
        {
            var candidate = _sectionHistory.Pop();
            if (!string.Equals(candidate, _activeSection, StringComparison.OrdinalIgnoreCase))
            {
                target = candidate;
                break;
            }
        }

        _isNavigatingBack = true;
        try
        {
            await NavigateToSectionAsync(target);
        }
        finally
        {
            _isNavigatingBack = false;
        }
    }

    private async void OnMainNavViewItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string tag)
        {
            await NavigateToSectionAsync(tag);
        }
    }

    private async void OnNavButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        await NavigateToSectionAsync(tag);
    }

    private async Task NavigateToSectionAsync(string tag)
    {
        await CloseMusicFullOverlayAsync(animate: false);
        ViewModel.CurrentSection = tag;

        switch (tag)
        {
            case "Album":
                if (ViewModel.NewAlbums.Count == 0)
                {
                    await ViewModel.LoadNewAlbumsAsync("ALL");
                }
                break;
            case "Podcast":
                if (ViewModel.RecommendedRadios.Count == 0)
                {
                    await ViewModel.LoadPodcastPageAsync(0);
                }
                break;
            case "Toplist":
                await ViewModel.LoadToplistsAsync();
                break;
            case "Playlists":
                if (ViewModel.SquarePlaylists.Count == 0)
                {
                    await ViewModel.LoadSquarePlaylistsAsync("全部");
                }
                break;
            case "Mv":
                SyncMvAreaButtonsUi();
                if (ViewModel.MvList.Count == 0)
                {
                    await ViewModel.LoadMvSectionAsync(ViewModel.SelectedMvArea, loadMore: false);
                }
                break;
            case "HistoryFavorite":
                ViewModel.RefreshHistoryAndFavorites();
                break;
            case "LocalMusic":
                ViewModel.SyncConfiguredFoldersList();
                if (ViewModel.LocalSongs.Count == 0)
                {
                    await ViewModel.ScanLocalMusicAsync();
                }
                break;
            case "QqMusic":
                if (ViewModel.QqCreatedPlaylists.Count == 0 &&
                    (!string.IsNullOrWhiteSpace(ViewModel.Config.Settings.QqMusicUin) ||
                     !string.IsNullOrWhiteSpace(ViewModel.Config.Settings.QqMusicCookie)))
                {
                    await ViewModel.RefreshQqUserProfileAsync();
                }
                break;
            case "Downloads":
                await ViewModel.Downloader.RefreshCompletedDownloadsAsync();
                SwitchDownloadTabUi(ViewModel.Downloader.Tasks.Count > 0 ? "downloading" : "downloaded");
                break;
            case "Settings":
                SyncCookieSettingsRowUi();
                ViewModel.RefreshCacheStats();
                break;
        }

        ScheduleAdaptiveWallpaperTextPass(immediate: true);
    }

    private void OnSearchNavViewItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item || item.Tag is not string tab) return;
        SearchSongsListView.Visibility = tab == "Songs" ? Visibility.Visible : Visibility.Collapsed;
        SearchPlaylistsGrid.Visibility = tab == "Playlists" ? Visibility.Visible : Visibility.Collapsed;
        SearchAlbumsGrid.Visibility = tab == "Albums" ? Visibility.Visible : Visibility.Collapsed;
        SearchArtistsGrid.Visibility = tab == "Artists" ? Visibility.Visible : Visibility.Collapsed;
        SearchMvsGrid.Visibility = tab == "Mvs" ? Visibility.Visible : Visibility.Collapsed;
        SearchBiliListView.Visibility = tab == "Bili" ? Visibility.Visible : Visibility.Collapsed;

        UIElement? active = tab switch
        {
            "Songs" => SearchSongsListView,
            "Playlists" => SearchPlaylistsGrid,
            "Albums" => SearchAlbumsGrid,
            "Artists" => SearchArtistsGrid,
            "Mvs" => SearchMvsGrid,
            "Bili" => SearchBiliListView,
            _ => null
        };
        PlayEntranceAnimation(active, 0, 14, 240);
    }

    private void OnHistoryFavNavViewItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item || item.Tag is not string tab) return;
        FavoriteSongsListView.Visibility = tab == "Fav" ? Visibility.Visible : Visibility.Collapsed;
        HistorySongsListView.Visibility = tab == "History" ? Visibility.Visible : Visibility.Collapsed;
        PlayEntranceAnimation(tab == "Fav" ? FavoriteSongsListView : HistorySongsListView, 0, 14, 240);
    }

    private void OnLoginMethodNavViewItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item || item.Tag is not string tab) return;
        LoginQrPanel.Visibility = tab == "Qr" ? Visibility.Visible : Visibility.Collapsed;
        LoginUidPanel.Visibility = tab == "Uid" ? Visibility.Visible : Visibility.Collapsed;
        LoginCookiePanel.Visibility = tab == "Cookie" ? Visibility.Visible : Visibility.Collapsed;
        LoginImportPanel.Visibility = tab == "Import" ? Visibility.Visible : Visibility.Collapsed;

        UIElement? active = tab switch
        {
            "Qr" => LoginQrPanel,
            "Uid" => LoginUidPanel,
            "Cookie" => LoginCookiePanel,
            "Import" => LoginImportPanel,
            _ => null
        };
        PlayEntranceAnimation(active, 0, 12, 220);
    }

    private void OnQqLoginMethodNavViewItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item || item.Tag is not string tab) return;
        QqLoginLocalSubPanel.Visibility = tab == "Local" ? Visibility.Visible : Visibility.Collapsed;
        QqLoginPasswordSubPanel.Visibility = tab == "Password" ? Visibility.Visible : Visibility.Collapsed;
        QqLoginQrSubPanel.Visibility = tab == "Qr" ? Visibility.Visible : Visibility.Collapsed;

        UIElement? active = tab switch
        {
            "Local" => QqLoginLocalSubPanel,
            "Password" => QqLoginPasswordSubPanel,
            "Qr" => QqLoginQrSubPanel,
            _ => null
        };
        PlayEntranceAnimation(active, 0, 12, 220);
    }

    private void OnUserLibraryNavViewItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item || item.Tag is not string tab) return;
        UserCreatedPlaylistsGrid.Visibility = tab == "Created" ? Visibility.Visible : Visibility.Collapsed;
        UserFavoritePlaylistsGrid.Visibility = tab == "Fav" ? Visibility.Visible : Visibility.Collapsed;
        UserSubscribedAlbumsGrid.Visibility = tab == "Album" ? Visibility.Visible : Visibility.Collapsed;

        UIElement? active = tab switch
        {
            "Created" => UserCreatedPlaylistsGrid,
            "Fav" => UserFavoritePlaylistsGrid,
            "Album" => UserSubscribedAlbumsGrid,
            _ => null
        };
        PlayEntranceAnimation(active, 0, 14, 240);
    }

    private void OnSettingsPillTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tab) return;
        SwitchSettingsTab(tab);
    }

    private void OnSettingsNavViewItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item || item.Tag is not string tab) return;
        SwitchSettingsTab(tab);
    }

    private void SwitchSettingsTab(string tab)
    {
        SettingsBasicPanel.Visibility = tab == "Basic" ? Visibility.Visible : Visibility.Collapsed;
        SettingsThemePanel.Visibility = tab == "Theme" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPlaybackPanel.Visibility = tab == "Playback" ? Visibility.Visible : Visibility.Collapsed;
        SettingsApplicationPanel.Visibility = tab == "Application" ? Visibility.Visible : Visibility.Collapsed;
        SettingsNetworkPanel.Visibility = tab == "Network" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSystemPanel.Visibility = tab == "System" ? Visibility.Visible : Visibility.Collapsed;
        SettingsAboutPanel.Visibility = tab == "About" ? Visibility.Visible : Visibility.Collapsed;

        ApplyDynamicAccentButtonState(SettingsTabBasicBtn, tab == "Basic");
        ApplyDynamicAccentButtonState(SettingsTabThemeBtn, tab == "Theme");
        ApplyDynamicAccentButtonState(SettingsTabPlaybackBtn, tab == "Playback");
        ApplyDynamicAccentButtonState(SettingsTabApplicationBtn, tab == "Application");
        ApplyDynamicAccentButtonState(SettingsTabNetworkBtn, tab == "Network");
        ApplyDynamicAccentButtonState(SettingsTabSystemBtn, tab == "System");
        ApplyDynamicAccentButtonState(SettingsTabAboutBtn, tab == "About");

        if (tab == "Theme")
        {
            PrepareInlineSettingsThemeTab();
        }

        UIElement? active = tab switch
        {
            "Basic" => SettingsBasicPanel,
            "Theme" => SettingsThemePanel,
            "Playback" => SettingsPlaybackPanel,
            "Application" => SettingsApplicationPanel,
            "Network" => SettingsNetworkPanel,
            "System" => SettingsSystemPanel,
            "About" => SettingsAboutPanel,
            _ => null
        };
        PlayEntranceAnimation(active, 0, 16, 260);
    }

    private async void OnStartIntelligenceModeClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.Player.StartIntelligenceModeAsync();
        SyncPlayModeIcons(PlayMode.Intelligence);
    }

    private async void OnSearchBoxTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var text = sender.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            var hot = await ViewModel.Api.GetHotSearchKeywordsAsync();
            sender.ItemsSource = hot.Take(10).ToList();
            return;
        }

        var suggestions = await ViewModel.Unblock.GetSearchSuggestionsAsync(text);
        sender.ItemsSource = suggestions;
    }

    private async void OnSearchBoxQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var query = args.ChosenSuggestion?.ToString() ?? args.QueryText;
        if (string.IsNullOrWhiteSpace(query)) return;
        await CloseMusicFullOverlayAsync(animate: false);
        await ViewModel.ExecuteSearchAsync(query.Trim());
    }

    // ==================== 首页 HomeHero（每日推荐 & 私人 FM）与四大模块事件 ====================

    private async void OnRefreshHomeClick(object sender, RoutedEventArgs e) => await ViewModel.LoadHomeDataAsync();

    private void OnOpenDailyDetailClick(object sender, RoutedEventArgs e) => ViewModel.OpenDailyRecommendDetail();

    private async void OnPlayAllDailyClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.DailyRecommendSongs.Count > 0)
        {
            ViewModel.IsFmModeActive = false;
            await ViewModel.Player.PlaySongAsync(ViewModel.DailyRecommendSongs[0], ViewModel.DailyRecommendSongs);
        }
    }

    private async void OnFmPlayPauseClick(object sender, RoutedEventArgs e) => await ViewModel.TogglePlayFmAsync();

    private async void OnFmNextClick(object sender, RoutedEventArgs e) => await ViewModel.NextFmAsync();

    private async void OnFmTrashClick(object sender, RoutedEventArgs e) => await ViewModel.TrashFmAsync();

    private async void OnHomeNewSongRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is SongItem song)
        {
            e.Handled = true;
            ViewModel.IsFmModeActive = false;
            await ViewModel.Player.PlaySongAsync(song, ViewModel.HomeNewSongs);
        }
    }

    private async void OnPlayAllHomeNewSongsClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.HomeNewSongs.Count > 0)
        {
            ViewModel.IsFmModeActive = false;
            await ViewModel.Player.PlaySongAsync(ViewModel.HomeNewSongs[0], ViewModel.HomeNewSongs);
        }
    }

    // ==================== 新碟上架页 & 电台播客页事件 ====================

    private async void OnAlbumAreaFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string area)
        {
            await ViewModel.LoadNewAlbumsAsync(area);
        }
    }

    private async void OnPodcastCategoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out var cateId))
        {
            await ViewModel.LoadPodcastPageAsync(cateId);
        }
    }

    private async void OnPodcastPreferredRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is RadioProgramItem program && program.MainSong != null)
        {
            e.Handled = true;
            var queue = ViewModel.PodcastTodayPreferred
                .Where(p => p.MainSong != null)
                .Select(p => p.MainSong!)
                .ToList();
            await ViewModel.Player.PlaySongAsync(program.MainSong, queue);
        }
    }

    private async void OnPlayAllPodcastPreferredClick(object sender, RoutedEventArgs e)
    {
        var queue = ViewModel.PodcastTodayPreferred
            .Where(p => p.MainSong != null)
            .Select(p => p.MainSong!)
            .ToList();
        if (queue.Count > 0)
        {
            await ViewModel.Player.PlaySongAsync(queue[0], queue);
        }
    }

    private async void OnRadioStationCardClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RadioStationItem radio)
        {
            await ViewModel.OpenRadioStationDetailAsync(radio);
        }
    }

    // ==================== 歌单 / 专辑 / 歌手 / 详情 / 搜索 / 本地音乐事件 ====================

    private async void OnPlaylistCardClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistItem pl)
        {
            await ViewModel.OpenPlaylistDetailAsync(pl);
        }
    }

    private async void OnAlbumCardClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumItem album)
        {
            await ViewModel.OpenAlbumDetailAsync(album);
        }
    }

    private async void OnArtistCardClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ArtistItem artist)
        {
            await ViewModel.OpenArtistDetailAsync(artist);
        }
    }

    private async void OnPlaylistCatClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string cat)
        {
            await ViewModel.LoadSquarePlaylistsAsync(cat);
        }
    }

    private SongItem? _contextMenuSong;

    private void SyncDetailHeaderUi()
    {
        DetailBadgeTextBlock.Text = ViewModel.DetailBadgeText;
        DetailTitleText.Text = ViewModel.DetailTitle;
        DetailCreatorNameText.Text = ViewModel.DetailCreatorName;
        bool hasCreator = !string.IsNullOrWhiteSpace(ViewModel.DetailCreatorName);
        DetailCreatorNameText.Visibility = hasCreator ? Visibility.Visible : Visibility.Collapsed;
        DetailMetaDot.Visibility = hasCreator ? Visibility.Visible : Visibility.Collapsed;

        bool hasAvatar = !string.IsNullOrWhiteSpace(ViewModel.DetailCreatorAvatarUrl);
        DetailCreatorAvatarBorder.Visibility = hasAvatar ? Visibility.Visible : Visibility.Collapsed;
        if (hasAvatar)
        {
            SetImageUrlSafe(DetailCreatorAvatarImage, ViewModel.DetailCreatorAvatarUrl);
        }

        DetailSongCountTextBlock.Text = ViewModel.DetailSongCountText;
        DetailSubtitleText.Text = ViewModel.DetailSubtitle;
        DetailDescText.Text = ViewModel.DetailDescription;
        DetailDescText.Visibility = string.IsNullOrWhiteSpace(ViewModel.DetailDescription)
            ? Visibility.Collapsed
            : Visibility.Visible;

        SetImageUrlSafe(DetailCoverImage, ViewModel.DetailCoverUrl);

        DetailCollectBtn.Visibility = ViewModel.CanCollectDetail ? Visibility.Visible : Visibility.Collapsed;
        DetailCollectIcon.Glyph = ViewModel.IsDetailCollected ? "\uEB52" : "\uEB51";
        if (ViewModel.IsDetailCollected)
        {
            DetailCollectIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 239, 68, 68));
        }
        else
        {
            DetailCollectIcon.ClearValue(IconElement.ForegroundProperty);
        }
        DetailCollectText.Text = ViewModel.IsDetailCollected ? "取消收藏" : "收藏";
    }

    private void OnDetailSongItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SongItem song && ViewModel.IsDetailSelecting)
        {
            song.IsBatchSelected = !song.IsBatchSelected;
            SyncDetailBatchSelectionUi();
        }
    }

    private async void OnDetailSongRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.IsDetailSelecting) return;
        if (sender is FrameworkElement fe && fe.Tag is SongItem song)
        {
            e.Handled = true;
            ViewModel.IsFmModeActive = false;
            var playList = ViewModel.FilteredDetailSongs.Count > 0 ? ViewModel.FilteredDetailSongs : ViewModel.DetailSongs;
            await ViewModel.Player.PlaySongAsync(song, playList);
        }
    }

    private async void OnDetailRowPlayButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SongItem song)
        {
            if (ViewModel.Player.CurrentSong?.Id == song.Id)
            {
                ViewModel.Player.TogglePlayPause();
            }
            else
            {
                ViewModel.IsFmModeActive = false;
                var playList = ViewModel.FilteredDetailSongs.Count > 0 ? ViewModel.FilteredDetailSongs : ViewModel.DetailSongs;
                await ViewModel.Player.PlaySongAsync(song, playList);
            }
        }
    }

    private async void OnPlayAllDetailSongsClick(object sender, RoutedEventArgs e)
    {
        var playList = ViewModel.FilteredDetailSongs.Count > 0 ? ViewModel.FilteredDetailSongs : ViewModel.DetailSongs;
        if (playList.Count > 0)
        {
            ViewModel.IsFmModeActive = false;
            await ViewModel.Player.PlaySongAsync(playList[0], playList);
        }
    }

    private async void OnToggleCollectDetailClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanCollectDetail || ViewModel.DetailPlaylistId <= 0) return;
        if (ViewModel.CurrentUser == null || ViewModel.Config.Settings.LoginType == "uid")
        {
            ViewModel.StatusToast = "请先使用扫码或 Cookie 登录网易云账号后再收藏歌单";
            return;
        }

        bool targetCollect = !ViewModel.IsDetailCollected;
        bool ok = await ViewModel.Api.SubscribePlaylistAsync(ViewModel.DetailPlaylistId, targetCollect);
        if (ok)
        {
            ViewModel.IsDetailCollected = targetCollect;
            ViewModel.StatusToast = targetCollect ? "已收藏歌单" : "已取消收藏歌单";
            _ = ViewModel.RefreshUserProfileAsync();
        }
        else
        {
            ViewModel.StatusToast = "操作失败，请稍后重试";
        }
    }

    private void OnDetailStartSelectClick(object sender, RoutedEventArgs e)
    {
        ViewModel.IsDetailSelecting = true;
        DetailStartSelectBtn.Visibility = Visibility.Collapsed;
        DetailBatchActionPanel.Visibility = Visibility.Visible;
        SyncDetailBatchSelectionUi();
    }

    private void OnDetailCancelSelectClick(object sender, RoutedEventArgs e)
    {
        ViewModel.IsDetailSelecting = false;
        DetailBatchActionPanel.Visibility = Visibility.Collapsed;
        DetailStartSelectBtn.Visibility = Visibility.Visible;
        DetailSelectAllCheckBox.IsChecked = false;
    }

    private void OnDetailSelectAllClick(object sender, RoutedEventArgs e)
    {
        bool selectAll = DetailSelectAllCheckBox.IsChecked == true;
        foreach (var s in ViewModel.FilteredDetailSongs)
        {
            s.IsBatchSelected = selectAll;
        }
        SyncDetailBatchSelectionUi();
    }

    private void OnDetailSongBatchCheckClick(object sender, RoutedEventArgs e)
    {
        SyncDetailBatchSelectionUi();
    }

    private void SyncDetailBatchSelectionUi()
    {
        int total = ViewModel.FilteredDetailSongs.Count;
        int selected = ViewModel.FilteredDetailSongs.Count(s => s.IsBatchSelected);
        DetailBatchDownloadText.Text = $"下载 ({selected})";
        if (total == 0 || selected == 0)
        {
            DetailSelectAllCheckBox.IsChecked = false;
        }
        else if (selected == total)
        {
            DetailSelectAllCheckBox.IsChecked = true;
        }
        else
        {
            DetailSelectAllCheckBox.IsChecked = null;
        }
    }

    private async void OnDetailBatchDownloadClick(object sender, RoutedEventArgs e)
    {
        var selected = ViewModel.FilteredDetailSongs.Where(s => s.IsBatchSelected).ToList();
        if (selected.Count == 0)
        {
            ViewModel.StatusToast = "请先勾选要下载的歌曲";
            return;
        }
        ViewModel.StatusToast = $"已将 {selected.Count} 首歌曲加入下载队列";
        OnDetailCancelSelectClick(sender, e);
        foreach (var song in selected)
        {
            await ViewModel.Downloader.EnqueueDownloadAsync(song);
        }
    }

    private async void OnDetailBatchAddToQueueClick(object sender, RoutedEventArgs e)
    {
        var selected = ViewModel.FilteredDetailSongs.Where(s => s.IsBatchSelected).ToList();
        if (selected.Count == 0)
        {
            ViewModel.StatusToast = "请先勾选要添加的歌曲";
            return;
        }
        int added = 0;
        foreach (var song in selected)
        {
            if (ViewModel.Player.Queue.All(q => q.Id != song.Id))
            {
                ViewModel.Player.Queue.Add(song);
                added++;
            }
        }
        await ViewModel.Config.SaveQueueAsync(ViewModel.Player.Queue);
        ViewModel.StatusToast = added > 0 ? $"已添加 {added} 首歌曲到播放列表" : "所选歌曲已在播放列表中";
        OnDetailCancelSelectClick(sender, e);
    }

    private void OnDetailSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized) return;
        ViewModel.DetailSearchKeyword = DetailSearchBox.Text;
    }

    private void OnDetailLocateCurrentSongClick(object sender, RoutedEventArgs e)
    {
        var current = ViewModel.Player.CurrentSong;
        if (current == null)
        {
            ViewModel.StatusToast = "当前没有正在播放的歌曲";
            return;
        }
        var target = ViewModel.FilteredDetailSongs.FirstOrDefault(s => s.Id == current.Id);
        if (target == null)
        {
            ViewModel.StatusToast = "当前播放歌曲不在本歌单列表中";
            return;
        }
        var activeList = ViewModel.IsDetailCompactLayout ? DetailCompactSongsListView : DetailSongsListView;
        activeList.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
    }

    private void OnDetailToggleLayoutClick(object sender, RoutedEventArgs e)
    {
        ViewModel.IsDetailCompactLayout = !ViewModel.IsDetailCompactLayout;
        bool compact = ViewModel.IsDetailCompactLayout;
        DetailSongsListView.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        DetailCompactSongsListView.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        DetailLayoutIcon.Glyph = compact ? "\uE8FD" : "\uE80A";
        ViewModel.StatusToast = compact ? "已切换为紧凑列表布局" : "已切换为标准卡片布局";
    }

    private void OnDetailScrollToTopClick(object sender, RoutedEventArgs e)
    {
        DetailPageScrollViewer.ChangeView(null, 0, null, false);
    }

    // ==================== 全局歌曲右键菜单 (1:1 SongItemDropdown.vue) & 添加到歌单抽屉 (PlaylistDrawer.vue) ====================

    private void OnSongItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is SongItem song)
        {
            e.Handled = true;
            ShowSongContextMenu(fe, song, e.GetPosition(fe));
        }
    }

    private void OnSongMoreMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is SongItem song)
        {
            ShowSongContextMenu(fe, song, null);
        }
    }

    private void ShowSongContextMenu(FrameworkElement anchor, SongItem song, Windows.Foundation.Point? position)
    {
        _contextMenuSong = song;
        SetImageUrlSafe(ContextMenuSongCover, song.CoverThumbnailUrl);
        ContextMenuSongTitle.Text = song.Name;
        ContextMenuSongArtist.Text = song.ArtistText;

        bool isLocal = song.IsLocalSong && !string.IsNullOrWhiteSpace(song.LocalFilePath);
        ContextMenuLocateLocalBtn.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;

        bool isFav = ViewModel.Config.IsFavorite(song.Id);
        song.IsLiked = isFav;
        ContextMenuFavoriteIcon.Glyph = isFav ? "\uEB52" : "\uEB51";
        if (isFav)
        {
            ContextMenuFavoriteIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 239, 68, 68));
        }
        else
        {
            ContextMenuFavoriteIcon.ClearValue(IconElement.ForegroundProperty);
        }
        ContextMenuFavoriteText.Text = isFav ? "取消收藏" : "收藏";

        bool isDisliked = ViewModel.Config.Settings.DislikedSongIds.Contains(song.Id);
        ContextMenuDislikeText.Text = isDisliked ? "取消不感兴趣" : "不感兴趣";

        bool canRemove = isLocal || (ViewModel.CurrentSection == "Detail" && ViewModel.CanRemoveFromCurrentDetail);
        ContextMenuRemoveDivider.Visibility = canRemove ? Visibility.Visible : Visibility.Collapsed;
        ContextMenuRemoveBtn.Visibility = canRemove ? Visibility.Visible : Visibility.Collapsed;
        ContextMenuRemoveText.Text = isLocal ? "从本地列表移除" : "从歌单中删除";

        var options = new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft
        };
        if (position.HasValue)
        {
            options.Position = position.Value;
        }
        SongContextMenuFlyout.ShowAt(anchor, options);
    }

    private async void OnContextMenuPlayClick(object sender, RoutedEventArgs e)
    {
        SongContextMenuFlyout.Hide();
        if (_contextMenuSong == null) return;
        ViewModel.IsFmModeActive = false;
        if (ViewModel.CurrentSection == "Detail" && ViewModel.FilteredDetailSongs.Count > 0)
        {
            await ViewModel.Player.PlaySongAsync(_contextMenuSong, ViewModel.FilteredDetailSongs);
        }
        else
        {
            await ViewModel.Player.PlaySongAsync(_contextMenuSong);
        }
    }

    private async void OnContextMenuPlayNextClick(object sender, RoutedEventArgs e)
    {
        SongContextMenuFlyout.Hide();
        if (_contextMenuSong == null) return;
        if (!ViewModel.Player.MoveQueueItemToNext(_contextMenuSong))
        {
            int curIdx = ViewModel.Player.CurrentSong != null
                ? ViewModel.Player.Queue.ToList().FindIndex(s => s.Id == ViewModel.Player.CurrentSong.Id)
                : -1;
            if (curIdx >= 0 && curIdx + 1 <= ViewModel.Player.Queue.Count)
            {
                ViewModel.Player.Queue.Insert(curIdx + 1, _contextMenuSong);
            }
            else
            {
                ViewModel.Player.Queue.Add(_contextMenuSong);
            }
            await ViewModel.Config.SaveQueueAsync(ViewModel.Player.Queue);
        }
        ViewModel.StatusToast = $"已添加到下一首播放：《{_contextMenuSong.Name}》";
    }

    private void OnContextMenuLocateLocalClick(object sender, RoutedEventArgs e)
    {
        SongContextMenuFlyout.Hide();
        if (_contextMenuSong == null || string.IsNullOrWhiteSpace(_contextMenuSong.LocalFilePath)) return;
        try
        {
            if (File.Exists(_contextMenuSong.LocalFilePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{_contextMenuSong.LocalFilePath}\"",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"无法打开文件位置：{ex.Message}";
        }
    }

    private void OnContextMenuDownloadSongClick(object sender, RoutedEventArgs e)
    {
        SongContextMenuFlyout.Hide();
        if (_contextMenuSong == null) return;
        OpenDownloadQualityModal(_contextMenuSong);
    }

    private async void OnContextMenuDownloadLyricClick(object sender, RoutedEventArgs e)
    {
        SongContextMenuFlyout.Hide();
        if (_contextMenuSong == null) return;
        try
        {
            var (lrc, tlrc, _, _) = await ViewModel.Api.GetLyricRawAsync(_contextMenuSong.NumericId);
            if (string.IsNullOrWhiteSpace(lrc))
            {
                ViewModel.StatusToast = $"《{_contextMenuSong.Name}》暂无可下载的歌词";
                return;
            }
            var dir = ViewModel.Config.Settings.DownloadPath;
            Directory.CreateDirectory(dir);
            var safeName = string.Join("_", $"{_contextMenuSong.Name} - {_contextMenuSong.ArtistText}".Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(dir, $"{safeName}.lrc");
            var content = string.IsNullOrWhiteSpace(tlrc) ? lrc : $"{lrc}\n\n{tlrc}";
            await File.WriteAllTextAsync(path, content);
            ViewModel.StatusToast = $"歌词已下载至：{path}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"下载歌词失败：{ex.Message}";
        }
    }

    private string _addToPlaylistTargetPlatform = "netease";

    private async void OnContextMenuAddToNeteasePlaylistClick(object sender, RoutedEventArgs e)
    {
        SongContextMenuFlyout.Hide();
        await OpenAddToPlaylistDrawerAsync("netease");
    }

    private async void OnContextMenuAddToQqPlaylistClick(object sender, RoutedEventArgs e)
    {
        SongContextMenuFlyout.Hide();
        await OpenAddToPlaylistDrawerAsync("qq");
    }

    private async Task OpenAddToPlaylistDrawerAsync(string targetPlatform)
    {
        if (_contextMenuSong == null) return;
        _addToPlaylistTargetPlatform = targetPlatform;

        if (targetPlatform == "qq")
        {
            if (string.IsNullOrWhiteSpace(ViewModel.Config.Settings.QqMusicCookie))
            {
                ViewModel.StatusToast = "添加到 QQ 音乐歌单需要先登录 QQ 音乐账号";
                return;
            }
            if (ViewModel.QqCreatedPlaylists.Count == 0)
            {
                await ViewModel.RefreshQqUserProfileAsync();
            }
            AddToPlaylistDrawerTitleText.Text = "添加到 QQ 音乐歌单";
            NewPlaylistPrivacyRow.Visibility = Visibility.Collapsed;
            AddToPlaylistTargetListView.ItemsSource = ViewModel.QqCreatedPlaylists;
        }
        else
        {
            if (ViewModel.CurrentUser == null || ViewModel.Config.Settings.LoginType == "uid")
            {
                ViewModel.StatusToast = "添加到网易云歌单需要先使用扫码或 Cookie 登录网易云账号";
                return;
            }
            AddToPlaylistDrawerTitleText.Text = "添加到网易云歌单";
            NewPlaylistPrivacyRow.Visibility = Visibility.Visible;
            AddToPlaylistTargetListView.ItemsSource = ViewModel.UserCreatedPlaylists;
        }

        CreatePlaylistFormPanel.Visibility = Visibility.Collapsed;
        CreatePlaylistToggleIcon.Glyph = "\uE710";
        CreatePlaylistToggleText.Text = "创建新歌单";
        NewPlaylistNameInput.Text = string.Empty;
        NewPlaylistPrivateSwitch.IsOn = false;
        AddToPlaylistDrawerOverlay.Visibility = Visibility.Visible;
        PlayEntranceAnimation(AddToPlaylistBackdrop, 0, 0, 200);
        PlayEntranceAnimation(AddToPlaylistDrawerPanel, 340, 0, 260);
    }

    private async Task HideAddToPlaylistDrawerAnimatedAsync()
    {
        if (AddToPlaylistDrawerOverlay.Visibility != Visibility.Visible) return;
        await PlayExitAnimationAsync(AddToPlaylistDrawerPanel, 340, 0, 180);
        AddToPlaylistDrawerOverlay.Visibility = Visibility.Collapsed;
    }

    private async void OnAddToPlaylistBackdropTapped(object sender, TappedRoutedEventArgs e)
    {
        await HideAddToPlaylistDrawerAnimatedAsync();
    }

    private async void OnCloseAddToPlaylistDrawerClick(object sender, RoutedEventArgs e)
    {
        await HideAddToPlaylistDrawerAnimatedAsync();
    }

    private void OnToggleCreatePlaylistFormClick(object sender, RoutedEventArgs e)
    {
        bool show = CreatePlaylistFormPanel.Visibility != Visibility.Visible;
        CreatePlaylistFormPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CreatePlaylistToggleIcon.Glyph = show ? "\uE711" : "\uE710";
        CreatePlaylistToggleText.Text = show ? "取消创建" : "创建新歌单";
    }

    private async void OnConfirmCreatePlaylistClick(object sender, RoutedEventArgs e)
    {
        var name = NewPlaylistNameInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ViewModel.StatusToast = "请输入歌单名称";
            return;
        }

        if (_addToPlaylistTargetPlatform == "qq")
        {
            var createdQq = await ViewModel.QqMusic.CreatePlaylistAsync(name);
            if (createdQq != null)
            {
                CreatePlaylistFormPanel.Visibility = Visibility.Collapsed;
                CreatePlaylistToggleIcon.Glyph = "\uE710";
                CreatePlaylistToggleText.Text = "创建新歌单";
                NewPlaylistNameInput.Text = string.Empty;
                await ViewModel.RefreshQqUserProfileAsync();
                AddToPlaylistTargetListView.ItemsSource = ViewModel.QqCreatedPlaylists;
                ViewModel.StatusToast = $"已在 QQ 音乐创建歌单《{name}》";
            }
            else
            {
                ViewModel.StatusToast = "创建 QQ 音乐歌单失败，请检查登录状态";
            }
            return;
        }

        var created = await ViewModel.Api.CreatePlaylistAsync(name, NewPlaylistPrivateSwitch.IsOn);
        if (created != null)
        {
            CreatePlaylistFormPanel.Visibility = Visibility.Collapsed;
            CreatePlaylistToggleIcon.Glyph = "\uE710";
            CreatePlaylistToggleText.Text = "创建新歌单";
            NewPlaylistNameInput.Text = string.Empty;
            await ViewModel.RefreshUserProfileAsync();
            ViewModel.StatusToast = $"已在网易云创建歌单《{name}》";
        }
        else
        {
            ViewModel.StatusToast = "创建歌单失败，请检查登录状态";
        }
    }

    private async Task<long> ResolveNeteaseSongNumericIdAsync(SongItem song)
    {
        if (song.NumericId > 0) return song.NumericId;
        var keyword = $"{song.Name} {song.ArtistText}".Replace("未知歌手", "").Trim();
        if (string.IsNullOrWhiteSpace(keyword)) return 0;
        try
        {
            var songs = await ViewModel.Api.SearchSongsAsync(keyword, 5, 0);
            var matched = songs.FirstOrDefault(s => s.NumericId > 0);
            if (matched != null)
            {
                return matched.NumericId;
            }
        }
        catch
        {
        }
        return 0;
    }

    private async void OnAddToTargetPlaylistItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistItem playlist && _contextMenuSong != null)
        {
            if (_addToPlaylistTargetPlatform == "qq" || playlist.Source == "qq")
            {
                int dirId = await ViewModel.QqMusic.ResolvePlaylistDirIdAsync(playlist);
                var (okQq, msgQq) = await ViewModel.QqMusic.AddOrRemoveSongInPlaylistAsync(_contextMenuSong, dirId, isDelete: false);
                if (okQq)
                {
                    ViewModel.StatusToast = $"已将《{_contextMenuSong.Name}》添加到 QQ 音乐歌单《{playlist.Name}》";
                    await HideAddToPlaylistDrawerAnimatedAsync();
                    _ = ViewModel.RefreshQqUserProfileAsync();
                }
                else
                {
                    ViewModel.StatusToast = msgQq;
                }
                return;
            }

            long neteaseId = await ResolveNeteaseSongNumericIdAsync(_contextMenuSong);
            if (neteaseId <= 0)
            {
                ViewModel.StatusToast = $"未能在网易云曲库匹配到《{_contextMenuSong.Name}》";
                return;
            }
            bool ok = await ViewModel.Api.UpdatePlaylistTracksAsync("add", playlist.Id, [neteaseId]);
            if (ok)
            {
                ViewModel.StatusToast = $"已将《{_contextMenuSong.Name}》添加到网易云歌单《{playlist.Name}》";
                await HideAddToPlaylistDrawerAnimatedAsync();
                _ = ViewModel.RefreshUserProfileAsync();
            }
            else
            {
                ViewModel.StatusToast = "添加到网易云歌单失败（歌曲可能已存在或登录态已过期）";
            }
        }
    }

    private async Task ToggleSongFavoriteWithCloudSyncAsync(SongItem song)
    {
        var liked = await ViewModel.Config.ToggleFavoriteAsync(song);
        song.IsLiked = liked;
        SyncSongLikeStateAcrossLists(song.Id, liked);
        ViewModel.RefreshHistoryAndFavorites();
        if (ViewModel.Player.CurrentSong?.Id == song.Id)
        {
            ViewModel.IsCurrentSongLiked = liked;
        }

        var syncedPlatforms = new List<string>();

        // 同步到 QQ 音乐“我喜欢”歌单 (dirId = 201)
        if (!string.IsNullOrWhiteSpace(ViewModel.Config.Settings.QqMusicCookie))
        {
            var (okQq, _) = await ViewModel.QqMusic.AddOrRemoveSongInPlaylistAsync(song, 201, isDelete: !liked);
            if (okQq)
            {
                syncedPlatforms.Add("QQ音乐");
                _ = ViewModel.RefreshQqUserProfileAsync();
            }
        }

        // 同步到网易云“我喜欢的音乐”歌单
        if (ViewModel.CurrentUser != null && ViewModel.Config.Settings.LoginType != "uid")
        {
            var neteaseFavPlaylist = ViewModel.UserCreatedPlaylists.FirstOrDefault();
            if (neteaseFavPlaylist != null)
            {
                long neteaseId = await ResolveNeteaseSongNumericIdAsync(song);
                if (neteaseId > 0)
                {
                    bool okNe = await ViewModel.Api.UpdatePlaylistTracksAsync(liked ? "add" : "del", neteaseFavPlaylist.Id, [neteaseId]);
                    if (okNe)
                    {
                        syncedPlatforms.Add("网易云");
                        _ = ViewModel.RefreshUserProfileAsync();
                    }
                }
            }
        }

        string syncSuffix = syncedPlatforms.Count > 0 ? $"（已同步至 {string.Join(" / ", syncedPlatforms)}）" : string.Empty;
        ViewModel.StatusToast = liked
            ? $"已添加到我喜欢的音乐{syncSuffix}：《{song.Name}》"
            : $"已取消喜欢{syncSuffix}：《{song.Name}》";
    }

    private async void OnContextMenuToggleFavoriteClick(object sender, RoutedEventArgs e)
    {
        SongContextMenuFlyout.Hide();
        if (_contextMenuSong == null) return;
        await ToggleSongFavoriteWithCloudSyncAsync(_contextMenuSong);
    }

    private async void OnContextMenuToggleDislikeClick(object sender, RoutedEventArgs e)
    {
        SongContextMenuFlyout.Hide();
        if (_contextMenuSong == null) return;
        if (ViewModel.Config.Settings.DislikedSongIds.Contains(_contextMenuSong.Id))
        {
            ViewModel.Config.Settings.DislikedSongIds.Remove(_contextMenuSong.Id);
            ViewModel.StatusToast = $"已取消不感兴趣：《{_contextMenuSong.Name}》";
        }
        else
        {
            ViewModel.Config.Settings.DislikedSongIds.Add(_contextMenuSong.Id);
            ViewModel.StatusToast = $"已标记为不感兴趣：《{_contextMenuSong.Name}》";
        }
        await ViewModel.Config.SaveSettingsAsync();
        ViewModel.RebuildFilteredDetailSongs();
    }

    private async void OnContextMenuRemoveClick(object sender, RoutedEventArgs e)
    {
        SongContextMenuFlyout.Hide();
        if (_contextMenuSong == null) return;
        if (_contextMenuSong.IsLocalSong)
        {
            await ViewModel.RemoveSongFromLocalListAsync(_contextMenuSong);
            return;
        }
        if (ViewModel.CurrentSection == "Detail" && ViewModel.CanRemoveFromCurrentDetail)
        {
            if (ViewModel.DetailType == "qqPlaylist")
            {
                int dirId = ViewModel.DetailQqDirId > 0 ? ViewModel.DetailQqDirId : 201;
                var (okQq, msgQq) = await ViewModel.QqMusic.AddOrRemoveSongInPlaylistAsync(_contextMenuSong, dirId, isDelete: true);
                if (okQq)
                {
                    ViewModel.DetailSongs.Remove(_contextMenuSong);
                    ViewModel.RebuildFilteredDetailSongs();
                    ViewModel.DetailSongCountText = $"{ViewModel.DetailSongs.Count} 首音乐";
                    ViewModel.StatusToast = $"已从 QQ 音乐歌单中删除：《{_contextMenuSong.Name}》";
                    _ = ViewModel.RefreshQqUserProfileAsync();
                }
                else
                {
                    ViewModel.StatusToast = msgQq;
                }
                return;
            }

            if (ViewModel.DetailPlaylistId > 0)
            {
                bool ok = await ViewModel.Api.UpdatePlaylistTracksAsync("del", ViewModel.DetailPlaylistId, [_contextMenuSong.NumericId]);
                if (ok)
                {
                    ViewModel.DetailSongs.Remove(_contextMenuSong);
                    ViewModel.RebuildFilteredDetailSongs();
                    ViewModel.DetailSongCountText = $"{ViewModel.DetailSongs.Count} 首音乐";
                    ViewModel.StatusToast = $"已从歌单中删除：《{_contextMenuSong.Name}》";
                    _ = ViewModel.RefreshUserProfileAsync();
                }
                else
                {
                    ViewModel.StatusToast = "从歌单删除歌曲失败";
                }
            }
        }
    }

    private void SyncSongLikeStateAcrossLists(string songId, bool liked)
    {
        IEnumerable<SongItem>[] lists =
        [
            ViewModel.Player.Queue,
            ViewModel.DetailSongs,
            ViewModel.FilteredDetailSongs,
            ViewModel.DailyRecommendSongs,
            ViewModel.DailyPreviewSongs,
            ViewModel.HomeNewSongs,
            ViewModel.SearchSongs,
            ViewModel.HistorySongs,
            ViewModel.FavoriteSongs,
            ViewModel.LocalSongs
        ];
        foreach (var list in lists)
        {
            foreach (var s in list.Where(x => x.Id == songId))
            {
                s.IsLiked = liked;
            }
        }
    }

    private async void OnSearchSongRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is SongItem song)
        {
            e.Handled = true;
            ViewModel.IsFmModeActive = false;
            await ViewModel.Player.PlaySongAsync(song, ViewModel.SearchSongs);
        }
    }

    private async void OnBiliVideoRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is BilibiliVideoItem video)
        {
            e.Handled = true;
            await ViewModel.PlayBilibiliVideoAsAudioAsync(video);
        }
    }

    private async void OnBiliSearchButtonClick(object sender, RoutedEventArgs e)
    {
        var keyword = BiliInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(keyword)) return;
        ViewModel.IsLoading = true;
        try
        {
            var videos = await ViewModel.Bilibili.SearchVideosAsync(keyword, 1, 25);
            ViewModel.BilibiliVideos.Clear();
            foreach (var v in videos) ViewModel.BilibiliVideos.Add(v);
        }
        finally
        {
            ViewModel.IsLoading = false;
        }
    }

    private bool _mvMediaEndedHooked;

    private void EnsureMvMediaEndedHooked()
    {
        if (_mvMediaEndedHooked || MvMediaPlayer.MediaPlayer == null) return;
        _mvMediaEndedHooked = true;
        MvMediaPlayer.MediaPlayer.MediaEnded += (player, _) =>
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (!ViewModel.IsMvOverlayOpen) return;
                if (ViewModel.IsMvSingleLoop)
                {
                    try
                    {
                        player.PlaybackSession.Position = TimeSpan.Zero;
                        player.Play();
                    }
                    catch
                    {
                        // 忽略重播异常
                    }
                }
                else
                {
                    await ViewModel.PlayNextMvAsync();
                }
            });
        };
    }

    private void SyncMvOverlayHeaderUi()
    {
        MvOverlayTitleText.Text = string.IsNullOrWhiteSpace(ViewModel.CurrentMvTitle) ? "MV 播放" : ViewModel.CurrentMvTitle;
        MvOverlayArtistText.Text = string.IsNullOrWhiteSpace(ViewModel.CurrentMvArtist) ? string.Empty : $"— {ViewModel.CurrentMvArtist}";
        MvResolutionText.Text = $"{ViewModel.CurrentMvResolution}P";
        if (ViewModel.IsMvSingleLoop)
        {
            MvLoopModeIcon.Glyph = "\uE8ED";
            MvLoopModeText.Text = "单曲循环";
            ToolTipService.SetToolTip(MvLoopModeBtn, "当前：单曲循环（点击切换自动连播）");
        }
        else
        {
            MvLoopModeIcon.Glyph = "\uE8EE";
            MvLoopModeText.Text = "自动连播";
            ToolTipService.SetToolTip(MvLoopModeBtn, "当前：自动连播（点击切换单曲循环）");
        }
    }

    private Windows.UI.Color GetActiveThemeAccentColor()
    {
        var raw = ViewModel.Theme.GetEffectiveTheme();
        bool isLight = IsCurrentUiLightMode;
        var resolved = ThemeService.ResolveModeAdaptedProfile(raw, isLight);
        return ThemeService.ParseColorOrDefault(resolved.AccentColorHex, Windows.UI.Color.FromArgb(255, 16, 185, 129));
    }

    private void SyncMvAreaButtonsUi()
    {
        var activeArea = ViewModel.SelectedMvArea;
        var accent = GetActiveThemeAccentColor();
        foreach (var child in MvAreaTabsPanel.Children)
        {
            if (child is Button btn && btn.Tag is string tag)
            {
                bool isSelected = string.Equals(tag, activeArea, StringComparison.OrdinalIgnoreCase);
                btn.Background = isSelected
                    ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(36, accent.R, accent.G, accent.B))
                    : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                btn.BorderBrush = isSelected
                    ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(90, accent.R, accent.G, accent.B))
                    : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];
                btn.Foreground = isSelected
                    ? new Microsoft.UI.Xaml.Media.SolidColorBrush(accent)
                    : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
                btn.FontWeight = isSelected
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal;
            }
        }
    }

    private void SyncMvLoadMoreButtonUi()
    {
        MvLoadMoreBtn.Visibility = ViewModel.MvHasMore ? Visibility.Visible : Visibility.Collapsed;
        MvLoadMoreBtn.IsEnabled = !ViewModel.IsLoadingMoreMv;
        MvLoadMoreBtn.Content = ViewModel.IsLoadingMoreMv ? "加载中..." : "加载更多 MV";
    }

    private async void OnMvAreaTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string area)
        {
            await ViewModel.LoadMvSectionAsync(area, loadMore: false);
            PlayEntranceAnimation(MvGridView, 0, 14, 240);
        }
    }

    private void OnMvScrollViewerViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (ViewModel.IsLoadingMoreMv || !ViewModel.MvHasMore) return;
        if (sv.ScrollableHeight > 0 && sv.VerticalOffset >= sv.ScrollableHeight - 160)
        {
            _ = ViewModel.LoadMvSectionAsync(loadMore: true);
        }
    }

    private async void OnLoadMoreMvClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadMvSectionAsync(loadMore: true);
    }

    private async void OnMvCardClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MvItem mv)
        {
            await ViewModel.PlayMvAsync(mv);
        }
    }

    private void OnCloseMvOverlayClick(object sender, RoutedEventArgs e)
    {
        ViewModel.CloseMvOverlay();
    }

    private async void OnPrevMvClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.PlayPrevMvAsync();
    }

    private async void OnNextMvClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.PlayNextMvAsync();
    }

    private void OnToggleMvLoopModeClick(object sender, RoutedEventArgs e)
    {
        ViewModel.IsMvSingleLoop = !ViewModel.IsMvSingleLoop;
    }

    private async void OnSelectMvResolutionClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string resStr && int.TryParse(resStr, out var res))
        {
            await ViewModel.SwitchMvResolutionAsync(res);
        }
    }

    private async void OnFavoriteSongRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is SongItem song)
        {
            e.Handled = true;
            await ViewModel.Player.PlaySongAsync(song, ViewModel.FavoriteSongs);
        }
    }

    private async void OnHistorySongRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is SongItem song)
        {
            e.Handled = true;
            await ViewModel.Player.PlaySongAsync(song, ViewModel.HistorySongs);
        }
    }

    private async void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.Config.ClearPlayHistoryAsync();
        ViewModel.RefreshHistoryAndFavorites();
    }

    private async void OnScanLocalMusicClick(object sender, RoutedEventArgs e) => await ViewModel.ScanLocalMusicAsync();

    private void OnLocalMusicSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized) return;
        ViewModel.LocalSearchKeyword = LocalMusicSearchBox.Text;
    }

    private async void OnLocalViewModeSwitchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string mode) return;
        SwitchLocalMusicViewModeUi(mode);
        ViewModel.Config.Settings.LocalMusicViewMode = mode;
        await ViewModel.Config.SaveSettingsAsync();
    }

    private static void ApplyDynamicAccentButtonState(Button btn, bool isAccent)
    {
        btn.ClearValue(Control.BackgroundProperty);
        btn.ClearValue(Control.ForegroundProperty);
        btn.ClearValue(Control.BorderBrushProperty);
        btn.Style = (Style)Application.Current.Resources[isAccent ? "BrandPrimaryButtonStyle" : "SecondaryTabButtonStyle"];
    }

    private void SwitchLocalMusicViewModeUi(string mode)
    {
        bool isFolder = mode != "list";
        LocalFolderSplitGrid.Visibility = isFolder ? Visibility.Visible : Visibility.Collapsed;
        LocalFlatListCard.Visibility = isFolder ? Visibility.Collapsed : Visibility.Visible;

        ApplyDynamicAccentButtonState(LocalFolderModeBtn, isFolder);
        ApplyDynamicAccentButtonState(LocalListModeBtn, !isFolder);

        if (_isInitialized)
        {
            PlayEntranceAnimation(isFolder ? LocalFolderSplitGrid : LocalFlatListCard, 0, 14, 240);
        }
    }

    private void OnFolderTreeNodeItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LocalMusicFolderNode node)
        {
            ViewModel.SelectLocalFolderNode(node);
            if (node.HasChildren)
            {
                if (node.IsExpanded)
                {
                    // 收起：保持现有原生收起过渡动画
                    ViewModel.ToggleExpandFolderNode(node);
                }
                else
                {
                    // 展开：使用同帧同步展开下拉与下方文件夹顺滑位移动画，消除两段式生硬跳变
                    ExpandFolderNodeSmoothly(node);
                }
            }
        }
    }

    private void ExpandFolderNodeSmoothly(LocalMusicFolderNode parentNode)
    {
        var beforeTops = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double parentTop = 0;

        foreach (var item in ViewModel.VisibleFolderNodes)
        {
            if (FolderTreeListView.ContainerFromItem(item) is UIElement container)
            {
                var pt = container.TransformToVisual(FolderTreeListView).TransformPoint(new Windows.Foundation.Point(0, 0));
                beforeTops[item.Key] = pt.Y;
                if (string.Equals(item.Key, parentNode.Key, StringComparison.OrdinalIgnoreCase))
                {
                    parentTop = pt.Y;
                }
            }
        }

        var savedTransitions = FolderTreeListView.ItemContainerTransitions;
        FolderTreeListView.ItemContainerTransitions = null;

        try
        {
            ViewModel.ToggleExpandFolderNode(parentNode);
            FolderTreeListView.UpdateLayout();

            int newChildOrder = 0;
            foreach (var item in ViewModel.VisibleFolderNodes)
            {
                if (FolderTreeListView.ContainerFromItem(item) is not UIElement container) continue;

                var afterTop = container.TransformToVisual(FolderTreeListView).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                ElementCompositionPreview.SetIsTranslationEnabled(container, true);
                var visual = ElementCompositionPreview.GetElementVisual(container);
                var compositor = visual.Compositor;
                var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1.0f), new Vector2(0.36f, 1.0f));

                if (beforeTops.TryGetValue(item.Key, out var prevTop))
                {
                    float deltaY = (float)(prevTop - afterTop);
                    if (Math.Abs(deltaY) >= 1.0f)
                    {
                        var moveAnim = compositor.CreateVector3KeyFrameAnimation();
                        moveAnim.InsertKeyFrame(0f, new Vector3(0f, deltaY, 0f));
                        moveAnim.InsertKeyFrame(1f, Vector3.Zero, easing);
                        moveAnim.Duration = TimeSpan.FromMilliseconds(280);
                        visual.StartAnimation("Translation", moveAnim);
                    }
                }
                else
                {
                    // 新展开的子文件夹：从父文件夹下方顺滑下拉展开并渐显
                    float rawDist = (float)Math.Max(14.0, afterTop - parentTop);
                    float startOffsetY = -Math.Min(44f, rawDist * 0.65f);

                    var slideAnim = compositor.CreateVector3KeyFrameAnimation();
                    slideAnim.InsertKeyFrame(0f, new Vector3(0f, startOffsetY, 0f));
                    slideAnim.InsertKeyFrame(1f, Vector3.Zero, easing);
                    slideAnim.Duration = TimeSpan.FromMilliseconds(280 + Math.Min(newChildOrder * 12, 60));

                    var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
                    fadeAnim.InsertKeyFrame(0f, 0f);
                    fadeAnim.InsertKeyFrame(1f, 1f, easing);
                    fadeAnim.Duration = TimeSpan.FromMilliseconds(240 + Math.Min(newChildOrder * 10, 50));

                    visual.StartAnimation("Translation", slideAnim);
                    visual.StartAnimation("Opacity", fadeAnim);
                    newChildOrder++;
                }
            }
        }
        finally
        {
            FolderTreeListView.ItemContainerTransitions = savedTransitions;
        }
    }

    private async void OnCurrentFolderSongRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is SongItem song)
        {
            e.Handled = true;
            ViewModel.IsFmModeActive = false;
            await ViewModel.Player.PlaySongAsync(song, ViewModel.CurrentFolderSongs);
        }
    }

    private async void OnPlayAllLocalMusicClick(object sender, RoutedEventArgs e)
    {
        bool isFolderMode = LocalFolderSplitGrid.Visibility == Visibility.Visible;
        var targetQueue = isFolderMode && ViewModel.CurrentFolderSongs.Count > 0
            ? ViewModel.CurrentFolderSongs
            : ViewModel.FilteredLocalSongs;

        if (targetQueue.Count > 0)
        {
            ViewModel.IsFmModeActive = false;
            await ViewModel.Player.PlaySongAsync(targetQueue[0], targetQueue);
        }
    }

    private async void OnLocalSongRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is SongItem song)
        {
            e.Handled = true;
            ViewModel.IsFmModeActive = false;
            await ViewModel.Player.PlaySongAsync(song, ViewModel.FilteredLocalSongs);
        }
    }

    private void OnLocatePlayingLocalSongClick(object sender, RoutedEventArgs e)
    {
        var (folderNode, song) = ViewModel.LocateCurrentPlayingLocalSong();
        if (song == null) return;

        if (LocalFolderSplitGrid.Visibility == Visibility.Visible)
        {
            if (folderNode != null)
            {
                FolderTreeListView.SelectedItem = folderNode;
                FolderTreeListView.ScrollIntoView(folderNode, ScrollIntoViewAlignment.Leading);
            }
            CurrentFolderSongsListView.SelectedItem = song;
            CurrentFolderSongsListView.ScrollIntoView(song, ScrollIntoViewAlignment.Leading);
        }
        else
        {
            LocalSongsListView.SelectedItem = song;
            LocalSongsListView.ScrollIntoView(song, ScrollIntoViewAlignment.Leading);
        }
    }

    private IntPtr GetDialogOwnerHwnd() => _hWnd != IntPtr.Zero ? _hWnd : WindowNative.GetWindowHandle(this);

    private async void OnAddLocalMusicFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string? folderPath = await Helpers.Win32Interop.PickFolderAsync(GetDialogOwnerHwnd(), "选择本地音乐扫描文件夹");
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                await ViewModel.AddLocalMusicFolderAsync(folderPath);
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"添加扫描文件夹失败：{ex.Message}";
        }
    }

    private async void OnMoveLocalFolderUpClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string folderPath)
        {
            await ViewModel.MoveLocalMusicFolderAsync(folderPath, -1);
        }
    }

    private async void OnMoveLocalFolderDownClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string folderPath)
        {
            await ViewModel.MoveLocalMusicFolderAsync(folderPath, 1);
        }
    }

    private async void OnRemoveLocalFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string folderPath)
        {
            await ViewModel.RemoveLocalMusicFolderAsync(folderPath);
        }
    }

    private void OnOpenLocalSongLocationClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SongItem song && !string.IsNullOrWhiteSpace(song.LocalFilePath))
        {
            try
            {
                if (File.Exists(song.LocalFilePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{song.LocalFilePath}\"",
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                ViewModel.StatusToast = $"无法打开文件位置：{ex.Message}";
            }
        }
    }

    private async void OnRemoveLocalSongFromListClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SongItem song)
        {
            await ViewModel.RemoveSongFromLocalListAsync(song);
        }
    }

    private async void OnSongLikeButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SongItem song)
        {
            await ToggleSongFavoriteWithCloudSyncAsync(song);
        }
    }

    private void OnSongDownloadButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SongItem song)
        {
            OpenDownloadQualityModal(song);
        }
    }

    private void OnDownloadCurrentSongClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Player.CurrentSong == null) return;
        OpenDownloadQualityModal(ViewModel.Player.CurrentSong);
    }

    private void OnOpenDownloadFolderClick(object sender, RoutedEventArgs e)
    {
        var dir = ViewModel.Config.Settings.DownloadPath;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true
        });
    }

    // ==================== 下载管理页面 (DownloadPage.vue) 与下载设置抽屉 (DownloadSettingsDrawer) ====================

    private void OnDownloadTabSwitchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tab)
        {
            SwitchDownloadTabUi(tab);
        }
    }

    private void SwitchDownloadTabUi(string tab)
    {
        _activeDownloadTab = tab == "downloading" ? "downloading" : "downloaded";
        bool isDownloading = _activeDownloadTab == "downloading";

        DownloadingTabPanel.Visibility = isDownloading ? Visibility.Visible : Visibility.Collapsed;
        DownloadedTabPanel.Visibility = isDownloading ? Visibility.Collapsed : Visibility.Visible;

        bool isDark = RootLayout.ActualTheme == ElementTheme.Dark;
        var activeBg = new SolidColorBrush(isDark
            ? Windows.UI.Color.FromArgb(255, 38, 43, 53)
            : Windows.UI.Color.FromArgb(255, 255, 255, 255));
        var transparentBg = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var brandFg = (Brush)Application.Current.Resources["BrandAccentBrush"];
        var defaultFg = new SolidColorBrush(isDark
            ? Windows.UI.Color.FromArgb(225, 255, 255, 255)
            : Windows.UI.Color.FromArgb(225, 31, 41, 55));

        DownloadTabDownloadingBtn.Background = isDownloading ? activeBg : transparentBg;
        DownloadTabDownloadingBtn.Foreground = isDownloading ? brandFg : defaultFg;
        DownloadTabDownloadedBtn.Background = !isDownloading ? activeBg : transparentBg;
        DownloadTabDownloadedBtn.Foreground = !isDownloading ? brandFg : defaultFg;

        UpdateDownloadViewState();
        if (_isInitialized)
        {
            PlayEntranceAnimation(isDownloading ? DownloadingTabPanel : DownloadedTabPanel, 0, 14, 240);
        }
    }

    private void UpdateDownloadViewState()
    {
        bool isDownloading = _activeDownloadTab == "downloading";
        DownloadHeroTitleText.Text = isDownloading ? "下载中" : "已下载";
        DownloadHeroSubtitleText.Text = isDownloading
            ? $"总进度: {ViewModel.Downloader.GetTotalProgress():F1}%"
            : $"共 {ViewModel.Downloader.CompletedTasks.Count} 首歌曲";

        bool hasActiveTasks = ViewModel.Downloader.Tasks.Count > 0;
        DownloadingEmptyState.Visibility = hasActiveTasks ? Visibility.Collapsed : Visibility.Visible;
        DownloadTasksListView.Visibility = hasActiveTasks ? Visibility.Visible : Visibility.Collapsed;

        bool hasCompletedTasks = ViewModel.Downloader.CompletedTasks.Count > 0;
        DownloadedEmptyState.Visibility = hasCompletedTasks ? Visibility.Collapsed : Visibility.Visible;
        CompletedDownloadsListView.Visibility = hasCompletedTasks ? Visibility.Visible : Visibility.Collapsed;

        ClearDownloadHistoryBtn.Visibility = (!isDownloading && hasCompletedTasks)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnPauseDownloadTaskClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string taskId)
        {
            ViewModel.Downloader.PauseTask(taskId);
            ViewModel.StatusToast = "已暂停下载任务";
        }
    }

    private void OnResumeDownloadTaskClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string taskId)
        {
            ViewModel.Downloader.ResumeTask(taskId);
            ViewModel.StatusToast = "已恢复下载任务";
        }
    }

    private void OnCancelDownloadTaskClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string taskId)
        {
            ViewModel.Downloader.CancelTask(taskId);
            ViewModel.StatusToast = "已取消下载任务";
        }
    }

    private async void OnPlayCompletedDownloadClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not CompletedDownloadItem item) return;
        await PlayCompletedDownloadItemInternalAsync(item);
    }

    private async void OnCompletedDownloadRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not CompletedDownloadItem item) return;
        e.Handled = true;
        await PlayCompletedDownloadItemInternalAsync(item);
    }

    private async Task PlayCompletedDownloadItemInternalAsync(CompletedDownloadItem item)
    {
        if (!File.Exists(item.FilePath))
        {
            ViewModel.Downloader.CompletedTasks.Remove(item);
            UpdateDownloadViewState();
            ViewModel.StatusToast = "文件不存在或已被移动，已从记录中移除";
            return;
        }

        var song = item.ToSongItem();
        var queue = ViewModel.Downloader.CompletedTasks
            .Where(x => File.Exists(x.FilePath))
            .Select(x => x.ToSongItem())
            .ToList();
        await ViewModel.Player.PlaySongAsync(song, queue);
    }

    private void OnCopyCompletedPathClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not CompletedDownloadItem item) return;

        var dp = new DataPackage();
        dp.SetText(item.FilePath);
        Clipboard.SetContent(dp);
        ViewModel.StatusToast = "路径已复制到剪贴板";
    }

    private void OnOpenCompletedItemFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not CompletedDownloadItem item) return;

        if (File.Exists(item.FilePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{item.FilePath}\"",
                UseShellExecute = true
            });
        }
        else
        {
            var dir = Path.GetDirectoryName(item.FilePath) ?? ViewModel.Config.Settings.DownloadPath;
            if (Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
    }

    private async void OnDeleteCompletedDownloadClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not CompletedDownloadItem item) return;

        var dialog = new ContentDialog
        {
            XamlRoot = RootLayout.XamlRoot,
            Title = "删除确认",
            Content = $"确定要删除歌曲 \"{item.DisplayName}\" 吗？此操作不可恢复。",
            PrimaryButtonText = "确定删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            bool deleted = await ViewModel.Downloader.DeleteCompletedDownloadAsync(item);
            ViewModel.StatusToast = deleted
                ? $"已删除歌曲：{item.DisplayName}"
                : $"删除失败，文件可能正在被占用";
        }
    }

    private async void OnClearDownloadHistoryClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootLayout.XamlRoot,
            Title = "清空下载记录",
            Content = "确定要清空所有下载记录吗？此操作不会删除已下载的音乐文件，但将清空所有记录。",
            PrimaryButtonText = "确定清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.Downloader.ClearCompletedRecordsAsync();
            ViewModel.StatusToast = "下载记录已清空";
        }
    }

    // ---------- 下载设置抽屉交互 ----------

    private void OnOpenDownloadSettingsDrawerClick(object sender, RoutedEventArgs e)
    {
        _isLoadingDownloadDrawer = true;
        var s = ViewModel.Config.Settings;
        DownloadDrawerPathBox.Text = s.DownloadPath;
        DownloadDrawerSaveLyricToggle.IsOn = s.DownloadSaveLyricFile;
        DownloadDrawerConcurrentBox.Value = Math.Clamp(s.DownloadMaxConcurrent, 1, 5);

        ParseDownloadFormatString(
            string.IsNullOrWhiteSpace(s.DownloadNameFormat) ? "{songName} - {artistName}" : s.DownloadNameFormat,
            s.DownloadSeparator);

        DownloadSeparatorCustomBox.Text = _downloadSeparator;
        RefreshDownloadFormatDrawerUi();
        _downloadDrawerModified = false;
        _isLoadingDownloadDrawer = false;

        DownloadSettingsDrawerOverlay.Visibility = Visibility.Visible;
        PlayEntranceAnimation(DownloadSettingsBackdrop, 0, 0, 220);
        PlayEntranceAnimation(DownloadSettingsDrawerPanel, 380, 0, 280);
    }

    private async Task HideDownloadSettingsDrawerAnimatedAsync()
    {
        await PlayExitAnimationAsync(DownloadSettingsDrawerPanel, 380, 0, 200);
        DownloadSettingsDrawerOverlay.Visibility = Visibility.Collapsed;
    }

    private async void OnCloseDownloadSettingsDrawerClick(object sender, RoutedEventArgs e)
    {
        await TryCloseDownloadSettingsDrawerAsync();
    }

    private async void OnDownloadSettingsBackdropTapped(object sender, TappedRoutedEventArgs e)
    {
        await TryCloseDownloadSettingsDrawerAsync();
    }

    private async Task TryCloseDownloadSettingsDrawerAsync()
    {
        if (!_downloadDrawerModified)
        {
            await HideDownloadSettingsDrawerAnimatedAsync();
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootLayout.XamlRoot,
            Title = "保存设置",
            Content = "当前下载设置未保存，是否保存更改？",
            PrimaryButtonText = "保存",
            SecondaryButtonText = "放弃",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await SaveDownloadSettingsDrawerInternalAsync();
            await HideDownloadSettingsDrawerAnimatedAsync();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            _downloadDrawerModified = false;
            await HideDownloadSettingsDrawerAnimatedAsync();
        }
    }

    private async void OnPickDownloadFolderFromDrawerClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string? folderPath = await Helpers.Win32Interop.PickFolderAsync(GetDialogOwnerHwnd(), "选择音乐下载保存目录");
            if (!string.IsNullOrWhiteSpace(folderPath) && !string.Equals(DownloadDrawerPathBox.Text, folderPath, StringComparison.OrdinalIgnoreCase))
            {
                DownloadDrawerPathBox.Text = folderPath;
                _downloadDrawerModified = true;
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"选择下载目录失败：{ex.Message}";
        }
    }

    private void OnOpenDownloadFolderFromDrawerClick(object sender, RoutedEventArgs e)
    {
        var dir = string.IsNullOrWhiteSpace(DownloadDrawerPathBox.Text)
            ? ViewModel.Config.Settings.DownloadPath
            : DownloadDrawerPathBox.Text.Trim();
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true
        });
    }

    private void OnDownloadDrawerSettingModified(object sender, RoutedEventArgs e)
    {
        if (_isLoadingDownloadDrawer || !_isInitialized) return;
        _downloadDrawerModified = true;
    }

    private void OnDownloadDrawerConcurrentChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isLoadingDownloadDrawer || !_isInitialized) return;
        if (double.IsNaN(args.NewValue))
        {
            sender.Value = 3;
            return;
        }
        _downloadDrawerModified = true;
    }

    private void OnDownloadFormatPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string presetKey) return;

        string presetFormat = presetKey switch
        {
            "artist_song" => "{artistName} - {songName}",
            "song_only" => "{songName}",
            _ => "{songName} - {artistName}"
        };

        _isLoadingDownloadDrawer = true;
        ParseDownloadFormatString(presetFormat, null);
        DownloadSeparatorCustomBox.Text = _downloadSeparator;
        RefreshDownloadFormatDrawerUi();
        _isLoadingDownloadDrawer = false;
        _downloadDrawerModified = true;
    }

    private void OnDownloadSeparatorQuickClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string sep) return;

        _isLoadingDownloadDrawer = true;
        _downloadSeparator = sep;
        DownloadSeparatorCustomBox.Text = sep;
        RefreshDownloadFormatDrawerUi();
        _isLoadingDownloadDrawer = false;
        _downloadDrawerModified = true;
    }

    private void OnDownloadSeparatorCustomTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingDownloadDrawer || !_isInitialized) return;
        _downloadSeparator = string.IsNullOrEmpty(DownloadSeparatorCustomBox.Text)
            ? " - "
            : DownloadSeparatorCustomBox.Text;
        RefreshDownloadFormatDrawerUi();
        _downloadDrawerModified = true;
    }

    private void OnMoveFormatComponentUpClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int index || index <= 0 || index >= _downloadFormatComponents.Count) return;

        (_downloadFormatComponents[index - 1], _downloadFormatComponents[index]) =
            (_downloadFormatComponents[index], _downloadFormatComponents[index - 1]);
        RefreshDownloadFormatDrawerUi();
        _downloadDrawerModified = true;
    }

    private void OnMoveFormatComponentDownClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int index || index < 0 || index >= _downloadFormatComponents.Count - 1) return;

        (_downloadFormatComponents[index], _downloadFormatComponents[index + 1]) =
            (_downloadFormatComponents[index + 1], _downloadFormatComponents[index]);
        RefreshDownloadFormatDrawerUi();
        _downloadDrawerModified = true;
    }

    private void OnRemoveFormatComponentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string type) return;
        if (_downloadFormatComponents.Count <= 1) return;

        _downloadFormatComponents.Remove(type);
        RefreshDownloadFormatDrawerUi();
        _downloadDrawerModified = true;
    }

    private void OnAddFormatComponentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string type) return;
        if (_downloadFormatComponents.Contains(type)) return;

        _downloadFormatComponents.Add(type);
        RefreshDownloadFormatDrawerUi();
        _downloadDrawerModified = true;
    }

    private async void OnSaveDownloadSettingsDrawerClick(object sender, RoutedEventArgs e)
    {
        await SaveDownloadSettingsDrawerInternalAsync();
        await HideDownloadSettingsDrawerAnimatedAsync();
    }

    private async Task SaveDownloadSettingsDrawerInternalAsync()
    {
        var s = ViewModel.Config.Settings;
        bool pathChanged = !string.Equals(s.DownloadPath, DownloadDrawerPathBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(DownloadDrawerPathBox.Text))
        {
            s.DownloadPath = DownloadDrawerPathBox.Text.Trim();
            DownloadPathBox.Text = s.DownloadPath;
        }

        s.DownloadSaveLyricFile = DownloadDrawerSaveLyricToggle.IsOn;
        s.DownloadMaxConcurrent = Math.Clamp((int)Math.Round(DownloadDrawerConcurrentBox.Value), 1, 5);
        s.DownloadSeparator = _downloadSeparator;
        s.DownloadNameFormat = BuildCurrentDownloadNameFormat();

        await ViewModel.Config.SaveSettingsAsync();
        _downloadDrawerModified = false;

        if (pathChanged)
        {
            await ViewModel.Downloader.RefreshCompletedDownloadsAsync();
        }

        ViewModel.StatusToast = "下载设置已保存";
    }

    private void ParseDownloadFormatString(string formatStr, string? fallbackSeparator)
    {
        _downloadFormatComponents.Clear();
        var matches = System.Text.RegularExpressions.Regex.Matches(formatStr, @"\{(\w+)\}");
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var field = m.Groups[1].Value;
            if (field is "songName" or "artistName" or "albumName" && !_downloadFormatComponents.Contains(field))
            {
                _downloadFormatComponents.Add(field);
            }
        }

        if (_downloadFormatComponents.Count == 0)
        {
            _downloadFormatComponents.Add("songName");
            _downloadFormatComponents.Add("artistName");
        }

        if (matches.Count >= 2)
        {
            int endFirst = matches[0].Index + matches[0].Length;
            int startSecond = matches[1].Index;
            if (startSecond > endFirst)
            {
                _downloadSeparator = formatStr[endFirst..startSecond];
                return;
            }
        }

        _downloadSeparator = !string.IsNullOrEmpty(fallbackSeparator) ? fallbackSeparator : " - ";
    }

    private string BuildCurrentDownloadNameFormat()
    {
        if (_downloadFormatComponents.Count == 0)
        {
            return "{songName} - {artistName}";
        }
        return string.Join(_downloadSeparator, _downloadFormatComponents.Select(c => $"{{{c}}}"));
    }

    private void RefreshDownloadFormatDrawerUi()
    {
        var items = new List<DownloadFormatComponentItem>();
        for (int i = 0; i < _downloadFormatComponents.Count; i++)
        {
            items.Add(new DownloadFormatComponentItem
            {
                Type = _downloadFormatComponents[i],
                Index = i,
                CanMoveUp = i > 0,
                CanMoveDown = i < _downloadFormatComponents.Count - 1,
                CanRemove = _downloadFormatComponents.Count > 1
            });
        }
        DownloadFormatComponentsList.ItemsSource = items;

        AddFormatSongNameBtn.IsEnabled = !_downloadFormatComponents.Contains("songName");
        AddFormatArtistNameBtn.IsEnabled = !_downloadFormatComponents.Contains("artistName");
        AddFormatAlbumNameBtn.IsEnabled = !_downloadFormatComponents.Contains("albumName");

        string currentFormat = BuildCurrentDownloadNameFormat();

        ApplyDynamicAccentButtonState(DownloadFormatPreset0Btn, currentFormat == "{songName} - {artistName}");
        ApplyDynamicAccentButtonState(DownloadFormatPreset1Btn, currentFormat == "{artistName} - {songName}");
        ApplyDynamicAccentButtonState(DownloadFormatPreset2Btn, currentFormat == "{songName}");

        ApplyDynamicAccentButtonState(DownloadSepDashBtn, _downloadSeparator == " - ");
        ApplyDynamicAccentButtonState(DownloadSepUnderscoreBtn, _downloadSeparator == "_");
        ApplyDynamicAccentButtonState(DownloadSepSpaceBtn, _downloadSeparator == " ");

        DownloadFormatPreviewText.Text = currentFormat
            .Replace("{songName}", "莫失莫忘")
            .Replace("{artistName}", "香蜜沉沉烬如霜")
            .Replace("{albumName}", "电视剧原声带");
    }

    // ==================== 4 合 1 登录、UID 免密登录、外部歌单导入与听歌排行 ====================

    private async void OnGenerateQrLoginClick(object sender, RoutedEventArgs e)
    {
        _qrPollCts?.Cancel();
        _qrPollCts = new CancellationTokenSource();
        var ct = _qrPollCts.Token;

        var key = await ViewModel.Api.CreateQrKeyAsync();
        if (string.IsNullOrWhiteSpace(key))
        {
            ViewModel.LoginStatusText = "获取登录二维码 Key 失败，请检查网络连接";
            return;
        }

        var qrContent = $"https://music.163.com/login?codekey={key}";
        using var qrGen = new QRCodeGenerator();
        using var qrData = qrGen.CreateQrCode(qrContent, QRCodeGenerator.ECCLevel.M);
        using var pngQr = new PngByteQRCode(qrData);
        var pngBytes = pngQr.GetGraphic(10);

        using var ms = new MemoryStream(pngBytes);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(ms.AsRandomAccessStream());
        LoginQrImage.Source = bitmap;

        ViewModel.LoginStatusText = "请使用网易云音乐 App 扫描二维码登录...";

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(2200, ct);
            var (code, _, cookie) = await ViewModel.Api.CheckQrStatusAsync(key);
            if (code == 800)
            {
                ViewModel.LoginStatusText = "二维码已过期，请点击刷新";
                break;
            }
            if (code == 802)
            {
                ViewModel.LoginStatusText = "扫码成功，请在手机上点击确认授权...";
            }
            else if (code == 803 && !string.IsNullOrWhiteSpace(cookie))
            {
                ViewModel.Config.Settings.NeteaseCookie = cookie;
                ViewModel.Config.Settings.LoginType = "qr";
                CookieInputBox.Text = cookie;
                await ViewModel.Config.SaveSettingsAsync();
                await ViewModel.RefreshUserProfileAsync();
                break;
            }
        }
    }

    private async void OnUidLoginClick(object sender, RoutedEventArgs e)
    {
        if (long.TryParse(UidLoginInputBox.Text.Trim(), out var uid) && uid > 0)
        {
            await ViewModel.LoginByUidAsync(uid);
        }
        else
        {
            ViewModel.StatusToast = "请输入有效的网易云纯数字 UID";
        }
    }

    private async void OnSaveCookieLoginClick(object sender, RoutedEventArgs e)
    {
        ViewModel.Config.Settings.NeteaseCookie = CookieInputBox.Text.Trim();
        ViewModel.Config.Settings.LoginType = "cookie";
        await ViewModel.Config.SaveSettingsAsync();
        await ViewModel.RefreshUserProfileAsync();
    }

    private async void OnImportPlaylistClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ImportPlaylistByIdOrUrlAsync(ImportPlaylistInputBox.Text);
    }

    private async void OnUserPlayRecordTypeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out var type))
        {
            await ViewModel.LoadUserPlayRecordAsync(type);
        }
    }

    private async void OnUserPlayRecordRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is PlayRecordItem record)
        {
            e.Handled = true;
            var songs = ViewModel.UserPlayRecords.Select(r => r.Song).ToList();
            await ViewModel.Player.PlaySongAsync(record.Song, songs);
        }
    }

    private async void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        bool isQqTabVisible = string.Equals(_activeLoginProvider, "qq", StringComparison.OrdinalIgnoreCase);
        bool hasQqLogin = ViewModel.CurrentQqUser != null ||
                          !string.IsNullOrWhiteSpace(ViewModel.Config.Settings.QqMusicUin) ||
                          !string.IsNullOrWhiteSpace(ViewModel.Config.Settings.QqMusicCookie);
        bool hasNeteaseLogin = ViewModel.CurrentUser != null ||
                               ViewModel.Config.Settings.SavedUid > 0 ||
                               !string.IsNullOrWhiteSpace(ViewModel.Config.Settings.NeteaseCookie);

        if (isQqTabVisible)
        {
            await PerformQqLogoutWithUiResetAsync();
            return;
        }

        if (hasNeteaseLogin)
        {
            await PerformNeteaseLogoutWithUiResetAsync();
            return;
        }

        if (hasQqLogin)
        {
            await PerformQqLogoutWithUiResetAsync();
            return;
        }

        await PerformNeteaseLogoutWithUiResetAsync();
    }

    private async Task PerformNeteaseLogoutWithUiResetAsync()
    {
        _qrPollCts?.Cancel();
        CookieInputBox.Text = string.Empty;
        UidLoginInputBox.Text = string.Empty;
        LoginQrImage.Source = null;
        await ViewModel.LogoutUserAsync();
        BasicTokenStatusText.Text = ViewModel.LoginStatusText;
        SyncLoginPanelsVisibilityUi();
        SyncCookieSettingsRowUi();
    }

    private async Task PerformQqLogoutWithUiResetAsync()
    {
        _qrPollCts?.Cancel();
        _isHandlingQqOAuthCode = false;
        QqLoginQrImage.Source = null;
        QqPageAvatarImg.Source = null;
        QqManualPasswordInputBox.Password = string.Empty;

        try
        {
            if (QqQuickLoginWebView.CoreWebView2 != null)
            {
                QqQuickLoginWebView.CoreWebView2.CookieManager.DeleteAllCookies();
            }
        }
        catch
        {
            // 忽略清理内嵌 WebView2 Cookie 异常
        }

        await ViewModel.LogoutQqMusicAsync();

        QqPageNicknameText.Text = "QQ 音乐歌单中心";
        QqPageStatusText.Text = ViewModel.QqLoginStatusText;
        SyncLoginPanelsVisibilityUi();
        SyncCookieSettingsRowUi();

        if (string.Equals(_activeLoginProvider, "qq", StringComparison.OrdinalIgnoreCase))
        {
            _ = DetectAndPopulateLocalQqAccountsAsync(false);
        }
    }

    // ==================== QQ 音乐双平台登录与独立歌单中心事件 ====================

    private string _activeLoginProvider = "netease";
    private bool _isQqQuickLoginWebViewHooked;
    private bool _isHandlingQqOAuthCode;

    private void OnLoginProviderSwitchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string provider) return;
        SwitchLoginProviderUi(provider);
    }

    private void SwitchLoginProviderUi(string provider)
    {
        _activeLoginProvider = string.Equals(provider, "qq", StringComparison.OrdinalIgnoreCase) ? "qq" : "netease";
        bool isQq = _activeLoginProvider == "qq";

        ApplyDynamicAccentButtonState(LoginProviderNeteaseBtn, !isQq);
        ApplyDynamicAccentButtonState(LoginProviderQqBtn, isQq);

        SyncLoginPanelsVisibilityUi();

        if (_isInitialized)
        {
            var activePanel = isQq ? QqLoginProviderPanel : NeteaseLoginProviderPanel;
            if (activePanel.Visibility == Visibility.Visible)
            {
                PlayEntranceAnimation(activePanel, 0, 14, 240);
            }
        }

        if (isQq && ViewModel.CurrentQqUser == null)
        {
            _ = DetectAndPopulateLocalQqAccountsAsync(false);
        }
    }

    private void SyncLoginPanelsVisibilityUi()
    {
        bool isQq = string.Equals(_activeLoginProvider, "qq", StringComparison.OrdinalIgnoreCase);
        bool isNeteaseLoggedIn = ViewModel.CurrentUser != null;
        bool isQqLoggedIn = ViewModel.CurrentQqUser != null;

        // 顶部只保留唯一绿色状态文案
        UserLoginStatusText.Text = isQq ? ViewModel.QqLoginStatusText : ViewModel.LoginStatusText;

        // 已登录时隐藏下方登录入口面板，仅在平台切换栏右侧显示对应的「退出登录」按钮
        NeteaseLoginProviderPanel.Visibility = (!isQq && !isNeteaseLoggedIn) ? Visibility.Visible : Visibility.Collapsed;
        QqLoginProviderPanel.Visibility = (isQq && !isQqLoggedIn) ? Visibility.Visible : Visibility.Collapsed;

        NeteaseInlineLogoutBtn.Visibility = (!isQq && isNeteaseLoggedIn) ? Visibility.Visible : Visibility.Collapsed;
        QqInlineLogoutBtn.Visibility = (isQq && isQqLoggedIn) ? Visibility.Visible : Visibility.Collapsed;

        // 独立 QQ 音乐歌单中心页：已登录时隐藏登录入口按钮，显示退出按钮
        QqPageQuickLoginBtn.Visibility = isQqLoggedIn ? Visibility.Collapsed : Visibility.Visible;
        QqPageGoLoginBtn.Visibility = isQqLoggedIn ? Visibility.Collapsed : Visibility.Visible;
        QqPageLogoutBtn.Visibility = isQqLoggedIn ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SelectLocalQqAccount(LocalQqAccountItem? selected)
    {
        foreach (var acc in ViewModel.LocalQqAccounts)
        {
            acc.IsSelected = ReferenceEquals(acc, selected);
        }

        if (selected != null)
        {
            PanelConfirmLocalQqLoginBtn.Content = $"登录「{selected.DisplayNickname}」";
            ModalConfirmLocalQqLoginBtn.Content = $"授权并登录「{selected.DisplayNickname} ({selected.Uin})」";
            QqQuickLoginModalStatusText.Text = $"已选择账号：{selected.DisplayNickname} (QQ: {selected.Uin})，点击下方按钮确认登录";
        }
        else
        {
            PanelConfirmLocalQqLoginBtn.Content = "登录选中 QQ";
            ModalConfirmLocalQqLoginBtn.Content = "授权并登录选中 QQ 账号";
        }
    }

    private async Task ApplyQqMusicLoginSuccessAsync(string cookie, string uin, string nickname, string sourceLabel = "QQ 音乐登录成功")
    {
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            ViewModel.Config.Settings.QqMusicCookie = cookie;
        }
        if (!string.IsNullOrWhiteSpace(uin))
        {
            ViewModel.Config.Settings.QqMusicUin = uin;
        }
        if (!string.IsNullOrWhiteSpace(nickname))
        {
            ViewModel.Config.Settings.QqMusicNickname = nickname;
        }

        await ViewModel.Config.SaveSettingsAsync();
        await ViewModel.RefreshQqUserProfileAsync();
        QqQuickLoginModalOverlay.Visibility = Visibility.Collapsed;
        SyncLoginPanelsVisibilityUi();
        var displayName = ViewModel.CurrentQqUser?.Nickname ?? (!string.IsNullOrWhiteSpace(nickname) ? nickname : uin);
        ViewModel.StatusToast = $"{sourceLabel}：{displayName}";
    }

    private async Task<List<LocalQqAccountItem>> DetectAndPopulateLocalQqAccountsAsync(bool showToast)
    {
        var previousSelectedUin = ViewModel.LocalQqAccounts.FirstOrDefault(a => a.IsSelected)?.Uin
                                  ?? ViewModel.Config.Settings.QqMusicUin;

        QqQuickLoginModalStatusText.Text = "正在检测当前电脑已登录的 QQ 账号...";
        var accounts = await ViewModel.QqMusic.DetectLocalLoggedInQqAccountsAsync();

        ViewModel.LocalQqAccounts.Clear();
        foreach (var acc in accounts)
        {
            ViewModel.LocalQqAccounts.Add(acc);
        }

        if (accounts.Count > 0)
        {
            var defaultSelected = accounts.FirstOrDefault(a => string.Equals(a.Uin, previousSelectedUin, StringComparison.OrdinalIgnoreCase))
                                  ?? accounts[0];
            SelectLocalQqAccount(defaultSelected);

            LocalQqAccountsGridView.Visibility = Visibility.Visible;
            ModalLocalQqAccountsListView.Visibility = Visibility.Visible;
            ModalLocalQqEmptyPanel.Visibility = Visibility.Collapsed;

            QqQuickLoginModalStatusText.Text = $"已检测到 {accounts.Count} 个本机已登录 QQ 账号，请选择要使用的账号后点击下方「授权并登录」按钮";
            if (showToast)
            {
                ViewModel.StatusToast = $"检测到 {accounts.Count} 个已登录的本机 QQ 账号，请选择后确认登录";
            }
        }
        else
        {
            SelectLocalQqAccount(null);
            LocalQqAccountsGridView.Visibility = Visibility.Collapsed;
            ModalLocalQqAccountsListView.Visibility = Visibility.Collapsed;
            ModalLocalQqEmptyPanel.Visibility = Visibility.Visible;

            QqQuickLoginModalStatusText.Text = "暂未在本地快速端口检测到已登录的 QQ，可点击上方「内嵌网页快速授权」";
            if (showToast)
            {
                ViewModel.StatusToast = "未检测到本机 QQ 本地快速端口，可在登录器内使用内嵌网页授权";
            }
        }

        return accounts;
    }

    private async void OnDetectLocalQqAccountsClick(object sender, RoutedEventArgs e)
    {
        await DetectAndPopulateLocalQqAccountsAsync(true);
    }

    private void OnLocalQqAccountItemSelectClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LocalQqAccountItem account) return;
        SelectLocalQqAccount(account);
    }

    private async void OnConfirmSelectedLocalQqLoginClick(object sender, RoutedEventArgs e)
    {
        var selected = ViewModel.LocalQqAccounts.FirstOrDefault(a => a.IsSelected)
                       ?? ViewModel.LocalQqAccounts.FirstOrDefault();

        if (selected == null)
        {
            var accounts = await DetectAndPopulateLocalQqAccountsAsync(false);
            selected = accounts.FirstOrDefault(a => a.IsSelected) ?? accounts.FirstOrDefault();
        }

        if (selected == null)
        {
            await OpenQqQuickLoginLauncherModalAsync("web");
            return;
        }

        await LoginWithLocalQqAccountInternalAsync(selected);
    }

    private async void OnQqClientQuickLoginClick(object sender, RoutedEventArgs e)
    {
        await OpenQqQuickLoginLauncherModalAsync("local");
    }

    private async void OnOpenQqPasswordLauncherClick(object sender, RoutedEventArgs e)
    {
        await OpenQqQuickLoginLauncherModalAsync("password");
    }

    private async void OnQqManualPasswordInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await ExecuteQqManualPasswordLoginAsync();
        }
    }

    private async void OnSubmitQqManualPasswordLoginClick(object sender, RoutedEventArgs e)
    {
        await ExecuteQqManualPasswordLoginAsync();
    }

    private async Task ExecuteQqManualPasswordLoginAsync()
    {
        var uin = QqManualUinInputBox.Text.Trim();
        var password = QqManualPasswordInputBox.Password;

        if (string.IsNullOrWhiteSpace(uin))
        {
            ViewModel.StatusToast = "请输入 QQ 号码或邮箱账号";
            return;
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            ViewModel.StatusToast = "请输入 QQ 登录密码";
            return;
        }

        SubmitQqManualPasswordLoginBtn.IsEnabled = false;
        SubmitQqManualPasswordLoginBtn.Content = "正在验证登录...";
        UserLoginStatusText.Text = $"正在验证 QQ 账号 ({uin}) 密码登录...";

        try
        {
            await LoadEmbeddedQqQuickLoginWebViewAsync(switchToPasswordMode: true, autoFillUin: uin, autoFillPassword: password);

            // 等待 PTLogin2 校验密码或弹出滑块验证码
            for (int i = 0; i < 6; i++)
            {
                await Task.Delay(450);
                if (ViewModel.CurrentQqUser != null || _isHandlingQqOAuthCode)
                {
                    QqManualPasswordInputBox.Password = string.Empty;
                    return;
                }

                if (QqQuickLoginWebView.CoreWebView2 != null)
                {
                    var stateJson = await QqQuickLoginWebView.CoreWebView2.ExecuteScriptAsync(@"
                        (function() {
                            var err = document.getElementById('err_m');
                            var errText = (err && err.innerText) ? err.innerText.trim() : '';
                            var vcode = document.getElementById('newVcodeArea');
                            var needSlider = vcode && (vcode.style.display !== 'none' && vcode.offsetHeight > 0);
                            return JSON.stringify({ err: errText, slider: !!needSlider });
                        })()
                    ");

                    if (!string.IsNullOrWhiteSpace(stateJson) && stateJson != "null")
                    {
                        var unescaped = System.Text.Json.JsonSerializer.Deserialize<string>(stateJson);
                        if (!string.IsNullOrWhiteSpace(unescaped))
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(unescaped);
                            var errText = doc.RootElement.TryGetProperty("err", out var errProp) ? errProp.GetString() : string.Empty;
                            bool needSlider = doc.RootElement.TryGetProperty("slider", out var slProp) && slProp.GetBoolean();

                            if (needSlider)
                            {
                                UserLoginStatusText.Text = "请在弹出的登录器内拖动滑块完成安全验证";
                                QqQuickLoginModalStatusText.Text = "已为您填入账号密码，请拖动下方滑块完成安全验证（验证通过后将自动登录）";
                                ApplyDynamicAccentButtonState(QqLauncherLocalTabBtn, false);
                                ApplyDynamicAccentButtonState(QqLauncherPasswordTabBtn, true);
                                ApplyDynamicAccentButtonState(QqLauncherWebTabBtn, false);
                                QqLauncherLocalPanel.Visibility = Visibility.Collapsed;
                                QqLauncherWebPanel.Visibility = Visibility.Visible;
                                QqQuickLoginModalOverlay.Visibility = Visibility.Visible;
                                return;
                            }

                            if (!string.IsNullOrWhiteSpace(errText))
                            {
                                UserLoginStatusText.Text = errText;
                                ViewModel.StatusToast = errText;
                                return;
                            }
                        }
                    }
                }
            }

            // 若需要二次确认或滑块验证，展示登录器密码面板供用户完成交互
            if (ViewModel.CurrentQqUser == null)
            {
                QqQuickLoginModalStatusText.Text = "请在下方确认登录或完成安全验证，完成后将自动同步歌单";
                ApplyDynamicAccentButtonState(QqLauncherLocalTabBtn, false);
                ApplyDynamicAccentButtonState(QqLauncherPasswordTabBtn, true);
                ApplyDynamicAccentButtonState(QqLauncherWebTabBtn, false);
                QqLauncherLocalPanel.Visibility = Visibility.Collapsed;
                QqLauncherWebPanel.Visibility = Visibility.Visible;
                QqQuickLoginModalOverlay.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            UserLoginStatusText.Text = $"QQ 密码登录异常：{ex.Message}";
        }
        finally
        {
            SubmitQqManualPasswordLoginBtn.IsEnabled = true;
            SubmitQqManualPasswordLoginBtn.Content = "登录 QQ 音乐";
        }
    }

    private async void OnQqLauncherTabSwitchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tab) return;
        await SwitchQqLauncherTabAsync(tab);
    }

    private async Task SwitchQqLauncherTabAsync(string tab)
    {
        bool isLocal = string.Equals(tab, "local", StringComparison.OrdinalIgnoreCase);
        bool isPassword = string.Equals(tab, "password", StringComparison.OrdinalIgnoreCase);
        bool isWeb = string.Equals(tab, "web", StringComparison.OrdinalIgnoreCase);

        QqLauncherLocalPanel.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
        QqLauncherWebPanel.Visibility = isLocal ? Visibility.Collapsed : Visibility.Visible;

        ApplyDynamicAccentButtonState(QqLauncherLocalTabBtn, isLocal);
        ApplyDynamicAccentButtonState(QqLauncherPasswordTabBtn, isPassword);
        ApplyDynamicAccentButtonState(QqLauncherWebTabBtn, isWeb);

        if (isLocal)
        {
            var selected = ViewModel.LocalQqAccounts.FirstOrDefault(a => a.IsSelected);
            QqQuickLoginModalStatusText.Text = selected != null
                ? $"已选择账号：{selected.DisplayNickname} (QQ: {selected.Uin})，点击下方按钮确认登录"
                : "请选择要用于登录 QQ 音乐的本机已登录 QQ 账号，选好后点击下方按钮确认登录";
        }
        else if (isPassword)
        {
            var prefillUin = QqManualUinInputBox?.Text?.Trim();
            var prefillPwd = QqManualPasswordInputBox?.Password;
            await LoadEmbeddedQqQuickLoginWebViewAsync(switchToPasswordMode: true, autoFillUin: prefillUin, autoFillPassword: prefillPwd);
        }
        else
        {
            await LoadEmbeddedQqQuickLoginWebViewAsync(switchToPasswordMode: false);
        }
    }

    private async Task OpenQqQuickLoginLauncherModalAsync(string initialTab = "local")
    {
        _isHandlingQqOAuthCode = false;
        QqQuickLoginModalOverlay.Visibility = Visibility.Visible;

        var accounts = await DetectAndPopulateLocalQqAccountsAsync(false);
        if (string.Equals(initialTab, "password", StringComparison.OrdinalIgnoreCase))
        {
            await SwitchQqLauncherTabAsync("password");
        }
        else if (string.Equals(initialTab, "web", StringComparison.OrdinalIgnoreCase) || accounts.Count == 0)
        {
            await SwitchQqLauncherTabAsync("web");
        }
        else
        {
            await SwitchQqLauncherTabAsync("local");
        }
    }

    private async Task LoginWithLocalQqAccountInternalAsync(LocalQqAccountItem account)
    {
        QqQuickLoginModalStatusText.Text = $"正在调用所选本机 QQ ({account.DisplayNickname}) 授权登录...";
        ViewModel.StatusToast = $"正在使用所选 QQ ({account.DisplayNickname}) 登录...";

        var (success, _, cookie, uin, nickname) = await ViewModel.QqMusic.LoginWithLocalQqAccountAsync(account);
        if (success && (!string.IsNullOrWhiteSpace(uin) || !string.IsNullOrWhiteSpace(cookie)))
        {
            await ApplyQqMusicLoginSuccessAsync(cookie, uin, nickname, "已调用本机 QQ 登录成功");
            return;
        }

        // 若本机 QQ 客户端需要内嵌授权页二次确认，则切换到模态框内的 WebView2 快速授权页
        QqQuickLoginModalOverlay.Visibility = Visibility.Visible;
        await SwitchQqLauncherTabAsync("web");
    }

    private bool _pendingPtLoginPasswordMode;
    private string? _pendingPtLoginUin;
    private string? _pendingPtLoginPassword;

    private async Task LoadEmbeddedQqQuickLoginWebViewAsync(
        bool switchToPasswordMode = false,
        string? autoFillUin = null,
        string? autoFillPassword = null)
    {
        _isHandlingQqOAuthCode = false;
        _pendingPtLoginPasswordMode = switchToPasswordMode;
        _pendingPtLoginUin = autoFillUin;
        _pendingPtLoginPassword = autoFillPassword;

        QqQuickLoginModalStatusText.Text = switchToPasswordMode
            ? "正在加载 QQ 账号密码登录组件，输入 QQ 账号与密码即可直接登录..."
            : "正在加载本机 QQ 快速登录组件，点击下方想要登录的 QQ 头像即可完成授权...";

        try
        {
            await App.EnsureWebView2Async(QqQuickLoginWebView);
            var coreWv = QqQuickLoginWebView.CoreWebView2;
            if (coreWv == null) return;

            if (!_isQqQuickLoginWebViewHooked)
            {
                _isQqQuickLoginWebViewHooked = true;
                coreWv.Settings.AreDefaultContextMenusEnabled = false;
                coreWv.Settings.AreDevToolsEnabled = false;

                coreWv.NewWindowRequested += (s, args) =>
                {
                    args.Handled = true;
                    if (!string.IsNullOrWhiteSpace(args.Uri))
                    {
                        coreWv.Navigate(args.Uri);
                    }
                };

                coreWv.NavigationStarting += async (s, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Uri))
                    {
                        await TryInterceptQqQuickLoginRedirectUrlAsync(args.Uri);
                    }
                };

                coreWv.SourceChanged += async (s, args) =>
                {
                    var currentUri = coreWv.Source;
                    if (!string.IsNullOrWhiteSpace(currentUri))
                    {
                        await TryInterceptQqQuickLoginRedirectUrlAsync(currentUri);
                    }
                };

                coreWv.NavigationCompleted += async (s, args) =>
                {
                    var currentUri = coreWv.Source;
                    if (!string.IsNullOrWhiteSpace(currentUri))
                    {
                        await TryInterceptQqQuickLoginRedirectUrlAsync(currentUri);
                    }

                    if (_pendingPtLoginPasswordMode &&
                        currentUri != null &&
                        currentUri.Contains("xui.ptlogin2.qq.com", StringComparison.OrdinalIgnoreCase))
                    {
                        await ApplyPtLoginPasswordModeScriptAsync(coreWv, _pendingPtLoginUin, _pendingPtLoginPassword);
                    }
                };
            }

            if (switchToPasswordMode)
            {
                const string ptLoginUrl = "https://xui.ptlogin2.qq.com/cgi-bin/xlogin?appid=716027609&daid=383&style=33&login_text=%E7%99%BB%E5%BD%95&hide_title_bar=1&hide_border=1&target=self&s_url=https%3A%2F%2Fgraph.qq.com%2Foauth2.0%2Flogin_jump&pt_3rd_aid=100497308&pt_feedback_link=https%3A%2F%2Fsupport.qq.com%2Fproducts%2F77942%3FcustomInfo%3D.appid100497308&theme=2&verify_theme=";
                var tcs = new TaskCompletionSource<bool>();
                void OnNavDone(object? s, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
                {
                    coreWv.NavigationCompleted -= OnNavDone;
                    tcs.TrySetResult(true);
                }
                coreWv.NavigationCompleted += OnNavDone;
                coreWv.Navigate(ptLoginUrl);
                await Task.WhenAny(tcs.Task, Task.Delay(3500));
                await ApplyPtLoginPasswordModeScriptAsync(coreWv, autoFillUin, autoFillPassword);
            }
            else
            {
                const string oauthUrl = "https://graph.qq.com/oauth2.0/show?which=Login&display=pc&response_type=code&client_id=100497308&redirect_uri=https%3A%2F%2Fy.qq.com%2Fportal%2Fwx_redirect.html%3Flogin_type%3D1%26surl%3Dhttps%3A%2F%2Fy.qq.com%2F&state=state";
                coreWv.Navigate(oauthUrl);
            }
        }
        catch (Exception ex)
        {
            QqQuickLoginModalStatusText.Text = $"初始化客户端内登录组件失败：{ex.Message}";
        }
    }

    private static async Task ApplyPtLoginPasswordModeScriptAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 coreWv,
        string? uin,
        string? password)
    {
        try
        {
            var uinJson = System.Text.Json.JsonSerializer.Serialize(uin ?? string.Empty);
            var pwdJson = System.Text.Json.JsonSerializer.Serialize(password ?? string.Empty);

            await coreWv.ExecuteScriptAsync($@"
                (function() {{
                    var sw = document.getElementById('switcher_plogin');
                    if (sw) sw.click();
                    var uinVal = {uinJson};
                    var pwdVal = {pwdJson};
                    var u = document.getElementById('u');
                    var p = document.getElementById('p');
                    if (u && uinVal) {{
                        u.focus();
                        u.value = uinVal;
                        u.dispatchEvent(new Event('input', {{ bubbles: true }}));
                        u.dispatchEvent(new Event('change', {{ bubbles: true }}));
                    }}
                    if (p && pwdVal) {{
                        p.focus();
                        p.value = pwdVal;
                        p.dispatchEvent(new Event('input', {{ bubbles: true }}));
                        p.dispatchEvent(new Event('change', {{ bubbles: true }}));
                    }}
                    if (u && p && uinVal && pwdVal) {{
                        var btn = document.getElementById('login_button');
                        if (btn) btn.click();
                    }}
                }})();
            ");
        }
        catch
        {
            // 忽略脚本注入异常
        }
    }

    private async Task TryInterceptQqQuickLoginRedirectUrlAsync(string url)
    {
        if (_isHandlingQqOAuthCode || string.IsNullOrWhiteSpace(url)) return;

        var coreWv = QqQuickLoginWebView.CoreWebView2;
        if (coreWv == null) return;

        // 当 xui.ptlogin2.qq.com 账号密码登录成功跳转到 graph.qq.com/oauth2.0/login_jump 时，自动接力跳转到 OAuth 授权地址换取 code
        if (url.Contains("graph.qq.com/oauth2.0/login_jump", StringComparison.OrdinalIgnoreCase))
        {
            const string oauthUrl = "https://graph.qq.com/oauth2.0/authorize?response_type=code&client_id=100497308&redirect_uri=https%3A%2F%2Fy.qq.com%2Fportal%2Fwx_redirect.html%3Flogin_type%3D1%26surl%3Dhttps%3A%2F%2Fy.qq.com%2F&state=state";
            coreWv.Navigate(oauthUrl);
            return;
        }

        var codeMatch = System.Text.RegularExpressions.Regex.Match(url, @"[?&]code=([^&#]+)");
        bool isYqqDomain = url.Contains("y.qq.com", StringComparison.OrdinalIgnoreCase);

        if (!codeMatch.Success && !isYqqDomain) return;

        try
        {
            var cookiePairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var yqqCookies = await coreWv.CookieManager.GetCookiesAsync("https://y.qq.com");
            foreach (var c in yqqCookies)
            {
                if (!string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Value))
                {
                    cookiePairs[c.Name] = c.Value;
                }
            }
            var uYqqCookies = await coreWv.CookieManager.GetCookiesAsync("https://u.y.qq.com");
            foreach (var c in uYqqCookies)
            {
                if (!string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Value))
                {
                    cookiePairs[c.Name] = c.Value;
                }
            }
            var graphCookies = await coreWv.CookieManager.GetCookiesAsync("https://graph.qq.com");
            foreach (var c in graphCookies)
            {
                if (!string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Value) && !cookiePairs.ContainsKey(c.Name))
                {
                    cookiePairs[c.Name] = c.Value;
                }
            }

            string combinedBrowserCookie = string.Join("; ", cookiePairs.Select(kv => $"{kv.Key}={kv.Value}"));

            if (codeMatch.Success)
            {
                _isHandlingQqOAuthCode = true;
                var code = Uri.UnescapeDataString(codeMatch.Groups[1].Value);
                QqQuickLoginModalStatusText.Text = "已获取本机 QQ 授权码，正在同步 QQ 音乐登录态...";

                var (success, _, exchangedCookie, uin, nickname) = await ViewModel.QqMusic.ExchangeOAuthCodeForQqMusicCookieAsync(code, combinedBrowserCookie);
                if (success && (!string.IsNullOrWhiteSpace(uin) || !string.IsNullOrWhiteSpace(exchangedCookie)))
                {
                    QqQuickLoginModalOverlay.Visibility = Visibility.Collapsed;
                    await ApplyQqMusicLoginSuccessAsync(exchangedCookie, uin, nickname, "客户端内 QQ 快捷登录成功");
                    return;
                }
                _isHandlingQqOAuthCode = false;
            }

            // 若 WebView2 已完成 wx_redirect 并在 y.qq.com 写入了 qm_keyst / qqmusic_key
            if ((cookiePairs.ContainsKey("qm_keyst") || cookiePairs.ContainsKey("qqmusic_key")) &&
                !string.IsNullOrWhiteSpace(Services.Api.QqMusicService.ExtractUinFromCookieOrInput(combinedBrowserCookie)))
            {
                _isHandlingQqOAuthCode = true;
                var uin = Services.Api.QqMusicService.ExtractUinFromCookieOrInput(combinedBrowserCookie);
                QqQuickLoginModalOverlay.Visibility = Visibility.Collapsed;
                await ApplyQqMusicLoginSuccessAsync(combinedBrowserCookie, uin, $"QQ用户 ({uin})", "客户端内 QQ 快捷登录成功");
            }
        }
        catch
        {
            _isHandlingQqOAuthCode = false;
        }
    }

    private void OnCloseQqQuickLoginModalClick(object sender, RoutedEventArgs e)
    {
        QqQuickLoginModalOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnQqQuickLoginModalBackdropTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, QqQuickLoginModalOverlay))
        {
            QqQuickLoginModalOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void OnModalCardInnerTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private async void OnGenerateQqQrLoginClick(object sender, RoutedEventArgs e)
    {
        _qrPollCts?.Cancel();
        _qrPollCts = new CancellationTokenSource();
        var ct = _qrPollCts.Token;

        UserLoginStatusText.Text = "正在获取手机 QQ 安全登录二维码...";
        var (pngBytes, qrsig) = await ViewModel.QqMusic.CreateQrCodeAsync();
        if (pngBytes == null || pngBytes.Length == 0 || string.IsNullOrWhiteSpace(qrsig))
        {
            UserLoginStatusText.Text = "获取手机 QQ 二维码失败，请检查网络连接";
            return;
        }

        using (var ms = new MemoryStream(pngBytes))
        {
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(ms.AsRandomAccessStream());
            QqLoginQrImage.Source = bitmap;
        }

        UserLoginStatusText.Text = "请使用手机 QQ 扫描下方二维码授权登录...";

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(2200, ct);
            var (statusCode, message, cookie, uin, nickname) = await ViewModel.QqMusic.CheckQrStatusAsync(qrsig);
            UserLoginStatusText.Text = message;

            if (statusCode == 800)
            {
                break;
            }
            if (statusCode == 803 && (!string.IsNullOrWhiteSpace(uin) || !string.IsNullOrWhiteSpace(cookie)))
            {
                await ApplyQqMusicLoginSuccessAsync(cookie, uin, nickname, "QQ 音乐扫码登录成功");
                break;
            }
        }
    }

    private void OnQqMusicPlaylistsNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item || item.Tag is not string tab) return;
        QqCreatedPlaylistsGrid.Visibility = tab == "Created" ? Visibility.Visible : Visibility.Collapsed;
        QqCollectedPlaylistsGrid.Visibility = tab == "Collected" ? Visibility.Visible : Visibility.Collapsed;
        PlayEntranceAnimation(tab == "Created" ? QqCreatedPlaylistsGrid : QqCollectedPlaylistsGrid, 0, 14, 240);
    }

    private async void OnGoToQqLoginClick(object sender, RoutedEventArgs e)
    {
        await NavigateToSectionAsync("User");
        SwitchLoginProviderUi("qq");
    }

    private async void OnRefreshQqPlaylistsClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshQqUserProfileAsync();
    }

    private async void OnLogoutQqMusicClick(object sender, RoutedEventArgs e)
    {
        await PerformQqLogoutWithUiResetAsync();
    }

    private async void OnLogoutAllAccountsClick(object sender, RoutedEventArgs e)
    {
        await PerformNeteaseLogoutWithUiResetAsync();
        await PerformQqLogoutWithUiResetAsync();
        ViewModel.StatusToast = "已退出全部音乐平台账号";
    }

    // ==================== 底部播放控制栏、倍速、10段独立EQ滑块与全屏沉浸歌词控制 ====================

    private void OnPlayPauseButtonClick(object sender, RoutedEventArgs e) => ViewModel.Player.TogglePlayPause();

    private void OnStopButtonClick(object sender, RoutedEventArgs e) => ViewModel.Player.StopPlayback();

    private async void OnPrevButtonClick(object sender, RoutedEventArgs e) => await ViewModel.Player.PreviousAsync();

    private async void OnNextButtonClick(object sender, RoutedEventArgs e) => await ViewModel.Player.NextAsync();

    private async void OnToggleCurrentSongLikeClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Player.CurrentSong == null) return;
        await ToggleSongFavoriteWithCloudSyncAsync(ViewModel.Player.CurrentSong);
    }

    private void SyncPlayModeIcons(PlayMode mode)
    {
        (string glyph, string label) = mode switch
        {
            PlayMode.LoopOne => ("\uE8ED", "单曲循环"),
            PlayMode.Random => ("\uE8B1", "随机播放"),
            PlayMode.Sequence => ("\uE8AB", "顺序播放"),
            PlayMode.Intelligence => ("\uEB52", "心动模式"),
            _ => ("\uE8EE", "列表循环")
        };
        PlayModeIcon.Glyph = glyph;
        PlayModeText.Text = label;
        SimplePlayBarModeIcon.Glyph = glyph;
        ToolTipService.SetToolTip(SimplePlayBarModeBtn, $"当前模式：{label}（点击切换）");
    }

    private async void OnCyclePlayModeClick(object sender, RoutedEventArgs e)
    {
        var nextMode = ViewModel.Config.Settings.PlayMode switch
        {
            PlayMode.LoopAll => PlayMode.LoopOne,
            PlayMode.LoopOne => PlayMode.Random,
            PlayMode.Random => PlayMode.Sequence,
            PlayMode.Sequence => PlayMode.Intelligence,
            _ => PlayMode.LoopAll
        };
        ViewModel.Config.Settings.PlayMode = nextMode;
        SyncPlayModeIcons(nextMode);
        await ViewModel.Config.SaveSettingsAsync();

        if (nextMode == PlayMode.Intelligence)
        {
            await ViewModel.Player.StartIntelligenceModeAsync();
        }
    }

    private void OnSeekSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isInitialized || _isInternalSeekUpdate) return;
        if (Math.Abs(e.NewValue - ViewModel.PositionSeconds) > 1.5)
        {
            ViewModel.Player.Seek(TimeSpan.FromSeconds(e.NewValue));
        }
    }

    private void OnVolumeSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isInitialized || _isInternalVolumeUpdate) return;
        float vol = (float)Math.Clamp(e.NewValue / 100.0, 0.0, 1.0);
        if (vol > 0.01f)
        {
            _volumeBeforeMute = vol;
        }
        ViewModel.Player.SetVolume(vol);

        _isInternalVolumeUpdate = true;
        if (!ReferenceEquals(sender, VolumeSlider))
        {
            VolumeSlider.Value = Math.Round(e.NewValue);
        }
        if (!ReferenceEquals(sender, SimplePlayBarVolumeSlider))
        {
            SimplePlayBarVolumeSlider.Value = Math.Round(e.NewValue);
        }
        _isInternalVolumeUpdate = false;
        UpdateVolumeMuteGlyph(vol);
    }

    private void UpdateVolumeMuteGlyph(float volume)
    {
        string glyph = volume <= 0.005f ? "\uE74F" : "\uE767";
        SimplePlayBarVolumeIcon.Glyph = glyph;
        if (BottomVolumeIcon != null)
        {
            BottomVolumeIcon.Glyph = glyph;
        }
    }

    private void OnSimplePlayBarMuteClick(object sender, RoutedEventArgs e)
    {
        float current = ViewModel.Config.Settings.Volume;
        float next = current > 0.005f ? 0f : Math.Clamp(_volumeBeforeMute <= 0.01f ? 0.6f : _volumeBeforeMute, 0.1f, 1.0f);
        if (current > 0.005f)
        {
            _volumeBeforeMute = current;
        }
        ViewModel.Player.SetVolume(next);
        _isInternalVolumeUpdate = true;
        VolumeSlider.Value = Math.Round(next * 100);
        SimplePlayBarVolumeSlider.Value = Math.Round(next * 100);
        _isInternalVolumeUpdate = false;
        UpdateVolumeMuteGlyph(next);
    }

    private void OnBottomPlayBarVolumeWheel(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(sender as UIElement).Properties.MouseWheelDelta;
        if (delta == 0) return;
        double step = delta > 0 ? 5 : -5;
        double target = Math.Clamp(VolumeSlider.Value + step, 0, 100);
        VolumeSlider.Value = target;
        e.Handled = true;
    }

    private void OnSimplePlayBarVolumeWheel(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(sender as UIElement).Properties.MouseWheelDelta;
        if (delta == 0) return;
        double step = delta > 0 ? 5 : -5;
        double target = Math.Clamp(SimplePlayBarVolumeSlider.Value + step, 0, 100);
        SimplePlayBarVolumeSlider.Value = target;
        e.Handled = true;
    }

    private void OnPlaybackRateMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && float.TryParse(item.Tag?.ToString(), out var rate))
        {
            ViewModel.Player.SetPlaybackRate(rate);
            BottomRateBadgeText.Text = $"{rate:0.0#}x";
        }
    }

    private void OnSleepTimerButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out var minutes))
        {
            ViewModel.Player.SetSleepTimer(minutes);
        }
    }

    private async void OnSwitchAudioSourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string platform && ViewModel.Player.CurrentSong != null)
        {
            ViewModel.StatusToast = $"正在切换《{ViewModel.Player.CurrentSong.Name}》音源为 [{platform.ToUpperInvariant()}]...";
            await ViewModel.Player.PlaySongAsync(ViewModel.Player.CurrentSong, null, platform);
        }
    }

    private async void OnPlayBarQualityMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string quality && !string.IsNullOrWhiteSpace(quality))
        {
            SelectComboItemByTag(QualityComboBox, quality);
            await ViewModel.Player.SwitchQualityLevelAsync(quality);
        }
    }

    private async void OnEqEnableToggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        var eq = ViewModel.Config.Settings.Equalizer;
        eq.IsEnabled = EqEnableToggle.IsOn;
        Slider[] sliders = [EqBand0, EqBand1, EqBand2, EqBand3, EqBand4, EqBand5, EqBand6, EqBand7, EqBand8, EqBand9];
        foreach (var slider in sliders)
        {
            slider.IsEnabled = eq.IsEnabled;
        }
        ViewModel.Player.UpdateEqualizer(eq);
        await ViewModel.Config.SaveSettingsAsync();
    }

    private async void OnEqPresetTagClick(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || sender is not Button btn || btn.Tag is not string presetKey) return;
        if (EqualizerConfig.Presets.TryGetValue(presetKey, out var preset))
        {
            var eq = ViewModel.Config.Settings.Equalizer;
            if (!EqEnableToggle.IsOn)
            {
                EqEnableToggle.IsOn = true;
                eq.IsEnabled = true;
            }
            eq.ActivePresetName = presetKey;
            eq.BandGains = preset.Gains.ToArray();
            SyncEqSlidersFromConfig();
            ViewModel.Player.UpdateEqualizer(eq);
            await ViewModel.Config.SaveSettingsAsync();
        }
    }

    private void OnEqBandSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isInitialized || _isInternalEqUpdate) return;
        if (sender is Slider slider && int.TryParse(slider.Tag?.ToString(), out var bandIdx))
        {
            double val = Math.Round(e.NewValue, 1);
            TextBlock[] labels = [EqBandVal0, EqBandVal1, EqBandVal2, EqBandVal3, EqBandVal4, EqBandVal5, EqBandVal6, EqBandVal7, EqBandVal8, EqBandVal9];
            if (bandIdx >= 0 && bandIdx < labels.Length)
            {
                labels[bandIdx].Text = $"{val:0.#}dB";
            }

            var eq = ViewModel.Config.Settings.Equalizer;
            if (bandIdx >= 0 && bandIdx < eq.BandGains.Length)
            {
                eq.BandGains[bandIdx] = (float)val;
                string? matchedPreset = null;
                foreach (var (key, preset) in EqualizerConfig.Presets)
                {
                    if (preset.Gains.Length == eq.BandGains.Length &&
                        preset.Gains.Zip(eq.BandGains, (a, b) => Math.Abs(a - b) <= 0.1f).All(x => x))
                    {
                        matchedPreset = key;
                        break;
                    }
                }
                eq.ActivePresetName = matchedPreset ?? "custom";
                UpdateEqPresetTagButtonsUi(eq.ActivePresetName);
                ViewModel.Player.UpdateEqualizer(eq);
                _ = ViewModel.Config.SaveSettingsAsync();
            }
        }
    }

    private void OnEqBandPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && int.TryParse(fe.Tag?.ToString(), out var bandIdx))
        {
            var delta = e.GetCurrentPoint(fe).Properties.MouseWheelDelta;
            if (delta == 0) return;

            Slider[] sliders = [EqBand0, EqBand1, EqBand2, EqBand3, EqBand4, EqBand5, EqBand6, EqBand7, EqBand8, EqBand9];
            if (bandIdx >= 0 && bandIdx < sliders.Length)
            {
                if (!EqEnableToggle.IsOn)
                {
                    EqEnableToggle.IsOn = true;
                }
                double step = delta > 0 ? 0.5 : -0.5;
                sliders[bandIdx].Value = Math.Clamp(Math.Round((sliders[bandIdx].Value + step) * 10.0) / 10.0, -12.0, 12.0);
                e.Handled = true;
            }
        }
    }

    // ==================== 播放详情页 (1:1 还原 MusicFull.vue + Cover3D + SimplePlayBar + LyricSettings + LyricCorrectionControl) ====================

    private async void OnToggleImmersiveLyricClick(object sender, RoutedEventArgs e)
    {
        if (ImmersiveLyricOverlay.Visibility == Visibility.Visible)
        {
            await CloseMusicFullOverlayAsync(animate: true);
        }
        else
        {
            BottomCoverHoverArrowIcon.Glyph = "\uE70D";
            BottomCoverHoverArrowText.Text = "收起";
            SyncLyricLinesTypographyAndEmptyState();
            ImmersiveLyricOverlay.Visibility = Visibility.Visible;
            UpdatePlayBarsVisibilityForMusicFull(isOverlayOpen: true);
            ApplyMusicFullConfiguration();
            PlayEntranceAnimation(ImmersiveLyricOverlay, 0, 88, 300);
            ScrollToActiveLyricLine(force: true);
        }
    }

    private async Task CloseMusicFullOverlayAsync(bool animate)
    {
        if (ImmersiveLyricOverlay.Visibility != Visibility.Visible)
        {
            UpdatePlayBarsVisibilityForMusicFull(isOverlayOpen: false);
            return;
        }

        if (_appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            AppTitleBar.Visibility = Visibility.Visible;
            MusicFullFullscreenIcon.Glyph = "\uE740";
        }

        BottomCoverHoverArrowIcon.Glyph = "\uE70E";
        BottomCoverHoverArrowText.Text = "展开";
        UpdatePlayBarsVisibilityForMusicFull(isOverlayOpen: false);

        if (animate)
        {
            await PlayExitAnimationAsync(ImmersiveLyricOverlay, 0, 88, 220);
        }
        ImmersiveLyricOverlay.Visibility = Visibility.Collapsed;
        ConfigureTitleBarButtonsTheme();
        ScheduleUpdateTitleBarPassthroughRegions();
    }

    private void UpdatePlayBarsVisibilityForMusicFull(bool isOverlayOpen)
    {
        var cfg = ViewModel.Config.Settings.ImmersiveLyric;
        Grid.SetRow(ImmersiveLyricOverlay, 0);
        if (!isOverlayOpen)
        {
            BottomPlayBarBorder.Visibility = Visibility.Visible;
            Grid.SetRowSpan(ImmersiveLyricOverlay, 3);
            try
            {
                SetTitleBar(TitleBarDragRegion);
            }
            catch
            {
                // 忽略标题栏热切异常
            }
            ScheduleUpdateTitleBarPassthroughRegions();
            return;
        }

        bool showMiniPlayBar = !cfg.HideMiniPlayBar;
        MusicFullSimplePlayBar.Visibility = showMiniPlayBar ? Visibility.Visible : Visibility.Collapsed;
        BottomPlayBarBorder.Visibility = showMiniPlayBar ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRowSpan(ImmersiveLyricOverlay, showMiniPlayBar ? 3 : 2);
        try
        {
            SetTitleBar(ImmersiveTitleDragBar);
        }
        catch
        {
            // 忽略标题栏热切异常
        }
        ScheduleUpdateTitleBarPassthroughRegions();
    }

    private void OnBottomCoverPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        BottomCoverHoverArrowIcon.Glyph = ImmersiveLyricOverlay.Visibility == Visibility.Visible ? "\uE70D" : "\uE70E";
        BottomCoverHoverArrowText.Text = ImmersiveLyricOverlay.Visibility == Visibility.Visible ? "收起" : "展开";
        BottomCoverHoverArrowOverlay.Opacity = 1;
    }

    private void OnBottomCoverPointerExited(object sender, PointerRoutedEventArgs e)
    {
        BottomCoverHoverArrowOverlay.Opacity = 0;
    }

    private void OnImmersiveOverlayPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        MusicFullLeftControlPanel.Opacity = 1;
        MusicFullRightControlPanel.Opacity = 1;
    }

    private void OnImmersiveOverlayPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.Config.Settings.ImmersiveLyric.PureModeEnabled)
        {
            MusicFullLeftControlPanel.Opacity = 0;
            MusicFullRightControlPanel.Opacity = 0;
        }
    }

    private void OnToggleMusicFullWindowFullscreenClick(object sender, RoutedEventArgs e)
    {
        bool enteringFull = _appWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen;
        _appWindow.SetPresenter(enteringFull ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        AppTitleBar.Visibility = enteringFull ? Visibility.Collapsed : Visibility.Visible;
        MusicFullFullscreenIcon.Glyph = enteringFull ? "\uE73F" : "\uE740";
    }

    private void OnCover3DPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.Config.Settings.NoAnimate) return;
        Cover3DShineOverlay.Opacity = 0.85;
    }

    private void OnCover3DPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.Config.Settings.NoAnimate || sender is not FrameworkElement el || el.ActualWidth <= 0 || el.ActualHeight <= 0) return;

        var pt = e.GetCurrentPoint(el).Position;
        double nx = Math.Clamp((pt.X / el.ActualWidth) - 0.5, -0.5, 0.5);
        double ny = Math.Clamp((pt.Y / el.ActualHeight) - 0.5, -0.5, 0.5);

        const double maxTiltDeg = 12.0;
        Cover3DProjection.RotationY = nx * maxTiltDeg * 2.0;
        Cover3DProjection.RotationX = -ny * maxTiltDeg * 2.0;

        Cover3DShineBrush.StartPoint = new Windows.Foundation.Point(0.5 - nx, 0.5 - ny);
        Cover3DShineBrush.EndPoint = new Windows.Foundation.Point(0.5 + nx, 0.5 + ny);
    }

    private void OnCover3DPointerExited(object sender, PointerRoutedEventArgs e)
    {
        Cover3DProjection.RotationX = 0;
        Cover3DProjection.RotationY = 0;
        Cover3DShineOverlay.Opacity = 0;
    }

    private async void OnImmersiveArtistClick(object sender, RoutedEventArgs e)
    {
        var currentSong = ViewModel.Player.CurrentSong;
        if (currentSong == null) return;

        await CloseMusicFullOverlayAsync(animate: true);

        var firstArtist = currentSong.Artists?.FirstOrDefault();
        if (firstArtist != null && firstArtist.Id > 0)
        {
            await ViewModel.OpenArtistDetailAsync(firstArtist);
        }
        else if (!string.IsNullOrWhiteSpace(currentSong.ArtistText))
        {
            await ViewModel.ExecuteSearchAsync(currentSong.ArtistText);
        }
    }

    private void OnLyricAreaPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _lyricResumeScrollCts?.Cancel();
        _isPointerOverLyrics = true;
        ImmersiveLyricCorrectionPanel.Opacity = 1;
    }

    private async void OnLyricAreaPointerExited(object sender, PointerRoutedEventArgs e)
    {
        ImmersiveLyricCorrectionPanel.Opacity = 0;
        _lyricResumeScrollCts?.Cancel();
        _lyricResumeScrollCts = new CancellationTokenSource();
        var token = _lyricResumeScrollCts.Token;

        try
        {
            await Task.Delay(2000, token);
            if (!token.IsCancellationRequested)
            {
                _isPointerOverLyrics = false;
                ScrollToActiveLyricLine(force: true);
            }
        }
        catch (OperationCanceledException)
        {
            // 重新移入歌词区域时取消自动回滚计时
        }
    }

    private void OnLyricOffsetMinusClick(object sender, RoutedEventArgs e)
    {
        ViewModel.Player.AdjustLyricOffset(-200);
        LyricOffsetBadgeText.Text = $"{ViewModel.Player.LyricOffsetMs / 1000.0:+0.0;-0.0;0.0}s";
    }

    private void OnLyricOffsetResetClick(object sender, RoutedEventArgs e)
    {
        ViewModel.Player.AdjustLyricOffset(0);
        LyricOffsetBadgeText.Text = "0.0s";
    }

    private void OnLyricOffsetPlusClick(object sender, RoutedEventArgs e)
    {
        ViewModel.Player.AdjustLyricOffset(200);
        LyricOffsetBadgeText.Text = $"{ViewModel.Player.LyricOffsetMs / 1000.0:+0.0;-0.0;0.0}s";
    }

    private void OnImmersiveLyricLineClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LyricLine line)
        {
            _isPointerOverLyrics = false;
            ViewModel.Player.Seek(TimeSpan.FromMilliseconds(Math.Max(0, line.StartTimeMs - ViewModel.Player.LyricOffsetMs)));
        }
    }

    private void LoadMusicFullSettingsToUi()
    {
        _isLoadingLyricSettingsUi = true;
        var cfg = ViewModel.Config.Settings.ImmersiveLyric;

        LyricPureModeToggle.IsOn = cfg.PureModeEnabled;
        LyricHideCoverToggle.IsOn = cfg.HideCover;
        LyricCenterToggle.IsOn = cfg.CenterLyrics;
        LyricShowTransToggle.IsOn = cfg.ShowTranslation;
        LyricHideLyricsToggle.IsOn = cfg.HideLyrics;

        LyricShowMiniPlayBarToggle.IsOn = !cfg.HideMiniPlayBar;
        LyricContentWidthSlider.Value = Math.Clamp(cfg.ContentWidth, 50, 100);
        LyricContentWidthValueText.Text = $"{cfg.ContentWidth}%";

        LyricFontSizeSlider.Value = Math.Clamp(cfg.FontSize, 12, 32);
        LyricFontSizeValueText.Text = $"{cfg.FontSize}px";

        LyricLetterSpacingSlider.Value = Math.Clamp(cfg.LetterSpacing, -2, 10);
        LyricLetterSpacingValueText.Text = $"{cfg.LetterSpacing:0.#}px";

        LyricFontWeightSlider.Value = Math.Clamp(cfg.FontWeight, 100, 900);
        LyricFontWeightValueText.Text = cfg.FontWeight.ToString();

        LyricLineHeightSlider.Value = Math.Clamp(cfg.LineHeight, 1.0, 3.0);
        LyricLineHeightValueText.Text = $"{cfg.LineHeight:0.0}";

        LyricUseCustomBgToggle.IsOn = cfg.UseCustomBackground;
        LyricSolidColorHexBox.Text = string.IsNullOrWhiteSpace(cfg.SolidColorHex) ? "#1a1a1a" : cfg.SolidColorHex;
        if (cfg.GradientColors.Count >= 2)
        {
            LyricGradColor0Box.Text = cfg.GradientColors[0];
            LyricGradColor1Box.Text = cfg.GradientColors[1];
        }
        SelectComboItemByTag(LyricGradDirectionCombo, cfg.GradientDirection);
        LyricBgBlurSlider.Value = Math.Clamp(cfg.ImageBlur, 0, 20);
        LyricBgBrightnessSlider.Value = Math.Clamp(cfg.ImageBrightness, 10, 200);
        LyricCustomCssBox.Text = cfg.CustomCss ?? string.Empty;

        SwitchLyricSettingsTabUi("display");
        _isLoadingLyricSettingsUi = false;

        ApplyMusicFullConfiguration();
    }

    private void SwitchLyricSettingsTabUi(string tab)
    {
        LyricSetDisplayPanel.Visibility = tab == "display" ? Visibility.Visible : Visibility.Collapsed;
        LyricSetInterfacePanel.Visibility = tab == "interface" ? Visibility.Visible : Visibility.Collapsed;
        LyricSetTypographyPanel.Visibility = tab == "typography" ? Visibility.Visible : Visibility.Collapsed;
        LyricSetBackgroundPanel.Visibility = tab == "background" ? Visibility.Visible : Visibility.Collapsed;

        ApplyDynamicAccentButtonState(LyricSetTabDisplayBtn, tab == "display");
        ApplyDynamicAccentButtonState(LyricSetTabInterfaceBtn, tab == "interface");
        ApplyDynamicAccentButtonState(LyricSetTabTypographyBtn, tab == "typography");
        ApplyDynamicAccentButtonState(LyricSetTabBackgroundBtn, tab == "background");
    }

    private void OnLyricSettingsTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tab)
        {
            SwitchLyricSettingsTabUi(tab);
        }
    }

    private async void OnLyricDisplayConfigToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingLyricSettingsUi || !_isInitialized) return;
        var cfg = ViewModel.Config.Settings.ImmersiveLyric;

        cfg.PureModeEnabled = LyricPureModeToggle.IsOn;
        cfg.HideCover = LyricHideCoverToggle.IsOn;
        cfg.CenterLyrics = LyricCenterToggle.IsOn;
        cfg.ShowTranslation = LyricShowTransToggle.IsOn;
        cfg.HideLyrics = LyricHideLyricsToggle.IsOn;
        cfg.HideMiniPlayBar = !LyricShowMiniPlayBarToggle.IsOn;
        cfg.HidePlayBar = LyricShowMiniPlayBarToggle.IsOn;
        cfg.UseCustomBackground = LyricUseCustomBgToggle.IsOn;

        ApplyMusicFullConfiguration();
        await ViewModel.Config.SaveSettingsAsync();
    }

    private async void OnLyricTypographySliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isLoadingLyricSettingsUi || !_isInitialized) return;
        var cfg = ViewModel.Config.Settings.ImmersiveLyric;

        cfg.ContentWidth = (int)Math.Round(LyricContentWidthSlider.Value);
        cfg.FontSize = (int)Math.Round(LyricFontSizeSlider.Value);
        cfg.LetterSpacing = Math.Round(LyricLetterSpacingSlider.Value, 1);
        cfg.FontWeight = (int)Math.Round(LyricFontWeightSlider.Value);
        cfg.LineHeight = Math.Round(LyricLineHeightSlider.Value, 1);

        LyricContentWidthValueText.Text = $"{cfg.ContentWidth}%";
        LyricFontSizeValueText.Text = $"{cfg.FontSize}px";
        LyricLetterSpacingValueText.Text = $"{cfg.LetterSpacing:0.#}px";
        LyricFontWeightValueText.Text = cfg.FontWeight.ToString();
        LyricLineHeightValueText.Text = $"{cfg.LineHeight:0.0}";

        ApplyMusicFullConfiguration();
        await ViewModel.Config.SaveSettingsAsync();
    }

    private async void OnLyricThemeOptionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string theme) return;
        ViewModel.Config.Settings.ImmersiveLyric.Theme = theme;
        ApplyMusicFullConfiguration();
        await ViewModel.Config.SaveSettingsAsync();
    }

    private async void OnLyricBgModeOptionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string mode) return;
        ViewModel.Config.Settings.ImmersiveLyric.BackgroundMode = mode;
        ApplyMusicFullConfiguration();
        await ViewModel.Config.SaveSettingsAsync();
    }

    private async void OnLyricSolidPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string hex) return;
        _isLoadingLyricSettingsUi = true;
        LyricSolidColorHexBox.Text = hex;
        _isLoadingLyricSettingsUi = false;
        ViewModel.Config.Settings.ImmersiveLyric.SolidColorHex = hex;
        ApplyMusicFullConfiguration();
        await ViewModel.Config.SaveSettingsAsync();
    }

    private async void OnLyricCustomColorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingLyricSettingsUi || !_isInitialized) return;
        var cfg = ViewModel.Config.Settings.ImmersiveLyric;
        cfg.SolidColorHex = LyricSolidColorHexBox.Text.Trim();
        cfg.GradientColors =
        [
            string.IsNullOrWhiteSpace(LyricGradColor0Box.Text) ? "#1a1a1a" : LyricGradColor0Box.Text.Trim(),
            string.IsNullOrWhiteSpace(LyricGradColor1Box.Text) ? "#000000" : LyricGradColor1Box.Text.Trim()
        ];
        cfg.CustomCss = LyricCustomCssBox.Text.Trim();
        ApplyMusicFullConfiguration();
        await ViewModel.Config.SaveSettingsAsync();
    }

    private async void OnLyricGradDirectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingLyricSettingsUi || !_isInitialized) return;
        if (LyricGradDirectionCombo.SelectedItem is ComboBoxItem item && item.Tag is string dir)
        {
            ViewModel.Config.Settings.ImmersiveLyric.GradientDirection = dir;
            ApplyMusicFullConfiguration();
            await ViewModel.Config.SaveSettingsAsync();
        }
    }

    private async void OnPickLyricBgImageClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string? selectedPath = await Helpers.Win32Interop.PickOpenFileAsync(
                GetDialogOwnerHwnd(),
                "选择自定义歌词背景图片",
                "图片文件 (*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.bmp|所有文件 (*.*)|*.*");
            if (string.IsNullOrWhiteSpace(selectedPath)) return;

            ViewModel.Config.Settings.ImmersiveLyric.BackgroundImage = selectedPath;
            ApplyMusicFullConfiguration();
            await ViewModel.Config.SaveSettingsAsync();
            ViewModel.StatusToast = $"已设置自定义歌词背景图：{Path.GetFileName(selectedPath)}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"选择背景图片失败：{ex.Message}";
        }
    }

    private async void OnClearLyricBgImageClick(object sender, RoutedEventArgs e)
    {
        ViewModel.Config.Settings.ImmersiveLyric.BackgroundImage = string.Empty;
        ApplyMusicFullConfiguration();
        await ViewModel.Config.SaveSettingsAsync();
    }

    private async void OnLyricBgImageFilterChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isLoadingLyricSettingsUi || !_isInitialized) return;
        var cfg = ViewModel.Config.Settings.ImmersiveLyric;
        cfg.ImageBlur = Math.Round(LyricBgBlurSlider.Value, 1);
        cfg.ImageBrightness = Math.Round(LyricBgBrightnessSlider.Value, 0);
        ApplyMusicFullConfiguration();
        await ViewModel.Config.SaveSettingsAsync();
    }

    private void ApplyMusicFullConfiguration()
    {
        var cfg = ViewModel.Config.Settings.ImmersiveLyric;

        // 1. 显隐封面、歌词、居中、迷你播放栏与内容区宽度 (对齐 MusicFull.vue)
        bool hideCover = cfg.HideCover;
        bool hideLyrics = cfg.HideLyrics;

        if (hideCover && hideLyrics)
        {
            // 若同时勾选隐藏封面与隐藏歌词，至少保留封面展示歌曲信息
            hideCover = false;
        }

        ImmersiveCoverPanel.Visibility = hideCover ? Visibility.Collapsed : Visibility.Visible;
        ImmersiveLyricRightPanel.Visibility = hideLyrics ? Visibility.Collapsed : Visibility.Visible;
        ImmersiveHiddenCoverHeader.Visibility = hideCover ? Visibility.Visible : Visibility.Collapsed;
        ImmersiveHiddenCoverHeader.HorizontalAlignment = cfg.CenterLyrics ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        ImmersiveHiddenCoverTitle.TextAlignment = cfg.CenterLyrics ? TextAlignment.Center : TextAlignment.Left;
        ImmersiveHiddenCoverArtist.TextAlignment = cfg.CenterLyrics ? TextAlignment.Center : TextAlignment.Left;

        if (hideCover)
        {
            ImmersiveLeftCol.Width = new GridLength(0);
            ImmersiveRightCol.Width = new GridLength(1, GridUnitType.Star);
        }
        else if (hideLyrics)
        {
            ImmersiveLeftCol.Width = new GridLength(1, GridUnitType.Star);
            ImmersiveRightCol.Width = new GridLength(0);
        }
        else
        {
            ImmersiveLeftCol.Width = new GridLength(2, GridUnitType.Star);
            ImmersiveRightCol.Width = new GridLength(3, GridUnitType.Star);
        }

        // 内容区宽度百分比 (50% ~ 100%)
        double baseMaxWidth = Math.Max(960, RootLayout.ActualWidth > 100 ? RootLayout.ActualWidth : 1320);
        MusicFullContentWrapper.MaxWidth = Math.Round(baseMaxWidth * Math.Clamp(cfg.ContentWidth, 50, 100) / 100.0);

        if (ImmersiveLyricOverlay.Visibility == Visibility.Visible)
        {
            UpdatePlayBarsVisibilityForMusicFull(isOverlayOpen: true);
        }

        // 2. 背景设置面板显隐与高亮状态
        LyricBgThemeGroup.Visibility = cfg.UseCustomBackground ? Visibility.Collapsed : Visibility.Visible;
        LyricBgCustomGroup.Visibility = cfg.UseCustomBackground ? Visibility.Visible : Visibility.Collapsed;

        ApplyDynamicAccentButtonState(LyricThemeDefaultBtn, cfg.Theme == "default");
        ApplyDynamicAccentButtonState(LyricThemeLightBtn, cfg.Theme == "light");
        ApplyDynamicAccentButtonState(LyricThemeDarkBtn, cfg.Theme == "dark");

        ApplyDynamicAccentButtonState(LyricBgModeSolidBtn, cfg.BackgroundMode == "solid");
        ApplyDynamicAccentButtonState(LyricBgModeGradientBtn, cfg.BackgroundMode == "gradient");
        ApplyDynamicAccentButtonState(LyricBgModeImageBtn, cfg.BackgroundMode == "image");
        ApplyDynamicAccentButtonState(LyricBgModeCssBtn, cfg.BackgroundMode == "css");

        LyricBgModeSolidPanel.Visibility = cfg.BackgroundMode == "solid" ? Visibility.Visible : Visibility.Collapsed;
        LyricBgModeGradientPanel.Visibility = cfg.BackgroundMode == "gradient" ? Visibility.Visible : Visibility.Collapsed;
        LyricBgModeImagePanel.Visibility = cfg.BackgroundMode == "image" ? Visibility.Visible : Visibility.Collapsed;
        LyricBgModeCssPanel.Visibility = cfg.BackgroundMode == "css" ? Visibility.Visible : Visibility.Collapsed;

        // 3. 应用背景渐变/图片与自适应文字明暗主题
        ApplyMusicFullThemeAndBackground();

        // 4. 同步所有歌词行的字号、字重、字间距、行高、居中、翻译与颜色画刷
        SyncLyricLinesTypographyAndEmptyState();
    }

    private void SyncLyricLinesTypographyAndEmptyState()
    {
        var cfg = ViewModel.Config.Settings.ImmersiveLyric;
        var primaryBrush = _isMusicFullLightText
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(205, 255, 255, 255))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(205, 17, 24, 39));

        Brush activeBrush;
        var themeProfile = ThemeService.ResolveModeAdaptedProfile(ViewModel.Theme.GetEffectiveTheme(), !_isMusicFullLightText);
        if (themeProfile.SyncImmersiveLyricHighlight)
        {
            string highlightHex = !string.IsNullOrWhiteSpace(themeProfile.LyricHighlightColorHex)
                ? themeProfile.LyricHighlightColorHex
                : themeProfile.AccentColorHex;
            var highlightCol = ThemeService.ParseColorOrDefault(highlightHex, GetActiveThemeAccentColor());
            activeBrush = new SolidColorBrush(highlightCol);
        }
        else
        {
            activeBrush = _isMusicFullLightText
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
        }

        foreach (var line in ViewModel.LyricLines)
        {
            line.ApplyTypographyAndTheme(cfg, primaryBrush, activeBrush);
        }

        ImmersiveEmptyLyricText.Visibility = ViewModel.LyricLines.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async Task ExtractAndApplyCoverThemeAsync(string? coverUrl)
    {
        var fallback = Windows.UI.Color.FromArgb(255, 30, 41, 59);
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            _extractedCoverColor = fallback;
            ApplyMusicFullThemeAndBackground();
            SyncLyricLinesTypographyAndEmptyState();
            return;
        }

        try
        {
            byte[]? bytes = null;
            if (coverUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                bytes = await http.GetByteArrayAsync(coverUrl);
            }
            else if (File.Exists(coverUrl))
            {
                bytes = await File.ReadAllBytesAsync(coverUrl);
            }

            if (bytes != null && bytes.Length > 0)
            {
                using var ms = new MemoryStream(bytes);
                var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
                var transform = new BitmapTransform
                {
                    ScaledWidth = 24,
                    ScaledHeight = 24,
                    InterpolationMode = BitmapInterpolationMode.Linear
                };
                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Rgba8,
                    BitmapAlphaMode.Ignore,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                var raw = pixelData.DetachPixelData();
                long sumR = 0, sumG = 0, sumB = 0, count = 0;
                for (int i = 0; i + 3 < raw.Length; i += 4)
                {
                    sumR += raw[i];
                    sumG += raw[i + 1];
                    sumB += raw[i + 2];
                    count++;
                }

                if (count > 0)
                {
                    _extractedCoverColor = Windows.UI.Color.FromArgb(
                        255,
                        (byte)(sumR / count),
                        (byte)(sumG / count),
                        (byte)(sumB / count));
                }
            }
        }
        catch
        {
            _extractedCoverColor = fallback;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyMusicFullThemeAndBackground();
            SyncLyricLinesTypographyAndEmptyState();
        });

        await ApplyDynamicCoverAccentIfEnabledAsync(coverUrl);
    }

    private void ApplyMusicFullThemeAndBackground()
    {
        var cfg = ViewModel.Config.Settings.ImmersiveLyric;
        MusicFullCustomBgImageLayer.Visibility = Visibility.Collapsed;
        MusicFullBgGradientBrush.StartPoint = new Windows.Foundation.Point(0, 0);
        MusicFullBgGradientBrush.EndPoint = new Windows.Foundation.Point(0, 1);

        Windows.UI.Color stop0;
        Windows.UI.Color stop1;
        Windows.UI.Color stop2;

        if (!cfg.UseCustomBackground)
        {
            if (cfg.Theme == "light")
            {
                stop0 = Windows.UI.Color.FromArgb(255, 248, 250, 252);
                stop1 = Windows.UI.Color.FromArgb(255, 241, 245, 249);
                stop2 = Windows.UI.Color.FromArgb(255, 226, 232, 240);
                _isMusicFullLightText = false;
            }
            else if (cfg.Theme == "dark")
            {
                stop0 = Windows.UI.Color.FromArgb(255, 26, 28, 35);
                stop1 = Windows.UI.Color.FromArgb(255, 17, 19, 24);
                stop2 = Windows.UI.Color.FromArgb(255, 10, 12, 16);
                _isMusicFullLightText = true;
            }
            else
            {
                // 默认封面智能取色渐变 (1:1 对齐 linearColor.ts getImageLinearBackground)
                var (h, s, l) = RgbToHsl(_extractedCoverColor.R, _extractedCoverColor.G, _extractedCoverColor.B);
                double lightL = Math.Min(l + 0.15, 0.92);
                double midL = Math.Clamp(l - 0.05, 0.14, 0.78);
                double darkL = Math.Max(l - 0.25, 0.08);

                stop0 = HslToColor(h, s, lightL);
                stop1 = HslToColor(h, s, midL);
                stop2 = HslToColor(h, s, darkL);

                double perceivedBrightness = (stop0.R * 299 + stop0.G * 587 + stop0.B * 114) / 1000.0;
                _isMusicFullLightText = perceivedBrightness <= 165;
            }
        }
        else
        {
            switch (cfg.BackgroundMode)
            {
                case "solid":
                    if (!TryParseHexColor(cfg.SolidColorHex, out stop0))
                    {
                        stop0 = Windows.UI.Color.FromArgb(255, 26, 26, 26);
                    }
                    stop1 = stop0;
                    stop2 = stop0;
                    break;

                case "gradient":
                    var c0Str = cfg.GradientColors.Count > 0 ? cfg.GradientColors[0] : "#1a1a1a";
                    var c1Str = cfg.GradientColors.Count > 1 ? cfg.GradientColors[1] : "#000000";
                    if (!TryParseHexColor(c0Str, out stop0)) stop0 = Windows.UI.Color.FromArgb(255, 26, 26, 26);
                    if (!TryParseHexColor(c1Str, out stop2)) stop2 = Windows.UI.Color.FromArgb(255, 0, 0, 0);
                    stop1 = Windows.UI.Color.FromArgb(
                        255,
                        (byte)((stop0.R + stop2.R) / 2),
                        (byte)((stop0.G + stop2.G) / 2),
                        (byte)((stop0.B + stop2.B) / 2));

                    (MusicFullBgGradientBrush.StartPoint, MusicFullBgGradientBrush.EndPoint) = cfg.GradientDirection switch
                    {
                        "to top" => (new Windows.Foundation.Point(0, 1), new Windows.Foundation.Point(0, 0)),
                        "to right" => (new Windows.Foundation.Point(0, 0), new Windows.Foundation.Point(1, 0)),
                        "to left" => (new Windows.Foundation.Point(1, 0), new Windows.Foundation.Point(0, 0)),
                        "to bottom right" or "45deg" => (new Windows.Foundation.Point(0, 0), new Windows.Foundation.Point(1, 1)),
                        _ => (new Windows.Foundation.Point(0, 0), new Windows.Foundation.Point(0, 1))
                    };
                    break;

                case "image":
                    stop0 = Windows.UI.Color.FromArgb(255, 15, 23, 42);
                    stop1 = Windows.UI.Color.FromArgb(255, 10, 15, 29);
                    stop2 = Windows.UI.Color.FromArgb(255, 5, 8, 17);
                    if (!string.IsNullOrWhiteSpace(cfg.BackgroundImage) && File.Exists(cfg.BackgroundImage))
                    {
                        MusicFullCustomBgImageLayer.Visibility = Visibility.Visible;
                        SetImageUrlSafe(MusicFullCustomBgImage, cfg.BackgroundImage);
                        double brightnessRatio = Math.Clamp(cfg.ImageBrightness / 100.0, 0.1, 2.0);
                        byte overlayAlpha = brightnessRatio < 1.0
                            ? (byte)Math.Clamp((1.0 - brightnessRatio) * 230 + (cfg.ImageBlur * 3), 25, 235)
                            : (byte)Math.Clamp((brightnessRatio - 1.0) * 140, 0, 160);
                        var overlayRgb = brightnessRatio < 1.0 ? (byte)0 : (byte)255;
                        MusicFullCustomBgDimBorder.Background = new SolidColorBrush(
                            Windows.UI.Color.FromArgb(overlayAlpha, overlayRgb, overlayRgb, overlayRgb));
                    }
                    break;

                default: // "css"
                    var hexMatches = System.Text.RegularExpressions.Regex.Matches(cfg.CustomCss ?? string.Empty, @"#[0-9a-fA-F]{6}");
                    if (hexMatches.Count >= 2 &&
                        TryParseHexColor(hexMatches[0].Value, out stop0) &&
                        TryParseHexColor(hexMatches[1].Value, out stop2))
                    {
                        stop1 = Windows.UI.Color.FromArgb(
                            255,
                            (byte)((stop0.R + stop2.R) / 2),
                            (byte)((stop0.G + stop2.G) / 2),
                            (byte)((stop0.B + stop2.B) / 2));
                    }
                    else if (hexMatches.Count == 1 && TryParseHexColor(hexMatches[0].Value, out stop0))
                    {
                        stop1 = stop0;
                        stop2 = stop0;
                    }
                    else
                    {
                        stop0 = Windows.UI.Color.FromArgb(255, 26, 26, 26);
                        stop1 = Windows.UI.Color.FromArgb(255, 15, 23, 42);
                        stop2 = Windows.UI.Color.FromArgb(255, 5, 8, 17);
                    }
                    break;
            }

            double customBrightness = (stop0.R * 299 + stop0.G * 587 + stop0.B * 114) / 1000.0;
            _isMusicFullLightText = cfg.BackgroundMode == "image" || customBrightness <= 165;
        }

        MusicFullBgStop0.Color = stop0;
        MusicFullBgStop1.Color = stop1;
        MusicFullBgStop2.Color = stop2;

        // 同步详情页文字、按钮图标与 SimplePlayBar 的前景色/主色
        ImmersiveLyricOverlay.RequestedTheme = _isMusicFullLightText ? ElementTheme.Dark : ElementTheme.Light;
        var mainFg = new SolidColorBrush(_isMusicFullLightText
            ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
            : Windows.UI.Color.FromArgb(255, 17, 24, 39));
        var subFg = new SolidColorBrush(_isMusicFullLightText
            ? Windows.UI.Color.FromArgb(204, 255, 255, 255)
            : Windows.UI.Color.FromArgb(190, 31, 41, 55));
        var mutedFg = new SolidColorBrush(_isMusicFullLightText
            ? Windows.UI.Color.FromArgb(153, 255, 255, 255)
            : Windows.UI.Color.FromArgb(153, 55, 65, 81));

        ImmersiveSongTitle.Foreground = mainFg;
        ImmersiveSongArtist.Foreground = subFg;
        ImmersiveHiddenCoverTitle.Foreground = mainFg;
        ImmersiveHiddenCoverArtist.Foreground = subFg;
        ImmersiveEmptyLyricText.Foreground = mutedFg;

        SimplePlayBarPositionText.Foreground = mutedFg;
        SimplePlayBarDurationText.Foreground = mutedFg;
        SimplePlayBarModeIcon.Foreground = mainFg;
        SimplePlayBarPrevIcon.Foreground = mainFg;
        SimplePlayBarStopIcon.Foreground = mainFg;
        SimplePlayBarNextIcon.Foreground = mainFg;
        SimplePlayBarQueueIcon.Foreground = mainFg;
        SimplePlayBarVolumeIcon.Foreground = mainFg;

        MusicFullCloseIcon.Foreground = mainFg;
        MusicFullSettingsIcon.Foreground = mainFg;
        MusicFullFullscreenIcon.Foreground = mainFg;
        ConfigureTitleBarButtonsTheme();

        // SimplePlayBar 中央播放按钮采用封面主色调或当前主题强调色增强质感
        var (ch, cs, cl) = RgbToHsl(_extractedCoverColor.R, _extractedCoverColor.G, _extractedCoverColor.B);
        var playBtnColor = cs > 0.12
            ? HslToColor(ch, Math.Clamp(cs, 0.35, 0.78), Math.Clamp(cl, 0.32, 0.48))
            : GetActiveThemeAccentColor();
        SimplePlayBarPlayBtn.Background = new SolidColorBrush(playBtnColor);
    }

    private static (double H, double S, double L) RgbToHsl(byte rByte, byte gByte, byte bByte)
    {
        double r = rByte / 255.0;
        double g = gByte / 255.0;
        double b = bByte / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double h = 0, s = 0, l = (max + min) / 2.0;

        if (Math.Abs(max - min) > 0.0001)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (Math.Abs(max - r) < 0.0001)
            {
                h = (g - b) / d + (g < b ? 6.0 : 0.0);
            }
            else if (Math.Abs(max - g) < 0.0001)
            {
                h = (b - r) / d + 2.0;
            }
            else
            {
                h = (r - g) / d + 4.0;
            }
            h /= 6.0;
        }
        return (h, s, l);
    }

    private static Windows.UI.Color HslToColor(double h, double s, double l)
    {
        double r, g, b;
        if (s <= 0.0001)
        {
            r = g = b = l;
        }
        else
        {
            static double Hue2Rgb(double p, double q, double t)
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
                if (t < 1.0 / 2.0) return q;
                if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
                return p;
            }

            double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
            double p = 2.0 * l - q;
            r = Hue2Rgb(p, q, h + 1.0 / 3.0);
            g = Hue2Rgb(p, q, h);
            b = Hue2Rgb(p, q, h - 1.0 / 3.0);
        }

        return Windows.UI.Color.FromArgb(
            255,
            (byte)Math.Clamp(Math.Round(r * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(g * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(b * 255), 0, 255));
    }

    private static bool TryParseHexColor(string? hex, out Windows.UI.Color color)
    {
        color = Windows.UI.Color.FromArgb(255, 26, 26, 26);
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var clean = hex.Trim().TrimStart('#');
        if (clean.Length == 6 &&
            byte.TryParse(clean[0..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(clean[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(clean[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            color = Windows.UI.Color.FromArgb(255, r, g, b);
            return true;
        }
        return false;
    }

    private void OnToggleDesktopLyricClick(object sender, RoutedEventArgs e)
    {
        if (_desktopLyricWindow != null)
        {
            _desktopLyricWindow.Close();
            _desktopLyricWindow = null;
            ViewModel.IsDesktopLyricOpen = false;
            return;
        }

        _desktopLyricWindow = new DesktopLyricWindow(ViewModel);
        _desktopLyricWindow.Closed += (_, _) => _desktopLyricWindow = null;
        _desktopLyricWindow.Activate();
        ViewModel.IsDesktopLyricOpen = true;
    }

    private async Task HideQueueDrawerAnimatedAsync()
    {
        if (QueueDrawerOverlay.Visibility != Visibility.Visible) return;
        await PlayExitAnimationAsync(QueueDrawerPanel, 300, 0, 180);
        QueueDrawerOverlay.Visibility = Visibility.Collapsed;
    }

    private async void OnQueueDrawerBackdropTapped(object sender, TappedRoutedEventArgs e)
    {
        await HideQueueDrawerAnimatedAsync();
    }

    private async void OnToggleQueueDrawerClick(object sender, RoutedEventArgs e)
    {
        if (QueueDrawerOverlay.Visibility == Visibility.Visible)
        {
            await HideQueueDrawerAnimatedAsync();
        }
        else
        {
            var currentId = ViewModel.Player.CurrentSong?.Id;
            bool activelyPlaying = ViewModel.Player.IsPlaying;
            foreach (var q in ViewModel.Player.Queue)
            {
                q.IsPlaying = !string.IsNullOrEmpty(currentId) && q.Id == currentId;
                q.IsAudioActivelyPlaying = q.IsPlaying && activelyPlaying;
                q.IsLiked = ViewModel.Config.IsFavorite(q.Id);
            }

            QueueDrawerOverlay.Visibility = Visibility.Visible;
            PlayEntranceAnimation(QueueDrawerBackdrop, 0, 0, 200);
            PlayEntranceAnimation(QueueDrawerPanel, 300, 0, 260);
            ScrollQueueToCurrentSong();
        }
    }

    private void ScrollQueueToCurrentSong()
    {
        var current = ViewModel.Player.CurrentSong;
        if (current == null || ViewModel.Player.Queue.Count == 0) return;
        var matched = ViewModel.Player.Queue.FirstOrDefault(s => s.Id == current.Id);
        if (matched != null)
        {
            QueueListView.ScrollIntoView(matched, ScrollIntoViewAlignment.Leading);
        }
    }

    private async void OnQueuePlayButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SongItem song)
        {
            if (ViewModel.Player.CurrentSong?.Id == song.Id)
            {
                ViewModel.Player.TogglePlayPause();
            }
            else
            {
                await ViewModel.Player.PlaySongAsync(song);
            }
        }
    }

    private void OnQueueContextPlayNextClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is SongItem song)
        {
            if (ViewModel.Player.MoveQueueItemToNext(song))
            {
                ViewModel.StatusToast = $"已将《{song.Name}》设为下一首播放";
            }
        }
    }

    private void OnQueueContextRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is SongItem song)
        {
            ViewModel.Player.RemoveQueueItem(song);
        }
    }

    private async void OnQueueRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is SongItem song)
        {
            e.Handled = true;
            await ViewModel.Player.PlaySongAsync(song);
        }
    }

    private async void OnClearQueueClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Player.Queue.Count == 0) return;

        var dialog = new ContentDialog
        {
            Title = "清空播放列表",
            Content = "确定要清空播放列表吗？此操作不可恢复。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootLayout.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.Player.Queue.Clear();
            await ViewModel.Config.SaveQueueAsync([]);
            ViewModel.StatusToast = "已清空播放列表";
        }
    }

    // ==================== 窗口状态与设置保存 ====================

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!_isInitialized || !_appWindow.IsVisible) return;
        if (!args.DidSizeChange && !args.DidPositionChange && !args.DidPresenterChange) return;

        DispatcherQueue?.TryEnqueue(ScheduleUpdateTitleBarPassthroughRegions);

        if (_appWindow.Presenter is OverlappedPresenter overlapped)
        {
            bool isMaximized = overlapped.State == OverlappedPresenterState.Maximized;
            ViewModel.Config.Settings.IsWindowMaximized = isMaximized;

            if (overlapped.State == OverlappedPresenterState.Restored)
            {
                var pos = _appWindow.Position;
                var size = _appWindow.Size;
                if (size.Width >= 900 && size.Height >= 600 && pos.X > -10000 && pos.Y > -10000)
                {
                    _sessionWindowBounds = new RectInt32(pos.X, pos.Y, size.Width, size.Height);
                    _hasSessionWindowBounds = true;
                    ViewModel.Config.Settings.WindowWidth = size.Width;
                    ViewModel.Config.Settings.WindowHeight = size.Height;
                }
            }

            _windowBoundsSaveCts?.Cancel();
            _windowBoundsSaveCts = new CancellationTokenSource();
            var token = _windowBoundsSaveCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(400, token);
                    if (!token.IsCancellationRequested)
                    {
                        await ViewModel.Config.SaveSettingsAsync();
                    }
                }
                catch
                {
                }
            }, token);
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_appWindow.Presenter is OverlappedPresenter overlapped)
        {
            ViewModel.Config.Settings.IsWindowMaximized = overlapped.State == OverlappedPresenterState.Maximized;
            if (overlapped.State == OverlappedPresenterState.Restored && _appWindow.Size.Width >= 900 && _appWindow.Size.Height >= 600)
            {
                ViewModel.Config.Settings.WindowWidth = _appWindow.Size.Width;
                ViewModel.Config.Settings.WindowHeight = _appWindow.Size.Height;
            }
        }
        ViewModel.Config.SaveSettingsSync();

        if (_isForceExiting)
        {
            _opticalGlassRenderer.Dispose();
            SingleInstanceGuard.ExternalActivationRequested -= OnExternalActivationRequested;
            SingleInstanceGuard.Release();
            RemoveSystemTrayIcon();
            ViewModel.Player.Dispose();
            _desktopLyricWindow?.Close();
            _miniPlayerWindow?.Close();
            return;
        }

        var closeAction = ViewModel.Config.Settings.CloseAction;
        if (string.Equals(closeAction, "close", StringComparison.OrdinalIgnoreCase))
        {
            _opticalGlassRenderer.Dispose();
            SingleInstanceGuard.ExternalActivationRequested -= OnExternalActivationRequested;
            SingleInstanceGuard.Release();
            RemoveSystemTrayIcon();
            ViewModel.Player.Dispose();
            _desktopLyricWindow?.Close();
            _miniPlayerWindow?.Close();
            return;
        }

        if (string.Equals(closeAction, "ask", StringComparison.OrdinalIgnoreCase))
        {
            args.Cancel = true;
            _ = ShowCloseConfirmDialogAsync();
            return;
        }

        // 默认或 "minimize"：最小化（隐藏）到系统托盘
        args.Cancel = true;
        HideMainWindowRememberingBounds();
    }

    private async Task ShowCloseConfirmDialogAsync()
    {
        if (_isCloseConfirmDialogOpen) return;
        _isCloseConfirmDialogOpen = true;
        try
        {
            var rememberCheck = new CheckBox
            {
                Content = "记住我的选择",
                IsChecked = false,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var contentPanel = new StackPanel { Spacing = 6 };
            contentPanel.Children.Add(new TextBlock
            {
                Text = "请选择关闭窗口时的操作：",
                TextWrapping = TextWrapping.Wrap
            });
            contentPanel.Children.Add(rememberCheck);

            var dialog = new ContentDialog
            {
                Title = "关闭应用",
                Content = contentPanel,
                PrimaryButtonText = "最小化到托盘",
                SecondaryButtonText = "退出应用",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootLayout.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                if (rememberCheck.IsChecked == true)
                {
                    ViewModel.Config.Settings.CloseAction = "minimize";
                    SelectComboItemByTag(CloseActionComboBox, "minimize");
                    await ViewModel.Config.SaveSettingsAsync();
                }
                HideMainWindowRememberingBounds();
            }
            else if (result == ContentDialogResult.Secondary)
            {
                if (rememberCheck.IsChecked == true)
                {
                    ViewModel.Config.Settings.CloseAction = "close";
                    SelectComboItemByTag(CloseActionComboBox, "close");
                    ViewModel.Config.SaveSettingsSync();
                }
                ForceExitApplication();
            }
        }
        finally
        {
            _isCloseConfirmDialogOpen = false;
        }
    }

    private void ForceExitApplication()
    {
        _isForceExiting = true;
        _opticalGlassRenderer.Dispose();
        SingleInstanceGuard.ExternalActivationRequested -= OnExternalActivationRequested;
        SingleInstanceGuard.Release();
        RemoveSystemTrayIcon();
        ViewModel.Player.Dispose();
        _desktopLyricWindow?.Close();
        _miniPlayerWindow?.Close();
        Application.Current.Exit();
    }

    private void InitializeSystemTrayIcon()
    {
        try
        {
            var pngPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.png");
            if (File.Exists(pngPath))
            {
                AppBrandLogoImg.Source = new BitmapImage(new Uri(pngPath));
            }

            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
            if (File.Exists(iconPath))
            {
                _appWindow.SetIcon(iconPath);
                _trayIconHandle = Win32Interop.LoadImageW(IntPtr.Zero, iconPath, Win32Interop.IMAGE_ICON, 0, 0, Win32Interop.LR_LOADFROMFILE | Win32Interop.LR_DEFAULTSIZE);
            }
            if (_trayIconHandle == IntPtr.Zero)
            {
                _trayIconHandle = Win32Interop.LoadIconW(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
            }

            _traySubclassDelegate = OnTrayWindowSubclassProc;
            Win32Interop.SetWindowSubclass(_hWnd, _traySubclassDelegate, new UIntPtr(9105), UIntPtr.Zero);

            var nid = new Win32Interop.NOTIFYICONDATAW
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32Interop.NOTIFYICONDATAW>(),
                hWnd = _hWnd,
                uID = 1001,
                uFlags = Win32Interop.NIF_MESSAGE | Win32Interop.NIF_ICON | Win32Interop.NIF_TIP,
                uCallbackMessage = Win32Interop.WM_TRAYICON,
                hIcon = _trayIconHandle,
                szTip = "Alger Music Player"
            };
            _isTrayIconAdded = Win32Interop.Shell_NotifyIconW(Win32Interop.NIM_ADD, ref nid);
        }
        catch
        {
        }
    }

    private void UpdateSystemTrayTooltip()
    {
        if (!_isTrayIconAdded) return;
        try
        {
            string tip = "Alger Music Player";
            if (!string.IsNullOrWhiteSpace(ViewModel.CurrentSongName) && ViewModel.CurrentSongName != "未播放音乐")
            {
                tip = string.IsNullOrWhiteSpace(ViewModel.CurrentArtistName)
                    ? $"{ViewModel.CurrentSongName} - Alger Music"
                    : $"{ViewModel.CurrentSongName} - {ViewModel.CurrentArtistName}";
            }
            if (tip.Length > 120) tip = tip[..120];

            var nid = new Win32Interop.NOTIFYICONDATAW
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32Interop.NOTIFYICONDATAW>(),
                hWnd = _hWnd,
                uID = 1001,
                uFlags = Win32Interop.NIF_TIP,
                szTip = tip
            };
            _ = Win32Interop.Shell_NotifyIconW(Win32Interop.NIM_MODIFY, ref nid);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 实时更新任务栏与窗口标题，播放时显示“歌曲名 - 歌手 - Alger Music Player”，未播放时复原为“Alger Music Player”
    /// </summary>
    private void UpdateTaskbarTitle()
    {
        try
        {
            string titleText = "Alger Music Player";
            if (!string.IsNullOrWhiteSpace(ViewModel.CurrentSongName) &&
                ViewModel.CurrentSongName != "未播放音乐" &&
                ViewModel.CurrentSongName != "Alger Music Player")
            {
                var artist = ViewModel.CurrentArtistName;
                titleText = string.IsNullOrWhiteSpace(artist)
                    ? $"{ViewModel.CurrentSongName} - Alger Music Player"
                    : $"{ViewModel.CurrentSongName} - {artist} - Alger Music Player";
            }

            this.Title = titleText;
            if (_appWindow != null)
            {
                _appWindow.Title = titleText;
            }
            if (_hWnd != IntPtr.Zero)
            {
                Win32Interop.SetWindowText(_hWnd, titleText);
            }
        }
        catch
        {
        }
    }

    private void RemoveSystemTrayIcon()
    {
        if (!_isTrayIconAdded) return;
        _isTrayIconAdded = false;
        try
        {
            var nid = new Win32Interop.NOTIFYICONDATAW
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32Interop.NOTIFYICONDATAW>(),
                hWnd = _hWnd,
                uID = 1001
            };
            _ = Win32Interop.Shell_NotifyIconW(Win32Interop.NIM_DELETE, ref nid);
        }
        catch
        {
        }
    }

    private IntPtr OnTrayWindowSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (uMsg == Win32Interop.WM_TRAYICON)
        {
            int mouseMsg = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouseMsg == Win32Interop.WM_LBUTTONUP || mouseMsg == Win32Interop.WM_LBUTTONDBLCLK)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_appWindow.IsVisible)
                    {
                        HideMainWindowRememberingBounds();
                    }
                    else
                    {
                        ShowMainWindowRestoringBounds();
                    }
                });
                return IntPtr.Zero;
            }

            if (mouseMsg == Win32Interop.WM_RBUTTONUP || mouseMsg == Win32Interop.WM_CONTEXTMENU)
            {
                DispatcherQueue.TryEnqueue(ShowSystemTrayContextMenu);
                return IntPtr.Zero;
            }
        }
        return Win32Interop.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void ShowSystemTrayContextMenu()
    {
        IntPtr hMenu = Win32Interop.CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        try
        {
            if (ViewModel.Player.CurrentSong != null)
            {
                var songLabel = $"{ViewModel.CurrentSongName} - {ViewModel.CurrentArtistName}";
                if (songLabel.Length > 28) songLabel = songLabel[..28] + "...";
                Win32Interop.AppendMenuW(hMenu, Win32Interop.MF_STRING | Win32Interop.MF_GRAYED | Win32Interop.MF_DISABLED, UIntPtr.Zero, songLabel);
                Win32Interop.AppendMenuW(hMenu, Win32Interop.MF_SEPARATOR, UIntPtr.Zero, null);
            }

            Win32Interop.AppendMenuW(hMenu, Win32Interop.MF_STRING, new UIntPtr(1), "显示主窗口");
            Win32Interop.AppendMenuW(hMenu, Win32Interop.MF_STRING, new UIntPtr(2), ViewModel.IsCurrentSongLiked ? "取消收藏" : "收藏");
            Win32Interop.AppendMenuW(hMenu, Win32Interop.MF_SEPARATOR, UIntPtr.Zero, null);
            Win32Interop.AppendMenuW(hMenu, Win32Interop.MF_STRING, new UIntPtr(3), "上一首");
            Win32Interop.AppendMenuW(hMenu, Win32Interop.MF_STRING, new UIntPtr(4), ViewModel.IsPlaying ? "暂停" : "播放");
            Win32Interop.AppendMenuW(hMenu, Win32Interop.MF_STRING, new UIntPtr(5), "下一首");
            Win32Interop.AppendMenuW(hMenu, Win32Interop.MF_STRING, new UIntPtr(6), ViewModel.IsDesktopLyricOpen ? "关闭桌面歌词" : "开启桌面歌词");
            Win32Interop.AppendMenuW(hMenu, Win32Interop.MF_SEPARATOR, UIntPtr.Zero, null);
            Win32Interop.AppendMenuW(hMenu, Win32Interop.MF_STRING, new UIntPtr(7), "退出");

            Win32Interop.GetCursorPos(out var pt);
            Win32Interop.SetForegroundWindow(_hWnd);
            int cmd = Win32Interop.TrackPopupMenuEx(
                hMenu,
                Win32Interop.TPM_RETURNCMD | Win32Interop.TPM_RIGHTBUTTON | Win32Interop.TPM_BOTTOMALIGN,
                pt.X,
                pt.Y,
                _hWnd,
                IntPtr.Zero);
            Win32Interop.PostMessage(_hWnd, Win32Interop.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (cmd)
            {
                case 1:
                    ShowMainWindowRestoringBounds();
                    break;
                case 2:
                    OnToggleCurrentSongLikeClick(this, new RoutedEventArgs());
                    break;
                case 3:
                    _ = ViewModel.Player.PreviousAsync();
                    break;
                case 4:
                    ViewModel.Player.TogglePlayPause();
                    break;
                case 5:
                    _ = ViewModel.Player.NextAsync();
                    break;
                case 6:
                    OnToggleDesktopLyricClick(this, new RoutedEventArgs());
                    break;
                case 7:
                    ForceExitApplication();
                    break;
            }
        }
        finally
        {
            Win32Interop.DestroyMenu(hMenu);
        }
    }

    private void HideMainWindowRememberingBounds()
    {
        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored } &&
            _appWindow.Size.Width >= 900 && _appWindow.Size.Height >= 600 &&
            _appWindow.Position.X > -10000 && _appWindow.Position.Y > -10000)
        {
            _sessionWindowBounds = new RectInt32(_appWindow.Position.X, _appWindow.Position.Y, _appWindow.Size.Width, _appWindow.Size.Height);
            _hasSessionWindowBounds = true;
        }
        _appWindow.Hide();
    }

    private void OnExternalActivationRequested()
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (_miniPlayerWindow != null)
            {
                try
                {
                    _miniPlayerWindow.Close();
                }
                catch
                {
                }
                _miniPlayerWindow = null;
            }
            ShowMainWindowRestoringBounds();
        });
    }

    private void ShowMainWindowRestoringBounds()
    {
        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } minimizedPresenter)
        {
            minimizedPresenter.Restore();
        }
        else if (_hasSessionWindowBounds && _appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            _appWindow.MoveAndResize(_sessionWindowBounds);
        }

        _appWindow.Show();
        if (Win32Interop.IsIconic(_hWnd))
        {
            _ = Win32Interop.ShowWindow(_hWnd, Win32Interop.SW_RESTORE);
        }
        _ = Win32Interop.SetForegroundWindow(_hWnd);
        Activate();
        ScheduleDynamicPowerStateCheck();
    }

    private void OnTogglePinTopClick(object sender, RoutedEventArgs e)
    {
        bool next = !ViewModel.Config.Settings.AlwaysOnTop;
        ViewModel.Config.Settings.AlwaysOnTop = next;
        Win32Interop.SetAlwaysOnTop(_hWnd, next);
        PinTopIcon.Glyph = next ? "\uE840" : "\uE718";
        ViewModel.StatusToast = next ? "已开启窗口置顶" : "已取消窗口置顶";
    }

    private void OnOpenMiniPlayerClick(object sender, RoutedEventArgs e)
    {
        HideMainWindowRememberingBounds();
        _miniPlayerWindow = new MiniPlayerWindow(ViewModel, openMusicFull =>
        {
            ShowMainWindowRestoringBounds();
            if (openMusicFull && ImmersiveLyricOverlay.Visibility != Visibility.Visible)
            {
                OnToggleImmersiveLyricClick(this, new RoutedEventArgs());
            }
        });
        _miniPlayerWindow.Activate();
    }

    // ==================== 系统设置 7 大分区交互事件（基本设置 / 播放设置 / 应用设置 / 网络设置 / 系统管理 / 关于 / 赞赏） ====================

    private async void OnAutoThemeToggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || _isSyncingThemeToggleUi) return;
        string targetMode = AutoThemeToggle.IsOn
            ? "System"
            : (ViewModel.Config.Settings.ManualTheme == "Light" ? "Light" : "Dark");
        await ViewModel.Theme.SetGlobalModeOverrideAsync(targetMode);
    }

    private async void OnToggleThemeClick(object sender, RoutedEventArgs e)
    {
        bool currentlyLight = RootLayout.ActualTheme == ElementTheme.Light;
        string nextMode = currentlyLight ? "Dark" : "Light";

        _isSyncingThemeToggleUi = true;
        AutoThemeToggle.IsOn = false;
        _isSyncingThemeToggleUi = false;

        await ViewModel.Theme.SetGlobalModeOverrideAsync(nextMode);
    }

    private async void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || LanguageComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string lang) return;
        await ViewModel.I18n.SetLanguageAsync(lang);
        ViewModel.StatusToast = $"语言已切换为: {lang}";
    }

    private async void OnTranslationEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || TranslationEngineComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string engine) return;
        ViewModel.Config.Settings.LyricTranslationEngine = engine;
        await ViewModel.Config.SaveSettingsAsync();
        ViewModel.StatusToast = $"歌词转换引擎已设置为: {engine}";
    }

    private async void OnFontScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || FontScopeComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string scope) return;
        ViewModel.Config.Settings.FontScope = scope;
        await ViewModel.Config.SaveSettingsAsync();
    }

    private async void OnSystemFontSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || SystemFontComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string fontName) return;
        var ff = new FontFamily(fontName);
        FontPreviewZh.FontFamily = ff;
        FontPreviewEn.FontFamily = ff;
        FontPreviewJa.FontFamily = ff;
        FontPreviewKo.FontFamily = ff;
        if (ViewModel.Config.Settings.FontScope == "global")
        {
            MainNavView.FontFamily = ff;
            ImmersiveLyricListView.FontFamily = ff;
            BottomSongTitleText.FontFamily = ff;
            BottomInlineLyricText.FontFamily = ff;
        }
        else
        {
            ImmersiveLyricListView.FontFamily = ff;
            BottomInlineLyricText.FontFamily = ff;
        }

        ViewModel.Config.Settings.FontFamily = fontName;
        await ViewModel.Config.SaveSettingsAsync();
        ViewModel.StatusToast = $"已应用字体: {fontName}";
    }

    private async void OnNoAnimateToggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        bool animateEnabled = NoAnimateToggle.IsOn;
        ViewModel.Config.Settings.NoAnimate = !animateEnabled;
        NoAnimateStateLabel.Text = animateEnabled ? "开启" : "关闭";
        AnimationSpeedSlider.IsEnabled = animateEnabled;
        ApplyCollectionTransitions();
        await ViewModel.Config.SaveSettingsAsync();
    }

    private async void OnAnimationSpeedChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isInitialized) return;
        double speed = Math.Round(e.NewValue, 1);
        ViewModel.Config.Settings.AnimationSpeed = speed;
        AnimationSpeedValueText.Text = $"{speed:0.#}x";
        await ViewModel.Config.SaveSettingsAsync();
    }

    private async void OnGpuAccelerationToggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        ViewModel.Config.Settings.EnableGpuAcceleration = GpuAccelerationToggle.IsOn;
        GpuRestartHintText.Visibility = Visibility.Visible;
        await ViewModel.Config.SaveSettingsAsync();
        ViewModel.StatusToast = "GPU 硬件加速设置已保存（重启应用后生效）";
    }

    private async void OnQualitySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || QualityComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string q) return;
        ViewModel.Config.Settings.MusicQuality = q;
        await ViewModel.Config.SaveSettingsAsync();
        ViewModel.StatusToast = $"在线播放与下载目标音质已切换为：{item.Content}";
    }

    private async void OnUnblockToggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        ViewModel.Config.Settings.EnableMusicUnblock = UnblockToggle.IsOn;
        SyncUnblockSettingsUi();
        await ViewModel.Config.SaveSettingsAsync();
    }

    private async void OnAutoPlayToggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        ViewModel.Config.Settings.AutoPlay = AutoPlayToggle.IsOn;
        await ViewModel.Config.SaveSettingsAsync();
    }

    private async void OnAudioFadeToggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        bool enabled = AudioFadeToggle.IsOn;
        ViewModel.Config.Settings.EnableAudioFade = enabled;
        AudioFadeDurationPanel.Opacity = enabled ? 1.0 : 0.4;
        AudioFadeDurationSlider.IsEnabled = enabled;
        ViewModel.Player.SetAudioFade(enabled, ViewModel.Config.Settings.AudioFadeDurationSeconds);
        await ViewModel.Config.SaveSettingsAsync();
    }

    private async void OnAudioFadeDurationChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isInitialized) return;
        double duration = Math.Round(e.NewValue * 2) / 2.0; // 0.5s 步进
        AudioFadeDurationLabel.Text = $"{duration:F1}s";
        ViewModel.Config.Settings.AudioFadeDurationSeconds = duration;
        ViewModel.Player.SetAudioFade(ViewModel.Config.Settings.EnableAudioFade, duration);
        await ViewModel.Config.SaveSettingsAsync();
    }

    // ==================== 5.1 / 7.1 多声道空间可视化配置与独立通道校准 ====================
    private int _multichannelModalAnimVersion;
    private bool _isClosingMultichannelModal;
    private bool _isSyncingMultichannelUi;

    private void OnOpenMultichannelModalClick(object sender, RoutedEventArgs e)
    {
        OpenMultichannelModal();
    }

    private void OnOpenMultichannelModalFromBadgeClick(object sender, RoutedEventArgs e)
    {
        OpenMultichannelModal();
    }

    private async void OnCloseMultichannelModalClick(object sender, RoutedEventArgs e)
    {
        await CloseMultichannelModalAsync();
    }

    private async void OnMultichannelModalBackdropTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, MultichannelModalOverlay))
        {
            await CloseMultichannelModalAsync();
        }
    }

    private void OnMultichannelModalCardTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void RefreshMultichannelSummaryLabel()
    {
        int devNum = ViewModel.Config.Settings.AudioOutputDeviceNumber;
        _ = Task.Run(() =>
        {
            try
            {
                var info = _multichannelService.DetectCurrentDeviceInfo(devNum);
                DispatcherQueue.TryEnqueue(() =>
                {
                    MultichannelSummaryLabel.Text = $"当前检测到：{info.DeviceName}（{info.ChannelTypeName}）";
                });
            }
            catch { }
        });
    }

    private void OpenMultichannelModal()
    {
        int myVersion = ++_multichannelModalAnimVersion;
        _isClosingMultichannelModal = false;

        SyncMultichannelConfigToUi();
        MultichannelModalOverlay.IsHitTestVisible = true;
        MultichannelModalOverlay.Visibility = Visibility.Visible;
        PlayModalDialogOpenAnimation(MultichannelModalOverlay, MultichannelModalCard, 250);

        // 后台异步侦测 WASAPI 音频端点能力，避免同步枚举声卡阻塞打开首帧动画
        _ = RefreshMultichannelModalUiAsync(myVersion);
    }

    private async Task CloseMultichannelModalAsync()
    {
        if (MultichannelModalOverlay.Visibility != Visibility.Visible || _isClosingMultichannelModal)
            return;

        int myVersion = ++_multichannelModalAnimVersion;
        _isClosingMultichannelModal = true;
        MultichannelModalOverlay.IsHitTestVisible = false;

        _surroundTourCts?.Cancel();
        _surroundTourCts = null;
        _multichannelService.StopTestTone();
        RunSurroundTourBtn.IsEnabled = true;

        await PlayModalDialogCloseAnimationAsync(MultichannelModalOverlay, MultichannelModalCard, 180);

        if (myVersion != _multichannelModalAnimVersion)
            return;

        MultichannelModalOverlay.Visibility = Visibility.Collapsed;
        MultichannelModalOverlay.IsHitTestVisible = true;
        _isClosingMultichannelModal = false;
    }

    private async void OnRefreshMultichannelDeviceClick(object sender, RoutedEventArgs e)
    {
        await RefreshMultichannelModalUiAsync(_multichannelModalAnimVersion);
        ViewModel.StatusToast = "已重新检测当前音频输出设备与声道能力";
    }

    private void OnOpenWindowsSoundPanelClick(object sender, RoutedEventArgs e)
    {
        MultichannelManagerService.OpenWindowsSoundControlPanel();
    }

    private async Task RefreshMultichannelModalUiAsync(int expectedVersion)
    {
        int devNum = ViewModel.Config.Settings.AudioOutputDeviceNumber;
        MultichannelDeviceInfo info;
        try
        {
            info = await Task.Run(() => _multichannelService.DetectCurrentDeviceInfo(devNum));
        }
        catch
        {
            return;
        }

        if (expectedVersion != _multichannelModalAnimVersion)
            return;

        ApplyMultichannelDeviceInfoToUi(info);
    }

    private void ApplyMultichannelDeviceInfoToUi(MultichannelDeviceInfo info)
    {
        MultichannelDeviceNameText.Text = info.DeviceName;
        MultichannelChannelBadgeText.Text = info.ChannelTypeName;
        MultichannelSampleRateBadgeText.Text = $"{info.SampleRate} Hz, {info.BitsPerSample}-bit Float";
        MultichannelDiagnosticText.Text = info.DiagnosticMessage;

        bool is71 = info.Channels >= 8;
        SpeakerBtnBackLeft.Visibility = is71 ? Visibility.Visible : Visibility.Collapsed;
        SpeakerBtnBackRight.Visibility = is71 ? Visibility.Visible : Visibility.Collapsed;
        ChannelRowBL.Visibility = is71 ? Visibility.Visible : Visibility.Collapsed;
        ChannelRowBR.Visibility = is71 ? Visibility.Visible : Visibility.Collapsed;

        if (info.Channels >= 6)
        {
            MultichannelChannelBadgeBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x35, 0x10, 0xB9, 0x81));
            MultichannelChannelBadgeText.Foreground = (Brush)Application.Current.Resources["BrandAccentBrush"];
        }
        else
        {
            MultichannelChannelBadgeBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x35, 0xF5, 0x9E, 0x0B));
            MultichannelChannelBadgeText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B));
        }

        MultichannelSummaryLabel.Text = $"当前检测到：{info.DeviceName}（{info.ChannelTypeName}）";
    }

    private void SyncMultichannelConfigToUi()
    {
        _isSyncingMultichannelUi = true;
        try
        {
            var cfg = ViewModel.Config.Settings.Multichannel;
            MultichannelEnableToggle.IsOn = cfg.IsEnabled;

        GainSliderFL.Value = cfg.FrontLeftDb;
        GainLabelFL.Text = FormatDb(cfg.FrontLeftDb);
        MuteToggleFL.IsOn = cfg.MuteFrontLeft;
        SpeakerBadgeFrontLeft.Text = FormatDb(cfg.FrontLeftDb);

        GainSliderFR.Value = cfg.FrontRightDb;
        GainLabelFR.Text = FormatDb(cfg.FrontRightDb);
        MuteToggleFR.IsOn = cfg.MuteFrontRight;
        SpeakerBadgeFrontRight.Text = FormatDb(cfg.FrontRightDb);

        GainSliderC.Value = cfg.CenterDb;
        GainLabelC.Text = FormatDb(cfg.CenterDb);
        MuteToggleC.IsOn = cfg.MuteCenter;
        SpeakerBadgeCenter.Text = FormatDb(cfg.CenterDb);

        GainSliderLFE.Value = cfg.SubwooferDb;
        GainLabelLFE.Text = FormatDb(cfg.SubwooferDb);
        MuteToggleLFE.IsOn = cfg.MuteSubwoofer;
        SpeakerBadgeSubwoofer.Text = FormatDb(cfg.SubwooferDb);

        GainSliderSL.Value = cfg.SurroundLeftDb;
        GainLabelSL.Text = FormatDb(cfg.SurroundLeftDb);
        MuteToggleSL.IsOn = cfg.MuteSurroundLeft;
        SpeakerBadgeSurroundLeft.Text = FormatDb(cfg.SurroundLeftDb);

        GainSliderSR.Value = cfg.SurroundRightDb;
        GainLabelSR.Text = FormatDb(cfg.SurroundRightDb);
        MuteToggleSR.IsOn = cfg.MuteSurroundRight;
        SpeakerBadgeSurroundRight.Text = FormatDb(cfg.SurroundRightDb);

        GainSliderBL.Value = cfg.BackLeftDb;
        GainLabelBL.Text = FormatDb(cfg.BackLeftDb);
        MuteToggleBL.IsOn = cfg.MuteBackLeft;
        SpeakerBadgeBackLeft.Text = FormatDb(cfg.BackLeftDb);

            GainSliderBR.Value = cfg.BackRightDb;
            GainLabelBR.Text = FormatDb(cfg.BackRightDb);
            MuteToggleBR.IsOn = cfg.MuteBackRight;
            SpeakerBadgeBackRight.Text = FormatDb(cfg.BackRightDb);
        }
        finally
        {
            _isSyncingMultichannelUi = false;
        }
    }

    // 先加 0f 消除 -0.0 导致的 "+-0.0dB" 显示
    private static string FormatDb(float db) { db = MathF.Round(db, 1) + 0f; return $"{(db >= 0 ? "+" : "")}{db:F1}dB"; }

    private async void OnSpeakerNodeButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || !int.TryParse(btn.Tag?.ToString(), out int channelIdx)) return;
        await TriggerSpeakerVisualAndAudioTestAsync(channelIdx, btn);
    }

    private async Task TriggerSpeakerVisualAndAudioTestAsync(int channelIdx, Button? btn = null)
    {
        btn ??= channelIdx switch
        {
            0 => SpeakerBtnFrontLeft,
            1 => SpeakerBtnFrontRight,
            2 => SpeakerBtnCenter,
            3 => SpeakerBtnSubwoofer,
            4 => SpeakerBtnSurroundLeft,
            5 => SpeakerBtnSurroundRight,
            6 => SpeakerBtnBackLeft,
            7 => SpeakerBtnBackRight,
            _ => null
        };

        if (btn != null)
        {
            var origBg = btn.Background;
            var origBorder = btn.BorderBrush;
            btn.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x10, 0xB9, 0x81));
            btn.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x34, 0xD3, 0x99));
            _ = Task.Delay(550).ContinueWith(_ =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    btn.Background = origBg;
                    btn.BorderBrush = origBorder;
                });
            });
        }

        int devNum = ViewModel.Config.Settings.AudioOutputDeviceNumber;
        var info = await Task.Run(() => _multichannelService.DetectCurrentDeviceInfo(devNum));
        await _multichannelService.PlaySingleSpeakerTestAsync(channelIdx, info.Channels >= 8 ? 8 : 6);
    }

    private async void OnRunSurroundTourClick(object sender, RoutedEventArgs e)
    {
        _surroundTourCts?.Cancel();
        _surroundTourCts = new CancellationTokenSource();
        var ct = _surroundTourCts.Token;

        int devNum = ViewModel.Config.Settings.AudioOutputDeviceNumber;
        var info = await Task.Run(() => _multichannelService.DetectCurrentDeviceInfo(devNum));
        int[] speakerSequence = info.Channels >= 8
            ? [0, 2, 1, 5, 7, 6, 4, 3]  // FL -> C -> FR -> SR -> BR -> BL -> SL -> LFE
            : [0, 2, 1, 5, 4, 3];        // FL -> C -> FR -> SR -> SL -> LFE

        try
        {
            RunSurroundTourBtn.IsEnabled = false;
            foreach (int spk in speakerSequence)
            {
                if (ct.IsCancellationRequested) break;
                await TriggerSpeakerVisualAndAudioTestAsync(spk);
                await Task.Delay(650, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            RunSurroundTourBtn.IsEnabled = true;
        }
    }

    private void OnStopSurroundTourClick(object sender, RoutedEventArgs e)
    {
        _surroundTourCts?.Cancel();
        _surroundTourCts = null;
        _multichannelService.StopTestTone();
        RunSurroundTourBtn.IsEnabled = true;
    }

    private void OnMultichannelEnableToggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || _isSyncingMultichannelUi) return;
        var cfg = ViewModel.Config.Settings.Multichannel;
        cfg.IsEnabled = MultichannelEnableToggle.IsOn;
        ViewModel.Player.UpdateMultichannelConfig(cfg);
    }

    private void OnChannelGainChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isInitialized || _isSyncingMultichannelUi) return;
        var cfg = ViewModel.Config.Settings.Multichannel;
        float val = (float)(Math.Round(e.NewValue * 2) / 2.0); // 0.5dB 步进

        if (ReferenceEquals(sender, GainSliderFL)) { cfg.FrontLeftDb = val; GainLabelFL.Text = FormatDb(val); SpeakerBadgeFrontLeft.Text = FormatDb(val); }
        else if (ReferenceEquals(sender, GainSliderFR)) { cfg.FrontRightDb = val; GainLabelFR.Text = FormatDb(val); SpeakerBadgeFrontRight.Text = FormatDb(val); }
        else if (ReferenceEquals(sender, GainSliderC)) { cfg.CenterDb = val; GainLabelC.Text = FormatDb(val); SpeakerBadgeCenter.Text = FormatDb(val); }
        else if (ReferenceEquals(sender, GainSliderLFE)) { cfg.SubwooferDb = val; GainLabelLFE.Text = FormatDb(val); SpeakerBadgeSubwoofer.Text = FormatDb(val); }
        else if (ReferenceEquals(sender, GainSliderSL)) { cfg.SurroundLeftDb = val; GainLabelSL.Text = FormatDb(val); SpeakerBadgeSurroundLeft.Text = FormatDb(val); }
        else if (ReferenceEquals(sender, GainSliderSR)) { cfg.SurroundRightDb = val; GainLabelSR.Text = FormatDb(val); SpeakerBadgeSurroundRight.Text = FormatDb(val); }
        else if (ReferenceEquals(sender, GainSliderBL)) { cfg.BackLeftDb = val; GainLabelBL.Text = FormatDb(val); SpeakerBadgeBackLeft.Text = FormatDb(val); }
        else if (ReferenceEquals(sender, GainSliderBR)) { cfg.BackRightDb = val; GainLabelBR.Text = FormatDb(val); SpeakerBadgeBackRight.Text = FormatDb(val); }

        ViewModel.Player.UpdateMultichannelConfig(cfg);
    }

    private void OnChannelMuteToggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || _isSyncingMultichannelUi) return;
        var cfg = ViewModel.Config.Settings.Multichannel;

        if (ReferenceEquals(sender, MuteToggleFL)) cfg.MuteFrontLeft = MuteToggleFL.IsOn;
        else if (ReferenceEquals(sender, MuteToggleFR)) cfg.MuteFrontRight = MuteToggleFR.IsOn;
        else if (ReferenceEquals(sender, MuteToggleC)) cfg.MuteCenter = MuteToggleC.IsOn;
        else if (ReferenceEquals(sender, MuteToggleLFE)) cfg.MuteSubwoofer = MuteToggleLFE.IsOn;
        else if (ReferenceEquals(sender, MuteToggleSL)) cfg.MuteSurroundLeft = MuteToggleSL.IsOn;
        else if (ReferenceEquals(sender, MuteToggleSR)) cfg.MuteSurroundRight = MuteToggleSR.IsOn;
        else if (ReferenceEquals(sender, MuteToggleBL)) cfg.MuteBackLeft = MuteToggleBL.IsOn;
        else if (ReferenceEquals(sender, MuteToggleBR)) cfg.MuteBackRight = MuteToggleBR.IsOn;

        ViewModel.Player.UpdateMultichannelConfig(cfg);
    }

    private void OnResetMultichannelCalibrationClick(object sender, RoutedEventArgs e)
    {
        var cfg = ViewModel.Config.Settings.Multichannel;
        cfg.ResetToDefaults();
        SyncMultichannelConfigToUi();
        ViewModel.Player.UpdateMultichannelConfig(cfg);
        ViewModel.StatusToast = "已恢复所有音箱微调为 0.0 dB";
    }

    private async void OnMusicSourceCardTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!_isInitialized || sender is not Border card || card.Tag is not string sourceKey) return;

        var selected = ViewModel.Config.Settings.EnabledMusicSources.ToList();
        bool isSelected = selected.Any(x => string.Equals(x, sourceKey, StringComparison.OrdinalIgnoreCase));

        // 1:1 对齐 MusicSourceSettings.vue toggleSource 校验逻辑
        if (string.Equals(sourceKey, "custom", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(ViewModel.Config.Settings.CustomUnblockApiUrl))
        {
            ViewModel.StatusToast = "请先在「自定义API」标签页配置接口";
            SwitchMusicSourceModalTab("customApi");
            return;
        }

        if (string.Equals(sourceKey, "lxMusic", StringComparison.OrdinalIgnoreCase) &&
            ViewModel.LxScriptsList.Count == 0)
        {
            ViewModel.StatusToast = "请先在「洛雪音源」标签页导入音源脚本";
            SwitchMusicSourceModalTab("lxMusic");
            return;
        }

        if (isSelected)
        {
            if (selected.Count <= 1)
            {
                ViewModel.StatusToast = "至少需要保留一个音源";
                return;
            }
            selected.RemoveAll(x => string.Equals(x, sourceKey, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            selected.Add(sourceKey);
        }

        ViewModel.Config.Settings.EnabledMusicSources = selected;
        SyncMusicSourceCardsUi();
        await ViewModel.Config.SaveSettingsAsync();
    }

    private void SyncMusicSourceCardsUi()
    {
        var s = ViewModel.Config.Settings;
        var selected = s.EnabledMusicSources.Select(x => x.ToLowerInvariant()).ToHashSet();

        UpdateSingleMusicSourceCard(
            SrcCard_migu, SrcIconBg_migu, SrcIconPath_migu, SrcCheckBg_migu, SrcCheckIcon_migu,
            selected.Contains("migu"), Windows.UI.Color.FromArgb(255, 255, 102, 0), true);
        UpdateSingleMusicSourceCard(
            SrcCard_kugou, SrcIconBg_kugou, SrcIconPath_kugou, SrcCheckBg_kugou, SrcCheckIcon_kugou,
            selected.Contains("kugou"), Windows.UI.Color.FromArgb(255, 41, 121, 255), true);
        UpdateSingleMusicSourceCard(
            SrcCard_kuwo, SrcIconBg_kuwo, SrcIconPath_kuwo, SrcCheckBg_kuwo, SrcCheckIcon_kuwo,
            selected.Contains("kuwo"), Windows.UI.Color.FromArgb(255, 255, 140, 0), true);
        UpdateSingleMusicSourceCard(
            SrcCard_pyncmd, SrcIconBg_pyncmd, SrcIconPath_pyncmd, SrcCheckBg_pyncmd, SrcCheckIcon_pyncmd,
            selected.Contains("pyncmd"), Windows.UI.Color.FromArgb(255, 236, 65, 65), true);
        UpdateSingleMusicSourceCard(
            SrcCard_gdmusic, SrcIconBg_gdmusic, SrcIconPath_gdmusic, SrcCheckBg_gdmusic, SrcCheckIcon_gdmusic,
            selected.Contains("gdmusic"), Windows.UI.Color.FromArgb(255, 66, 133, 244), true);

        var activeLx = ViewModel.LxScriptsList.FirstOrDefault(x => x.Id == s.ActiveLxMusicApiId) ?? ViewModel.LxScriptsList.FirstOrDefault();
        bool lxAvailable = activeLx != null;
        SrcSubText_lxMusic.Text = activeLx != null ? activeLx.Name : "未配置";
        UpdateSingleMusicSourceCard(
            SrcCard_lxMusic, SrcIconBg_lxMusic, SrcIconPath_lxMusic, SrcCheckBg_lxMusic, SrcCheckIcon_lxMusic,
            selected.Contains("lxmusic"), Windows.UI.Color.FromArgb(255, 34, 197, 94), lxAvailable);

        bool customAvailable = !string.IsNullOrWhiteSpace(s.CustomUnblockApiUrl);
        SrcSubText_custom.Text = customAvailable ? "已导入" : "未导入";
        UpdateSingleMusicSourceCard(
            SrcCard_custom, SrcIconBg_custom, SrcIconPath_custom, SrcCheckBg_custom, SrcCheckIcon_custom,
            selected.Contains("custom"), Windows.UI.Color.FromArgb(255, 139, 92, 246), customAvailable);

        SelectedSourcesSummaryText.Text = s.EnabledMusicSources.Count > 0
            ? $"已选音源：{string.Join(", ", s.EnabledMusicSources)}"
            : "未选择音源";
    }

    private void UpdateSingleMusicSourceCard(
        Border card,
        Border iconBg,
        Microsoft.UI.Xaml.Shapes.Path iconPath,
        Border checkBg,
        FontIcon checkIcon,
        bool isSelected,
        Windows.UI.Color brandColor,
        bool isAvailable)
    {
        var accent = GetActiveThemeAccentColor();
        var brandBrush = new SolidColorBrush(brandColor);
        var whiteBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
        var accentBrush = new SolidColorBrush(accent);

        card.Opacity = isAvailable ? 1.0 : 0.6;
        if (isSelected)
        {
            card.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(26, accent.R, accent.G, accent.B));
            card.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(100, accent.R, accent.G, accent.B));
            iconBg.Background = brandBrush;
            iconPath.Fill = whiteBrush;
            checkBg.Background = accentBrush;
            checkBg.BorderBrush = accentBrush;
            checkIcon.Visibility = Visibility.Visible;
        }
        else
        {
            card.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(14, 148, 163, 184));
            card.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(36, 148, 163, 184));
            iconBg.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(24, 148, 163, 184));
            iconPath.Fill = brandBrush;
            checkBg.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            checkBg.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(110, 148, 163, 184));
            checkIcon.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnImportLocalLxScriptClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string? selectedPath = await Helpers.Win32Interop.PickOpenFileAsync(
                GetDialogOwnerHwnd(),
                "导入本地洛雪音源脚本",
                "JavaScript 脚本 (*.js)|*.js|所有文件 (*.*)|*.*");
            if (string.IsNullOrWhiteSpace(selectedPath)) return;

            var content = await File.ReadAllTextAsync(selectedPath);
            var scriptItem = Services.Api.NeteaseApiClient.ParseLxScriptHeader(content, Path.GetFileNameWithoutExtension(selectedPath));
            ViewModel.Config.Settings.LxMusicScripts.Add(scriptItem);
            ViewModel.Config.Settings.ActiveLxMusicApiId = scriptItem.Id;
            ViewModel.LxScriptsList.Add(scriptItem);
            if (!ViewModel.Config.Settings.EnabledMusicSources.Any(x => string.Equals(x, "lxMusic", StringComparison.OrdinalIgnoreCase)))
            {
                ViewModel.Config.Settings.EnabledMusicSources.Add("lxMusic");
            }
            SyncMusicSourceCardsUi();
            await ViewModel.Config.SaveSettingsAsync();
            ViewModel.StatusToast = $"已成功导入洛雪音源脚本：{scriptItem.Name} ({scriptItem.Version})";
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"导入洛雪脚本失败：{ex.Message}";
        }
    }

    private async void OnImportUrlLxScriptClick(object sender, RoutedEventArgs e)
    {
        var url = LxScriptUrlInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            ViewModel.StatusToast = "请输入有效的 http/https 洛雪音源脚本 URL";
            return;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            var content = await http.GetStringAsync(url);
            var fallbackName = Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);
            var scriptItem = Services.Api.NeteaseApiClient.ParseLxScriptHeader(content, string.IsNullOrWhiteSpace(fallbackName) ? "在线洛雪音源" : fallbackName);
            ViewModel.Config.Settings.LxMusicScripts.Add(scriptItem);
            ViewModel.Config.Settings.ActiveLxMusicApiId = scriptItem.Id;
            ViewModel.LxScriptsList.Add(scriptItem);
            LxScriptUrlInputBox.Text = string.Empty;
            if (!ViewModel.Config.Settings.EnabledMusicSources.Any(x => string.Equals(x, "lxMusic", StringComparison.OrdinalIgnoreCase)))
            {
                ViewModel.Config.Settings.EnabledMusicSources.Add("lxMusic");
            }
            SyncMusicSourceCardsUi();
            await ViewModel.Config.SaveSettingsAsync();
            ViewModel.StatusToast = $"已从 URL 导入洛雪音源：{scriptItem.Name} ({scriptItem.Version})";
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"在线导入洛雪脚本失败：{ex.Message}";
        }
    }

    private async void OnRemoveLxScriptClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is LxScriptItem item)
        {
            ViewModel.LxScriptsList.Remove(item);
            ViewModel.Config.Settings.LxMusicScripts.RemoveAll(x => x.Id == item.Id);
            if (ViewModel.LxScriptsList.Count == 0)
            {
                ViewModel.Config.Settings.ActiveLxMusicApiId = string.Empty;
                ViewModel.Config.Settings.EnabledMusicSources.RemoveAll(x => string.Equals(x, "lxMusic", StringComparison.OrdinalIgnoreCase));
            }
            SyncMusicSourceCardsUi();
            await ViewModel.Config.SaveSettingsAsync();
            ViewModel.StatusToast = $"已移除洛雪脚本：{item.Name}";
        }
    }

    private async void OnSaveCustomUnblockUrlClick(object sender, RoutedEventArgs e)
    {
        ViewModel.Config.Settings.CustomUnblockApiUrl = CustomUnblockUrlBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(ViewModel.Config.Settings.CustomUnblockApiUrl) &&
            !ViewModel.Config.Settings.EnabledMusicSources.Any(x => string.Equals(x, "custom", StringComparison.OrdinalIgnoreCase)))
        {
            ViewModel.Config.Settings.EnabledMusicSources.Add("custom");
        }
        SyncMusicSourceCardsUi();
        await ViewModel.Config.SaveSettingsAsync();
        ViewModel.StatusToast = "已保存自定义解灰接口配置";
    }

    private async void OnAudioDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || AudioDeviceComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not int deviceNumber) return;
        ViewModel.Config.Settings.AudioOutputDeviceNumber = deviceNumber;
        await ViewModel.Config.SaveSettingsAsync();
        ViewModel.StatusToast = $"音频输出设备已切换为：{item.Content}";
    }

    private void OnRefreshAudioDevicesClick(object sender, RoutedEventArgs e)
    {
        RefreshAudioDevicesComboBox(ViewModel.Config.Settings.AudioOutputDeviceNumber);
        ViewModel.StatusToast = "已刷新系统音频输出设备列表";
    }

    private async void OnTestAudioDeviceClick(object sender, RoutedEventArgs e)
    {
        var dev = ViewModel.Config.Settings.AudioOutputDeviceNumber;
        ViewModel.StatusToast = "正在播放 440Hz 声道测试蜂鸣音...";
        await ViewModel.Player.PlayTestToneAsync(dev);
        ViewModel.StatusToast = "测试音播放完毕";
    }

    private async void OnCloseActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || CloseActionComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string action) return;
        ViewModel.Config.Settings.CloseAction = action;
        await ViewModel.Config.SaveSettingsAsync();
    }

    private async void OnAlwaysShowDownloadToggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        ViewModel.Config.Settings.AlwaysShowDownloadButton = AlwaysShowDownloadToggle.IsOn;
        AlwaysShowDownloadStateLabel.Text = AlwaysShowDownloadToggle.IsOn ? "显示" : "隐藏";
        await ViewModel.Config.SaveSettingsAsync();
    }

    private async void OnUnlimitedDownloadToggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        ViewModel.Config.Settings.UnlimitedDownload = UnlimitedDownloadToggle.IsOn;
        UnlimitedDownloadStateLabel.Text = UnlimitedDownloadToggle.IsOn ? "开启" : "关闭";
        await ViewModel.Config.SaveSettingsAsync();
    }

    private async void OnEmbedTagToggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        ViewModel.Config.Settings.EmbedCoverAndLyricOnDownload = EmbedTagToggle.IsOn;
        await ViewModel.Config.SaveSettingsAsync();
    }

    private async void OnPickDownloadFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string? folderPath = await Helpers.Win32Interop.PickFolderAsync(GetDialogOwnerHwnd(), "选择音乐下载保存目录");
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                DownloadPathBox.Text = folderPath;
                DownloadPathDisplayText.Text = folderPath;
                ViewModel.Config.Settings.DownloadPath = folderPath;
                await ViewModel.Config.SaveSettingsAsync();
                ViewModel.StatusToast = $"下载目录已更新为：{folderPath}";
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"选择目录失败：{ex.Message}";
        }
    }

    // ==================== 6 大原版设置弹窗与快捷键录制器 (ShortcutSettings / MusicSource / Cookie / Remote / Proxy / ClearCache) ====================

    private static readonly HashSet<string> ReservedSystemShortcuts = new(StringComparer.OrdinalIgnoreCase)
    {
        "CommandOrControl+C",
        "CommandOrControl+V",
        "CommandOrControl+X",
        "CommandOrControl+A",
        "CommandOrControl+Z",
        "CommandOrControl+Y",
        "CommandOrControl+W",
        "CommandOrControl+Q",
        "Alt+F4",
        "F12"
    };

    private void OnOpenShortcutModalClick(object sender, RoutedEventArgs e)
    {
        _recordingShortcutItem = null;
        ViewModel.Hotkeys.IsSuspended = true;

        var normalized = AppSettings.NormalizeShortcuts(ViewModel.Config.Settings.Shortcuts);
        _shortcutDraftItems = normalized.Select(s => s.Clone()).ToList();
        BindShortcutDraftGroups();
        ValidateShortcutDrafts();
        ShortcutRegistrationWarningBorder.Visibility = Visibility.Collapsed;

        ShortcutSettingsModalOverlay.Visibility = Visibility.Visible;
        PlayEntranceAnimation(ShortcutSettingsModalOverlay, 0, 14, 220);
    }

    private void OnCloseShortcutModalClick(object sender, RoutedEventArgs e)
    {
        if (_recordingShortcutItem != null)
        {
            _recordingShortcutItem.IsRecording = false;
            _recordingShortcutItem = null;
        }
        ViewModel.Hotkeys.IsSuspended = false;
        ShortcutSettingsModalOverlay.Visibility = Visibility.Collapsed;
    }

    private void BindShortcutDraftGroups()
    {
        ShortcutsPlaybackItemsControl.ItemsSource = _shortcutDraftItems.Where(x => x.GroupKey == "playback").ToList();
        ShortcutsSoundItemsControl.ItemsSource = _shortcutDraftItems.Where(x => x.GroupKey == "sound").ToList();
        ShortcutsWindowItemsControl.ItemsSource = _shortcutDraftItems.Where(x => x.GroupKey == "window").ToList();
    }

    private void OnShortcutRecorderButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ShortcutItem item) return;

        if (_recordingShortcutItem == item)
        {
            item.IsRecording = false;
            _recordingShortcutItem = null;
            return;
        }

        if (_recordingShortcutItem != null)
        {
            _recordingShortcutItem.IsRecording = false;
        }

        _recordingShortcutItem = item;
        item.IsRecording = true;
        btn.Focus(FocusState.Programmatic);
    }

    private void OnShortcutRecorderButtonLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ShortcutItem item && _recordingShortcutItem == item)
        {
            item.IsRecording = false;
            _recordingShortcutItem = null;
        }
    }

    private void OnShortcutRecorderButtonKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_recordingShortcutItem == null) return;
        e.Handled = true;

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            _recordingShortcutItem.IsRecording = false;
            _recordingShortcutItem = null;
            return;
        }

        bool ctrl = IsModifierKeyDown(Windows.System.VirtualKey.Control);
        bool alt = IsModifierKeyDown(Windows.System.VirtualKey.Menu);
        bool shift = IsModifierKeyDown(Windows.System.VirtualKey.Shift);

        if ((e.Key == Windows.System.VirtualKey.Back || e.Key == Windows.System.VirtualKey.Delete) && !ctrl && !alt && !shift)
        {
            _recordingShortcutItem.Enabled = false;
            _recordingShortcutItem.IsRecording = false;
            _recordingShortcutItem = null;
            ValidateShortcutDrafts();
            return;
        }

        if (!TryFormatMainShortcutKey(e.Key, out string mainKey))
        {
            return;
        }

        bool isFunctionOrMedia = mainKey.StartsWith('F') && mainKey.Length is >= 2 and <= 3 && int.TryParse(mainKey[1..], out _) ||
                                 mainKey.StartsWith("Media", StringComparison.OrdinalIgnoreCase);

        if (!isFunctionOrMedia && !ctrl && !alt && !shift)
        {
            _recordingShortcutItem.IssueText = "需要配合修饰键";
            return;
        }

        var parts = new List<string>();
        if (ctrl) parts.Add("CommandOrControl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        parts.Add(mainKey);

        _recordingShortcutItem.Key = string.Join("+", parts);
        _recordingShortcutItem.Enabled = true;
        _recordingShortcutItem.IsRecording = false;
        _recordingShortcutItem = null;
        ValidateShortcutDrafts();
    }

    private void OnRestoreSingleShortcutClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ShortcutItem item) return;
        var def = AppSettings.GetDefaultShortcuts().FirstOrDefault(x => x.ActionId == item.ActionId);
        if (def != null)
        {
            item.Key = def.Key;
            item.Enabled = def.Enabled;
            item.Scope = def.Scope;
            item.IsRecording = false;
            if (_recordingShortcutItem == item) _recordingShortcutItem = null;
            ValidateShortcutDrafts();
        }
    }

    private void OnShortcutDraftItemChanged(object sender, object e)
    {
        if (!_isInitialized || ShortcutSettingsModalOverlay?.Visibility != Visibility.Visible) return;
        ValidateShortcutDrafts();
    }

    private void ValidateShortcutDrafts()
    {
        if (_shortcutDraftItems.Count == 0) return;

        foreach (var item in _shortcutDraftItems)
        {
            item.IssueText = string.Empty;
        }

        var enabledItems = _shortcutDraftItems.Where(x => x.Enabled).ToList();
        foreach (var item in enabledItems)
        {
            if (string.IsNullOrWhiteSpace(item.Key))
            {
                item.IssueText = "快捷键格式无效";
                continue;
            }

            if (ReservedSystemShortcuts.Contains(item.Key.Trim()))
            {
                item.IssueText = "系统保留快捷键";
            }
        }

        var groups = enabledItems
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .GroupBy(x => x.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var dupGroup in groups)
        {
            var list = dupGroup.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var other = list[(i + 1) % list.Count];
                list[i].IssueText = $"与“{other.DisplayName}”冲突";
            }
        }

        int issueCount = _shortcutDraftItems.Count(x => !string.IsNullOrWhiteSpace(x.IssueText));
        ShortcutEnabledCountText.Text = $"已启用: {enabledItems.Count}";
        ShortcutSummaryText.Text = issueCount > 0
            ? $"存在 {issueCount} 个问题需要处理"
            : "当前快捷键配置可保存";
        SaveShortcutsModalBtn.IsEnabled = issueCount == 0;
    }

    private void OnEnableAllShortcutsClick(object sender, RoutedEventArgs e)
    {
        foreach (var s in _shortcutDraftItems) s.Enabled = true;
        ValidateShortcutDrafts();
    }

    private void OnDisableAllShortcutsClick(object sender, RoutedEventArgs e)
    {
        foreach (var s in _shortcutDraftItems) s.Enabled = false;
        ValidateShortcutDrafts();
    }

    private void OnResetShortcutsClick(object sender, RoutedEventArgs e)
    {
        _shortcutDraftItems = AppSettings.GetDefaultShortcuts().Select(x => x.Clone()).ToList();
        BindShortcutDraftGroups();
        ValidateShortcutDrafts();
        ShortcutRegistrationWarningBorder.Visibility = Visibility.Collapsed;
    }

    private async void OnSaveShortcutsClick(object sender, RoutedEventArgs e)
    {
        ValidateShortcutDrafts();
        if (_shortcutDraftItems.Any(x => !string.IsNullOrWhiteSpace(x.IssueText)))
        {
            return;
        }

        ViewModel.ShortcutsList.Clear();
        foreach (var d in _shortcutDraftItems)
        {
            ViewModel.ShortcutsList.Add(d.Clone());
        }

        ViewModel.Config.Settings.Shortcuts = ViewModel.ShortcutsList.Select(x => x.Clone()).ToList();
        ViewModel.Hotkeys.IsSuspended = false;
        var failed = ViewModel.Hotkeys.ReloadHotkeys();
        await ViewModel.Config.SaveSettingsAsync();

        if (failed.Count > 0)
        {
            ViewModel.Hotkeys.IsSuspended = true;
            ShortcutRegistrationWarningBorder.Visibility = Visibility.Visible;
            ShortcutRegistrationWarningText.Text = string.Join("；", failed.Select(f => $"{f.DisplayName} ({ShortcutItem.FormatKeyForDisplay(f.Key)})"));
            return;
        }

        ShortcutSettingsModalOverlay.Visibility = Visibility.Collapsed;
        ViewModel.StatusToast = "快捷键设置已保存";
    }

    private static bool IsModifierKeyDown(Windows.System.VirtualKey key)
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    private static bool TryFormatMainShortcutKey(Windows.System.VirtualKey key, out string token)
    {
        token = string.Empty;
        if (key is Windows.System.VirtualKey.Control or Windows.System.VirtualKey.LeftControl or Windows.System.VirtualKey.RightControl
            or Windows.System.VirtualKey.Menu or Windows.System.VirtualKey.LeftMenu or Windows.System.VirtualKey.RightMenu
            or Windows.System.VirtualKey.Shift or Windows.System.VirtualKey.LeftShift or Windows.System.VirtualKey.RightShift
            or Windows.System.VirtualKey.LeftWindows or Windows.System.VirtualKey.RightWindows)
        {
            return false;
        }

        if (key >= Windows.System.VirtualKey.A && key <= Windows.System.VirtualKey.Z)
        {
            token = key.ToString().ToUpperInvariant();
            return true;
        }

        if (key >= Windows.System.VirtualKey.Number0 && key <= Windows.System.VirtualKey.Number9)
        {
            token = ((int)key - (int)Windows.System.VirtualKey.Number0).ToString();
            return true;
        }

        if (key >= Windows.System.VirtualKey.F1 && key <= Windows.System.VirtualKey.F24)
        {
            token = key.ToString().ToUpperInvariant();
            return true;
        }

        token = key switch
        {
            Windows.System.VirtualKey.Space => "Space",
            Windows.System.VirtualKey.Left => "Left",
            Windows.System.VirtualKey.Right => "Right",
            Windows.System.VirtualKey.Up => "Up",
            Windows.System.VirtualKey.Down => "Down",
            Windows.System.VirtualKey.Enter => "Enter",
            Windows.System.VirtualKey.Tab => "Tab",
            Windows.System.VirtualKey.Home => "Home",
            Windows.System.VirtualKey.End => "End",
            Windows.System.VirtualKey.PageUp => "PageUp",
            Windows.System.VirtualKey.PageDown => "PageDown",
            Windows.System.VirtualKey.Insert => "Insert",
            (Windows.System.VirtualKey)187 or Windows.System.VirtualKey.Add => "Plus",
            (Windows.System.VirtualKey)189 or Windows.System.VirtualKey.Subtract => "Minus",
            (Windows.System.VirtualKey)0xB3 => "MediaPlayPause",
            (Windows.System.VirtualKey)0xB0 => "MediaNextTrack",
            (Windows.System.VirtualKey)0xB1 => "MediaPreviousTrack",
            (Windows.System.VirtualKey)0xB2 => "MediaStop",
            _ => string.Empty
        };

        return !string.IsNullOrEmpty(token);
    }

    private void OnRootLayoutKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ShortcutSettingsModalOverlay.Visibility == Visibility.Visible || _recordingShortcutItem != null)
        {
            return;
        }

        if (e.OriginalSource is TextBox or PasswordBox or RichEditBox or AutoSuggestBox)
        {
            return;
        }

        if (!TryFormatMainShortcutKey(e.Key, out string mainKey))
        {
            return;
        }

        bool ctrl = IsModifierKeyDown(Windows.System.VirtualKey.Control);
        bool alt = IsModifierKeyDown(Windows.System.VirtualKey.Menu);
        bool shift = IsModifierKeyDown(Windows.System.VirtualKey.Shift);

        var parts = new List<string>();
        if (ctrl) parts.Add("CommandOrControl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        parts.Add(mainKey);

        string pressed = string.Join("+", parts);
        var match = ViewModel.Config.Settings.Shortcuts.FirstOrDefault(s =>
            s.Enabled &&
            string.Equals(s.Scope, "app", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(NormalizeShortcutComparisonKey(s.Key), NormalizeShortcutComparisonKey(pressed), StringComparison.OrdinalIgnoreCase));

        if (match != null)
        {
            e.Handled = true;
            ExecuteShortcutAction(match.ActionId);
        }
    }

    private static string NormalizeShortcutComparisonKey(string raw)
    {
        return (raw ?? string.Empty)
            .Replace("Ctrl+", "CommandOrControl+", StringComparison.OrdinalIgnoreCase)
            .Replace("Control+", "CommandOrControl+", StringComparison.OrdinalIgnoreCase)
            .Replace("ArrowLeft", "Left", StringComparison.OrdinalIgnoreCase)
            .Replace("ArrowRight", "Right", StringComparison.OrdinalIgnoreCase)
            .Replace("ArrowUp", "Up", StringComparison.OrdinalIgnoreCase)
            .Replace("ArrowDown", "Down", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    // --- 模态弹窗 2：音源设置 (MusicSourceSettings.vue) ---
    private void OnOpenMusicSourcesModalClick(object sender, RoutedEventArgs e)
    {
        SyncMusicSourceCardsUi();
        SwitchMusicSourceModalTab("sources");
        MusicSourcesModalOverlay.Visibility = Visibility.Visible;
        PlayEntranceAnimation(MusicSourcesModalOverlay, 0, 14, 220);
    }

    private void OnCloseMusicSourcesModalClick(object sender, RoutedEventArgs e)
    {
        MusicSourcesModalOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnMusicSourceModalTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tab) return;
        SwitchMusicSourceModalTab(tab);
    }

    private void SwitchMusicSourceModalTab(string tab)
    {
        MusicSourceModalSourcesPanel.Visibility = tab == "sources" ? Visibility.Visible : Visibility.Collapsed;
        MusicSourceModalLxPanel.Visibility = tab == "lxMusic" ? Visibility.Visible : Visibility.Collapsed;
        MusicSourceModalCustomPanel.Visibility = tab == "customApi" ? Visibility.Visible : Visibility.Collapsed;

        ApplyDynamicAccentButtonState(MusicSourceTabSourcesBtn, tab == "sources");
        ApplyDynamicAccentButtonState(MusicSourceTabLxBtn, tab == "lxMusic");
        ApplyDynamicAccentButtonState(MusicSourceTabCustomBtn, tab == "customApi");
    }

    // --- 模态弹窗 3：Cookie设置 (CookieSettingsModal.vue) ---
    private void OnOpenCookieSettingsModalClick(object sender, RoutedEventArgs e)
    {
        var currentCookie = ViewModel.Config.Settings.NeteaseCookie ?? string.Empty;
        ModalCookieInputBox.Text = currentCookie;
        ModalCookieLengthText.Text = $"当前长度：{currentCookie.Length} 字符";
        CookieSettingsModalOverlay.Visibility = Visibility.Visible;
        PlayEntranceAnimation(CookieSettingsModalOverlay, 0, 14, 220);
    }

    private void OnCloseCookieSettingsModalClick(object sender, RoutedEventArgs e)
    {
        CookieSettingsModalOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnModalCookieInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized || ModalCookieLengthText == null || ModalCookieInputBox == null) return;
        ModalCookieLengthText.Text = $"当前长度：{(ModalCookieInputBox.Text?.Length ?? 0)} 字符";
    }

    private async void OnPasteModalCookieClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    ModalCookieInputBox.Text = text.Trim();
                    ViewModel.StatusToast = "已从剪贴板粘贴Cookie";
                }
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"读取剪贴板失败：{ex.Message}";
        }
    }

    private void OnClearModalCookieClick(object sender, RoutedEventArgs e)
    {
        ModalCookieInputBox.Text = string.Empty;
    }

    private async void OnSaveModalCookieClick(object sender, RoutedEventArgs e)
    {
        var val = ModalCookieInputBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(val))
        {
            ViewModel.StatusToast = "请输入有效的Cookie";
            return;
        }

        ViewModel.Config.Settings.NeteaseCookie = val;
        ViewModel.Config.Settings.LoginType = "cookie";
        CookieInputBox.Text = val;
        await ViewModel.Config.SaveSettingsAsync();
        SyncCookieSettingsRowUi();
        CookieSettingsModalOverlay.Visibility = Visibility.Collapsed;
        await ViewModel.RefreshUserProfileAsync();
        ViewModel.StatusToast = "Cookie已保存";
    }

    // --- 模态弹窗 4：远程控制 (ServerSetting.vue) ---
    private void OnOpenRemoteControlModalClick(object sender, RoutedEventArgs e)
    {
        RemoteControlToggle.IsOn = ViewModel.Config.Settings.EnableRemoteControl;
        RemoteControlPortBox.Text = ViewModel.Config.Settings.RemoteControlPort.ToString();
        RemoteAllowedIpsBox.Text = string.Join(", ", ViewModel.Config.Settings.RemoteControlAllowedIps ?? []);
        RemoteControlUrlText.Text = ViewModel.Config.Settings.EnableRemoteControl
            ? ViewModel.RemoteControl.AccessUrl
            : $"http://localhost:{ViewModel.Config.Settings.RemoteControlPort}/";
        RemoteControlModalOverlay.Visibility = Visibility.Visible;
        PlayEntranceAnimation(RemoteControlModalOverlay, 0, 14, 220);
    }

    private void OnCloseRemoteControlModalClick(object sender, RoutedEventArgs e)
    {
        RemoteControlModalOverlay.Visibility = Visibility.Collapsed;
    }

    private async void OnResetRemoteControlClick(object sender, RoutedEventArgs e)
    {
        RemoteControlToggle.IsOn = false;
        RemoteControlPortBox.Text = "31888";
        RemoteAllowedIpsBox.Text = string.Empty;
        ViewModel.Config.Settings.EnableRemoteControl = false;
        ViewModel.Config.Settings.RemoteControlPort = 31888;
        ViewModel.Config.Settings.RemoteControlAllowedIps = [];
        ViewModel.RemoteControl.Stop();
        RemoteControlUrlText.Text = "http://localhost:31888/";
        await ViewModel.Config.SaveSettingsAsync();
        ViewModel.StatusToast = "远程控制配置已重置为默认值";
    }

    private async void OnApplyRemotePortClick(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(RemoteControlPortBox.Text.Trim(), out var port) && port is >= 1024 and <= 65535)
        {
            ViewModel.Config.Settings.RemoteControlPort = port;
            ViewModel.Config.Settings.RemoteControlAllowedIps = RemoteAllowedIpsBox.Text
                .Split([',', ';', '，', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            await ViewModel.Config.SaveSettingsAsync();
            if (RemoteControlToggle.IsOn)
            {
                bool started = ViewModel.RemoteControl.Start();
                RemoteControlUrlText.Text = started
                    ? ViewModel.RemoteControl.AccessUrl
                    : "启动失败（端口可能被占用）";
            }
            else
            {
                RemoteControlUrlText.Text = $"http://localhost:{port}/";
            }
            RemoteControlModalOverlay.Visibility = Visibility.Collapsed;
            ViewModel.StatusToast = $"远程控制配置已保存（端口 {port}）";
        }
        else
        {
            ViewModel.StatusToast = "端口号需在 1024 ~ 65535 之间";
        }
    }

    // --- 模态弹窗 5：代理设置 (ProxySettings.vue) & 网络设置实时保存 ---
    private void OnOpenProxyModalClick(object sender, RoutedEventArgs e)
    {
        ProxySettingsModalOverlay.Visibility = Visibility.Visible;
        PlayEntranceAnimation(ProxySettingsModalOverlay, 0, 14, 220);
    }

    private void OnCloseProxyModalClick(object sender, RoutedEventArgs e)
    {
        ProxySettingsModalOverlay.Visibility = Visibility.Collapsed;
    }

    private async void OnConfirmProxyModalClick(object sender, RoutedEventArgs e)
    {
        await SaveNetworkSettingsInternalAsync();
        ProxySettingsModalOverlay.Visibility = Visibility.Collapsed;
        ViewModel.StatusToast = "代理配置已保存，重启应用后生效";
    }

    private async void OnProxyEnableToggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        await SaveNetworkSettingsInternalAsync();
    }

    private async void OnEnableRealIpToggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        RealIpBox.Visibility = EnableRealIpToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        await SaveNetworkSettingsInternalAsync();
    }

    private async void OnNetworkFieldLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        await SaveNetworkSettingsInternalAsync();
    }

    private async void OnSaveNetworkAndPathClick(object sender, RoutedEventArgs e)
    {
        await SaveNetworkSettingsInternalAsync();
        ViewModel.StatusToast = "网络设置已保存";
    }

    private async Task SaveNetworkSettingsInternalAsync()
    {
        if (int.TryParse(MusicApiPortBox.Text.Trim(), out var apiPort) && apiPort is >= 1024 and <= 65535)
        {
            ViewModel.Config.Settings.MusicApiPort = apiPort;
        }
        ViewModel.Config.Settings.UseCustomApiServer = UseCustomApiToggle.IsOn;
        ViewModel.Config.Settings.CustomApiBaseUrl = CustomApiUrlBox.Text.Trim();
        ViewModel.Config.Settings.RealIP = RealIpBox.Text.Trim();
        ViewModel.Config.Settings.EnableRealIP = EnableRealIpToggle.IsOn;

        var host = ProxyHostBox.Text.Trim();
        if (ProxyProtocolComboBox.SelectedItem is ComboBoxItem protoItem && protoItem.Tag is string proto)
        {
            ViewModel.Config.Settings.Proxy.Protocol = proto;
        }
        ViewModel.Config.Settings.Proxy.Host = host;
        if (int.TryParse(ProxyPortBox.Text.Trim(), out var port) && port > 0)
        {
            ViewModel.Config.Settings.Proxy.Port = port;
        }
        ViewModel.Config.Settings.Proxy.Enable = ProxyEnableToggle.IsOn && !string.IsNullOrWhiteSpace(host);

        await ViewModel.Config.SaveSettingsAsync();
    }

    // --- 模态弹窗 6：缓存管理 (ClearCacheSettings.vue) ---
    private void OnOpenClearCacheModalClick(object sender, RoutedEventArgs e)
    {
        ClearHistoryCheck.IsChecked = false;
        ClearFavoriteCheck.IsChecked = false;
        ClearUserCheck.IsChecked = false;
        ClearSettingsCheck.IsChecked = false;
        ClearDownloadsCheck.IsChecked = false;
        ClearResourcesCheck.IsChecked = false;
        ClearLyricsCheck.IsChecked = false;
        ClearCacheModalOverlay.Visibility = Visibility.Visible;
        PlayEntranceAnimation(ClearCacheModalOverlay, 0, 14, 220);
    }

    private void OnCloseClearCacheModalClick(object sender, RoutedEventArgs e)
    {
        ClearCacheModalOverlay.Visibility = Visibility.Collapsed;
    }

    private async void OnDiskCacheConfigChanged(object sender, object e)
    {
        if (!_isInitialized) return;
        ViewModel.Config.Settings.EnableDiskCache = EnableDiskCacheToggle.IsOn;
        if (int.TryParse(DiskCacheMaxSizeBox.Text.Trim(), out var maxMb) && maxMb >= 100)
        {
            ViewModel.Config.Settings.DiskCacheMaxSizeMB = maxMb;
        }
        if (DiskCachePolicyComboBox.SelectedItem is ComboBoxItem item && item.Tag is string policy)
        {
            ViewModel.Config.Settings.DiskCacheCleanupPolicy = policy;
        }
        await ViewModel.Config.SaveSettingsAsync();
        ViewModel.RefreshCacheStats();
    }

    private async void OnPickCacheFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string? folderPath = await Helpers.Win32Interop.PickFolderAsync(GetDialogOwnerHwnd(), "选择音频与歌词缓存目录");
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                string mode = "keep";
                if (!string.Equals(ViewModel.Config.CacheDirectory, folderPath, StringComparison.OrdinalIgnoreCase) &&
                    ViewModel.Config.HasExistingCacheFiles())
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = RootLayout.XamlRoot,
                        Title = "检测到已有缓存",
                        Content = $"旧缓存目录（{ViewModel.Config.CacheDirectory}）中存在已缓存的音频或歌词文件。\n请选择如何处理旧目录中的缓存文件：",
                        PrimaryButtonText = "迁移到新目录",
                        SecondaryButtonText = "销毁旧缓存",
                        CloseButtonText = "保留旧缓存",
                        DefaultButton = ContentDialogButton.Primary
                    };

                    var result = await dialog.ShowAsync();
                    mode = result switch
                    {
                        ContentDialogResult.Primary => "migrate",
                        ContentDialogResult.Secondary => "destroy",
                        _ => "keep"
                    };
                }

                var (_, summaryMsg) = await ViewModel.Config.SwitchDiskCacheDirectoryAsync(folderPath, mode);
                DiskCacheDirBox.Text = folderPath;
                DiskCacheDirDisplayText.Text = folderPath;
                ViewModel.RefreshCacheStats();
                ViewModel.StatusToast = summaryMsg;
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"选择缓存目录失败：{ex.Message}";
        }
    }

    private void OnRefreshCacheStatsClick(object sender, RoutedEventArgs e) => ViewModel.RefreshCacheStats();

    private void OnClearCacheByTypeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string cacheType)
        {
            int deleted = ViewModel.Config.ClearCacheFiles(cacheType);
            ViewModel.RefreshCacheStats();
            ViewModel.StatusToast = $"已清理 {deleted} 个缓存文件";
        }
    }

    private async void OnClearSelectedAppDataClick(object sender, RoutedEventArgs e)
    {
        var scopes = new List<string>();
        if (ClearHistoryCheck.IsChecked == true) scopes.Add("history");
        if (ClearFavoriteCheck.IsChecked == true) scopes.Add("favorite");
        if (ClearUserCheck.IsChecked == true) scopes.Add("user");
        if (ClearSettingsCheck.IsChecked == true) scopes.Add("settings");
        if (ClearDownloadsCheck.IsChecked == true) scopes.Add("downloads");
        if (ClearResourcesCheck.IsChecked == true) scopes.Add("resources");
        if (ClearLyricsCheck.IsChecked == true) scopes.Add("lyrics");

        if (scopes.Count == 0)
        {
            ViewModel.StatusToast = "请先勾选至少一项需要清除的缓存类型";
            return;
        }

        await ViewModel.Config.ClearAppDataByScopesAsync(scopes);
        ViewModel.RefreshHistoryAndFavorites();
        ViewModel.RefreshCacheStats();
        LoadInitialSettingsToUi();
        ClearCacheModalOverlay.Visibility = Visibility.Collapsed;
        ViewModel.StatusToast = $"已清除所选的 {scopes.Count} 项缓存数据";
    }

    private void OnRestartAppClick(object sender, RoutedEventArgs e)
    {
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
        {
            SingleInstanceGuard.ExternalActivationRequested -= OnExternalActivationRequested;
            SingleInstanceGuard.Release();
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--restarting",
                UseShellExecute = true
            });
            ForceExitApplication();
        }
    }

    private void OnOpenAppDataFolderClick(object sender, RoutedEventArgs e)
    {
        var dir = string.IsNullOrWhiteSpace(ViewModel.Config.Settings.DiskCacheDir)
            ? ViewModel.Config.AppDataDirectory
            : ViewModel.Config.Settings.DiskCacheDir;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true
        });
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        AboutVersionStatusText.Text = "正在连接 GitHub 检查最新版本...";
        var (hasUpdate, latestVersion, _) = await ViewModel.Api.CheckGitHubUpdateAsync("v5.1.2");
        AboutVersionStatusText.Text = hasUpdate
            ? $"发现新版本：{latestVersion}（当前 5.1.2，可点击右侧前往更新）"
            : $"5.1.2 (已是最新版本: {latestVersion})";
        ViewModel.StatusToast = AboutVersionStatusText.Text;
    }

    private async void OnRemoteControlToggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        ViewModel.Config.Settings.EnableRemoteControl = RemoteControlToggle.IsOn;
        if (RemoteControlToggle.IsOn)
        {
            bool started = ViewModel.RemoteControl.Start();
            RemoteControlUrlText.Text = started
                ? ViewModel.RemoteControl.AccessUrl
                : "启动失败（端口可能被占用）";
        }
        else
        {
            ViewModel.RemoteControl.Stop();
            RemoteControlUrlText.Text = $"http://localhost:{ViewModel.Config.Settings.RemoteControlPort}/";
        }
        await ViewModel.Config.SaveSettingsAsync();
    }

    private void OnGlobalHotkeyTriggered(string actionId)
    {
        DispatcherQueue.TryEnqueue(() => ExecuteShortcutAction(actionId));
    }

    private async void ExecuteShortcutAction(string actionId)
    {
        switch (actionId)
        {
            case "togglePlay":
            case "PlayPause":
                ViewModel.Player.TogglePlayPause();
                break;
            case "prevPlay":
            case "PrevSong":
                await ViewModel.Player.PreviousAsync();
                break;
            case "nextPlay":
            case "NextSong":
                await ViewModel.Player.NextAsync();
                break;
            case "volumeUp":
            case "VolumeUp":
                VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5);
                break;
            case "volumeDown":
            case "VolumeDown":
                VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5);
                break;
            case "toggleFavorite":
            case "LikeSong":
                OnToggleCurrentSongLikeClick(this, new RoutedEventArgs());
                break;
            case "toggleWindow":
                if (_appWindow.IsVisible)
                {
                    HideMainWindowRememberingBounds();
                }
                else
                {
                    ShowMainWindowRestoringBounds();
                }
                break;
            case "ToggleDesktopLyric":
                OnToggleDesktopLyricClick(this, new RoutedEventArgs());
                break;
        }
    }

    #region 下载音质与各音源选择模态弹窗 (方案 1)

    private CancellationTokenSource? _dlQualityProbeCts;
    private SongItem? _dlQualityTargetSong;

    private async void OpenDownloadQualityModal(SongItem? song)
    {
        if (song == null) return;
        _dlQualityTargetSong = song;

        DlQualitySongTitle.Text = song.Name;
        DlQualitySongArtist.Text = song.ArtistText;

        if (!string.IsNullOrWhiteSpace(song.PicUrl))
        {
            var coverUrl = song.PicUrl.Contains("param=") ? song.PicUrl : $"{song.PicUrl}?param=120y120";
            try
            {
                DlQualitySongCover.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(coverUrl));
            }
            catch
            {
                DlQualitySongCover.Source = null;
            }
        }
        else
        {
            DlQualitySongCover.Source = null;
        }

        DlQualityLoadingPanel.Visibility = Visibility.Visible;
        DlQualityListPanel.Visibility = Visibility.Collapsed;
        DlQualityBackgroundProgress.Visibility = Visibility.Collapsed;
        DlQualityItemsContainer.Children.Clear();

        ViewModel.StatusToast = $"正在探查《{song.Name}》各音源音质...";

        DownloadQualityModalOverlay.Opacity = 1;
        DownloadQualityModalOverlay.Visibility = Visibility.Visible;
        PlayEntranceAnimation(DlQualityModalCard, 0, 14, 220);

        _dlQualityProbeCts?.Cancel();
        _dlQualityProbeCts = new CancellationTokenSource();
        var ct = _dlQualityProbeCts.Token;

        var discoveredOptions = new List<DownloadQualityOption>();

        try
        {
            await ViewModel.Player.ProbeAllDownloadSourcesStreamAsync(song, opt =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ct.IsCancellationRequested || _dlQualityTargetSong?.Id != song.Id) return;

                    // 去重：同一平台且音质完全一致则跳过
                    if (discoveredOptions.Any(o => o.Platform == opt.Platform && o.QualityBadge == opt.QualityBadge))
                        return;

                    discoveredOptions.Add(opt);
                    // 按 QualityRank 降序排序，排在首位且为最高音质的设为推荐
                    discoveredOptions.Sort((a, b) => b.QualityRank.CompareTo(a.QualityRank));
                    for (int i = 0; i < discoveredOptions.Count; i++)
                    {
                        discoveredOptions[i].IsRecommended = (i == 0);
                    }

                    // 实时刷新卡片列表
                    DlQualityItemsContainer.Children.Clear();
                    foreach (var item in discoveredOptions)
                    {
                        DlQualityItemsContainer.Children.Add(CreateDownloadOptionCard(song, item));
                    }

                    // 首个或新结果到达时：立刻切换展示列表，并显示后台细微流光进度条指示仍在继续探查
                    DlQualityLoadingPanel.Visibility = Visibility.Collapsed;
                    DlQualityListPanel.Visibility = Visibility.Visible;
                    DlQualityBackgroundProgress.Visibility = Visibility.Visible;
                });
            }, ct);

            if (ct.IsCancellationRequested || _dlQualityTargetSong?.Id != song.Id) return;

            // 全部源探查完毕（或者短超时触发完成），隐藏后台流光条
            DlQualityBackgroundProgress.Visibility = Visibility.Collapsed;

            // 如果全部探查完毕后依然没有获取到任何直链，展示兜底卡片
            if (discoveredOptions.Count == 0)
            {
                DlQualityItemsContainer.Children.Clear();
                var emptyCard = new Border
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255)),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(16, 14, 16, 14),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                var emptySp = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
                emptySp.Children.Add(new TextBlock
                {
                    Text = "各音源探查均未能直接提供直链",
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                var fallbackBtn = new Button
                {
                    Content = "使用默认后台解析直接下载",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Style = (Style)Application.Current.Resources["BrandPrimaryButtonStyle"]
                };
                fallbackBtn.Click += async (_, _) =>
                {
                    _dlQualityProbeCts?.Cancel();
                    DownloadQualityModalOverlay.Visibility = Visibility.Collapsed;
                    ViewModel.StatusToast = $"已加入下载队列：《{song.Name}》";
                    await ViewModel.Downloader.EnqueueDownloadAsync(song);
                };
                emptySp.Children.Add(fallbackBtn);
                emptyCard.Child = emptySp;
                DlQualityItemsContainer.Children.Add(emptyCard);

                DlQualityLoadingPanel.Visibility = Visibility.Collapsed;
                DlQualityListPanel.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"探测音质失败：{ex.Message}";
            DlQualityLoadingPanel.Visibility = Visibility.Collapsed;
            DlQualityBackgroundProgress.Visibility = Visibility.Collapsed;
        }
    }

    private Border CreateDownloadOptionCard(SongItem song, DownloadQualityOption opt)
    {
        bool isLight = IsCurrentUiLightMode;
        var accent = GetActiveThemeAccentColor();
        var readableAccent = ThemeService.EnsureReadableTextAccent(accent, isLight);
        var normalBg = isLight
            ? Windows.UI.Color.FromArgb(145, 255, 255, 255)
            : Windows.UI.Color.FromArgb(16, 255, 255, 255);
        var hoverBg = isLight
            ? Windows.UI.Color.FromArgb(215, 255, 255, 255)
            : Windows.UI.Color.FromArgb(32, 255, 255, 255);
        var normalBorder = opt.IsRecommended
            ? Windows.UI.Color.FromArgb(isLight ? (byte)155 : (byte)95, accent.R, accent.G, accent.B)
            : (isLight
                ? Windows.UI.Color.FromArgb(36, 15, 23, 42)
                : Windows.UI.Color.FromArgb(24, 255, 255, 255));

        var card = new Border
        {
            Background = new SolidColorBrush(normalBg),
            BorderBrush = new SolidColorBrush(normalBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 16, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        SetProtectedCursorSafe(card, Microsoft.UI.Input.InputSystemCursorShape.Hand);

        // 主信息区（纯净选项卡片，无任何右侧按钮或图标，点击整张卡片即直接下载）
        var contentSp = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        // 平台徽标
        var rawBrandCol = ThemeService.ParseColorOrDefault(opt.BrandColorHex, accent);
        var readableBrandCol = ThemeService.EnsureReadableTextAccent(rawBrandCol, isLight);
        var platBadge = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(36, rawBrandCol.R, rawBrandCol.G, rawBrandCol.B)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(110, readableBrandCol.R, readableBrandCol.G, readableBrandCol.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center
        };
        platBadge.Child = new TextBlock
        {
            Text = opt.PlatformName,
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(readableBrandCol)
        };
        topRow.Children.Add(platBadge);

        // 音质标签
        var qText = new TextBlock
        {
            Text = opt.QualityBadge,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(isLight
                ? Windows.UI.Color.FromArgb(255, 15, 23, 42)
                : Windows.UI.Color.FromArgb(255, 248, 250, 252)),
            VerticalAlignment = VerticalAlignment.Center
        };
        topRow.Children.Add(qText);

        // 推荐徽章
        if (opt.IsRecommended)
        {
            var recBadge = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(36, accent.R, accent.G, accent.B)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(120, readableAccent.R, readableAccent.G, readableAccent.B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1),
                VerticalAlignment = VerticalAlignment.Center
            };
            recBadge.Child = new TextBlock
            {
                Text = "最高推荐",
                FontSize = 9,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(readableAccent)
            };
            topRow.Children.Add(recBadge);
        }

        contentSp.Children.Add(topRow);

        // 规格与文件大小小字
        var specParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(opt.Extension)) specParts.Add(opt.Extension.TrimStart('.').ToUpperInvariant());
        if (opt.BitrateKbps > 0) specParts.Add($"{opt.BitrateKbps}kbps");
        if (!string.IsNullOrWhiteSpace(opt.SizeText)) specParts.Add(opt.SizeText);

        var specText = new TextBlock
        {
            Text = string.Join(" · ", specParts),
            FontSize = 11,
            Foreground = new SolidColorBrush(isLight
                ? Windows.UI.Color.FromArgb(255, 51, 65, 85)
                : Windows.UI.Color.FromArgb(255, 148, 163, 184))
        };
        contentSp.Children.Add(specText);

        async void DoDownload()
        {
            _dlQualityProbeCts?.Cancel();
            DownloadQualityModalOverlay.Visibility = Visibility.Collapsed;
            ViewModel.StatusToast = $"已加入下载队列：《{song.Name}》[{opt.PlatformName} - {opt.QualityBadge}]";
            await ViewModel.Downloader.EnqueueDownloadWithSourceAsync(song, opt.DownloadUrl, opt.Extension, opt.QualityBadge);
        }

        card.Tapped += (_, e) =>
        {
            e.Handled = true;
            DoDownload();
        };

        // 鼠标悬停动效与手型反馈
        card.PointerEntered += (_, _) =>
        {
            card.Background = new SolidColorBrush(hoverBg);
        };
        card.PointerExited += (_, _) =>
        {
            card.Background = new SolidColorBrush(normalBg);
        };

        card.Child = contentSp;
        return card;
    }

    private static SolidColorBrush ParseHexColorBrush(string hex, byte alpha = 255)
    {
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, r, g, b));
            }
        }
        catch { }
        return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, 16, 185, 129));
    }

    private void OnCloseDownloadQualityModalClick(object sender, RoutedEventArgs e)
    {
        _dlQualityProbeCts?.Cancel();
        DownloadQualityModalOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnDownloadQualityModalOverlayTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        _dlQualityProbeCts?.Cancel();
        DownloadQualityModalOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnDownloadQualityModalCardTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true;
    }

    #endregion
}
