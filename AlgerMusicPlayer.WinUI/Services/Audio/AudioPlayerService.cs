using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.Api;
using AlgerMusicPlayer.WinUI.Services.Lyric;
using AlgerMusicPlayer.WinUI.Services.System;
using AlgerMusicPlayer.WinUI.Services.Unblock;
using Microsoft.UI.Dispatching;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Windows.Media;
using Windows.Storage.Streams;

namespace AlgerMusicPlayer.WinUI.Services.Audio;

/// <summary>
/// 核心音频播放引擎（集成 NAudio、WASAPI 5.1/7.1 环绕声原生输出、10 段 EQ 均衡器、多音源自动解灰、歌词同步、定时停止与 Windows 原生 SMTC）
/// </summary>
public class AudioPlayerService : IDisposable
{
    private readonly ConfigService _configService;
    private readonly NeteaseApiClient _apiClient;
    private readonly QqMusicService _qqMusicService;
    private readonly ThirdPartyMusicService _thirdPartyService;
    private readonly UnblockMusicService _unblockService;
    private readonly BilibiliService _bilibiliService;
    private readonly LyricService _lyricService;
    private readonly LxScriptHostService _lxHost;

    private IWavePlayer? _outputDevice;
    private WaveStream? _waveReader;
    private Stream? _activeFileStream;
    private EqualizerSampleProvider? _eqProvider;
    private MultichannelGainSampleProvider? _multichannelGainProvider;
    private FadeSampleProvider? _fadeProvider;
    private VolumeSampleProvider? _volumeProvider;
    private readonly MpvPlaybackService _mpvService = new();
    private string _activeBackend = "naudio"; // "naudio" | "mpv"
    private CancellationTokenSource? _loadCts;
    private readonly Timer _positionTimer;
    private Timer? _sleepTimer;
    private SystemMediaTransportControls? _smtc;
    private DispatcherQueue? _dispatcher;
    private bool _suppressQueueAutoSave;
    private volatile bool _isStoppingManually;
    private long _lastAutoAdvanceTickMs;
    private int _currentPlaySessionId;
    private readonly object _deviceLock = new();

    // 官方先播 320k、后台寻找无损后无缝切换的待升级记录
    private (string SongId, int OfficialBr, bool IsOfficialLossless)? _pendingLosslessUpgrade;
    // 预解析的下一首播放地址缓存（songId -> url, 解析时间）
    private readonly Dictionary<string, (string Url, DateTime Time)> _prefetchedSources = new();
    private readonly object _prefetchLock = new();
    // 外部/解灰音源单曲实测最高音质上限缓存（songId|platform -> 最高可达档位、码率、文件大小）
    private readonly ConcurrentDictionary<string, (int MaxRank, int MaxKbps, long MaxSize)> _observedExternalCeiling = new(StringComparer.OrdinalIgnoreCase);
    // 在线音频流中途中断时已重连过的歌曲，避免反复重试
    private string? _streamRetrySongId;
    // 当前播放队列（或单曲切换）记住的定向音源 key（例如 lx:<scriptId> / gdmusic / kuwo；null 表示多源自动并发）
    private string? _activeQueueForcePlatform;

    public string? ActiveForcePlatform => _activeQueueForcePlatform;

