using System.Collections.ObjectModel;
using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.Api;
using AlgerMusicPlayer.WinUI.Services.Audio;
using AlgerMusicPlayer.WinUI.Services.Download;
using AlgerMusicPlayer.WinUI.Services.Local;
using AlgerMusicPlayer.WinUI.Services.Lyric;
using AlgerMusicPlayer.WinUI.Services.System;
using AlgerMusicPlayer.WinUI.Services.Unblock;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;

namespace AlgerMusicPlayer.WinUI.ViewModels;

/// <summary>
/// 主视图模型（统一管理导航、首页推荐、搜索、歌单/专辑/歌手详情、播放控制、歌词、下载、本地音乐与设置）
/// </summary>
public partial class MainViewModel : ObservableObject
{
    public ConfigService Config { get; }
    public I18nService I18n { get; }
    public NeteaseApiClient Api { get; }
    public QqMusicService QqMusic { get; }
    public UnblockMusicService Unblock { get; }
    public BilibiliService Bilibili { get; }
    public LyricService Lyric { get; }
    public AudioPlayerService Player { get; }
    public DownloadManagerService Downloader { get; }
    public LocalMusicScannerService LocalScanner { get; }
    public RemoteControlService RemoteControl { get; }
    public GlobalHotkeyService Hotkeys { get; }

    private DispatcherQueue? _dispatcher;

    // ==================== 导航与全局状态 ====================