    public ObservableCollection<SongItem> Queue { get; } = [];
    public SongItem? CurrentSong { get; private set; }
    public ParsedLyric CurrentLyric { get; private set; } = new();
    public LyricLine? CurrentLyricLine { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool IsBuffering { get; private set; }
    public TimeSpan Position => _activeBackend == "mpv"
        ? TimeSpan.FromSeconds(Math.Max(0, _mpvService.CurrentTimeSeconds))
        : (_waveReader?.CurrentTime ?? TimeSpan.Zero);
    public TimeSpan Duration => _activeBackend == "mpv" && _mpvService.DurationSeconds > 0
        ? TimeSpan.FromSeconds(_mpvService.DurationSeconds)
        : (_waveReader?.TotalTime ?? TimeSpan.FromMilliseconds(CurrentSong?.DurationMs ?? 0));
    public float PeakLevel => _activeBackend == "mpv"
        ? (IsPlaying ? 0.55f : 0f)
        : (_eqProvider?.CurrentPeakLevel ?? 0f);
    public DateTime? SleepTimerEndTime { get; private set; }
    public int LyricOffsetMs { get; set; }
    public float PlaybackRate { get; private set; } = 1.0f;

    public event Action<SongItem?>? SongChanged;
    public event Action<bool>? PlayStateChanged;
    public event Action<TimeSpan, TimeSpan>? PositionChanged;
    public event Action<ParsedLyric>? LyricLoaded;
    public event Action<LyricLine?>? LyricLineChanged;
    public event Action<string>? QualityBadgeChanged;
    public event Action<string, string>? PlaybackBadgesChanged;
    public event Action? SongEndedNaturally;
    public event Action<string>? StatusMessage;

    public AudioPlayerService(
        ConfigService configService,
        NeteaseApiClient apiClient,
        QqMusicService qqMusicService,
        ThirdPartyMusicService thirdPartyService,
        UnblockMusicService unblockService,
        BilibiliService bilibiliService,
        LyricService lyricService,
        LxScriptHostService lxHost)
    {
        _configService = configService;
        _apiClient = apiClient;
        _qqMusicService = qqMusicService;
        _thirdPartyService = thirdPartyService;
        _unblockService = unblockService;
        _bilibiliService = bilibiliService;
        _lyricService = lyricService;
        _lxHost = lxHost;

        _suppressQueueAutoSave = true;
        foreach (var s in _configService.SavedQueue)
        {
            s.IsLiked = _configService.IsFavorite(s.Id);
            Queue.Add(s);
        }
        _suppressQueueAutoSave = false;

        // 恢复上次退出前正在播放的歌曲与状态
        var savedId = _configService.Settings.LastPlayedSongId;
        var savedPath = _configService.Settings.LastPlayedSongPath;
        SongItem? restored = null;
        if (!string.IsNullOrWhiteSpace(savedId))
        {
            restored = Queue.FirstOrDefault(s => string.Equals(s.Id, savedId, StringComparison.OrdinalIgnoreCase));
        }
        if (restored == null && !string.IsNullOrWhiteSpace(savedPath))
        {
            restored = Queue.FirstOrDefault(s =>
                !string.IsNullOrWhiteSpace(s.LocalFilePath) &&
                string.Equals(s.LocalFilePath, savedPath, StringComparison.OrdinalIgnoreCase));
        }
        if (restored == null && _configService.Settings.LastPlayedSong != null)
        {
            restored = _configService.Settings.LastPlayedSong;
            restored.IsLiked = _configService.IsFavorite(restored.Id);
        }
        restored ??= Queue.FirstOrDefault();

        if (restored != null)
        {
            CurrentSong = restored;
            foreach (var q in Queue)
            {
                q.IsPlaying = string.Equals(q.Id, restored.Id, StringComparison.OrdinalIgnoreCase);
            }
        }

        // 监听列表拖拽重排与单曲增删，自动持久化队列顺序
        Queue.CollectionChanged += (_, _) =>
        {
            if (!_suppressQueueAutoSave)
            {
                _ = _configService.SaveQueueAsync(Queue);
            }
        };

        // 后台预热内置 mpv 进程与 IPC 管道，确保首个第三方/国际流起播不卡冷启动
        _mpvService.PreWarm();

        // 绑定内置 mpv 事件（1:1 还原 .worktrees/local-music-folder-tree/src/renderer/services/audioService.ts handleMpvEvent）
        _mpvService.Ended += () =>
        {
            if (_activeBackend != "mpv") return;
            TriggerAutoNextOnUiThread();
        };
        _mpvService.WarningReceived += msg =>
        {
            if (_activeBackend != "mpv") return;
            StatusMessage?.Invoke(msg);
        };
        _mpvService.ErrorReceived += msg =>
        {
            if (_activeBackend != "mpv") return;
            StatusMessage?.Invoke(msg);
        };
        _mpvService.InputAudioParamsChanged += audioParams =>
        {
            if (_activeBackend != "mpv" || audioParams?.ChannelCount is not > 0 || CurrentSong == null) return;
            int ch = audioParams.ChannelCount.Value;
            var songRef = CurrentSong;
            RunOnUi(() =>
            {
                songRef.ChannelCount = ch;
                if (ch >= 6)
                {
                    string chLabel = ch == 6 ? "5.1" : ch == 8 ? "7.1" : $"{ch}CH";
                    var currentBadge = songRef.PlayingQualityBadge ?? string.Empty;
                    if (!currentBadge.Contains(chLabel, StringComparison.OrdinalIgnoreCase))
                    {
                        songRef.PlayingQualityBadge = string.IsNullOrWhiteSpace(currentBadge) || currentBadge == "LOCAL"
                            ? $"{chLabel} FLAC"
                            : $"{chLabel} {currentBadge}";
                        QualityBadgeChanged?.Invoke(songRef.PlayingQualityBadge);
                    }
                }
            });
        };

        _positionTimer = new Timer(OnPositionTick, null, 150, 150);
    }

    public void AttachDispatcher(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
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
    /// 上移或下移播放队列中的指定歌曲（delta = -1 上移，+1 下移）
    /// </summary>
    public bool MoveQueueItem(SongItem song, int delta)
    {
        if (song == null || Queue.Count <= 1) return false;
        int idx = Queue.IndexOf(song);
        if (idx < 0)
        {
            var matched = Queue.FirstOrDefault(s => s.Id == song.Id);
            if (matched != null) idx = Queue.IndexOf(matched);
        }
        if (idx < 0) return false;

        int targetIdx = Math.Clamp(idx + delta, 0, Queue.Count - 1);
        if (targetIdx == idx) return false;

        Queue.Move(idx, targetIdx);
        return true;
    }

    /// <summary>
    /// 将指定歌曲移动到“下一首播放”位置（紧跟在当前播放歌曲之后；若当前无播放则移至队首）
    /// </summary>
    public bool MoveQueueItemToNext(SongItem song)
    {
        if (song == null || Queue.Count <= 1) return false;
        int idx = Queue.IndexOf(song);
        if (idx < 0)
        {
            var matched = Queue.FirstOrDefault(s => s.Id == song.Id);
            if (matched != null) idx = Queue.IndexOf(matched);
        }
        if (idx < 0) return false;

        int currentIdx = CurrentSong != null
            ? Queue.IndexOf(Queue.FirstOrDefault(s => s.Id == CurrentSong.Id)!)
            : -1;

        int targetIdx;
        if (currentIdx < 0)
        {
            targetIdx = 0;
        }
        else if (idx == currentIdx)
        {
            return false;
        }
        else if (idx < currentIdx)
        {
            targetIdx = currentIdx;
        }
        else
        {
            targetIdx = Math.Min(Queue.Count - 1, currentIdx + 1);
        }

        if (targetIdx == idx) return false;
        Queue.Move(idx, targetIdx);
        return true;
    }

    /// <summary>
    /// 从播放队列移除指定歌曲
    /// </summary>
    public bool RemoveQueueItem(SongItem song)
    {
        if (song == null) return false;
        if (Queue.Remove(song)) return true;
        var matched = Queue.FirstOrDefault(s => s.Id == song.Id);
        return matched != null && Queue.Remove(matched);
    }

    /// <summary>
    /// 绑定主窗口句柄以初始化 Windows 原生系统媒体传输控制 (SMTC)
    /// </summary>
    public void InitializeSmtc(IntPtr hWnd)
    {
        try
        {
            _smtc = SystemMediaTransportControlsInterop.GetForWindow(hWnd);
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsStopEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;
            _smtc.ButtonPressed += OnSmtcButtonPressed;
        }
        catch
        {
            // 非桌面环境或权限受限时忽略
        }
    }

    private void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        RunOnUi(() =>
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    Resume();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    Pause();
                    break;
                case SystemMediaTransportControlsButton.Stop:
                    StopPlayback();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    _ = NextAsync();
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    _ = PreviousAsync();
                    break;
            }
        });
    }

    /// <summary>
    /// 播放指定歌曲并可选替换当前播放队列
    /// </summary>
    public async Task PlaySongAsync(SongItem song, IEnumerable<SongItem>? newQueue = null, string? forcePlatform = null, TimeSpan? initialSeekPosition = null, string? preResolvedUrl = null)
    {
        if (song == null) return;

        // 处理定向音源切换：传入 "auto" 时显式清除记忆回到多源并发自动择优
        if (string.Equals(forcePlatform, "auto", StringComparison.OrdinalIgnoreCase))
        {
            _activeQueueForcePlatform = null;
            forcePlatform = null;
        }
        else if (newQueue != null)
        {
            _activeQueueForcePlatform = string.IsNullOrWhiteSpace(forcePlatform) ? null : forcePlatform;
        }
        var effectiveForcePlatform = !string.IsNullOrWhiteSpace(forcePlatform) ? forcePlatform : _activeQueueForcePlatform;

        int sessionId = Interlocked.Increment(ref _currentPlaySessionId);
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        if (initialSeekPosition == null)
        {
            _streamRetrySongId = null;
        }

        // 立即同步停止当前可能正在发声的旧音频流，防止任何重叠发声
        StopInternal();

        RunOnUi(() =>
        {
            if (newQueue != null)
            {
                _suppressQueueAutoSave = true;
                Queue.Clear();
                foreach (var item in newQueue)
                {
                    item.IsLiked = _configService.IsFavorite(item.Id);
                    Queue.Add(item);
                }
                _suppressQueueAutoSave = false;
                _ = _configService.SaveQueueAsync(Queue);
            }
            else if (Queue.All(s => s.Id != song.Id))
            {
                song.IsLiked = _configService.IsFavorite(song.Id);
                Queue.Add(song);
            }

            foreach (var q in Queue)
            {
                q.IsPlaying = q.Id == song.Id;
            }

            CurrentSong = song;
            if (initialSeekPosition == null)
            {
                LyricOffsetMs = 0;
            }
            song.IsLiked = _configService.IsFavorite(song.Id);
            song.IsLoading = true;

            // 切歌或手动切音质且需实时在线解析时，立即清空旧平台与旧音质徽章，避免误显示上一轮残留或虚假的全局设置
            if (!song.IsLocalSong && string.IsNullOrWhiteSpace(preResolvedUrl))
            {
                bool hasValidPrefetch = false;
                if (string.IsNullOrWhiteSpace(effectiveForcePlatform) && initialSeekPosition == null)
                {
                    lock (_prefetchLock)
                    {
                        hasValidPrefetch = _prefetchedSources.TryGetValue(song.Id, out var cached) &&
                                           DateTime.UtcNow - cached.Time < PrefetchTtl &&
                                           string.Equals(song.PlayMusicUrl, cached.Url, StringComparison.Ordinal) &&
                                           !string.IsNullOrWhiteSpace(song.PlayingQualityBadge);
                    }
                }

                if (!hasValidPrefetch)
                {
                    song.ResolvedPlatform = null;
                    song.PlayingQualityBadge = initialSeekPosition != null ? "切换中..." : "解析中...";
                }
            }

            // 实时持久化当前播放歌曲及所在本地文件夹，确保进程退出重进后精准记忆
            _configService.Settings.LastPlayedSongId = song.Id;
            _configService.Settings.LastPlayedSongPath = song.LocalFilePath ?? string.Empty;
            _configService.Settings.LastPlayedPositionSeconds = initialSeekPosition?.TotalSeconds ?? 0;
            _configService.Settings.LastPlayedSong = song;
            _ = _configService.SaveSettingsAsync();
        });

        IsBuffering = true;
        SongChanged?.Invoke(song);
        if (!song.IsLocalSong)
        {
            var initialPlat = FormatPlatformBadgeDisplay(song.ResolvedPlatform ?? song.Source);
            var initialBadge = !string.IsNullOrWhiteSpace(song.PlayingQualityBadge)
                ? song.PlayingQualityBadge
                : (initialSeekPosition != null ? "切换中..." : "解析中...");
            PlaybackBadgesChanged?.Invoke(initialPlat, initialBadge);
        }
        UpdateSmtcMetadata(song);

        // 异步并行加载歌词与当前歌曲可用音质详情缓存（非阻塞）
        if (initialSeekPosition == null)
        {
            _ = LoadLyricsForCurrentSongAsync(song);
            if (song.NumericId > 0)
            {
                _ = _apiClient.GetSongQualityDetailAsync(song.NumericId);
            }
            else if (song.Source == "qishui" && !string.IsNullOrWhiteSpace(song.QishuiTrackId))
            {
                _ = _thirdPartyService.GetQishuiPlayCandidatesAsync(song.QishuiTrackId, ct);
            }
        }

        try
        {
            var resolvedSource = preResolvedUrl ?? await ResolvePlayableSourceAsync(song, effectiveForcePlatform, ct, isPrefetch: false, allowFastStart: initialSeekPosition == null);
            if (sessionId != _currentPlaySessionId || ct.IsCancellationRequested) return;

            if (string.IsNullOrWhiteSpace(resolvedSource))
            {
                StatusMessage?.Invoke($"无法解析《{song.Name}》的音频源，已自动跳过");
                RunOnUi(() => song.IsLoading = false);
                IsBuffering = false;
                return;
            }

            // 本地/SMB 文件、第三方直连与国际流优先交由内置 mpv 流式播放（规避 MediaFoundationReader HTTP 多次 Range 探测慢及 Referer/403 拒绝访问问题）
            bool isLocalOrUnc = !resolvedSource.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                                !resolvedSource.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            bool preferMpvBackend = _mpvService.IsAvailable(out _) &&
                                    (isLocalOrUnc || ShouldUseMpvForOnlineStream(song, resolvedSource));

            bool startedViaMpv = false;
            if (preferMpvBackend)
            {
                string? effectiveMpvSource = resolvedSource;
                string? referrer = isLocalOrUnc ? null : GetHttpReferrerForStream(song, resolvedSource);

                lock (_deviceLock)
                {
                    StopNaudioInternal();
                }
                if (sessionId != _currentPlaySessionId || ct.IsCancellationRequested) return;

                try
                {
                    _activeBackend = "mpv";
                    _isStoppingManually = false;

                    var eq = _configService.Settings.Equalizer;
                    await _mpvService.SetChannelGainsAsync(MultichannelGainSampleProvider.ComputeGains(_configService.Settings.Multichannel));
                    await _mpvService.LoadAsync(new MpvLoadRequest
                    {
                        FilePath = effectiveMpvSource,
                        HttpReferrer = referrer,
                        ShouldPlay = true,
                        SeekTimeSeconds = initialSeekPosition?.TotalSeconds ?? 0,
                        Volume = Math.Clamp(_configService.Settings.Volume, 0f, 1f),
                        PlaybackRate = PlaybackRate,
                        EqEnabled = eq.IsEnabled,
                        EqBandGains = eq.BandGains
                    }, ct);

                    if (sessionId != _currentPlaySessionId || ct.IsCancellationRequested)
                    {
                        _ = _mpvService.StopAsync();
                        return;
                    }

                    IsPlaying = true;
                    UpdateSmtcPlayState(true);
                    PlayStateChanged?.Invoke(true);
                    startedViaMpv = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch when (!isLocalOrUnc)
                {
                    // 远程 HTTP 流若被防盗链节点拦截：B站/YT 回退本地缓存后重试 mpv，其余回退 NAudio
                    _ = _mpvService.StopAsync();
                    string? cachedFallback = null;
                    if (song.Source == "bilibili" && !string.IsNullOrWhiteSpace(song.BilibiliBvid))
                    {
                        cachedFallback = await _bilibiliService.DownloadAudioToCacheAsync(
                            resolvedSource, song.BilibiliBvid, song.BilibiliCid ?? 0, _configService.CacheDirectory, ct);
                    }
                    else if (string.Equals(song.ResolvedPlatform, "ytmusic", StringComparison.OrdinalIgnoreCase) ||
                             resolvedSource.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
                             resolvedSource.Contains(".webm", StringComparison.OrdinalIgnoreCase))
                    {
                        cachedFallback = await _unblockService.DownloadInternationalStreamToCacheAsync(
                            resolvedSource, song.Id, _configService.CacheDirectory, ct);
                    }

                    if (!string.IsNullOrWhiteSpace(cachedFallback) && File.Exists(cachedFallback))
                    {
                        _activeBackend = "mpv";
                        var eq = _configService.Settings.Equalizer;
                        await _mpvService.LoadAsync(new MpvLoadRequest
                        {
                            FilePath = cachedFallback,
                            ShouldPlay = true,
                            SeekTimeSeconds = initialSeekPosition?.TotalSeconds ?? 0,
                            Volume = Math.Clamp(_configService.Settings.Volume, 0f, 1f),
                            PlaybackRate = PlaybackRate,
                            EqEnabled = eq.IsEnabled,
                            EqBandGains = eq.BandGains
                        }, ct);
                        if (sessionId != _currentPlaySessionId || ct.IsCancellationRequested)
                        {
                            _ = _mpvService.StopAsync();
                            return;
                        }
                        IsPlaying = true;
                        UpdateSmtcPlayState(true);
                        PlayStateChanged?.Invoke(true);
                        startedViaMpv = true;
                    }
                }
            }

            if (!startedViaMpv)
            {
                if (_activeBackend == "mpv")
                {
                    await _mpvService.StopAsync();
                }
                if (sessionId != _currentPlaySessionId || ct.IsCancellationRequested) return;

                _activeBackend = "naudio";
                await Task.Run(() => StartNaudioPipeline(resolvedSource, initialSeekPosition, sessionId, ct), ct);
            }

            if (sessionId != _currentPlaySessionId || ct.IsCancellationRequested) return;

            RunOnUi(() => song.IsLoading = false);
            IsBuffering = false;
            if (initialSeekPosition == null)
            {
                await _configService.AddPlayHistoryAsync(song);
            }

            // 播放开始后：后台寻找无损并无缝切换，然后预解析下一首
            _ = RunPostStartTasksAsync(song, sessionId, ct);
        }
        catch (OperationCanceledException)
        {
            // 已切换下一首，忽略
        }
        catch (Exception ex)
        {
            if (sessionId == _currentPlaySessionId)
            {
                RunOnUi(() => song.IsLoading = false);
                IsBuffering = false;
                StatusMessage?.Invoke($"播放失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 播放开始后的后台任务：先尝试无损升级，再预解析下一首（顺序执行，避免并发调用解灰服务互相覆盖码率信息）
    /// </summary>
    private async Task RunPostStartTasksAsync(SongItem song, int sessionId, CancellationToken ct)
    {
        try
        {
            var pending = _pendingLosslessUpgrade;
            _pendingLosslessUpgrade = null;
            if (pending != null && pending.Value.SongId == song.Id)
            {
                await TryUpgradeToLosslessAsync(song, pending.Value.OfficialBr, pending.Value.IsOfficialLossless, sessionId, ct);
            }

            if (sessionId != _currentPlaySessionId || ct.IsCancellationRequested) return;
            await PrefetchNextSongAsync(song, ct);
        }
        catch
        {
            // 后台任务失败不影响当前播放
        }
    }

    /// <summary>
    /// 官方仅返回有损音质时，后台通过解灰寻找无损音源，找到后从当前进度无缝切换
    /// </summary>
    private async Task TryUpgradeToLosslessAsync(SongItem song, int officialBr, bool isOfficialLossless, int sessionId, CancellationToken ct)
    {
        // 国际平台严格走各自原生 API，禁止后台无损升级跳源
        if (song.Source?.ToLowerInvariant() is "spotify" or "apple" or "deezer" or "tidal" or "qobuz" or "ytmusic")
            return;

        var (unblockUrl, platform) = await _unblockService.MatchAudioUrlAsync(song, null, ct, preferLossless: true);
        if (string.IsNullOrWhiteSpace(unblockUrl) || sessionId != _currentPlaySessionId || ct.IsCancellationRequested) return;

        int kbps = _unblockService.LastResolvedBitrateKbps;
        bool unblockIsFlac = unblockUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase) || kbps > 400;
        bool isBetter = unblockIsFlac && (!isOfficialLossless || kbps * 1000 > officialBr);
        // 暂停中不打断用户
        if (!isBetter || !IsPlaying) return;

        var badge = _unblockService.FormatResolvedQualityBadge(unblockUrl, song, _configService.Settings.MusicQuality);
        RecordResolvedSongQuality(
            song,
            platform,
            _configService.Settings.MusicQuality,
            actualLevel: null,
            url: unblockUrl,
            kbps: kbps,
            sizeBytes: _unblockService.LastResolvedSizeBytes);
        ApplyResolvedPlaybackInfo(song, platform, unblockUrl, badge, isPrefetch: false);
        await PlaySongAsync(song, initialSeekPosition: Position, preResolvedUrl: unblockUrl);
    }

    /// <summary>
    /// 预解析队列中下一首歌曲的播放地址（随机/单曲循环模式下无法确定下一首，跳过）
    /// </summary>
    private async Task PrefetchNextSongAsync(SongItem current, CancellationToken ct)
    {
        var mode = _configService.Settings.PlayMode;
        if (mode == PlayMode.Random || mode == PlayMode.LoopOne || Queue.Count < 2) return;

        SongItem? next;
        try
        {
            int idx = Queue.IndexOf(Queue.FirstOrDefault(s => s.Id == current.Id)!);
            if (idx < 0) return;
            next = Queue[(idx + 1) % Queue.Count];
        }
        catch
        {
            return;
        }

        if (next == null || next.IsLocalSong || next.Id == current.Id) return;
        lock (_prefetchLock)
        {
            if (_prefetchedSources.TryGetValue(next.Id, out var existing) &&
                DateTime.UtcNow - existing.Time < PrefetchTtl)
            {
                return;
            }
        }

        var url = await ResolvePlayableSourceAsync(next, _activeQueueForcePlatform, ct, isPrefetch: true);
        if (string.IsNullOrWhiteSpace(url) || ct.IsCancellationRequested) return;

        lock (_prefetchLock)
        {
            _prefetchedSources[next.Id] = (url, DateTime.UtcNow);
        }
    }

    // 预解析地址有效期（在线直链通常会过期，超过该时间重新解析）
    private static readonly TimeSpan PrefetchTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 实时切换当前播放歌曲的音源通道（支持多源自动 / 指定导入脚本 / 指定自带源 / 官方源），保持进度无缝重载
    /// </summary>
    public async Task SwitchPlaybackSourceAsync(string? sourceKey, string displayName)
    {
        var normalizedKey = string.IsNullOrWhiteSpace(sourceKey) ? "auto" : sourceKey.Trim();
        _activeQueueForcePlatform = string.Equals(normalizedKey, "auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalizedKey;

        lock (_prefetchLock)
        {
            _prefetchedSources.Clear();
        }

        if (CurrentSong == null || CurrentSong.IsLocalSong)
        {
            StatusMessage?.Invoke($"默认播放音源已设为：{displayName}");
            return;
        }

        var savedPos = Position;
        StatusMessage?.Invoke($"正在切换《{CurrentSong.Name}》音源至 {displayName}...");
        PlaybackBadgesChanged?.Invoke(FormatPlatformBadgeDisplay(displayName), "换源中...");
        await PlaySongAsync(
            CurrentSong,
            forcePlatform: normalizedKey,
            initialSeekPosition: savedPos.TotalSeconds > 1 ? savedPos : null);
    }

    /// <summary>
    /// 在线播放时实时切换目标音质等级，并保持当前播放进度无缝重载当前在线歌曲
    /// </summary>
    public async Task SwitchQualityLevelAsync(string qualityLevel)
    {
        if (string.IsNullOrWhiteSpace(qualityLevel)) return;
        _configService.Settings.MusicQuality = qualityLevel;
        await _configService.SaveSettingsAsync();
        lock (_prefetchLock)
        {
            _prefetchedSources.Clear();
        }

        var displayName = MusicQualityLevel.GetDisplayName(qualityLevel);
        if (CurrentSong == null || CurrentSong.IsLocalSong)
        {
            QualityBadgeChanged?.Invoke(MusicQualityLevel.GetShortBadgeName(qualityLevel));
            StatusMessage?.Invoke($"默认在线音质已切换为：{displayName}");
            return;
        }

        var savedPos = Position;
        StatusMessage?.Invoke($"正在切换《{CurrentSong.Name}》音质至 {displayName}...");
        var currentPlat = FormatPlatformBadgeDisplay(CurrentSong.ResolvedPlatform ?? CurrentSong.Source);
        PlaybackBadgesChanged?.Invoke(currentPlat, "切换中...");
        await PlaySongAsync(CurrentSong, forcePlatform: _activeQueueForcePlatform, initialSeekPosition: savedPos.TotalSeconds > 1 ? savedPos : null);
        var actualBadge = CurrentSong.PlayingQualityBadge ?? MusicQualityLevel.GetShortBadgeName(qualityLevel);
        StatusMessage?.Invoke($"已切换音质：{displayName}（当前音源实际输出：{actualBadge}）");
    }

    /// <summary>
    /// 判断指定音质档位是否为无损及以上档位（lossless / hires / jyeffect / sky / dolby / jymaster）
    /// </summary>
    public static bool IsLosslessQualityLevel(string? quality)
    {
        if (string.IsNullOrWhiteSpace(quality)) return true;
        return quality.ToLowerInvariant() is "lossless" or "hires" or "jyeffect" or "sky" or "dolby" or "jymaster";
    }

    /// <summary>
    /// 解析歌曲最高无损下载直链及格式提示（专供下载任务使用，强制优先请求超清母带/Hi-Res/FLAC 无损音源）
    /// </summary>
    public async Task<(string? Url, string ExtensionHint)> ResolveDownloadSourceAsync(SongItem song, CancellationToken ct = default)
    {
        if (song == null) return (null, ".mp3");

        // 1. 本地或 SMB 音乐文件
        if (!string.IsNullOrWhiteSpace(song.LocalFilePath) && File.Exists(song.LocalFilePath))
        {
            var ext = Path.GetExtension(song.LocalFilePath);
            return (song.LocalFilePath, string.IsNullOrWhiteSpace(ext) ? ".flac" : ext);
        }

        // 1.8 汽水音乐原生直链音源（优先无加密直链，失败回退落雪与多源匹配）
        if (song.Source == "qishui")
        {
            var (qsUrl, qsKbps, _) = await _thirdPartyService.GetQishuiPlayUrlAsync(song.QishuiTrackId, preferLossless: true, ct);
            if (!string.IsNullOrWhiteSpace(qsUrl))
            {
                bool qsFlac = qsKbps > 400 || qsUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase);
                string qsExt = qsFlac ? ".flac" : (qsUrl.Contains(".m4a", StringComparison.OrdinalIgnoreCase) ? ".m4a" : string.Empty);
                return (qsUrl, qsExt);
            }

            var lxQs = await _unblockService.TryLxMusicAsync(song, preferLossless: true, ct);
            if (!string.IsNullOrWhiteSpace(lxQs))
            {
                bool lxFlac = lxQs.Contains(".flac", StringComparison.OrdinalIgnoreCase) || _unblockService.LastResolvedBitrateKbps > 400;
                return (lxQs, lxFlac ? ".flac" : string.Empty);
            }

            var (ubQs, _) = await _unblockService.MatchAudioUrlAsync(song, null, ct, preferLossless: true);
            if (!string.IsNullOrWhiteSpace(ubQs))
            {
                bool ubFlac = ubQs.Contains(".flac", StringComparison.OrdinalIgnoreCase) || _unblockService.LastResolvedBitrateKbps > 400;
                return (ubQs, ubFlac ? ".flac" : string.Empty);
            }
        }

        // 1.9 国际音乐平台音源（方案 A：优先纯国际无损/完整直链，未命中才回退各自官方 30 秒试听，严禁跳国内源）
        if (song.Source is "spotify" or "apple" or "deezer" or "tidal" or "qobuz" or "ytmusic")
        {
            var (intlUrl, intlPlat) = await _unblockService.ResolveInternationalAudioAsync(song, preferLossless: true, ct);
            if (!string.IsNullOrWhiteSpace(intlUrl))
            {
                bool isFlac = intlUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase) || _unblockService.LastResolvedBitrateKbps > 400;
                string ext = isFlac
                    ? ".flac"
                    : (intlUrl.Contains(".mp3", StringComparison.OrdinalIgnoreCase)
                        ? ".mp3"
                        : ((intlPlat == "ytmusic" || intlUrl.Contains(".webm", StringComparison.OrdinalIgnoreCase) || intlUrl.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase))
                            ? ".webm"
                            : ".m4a"));
                return (intlUrl, ext);
            }

            var (nativeUrl, _) = await _thirdPartyService.GetInternationalNativePlayUrlAsync(song, ct);
            if (!string.IsNullOrWhiteSpace(nativeUrl))
            {
                bool isFlac = nativeUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase);
                return (nativeUrl, isFlac ? ".flac" : ".m4a");
            }

            return (null, ".m4a");
        }

        // 2. 第三方原生曲库音源（QQ音乐 / 酷狗 / 酷我 / 咪咕：优先落雪多脚本直连原生 ID/Hash 拿无损，再回退官方/多源通道）
        if (song.Source is "qq" or "kugou" or "kuwo" or "migu")
        {
            string? qqUrl = null;
            if (song.Source == "qq" && !string.IsNullOrWhiteSpace(song.QqSongMid))
            {
                qqUrl = await _qqMusicService.GetSongPlayUrlAsync(song.QqSongMid, ct, preferLossless: true);
                if (!string.IsNullOrWhiteSpace(qqUrl) && qqUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase))
                {
                    return (qqUrl, ".flac");
                }
            }

            var lxUrl = await _unblockService.TryLxMusicAsync(song, preferLossless: true, ct);
            if (!string.IsNullOrWhiteSpace(lxUrl))
            {
                bool lxFlac = lxUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase) ||
                              _unblockService.LastResolvedBitrateKbps > 400;
                if (lxFlac || string.IsNullOrWhiteSpace(qqUrl))
                {
                    return (lxUrl, lxFlac ? ".flac" : string.Empty);
                }
            }

            var (unblockUrl, _) = await _unblockService.MatchAudioUrlAsync(song, null, ct, preferLossless: true);
            if (!string.IsNullOrWhiteSpace(unblockUrl))
            {
                bool ubFlac = unblockUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase) ||
                              _unblockService.LastResolvedBitrateKbps > 400;
                if (ubFlac || string.IsNullOrWhiteSpace(qqUrl))
                {
                    return (unblockUrl, ubFlac ? ".flac" : string.Empty);
                }
            }

            if (!string.IsNullOrWhiteSpace(qqUrl))
            {
                return (qqUrl, string.Empty);
            }
        }

        // 3. B站音源
        if (song.Source == "bilibili" && !string.IsNullOrWhiteSpace(song.BilibiliBvid))
        {
            var streamUrl = await _bilibiliService.GetAudioStreamUrlAsync(song.BilibiliBvid, song.BilibiliCid ?? 0);
            if (!string.IsNullOrWhiteSpace(streamUrl))
            {
                var cachedFile = await _bilibiliService.DownloadAudioToCacheAsync(
                    streamUrl, song.BilibiliBvid, song.BilibiliCid ?? 0, _configService.CacheDirectory, ct);
                if (!string.IsNullOrWhiteSpace(cachedFile))
                {
                    return (cachedFile, Path.GetExtension(cachedFile));
                }
            }
        }

        // 4. 网易云歌曲：下载时始终优先请求最高无损音质（若用户设置为标准/较高/极高，下载也自动提档至 jymaster / lossless 获取无损 FLAC）
        if (song.NumericId > 0)
        {
            var userQuality = _configService.Settings.MusicQuality;
            var targetQuality = IsLosslessQualityLevel(userQuality) ? userQuality : MusicQualityLevel.Jymaster;

            var (officialUrl, type, _, br, isFreeTrial) = await _apiClient.GetSongDownloadUrlAsync(song.NumericId, targetQuality);
            bool isOfficialLossless = !string.IsNullOrWhiteSpace(officialUrl) &&
                                      !isFreeTrial &&
                                      (string.Equals(type, "flac", StringComparison.OrdinalIgnoreCase) ||
                                       officialUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase) ||
                                       br > 320000);

            if (isOfficialLossless)
            {
                return (officialUrl, ".flac");
            }

            // 若官方因账号非 SVIP 仅返回了 mp3 或试听片段，且开启了多音源解灰，优先通过解灰通道获取 999kbps FLAC 无损流
            if (_configService.Settings.EnableMusicUnblock)
            {
                var (unblockUrl, _) = await _unblockService.MatchAudioUrlAsync(song, null, ct, preferLossless: true);
                if (!string.IsNullOrWhiteSpace(unblockUrl))
                {
                    // 若解灰拿到了 FLAC 无损，或官方无完整链接（仅试听/空），优先采用解灰链接
                    if (unblockUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(officialUrl) ||
                        isFreeTrial)
                    {
                        var extHint = unblockUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase) ? ".flac" : string.Empty;
                        return (unblockUrl, extHint);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(officialUrl))
            {
                var extHint = !string.IsNullOrWhiteSpace(type) ? $".{type.TrimStart('.').ToLowerInvariant()}" : string.Empty;
                return (officialUrl, extHint);
            }
        }

        return (null, string.Empty);
    }

    /// <summary>
    /// 流式并发探测所有可用音源（带单个音源 2.5 秒快速超时），每探查到一个可用音质立即通过 onOptionFound 回调实时上屏
    /// </summary>
    public async Task ProbeAllDownloadSourcesStreamAsync(SongItem song, Action<DownloadQualityOption> onOptionFound, CancellationToken ct = default)
    {
        if (song == null) return;

        // 本地或 SMB 歌曲直接回调本地文件选项
        if (!string.IsNullOrWhiteSpace(song.LocalFilePath) && File.Exists(song.LocalFilePath))
        {
            var ext = Path.GetExtension(song.LocalFilePath).ToLowerInvariant();
            var fi = new FileInfo(song.LocalFilePath);
            onOptionFound(new DownloadQualityOption
            {
                Platform = "local",
                PlatformName = "本地文件",
                QualityBadge = ext == ".flac" ? "本地无损 FLAC" : $"本地 {ext.TrimStart('.').ToUpperInvariant()}",
                Extension = ext,
                DownloadUrl = song.LocalFilePath,
                SizeBytes = fi.Length,
                SizeText = FormatFileSizeText(fi.Length),
                IsRecommended = true,
                BrandColorHex = "#10B981",
                QualityRank = ext == ".flac" ? 100 : 50
            });
            return;
        }

        var seenKeys = new global::System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        void TryDispatchOption(DownloadQualityOption opt)
        {
            if (opt == null || string.IsNullOrWhiteSpace(opt.DownloadUrl)) return;
            var key = $"{opt.Platform}_{opt.QualityBadge}_{opt.BitrateKbps}";
            if (seenKeys.TryAdd(key, 0) && seenKeys.TryAdd(opt.DownloadUrl, 0))
            {
                onOptionFound(opt);
            }
        }

        // 1. 若当前歌曲正在播放中且已成功解析出直链，0 毫秒立即直出！
        if (CurrentSong != null && CurrentSong.Id == song.Id &&
            !string.IsNullOrWhiteSpace(CurrentSong.PlayMusicUrl) &&
            CurrentSong.PlayMusicUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var curUrl = CurrentSong.PlayMusicUrl;
            bool isFlac = curUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase);
            var curBadge = CurrentSong.PlayingQualityBadge ?? (isFlac ? "无损 FLAC" : "标准品质");
            var curPlat = (CurrentSong.ResolvedPlatform ?? CurrentSong.Source ?? "netease").ToLowerInvariant();
            var curPlatName = curPlat switch
            {
                "netease" => "网易云官方",
                "gdmusic" => "GD 音乐台",
                "kuwo" => "酷我音乐",
                "kugou" => "酷狗音乐",
                "migu" => "咪咕音乐",
                "qq" => "QQ 音乐",
                "qishui" => "汽水音乐",
                "spotify" => "Spotify",
                "apple" => "Apple Music",
                "deezer" => "Deezer",
                "tidal" => "Tidal Hi-Res",
                "qobuz" => "Qobuz Hi-Res",
                "ytmusic" => "YouTube Music",
                "pyncmd" => "Pyncmd 引擎",
                "lxmusic" => "落雪音源",
                _ => curPlat.ToUpperInvariant()
            };

            var curBytes = TryEstimateOrGetFileSize(curUrl, song, isFlac, isFlac ? 999 : 320);

            var curExt = isFlac
                ? ".flac"
                : (curUrl.Contains(".m4a", StringComparison.OrdinalIgnoreCase) || curUrl.Contains(".mp4", StringComparison.OrdinalIgnoreCase)
                    ? ".m4a"
                    : (curUrl.Contains(".webm", StringComparison.OrdinalIgnoreCase) ? ".webm" : ".mp3"));

            TryDispatchOption(new DownloadQualityOption
            {
                Platform = curPlat,
                PlatformName = $"{curPlatName} (当前播放)",
                QualityBadge = curBadge,
                Extension = curExt,
                BitrateKbps = isFlac ? 999 : 320,
                SizeBytes = curBytes,
                SizeText = curBytes > 0 ? FormatFileSizeText(curBytes) : "",
                DownloadUrl = curUrl,
                IsRecommended = isFlac,
                BrandColorHex = "#10B981",
                QualityRank = isFlac ? 96 : 55
            });
        }

        // 1.5 国际平台严格隔离（方案 A）：先探纯国际完整无损直链，未命中才回退各自原生试听，绝不穿透国内音源
        if (song.Source is "spotify" or "apple" or "deezer" or "tidal" or "qobuz" or "ytmusic")
        {
            var (intlUrl, intlPlat) = await _unblockService.ResolveInternationalAudioAsync(song, preferLossless: true, ct);
            if (!string.IsNullOrWhiteSpace(intlUrl))
            {
                int kbps = _unblockService.LastResolvedBitrateKbps;
                long size = _unblockService.LastResolvedSizeBytes;
                bool isFlac = intlUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase) || kbps > 400;
                int safeKbps = kbps > 0 ? kbps : (isFlac ? 999 : 256);
                string ext = isFlac
                    ? ".flac"
                    : (intlUrl.Contains(".mp3", StringComparison.OrdinalIgnoreCase)
                        ? ".mp3"
                        : ((intlPlat == "ytmusic" || intlUrl.Contains(".webm", StringComparison.OrdinalIgnoreCase) || intlUrl.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase))
                            ? ".webm"
                            : ".m4a"));
                string badge = _unblockService.FormatResolvedQualityBadge(intlUrl, song, MusicQualityLevel.Lossless);
                string platName = intlPlat.ToLowerInvariant() switch
                {
                    "joox" => "JOOX (国际无损)",
                    "tidal" => "Tidal (国际高音质)",
                    "apple" => "Apple Music (国际高音质)",
                    "ytmusic" => "YouTube Music (完整音频)",
                    _ => $"{intlPlat.ToUpperInvariant()} (国际)"
                };
                if (size <= 0)
                {
                    size = await TryProbeFileSizeAsync(intlUrl, song, isFlac, safeKbps, ct);
                }

                TryDispatchOption(new DownloadQualityOption
                {
                    Platform = intlPlat,
                    PlatformName = platName,
                    QualityBadge = badge,
                    Extension = ext,
                    BitrateKbps = safeKbps,
                    SizeBytes = size,
                    SizeText = size > 0 ? FormatFileSizeText(size) : "",
                    DownloadUrl = intlUrl,
                    IsRecommended = true,
                    BrandColorHex = "#10B981",
                    QualityRank = isFlac ? 98 : 82
                });
            }
            else
            {
                var (nativeUrl, kbps) = await _thirdPartyService.GetInternationalNativePlayUrlAsync(song, ct);
                if (!string.IsNullOrWhiteSpace(nativeUrl))
                {
                    int safeKbps = kbps > 0 ? kbps : 128;
                    string ext = nativeUrl.Contains(".mp3", StringComparison.OrdinalIgnoreCase) ? ".mp3" : ".m4a";
                    string platName = song.Source switch
                    {
                        "spotify" => "Spotify",
                        "apple" => "Apple Music",
                        "deezer" => "Deezer",
                        "tidal" => "Tidal",
                        "ytmusic" => "YouTube Music",
                        _ => song.Source.ToUpperInvariant()
                    };
                    long size = await TryProbeFileSizeAsync(nativeUrl, song, false, safeKbps, ct);
                    TryDispatchOption(new DownloadQualityOption
                    {
                        Platform = song.Source,
                        PlatformName = $"{platName} (官方 30s 试听)",
                        QualityBadge = $"Native Preview {safeKbps}k",
                        Extension = ext,
                        BitrateKbps = safeKbps,
                        SizeBytes = size,
                        SizeText = size > 0 ? FormatFileSizeText(size) : "",
                        DownloadUrl = nativeUrl,
                        IsRecommended = false,
                        BrandColorHex = "#6B7280",
                        QualityRank = 30
                    });
                }
            }
            return;
        }

        var tasks = new List<Task>();

        // 2. 网易云官方音源探测（不设短超时，给足 20 秒网络安全保护上限）
        if (song.NumericId > 0)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var officialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    officialCts.CancelAfter(TimeSpan.FromSeconds(20));

                    var (url, type, size, br, isTrial) = await _apiClient.GetSongDownloadUrlAsync(song.NumericId, MusicQualityLevel.Jymaster);
                    if (string.IsNullOrWhiteSpace(url) || isTrial) return;

                    bool isFlac = string.Equals(type, "flac", StringComparison.OrdinalIgnoreCase) ||
                                  url.Contains(".flac", StringComparison.OrdinalIgnoreCase) ||
                                  br > 320000;
                    int kbps = br >= 1000 ? br / 1000 : (isFlac ? 999 : 320);
                    string qName = isFlac
                        ? (kbps > 1000 ? "超清母带 / Hi-Res" : "无损品质 (FLAC)")
                        : (kbps >= 300 ? "极高品质 (320k)" : "标准品质 (128k)");

                    if (size <= 0)
                    {
                        size = await TryProbeFileSizeAsync(url, song, isFlac, kbps, ct);
                    }

                    TryDispatchOption(new DownloadQualityOption
                    {
                        Platform = "netease",
                        PlatformName = "网易云官方",
                        QualityBadge = qName,
                        Extension = isFlac ? ".flac" : ".mp3",
                        BitrateKbps = kbps,
                        SizeBytes = size,
                        SizeText = size > 0 ? FormatFileSizeText(size) : "",
                        DownloadUrl = url,
                        BrandColorHex = "#EC4141",
                        QualityRank = isFlac ? (kbps > 1000 ? 99 : 92) : (kbps >= 300 ? 50 : 20)
                    });
                }
                catch { }
            }, ct));
        }

        // 3. QQ 音乐音源（若歌曲源为 QQ 音乐，给足 20 秒）
        if (song.Source == "qq" && !string.IsNullOrWhiteSpace(song.QqSongMid))
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var qqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    qqCts.CancelAfter(TimeSpan.FromSeconds(20));

                    var qqUrl = await _qqMusicService.GetSongPlayUrlAsync(song.QqSongMid, qqCts.Token, preferLossless: true);
                    if (string.IsNullOrWhiteSpace(qqUrl)) return;
                    bool isFlac = qqUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase);
                    int kbps = isFlac ? 999 : 320;
                    long qqSize = await TryProbeFileSizeAsync(qqUrl, song, isFlac, kbps, ct);

                    TryDispatchOption(new DownloadQualityOption
                    {
                        Platform = "qq",
                        PlatformName = "QQ 音乐",
                        QualityBadge = isFlac ? "无损品质 (FLAC)" : "高品质 (HQ 320k)",
                        Extension = isFlac ? ".flac" : ".mp3",
                        BitrateKbps = kbps,
                        SizeBytes = qqSize,
                        SizeText = qqSize > 0 ? FormatFileSizeText(qqSize) : "",
                        DownloadUrl = qqUrl,
                        BrandColorHex = "#10B981",
                        QualityRank = isFlac ? 91 : 48
                    });
                }
                catch { }
            }, ct));
        }

        // 4. 并发探测全部音源（不设 2.5 秒封顶，放宽至 20 秒，让所有源充分加载供选择）
        var allSupportedPlatforms = new[] { "kuwo", "kugou", "migu", "qq", "pyncmd", "gdmusic", "joox" };
        var platforms = _configService.Settings.EnabledMusicSources.Count > 0
            ? _configService.Settings.EnabledMusicSources.Union(allSupportedPlatforms, StringComparer.OrdinalIgnoreCase).ToList()
            : allSupportedPlatforms.ToList();
        // 落雪脚本单独按脚本逐个探测（见下方第 5 步）
        platforms.RemoveAll(p => p.Equals("lxmusic", StringComparison.OrdinalIgnoreCase));

        // 5. 并发运行全部已导入且启用的落雪 JS 脚本，每个脚本单独一行
        foreach (var lxScript in _configService.Settings.LxMusicScripts.Where(x => x.Enabled).ToList())
        {
            var script = lxScript;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var lxCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    lxCts.CancelAfter(TimeSpan.FromSeconds(30));

                    var (url, quality) = await _lxHost.GetMusicUrlAsync(script, song, "flac24bit", lxCts.Token);
                    if (string.IsNullOrWhiteSpace(url)) return;

                    int kbps = UnblockMusicService.LxQualityToKbps(quality);
                    bool isFlac = quality.StartsWith("flac", StringComparison.OrdinalIgnoreCase) ||
                                  url.Contains(".flac", StringComparison.OrdinalIgnoreCase);
                    if (kbps == 0) kbps = isFlac ? 999 : 320;

                    string badge = quality switch
                    {
                        "flac24bit" => "24bit Hi-Res (FLAC)",
                        "flac" => "无损音质 (FLAC)",
                        _ => $"{kbps}k MP3"
                    };

                    long size = await TryProbeFileSizeAsync(url, song, isFlac, kbps, ct);

                    TryDispatchOption(new DownloadQualityOption
                    {
                        Platform = $"lx_{script.Id}",
                        PlatformName = $"落雪·{script.Name}",
                        QualityBadge = badge,
                        Extension = isFlac ? ".flac" : ".mp3",
                        BitrateKbps = kbps,
                        SizeBytes = size,
                        SizeText = size > 0 ? FormatFileSizeText(size) : "",
                        DownloadUrl = url,
                        BrandColorHex = "#22C55E",
                        QualityRank = isFlac ? (kbps > 1000 ? 97 : 88) : (kbps >= 300 ? 45 : 17)
                    });
                }
                catch { }
            }, ct));
        }

        foreach (var platform in platforms)
        {
            var platId = platform.ToLowerInvariant();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var singleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    singleCts.CancelAfter(TimeSpan.FromSeconds(20));

                    var (url, p) = await _unblockService.MatchAudioUrlAsync(song, [platId], singleCts.Token, preferLossless: true);
                    if (string.IsNullOrWhiteSpace(url)) return;

                    bool isFlac = url.Contains(".flac", StringComparison.OrdinalIgnoreCase) ||
                                  _unblockService.LastResolvedBitrateKbps > 400;
                    int kbps = _unblockService.LastResolvedBitrateKbps > 0
                        ? _unblockService.LastResolvedBitrateKbps
                        : (isFlac ? 999 : 320);

                    string platName = platId switch
                    {
                        "gdmusic" => "GD 音乐台",
                        "pyncmd" => "Pyncmd 引擎",
                        "kuwo" => "酷我音乐",
                        "kugou" => "酷狗音乐",
                        "migu" => "咪咕音乐",
                        "qq" => "QQ 音乐",
                        "qishui" => "汽水音乐",
                        "spotify" => "Spotify",
                        "apple" => "Apple Music",
                        "deezer" => "Deezer",
                        "tidal" => "Tidal Hi-Res",
                        "qobuz" => "Qobuz Hi-Res",
                        "ytmusic" => "YouTube Music",
                        "joox" => "JOOX 音乐",
                        "lxmusic" => "落雪音源",
                        "custom" => "自定义 API",
                        _ => platId.ToUpperInvariant()
                    };

                    string brandColor = platId switch
                    {
                        "gdmusic" => "#3B82F6",
                        "pyncmd" => "#8B5CF6",
                        "kuwo" => "#F59E0B",
                        "kugou" => "#06B6D4",
                        "migu" => "#EC4899",
                        "qq" => "#10B981",
                        "qishui" => "#22C55E",
                        "spotify" => "#1DB954",
                        "apple" => "#FA243C",
                        "deezer" => "#A238FF",
                        "tidal" => "#0EA5E9",
                        "qobuz" => "#0284C7",
                        "ytmusic" => "#EF4444",
                        "joox" => "#10B981",
                        "lxmusic" => "#22C55E",
                        _ => "#6366F1"
                    };

                    string badge = isFlac
                        ? (kbps > 1000 ? "24bit Hi-Res (FLAC)" : "无损音质 (FLAC)")
                        : $"{kbps}k MP3";

                    long size = _unblockService.LastResolvedSizeBytes;
                    if (size <= 0)
                    {
                        size = await TryProbeFileSizeAsync(url, song, isFlac, kbps, ct);
                    }

                    TryDispatchOption(new DownloadQualityOption
                    {
                        Platform = platId,
                        PlatformName = platName,
                        QualityBadge = badge,
                        Extension = isFlac ? ".flac" : ".mp3",
                        BitrateKbps = kbps,
                        SizeBytes = size,
                        SizeText = size > 0 ? FormatFileSizeText(size) : "",
                        DownloadUrl = url,
                        BrandColorHex = brandColor,
                        QualityRank = isFlac ? (kbps > 1000 ? 98 : 89) : (kbps >= 300 ? 46 : 18)
                    });
                }
                catch { }
            }, ct));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch { }
    }

    /// <summary>
    /// 并发探测所有可用音源（网易云官方及启用的第三方解灰音源），获取各平台可用的最高音质直链列表供用户选择
    /// </summary>
    public async Task<List<DownloadQualityOption>> ProbeAllDownloadSourcesAsync(SongItem song, CancellationToken ct = default)
    {
        var list = new List<DownloadQualityOption>();
        await ProbeAllDownloadSourcesStreamAsync(song, opt =>
        {
            lock (list) { list.Add(opt); }
        }, ct);
        list.Sort((a, b) => b.QualityRank.CompareTo(a.QualityRank));
        if (list.Count > 0) list[0].IsRecommended = true;
        return list;
    }

    private static string FormatFileSizeText(long bytes)
    {
        if (bytes <= 0) return "";
        if (bytes >= 1024 * 1024 * 1024)
            return $"约 {(double)bytes / (1024 * 1024 * 1024):F1} GB";
        if (bytes >= 1024 * 1024)
            return $"约 {(double)bytes / (1024 * 1024):F1} MB";
        if (bytes >= 1024)
            return $"约 {(double)bytes / 1024:F0} KB";
        return $"{bytes} B";
    }

    private static readonly HttpClient s_probeHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private static long TryEstimateOrGetFileSize(string url, SongItem song, bool isFlac, int kbps)
    {
        if (File.Exists(url))
        {
            try { return new FileInfo(url).Length; } catch { }
        }
        if (song.DurationMs > 3000)
        {
            double effectiveKbps = isFlac ? (kbps > 1000 ? 1400 : 880) : (kbps > 0 ? kbps : 320);
            return (long)((song.DurationMs / 1000.0) * (effectiveKbps * 125));
        }
        return 0;
    }

    private static async Task<long> TryProbeFileSizeAsync(string url, SongItem song, bool isFlac, int kbps, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return 0;

        if (File.Exists(url))
        {
            try { return new FileInfo(url).Length; } catch { }
        }

        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(TimeSpan.FromMilliseconds(1200));

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new global::System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using var resp = await s_probeHttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, probeCts.Token);

                if (resp.Content.Headers.ContentRange?.Length.HasValue == true && resp.Content.Headers.ContentRange.Length.Value > 1024)
                {
                    return resp.Content.Headers.ContentRange.Length.Value;
                }
                if (resp.Content.Headers.ContentLength.HasValue && resp.Content.Headers.ContentLength.Value > 1024)
                {
                    return resp.Content.Headers.ContentLength.Value;
                }
            }
            catch { }
        }

        // 网络请求头未返回时，根据时长与码率估算体积
        if (song.DurationMs > 3000)
        {
            double effectiveKbps = isFlac ? (kbps > 1000 ? 1400 : 880) : (kbps > 0 ? kbps : 320);
            return (long)((song.DurationMs / 1000.0) * (effectiveKbps * 125));
        }

        return 0;
    }

    /// <summary>
    /// 将底层音源标识或具体洛雪脚本名称映射为清晰易读的底部播放徽章文本
    /// </summary>
    public static string FormatPlatformBadgeDisplay(string? platform)
    {
        var raw = (platform ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw)) return "MULTI";

        return raw.ToLowerInvariant() switch
        {
            "netease" => "网易云",
            "netease (试听)" => "网易云试听",
            "qq" or "tx" => "QQ音乐",
            "qishui" => "汽水音乐",
            "kugou" or "kg" => "酷狗源",
            "kuwo" or "kw" => "酷我源",
            "migu" or "mg" => "咪咕源",
            "gdmusic" => "GD音乐台",
            "pyncmd" => "PyNCMD",
            "lxmusic" => "洛雪音源",
            "custom" => "自定义API",
            "local" => "本地音乐",
            "bilibili" => "B站音乐",
            _ => raw.Any(ch => ch > 127 || ch == '-' || ch == '_') ? raw : raw.ToUpperInvariant()
        };
    }

    /// <summary>
    /// 同步记录已解析音源平台/地址/音质标签，并在非预解析场景下原子广播底栏与全屏歌词页徽章更新（早于解码缓冲阶段生效）
    /// </summary>
    private void ApplyResolvedPlaybackInfo(SongItem song, string? platform, string playUrl, string qualityBadge, bool isPrefetch)
    {
        var resolvedDisplay = !string.IsNullOrWhiteSpace(platform) ? platform : song.Source;
        song.ResolvedPlatform = resolvedDisplay;
        song.PlayMusicUrl = playUrl;
        song.PlayingQualityBadge = qualityBadge;

        RunOnUi(() =>
        {
            song.ResolvedPlatform = resolvedDisplay;
            song.PlayMusicUrl = playUrl;
            song.PlayingQualityBadge = qualityBadge;
        });

        if (!isPrefetch && CurrentSong?.Id == song.Id)
        {
            PlaybackBadgesChanged?.Invoke(FormatPlatformBadgeDisplay(resolvedDisplay), qualityBadge);
            QualityBadgeChanged?.Invoke(qualityBadge);
        }
    }

    private static bool ShouldUseMpvForOnlineStream(SongItem song, string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl)) return false;

        // 网易云官方直连 MP3（非 FLAC）在 NAudio 下同样 ~150ms 起播；其余跨平台/代理/FLAC/M4A/Opus 流统一走内置 mpv 流式直连
        bool isNeteaseOfficialCdn = sourceUrl.Contains("music.126.net", StringComparison.OrdinalIgnoreCase);
        bool isHeavyContainerOrProxy = sourceUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase) ||
                                       sourceUrl.Contains(".m4a", StringComparison.OrdinalIgnoreCase) ||
                                       sourceUrl.Contains(".webm", StringComparison.OrdinalIgnoreCase) ||
                                       sourceUrl.Contains(".opus", StringComparison.OrdinalIgnoreCase) ||
                                       sourceUrl.Contains(".ogg", StringComparison.OrdinalIgnoreCase) ||
                                       sourceUrl.Contains("gdstudio.org", StringComparison.OrdinalIgnoreCase) ||
                                       sourceUrl.Contains("bilivideo", StringComparison.OrdinalIgnoreCase);
        return !isNeteaseOfficialCdn || isHeavyContainerOrProxy || song.Source == "bilibili";
    }

    private static string? GetHttpReferrerForStream(SongItem song, string sourceUrl)
    {
        if (song.Source == "bilibili" ||
            sourceUrl.Contains("bilivideo", StringComparison.OrdinalIgnoreCase) ||
            sourceUrl.Contains("hdslb", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(song.BilibiliBvid)
                ? "https://www.bilibili.com/"
                : $"https://www.bilibili.com/video/{song.BilibiliBvid}";
        }

        if (sourceUrl.Contains("gdstudio.org", StringComparison.OrdinalIgnoreCase))
        {
            return "https://music.gdstudio.org/";
        }

        if (sourceUrl.Contains("qqmusic.qq.com", StringComparison.OrdinalIgnoreCase) ||
            sourceUrl.Contains("y.qq.com", StringComparison.OrdinalIgnoreCase))
        {
            return "https://y.qq.com/";
        }

        if (sourceUrl.Contains("douyin", StringComparison.OrdinalIgnoreCase) ||
            sourceUrl.Contains("byte", StringComparison.OrdinalIgnoreCase) ||
            song.Source == "qishui")
        {
            return "https://music.douyin.com/";
        }

        return null;
    }

    /// <summary>
    /// 解析歌曲可播放地址（支持本地/SMB 文件、B站音频、网易云直连及多音源解灰回退，并实时计算正在播放的音质徽章）
    /// </summary>
    public async Task<string?> ResolvePlayableSourceAsync(
        SongItem song, string? forcePlatform = null, CancellationToken ct = default, bool isPrefetch = false, bool allowFastStart = false)
    {
        var userQuality = _configService.Settings.MusicQuality;
        bool preferLossless = IsLosslessQualityLevel(userQuality);
        bool canFastStartAndUpgrade = allowFastStart && !isPrefetch && preferLossless;

        if (!isPrefetch)
        {
            _pendingLosslessUpgrade = null;

            // 0. 命中预解析缓存（预解析时已在同一 SongItem 上写入音源/音质信息）
            if (string.IsNullOrWhiteSpace(forcePlatform) && !song.IsLocalSong)
            {
                (string Url, DateTime Time) cached;
                bool hit;
                lock (_prefetchLock)
                {
                    hit = _prefetchedSources.Remove(song.Id, out cached);
                }
                if (hit && DateTime.UtcNow - cached.Time < PrefetchTtl &&
                    string.Equals(song.PlayMusicUrl, cached.Url, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(song.PlayingQualityBadge))
                    {
                        var plat = FormatPlatformBadgeDisplay(song.ResolvedPlatform ?? song.Source);
                        PlaybackBadgesChanged?.Invoke(plat, song.PlayingQualityBadge);
                        QualityBadgeChanged?.Invoke(song.PlayingQualityBadge);
                    }
                    return cached.Url;
                }
            }
        }

        // 1. 本地或 SMB 共享目录音乐文件（兼容 UNC 路径 \\server\share\...、映射网络驱动器及多声道 5.1 / 7.1）
        var rawLocalPath = !string.IsNullOrWhiteSpace(song.LocalFilePath)
            ? song.LocalFilePath
            : (song.IsLocalSong ? song.PlayMusicUrl : null);

        if (!string.IsNullOrWhiteSpace(rawLocalPath) && song.IsLocalSong)
        {
            var localOrStagedPath = await PrepareLocalOrSmbAudioFileAsync(rawLocalPath, ct);
            if (!string.IsNullOrWhiteSpace(localOrStagedPath))
            {
                var extUpper = Path.GetExtension(rawLocalPath).TrimStart('.').ToUpperInvariant();
                var localBadge = string.IsNullOrWhiteSpace(extUpper) ? "本地音源" : $"本地 {extUpper}";
                long localBytes = song.FileSize;
                try { if (localBytes <= 0 && File.Exists(localOrStagedPath)) localBytes = new FileInfo(localOrStagedPath).Length; } catch { }
                RecordResolvedSongQuality(song, "local", userQuality, MusicQualityLevel.Lossless, localOrStagedPath, 0, localBytes);
                song.LocalFilePath = rawLocalPath;
                RunOnUi(() => song.LocalFilePath = rawLocalPath);
                ApplyResolvedPlaybackInfo(song, "local", localOrStagedPath, localBadge, isPrefetch);
                return localOrStagedPath;
            }

            StatusMessage?.Invoke($"无法访问本地或 SMB 共享文件：{rawLocalPath}");
            return null;
        }

        // 1.1 专区定向音源或底栏手动指定音源优先路由（国际平台除外；指定源无该曲时平滑回退全量多源并发）
        bool isIntlTrack = song.Source is "spotify" or "apple" or "deezer" or "tidal" or "qobuz" or "ytmusic";
        if (!string.IsNullOrWhiteSpace(forcePlatform) &&
            !string.Equals(forcePlatform, "auto", StringComparison.OrdinalIgnoreCase) &&
            !isIntlTrack)
        {
            var forcedNorm = forcePlatform.Trim();
            if (forcedNorm.StartsWith("lx:", StringComparison.OrdinalIgnoreCase))
            {
                var scriptId = forcedNorm["lx:".Length..].Trim();
                var (lxUrl, scriptName) = await _unblockService.TrySpecificLxScriptAsync(scriptId, song, preferLossless, ct, userQuality);
                if (!string.IsNullOrWhiteSpace(lxUrl))
                {
                    var lxBadge = _unblockService.FormatResolvedQualityBadge(lxUrl, song, userQuality);
                    var displayPlat = string.IsNullOrWhiteSpace(scriptName) ? "洛雪音源" : scriptName;
                    RecordResolvedSongQuality(song, displayPlat, userQuality, null, lxUrl, _unblockService.LastResolvedBitrateKbps, _unblockService.LastResolvedSizeBytes);
                    ApplyResolvedPlaybackInfo(song, displayPlat, lxUrl, lxBadge, isPrefetch);
                    return lxUrl;
                }
            }
            else if (string.Equals(forcedNorm, "lx_all", StringComparison.OrdinalIgnoreCase))
            {
                var lxUrl = await _unblockService.TryLxMusicAsync(song, preferLossless, ct, userQuality);
                if (!string.IsNullOrWhiteSpace(lxUrl))
                {
                    var lxBadge = _unblockService.FormatResolvedQualityBadge(lxUrl, song, userQuality);
                    var displayPlat = !string.IsNullOrWhiteSpace(_unblockService.LastResolvedSourceName)
                        ? _unblockService.LastResolvedSourceName
                        : "洛雪音源";
                    RecordResolvedSongQuality(song, displayPlat, userQuality, null, lxUrl, _unblockService.LastResolvedBitrateKbps, _unblockService.LastResolvedSizeBytes);
                    ApplyResolvedPlaybackInfo(song, displayPlat, lxUrl, lxBadge, isPrefetch);
                    return lxUrl;
                }
            }
            else if (string.Equals(forcedNorm, "builtin_all", StringComparison.OrdinalIgnoreCase))
            {
                var (builtinUrl, builtinPlat) = await _unblockService.MatchAudioUrlAsync(
                    song, ["gdmusic", "pyncmd", "kuwo", "kugou", "migu"], ct, preferLossless, userQuality, allowFastStart: canFastStartAndUpgrade);
                if (!string.IsNullOrWhiteSpace(builtinUrl))
                {
                    bool biFlac = builtinUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase) || _unblockService.LastResolvedBitrateKbps > 400;
                    var biBadge = _unblockService.FormatResolvedQualityBadge(builtinUrl, song, userQuality);
                    RecordResolvedSongQuality(song, builtinPlat, userQuality, null, builtinUrl, _unblockService.LastResolvedBitrateKbps, _unblockService.LastResolvedSizeBytes);
                    ApplyResolvedPlaybackInfo(song, builtinPlat, builtinUrl, biBadge, isPrefetch);
                    if (!biFlac && canFastStartAndUpgrade)
                    {
                        _pendingLosslessUpgrade = (song.Id, Math.Max(_unblockService.LastResolvedBitrateKbps, 128) * 1000, false);
                    }
                    return builtinUrl;
                }
            }
            else if (forcedNorm.ToLowerInvariant() is "gdmusic" or "pyncmd" or "kuwo" or "kugou" or "migu" or "custom")
            {
                var (directForcedUrl, directForcedPlat) = await _unblockService.MatchAudioUrlAsync(
                    song, [forcedNorm.ToLowerInvariant()], ct, preferLossless, userQuality);
                if (!string.IsNullOrWhiteSpace(directForcedUrl))
                {
                    var forcedBadge = _unblockService.FormatResolvedQualityBadge(directForcedUrl, song, userQuality);
                    RecordResolvedSongQuality(song, directForcedPlat, userQuality, null, directForcedUrl, _unblockService.LastResolvedBitrateKbps, _unblockService.LastResolvedSizeBytes);
                    ApplyResolvedPlaybackInfo(song, directForcedPlat, directForcedUrl, forcedBadge, isPrefetch);
                    return directForcedUrl;
                }
            }

            // 指定单一音源未命中该曲时（非 netease/qq/qishui 原生指定），平滑清理定向约束并自动并发其余可用音源兜底
            if (!string.Equals(forcedNorm, "netease", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(forcedNorm, song.Source, StringComparison.OrdinalIgnoreCase))
            {
                if (!isPrefetch)
                {
                    StatusMessage?.Invoke($"当前指定音源暂无《{song.Name}》，已自动并发切换其他可用音源");
                }
                forcePlatform = null;
            }
        }

        // 1.3 汽水音乐原生直链音源（无加密音频流秒播；方案 A 先播后升级无损）
        if (song.Source == "qishui" &&
            (string.IsNullOrWhiteSpace(forcePlatform) || forcePlatform == "qishui"))
        {
            var (qsUrl, qsKbps, qsSize) = await _thirdPartyService.GetQishuiPlayUrlAsync(song.QishuiTrackId, preferLossless, ct, userQuality);
            if (!string.IsNullOrWhiteSpace(qsUrl))
            {
                bool qsIsFlac = qsKbps > 400 || qsUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase);
                var qsBadge = qsIsFlac ? "无损 FLAC" : (qsKbps > 0 ? MusicQualityLevel.FormatActualQualityBadge(null, null, qsKbps * 1000, userQuality) : "汽水臻音");
                RecordResolvedSongQuality(song, "qishui", userQuality, null, qsUrl, qsKbps, qsSize);
                ApplyResolvedPlaybackInfo(song, "qishui", qsUrl, qsBadge, isPrefetch);
                if (!qsIsFlac && canFastStartAndUpgrade && _configService.Settings.EnableMusicUnblock)
                {
                    _pendingLosslessUpgrade = (song.Id, Math.Max(qsKbps, 128) * 1000, false);
                }
                return qsUrl;
            }

            var (ubQsUrl, ubQsPlat) = await _unblockService.MatchAudioUrlAsync(
                song, null, ct, preferLossless, userQuality, allowFastStart: canFastStartAndUpgrade);
            if (!string.IsNullOrWhiteSpace(ubQsUrl))
            {
                bool ubQsFlac = ubQsUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase) || _unblockService.LastResolvedBitrateKbps > 400;
                var ubBadge = _unblockService.FormatResolvedQualityBadge(ubQsUrl, song, userQuality);
                RecordResolvedSongQuality(song, ubQsPlat, userQuality, null, ubQsUrl, _unblockService.LastResolvedBitrateKbps, _unblockService.LastResolvedSizeBytes);
                ApplyResolvedPlaybackInfo(song, ubQsPlat, ubQsUrl, ubBadge, isPrefetch);
                if (!ubQsFlac && canFastStartAndUpgrade)
                {
                    _pendingLosslessUpgrade = (song.Id, Math.Max(_unblockService.LastResolvedBitrateKbps, 128) * 1000, false);
                }
                return ubQsUrl;
            }
        }

        // 1.4 国际音乐平台（严格纯国际候选源，内置 mpv 直接流式秒播不再被整首落盘卡住；未命中才回退官方 30 秒试听）
        if (song.Source is "spotify" or "apple" or "deezer" or "tidal" or "qobuz" or "ytmusic")
        {
            var (intlUrl, intlPlat) = await _unblockService.ResolveInternationalAudioAsync(song, preferLossless, ct, userQuality);
            if (!string.IsNullOrWhiteSpace(intlUrl))
            {
                string? finalPlaySource = intlUrl;
                bool isYtProxyWebm = intlPlat == "ytmusic" ||
                                     intlUrl.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
                                     intlUrl.Contains(".webm", StringComparison.OrdinalIgnoreCase);
                if (isYtProxyWebm)
                {
                    var safeKey = global::System.Text.RegularExpressions.Regex.Replace(song.Id ?? "track", @"[^A-Za-z0-9_\-]", "_");
                    var cachedWebm = Path.Combine(_configService.CacheDirectory, $"ytm_{safeKey}.webm");
                    if (File.Exists(cachedWebm) && new FileInfo(cachedWebm).Length > 4096)
                    {
                        finalPlaySource = cachedWebm;
                    }
                    else if (!_mpvService.IsAvailable(out _))
                    {
                        finalPlaySource = await _unblockService.DownloadInternationalStreamToCacheAsync(
                            intlUrl, song.Id ?? "track", _configService.CacheDirectory, ct);
                    }
                    else if (!isPrefetch)
                    {
                        // 内置 mpv 可直接流式秒播 WebM/Opus，后台异步落盘供下次离线复用
                        _ = _unblockService.DownloadInternationalStreamToCacheAsync(
                            intlUrl, song.Id ?? "track", _configService.CacheDirectory, CancellationToken.None);
                    }
                }

                if (!string.IsNullOrWhiteSpace(finalPlaySource))
                {
                    var intlBadge = _unblockService.FormatResolvedQualityBadge(intlUrl, song, userQuality);
                    long intlSize = _unblockService.LastResolvedSizeBytes;
                    if (intlSize <= 0 && !finalPlaySource.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        try { if (File.Exists(finalPlaySource)) intlSize = new FileInfo(finalPlaySource).Length; } catch { }
                    }
                    RecordResolvedSongQuality(song, intlPlat, userQuality, null, intlUrl, _unblockService.LastResolvedBitrateKbps, intlSize);
                    ApplyResolvedPlaybackInfo(song, intlPlat, finalPlaySource, intlBadge, isPrefetch);
                    return finalPlaySource;
                }
            }

            var (nativeUrl, _) = await _thirdPartyService.GetInternationalNativePlayUrlAsync(song, ct);
            if (!string.IsNullOrWhiteSpace(nativeUrl))
            {
                const string previewBadge = "试听 Preview";
                RecordResolvedSongQuality(song, $"{song.Source} (试听)", userQuality, MusicQualityLevel.Standard, nativeUrl, 128, 0);
                ApplyResolvedPlaybackInfo(song, song.Source, nativeUrl, previewBadge, isPrefetch);
                return nativeUrl;
            }

            if (!isPrefetch)
            {
                StatusMessage?.Invoke($"暂无《{song.Name}》可用的国际完整音源");
            }
            return null;
        }

        // 1.5 第三方原生曲库音源（QQ音乐 / 酷狗 / 酷我 / 咪咕：并发竞速秒播 + 方案 A 先播后无损升级）
        if (song.Source is "qq" or "kugou" or "kuwo" or "migu" &&
            (string.IsNullOrWhiteSpace(forcePlatform) || forcePlatform == song.Source))
        {
            bool hasEnabledLx = _configService.Settings.LxMusicScripts.Any(x => x.Enabled);
            string? qqFallbackUrl = null;
            if (song.Source == "qq" && !string.IsNullOrWhiteSpace(song.QqSongMid))
            {
                var qqUrl = await _qqMusicService.GetSongPlayUrlAsync(
                    song.QqSongMid, ct, preferLossless, userQuality, allowFastStart: canFastStartAndUpgrade && !hasEnabledLx);
                if (!string.IsNullOrWhiteSpace(qqUrl))
                {
                    bool qqIsFlac = qqUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase);
                    int qqKbps = qqIsFlac
                        ? 900
                        : (qqUrl.Contains("C400", StringComparison.OrdinalIgnoreCase) ? 128 : 320);
                    var qqBadge = qqIsFlac
                        ? "无损 FLAC"
                        : MusicQualityLevel.FormatActualQualityBadge(null, null, qqKbps * 1000, userQuality);

                    if (qqIsFlac || !preferLossless || (canFastStartAndUpgrade && !hasEnabledLx))
                    {
                        RecordResolvedSongQuality(song, "qq", userQuality, null, qqUrl, qqKbps, 0);
                        ApplyResolvedPlaybackInfo(song, "qq", qqUrl, qqBadge, isPrefetch);
                        if (!qqIsFlac && canFastStartAndUpgrade && _configService.Settings.EnableMusicUnblock)
                        {
                            _pendingLosslessUpgrade = (song.Id, qqKbps * 1000, false);
                        }
                        return qqUrl;
                    }
                    qqFallbackUrl = qqUrl;
                }
            }

            var (unblockUrl, platform) = await _unblockService.MatchAudioUrlAsync(
                song, null, ct, preferLossless, userQuality, allowFastStart: canFastStartAndUpgrade);
            if (!string.IsNullOrWhiteSpace(unblockUrl))
            {
                bool ubIsFlac = unblockUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase) ||
                                _unblockService.LastResolvedBitrateKbps > 400;
                var ubBadge = _unblockService.FormatResolvedQualityBadge(unblockUrl, song, userQuality);
                RecordResolvedSongQuality(song, platform, userQuality, null, unblockUrl, _unblockService.LastResolvedBitrateKbps, _unblockService.LastResolvedSizeBytes);
                ApplyResolvedPlaybackInfo(song, platform, unblockUrl, ubBadge, isPrefetch);
                if (!ubIsFlac && canFastStartAndUpgrade)
                {
                    _pendingLosslessUpgrade = (song.Id, Math.Max(_unblockService.LastResolvedBitrateKbps, 128) * 1000, false);
                }
                return unblockUrl;
            }

            if (!string.IsNullOrWhiteSpace(qqFallbackUrl))
            {
                int qqKbps = qqFallbackUrl.Contains("C400", StringComparison.OrdinalIgnoreCase) ? 128 : 320;
                var qqBadge = MusicQualityLevel.FormatActualQualityBadge(null, null, qqKbps * 1000, userQuality);
                RecordResolvedSongQuality(song, "qq", userQuality, null, qqFallbackUrl, qqKbps, 0);
                ApplyResolvedPlaybackInfo(song, "qq", qqFallbackUrl, qqBadge, isPrefetch);
                if (canFastStartAndUpgrade && _configService.Settings.EnableMusicUnblock)
                {
                    _pendingLosslessUpgrade = (song.Id, qqKbps * 1000, false);
                }
                return qqFallbackUrl;
            }
        }

        // 2. B站音源（已有本地缓存直接复用；否则优先内置 mpv 带 Referer 流式秒播，后台异步落盘缓存）
        if (song.Source == "bilibili" && !string.IsNullOrWhiteSpace(song.BilibiliBvid))
        {
            long biliCid = song.BilibiliCid ?? 0;
            var cachedPath = Path.Combine(_configService.CacheDirectory, $"bili_{song.BilibiliBvid}_{biliCid}.m4a");
            if (biliCid > 0 && File.Exists(cachedPath) && new FileInfo(cachedPath).Length > 1024)
            {
                const string biliBadge = "B站高音质";
                long biliSize = new FileInfo(cachedPath).Length;
                RecordResolvedSongQuality(song, "bilibili", userQuality, MusicQualityLevel.Exhigh, cachedPath, 192, biliSize);
                ApplyResolvedPlaybackInfo(song, "bilibili", cachedPath, biliBadge, isPrefetch);
                return cachedPath;
            }

            var streamUrl = await _bilibiliService.GetAudioStreamUrlAsync(song.BilibiliBvid, biliCid);
            if (!string.IsNullOrWhiteSpace(streamUrl))
            {
                const string biliBadge = "B站高音质";
                if (_mpvService.IsAvailable(out _))
                {
                    RecordResolvedSongQuality(song, "bilibili", userQuality, MusicQualityLevel.Exhigh, streamUrl, 192, 0);
                    ApplyResolvedPlaybackInfo(song, "bilibili", streamUrl, biliBadge, isPrefetch);
                    if (!isPrefetch)
                    {
                        _ = _bilibiliService.DownloadAudioToCacheAsync(
                            streamUrl, song.BilibiliBvid, biliCid, _configService.CacheDirectory, CancellationToken.None);
                    }
                    return streamUrl;
                }

                var cachedFile = await _bilibiliService.DownloadAudioToCacheAsync(
                    streamUrl, song.BilibiliBvid, biliCid, _configService.CacheDirectory, ct);
                if (!string.IsNullOrWhiteSpace(cachedFile))
                {
                    long biliSize = 0;
                    try { if (File.Exists(cachedFile)) biliSize = new FileInfo(cachedFile).Length; } catch { }
                    RecordResolvedSongQuality(song, "bilibili", userQuality, MusicQualityLevel.Exhigh, cachedFile, 192, biliSize);
                    ApplyResolvedPlaybackInfo(song, "bilibili", cachedFile, biliBadge, isPrefetch);
                    return cachedFile;
                }
            }
        }

        // 3. 若用户手动指定了第三方音源平台
        if (!string.IsNullOrWhiteSpace(forcePlatform) && forcePlatform != "netease")
        {
            var (unblockUrl, platform) = await _unblockService.MatchAudioUrlAsync(song, [forcePlatform], ct, preferLossless, userQuality);
            if (!string.IsNullOrWhiteSpace(unblockUrl))
            {
                var forcedBadge = _unblockService.FormatResolvedQualityBadge(unblockUrl, song, userQuality);
                RecordResolvedSongQuality(song, platform, userQuality, null, unblockUrl, _unblockService.LastResolvedBitrateKbps, _unblockService.LastResolvedSizeBytes);
                ApplyResolvedPlaybackInfo(song, platform, unblockUrl, forcedBadge, isPrefetch);
                return unblockUrl;
            }
        }

        // 4. 尝试网易云官方 EAPI 获取音频流（含真实返回音质 level 与码率 br）
        if (song.NumericId > 0)
        {
            var (url, encodeType, actualLevel, sizeBytes, br, isFreeTrial) = await _apiClient.GetSongUrlWithLevelAsync(
                song.NumericId, userQuality);

            bool isOfficialLossless = !string.IsNullOrWhiteSpace(url) &&
                                      !isFreeTrial &&
                                      (string.Equals(encodeType, "flac", StringComparison.OrdinalIgnoreCase) ||
                                       url.Contains(".flac", StringComparison.OrdinalIgnoreCase) ||
                                       br > 320000);
            bool wantsMasterOrHires = string.Equals(userQuality, MusicQualityLevel.Jymaster, StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(userQuality, MusicQualityLevel.Hires, StringComparison.OrdinalIgnoreCase);
            bool isOfficialMasterOrHires = isOfficialLossless &&
                                           (br >= 1350000 ||
                                            string.Equals(actualLevel, MusicQualityLevel.Jymaster, StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(actualLevel, MusicQualityLevel.Hires, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(url) && !isFreeTrial &&
                (!preferLossless || (isOfficialLossless && (!wantsMasterOrHires || isOfficialMasterOrHires)) || !_configService.Settings.EnableMusicUnblock))
            {
                var neteaseBadge = MusicQualityLevel.FormatActualQualityBadge(actualLevel, encodeType, br, userQuality);
                RecordResolvedSongQuality(song, "netease", userQuality, actualLevel, url, br >= 1000 ? br / 1000 : br, sizeBytes);
                ApplyResolvedPlaybackInfo(song, "netease", url, neteaseBadge, isPrefetch);
                return url;
            }

            // 5. 先播后升级：官方有完整（非试听）但未达目标音质的链接时，立即用官方链接开播，播放开始后后台寻找无损再无缝切换
            //    预解析下一首时时间充裕，仍走下面的同步解灰流程直接拿无损
            if (!isPrefetch && !string.IsNullOrWhiteSpace(url) && !isFreeTrial)
            {
                var quickBadge = MusicQualityLevel.FormatActualQualityBadge(actualLevel, encodeType, br, userQuality);
                RecordResolvedSongQuality(song, "netease", userQuality, actualLevel, url, br >= 1000 ? br / 1000 : br, sizeBytes);
                ApplyResolvedPlaybackInfo(song, "netease", url, quickBadge, isPrefetch);
                _pendingLosslessUpgrade = (song.Id, br, isOfficialLossless);
                return url;
            }

            // 6. 若为 VIP 试听/无版权，或用户请求了超清母带/Hi-Res/无损但官方因非 SVIP 降级到了有损 MP3，调用多音源解灰/目标音质通道
            if (_configService.Settings.EnableMusicUnblock)
            {
                var (unblockUrl, platform) = await _unblockService.MatchAudioUrlAsync(song, null, ct, preferLossless, userQuality);
                if (!string.IsNullOrWhiteSpace(unblockUrl))
                {
                    bool unblockIsFlac = unblockUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase) ||
                                         _unblockService.LastResolvedBitrateKbps > 400;
                    bool shouldUseUnblock = string.IsNullOrWhiteSpace(url) ||
                                            isFreeTrial ||
                                            !preferLossless ||
                                            (unblockIsFlac && !isOfficialLossless) ||
                                            (unblockIsFlac && _unblockService.LastResolvedBitrateKbps * 1000 > br);

                    if (shouldUseUnblock)
                    {
                        var unblockBadge = _unblockService.FormatResolvedQualityBadge(unblockUrl, song, userQuality);
                        RecordResolvedSongQuality(song, platform, userQuality, null, unblockUrl, _unblockService.LastResolvedBitrateKbps, _unblockService.LastResolvedSizeBytes);
                        ApplyResolvedPlaybackInfo(song, platform, unblockUrl, unblockBadge, isPrefetch);
                        return unblockUrl;
                    }
                }
            }

            // 若官方有完整非试听链接（如解灰未找到更高规格时），回退使用官方完整链接
            if (!string.IsNullOrWhiteSpace(url) && !isFreeTrial)
            {
                var neteaseBadge = MusicQualityLevel.FormatActualQualityBadge(actualLevel, encodeType, br, userQuality);
                RecordResolvedSongQuality(song, "netease", userQuality, actualLevel, url, br >= 1000 ? br / 1000 : br, sizeBytes);
                ApplyResolvedPlaybackInfo(song, "netease", url, neteaseBadge, isPrefetch);
                return url;
            }

            // 若解灰无结果但官方有试听链接，返回试听链接兜底
            if (!string.IsNullOrWhiteSpace(url))
            {
                var trialBadge = $"{MusicQualityLevel.FormatActualQualityBadge(actualLevel, encodeType, br, userQuality)} (试听)";
                RecordResolvedSongQuality(song, "netease (试听)", userQuality, actualLevel, url, br >= 1000 ? br / 1000 : br, sizeBytes);
                ApplyResolvedPlaybackInfo(song, "netease (试听)", url, trialBadge, isPrefetch);
                return url;
            }
        }

        return null;
    }

    /// <summary>
    /// 记录单曲实际解析落地的音质档位、码率与体积，并仅在请求触及上限或高于已记录上限时更新外部源实测天花板（主动切低音质时不覆盖高档上限）
    /// </summary>
    private void RecordResolvedSongQuality(
        SongItem song,
        string platform,
        string requestedQuality,
        string? actualLevel,
        string? url,
        int kbps,
        long sizeBytes)
    {
        if (song == null) return;

        if ((kbps <= 0 || kbps == 999) && sizeBytes > 0 && song.DurationMs > 2000)
        {
            int estimated = (int)(sizeBytes * 8L / song.DurationMs);
            if (estimated is >= 64 and <= 9000)
            {
                kbps = estimated;
            }
        }

        var inferredLevel = MusicQualityLevel.InferActualQualityLevel(actualLevel, url, kbps, sizeBytes);
        song.PlayingQualityLevel = inferredLevel;
        song.PlayingBitrateKbps = kbps;
        song.PlayingSizeBytes = sizeBytes;
        RunOnUi(() =>
        {
            song.PlayingQualityLevel = inferredLevel;
            song.PlayingBitrateKbps = kbps;
            song.PlayingSizeBytes = sizeBytes;
        });

        var platKey = (platform ?? string.Empty).Trim().ToLowerInvariant();
        bool isExternalSource = !string.IsNullOrEmpty(platKey) &&
                                platKey != "netease" &&
                                platKey != "local" &&
                                !platKey.Contains("试听", StringComparison.OrdinalIgnoreCase);
        if (!isExternalSource) return;

        int reqRank = MusicQualityLevel.GetQualityRank(requestedQuality);
        int actRank = MusicQualityLevel.GetQualityRank(inferredLevel);
        int hiresRank = MusicQualityLevel.GetQualityRank(MusicQualityLevel.Hires);
        var cacheKey = $"{song.Id}|{platKey}";

        if (reqRank > actRank || reqRank >= hiresRank)
        {
            // 请求了高于实际返回或最高档时，真实上限即此刻探测到的 actRank
            _observedExternalCeiling.AddOrUpdate(
                cacheKey,
                _ => (actRank, kbps, sizeBytes),
                (_, prev) =>
                {
                    if (reqRank >= hiresRank)
                    {
                        return (actRank, kbps > 0 ? kbps : prev.MaxKbps, sizeBytes > 0 ? sizeBytes : prev.MaxSize);
                    }
                    return (Math.Max(prev.MaxRank, actRank), Math.Max(prev.MaxKbps, kbps), Math.Max(prev.MaxSize, sizeBytes));
                });
        }
        else if (actRank > 0)
        {
            // 用户主动切低音质（reqRank <= actRank 且未请求最高档）时，仅在尚未有更高记录或本次意外更高时写入，绝不拉低原有上限
            _observedExternalCeiling.AddOrUpdate(
                cacheKey,
                _ => (actRank, kbps, sizeBytes),
                (_, prev) => actRank > prev.MaxRank
                    ? (actRank, kbps > 0 ? kbps : prev.MaxKbps, sizeBytes > 0 ? sizeBytes : prev.MaxSize)
                    : prev);
        }
    }

    /// <summary>
    /// 同步构建当前歌曲可用音质选项快照（直接读取内存缓存，确保 Flyout Opening 阶段秒出菜单并标注是否需要异步刷新详情）
    /// </summary>
    public IReadOnlyList<SongQualityOption> BuildAvailableQualitiesSnapshot(out bool needsAsyncRefresh)
    {
        needsAsyncRefresh = false;
        var song = CurrentSong;
        if (song == null)
        {
            var defLevel = _configService.Settings.MusicQuality;
            string[] defLevels =
            [
                MusicQualityLevel.Standard,
                MusicQualityLevel.Higher,
                MusicQualityLevel.Exhigh,
                MusicQualityLevel.Lossless,
                MusicQualityLevel.Hires,
                MusicQualityLevel.Jymaster
            ];
            return defLevels.Select(lvl => new SongQualityOption
            {
                Level = lvl,
                Label = MusicQualityLevel.GetDisplayName(lvl),
                IsCurrent = string.Equals(lvl, defLevel, StringComparison.OrdinalIgnoreCase)
            }).ToList();
        }

        // 1. 本地 / SMB 音乐文件：固定本地源文件，无在线多档位
        if (song.IsLocalSong)
        {
            var badge = string.IsNullOrWhiteSpace(song.PlayingQualityBadge) ? "本地音源" : song.PlayingQualityBadge;
            return
            [
                new SongQualityOption
                {
                    Level = song.PlayingQualityLevel ?? MusicQualityLevel.Lossless,
                    Label = badge,
                    Detail = MusicQualityLevel.FormatFileSizeShort(song.FileSize),
                    IsCurrent = true,
                    IsSwitchable = false
                }
            ];
        }

        var plat = (song.ResolvedPlatform ?? song.Source ?? string.Empty).Trim().ToLowerInvariant();
        var activeBadge = song.PlayingQualityBadge ?? string.Empty;

        // 2. B站 / YT 原生代理流 / 试听流：仅存在当前单一真实流
        if (song.Source == "bilibili" ||
            plat == "ytmusic" ||
            plat.Contains("试听", StringComparison.OrdinalIgnoreCase) ||
            activeBadge.Contains("试听", StringComparison.OrdinalIgnoreCase) ||
            activeBadge.Contains("Preview", StringComparison.OrdinalIgnoreCase))
        {
            var singleLabel = !string.IsNullOrWhiteSpace(activeBadge)
                ? activeBadge
                : (song.Source == "bilibili" ? "B站高音质" : "当前音质");
            var sizeDetail = MusicQualityLevel.FormatFileSizeShort(song.PlayingSizeBytes);
            return
            [
                new SongQualityOption
                {
                    Level = song.PlayingQualityLevel ?? MusicQualityLevel.Exhigh,
                    Label = singleLabel,
                    Detail = sizeDetail,
                    IsCurrent = true,
                    IsSwitchable = false
                }
            ];
        }

        // 确定当前正在播放的推断档位
        var currentPlayingLevel = song.PlayingQualityLevel;
        if (string.IsNullOrWhiteSpace(currentPlayingLevel))
        {
            currentPlayingLevel = MusicQualityLevel.InferActualQualityLevel(null, song.PlayMusicUrl, song.PlayingBitrateKbps, song.PlayingSizeBytes);
        }

        // 3. 汽水音乐原生流：严格依据接口返回的无加密音频流清单分组
        if (song.Source == "qishui" && plat == "qishui")
        {
            if (_thirdPartyService.TryGetCachedQishuiCandidates(song.QishuiTrackId, out var qsList) && qsList.Count > 0)
            {
                return BuildQishuiOptionsFromCandidates(qsList, currentPlayingLevel);
            }

            needsAsyncRefresh = !string.IsNullOrWhiteSpace(song.QishuiTrackId);
            return BuildSingleCurrentFallbackOption(song, currentPlayingLevel);
        }

        // 4. 网易云官方直连播放：精确读取 /api/song/music/detail/get 返回的真实存在档位
        List<(string Level, int BitrateBps, long SizeBytes, int SampleRate)> neteaseTiers = [];
        bool hasNeteaseCache = song.NumericId > 0 && _apiClient.TryGetCachedSongQualityDetail(song.NumericId, out neteaseTiers);
        if (song.NumericId > 0 && !hasNeteaseCache)
        {
            needsAsyncRefresh = true;
        }

        if (song.NumericId > 0 && (plat == "netease" || string.IsNullOrEmpty(plat)))
        {
            if (hasNeteaseCache && neteaseTiers.Count > 0)
            {
                return BuildNeteaseOptionsFromTiers(song, neteaseTiers, currentPlayingLevel);
            }

            // 未命中缓存前先展示当前真实播放的这一档，等待异步刷新替换为完整真实列表
            return BuildSingleCurrentFallbackOption(song, currentPlayingLevel);
        }

        // 5. 外部音源 / 解灰 / 第三方国内与国际平台（GDMUSIC / 落雪 / 酷狗 / QQ / 酷我 / 咪咕 / JOOX / Tidal / Apple）
        return BuildExternalAvailableQualityOptions(song, plat, currentPlayingLevel, hasNeteaseCache ? neteaseTiers : null);
    }

    /// <summary>
    /// 异步拉取完整单曲音质详情并返回精确可用的音质列表
    /// </summary>
    public async Task<IReadOnlyList<SongQualityOption>> GetAvailableQualitiesForCurrentSongAsync(CancellationToken ct = default)
    {
        var song = CurrentSong;
        if (song != null && !song.IsLocalSong)
        {
            var plat = (song.ResolvedPlatform ?? song.Source ?? string.Empty).Trim().ToLowerInvariant();
            if (song.Source == "qishui" && plat == "qishui" && !string.IsNullOrWhiteSpace(song.QishuiTrackId))
            {
                try { await _thirdPartyService.GetQishuiPlayCandidatesAsync(song.QishuiTrackId, ct); } catch { }
            }
            else if (song.NumericId > 0)
            {
                try { await _apiClient.GetSongQualityDetailAsync(song.NumericId).WaitAsync(ct); } catch { }
            }
        }

        return BuildAvailableQualitiesSnapshot(out _);
    }

    private static List<SongQualityOption> BuildSingleCurrentFallbackOption(SongItem song, string currentLevel)
    {
        var parts = new List<string>();
        if (song.PlayingBitrateKbps > 0 && song.PlayingBitrateKbps != 999) parts.Add($"{song.PlayingBitrateKbps}k");
        var sz = MusicQualityLevel.FormatFileSizeShort(song.PlayingSizeBytes);
        if (!string.IsNullOrEmpty(sz)) parts.Add(sz);

        return
        [
            new SongQualityOption
            {
                Level = currentLevel,
                Label = MusicQualityLevel.GetDisplayName(currentLevel),
                Detail = string.Join(" · ", parts),
                IsCurrent = true,
                IsSwitchable = true
            }
        ];
    }

    private List<SongQualityOption> BuildNeteaseOptionsFromTiers(
        SongItem song,
        List<(string Level, int BitrateBps, long SizeBytes, int SampleRate)> tiers,
        string currentPlayingLevel)
    {
        // 若未开启解灰且当前实际档位低于用户请求档位（例如非 VIP 被服务器降级），则高于当前可播上限的 VIP 档位不可直切，予隐藏
        int reqRank = MusicQualityLevel.GetQualityRank(_configService.Settings.MusicQuality);
        int curRank = MusicQualityLevel.GetQualityRank(currentPlayingLevel);
        int? nonVipCeilingRank = (!_configService.Settings.EnableMusicUnblock && reqRank > curRank) ? curRank : null;

        var list = new List<SongQualityOption>();
        foreach (var (level, br, size, sr) in tiers)
        {
            if (nonVipCeilingRank.HasValue && MusicQualityLevel.GetQualityRank(level) > nonVipCeilingRank.Value)
            {
                continue;
            }

            var details = new List<string>();
            if (sr >= 48000 && MusicQualityLevel.GetQualityRank(level) >= 4)
            {
                details.Add($"{sr / 1000.0:0.#}kHz");
            }
            var sizeStr = MusicQualityLevel.FormatFileSizeShort(size);
            if (!string.IsNullOrEmpty(sizeStr))
            {
                details.Add(sizeStr);
            }
            else if (br > 0)
            {
                details.Add($"{(br >= 1000 ? br / 1000 : br)}k");
            }

            list.Add(new SongQualityOption
            {
                Level = level,
                Label = MusicQualityLevel.GetDisplayName(level),
                Detail = string.Join(" · ", details),
                IsCurrent = string.Equals(level, currentPlayingLevel, StringComparison.OrdinalIgnoreCase),
                IsSwitchable = true
            });
        }

        if (list.Count == 0)
        {
            return BuildSingleCurrentFallbackOption(song, currentPlayingLevel);
        }

        // 确保必然有一个当前高亮项
        if (!list.Any(o => o.IsCurrent))
        {
            var fallbackCur = list.OrderBy(o => Math.Abs(MusicQualityLevel.GetQualityRank(o.Level) - curRank)).First();
            fallbackCur.IsCurrent = true;
        }
        return list;
    }

    private static List<SongQualityOption> BuildQishuiOptionsFromCandidates(
        List<(string Url, int BitrateKbps, long SizeBytes)> candidates,
        string currentPlayingLevel)
    {
        var byLevel = new Dictionary<string, (int Kbps, long Size)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, kbps, size) in candidates)
        {
            var lvl = MusicQualityLevel.InferActualQualityLevel(null, null, kbps, size);
            if (!byLevel.TryGetValue(lvl, out var prev) || kbps > prev.Kbps)
            {
                byLevel[lvl] = (kbps, size);
            }
        }

        var list = new List<SongQualityOption>();
        foreach (var lvl in MusicQualityLevel.AllOrderedLevels)
        {
            if (!byLevel.TryGetValue(lvl, out var info)) continue;
            var details = new List<string>();
            if (info.Kbps > 0) details.Add($"{info.Kbps}k");
            var sz = MusicQualityLevel.FormatFileSizeShort(info.Size);
            if (!string.IsNullOrEmpty(sz)) details.Add(sz);

            list.Add(new SongQualityOption
            {
                Level = lvl,
                Label = MusicQualityLevel.GetDisplayName(lvl),
                Detail = string.Join(" · ", details),
                IsCurrent = string.Equals(lvl, currentPlayingLevel, StringComparison.OrdinalIgnoreCase),
                IsSwitchable = list.Count > 0 || byLevel.Count > 1
            });
        }

        if (list.Count == 1)
        {
            list[0].IsCurrent = true;
            list[0].IsSwitchable = false;
        }
        else if (list.Count > 1 && !list.Any(x => x.IsCurrent))
        {
            list[^1].IsCurrent = true;
        }
        return list;
    }

    private List<SongQualityOption> BuildExternalAvailableQualityOptions(
        SongItem song,
        string plat,
        string currentPlayingLevel,
        List<(string Level, int BitrateBps, long SizeBytes, int SampleRate)>? cachedNeteaseTiers)
    {
        var candidateLevels = new List<string>();

        if (song.Source == "kugou")
        {
            // 酷狗曲目：严格按照歌单/搜索返回的真实 Hash 确定存在哪些档位
            if (!string.IsNullOrWhiteSpace(song.KugouHash)) candidateLevels.Add(MusicQualityLevel.Standard);
            if (!string.IsNullOrWhiteSpace(song.Kugou320Hash)) candidateLevels.Add(MusicQualityLevel.Exhigh);
            if (!string.IsNullOrWhiteSpace(song.KugouSqHash)) candidateLevels.Add(MusicQualityLevel.Lossless);
            if (!string.IsNullOrWhiteSpace(song.KugouResHash)) candidateLevels.Add(MusicQualityLevel.Hires);
            if (plat == "lxmusic")
            {
                var lxQ = _unblockService.GetSupportedLxQualities(song);
                if (lxQ.Count > 0)
                {
                    candidateLevels.RemoveAll(l => !lxQ.Contains(UnblockMusicService.MapUserQualityToLxQuality(l)));
                }
            }
            if (candidateLevels.Count == 0)
            {
                candidateLevels.AddRange([MusicQualityLevel.Standard, MusicQualityLevel.Exhigh, MusicQualityLevel.Lossless]);
            }
        }
        else if (plat == "lxmusic")
        {
            var lxQ = _unblockService.GetSupportedLxQualities(song);
            if (lxQ.Contains("128k")) candidateLevels.Add(MusicQualityLevel.Standard);
            if (lxQ.Contains("320k")) candidateLevels.Add(MusicQualityLevel.Exhigh);
            if (lxQ.Contains("flac")) candidateLevels.Add(MusicQualityLevel.Lossless);
            if (lxQ.Contains("flac24bit")) candidateLevels.Add(MusicQualityLevel.Hires);
            if (candidateLevels.Count == 0)
            {
                candidateLevels.AddRange([MusicQualityLevel.Standard, MusicQualityLevel.Exhigh, MusicQualityLevel.Lossless]);
            }
        }
        else if (plat == "apple")
        {
            candidateLevels.Add(MusicQualityLevel.Exhigh);
            if (MusicQualityLevel.GetQualityRank(currentPlayingLevel) >= 4)
            {
                candidateLevels.Add(MusicQualityLevel.Lossless);
            }
        }
        else if (plat is "qq" or "kuwo" or "migu")
        {
            candidateLevels.AddRange([MusicQualityLevel.Standard, MusicQualityLevel.Exhigh, MusicQualityLevel.Lossless, MusicQualityLevel.Hires]);
        }
        else
        {
            // gdmusic / pyncmd / joox / tidal / 其它多源解灰：无网易云独占音效（臻音/环绕/母带）
            candidateLevels.AddRange([MusicQualityLevel.Standard, MusicQualityLevel.Higher, MusicQualityLevel.Exhigh, MusicQualityLevel.Lossless, MusicQualityLevel.Hires]);
        }

        // 若有网易云原曲详情缓存且原曲既无 hr 也无 jm，同时当前实测码率 < 1350k，剔除不可达的 Hi-Res
        if (cachedNeteaseTiers != null && cachedNeteaseTiers.Count > 0 && song.PlayingBitrateKbps < 1350)
        {
            bool hasOriginalHires = cachedNeteaseTiers.Any(t =>
                t.Level is MusicQualityLevel.Hires or MusicQualityLevel.Jymaster);
            if (!hasOriginalHires)
            {
                candidateLevels.Remove(MusicQualityLevel.Hires);
            }
        }

        // 依据已实测触达的天花板裁剪超上限的虚高档位（例如请求最高无损但 GDMusic 仅返回 885k，自动过滤 Hi-Res）
        int curRank = MusicQualityLevel.GetQualityRank(currentPlayingLevel);
        if (!string.IsNullOrEmpty(plat) &&
            _observedExternalCeiling.TryGetValue($"{song.Id}|{plat}", out var ceiling) &&
            ceiling.MaxRank > 0)
        {
            int effectiveMaxRank = Math.Max(ceiling.MaxRank, curRank);
            candidateLevels.RemoveAll(l => MusicQualityLevel.GetQualityRank(l) > effectiveMaxRank);
        }

        // 确保当前正在输出的档位一定存在于列表中
        if (!candidateLevels.Any(l => string.Equals(l, currentPlayingLevel, StringComparison.OrdinalIgnoreCase)))
        {
            candidateLevels.Add(currentPlayingLevel);
        }

        var ordered = candidateLevels
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(MusicQualityLevel.GetQualityRank)
            .ToList();

        var result = new List<SongQualityOption>(ordered.Count);
        foreach (var lvl in ordered)
        {
            bool isCur = string.Equals(lvl, currentPlayingLevel, StringComparison.OrdinalIgnoreCase);
            var label = MusicQualityLevel.GetDisplayName(lvl);
            string detail = string.Empty;

            if (isCur)
            {
                var parts = new List<string>();
                if (song.PlayingBitrateKbps > 0 && song.PlayingBitrateKbps != 999)
                {
                    parts.Add($"{song.PlayingBitrateKbps}k");
                }
                var sz = MusicQualityLevel.FormatFileSizeShort(song.PlayingSizeBytes);
                if (!string.IsNullOrEmpty(sz))
                {
                    parts.Add(sz);
                }
                detail = string.Join(" · ", parts);
            }

            result.Add(new SongQualityOption
            {
                Level = lvl,
                Label = label,
                Detail = detail,
                IsCurrent = isCur,
                IsSwitchable = ordered.Count > 1
            });
        }

        return result;
    }

    /// <summary>
    /// 针对 SMB 共享目录 (\\server\share\...) 或映射网络盘音频文件进行预检与本地高速缓存暂存，
    /// 彻底规避 Windows MediaFoundation 对 UNC 路径/中文共享路径/SMB 共享锁不兼容导致无法播放的问题
    /// </summary>
    private async Task<string?> PrepareLocalOrSmbAudioFileAsync(string filePath, CancellationToken ct)
    {
        return await Task.Run(async () =>
        {
            var normalized = filePath.Trim().Replace('/', '\\');
            bool exists = false;
            try
            {
                exists = File.Exists(normalized);
            }
            catch
            {
                exists = false;
            }

            if (!exists) return null;

            bool isNetworkOrUnc = normalized.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase);
            if (!isNetworkOrUnc)
            {
                try
                {
                    var root = Path.GetPathRoot(normalized);
                    if (!string.IsNullOrWhiteSpace(root))
                    {
                        var drive = new DriveInfo(root);
                        isNetworkOrUnc = drive.DriveType == DriveType.Network;
                    }
                }
                catch
                {
                    // 忽略盘符类型检测异常
                }
            }

            // 若内置 mpv 可用，直接返回规范化后的本地/SMB 原始路径交由 mpv 流式直通播放（1:1 对应 resolveMpvPlaybackFilePath）
            if (_mpvService.IsAvailable(out _) || !isNetworkOrUnc)
            {
                return normalized;
            }

            // 对 SMB 共享目录或网络映射盘：
            // 1) 超大文件（如 >160MB 的 5.1 声道整张串烧专辑 FLAC）直接返回路径由 MediaFoundation / 共享 FileStream 流式解码，避免整包复制等待；
            // 2) 常规大小单曲拷贝至本地高速缓存目录，避免网络抖动与 SMB 文件锁冲突。
            try
            {
                var fi = new FileInfo(normalized);
                if (fi.Length > 160L * 1024 * 1024)
                {
                    return normalized;
                }

                var cacheDir = Path.Combine(_configService.CacheDirectory, "SmbAudioCache");
                Directory.CreateDirectory(cacheDir);

                var ext = Path.GetExtension(normalized);
                if (string.IsNullOrWhiteSpace(ext)) ext = ".flac";

                var hashInput = $"{normalized.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
                var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)))[..20];
                var stagedPath = Path.Combine(cacheDir, $"smb_{hash}{ext}");

                if (File.Exists(stagedPath) && new FileInfo(stagedPath).Length == fi.Length)
                {
                    return stagedPath;
                }

                var tempPath = stagedPath + ".tmp";
                await using (var sourceStream = new FileStream(
                    normalized,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 131072,
                    useAsync: true))
                await using (var destStream = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 131072,
                    useAsync: true))
                {
                    await sourceStream.CopyToAsync(destStream, 131072, ct);
                }

                File.Move(tempPath, stagedPath, overwrite: true);
                return stagedPath;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 若缓存目录不可写，回退原始路径由 StartNaudioPipeline 使用共享 FileStream 流式解码
                return normalized;
            }
        }, ct);
    }

    private WaveStream CreateWaveReaderRobust(string mediaSource)
    {
        if (mediaSource.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            mediaSource.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new MediaFoundationReader(mediaSource);
        }

        // 1. 优先使用 MediaFoundationReader 直接解码（原生支持 5.1 六声道 / 7.1 八声道 FLAC/WAV/AAC/M4A，不会触发 AudioFileReader 内部 SampleChannel 对 >2 声道的异常）
        try
        {
            return new MediaFoundationReader(mediaSource);
        }
        catch
        {
            // 2. 回退：以 FileShare.ReadWrite | FileShare.Delete 打开共享只读文件流交由 StreamMediaFoundationReader 解码（兼容 SMB 共享文件锁）
            try
            {
                _activeFileStream?.Dispose();
                _activeFileStream = new FileStream(
                    mediaSource,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 131072);
                return new StreamMediaFoundationReader(_activeFileStream);
            }
            catch
            {
                _activeFileStream?.Dispose();
                _activeFileStream = null;
                return new AudioFileReader(mediaSource);
            }
        }
    }

    private void StartNaudioPipeline(string mediaSource, TimeSpan? initialSeekPosition, int sessionId, CancellationToken ct)
    {
        if (sessionId != _currentPlaySessionId || ct.IsCancellationRequested) return;

        WaveStream? waveReader = null;
        try
        {
            waveReader = CreateWaveReaderRobust(mediaSource);
        }
        catch
        {
            if (sessionId != _currentPlaySessionId || ct.IsCancellationRequested) return;
            throw;
        }

        if (sessionId != _currentPlaySessionId || ct.IsCancellationRequested)
        {
            waveReader?.Dispose();
            return;
        }

        if (initialSeekPosition is { TotalSeconds: > 0.5 } seekPos && waveReader.CanSeek && seekPos < waveReader.TotalTime)
        {
            waveReader.CurrentTime = seekPos;
        }

        var sampleProvider = waveReader.ToSampleProvider();
        int sourceChannels = Math.Max(1, sampleProvider.WaveFormat.Channels);
        int sampleRate = sampleProvider.WaveFormat.SampleRate;

        // 若检测到 5.1 (6声道) 或 7.1 (8声道) 多声道音源，自动在底部音质徽章标记 5.1 / 7.1
        if (sourceChannels >= 6 && CurrentSong != null)
        {
            string chLabel = sourceChannels == 6 ? "5.1" : sourceChannels == 8 ? "7.1" : $"{sourceChannels}CH";
            var songRef = CurrentSong;
            RunOnUi(() =>
            {
                var currentBadge = songRef.PlayingQualityBadge ?? string.Empty;
                if (!currentBadge.Contains(chLabel, StringComparison.OrdinalIgnoreCase))
                {
                    songRef.PlayingQualityBadge = string.IsNullOrWhiteSpace(currentBadge) || currentBadge == "LOCAL"
                        ? $"{chLabel} FLAC"
                        : $"{chLabel} {currentBadge}";
                    QualityBadgeChanged?.Invoke(songRef.PlayingQualityBadge);
                }
            });
        }

        int targetDevice = _configService.Settings.AudioOutputDeviceNumber;
        if (targetDevice >= WaveOut.DeviceCount)
        {
            targetDevice = -1;
        }

        bool supportsNativeMultichannel = false;
        int endpointChannels = 2;
        int? endpointChannelMask = null;
        MMDevice? targetMmDevice = null;

        if (sourceChannels > 2)
        {
            supportsNativeMultichannel = TryProbeMultichannelEndpoint(
                targetDevice,
                sampleRate,
                sourceChannels,
                out targetMmDevice,
                out endpointChannels,
                out endpointChannelMask);
        }

        // 1:1 对应 src/renderer/utils/audioDestinationChannel.ts (不做软件矩阵混解码)
        _ = EqualizerSampleProvider.ResolveAudioDestinationChannelConfig(sourceChannels, endpointChannels);

        var eqProvider = new EqualizerSampleProvider(sampleProvider, _configService.Settings.Equalizer)
        {
            PlaybackRate = PlaybackRate
        };

        var multichannelGainProvider = new MultichannelGainSampleProvider(eqProvider, _configService.Settings.Multichannel);

        var fadeProvider = new FadeSampleProvider(multichannelGainProvider);
        if (_configService.Settings.EnableAudioFade)
        {
            fadeProvider.StartFadeIn(_configService.Settings.AudioFadeDurationSeconds * 1000.0);
        }
        else
        {
            fadeProvider.ResetToFullVolume();
        }

        var volumeProvider = new VolumeSampleProvider(fadeProvider)
        {
            Volume = Math.Clamp(_configService.Settings.Volume, 0f, 1f)
        };

        IWavePlayer? outputDevice = null;
        bool initialized = false;

        if (sourceChannels > 2 && supportsNativeMultichannel)
        {
            var multichannelWaveProvider = new MultichannelSampleToWaveProvider(volumeProvider, endpointChannelMask);
            try
            {
                var wasapiOut = targetMmDevice != null
                    ? new WasapiOut(targetMmDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 100)
                    : new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 100);
                wasapiOut.PlaybackStopped += OnPlaybackStopped;
                wasapiOut.Init(multichannelWaveProvider);
                outputDevice = wasapiOut;
                initialized = true;
            }
            catch
            {
                outputDevice?.Dispose();
                outputDevice = null;
            }
        }
        else if (sourceChannels == 6 && endpointChannels != 6)
        {
            StatusMessage?.Invoke($"检测到 6 声道音源，但 Windows 默认输出当前为 {endpointChannels} 声道");
        }

        targetMmDevice?.Dispose();

        // 路径 B：标准双声道/单声道（或多声道回退下混后）输出
        if (!initialized)
        {
            var waveOut = new WaveOutEvent
            {
                DeviceNumber = targetDevice,
                DesiredLatency = 150
            };
            waveOut.PlaybackStopped += OnPlaybackStopped;

            try
            {
                waveOut.Init(volumeProvider);
                outputDevice = waveOut;
            }
            catch
            {
                // 若声卡不支持超高采样率 (如 192kHz / 384kHz)，自动重采样至 48000Hz 后重新初始化
                waveOut.Dispose();
                var fallbackWaveOut = new WaveOutEvent
                {
                    DeviceNumber = -1,
                    DesiredLatency = 150
                };
                fallbackWaveOut.PlaybackStopped += OnPlaybackStopped;
                var resampled = new WdlResamplingSampleProvider(volumeProvider, 48000);
                fallbackWaveOut.Init(resampled);
                outputDevice = fallbackWaveOut;
            }
        }

        // 临界区同步：严格验证 session ID，销毁旧设备并替换为新设备
        lock (_deviceLock)
        {
            if (sessionId != _currentPlaySessionId || ct.IsCancellationRequested)
            {
                outputDevice?.Dispose();
                waveReader.Dispose();
                return;
            }

            StopNaudioInternal();

            _waveReader = waveReader;
            _eqProvider = eqProvider;
            _multichannelGainProvider = multichannelGainProvider;
            _fadeProvider = fadeProvider;
            _volumeProvider = volumeProvider;
            _outputDevice = outputDevice;
            _isStoppingManually = false;

            _outputDevice?.Play();
        }

        IsPlaying = true;
        UpdateSmtcPlayState(true);
        PlayStateChanged?.Invoke(true);
    }

    /// <summary>
    /// 探测当前选择的 Windows 音频输出设备是否支持 5.1 (6声道) / 7.1 (8声道) 原生多声道输出，并返回匹配的扬声器通道掩码（如 0x3F 后置 5.1 或 0x60F 侧置 5.1）
    /// </summary>
    private static bool TryProbeMultichannelEndpoint(
        int waveOutDeviceNumber,
        int sampleRate,
        int sourceChannels,
        out MMDevice? matchedDevice,
        out int endpointChannels,
        out int? endpointChannelMask)
    {
        matchedDevice = null;
        endpointChannels = 2;
        endpointChannelMask = null;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (waveOutDeviceNumber >= 0 && waveOutDeviceNumber < WaveOut.DeviceCount)
            {
                var caps = WaveOut.GetCapabilities(waveOutDeviceNumber);
                var prodName = caps.ProductName?.Trim();
                if (!string.IsNullOrWhiteSpace(prodName))
                {
                    foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    {
                        if (dev.FriendlyName.Contains(prodName, StringComparison.OrdinalIgnoreCase) ||
                            prodName.Contains(dev.FriendlyName, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedDevice = dev;
                            break;
                        }
                        dev.Dispose();
                    }
                }
            }

            matchedDevice ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            var mixFormat = matchedDevice.AudioClient.MixFormat;
            endpointChannels = Math.Max(1, mixFormat.Channels);
            endpointChannelMask = MultichannelSampleToWaveProvider.TryGetChannelMask(mixFormat);

            // 1. 若 Windows 混音器当前已配置为 >= 源声道数（例如 Windows 声音控制面板已设为 5.1 环绕声 / 7.1 环绕声）
            if (endpointChannels >= sourceChannels)
            {
                return true;
            }

            // 2. 同时探测 WASAPI 共享模式是否直接接受 5.1 Back (0x3F) 或 5.1 Side/Surround (0x60F) / 7.1 (0x63F)
            int[] candidateMasks = sourceChannels == 6
                ? [MultichannelSampleToWaveProvider.SpeakerMask5Point1Back, MultichannelSampleToWaveProvider.SpeakerMask5Point1Surround]
                : [MultichannelSampleToWaveProvider.GetDefaultSpeakerMask(sourceChannels)];

            int[] candidateRates = sampleRate == mixFormat.SampleRate
                ? [sampleRate]
                : [sampleRate, mixFormat.SampleRate];

            foreach (int rate in candidateRates)
            {
                foreach (int mask in candidateMasks)
                {
                    var testWf = MultichannelSampleToWaveProvider.CreateExtensibleFloatWaveFormat(rate, sourceChannels, mask);
                    if (matchedDevice.AudioClient.IsFormatSupported(AudioClientShareMode.Shared, testWf, out var closest))
                    {
                        endpointChannels = sourceChannels;
                        endpointChannelMask = mask;
                        return true;
                    }
                    if (closest != null && closest.Channels >= sourceChannels)
                    {
                        endpointChannels = closest.Channels;
                        endpointChannelMask = MultichannelSampleToWaveProvider.TryGetChannelMask(closest) ?? mask;
                        return true;
                    }
                }
            }
        }
        catch
        {
            // 若枚举或探测异常，检查 WaveOut Capabilities 兜底
            try
            {
                if (waveOutDeviceNumber >= -1 && waveOutDeviceNumber < WaveOut.DeviceCount)
                {
                    var caps = WaveOut.GetCapabilities(waveOutDeviceNumber);
                    if (caps.Channels >= sourceChannels)
                    {
                        endpointChannels = caps.Channels;
                        return true;
                    }
                }
            }
            catch
            {
                // 忽略
            }
        }

        return false;
    }

    private async Task LoadLyricsForCurrentSongAsync(SongItem song)
    {
        var parsed = await _lyricService.GetParsedLyricAsync(song);
        if (CurrentSong?.Id != song.Id) return;

        RunOnUi(() =>
        {
            CurrentLyric = parsed;
            CurrentLyricLine = parsed.Lines.FirstOrDefault();
            LyricLoaded?.Invoke(parsed);
            LyricLineChanged?.Invoke(CurrentLyricLine);
        });
    }

    /// <summary>
    /// 启动时恢复上次播放歌曲的封面、歌词与进度条状态（若开启了启动自动播放则直接续播）
    /// </summary>
    public async Task RestoreLastSessionSongStateAsync()
    {
        var song = CurrentSong;
        if (song == null) return;

        UpdateSmtcMetadata(song);
        _ = LoadLyricsForCurrentSongAsync(song);

        double savedSec = Math.Max(0, _configService.Settings.LastPlayedPositionSeconds);
        var dur = TimeSpan.FromMilliseconds(song.DurationMs);
        var savedPos = dur.TotalSeconds > 1 && savedSec < dur.TotalSeconds - 1
            ? TimeSpan.FromSeconds(savedSec)
            : TimeSpan.Zero;

        RunOnUi(() =>
        {
            SongChanged?.Invoke(song);
            PositionChanged?.Invoke(savedPos, dur);
        });

        if (_configService.Settings.AutoPlay)
        {
            await PlaySongAsync(song, initialSeekPosition: savedPos > TimeSpan.Zero ? savedPos : null);
        }
    }

    public void TogglePlayPause()
    {
        if (IsPlaying)
        {
            Pause();
        }
        else
        {
            Resume();
        }
    }

    public void Pause()
    {
        _configService.Settings.LastPlayedPositionSeconds = Position.TotalSeconds;
        _ = _configService.SaveSettingsAsync();
        _isStoppingManually = true;

        // 同时暂停内置 mpv 与 NAudio，绝不遗漏任一后端导致孤儿音频
        _ = _mpvService.PauseAsync();

        lock (_deviceLock)
        {
            if (_outputDevice != null)
            {
                try
                {
                    _outputDevice.Pause();
                }
                catch { }
            }
        }

        IsPlaying = false;
        UpdateSmtcPlayState(false);
        PlayStateChanged?.Invoke(false);
    }

    public void Resume()
    {
        _isStoppingManually = false;
        if (_configService.Settings.EnableAudioFade && _fadeProvider != null)
        {
            _fadeProvider.StartFadeIn(_configService.Settings.AudioFadeDurationSeconds * 1000.0);
        }
        else
        {
            _fadeProvider?.ResetToFullVolume();
        }

        if (_activeBackend == "mpv" && !_mpvService.IsIdle)
        {
            _ = _mpvService.ResumeAsync();
            IsPlaying = true;
            UpdateSmtcPlayState(true);
            PlayStateChanged?.Invoke(true);
            return;
        }

        lock (_deviceLock)
        {
            if (_outputDevice == null)
            {
                if (CurrentSong != null)
                {
                    double savedSec = Math.Max(0, _configService.Settings.LastPlayedPositionSeconds);
                    TimeSpan? seekPos = savedSec > 1.0 ? TimeSpan.FromSeconds(savedSec) : null;
                    _ = PlaySongAsync(CurrentSong, initialSeekPosition: seekPos);
                }
                else if (Queue.Count > 0)
                {
                    _ = PlaySongAsync(Queue[0]);
                }
                return;
            }

            try
            {
                _outputDevice.Play();
            }
            catch { }
        }

        IsPlaying = true;
        UpdateSmtcPlayState(true);
        PlayStateChanged?.Invoke(true);
    }

    /// <summary>
    /// 停止当前歌曲播放并将播放进度重置为 00:00
    /// </summary>
    public void StopPlayback()
    {
        Interlocked.Increment(ref _currentPlaySessionId);
        _loadCts?.Cancel();
        _isStoppingManually = true;
        StopInternal();

        IsBuffering = false;
        IsPlaying = false;
        _configService.Settings.LastPlayedPositionSeconds = 0;
        _ = _configService.SaveSettingsAsync();

        if (_smtc != null)
        {
            try
            {
                _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
            }
            catch
            {
            }
        }

        var dur = TimeSpan.FromMilliseconds(CurrentSong?.DurationMs ?? 0);
        RunOnUi(() =>
        {
            if (CurrentSong != null)
            {
                CurrentSong.IsLoading = false;
            }
            foreach (var q in Queue)
            {
                q.IsAudioActivelyPlaying = false;
            }
            PlayStateChanged?.Invoke(false);
            PositionChanged?.Invoke(TimeSpan.Zero, dur);
        });
    }

    public void Seek(TimeSpan position)
    {
        _configService.Settings.LastPlayedPositionSeconds = Math.Max(0, position.TotalSeconds);
        if (_activeBackend == "mpv")
        {
            var clampedMpv = TimeSpan.FromMilliseconds(Math.Clamp(position.TotalMilliseconds, 0, Duration.TotalMilliseconds));
            _ = _mpvService.SeekAsync(clampedMpv.TotalSeconds);
            PositionChanged?.Invoke(clampedMpv, Duration);
            return;
        }

        if (_waveReader == null || !_waveReader.CanSeek) return;
        var clamped = TimeSpan.FromMilliseconds(Math.Clamp(position.TotalMilliseconds, 0, Duration.TotalMilliseconds));
        _eqProvider?.ResetEndOfStream();
        _waveReader.CurrentTime = clamped;
        PositionChanged?.Invoke(clamped, Duration);
    }

    public void SetVolume(float volume)
    {
        var v = Math.Clamp(volume, 0f, 1f);
        _configService.Settings.Volume = v;
        if (_activeBackend == "mpv")
        {
            _ = _mpvService.SetVolumeAsync(v);
        }
        if (_volumeProvider != null)
        {
            _volumeProvider.Volume = v;
        }
    }

    public void UpdateEqualizer(EqualizerConfig eqConfig)
    {
        _configService.Settings.Equalizer = eqConfig;
        _eqProvider?.ApplyConfig(eqConfig);
        if (_activeBackend == "mpv")
        {
            _ = _mpvService.SetEqAsync(eqConfig.IsEnabled, eqConfig.BandGains);
        }
        _ = _configService.SaveSettingsAsync();
    }

    /// <summary>
    /// 设置播放倍速（0.5x ~ 2.0x）
    /// </summary>
    public void SetPlaybackRate(float rate)
    {
        PlaybackRate = Math.Clamp(rate, 0.5f, 2.0f);
        if (_eqProvider != null)
        {
            _eqProvider.PlaybackRate = PlaybackRate;
        }
        if (_activeBackend == "mpv")
        {
            _ = _mpvService.SetPlaybackRateAsync(PlaybackRate);
        }
        _configService.Settings.PlaybackRate = PlaybackRate;
        _ = _configService.SaveSettingsAsync();
        StatusMessage?.Invoke($"播放倍速已设为 {PlaybackRate:0.0#}x");
    }

    /// <summary>
    /// 调整当前歌曲歌词时间轴偏移（毫秒）
    /// </summary>
    public void AdjustLyricOffset(int deltaMs)
    {
        if (deltaMs == 0)
        {
            LyricOffsetMs = 0;
        }
        else
        {
            LyricOffsetMs += deltaMs;
        }
        StatusMessage?.Invoke($"歌词时间偏移: {LyricOffsetMs / 1000.0:+0.0;-0.0;0.0}s");
    }

    /// <summary>
    /// 开启心动模式（智能推荐播放列表）
    /// </summary>
    public async Task StartIntelligenceModeAsync(SongItem? seedSong = null)
    {
        var target = seedSong ?? CurrentSong ?? Queue.FirstOrDefault();
        if (target == null || target.NumericId <= 0)
        {
            StatusMessage?.Invoke("请先播放一首在线歌曲以开启心动模式");
            return;
        }

        StatusMessage?.Invoke("正在生成心动模式智能歌单...");
        var list = await _apiClient.GetIntelligenceListAsync(target.NumericId, target.Album?.Id ?? 0);
        if (list.Count > 0)
        {
            _configService.Settings.PlayMode = PlayMode.Intelligence;
            await PlaySongAsync(list[0], list);
            StatusMessage?.Invoke($"已开启心动模式，共推荐 {list.Count} 首歌曲");
        }
        else
        {
            StatusMessage?.Invoke("心动模式歌单获取失败（需登录网易云账号或切歌重试）");
        }
    }

    /// <summary>
    /// 设置定时停止播放（minutes <= 0 表示取消定时）
    /// </summary>
    public void SetSleepTimer(int minutes)
    {
        _sleepTimer?.Dispose();
        _sleepTimer = null;

        if (minutes <= 0)
        {
            SleepTimerEndTime = null;
            StatusMessage?.Invoke("已取消定时停止播放");
            return;
        }

        SleepTimerEndTime = DateTime.Now.AddMinutes(minutes);
        _sleepTimer = new Timer(_ =>
        {
            Pause();
            SleepTimerEndTime = null;
            StatusMessage?.Invoke("定时播放时间已到，已自动暂停");
        }, null, TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);

        StatusMessage?.Invoke($"将在 {minutes} 分钟后自动暂停播放");
    }

    /// <summary>
    /// 枚举本机可用的音频输出设备列表（对应原 AudioDeviceSettings.vue）
    /// </summary>
    public List<(int DeviceNumber, string Name)> GetAudioOutputDevices()
    {
        var list = new List<(int DeviceNumber, string Name)>
        {
            (-1, "系统默认音频输出设备 (Default)")
        };

        try
        {
            for (int n = 0; n < WaveOut.DeviceCount; n++)
            {
                var caps = WaveOut.GetCapabilities(n);
                list.Add((n, caps.ProductName));
            }
        }
        catch
        {
            // 忽略枚举异常
        }
        return list;
    }

    /// <summary>
    /// 在选定音频输出设备上播放 0.5 秒 440Hz 测试音（对应原 AudioDeviceSettings.vue handleTest）
    /// </summary>
    public async Task PlayTestToneAsync(int deviceNumber = -1)
    {
        await Task.Run(async () =>
        {
            try
            {
                var sig = new SignalGenerator(44100, 2)
                {
                    Gain = 0.25,
                    Frequency = 440,
                    Type = SignalGeneratorType.Sin
                }.Take(TimeSpan.FromMilliseconds(500));

                using var waveOut = new WaveOutEvent
                {
                    DeviceNumber = deviceNumber,
                    DesiredLatency = 100
                };
                waveOut.Init(sig);
                waveOut.Play();
                await Task.Delay(520);
            }
            catch (Exception ex)
            {
                StatusMessage?.Invoke($"测试音频播放失败: {ex.Message}");
            }
        });
    }

    public async Task NextAsync(bool isAutoEnded = false)
    {
        if (Queue.Count == 0) return;

        var mode = _configService.Settings.PlayMode;
        if (isAutoEnded && mode == PlayMode.LoopOne && CurrentSong != null)
        {
            Seek(TimeSpan.Zero);
            Resume();
            return;
        }

        int currentIdx = CurrentSong != null
            ? Queue.IndexOf(Queue.FirstOrDefault(s => s.Id == CurrentSong.Id)!)
            : -1;

        int nextIdx;
        if (mode == PlayMode.Random && Queue.Count > 1)
        {
            nextIdx = Random.Shared.Next(Queue.Count);
            if (nextIdx == currentIdx) nextIdx = (nextIdx + 1) % Queue.Count;
        }
        else
        {
            nextIdx = (currentIdx + 1) % Queue.Count;
        }

        await PlaySongAsync(Queue[nextIdx]);
    }

    public async Task PreviousAsync()
    {
        if (Queue.Count == 0) return;
        int currentIdx = CurrentSong != null
            ? Queue.IndexOf(Queue.FirstOrDefault(s => s.Id == CurrentSong.Id)!)
            : 0;
        int prevIdx = (currentIdx - 1 + Queue.Count) % Queue.Count;
        await PlaySongAsync(Queue[prevIdx]);
    }

    public void SetAudioFade(bool enable, double durationSeconds)
    {
        _configService.Settings.EnableAudioFade = enable;
        _configService.Settings.AudioFadeDurationSeconds = durationSeconds;
        if (!enable)
        {
            _fadeProvider?.ResetToFullVolume();
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        lock (_deviceLock)
        {
            if (sender != _outputDevice) return;
        }

        if (_isStoppingManually || IsBuffering) return;

        bool reachedEnd = _eqProvider?.ReachedEndOfStream == true ||
                          (_waveReader != null &&
                           _waveReader.TotalTime.TotalSeconds > 1 &&
                           (_waveReader.TotalTime - _waveReader.CurrentTime).TotalSeconds < 6.0);

        if (reachedEnd)
        {
            TriggerAutoNextOnUiThread();
        }
    }

    private void TriggerAutoNextOnUiThread()
    {
        long nowMs = Environment.TickCount64;
        long prev = Interlocked.Exchange(ref _lastAutoAdvanceTickMs, nowMs);
        if (nowMs - prev < 800) return;

        // 在线音频流距结尾仍超过 10 秒就读完，判定为网络断流/直链过期：重新解析并从当前进度续播（每首最多 1 次）
        var cur = CurrentSong;
        if (_activeBackend == "naudio" && cur != null && !cur.IsLocalSong && _streamRetrySongId != cur.Id)
        {
            var resumePos = Position;
            if ((Duration - resumePos).TotalSeconds > 10)
            {
                _streamRetrySongId = cur.Id;
                StatusMessage?.Invoke($"《{cur.Name}》音频流中断，正在重新连接...");
                RunOnUi(() => _ = PlaySongAsync(cur, initialSeekPosition: resumePos));
                return;
            }
        }

        IsPlaying = false;
        UpdateSmtcPlayState(false);
        PlayStateChanged?.Invoke(false);

        RunOnUi(() =>
        {
            if (SongEndedNaturally != null)
            {
                SongEndedNaturally.Invoke();
            }
            else
            {
                _ = NextAsync(isAutoEnded: true);
            }
        });
    }

    private void OnPositionTick(object? state)
    {
        if (!IsPlaying) return;

        TimeSpan pos;
        TimeSpan dur;

        if (_activeBackend == "mpv")
        {
            pos = TimeSpan.FromSeconds(Math.Max(0, _mpvService.CurrentTimeSeconds));
            dur = _mpvService.DurationSeconds > 0
                ? TimeSpan.FromSeconds(_mpvService.DurationSeconds)
                : TimeSpan.FromMilliseconds(CurrentSong?.DurationMs ?? 0);
        }
        else
        {
            var reader = _waveReader;
            if (reader == null) return;

            // 兜底检测：若音频流已自然读完且 WaveOutEvent 已处于 Stopped 状态，确保触发自动下一首
            if (!_isStoppingManually &&
                !IsBuffering &&
                _eqProvider?.ReachedEndOfStream == true &&
                _outputDevice?.PlaybackState == PlaybackState.Stopped)
            {
                TriggerAutoNextOnUiThread();
                return;
            }

            try
            {
                pos = reader.CurrentTime;
                dur = reader.TotalTime;
            }
            catch
            {
                return;
            }
        }

        _configService.Settings.LastPlayedPositionSeconds = pos.TotalSeconds;
        PositionChanged?.Invoke(pos, dur);

        // 同步当前歌词行与逐字进度（必须在 UI 线程更新 LyricLine 属性，防止沉浸式歌词页 XAML x:Bind 跨线程崩溃）
        var lines = CurrentLyric.Lines;
        if (lines.Count > 0)
        {
            double posMs = pos.TotalMilliseconds + LyricOffsetMs;
            LyricLine? matched = null;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (posMs >= lines[i].StartTimeMs)
                {
                    matched = lines[i];
                    break;
                }
            }
            matched ??= lines[0];

            double progress = matched.DurationMs > 0
                ? Math.Clamp((posMs - matched.StartTimeMs) / matched.DurationMs, 0.0, 1.0)
                : 0.0;

            bool lineChanged = CurrentLyricLine != matched;
            var prevLine = CurrentLyricLine;
            if (lineChanged)
            {
                CurrentLyricLine = matched;
            }

            RunOnUi(() =>
            {
                matched.WordProgress = progress;
                if (lineChanged)
                {
                    if (prevLine != null) prevLine.IsCurrent = false;
                    matched.IsCurrent = true;
                    LyricLineChanged?.Invoke(matched);
                }
            });
        }
    }

    private void UpdateSmtcMetadata(SongItem song)
    {
        if (_smtc == null) return;
        try
        {
            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = song.Name;
            updater.MusicProperties.Artist = song.ArtistText;
            updater.MusicProperties.AlbumTitle = song.AlbumText;
            if (!string.IsNullOrWhiteSpace(song.PicUrl) && song.PicUrl.StartsWith("http"))
            {
                updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(song.CoverThumbnailUrl));
            }
            updater.Update();
        }
        catch
        {
            // 忽略 SMTC 元数据异常
        }
    }

    private void UpdateSmtcPlayState(bool playing)
    {
        if (_smtc == null) return;
        try
        {
            _smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        }
        catch
        {
            // 忽略
        }
    }

    private void StopNaudioInternal()
    {
        _isStoppingManually = true;
        if (_outputDevice != null)
        {
            _outputDevice.PlaybackStopped -= OnPlaybackStopped;
            try
            {
                _outputDevice.Stop();
            }
            catch
            {
                // 忽略设备停止异常
            }
            try
            {
                _outputDevice.Dispose();
            }
            catch
            {
                // 忽略设备释放异常
            }
            _outputDevice = null;
        }

        if (_waveReader != null)
        {
            try
            {
                _waveReader.Dispose();
            }
            catch { }
            _waveReader = null;
        }

        if (_activeFileStream != null)
        {
            try
            {
                _activeFileStream.Dispose();
            }
            catch { }
            _activeFileStream = null;
        }

        _fadeProvider = null;
        _multichannelGainProvider = null;
        _eqProvider = null;
        _volumeProvider = null;
    }

    /// <summary>
    /// 实时更新 5.1 / 7.1 各声道微调增益与静音配置并持久化保存
    /// </summary>
    private CancellationTokenSource? _multichannelApplyCts;

    public void UpdateMultichannelConfig(MultichannelConfig config)
    {
        _configService.Settings.Multichannel = config;
        // NAudio 后端仅更新增益数组，实时生效且不会卡顿
        _multichannelGainProvider?.ApplyConfig(config);
        // mpv 后端通过 af-command 实时修改各声道 volume，不重建音频链
        _ = _mpvService.SetChannelGainsAsync(MultichannelGainSampleProvider.ComputeGains(config));

        // 拖动滑块时防抖保存配置，避免频繁写盘
        _multichannelApplyCts?.Cancel();
        var cts = _multichannelApplyCts = new CancellationTokenSource();
        _ = Task.Delay(400, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _ = _configService.SaveSettingsAsync();
        }, TaskScheduler.Default);
    }

    private void StopInternal()
    {
        lock (_deviceLock)
        {
            StopNaudioInternal();
        }
        _ = _mpvService.StopAsync();
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _currentPlaySessionId);
        if (CurrentSong != null)
        {
            _configService.Settings.LastPlayedSongId = CurrentSong.Id;
            _configService.Settings.LastPlayedSongPath = CurrentSong.LocalFilePath ?? string.Empty;
            _configService.Settings.LastPlayedSong = CurrentSong;
            var currentSec = Position.TotalSeconds;
            if (currentSec > 0.5)
            {
                _configService.Settings.LastPlayedPositionSeconds = currentSec;
            }
            _configService.SaveSettingsSync();
        }

        _positionTimer.Dispose();
        _sleepTimer?.Dispose();
        _loadCts?.Cancel();
        StopInternal();
        _mpvService.Dispose();
        GC.SuppressFinalize(this);
    }
}