    private string _currentSection = "Home";
    public string CurrentSection
    {
        get => _currentSection;
        set => SetProperty(ref _currentSection, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    private string _statusToast = string.Empty;
    public string StatusToast
    {
        get => _statusToast;
        set
        {
            _statusToast = value;
            OnPropertyChanged(nameof(StatusToast));
        }
    }

    // ==================== 播放器实时状态 ====================

    private string _currentSongName = "未在播放";
    public string CurrentSongName
    {
        get => _currentSongName;
        set => SetProperty(ref _currentSongName, value);
    }

    private string _currentArtistName = "选择一首歌曲开始聆听";
    public string CurrentArtistName
    {
        get => _currentArtistName;
        set => SetProperty(ref _currentArtistName, value);
    }

    private string _currentCoverUrl = string.Empty;
    public string CurrentCoverUrl
    {
        get => _currentCoverUrl;
        set => SetProperty(ref _currentCoverUrl, value);
    }

    private string _currentPlatformBadge = "NETEASE";
    public string CurrentPlatformBadge
    {
        get => _currentPlatformBadge;
        set => SetProperty(ref _currentPlatformBadge, value);
    }

    private string _currentQualityBadge = "无损 FLAC";
    public string CurrentQualityBadge
    {
        get => _currentQualityBadge;
        set => SetProperty(ref _currentQualityBadge, value);
    }

    private string _playbackRateText = "1.0x";
    public string PlaybackRateText
    {
        get => _playbackRateText;
        set => SetProperty(ref _playbackRateText, value);
    }

    private string _lyricOffsetText = "0.0s";
    public string LyricOffsetText
    {
        get => _lyricOffsetText;
        set => SetProperty(ref _lyricOffsetText, value);
    }

    // ==================== 首页 Hero (每日推荐 & 私人FM) 状态 ====================

    private string _dailyCoverUrl = string.Empty;
    public string DailyCoverUrl
    {
        get => _dailyCoverUrl;
        set => SetProperty(ref _dailyCoverUrl, value);
    }

    private string _dailySongCountText = "30 首";
    public string DailySongCountText
    {
        get => _dailySongCountText;
        set => SetProperty(ref _dailySongCountText, value);
    }

    private SongItem? _fmCurrentSong;
    public SongItem? FmCurrentSong
    {
        get => _fmCurrentSong;
        set => SetProperty(ref _fmCurrentSong, value);
    }

    private string _fmSongName = "私人 FM 漫游";
    public string FmSongName
    {
        get => _fmSongName;
        set => SetProperty(ref _fmSongName, value);
    }

    private string _fmArtistName = "点击播放开启专属电台";
    public string FmArtistName
    {
        get => _fmArtistName;
        set => SetProperty(ref _fmArtistName, value);
    }

    private string _fmCoverUrl = string.Empty;
    public string FmCoverUrl
    {
        get => _fmCoverUrl;
        set => SetProperty(ref _fmCoverUrl, value);
    }

    private bool _isFmModeActive;
    public bool IsFmModeActive
    {
        get => _isFmModeActive;
        set => SetProperty(ref _isFmModeActive, value);
    }

    private string _cacheStatsText = "正在统计缓存...";
    public string CacheStatsText
    {
        get => _cacheStatsText;
        set => SetProperty(ref _cacheStatsText, value);
    }

    private double _cacheUsagePercent;
    public double CacheUsagePercent
    {
        get => _cacheUsagePercent;
        set => SetProperty(ref _cacheUsagePercent, value);
    }

    private string _cacheFilesDetailText = "音频文件: 0 个 · 歌词文件: 0 个";
    public string CacheFilesDetailText
    {
        get => _cacheFilesDetailText;
        set => SetProperty(ref _cacheFilesDetailText, value);
    }

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set => SetProperty(ref _isPlaying, value);
    }

    private bool _isCurrentSongLiked;
    public bool IsCurrentSongLiked
    {
        get => _isCurrentSongLiked;
        set => SetProperty(ref _isCurrentSongLiked, value);
    }

    private double _positionSeconds;
    public double PositionSeconds
    {
        get => _positionSeconds;
        set => SetProperty(ref _positionSeconds, value);
    }

    private double _durationSeconds = 1;
    public double DurationSeconds
    {
        get => _durationSeconds;
        set => SetProperty(ref _durationSeconds, value);
    }

    private string _positionText = "00:00";
    public string PositionText
    {
        get => _positionText;
        set => SetProperty(ref _positionText, value);
    }

    private string _durationText = "00:00";
    public string DurationText
    {
        get => _durationText;
        set => SetProperty(ref _durationText, value);
    }

    private double _volumePercent = 80;
    public double VolumePercent
    {
        get => _volumePercent;
        set => SetProperty(ref _volumePercent, value);
    }

    private string _currentLyricText = "Alger Music Player";
    public string CurrentLyricText
    {
        get => _currentLyricText;
        set => SetProperty(ref _currentLyricText, value);
    }

    private string _currentLyricTranslation = "纯粹、轻快的 WinUI 3 原生音乐体验";
    public string CurrentLyricTranslation
    {
        get => _currentLyricTranslation;
        set => SetProperty(ref _currentLyricTranslation, value);
    }

    private bool _isImmersiveLyricOpen;
    public bool IsImmersiveLyricOpen
    {
        get => _isImmersiveLyricOpen;
        set => SetProperty(ref _isImmersiveLyricOpen, value);
    }

    private bool _isDesktopLyricOpen;
    public bool IsDesktopLyricOpen
    {
        get => _isDesktopLyricOpen;
        set => SetProperty(ref _isDesktopLyricOpen, value);
    }

    private bool _isQueueDrawerOpen;
    public bool IsQueueDrawerOpen
    {
        get => _isQueueDrawerOpen;
        set => SetProperty(ref _isQueueDrawerOpen, value);
    }

    // ==================== 详情页头部与列表状态 (1:1 对齐 MusicListPage.vue) ====================

    private string _detailTitle = string.Empty;
    public string DetailTitle
    {
        get => _detailTitle;
        set => SetProperty(ref _detailTitle, value);
    }

    private string _detailSubtitle = string.Empty;
    public string DetailSubtitle
    {
        get => _detailSubtitle;
        set => SetProperty(ref _detailSubtitle, value);
    }

    private string _detailDescription = string.Empty;
    public string DetailDescription
    {
        get => _detailDescription;
        set => SetProperty(ref _detailDescription, value);
    }

    private string _detailCoverUrl = string.Empty;
    public string DetailCoverUrl
    {
        get => _detailCoverUrl;
        set => SetProperty(ref _detailCoverUrl, value);
    }

    private string _detailBadgeText = "PLAYLIST";
    public string DetailBadgeText
    {
        get => _detailBadgeText;
        set => SetProperty(ref _detailBadgeText, value);
    }

    private string _detailCreatorName = string.Empty;
    public string DetailCreatorName
    {
        get => _detailCreatorName;
        set => SetProperty(ref _detailCreatorName, value);
    }

    private string _detailCreatorAvatarUrl = string.Empty;
    public string DetailCreatorAvatarUrl
    {
        get => _detailCreatorAvatarUrl;
        set => SetProperty(ref _detailCreatorAvatarUrl, value);
    }

    private string _detailSongCountText = "0 首音乐";
    public string DetailSongCountText
    {
        get => _detailSongCountText;
        set => SetProperty(ref _detailSongCountText, value);
    }

    private string _detailType = "playlist";
    public string DetailType
    {
        get => _detailType;
        set => SetProperty(ref _detailType, value);
    }

    private long _detailPlaylistId;
    public long DetailPlaylistId
    {
        get => _detailPlaylistId;
        set => SetProperty(ref _detailPlaylistId, value);
    }

    public int DetailQqDirId { get; set; }

    private bool _canCollectDetail;
    public bool CanCollectDetail
    {
        get => _canCollectDetail;
        set => SetProperty(ref _canCollectDetail, value);
    }

    private bool _isDetailCollected;
    public bool IsDetailCollected
    {
        get => _isDetailCollected;
        set => SetProperty(ref _isDetailCollected, value);
    }

    private bool _canRemoveFromCurrentDetail;
    public bool CanRemoveFromCurrentDetail
    {
        get => _canRemoveFromCurrentDetail;
        set => SetProperty(ref _canRemoveFromCurrentDetail, value);
    }

    private string _detailSearchKeyword = string.Empty;
    public string DetailSearchKeyword
    {
        get => _detailSearchKeyword;
        set
        {
            if (SetProperty(ref _detailSearchKeyword, value))
            {
                RebuildFilteredDetailSongs();
            }
        }
    }

    private bool _isDetailCompactLayout;
    public bool IsDetailCompactLayout
    {
        get => _isDetailCompactLayout;
        set => SetProperty(ref _isDetailCompactLayout, value);
    }

    private bool _isDetailSelecting;
    public bool IsDetailSelecting
    {
        get => _isDetailSelecting;
        set
        {
            if (SetProperty(ref _isDetailSelecting, value))
            {
                foreach (var s in DetailSongs)
                {
                    s.IsBatchSelectable = value;
                    if (!value) s.IsBatchSelected = false;
                }
            }
        }
    }

    // ==================== 用户登录状态 ====================

    private UserProfile? _currentUser;
    public UserProfile? CurrentUser
    {
        get => _currentUser;
        set => SetProperty(ref _currentUser, value);
    }

    private string _loginStatusText = "未登录（支持扫码或填入网易云 MUSIC_U Cookie 登录）";
    public string LoginStatusText
    {
        get => _loginStatusText;
        set => SetProperty(ref _loginStatusText, value);
    }

    private QqUserProfile? _currentQqUser;
    public QqUserProfile? CurrentQqUser
    {
        get => _currentQqUser;
        set => SetProperty(ref _currentQqUser, value);
    }

    private string _qqLoginStatusText = "未登录 QQ 音乐（支持一键调用本机已登录 QQ、手机扫码或 Cookie 登录）";
    public string QqLoginStatusText
    {
        get => _qqLoginStatusText;
        set => SetProperty(ref _qqLoginStatusText, value);
    }

    // ==================== 本地音乐文件夹树状态 ====================

    private List<LocalMusicFolderNode> _localFolderRoots = [];
    private LocalMusicFolderNode? _selectedFolderNode;
    public LocalMusicFolderNode? SelectedFolderNode
    {
        get => _selectedFolderNode;
        set => SetProperty(ref _selectedFolderNode, value);
    }

    private string _localSearchKeyword = string.Empty;
    public string LocalSearchKeyword
    {
        get => _localSearchKeyword;
        set
        {
            if (SetProperty(ref _localSearchKeyword, value))
            {
                RebuildLocalMusicViewData();
            }
        }
    }

    private string _localMusicCountBadgeText = "0 首歌曲";
    public string LocalMusicCountBadgeText
    {
        get => _localMusicCountBadgeText;
        set => SetProperty(ref _localMusicCountBadgeText, value);
    }

    // ==================== MV 视频播放状态 (1:1 对应 views/mv/index.vue + components/MvPlayer.vue) ====================

    private string _selectedMvArea = "全部";
    public string SelectedMvArea
    {
        get => _selectedMvArea;
        set => SetProperty(ref _selectedMvArea, value);
    }

    private MvItem? _currentMvItem;
    public MvItem? CurrentMvItem
    {
        get => _currentMvItem;
        set => SetProperty(ref _currentMvItem, value);
    }

    private string _currentMvTitle = string.Empty;
    public string CurrentMvTitle
    {
        get => _currentMvTitle;
        set => SetProperty(ref _currentMvTitle, value);
    }

    private string _currentMvArtist = string.Empty;
    public string CurrentMvArtist
    {
        get => _currentMvArtist;
        set => SetProperty(ref _currentMvArtist, value);
    }

    private string _currentMvStreamUrl = string.Empty;
    public string CurrentMvStreamUrl
    {
        get => _currentMvStreamUrl;
        set => SetProperty(ref _currentMvStreamUrl, value);
    }

    private int _currentMvResolution = 1080;
    public int CurrentMvResolution
    {
        get => _currentMvResolution;
        set => SetProperty(ref _currentMvResolution, value);
    }

    private bool _isMvOverlayOpen;
    public bool IsMvOverlayOpen
    {
        get => _isMvOverlayOpen;
        set => SetProperty(ref _isMvOverlayOpen, value);
    }

    private bool _isMvSingleLoop;
    public bool IsMvSingleLoop
    {
        get => _isMvSingleLoop;
        set => SetProperty(ref _isMvSingleLoop, value);
    }

    private bool _mvHasMore = true;
    public bool MvHasMore
    {
        get => _mvHasMore;
        set => SetProperty(ref _mvHasMore, value);
    }

    private bool _isLoadingMoreMv;
    public bool IsLoadingMoreMv
    {
        get => _isLoadingMoreMv;
        set => SetProperty(ref _isLoadingMoreMv, value);
    }

    private int _mvOffset;
    private const int MvPageLimit = 40;

    // ==================== 数据集合 ====================

    public ObservableCollection<BannerItem> Banners { get; } = [];
    public ObservableCollection<PlaylistItem> RecommendPlaylists { get; } = [];
    public ObservableCollection<SongItem> DailyRecommendSongs { get; } = [];
    public ObservableCollection<SongItem> DailyPreviewSongs { get; } = [];
    public ObservableCollection<ArtistItem> HotArtists { get; } = [];
    public ObservableCollection<AlbumItem> HomeNewAlbums { get; } = [];
    public ObservableCollection<SongItem> HomeNewSongs { get; } = [];
    public ObservableCollection<AlbumItem> NewAlbums { get; } = [];
    public ObservableCollection<RadioProgramItem> PodcastTodayPreferred { get; } = [];
    public ObservableCollection<RadioStationItem> RecommendedRadios { get; } = [];
    public ObservableCollection<PlaylistItem> Toplists { get; } = [];
    public ObservableCollection<PlaylistItem> SquarePlaylists { get; } = [];
    public ObservableCollection<SongItem> DetailSongs { get; } = [];
    public ObservableCollection<SongItem> FilteredDetailSongs { get; } = [];

    public ObservableCollection<SongItem> SearchSongs { get; } = [];
    public ObservableCollection<PlaylistItem> SearchPlaylists { get; } = [];
    public ObservableCollection<AlbumItem> SearchAlbums { get; } = [];
    public ObservableCollection<ArtistItem> SearchArtists { get; } = [];
    public ObservableCollection<MvItem> SearchMvs { get; } = [];
    public ObservableCollection<BilibiliVideoItem> BilibiliVideos { get; } = [];
    public ObservableCollection<MvItem> MvList { get; } = [];

    public ObservableCollection<LyricLine> LyricLines { get; } = [];
    public ObservableCollection<CommentItem> CurrentSongComments { get; } = [];
    public ObservableCollection<SongItem> HistorySongs { get; } = [];
    public ObservableCollection<SongItem> FavoriteSongs { get; } = [];
    public ObservableCollection<SongItem> LocalSongs { get; } = [];
    public ObservableCollection<SongItem> FilteredLocalSongs { get; } = [];
    public ObservableCollection<LocalMusicFolderNode> RootFolderNodes { get; } = [];
    public ObservableCollection<LocalMusicFolderNode> VisibleFolderNodes { get; } = [];
    public ObservableCollection<SongItem> CurrentFolderSongs { get; } = [];
    public ObservableCollection<string> ConfiguredLocalFolders { get; } = [];
    public ObservableCollection<PlaylistItem> QqCreatedPlaylists { get; } = [];
    public ObservableCollection<PlaylistItem> QqCollectedPlaylists { get; } = [];
    public ObservableCollection<LocalQqAccountItem> LocalQqAccounts { get; } = [];
    public ObservableCollection<PlaylistItem> UserPlaylists { get; } = [];
    public ObservableCollection<PlaylistItem> UserCreatedPlaylists { get; } = [];
    public ObservableCollection<PlaylistItem> UserFavoritePlaylists { get; } = [];
    public ObservableCollection<AlbumItem> UserSubscribedAlbums { get; } = [];
    public ObservableCollection<PlayRecordItem> UserPlayRecords { get; } = [];
    public ObservableCollection<ShortcutItem> ShortcutsList { get; } = [];
    public ObservableCollection<LxScriptItem> LxScriptsList { get; } = [];
    public ObservableCollection<DonorItem> DonorsList { get; } = [];
    public ObservableCollection<string> SearchSuggestions { get; } = [];

    private readonly List<SongItem> _fmQueue = [];
    private int _fmIndex;

    public bool IsDraggingSeek { get; set; }

    public MainViewModel(
        ConfigService config,
        I18nService i18n,
        NeteaseApiClient api,
        QqMusicService qqMusic,
        UnblockMusicService unblock,
        BilibiliService bilibili,
        LyricService lyric,
        AudioPlayerService player,
        DownloadManagerService downloader,
        LocalMusicScannerService localScanner,
        RemoteControlService remoteControl,
        GlobalHotkeyService hotkeys)
    {
        Config = config;
        I18n = i18n;
        Api = api;
        QqMusic = qqMusic;
        Unblock = unblock;
        Bilibili = bilibili;
        Lyric = lyric;
        Player = player;
        Downloader = downloader;
        LocalScanner = localScanner;
        RemoteControl = remoteControl;
        Hotkeys = hotkeys;

        VolumePercent = Math.Round(Config.Settings.Volume * 100);
        CurrentQualityBadge = MusicQualityLevel.GetShortBadgeName(Config.Settings.MusicQuality);

        Player.SongChanged += OnPlayerSongChanged;
        Player.PlayStateChanged += OnPlayerStateChanged;
        Player.PositionChanged += OnPlayerPositionChanged;
        Player.LyricLoaded += OnPlayerLyricLoaded;
        Player.LyricLineChanged += OnPlayerLyricLineChanged;
        Player.QualityBadgeChanged += badge => RunOnUi(() => CurrentQualityBadge = badge);
        Player.SongEndedNaturally += OnPlayerSongEndedNaturally;
        Player.StatusMessage += msg => RunOnUi(() => StatusToast = msg);
    }

    public void AttachDispatcher(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        Player.AttachDispatcher(dispatcher);
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher == null || _dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }

    /// <summary>
    /// 初始化加载首页数据与历史/收藏数据
    /// </summary>
    public async Task InitializeAsync()
    {
        RefreshHistoryAndFavorites();
        RefreshCacheStats();
        SyncConfiguredFoldersList();
        RestoreCachedLocalMusic();

        if (Player.CurrentSong != null)
        {
            UpdateCurrentSongDisplay(Player.CurrentSong);
            SyncSongPlayingStates(Player.CurrentSong.Id, Player.IsPlaying);
            _ = Player.RestoreLastSessionSongStateAsync();
        }
        else if (Player.Queue.Count > 0)
        {
            var first = Player.Queue[0];
            UpdateCurrentSongDisplay(first);
        }

        _ = Downloader.RefreshCompletedDownloadsAsync();
        await LoadHomeDataAsync();

        if (!string.IsNullOrWhiteSpace(Config.Settings.NeteaseCookie))
        {
            await RefreshUserProfileAsync();
        }
        else if (Config.Settings.SavedUid > 0)
        {
            await LoginByUidAsync(Config.Settings.SavedUid);
        }

        if (!string.IsNullOrWhiteSpace(Config.Settings.QqMusicUin) || !string.IsNullOrWhiteSpace(Config.Settings.QqMusicCookie))
        {
            await RefreshQqUserProfileAsync();
        }
    }

    /// <summary>
    /// 启动时从本地持久化缓存恢复已扫描的本地音乐列表（无需每次启动重新扫描磁盘）
    /// </summary>
    public void RestoreCachedLocalMusic()
    {
        if (Config.CachedLocalSongs.Count == 0) return;

        var removedSet = new HashSet<string>(
            Config.Settings.RemovedLocalSongPaths.Select(LocalMusicScannerService.NormalizeFolderPath),
            StringComparer.OrdinalIgnoreCase);

        LocalSongs.Clear();
        foreach (var s in Config.CachedLocalSongs)
        {
            if (!string.IsNullOrWhiteSpace(s.LocalFilePath))
            {
                var normPath = LocalMusicScannerService.NormalizeFolderPath(s.LocalFilePath);
                if (removedSet.Contains(normPath)) continue;
            }
            s.IsLiked = Config.IsFavorite(s.Id);
            LocalSongs.Add(s);
        }

        if (LocalSongs.Count > 0)
        {
            RebuildLocalMusicViewData();
        }
    }

    public void RefreshCacheStats()
    {
        var (musicBytes, lyricBytes, totalBytes, musicCount, lyricCount, usagePct) = Config.GetCacheSizeStatsFull();
        double musicMb = musicBytes / (1024.0 * 1024.0);
        double lyricKb = lyricBytes / 1024.0;
        double totalMb = totalBytes / (1024.0 * 1024.0);
        CacheUsagePercent = usagePct;
        CacheStatsText = $"已用 {totalMb:F2} MB / 上限 {Config.Settings.DiskCacheMaxSizeMB} MB（音频: {musicMb:F2} MB · 歌词: {lyricKb:F1} KB）";
        CacheFilesDetailText = $"音频缓存文件: {musicCount} 个 · 歌词缓存文件: {lyricCount} 个";
    }

    public async Task LoadDonorsAsync()
    {
        var donors = await Api.GetDonationListAsync();
        DonorsList.Clear();
        foreach (var d in donors.Take(48))
        {
            DonorsList.Add(d);
        }
    }

    public void RefreshHistoryAndFavorites()
    {
        HistorySongs.Clear();
        foreach (var s in Config.PlayHistory) HistorySongs.Add(s);

        FavoriteSongs.Clear();
        foreach (var s in Config.LocalFavorites) FavoriteSongs.Add(s);
    }

    public async Task LoadHomeDataAsync()
    {
        IsLoading = true;
        try
        {
            var bannersTask = Api.GetBannersAsync();
            var playlistsTask = Api.GetRecommendPlaylistsAsync(18);
            var dailySongsTask = Api.GetDailyRecommendSongsAsync();
            var artistsTask = Api.GetHotArtistsAsync(12);
            var newAlbumsTask = Api.GetNewAlbumsAsync("ALL", 6, 0);
            var newSongsTask = Api.GetRecommendNewSongsAsync(16);
            var fmTask = Api.GetPersonalFmAsync();

            await Task.WhenAll(bannersTask, playlistsTask, dailySongsTask, artistsTask, newAlbumsTask, newSongsTask, fmTask);

            Banners.Clear();
            foreach (var b in bannersTask.Result) Banners.Add(b);

            RecommendPlaylists.Clear();
            foreach (var p in playlistsTask.Result) RecommendPlaylists.Add(p);

            DailyRecommendSongs.Clear();
            DailyPreviewSongs.Clear();
            foreach (var s in dailySongsTask.Result)
            {
                s.IsLiked = Config.IsFavorite(s.Id);
                DailyRecommendSongs.Add(s);
                if (DailyPreviewSongs.Count < 3)
                {
                    DailyPreviewSongs.Add(s);
                }
            }
            DailySongCountText = $"{DailyRecommendSongs.Count} 首";
            DailyCoverUrl = DailyRecommendSongs.FirstOrDefault()?.CoverThumbnailUrl ?? string.Empty;

            HotArtists.Clear();
            foreach (var a in artistsTask.Result) HotArtists.Add(a);

            HomeNewAlbums.Clear();
            foreach (var al in newAlbumsTask.Result) HomeNewAlbums.Add(al);

            HomeNewSongs.Clear();
            foreach (var ns in newSongsTask.Result)
            {
                ns.IsLiked = Config.IsFavorite(ns.Id);
                HomeNewSongs.Add(ns);
            }

            if (fmTask.Result.Count > 0)
            {
                _fmQueue.Clear();
                _fmQueue.AddRange(fmTask.Result);
                _fmIndex = 0;
                UpdateFmDisplay(_fmQueue[0]);
            }
            else if (HomeNewSongs.Count > 0 && _fmQueue.Count == 0)
            {
                // 未登录时使用推荐新歌作为 FM 漫游兜底
                _fmQueue.AddRange(HomeNewSongs);
                _fmIndex = 0;
                UpdateFmDisplay(_fmQueue[0]);
            }
        }
        catch (Exception ex)
        {
            StatusToast = $"加载推荐数据失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void UpdateFmDisplay(SongItem song)
    {
        FmCurrentSong = song;
        FmSongName = song.Name;
        FmArtistName = song.ArtistText;
        FmCoverUrl = song.CoverThumbnailUrl;
    }

    public async Task TogglePlayFmAsync()
    {
        if (FmCurrentSong == null)
        {
            await NextFmAsync();
            return;
        }

        if (IsFmModeActive && Player.CurrentSong?.Id == FmCurrentSong.Id)
        {
            Player.TogglePlayPause();
        }
        else
        {
            IsFmModeActive = true;
            await Player.PlaySongAsync(FmCurrentSong, _fmQueue);
        }
    }

    public async Task NextFmAsync()
    {
        _fmIndex++;
        if (_fmIndex >= _fmQueue.Count - 1)
        {
            var more = await Api.GetPersonalFmAsync();
            foreach (var s in more)
            {
                if (_fmQueue.All(x => x.Id != s.Id))
                {
                    _fmQueue.Add(s);
                }
            }
        }

        if (_fmQueue.Count == 0) return;
        _fmIndex %= _fmQueue.Count;
        var next = _fmQueue[_fmIndex];
        UpdateFmDisplay(next);
        IsFmModeActive = true;
        await Player.PlaySongAsync(next, _fmQueue);
    }

    public async Task TrashFmAsync()
    {
        if (FmCurrentSong != null && FmCurrentSong.NumericId > 0)
        {
            _ = Api.FmTrashAsync(FmCurrentSong.NumericId);
            StatusToast = $"已将《{FmCurrentSong.Name}》移入 FM 垃圾桶";
        }
        await NextFmAsync();
    }

    public void OpenDailyRecommendDetail()
    {
        CurrentSection = "Detail";
        DetailType = "dailyRecommend";
        DetailPlaylistId = 0;
        DetailBadgeText = "PLAYLIST";
        DetailTitle = "每日歌曲推荐";
        DetailCreatorName = "根据你的音乐口味生成 · 每天 6:00 更新";
        DetailCreatorAvatarUrl = string.Empty;
        DetailSongCountText = $"{DailyRecommendSongs.Count} 首音乐";
        DetailSubtitle = $"根据你的音乐口味生成 · 每天 6:00 更新 · 共 {DailyRecommendSongs.Count} 首";
        DetailDescription = "为你精选的每日个性化推荐曲目";
        DetailCoverUrl = DailyCoverUrl;
        CanCollectDetail = false;
        IsDetailCollected = false;
        CanRemoveFromCurrentDetail = false;
        IsDetailSelecting = false;
        _detailSearchKeyword = string.Empty;
        OnPropertyChanged(nameof(DetailSearchKeyword));
        DetailSongs.Clear();
        foreach (var s in DailyRecommendSongs) DetailSongs.Add(s);
        RebuildFilteredDetailSongs();
    }

    public void RebuildFilteredDetailSongs()
    {
        var kw = (_detailSearchKeyword ?? string.Empty).Trim();
        var currentPlayingId = Player.CurrentSong?.Id;
        bool activelyPlaying = Player.IsPlaying;

        var filtered = DetailSongs
            .Where(s => !Config.Settings.DislikedSongIds.Contains(s.Id))
            .Where(s => string.IsNullOrEmpty(kw) ||
                        s.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                        s.ArtistText.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                        s.AlbumText.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                        s.SubtitleText.Contains(kw, StringComparison.OrdinalIgnoreCase))
            .ToList();

        FilteredDetailSongs.Clear();
        for (int i = 0; i < filtered.Count; i++)
        {
            var s = filtered[i];
            s.DisplayIndex = i + 1;
            s.IsBatchSelectable = _isDetailSelecting;
            s.IsLiked = Config.IsFavorite(s.Id);
            s.IsPlaying = !string.IsNullOrEmpty(currentPlayingId) && s.Id == currentPlayingId;
            s.IsAudioActivelyPlaying = s.IsPlaying && activelyPlaying;
            FilteredDetailSongs.Add(s);
        }
    }

    public async Task LoadNewAlbumsAsync(string area = "ALL")
    {
        IsLoading = true;
        try
        {
            var albums = await Api.GetNewAlbumsAsync(area, 36, 0);
            NewAlbums.Clear();
            foreach (var al in albums) NewAlbums.Add(al);
            StatusToast = $"新碟上架 [{area}] 已加载 {albums.Count} 张专辑";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LoadPodcastPageAsync(int cateId = 0)
    {
        IsLoading = true;
        try
        {
            var preferredTask = Api.GetPodcastTodayPreferredAsync();
            var radiosTask = Api.GetRecommendedRadioStationsAsync(cateId);
            await Task.WhenAll(preferredTask, radiosTask);

            PodcastTodayPreferred.Clear();
            foreach (var p in preferredTask.Result) PodcastTodayPreferred.Add(p);

            RecommendedRadios.Clear();
            foreach (var r in radiosTask.Result) RecommendedRadios.Add(r);

            StatusToast = $"播客电台已加载 {RecommendedRadios.Count} 个电台与 {PodcastTodayPreferred.Count} 个优选节目";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task OpenRadioStationDetailAsync(RadioStationItem radio)
    {
        if (radio == null) return;
        CurrentSection = "Detail";
        DetailType = "podcast";
        DetailPlaylistId = radio.Id;
        DetailBadgeText = "PODCAST";
        DetailTitle = radio.Name;
        DetailCreatorName = radio.DjName;
        DetailCreatorAvatarUrl = string.Empty;
        DetailSongCountText = $"{radio.ProgramCount} 首音乐";
        DetailSubtitle = $"主播: {radio.DjName} · 分类: {radio.Category} · 共 {radio.ProgramCount} 期节目";
        DetailDescription = radio.RcmdText;
        DetailCoverUrl = radio.PicUrl;
        CanCollectDetail = false;
        IsDetailCollected = false;
        CanRemoveFromCurrentDetail = false;
        IsDetailSelecting = false;
        _detailSearchKeyword = string.Empty;
        OnPropertyChanged(nameof(DetailSearchKeyword));
        DetailSongs.Clear();
        FilteredDetailSongs.Clear();

        IsLoading = true;
        try
        {
            var programs = await Api.GetRadioProgramsAsync(radio.Id, 50, 0);
            foreach (var p in programs)
            {
                if (p.MainSong != null)
                {
                    p.MainSong.IsLiked = Config.IsFavorite(p.MainSong.Id);
                    DetailSongs.Add(p.MainSong);
                }
            }
            DetailSongCountText = $"{DetailSongs.Count} 首音乐";
            RebuildFilteredDetailSongs();
            StatusToast = $"已加载电台《{radio.Name}》共 {DetailSongs.Count} 期声音节目";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LoadToplistsAsync()
    {
        if (Toplists.Count > 0) return;
        IsLoading = true;
        try
        {
            var list = await Api.GetToplistsAsync();
            Toplists.Clear();
            foreach (var item in list) Toplists.Add(item);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LoadSquarePlaylistsAsync(string category = "全部")
    {
        IsLoading = true;
        try
        {
            var list = await Api.GetTopPlaylistsAsync(category, 36);
            SquarePlaylists.Clear();
            foreach (var item in list) SquarePlaylists.Add(item);
            StatusToast = $"歌单分类 [{category}] 已加载 {list.Count} 个歌单";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task OpenPlaylistDetailAsync(PlaylistItem playlist)
    {
        if (playlist == null) return;
        CurrentSection = "Detail";
        DetailType = (playlist.Source == "qq" || !string.IsNullOrWhiteSpace(playlist.QqDissId)) ? "qqPlaylist" : "playlist";
        DetailPlaylistId = playlist.Id;
        DetailQqDirId = playlist.QqDirId;
        DetailBadgeText = "PLAYLIST";
        DetailTitle = playlist.Name;
        DetailCreatorName = playlist.CreatorName;
        DetailCreatorAvatarUrl = playlist.CreatorAvatarUrl;
        DetailSongCountText = $"{playlist.TrackCount} 首音乐";
        DetailSubtitle = $"播放 {playlist.PlayCountText} · 共 {playlist.TrackCount} 首";
        DetailDescription = playlist.Description;
        DetailCoverUrl = playlist.CoverImgUrl;
        CanCollectDetail = DetailType == "playlist" && playlist.Id > 0;
        IsDetailCollected = playlist.Subscribed;
        CanRemoveFromCurrentDetail = false;
        IsDetailSelecting = false;
        _detailSearchKeyword = string.Empty;
        OnPropertyChanged(nameof(DetailSearchKeyword));
        DetailSongs.Clear();
        FilteredDetailSongs.Clear();

        IsLoading = true;
        try
        {
            if (playlist.Source == "qq" || !string.IsNullOrWhiteSpace(playlist.QqDissId))
            {
                var dissId = !string.IsNullOrWhiteSpace(playlist.QqDissId) ? playlist.QqDissId : playlist.Id.ToString();
                var qqFull = await QqMusic.GetPlaylistDetailAsync(dissId);
                if (qqFull != null)
                {
                    if (DetailQqDirId <= 0 && qqFull.QqDirId > 0)
                    {
                        DetailQqDirId = qqFull.QqDirId;
                    }
                    if (DetailQqDirId <= 0)
                    {
                        var matchedCreated = QqCreatedPlaylists.FirstOrDefault(p => p.QqDissId == dissId || p.Id == playlist.Id);
                        if (matchedCreated != null && matchedCreated.QqDirId > 0)
                        {
                            DetailQqDirId = matchedCreated.QqDirId;
                        }
                    }
                    CanRemoveFromCurrentDetail = DetailQqDirId > 0 || QqCreatedPlaylists.Any(p => p.QqDissId == dissId || p.Id == playlist.Id);
                    DetailTitle = qqFull.Name;
                    DetailDescription = qqFull.Description;
                    DetailCreatorName = qqFull.CreatorName;
                    DetailCreatorAvatarUrl = qqFull.CreatorAvatarUrl;
                    DetailSongCountText = $"{qqFull.Tracks.Count} 首音乐";
                    DetailSubtitle = $"[QQ音乐歌单] 创建者: {qqFull.CreatorName} · 播放 {qqFull.PlayCountText} · 共 {qqFull.Tracks.Count} 首";
                    if (!string.IsNullOrWhiteSpace(qqFull.CoverImgUrl))
                    {
                        DetailCoverUrl = qqFull.CoverImgUrl;
                    }
                    foreach (var s in qqFull.Tracks)
                    {
                        s.IsLiked = Config.IsFavorite(s.Id);
                        DetailSongs.Add(s);
                    }
                    RebuildFilteredDetailSongs();
                }
                return;
            }

            var full = await Api.GetPlaylistDetailAsync(playlist.Id);
            if (full != null)
            {
                DetailTitle = full.Name;
                DetailDescription = full.Description;
                DetailCreatorName = full.CreatorName;
                DetailCreatorAvatarUrl = full.CreatorAvatarUrl;
                int totalCount = full.TrackCount > 0 ? full.TrackCount : full.Tracks.Count;
                DetailSongCountText = $"{totalCount} 首音乐";
                DetailSubtitle = $"创建者: {full.CreatorName} · 播放 {full.PlayCountText} · 共 {totalCount} 首";
                if (!string.IsNullOrWhiteSpace(full.CoverImgUrl))
                {
                    DetailCoverUrl = full.CoverImgUrl;
                }
                IsDetailCollected = full.Subscribed;
                CanRemoveFromCurrentDetail = CurrentUser != null && full.CreatorUserId > 0 && full.CreatorUserId == CurrentUser.UserId;
                CanCollectDetail = !CanRemoveFromCurrentDetail && full.Id > 0;
                foreach (var s in full.Tracks)
                {
                    s.IsLiked = Config.IsFavorite(s.Id);
                    DetailSongs.Add(s);
                }
                RebuildFilteredDetailSongs();
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task OpenAlbumDetailAsync(AlbumItem album)
    {
        if (album == null) return;
        CurrentSection = "Detail";
        DetailType = "album";
        DetailPlaylistId = album.Id;
        DetailBadgeText = "ALBUM";
        DetailTitle = album.Name;
        DetailCreatorName = album.ArtistName;
        DetailCreatorAvatarUrl = album.Artists.FirstOrDefault()?.PicUrl ?? string.Empty;
        DetailSongCountText = $"{album.Size} 首音乐";
        DetailSubtitle = $"{album.ArtistName} · {album.PublishDateText}";
        DetailDescription = album.Description;
        DetailCoverUrl = album.PicUrl;
        CanCollectDetail = false;
        IsDetailCollected = false;
        CanRemoveFromCurrentDetail = false;
        IsDetailSelecting = false;
        _detailSearchKeyword = string.Empty;
        OnPropertyChanged(nameof(DetailSearchKeyword));
        DetailSongs.Clear();
        FilteredDetailSongs.Clear();

        IsLoading = true;
        try
        {
            var (fullAlbum, songs) = await Api.GetAlbumDetailAsync(album.Id);
            if (fullAlbum != null)
            {
                DetailDescription = fullAlbum.Description;
                if (!string.IsNullOrWhiteSpace(fullAlbum.ArtistName))
                {
                    DetailCreatorName = fullAlbum.ArtistName;
                }
            }
            DetailSongCountText = $"{songs.Count} 首音乐";
            foreach (var s in songs)
            {
                s.IsLiked = Config.IsFavorite(s.Id);
                DetailSongs.Add(s);
            }
            RebuildFilteredDetailSongs();
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task OpenArtistDetailAsync(ArtistItem artist)
    {
        if (artist == null) return;
        CurrentSection = "Detail";
        DetailType = "artist";
        DetailPlaylistId = artist.Id;
        DetailBadgeText = "ARTIST";
        DetailTitle = artist.Name;
        DetailCreatorName = artist.Name;
        DetailCreatorAvatarUrl = artist.PicUrl;
        DetailSongCountText = $"{artist.MusicSize} 首音乐";
        DetailSubtitle = $"单曲 {artist.MusicSize} · 专辑 {artist.AlbumSize}";
        DetailDescription = artist.BriefDesc;
        DetailCoverUrl = artist.PicUrl;
        CanCollectDetail = false;
        IsDetailCollected = false;
        CanRemoveFromCurrentDetail = false;
        IsDetailSelecting = false;
        _detailSearchKeyword = string.Empty;
        OnPropertyChanged(nameof(DetailSearchKeyword));
        DetailSongs.Clear();
        FilteredDetailSongs.Clear();

        IsLoading = true;
        try
        {
            var (fullArtist, songs) = await Api.GetArtistDetailAsync(artist.Id);
            if (fullArtist != null && !string.IsNullOrWhiteSpace(fullArtist.BriefDesc))
            {
                DetailDescription = fullArtist.BriefDesc;
            }
            DetailSongCountText = $"{songs.Count} 首音乐";
            foreach (var s in songs)
            {
                s.IsLiked = Config.IsFavorite(s.Id);
                DetailSongs.Add(s);
            }
            RebuildFilteredDetailSongs();
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task ExecuteSearchAsync(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return;
        CurrentSection = "Search";
        IsLoading = true;
        StatusToast = $"正在搜索 \"{keyword}\"...";

        try
        {
            var songsTask = Api.SearchSongsAsync(keyword, 40);
            var playlistsTask = Api.SearchPlaylistsAsync(keyword, 20);
            var albumsTask = Api.SearchAlbumsAsync(keyword, 20);
            var artistsTask = Api.SearchArtistsAsync(keyword, 20);
            var mvsTask = Api.SearchMvsAsync(keyword, 16);
            var biliTask = Bilibili.SearchVideosAsync(keyword, 1, 20);

            await Task.WhenAll(songsTask, playlistsTask, albumsTask, artistsTask, mvsTask, biliTask);

            SearchSongs.Clear();
            foreach (var s in songsTask.Result)
            {
                s.IsLiked = Config.IsFavorite(s.Id);
                SearchSongs.Add(s);
            }

            SearchPlaylists.Clear();
            foreach (var p in playlistsTask.Result) SearchPlaylists.Add(p);

            SearchAlbums.Clear();
            foreach (var al in albumsTask.Result) SearchAlbums.Add(al);

            SearchArtists.Clear();
            foreach (var ar in artistsTask.Result) SearchArtists.Add(ar);

            SearchMvs.Clear();
            foreach (var mv in mvsTask.Result) SearchMvs.Add(mv);

            BilibiliVideos.Clear();
            foreach (var bv in biliTask.Result) BilibiliVideos.Add(bv);

            StatusToast = $"搜索完成：{SearchSongs.Count} 首单曲，{SearchPlaylists.Count} 个歌单，{BilibiliVideos.Count} 个 B站视频";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LoadMvSectionAsync(string? area = null, bool loadMore = false)
    {
        if (!string.IsNullOrWhiteSpace(area) && !string.Equals(area, SelectedMvArea, StringComparison.Ordinal))
        {
            SelectedMvArea = area;
            _mvOffset = 0;
            MvHasMore = true;
            MvList.Clear();
        }

        if (!loadMore)
        {
            if (MvList.Count > 0 && area == null) return;
            _mvOffset = 0;
            MvHasMore = true;
            MvList.Clear();
        }
        else
        {
            if (!MvHasMore || IsLoadingMoreMv || IsLoading) return;
        }

        if (_mvOffset == 0)
        {
            IsLoading = true;
        }
        else
        {
            IsLoadingMoreMv = true;
        }

        try
        {
            var mvs = await Api.GetTopMvsAsync(MvPageLimit, _mvOffset, SelectedMvArea);
            var existingIds = MvList.Select(x => x.Id).ToHashSet();
            foreach (var m in mvs)
            {
                if (existingIds.Add(m.Id))
                {
                    MvList.Add(m);
                }
            }
            MvHasMore = mvs.Count >= MvPageLimit / 2;
            _mvOffset += mvs.Count;
        }
        finally
        {
            IsLoading = false;
            IsLoadingMoreMv = false;
        }
    }

    public async Task PlayMvAsync(MvItem mv, int? resolution = null)
    {
        if (mv == null) return;
        Player.Pause();

        int targetRes = resolution ?? CurrentMvResolution;
        CurrentMvResolution = targetRes;
        CurrentMvItem = mv;
        CurrentMvTitle = mv.Name;
        CurrentMvArtist = string.IsNullOrWhiteSpace(mv.ArtistName) ? string.Empty : $"- {mv.ArtistName}";
        IsMvOverlayOpen = true;
        StatusToast = $"正在加载 MV《{mv.Name}》({targetRes}P)...";

        var url = await Api.GetMvPlayUrlAsync(mv.Id, targetRes);
        if (!string.IsNullOrWhiteSpace(url))
        {
            // 若分辨率切换返回相同 URL，先置空再赋值以触发播放器刷新
            if (string.Equals(_currentMvStreamUrl, url, StringComparison.Ordinal))
            {
                _currentMvStreamUrl = string.Empty;
            }
            CurrentMvStreamUrl = url;
        }
        else
        {
            StatusToast = "未能获取到该 MV 的视频流地址";
        }
    }

    public async Task PlaySongMvAsync(SongItem song)
    {
        if (song == null || song.MvId <= 0) return;
        var detail = await Api.GetMvDetailAsync(song.MvId);
        var mv = detail ?? new MvItem
        {
            Id = song.MvId,
            Name = song.Name,
            ArtistName = song.ArtistText,
            Cover = song.PicUrl,
            DurationMs = song.DurationMs
        };
        await PlayMvAsync(mv);
    }

    public async Task SwitchMvResolutionAsync(int resolution)
    {
        if (CurrentMvItem == null) return;
        await PlayMvAsync(CurrentMvItem, resolution);
    }

    public async Task PlayPrevMvAsync()
    {
        if (CurrentMvItem == null) return;
        var activeList = CurrentSection == "Search" && SearchMvs.Count > 0 ? SearchMvs : MvList;
        int idx = -1;
        for (int i = 0; i < activeList.Count; i++)
        {
            if (activeList[i].Id == CurrentMvItem.Id)
            {
                idx = i;
                break;
            }
        }
        if (idx > 0)
        {
            await PlayMvAsync(activeList[idx - 1]);
        }
        else
        {
            StatusToast = "已经是第一部 MV";
        }
    }

    public async Task PlayNextMvAsync()
    {
        if (CurrentMvItem == null) return;
        var activeList = CurrentSection == "Search" && SearchMvs.Count > 0 ? SearchMvs : MvList;
        int idx = -1;
        for (int i = 0; i < activeList.Count; i++)
        {
            if (activeList[i].Id == CurrentMvItem.Id)
            {
                idx = i;
                break;
            }
        }

        if (idx >= 0 && idx < activeList.Count - 1)
        {
            await PlayMvAsync(activeList[idx + 1]);
        }
        else if (ReferenceEquals(activeList, MvList) && MvHasMore)
        {
            await LoadMvSectionAsync(loadMore: true);
            if (idx >= 0 && idx < MvList.Count - 1)
            {
                await PlayMvAsync(MvList[idx + 1]);
            }
            else
            {
                CloseMvOverlay();
            }
        }
        else
        {
            StatusToast = "已是最后一部 MV";
        }
    }

    public void CloseMvOverlay()
    {
        IsMvOverlayOpen = false;
        CurrentMvStreamUrl = string.Empty;
    }

    public async Task PlayBilibiliVideoAsAudioAsync(BilibiliVideoItem video)
    {
        if (video == null) return;
        StatusToast = $"正在解析 B站音频: {video.Title}...";
        var detail = await Bilibili.GetVideoDetailAsync(video.Bvid) ?? video;
        var song = BilibiliService.ToSongItem(detail, detail.Pages.FirstOrDefault());
        await Player.PlaySongAsync(song);
    }

    public async Task ScanLocalMusicAsync()
    {
        IsLoading = true;
        StatusToast = "正在扫描本地音乐文件夹...";
        try
        {
            SyncConfiguredFoldersList();
            var songs = await LocalScanner.ScanAllDirectoriesAsync(count =>
            {
                RunOnUi(() => StatusToast = $"正在扫描本地音乐... 已发现 {count} 首歌曲");
            });

            LocalSongs.Clear();
            foreach (var s in songs)
            {
                s.IsLiked = Config.IsFavorite(s.Id);
                LocalSongs.Add(s);
            }

            RebuildLocalMusicViewData();
            await Config.SaveLocalSongsAsync(LocalSongs);
            StatusToast = $"本地音乐扫描完成，共发现 {LocalSongs.Count} 首歌曲";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void SyncConfiguredFoldersList()
    {
        var folders = LocalScanner.GetConfiguredRootFolders();
        ConfiguredLocalFolders.Clear();
        foreach (var f in folders)
        {
            ConfiguredLocalFolders.Add(f);
        }
    }

    /// <summary>
    /// 根据当前搜索词与扫描结果重新构建本地音乐文件夹树与列表视图（对应 local-music/index.vue）
    /// </summary>
    public void RebuildLocalMusicViewData()
    {
        var keyword = LocalSearchKeyword.Trim();
        var filtered = string.IsNullOrWhiteSpace(keyword)
            ? LocalSongs.ToList()
            : LocalSongs.Where(s =>
                s.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                s.ArtistText.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                s.AlbumText.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                (s.LocalFilePath?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        FilteredLocalSongs.Clear();
        foreach (var s in filtered) FilteredLocalSongs.Add(s);

        var previousKey = SelectedFolderNode?.Key;
        _localFolderRoots = LocalScanner.BuildLocalMusicFolderTree(filtered);

        // 若正在搜索，自动展开所有树节点方便直接定位
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            foreach (var root in _localFolderRoots)
            {
                ExpandAllNodesRecursive(root);
            }
        }

        RootFolderNodes.Clear();
        foreach (var root in _localFolderRoots)
        {
            RootFolderNodes.Add(root);
        }

        RefreshVisibleFolderNodes(previousKey);
        LocalMusicCountBadgeText = $"{filtered.Count} 首歌曲";
    }

    private static void ExpandAllNodesRecursive(LocalMusicFolderNode node)
    {
        node.IsExpanded = true;
        foreach (var child in node.Children)
        {
            ExpandAllNodesRecursive(child);
        }
    }

    public void RefreshVisibleFolderNodes(string? preferredSelectKey = null)
    {
        var flat = LocalMusicScannerService.FlattenVisibleFolderTree(_localFolderRoots);
        var targetKeys = new HashSet<string>(flat.Select(n => n.Key), StringComparer.OrdinalIgnoreCase);

        // 1. 增量移除已收起或不再显示的节点（不调用 Clear，保留已有容器以触发 Win11 原生收起过渡动画且不跳动）
        for (int i = VisibleFolderNodes.Count - 1; i >= 0; i--)
        {
            if (!targetKeys.Contains(VisibleFolderNodes[i].Key))
            {
                VisibleFolderNodes.RemoveAt(i);
            }
        }

        // 2. 增量插入新展开的子文件夹节点（触发 Win11 原生 AddDeleteThemeTransition + RepositionThemeTransition）
        for (int i = 0; i < flat.Count; i++)
        {
            var targetNode = flat[i];
            if (i < VisibleFolderNodes.Count)
            {
                if (string.Equals(VisibleFolderNodes[i].Key, targetNode.Key, StringComparison.OrdinalIgnoreCase))
                {
                    if (!ReferenceEquals(VisibleFolderNodes[i], targetNode))
                    {
                        VisibleFolderNodes[i] = targetNode;
                    }
                    continue;
                }

                int existingIndex = -1;
                for (int j = i + 1; j < VisibleFolderNodes.Count; j++)
                {
                    if (string.Equals(VisibleFolderNodes[j].Key, targetNode.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        existingIndex = j;
                        break;
                    }
                }

                if (existingIndex >= 0)
                {
                    VisibleFolderNodes.Move(existingIndex, i);
                    if (!ReferenceEquals(VisibleFolderNodes[i], targetNode))
                    {
                        VisibleFolderNodes[i] = targetNode;
                    }
                }
                else
                {
                    VisibleFolderNodes.Insert(i, targetNode);
                }
            }
            else
            {
                VisibleFolderNodes.Add(targetNode);
            }
        }

        var targetKey = preferredSelectKey ?? SelectedFolderNode?.Key;
        if (string.IsNullOrWhiteSpace(targetKey))
        {
            // 若当前记忆歌曲为本地歌曲，优先定位其所在的文件夹节点
            var playingPath = Player.CurrentSong?.LocalFilePath;
            if (!string.IsNullOrWhiteSpace(playingPath))
            {
                var dirPath = Path.GetDirectoryName(playingPath);
                var playingFolder = LocalMusicScannerService.ExpandAndFindNodeByPath(_localFolderRoots, dirPath);
                if (playingFolder != null)
                {
                    targetKey = playingFolder.Key;
                    flat = LocalMusicScannerService.FlattenVisibleFolderTree(_localFolderRoots);
                }
            }
        }
        if (string.IsNullOrWhiteSpace(targetKey))
        {
            targetKey = Config.Settings.LastSelectedLocalFolderKey;
        }

        var nextSelected = (!string.IsNullOrWhiteSpace(targetKey)
            ? flat.FirstOrDefault(n => string.Equals(n.Key, targetKey, StringComparison.OrdinalIgnoreCase))
            : null)
            ?? flat.FirstOrDefault(n => n.Songs.Count > 0)
            ?? flat.FirstOrDefault();

        SelectLocalFolderNode(nextSelected);
    }

    public void SelectLocalFolderNode(LocalMusicFolderNode? node)
    {
        foreach (var n in VisibleFolderNodes)
        {
            n.IsSelected = node != null && string.Equals(n.Key, node.Key, StringComparison.OrdinalIgnoreCase);
        }

        if (node != null && !string.Equals(Config.Settings.LastSelectedLocalFolderKey, node.Key, StringComparison.OrdinalIgnoreCase))
        {
            Config.Settings.LastSelectedLocalFolderKey = node.Key;
            _ = Config.SaveSettingsAsync();
        }

        if (ReferenceEquals(SelectedFolderNode, node) &&
            node != null &&
            CurrentFolderSongs.Count == node.Songs.Count)
        {
            return;
        }

        SelectedFolderNode = node;
        CurrentFolderSongs.Clear();
        var currentPlayingId = Player.CurrentSong?.Id;
        if (node != null)
        {
            foreach (var s in node.Songs)
            {
                s.IsPlaying = !string.IsNullOrEmpty(currentPlayingId) && s.Id == currentPlayingId;
                s.IsAudioActivelyPlaying = s.IsPlaying && Player.IsPlaying;
                CurrentFolderSongs.Add(s);
            }
        }
    }

    public void ToggleExpandFolderNode(LocalMusicFolderNode node)
    {
        if (node == null || node.Children.Count == 0) return;
        node.IsExpanded = !node.IsExpanded;
        RefreshVisibleFolderNodes(SelectedFolderNode?.Key ?? node.Key);
    }

    public async Task AddLocalMusicFolderAsync(string folderPath)
    {
        var normalized = LocalMusicScannerService.NormalizeFolderPath(folderPath);
        if (string.IsNullOrWhiteSpace(normalized)) return;

        var dirs = LocalScanner.GetConfiguredRootFolders();
        if (dirs.Any(d => string.Equals(d, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            StatusToast = $"扫描文件夹已存在：{normalized}";
            return;
        }

        dirs.Add(normalized);
        Config.Settings.LocalMusicDirectories = dirs;
        await Config.SaveSettingsAsync();
        await ScanLocalMusicAsync();
    }

    public async Task RemoveLocalMusicFolderAsync(string folderPath)
    {
        var normalized = LocalMusicScannerService.NormalizeFolderPath(folderPath);
        var dirs = LocalScanner.GetConfiguredRootFolders();
        dirs.RemoveAll(d => string.Equals(d, normalized, StringComparison.OrdinalIgnoreCase));
        Config.Settings.LocalMusicDirectories = dirs;
        await Config.SaveSettingsAsync();
        await ScanLocalMusicAsync();
    }

    public async Task MoveLocalMusicFolderAsync(string folderPath, int delta)
    {
        var normalized = LocalMusicScannerService.NormalizeFolderPath(folderPath);
        var dirs = LocalScanner.GetConfiguredRootFolders();
        int idx = dirs.FindIndex(d => string.Equals(d, normalized, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        int targetIdx = idx + delta;
        if (targetIdx < 0 || targetIdx >= dirs.Count) return;

        (dirs[idx], dirs[targetIdx]) = (dirs[targetIdx], dirs[idx]);
        Config.Settings.LocalMusicDirectories = dirs;
        await Config.SaveSettingsAsync();
        SyncConfiguredFoldersList();
        RebuildLocalMusicViewData();
    }

    /// <summary>
    /// 从本地音乐列表中移除指定歌曲（不删除磁盘原文件，对应 local-music/index.vue removeFromLocalList）
    /// </summary>
    public async Task RemoveSongFromLocalListAsync(SongItem song)
    {
        if (song == null) return;
        if (!string.IsNullOrWhiteSpace(song.LocalFilePath))
        {
            Config.Settings.RemovedLocalSongPaths.Add(LocalMusicScannerService.NormalizeFolderPath(song.LocalFilePath));
            await Config.SaveSettingsAsync();
        }
        LocalSongs.Remove(song);
        RebuildLocalMusicViewData();
        await Config.SaveLocalSongsAsync(LocalSongs);
        StatusToast = $"已从本地音乐列表移除：《{song.Name}》";
    }

    /// <summary>
    /// 定位当前正在播放的本地歌曲所在文件夹与行项（对应 local-music/index.vue handleLocatePlayingSong）
    /// </summary>
    public (LocalMusicFolderNode? FolderNode, SongItem? Song) LocateCurrentPlayingLocalSong()
    {
        var playing = Player.CurrentSong;
        if (playing == null || string.IsNullOrWhiteSpace(playing.LocalFilePath))
        {
            StatusToast = "当前未播放本地音乐文件";
            return (null, null);
        }

        var matchedSong = LocalSongs.FirstOrDefault(s =>
            string.Equals(s.LocalFilePath, playing.LocalFilePath, StringComparison.OrdinalIgnoreCase) ||
            s.Id == playing.Id);
        if (matchedSong == null)
        {
            StatusToast = "当前播放歌曲不在已扫描的本地音乐列表中";
            return (null, null);
        }

        var dirPath = Path.GetDirectoryName(matchedSong.LocalFilePath);
        var folderNode = LocalMusicScannerService.ExpandAndFindNodeByPath(_localFolderRoots, dirPath);
        if (folderNode != null)
        {
            RefreshVisibleFolderNodes(folderNode.Key);
        }

        StatusToast = $"已定位到当前播放歌曲：《{matchedSong.Name}》";
        return (SelectedFolderNode, matchedSong);
    }

    // ==================== QQ 音乐双平台账号与独立歌单中心 ====================

    public async Task RefreshQqUserProfileAsync()
    {
        var uin = Config.Settings.QqMusicUin;
        var cookie = Config.Settings.QqMusicCookie;
        if (string.IsNullOrWhiteSpace(uin) && string.IsNullOrWhiteSpace(cookie))
        {
            CurrentQqUser = null;
            QqLoginStatusText = "未登录 QQ 音乐（支持手机 QQ 扫码或 QQ 音乐 Cookie 登录）";
            return;
        }

        IsLoading = true;
        try
        {
            var (profile, created, collected) = await QqMusic.GetUserProfileAndPlaylistsAsync(uin, cookie);
            if (profile != null)
            {
                CurrentQqUser = profile;
                Config.Settings.QqMusicUin = profile.Uin;
                Config.Settings.QqMusicNickname = profile.Nickname;
                await Config.SaveSettingsAsync();

                QqCreatedPlaylists.Clear();
                foreach (var p in created) QqCreatedPlaylists.Add(p);

                QqCollectedPlaylists.Clear();
                foreach (var p in collected) QqCollectedPlaylists.Add(p);

                QqLoginStatusText = $"已登录 QQ 音乐：{profile.Nickname} (QQ: {profile.Uin}) · 创建歌单 {created.Count} 个 · 收藏歌单 {collected.Count} 个";
            }
            else
            {
                CurrentQqUser = null;
                QqLoginStatusText = "QQ 音乐登录态已失效，请重新扫码或更新 Cookie";
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LogoutQqMusicAsync()
    {
        Config.Settings.QqMusicCookie = string.Empty;
        Config.Settings.QqMusicUin = string.Empty;
        Config.Settings.QqMusicNickname = string.Empty;
        await Config.SaveSettingsAsync();

        CurrentQqUser = null;
        QqCreatedPlaylists.Clear();
        QqCollectedPlaylists.Clear();
        QqLoginStatusText = "未登录 QQ 音乐（支持手机 QQ 扫码或 QQ 音乐 Cookie 登录）";
        StatusToast = "已退出 QQ 音乐账号";
    }

    public async Task LogoutAllMusicAccountsAsync()
    {
        await LogoutUserAsync();
        await LogoutQqMusicAsync();
        StatusToast = "已退出全部音乐平台账号";
    }

    public async Task RefreshUserProfileAsync()
    {
        var profile = await Api.GetCurrentUserProfileAsync();
        CurrentUser = profile;
        if (profile != null)
        {
            await PopulateUserDataAsync(profile, "Cookie/扫码");
        }
        else
        {
            LoginStatusText = "未登录或 Cookie 已失效";
        }
    }

    /// <summary>
    /// 原版特色：UID 免密登录（无需密码即可同步任意网易云 UID 的歌单与听歌排行）
    /// </summary>
    public async Task LoginByUidAsync(long uid)
    {
        if (uid <= 0) return;
        IsLoading = true;
        try
        {
            var profile = await Api.GetUserDetailByUidAsync(uid);
            if (profile != null)
            {
                CurrentUser = profile;
                Config.Settings.SavedUid = uid;
                Config.Settings.LoginType = "uid";
                await Config.SaveSettingsAsync();
                await PopulateUserDataAsync(profile, "UID 免密");
                StatusToast = $"UID 免密登录成功：{profile.Nickname}";
            }
            else
            {
                StatusToast = $"未找到 UID {uid} 对应的网易云用户";
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task PopulateUserDataAsync(UserProfile profile, string modeLabel)
    {
        LoginStatusText = $"已登录 ({modeLabel})：{profile.Nickname} · Lv.{profile.Level} · 关注 {profile.Follows} · 粉丝 {profile.Followeds} · 累计听歌 {profile.ListenSongs} 首";

        var playlistsTask = Api.GetUserPlaylistsAsync(profile.UserId);
        var albumsTask = Api.GetUserSubscribedAlbumsAsync(30);
        var recordTask = Api.GetUserPlayRecordAsync(profile.UserId, 1);

        await Task.WhenAll(playlistsTask, albumsTask, recordTask);

        UserPlaylists.Clear();
        UserCreatedPlaylists.Clear();
        UserFavoritePlaylists.Clear();

        foreach (var p in playlistsTask.Result)
        {
            UserPlaylists.Add(p);
            if (p.CreatorUserId == profile.UserId || p.CreatorName == profile.Nickname)
            {
                UserCreatedPlaylists.Add(p);
            }
            else
            {
                UserFavoritePlaylists.Add(p);
            }
        }

        UserSubscribedAlbums.Clear();
        foreach (var al in albumsTask.Result) UserSubscribedAlbums.Add(al);

        UserPlayRecords.Clear();
        foreach (var r in recordTask.Result) UserPlayRecords.Add(r);
    }

    public async Task LoadUserPlayRecordAsync(int type = 1)
    {
        if (CurrentUser == null) return;
        IsLoading = true;
        try
        {
            var records = await Api.GetUserPlayRecordAsync(CurrentUser.UserId, type);
            UserPlayRecords.Clear();
            foreach (var r in records) UserPlayRecords.Add(r);
            StatusToast = type == 1
                ? $"已加载最近一周听歌排行（共 {records.Count} 首）"
                : $"已加载所有时间听歌排行（共 {records.Count} 首）";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task ImportPlaylistByIdOrUrlAsync(string rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput)) return;
        long playlistId = 0;

        // 支持直接输入数字 ID 或粘贴网易云歌单链接（含 id=xxxx）
        var match = global::System.Text.RegularExpressions.Regex.Match(rawInput, @"id=(\d+)");
        if (match.Success)
        {
            long.TryParse(match.Groups[1].Value, out playlistId);
        }
        else
        {
            long.TryParse(rawInput.Trim(), out playlistId);
        }

        if (playlistId <= 0)
        {
            StatusToast = "请输入有效的网易云歌单 ID 或包含 id= 的链接";
            return;
        }

        var detail = await Api.GetPlaylistDetailAsync(playlistId);
        if (detail != null)
        {
            if (UserCreatedPlaylists.All(p => p.Id != detail.Id))
            {
                UserCreatedPlaylists.Insert(0, detail);
            }
            await OpenPlaylistDetailAsync(detail);
            StatusToast = $"成功导入并打开歌单《{detail.Name}》";
        }
        else
        {
            StatusToast = $"未能找到 ID 为 {playlistId} 的歌单";
        }
    }

    public async Task LogoutUserAsync()
    {
        Config.Settings.NeteaseCookie = string.Empty;
        Config.Settings.SavedUid = 0;
        Config.Settings.LoginType = string.Empty;
        await Config.SaveSettingsAsync();

        CurrentUser = null;
        UserPlaylists.Clear();
        UserCreatedPlaylists.Clear();
        UserFavoritePlaylists.Clear();
        UserSubscribedAlbums.Clear();
        UserPlayRecords.Clear();
        LoginStatusText = "未登录（支持扫码、UID 免密或 Cookie 登录）";
        StatusToast = "已退出登录";
    }

    public async Task LoadCommentsForCurrentSongAsync()
    {
        if (Player.CurrentSong == null || Player.CurrentSong.NumericId <= 0) return;
        var comments = await Api.GetSongCommentsAsync(Player.CurrentSong.NumericId, 30);
        RunOnUi(() =>
        {
            CurrentSongComments.Clear();
            foreach (var c in comments) CurrentSongComments.Add(c);
        });
    }

    // ==================== 播放器事件回调 ====================

    private void SyncSongPlayingStates(string? currentId, bool activelyPlaying)
    {
        IEnumerable<SongItem>[] lists =
        [
            Player.Queue,
            DetailSongs,
            FilteredDetailSongs,
            DailyRecommendSongs,
            DailyPreviewSongs,
            HomeNewSongs,
            SearchSongs,
            HistorySongs,
            FavoriteSongs,
            LocalSongs,
            FilteredLocalSongs,
            CurrentFolderSongs
        ];

        foreach (var list in lists)
        {
            foreach (var s in list)
            {
                s.IsPlaying = !string.IsNullOrEmpty(currentId) && s.Id == currentId;
                s.IsAudioActivelyPlaying = s.IsPlaying && activelyPlaying;
            }
        }
    }

    private void OnPlayerSongChanged(SongItem? song)
    {
        RunOnUi(() =>
        {
            if (song == null) return;
            SyncSongPlayingStates(song.Id, Player.IsPlaying);
            UpdateCurrentSongDisplay(song);
            RefreshHistoryAndFavorites();
            _ = LoadCommentsForCurrentSongAsync();
        });
    }

    private void UpdateCurrentSongDisplay(SongItem song)
    {
        CurrentSongName = song.Name;
        CurrentArtistName = song.ArtistText;
        CurrentCoverUrl = song.CoverThumbnailUrl;
        IsCurrentSongLiked = song.IsLiked;
        CurrentPlatformBadge = (song.ResolvedPlatform ?? song.Source).ToUpperInvariant();
        CurrentQualityBadge = !string.IsNullOrWhiteSpace(song.PlayingQualityBadge)
            ? song.PlayingQualityBadge
            : (song.IsLocalSong
                ? $"本地 {Path.GetExtension(song.LocalFilePath ?? string.Empty).TrimStart('.').ToUpperInvariant()}".Trim()
                : MusicQualityLevel.GetShortBadgeName(Config.Settings.MusicQuality));
        DurationSeconds = Math.Max(1, song.DurationMs / 1000.0);
        DurationText = song.DurationText;
        LyricOffsetText = "0.0s";
    }

    private void OnPlayerSongEndedNaturally()
    {
        RunOnUi(async () =>
        {
            if (IsFmModeActive && Config.Settings.PlayMode != PlayMode.LoopOne)
            {
                await NextFmAsync();
            }
            else
            {
                await Player.NextAsync(isAutoEnded: true);
            }
        });
    }

    private void OnPlayerStateChanged(bool playing)
    {
        RunOnUi(() =>
        {
            IsPlaying = playing;
            SyncSongPlayingStates(Player.CurrentSong?.Id, playing);
            if (Player.CurrentSong != null)
            {
                CurrentPlatformBadge = (Player.CurrentSong.ResolvedPlatform ?? Player.CurrentSong.Source).ToUpperInvariant();
                if (!string.IsNullOrWhiteSpace(Player.CurrentSong.PlayingQualityBadge))
                {
                    CurrentQualityBadge = Player.CurrentSong.PlayingQualityBadge;
                }
            }
        });
    }

    private void OnPlayerPositionChanged(TimeSpan pos, TimeSpan dur)
    {
        if (IsDraggingSeek) return;
        RunOnUi(() =>
        {
            PositionSeconds = pos.TotalSeconds;
            if (dur.TotalSeconds > 1)
            {
                DurationSeconds = dur.TotalSeconds;
                DurationText = $"{(int)dur.TotalMinutes:D2}:{dur.Seconds:D2}";
            }
            PositionText = $"{(int)pos.TotalMinutes:D2}:{pos.Seconds:D2}";
        });
    }

    private void OnPlayerLyricLoaded(ParsedLyric lyric)
    {
        RunOnUi(() =>
        {
            LyricLines.Clear();
            foreach (var line in lyric.Lines)
            {
                LyricLines.Add(line);
            }
            var first = lyric.Lines.FirstOrDefault();
            CurrentLyricText = first?.Text ?? "纯音乐，请欣赏";
            CurrentLyricTranslation = first?.TranslationText ?? string.Empty;
        });
    }

    private void OnPlayerLyricLineChanged(LyricLine? line)
    {
        if (line == null) return;
        RunOnUi(() =>
        {
            CurrentLyricText = line.Text;
            CurrentLyricTranslation = line.TranslationText;
        });
    }
}
