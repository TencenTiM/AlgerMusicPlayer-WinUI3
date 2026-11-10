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
    private readonly UnblockMusicService _unblockService;
    private readonly BilibiliService _bilibiliService;
    private readonly LyricService _lyricService;

    private IWavePlayer? _outputDevice;
    private WaveStream? _waveReader;
    private Stream? _activeFileStream;
    private EqualizerSampleProvider? _eqProvider;
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
    public event Action? SongEndedNaturally;
    public event Action<string>? StatusMessage;

    public AudioPlayerService(
        ConfigService configService,
        NeteaseApiClient apiClient,
        QqMusicService qqMusicService,
        UnblockMusicService unblockService,
        BilibiliService bilibiliService,
        LyricService lyricService)
    {
        _configService = configService;
        _apiClient = apiClient;
        _qqMusicService = qqMusicService;
        _unblockService = unblockService;
        _bilibiliService = bilibiliService;
        _lyricService = lyricService;

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
    public async Task PlaySongAsync(SongItem song, IEnumerable<SongItem>? newQueue = null, string? forcePlatform = null, TimeSpan? initialSeekPosition = null)
    {
        if (song == null) return;

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

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

            // 实时持久化当前播放歌曲及所在本地文件夹，确保进程退出重进后精准记忆
            _configService.Settings.LastPlayedSongId = song.Id;
            _configService.Settings.LastPlayedSongPath = song.LocalFilePath ?? string.Empty;
            _configService.Settings.LastPlayedPositionSeconds = initialSeekPosition?.TotalSeconds ?? 0;
            _configService.Settings.LastPlayedSong = song;
            _ = _configService.SaveSettingsAsync();
        });

        IsBuffering = true;
        SongChanged?.Invoke(song);
        UpdateSmtcMetadata(song);

        // 异步并行加载歌词
        if (initialSeekPosition == null)
        {
            _ = LoadLyricsForCurrentSongAsync(song);
        }

        try
        {
            var resolvedSource = await ResolvePlayableSourceAsync(song, forcePlatform, ct);
            if (ct.IsCancellationRequested) return;

            if (string.IsNullOrWhiteSpace(resolvedSource))
            {
                StatusMessage?.Invoke($"无法解析《{song.Name}》的音频源，已自动跳过");
                RunOnUi(() => song.IsLoading = false);
                IsBuffering = false;
                return;
            }

            // 1:1 对应 .worktrees/local-music-folder-tree/src/renderer/services/audioService.ts:
            // 本地或 SMB 文件优先交由内置 mpv (--ao=wasapi --audio-channels=5.1,stereo --ad-lavc-downmix=no) 原生无下混播放
            bool isLocalOrUnc = !resolvedSource.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                                !resolvedSource.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            if (isLocalOrUnc && _mpvService.IsAvailable(out _))
            {
                StopNaudioInternal();
                _activeBackend = "mpv";
                _isStoppingManually = false;

                var eq = _configService.Settings.Equalizer;
                await _mpvService.LoadAsync(new MpvLoadRequest
                {
                    FilePath = resolvedSource,
                    ShouldPlay = true,
                    SeekTimeSeconds = initialSeekPosition?.TotalSeconds ?? 0,
                    Volume = Math.Clamp(_configService.Settings.Volume, 0f, 1f),
                    PlaybackRate = PlaybackRate,
                    EqEnabled = eq.IsEnabled,
                    EqBandGains = eq.BandGains
                }, ct);

                IsPlaying = true;
                UpdateSmtcPlayState(true);
                PlayStateChanged?.Invoke(true);
            }
            else
            {
                if (_activeBackend == "mpv")
                {
                    await _mpvService.StopAsync();
                }
                _activeBackend = "naudio";
                await Task.Run(() => StartNaudioPipeline(resolvedSource, initialSeekPosition), ct);
            }

            RunOnUi(() => song.IsLoading = false);
            IsBuffering = false;
            if (initialSeekPosition == null)
            {
                await _configService.AddPlayHistoryAsync(song);
            }
        }
        catch (OperationCanceledException)
        {
            // 已切换下一首，忽略
        }
        catch (Exception ex)
        {
            RunOnUi(() => song.IsLoading = false);
            IsBuffering = false;
            StatusMessage?.Invoke($"播放失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 在线播放时实时切换目标音质等级，并保持当前播放进度无缝重载当前在线歌曲
    /// </summary>
    public async Task SwitchQualityLevelAsync(string qualityLevel)
    {
        if (string.IsNullOrWhiteSpace(qualityLevel)) return;
        _configService.Settings.MusicQuality = qualityLevel;
        await _configService.SaveSettingsAsync();

        var displayName = MusicQualityLevel.GetDisplayName(qualityLevel);
        if (CurrentSong == null || CurrentSong.IsLocalSong)
        {
            QualityBadgeChanged?.Invoke(MusicQualityLevel.GetShortBadgeName(qualityLevel));
            StatusMessage?.Invoke($"默认在线音质已切换为：{displayName}");
            return;
        }

        var savedPos = Position;
        StatusMessage?.Invoke($"正在切换《{CurrentSong.Name}》音质至 {displayName}...");
        await PlaySongAsync(CurrentSong, forcePlatform: null, initialSeekPosition: savedPos.TotalSeconds > 1 ? savedPos : null);
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

        // 2. QQ 音乐独立歌单音源（优先请求 RS01 母带 / F000 无损 FLAC 及 GD 音乐台 999k 无损）
        if (song.Source == "qq" && !string.IsNullOrWhiteSpace(song.QqSongMid))
        {
            var qqUrl = await _qqMusicService.GetSongPlayUrlAsync(song.QqSongMid, ct, preferLossless: true);
            if (!string.IsNullOrWhiteSpace(qqUrl) && qqUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase))
            {
                return (qqUrl, ".flac");
            }

            if (_configService.Settings.EnableMusicUnblock)
            {
                var (unblockUrl, _) = await _unblockService.MatchAudioUrlAsync(song, null, ct, preferLossless: true);
                if (!string.IsNullOrWhiteSpace(unblockUrl))
                {
                    if (unblockUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(qqUrl))
                    {
                        return (unblockUrl, unblockUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase) ? ".flac" : string.Empty);
                    }
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
    /// 解析歌曲可播放地址（支持本地/SMB 文件、B站音频、网易云直连及多音源解灰回退，并实时计算正在播放的音质徽章）
    /// </summary>
    public async Task<string?> ResolvePlayableSourceAsync(SongItem song, string? forcePlatform = null, CancellationToken ct = default)
    {
        var userQuality = _configService.Settings.MusicQuality;
        bool preferLossless = IsLosslessQualityLevel(userQuality);

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
                RunOnUi(() =>
                {
                    song.LocalFilePath = rawLocalPath;
                    song.ResolvedPlatform = "local";
                    song.PlayMusicUrl = localOrStagedPath;
                    song.PlayingQualityBadge = localBadge;
                });
                QualityBadgeChanged?.Invoke(localBadge);
                return localOrStagedPath;
            }

            StatusMessage?.Invoke($"无法访问本地或 SMB 共享文件：{rawLocalPath}");
            return null;
        }

        // 1.5 QQ 音乐独立歌单音源
        if (song.Source == "qq" && !string.IsNullOrWhiteSpace(song.QqSongMid))
        {
            var qqUrl = await _qqMusicService.GetSongPlayUrlAsync(song.QqSongMid, ct, preferLossless);
            if (!string.IsNullOrWhiteSpace(qqUrl))
            {
                var qqBadge = qqUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase)
                    ? "无损 FLAC"
                    : MusicQualityLevel.GetShortBadgeName(userQuality);
                RunOnUi(() =>
                {
                    song.ResolvedPlatform = "qq";
                    song.PlayMusicUrl = qqUrl;
                    song.PlayingQualityBadge = qqBadge;
                });
                QualityBadgeChanged?.Invoke(qqBadge);
                return qqUrl;
            }

            if (_configService.Settings.EnableMusicUnblock)
            {
                var (unblockUrl, platform) = await _unblockService.MatchAudioUrlAsync(song, null, ct, preferLossless);
                if (!string.IsNullOrWhiteSpace(unblockUrl))
                {
                    var ubBadge = _unblockService.FormatResolvedQualityBadge(unblockUrl, song, userQuality);
                    RunOnUi(() =>
                    {
                        song.ResolvedPlatform = platform;
                        song.PlayMusicUrl = unblockUrl;
                        song.PlayingQualityBadge = ubBadge;
                    });
                    QualityBadgeChanged?.Invoke(ubBadge);
                    return unblockUrl;
                }
            }
        }

        // 2. B站音源
        if (song.Source == "bilibili" && !string.IsNullOrWhiteSpace(song.BilibiliBvid))
        {
            var streamUrl = await _bilibiliService.GetAudioStreamUrlAsync(song.BilibiliBvid, song.BilibiliCid ?? 0);
            if (!string.IsNullOrWhiteSpace(streamUrl))
            {
                var cachedFile = await _bilibiliService.DownloadAudioToCacheAsync(
                    streamUrl, song.BilibiliBvid, song.BilibiliCid ?? 0, _configService.CacheDirectory, ct);
                if (!string.IsNullOrWhiteSpace(cachedFile))
                {
                    const string biliBadge = "B站高音质";
                    RunOnUi(() =>
                    {
                        song.ResolvedPlatform = "bilibili";
                        song.PlayMusicUrl = cachedFile;
                        song.PlayingQualityBadge = biliBadge;
                    });
                    QualityBadgeChanged?.Invoke(biliBadge);
                    return cachedFile;
                }
            }
        }

        // 3. 若用户手动指定了第三方音源平台
        if (!string.IsNullOrWhiteSpace(forcePlatform) && forcePlatform != "netease")
        {
            var (unblockUrl, platform) = await _unblockService.MatchAudioUrlAsync(song, [forcePlatform], ct, preferLossless);
            if (!string.IsNullOrWhiteSpace(unblockUrl))
            {
                var forcedBadge = _unblockService.FormatResolvedQualityBadge(unblockUrl, song, userQuality);
                RunOnUi(() =>
                {
                    song.ResolvedPlatform = platform;
                    song.PlayMusicUrl = unblockUrl;
                    song.PlayingQualityBadge = forcedBadge;
                });
                QualityBadgeChanged?.Invoke(forcedBadge);
                return unblockUrl;
            }
        }

        // 4. 尝试网易云官方 EAPI 获取音频流（含真实返回音质 level 与码率 br）
        if (song.NumericId > 0)
        {
            var (url, encodeType, actualLevel, _, br, isFreeTrial) = await _apiClient.GetSongUrlWithLevelAsync(
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
                RunOnUi(() =>
                {
                    song.ResolvedPlatform = "netease";
                    song.PlayMusicUrl = url;
                    song.PlayingQualityBadge = neteaseBadge;
                });
                QualityBadgeChanged?.Invoke(neteaseBadge);
                return url;
            }

            // 5. 若为 VIP 试听/无版权，或用户请求了超清母带/Hi-Res/无损但官方因非 SVIP 降级到了有损 MP3，调用 24bit Hi-Res / 母带解灰通道
            if (_configService.Settings.EnableMusicUnblock)
            {
                var (unblockUrl, platform) = await _unblockService.MatchAudioUrlAsync(song, null, ct, preferLossless);
                if (!string.IsNullOrWhiteSpace(unblockUrl))
                {
                    bool unblockIsFlac = unblockUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase) ||
                                         _unblockService.LastResolvedBitrateKbps > 400;
                    bool shouldUseUnblock = string.IsNullOrWhiteSpace(url) ||
                                            isFreeTrial ||
                                            (unblockIsFlac && !isOfficialLossless) ||
                                            (unblockIsFlac && _unblockService.LastResolvedBitrateKbps * 1000 > br);

                    if (shouldUseUnblock)
                    {
                        var unblockBadge = _unblockService.FormatResolvedQualityBadge(unblockUrl, song, userQuality);
                        RunOnUi(() =>
                        {
                            song.ResolvedPlatform = platform;
                            song.PlayMusicUrl = unblockUrl;
                            song.PlayingQualityBadge = unblockBadge;
                        });
                        QualityBadgeChanged?.Invoke(unblockBadge);
                        return unblockUrl;
                    }
                }
            }

            // 若官方有完整非试听链接（如解灰未找到更高规格时），回退使用官方完整链接
            if (!string.IsNullOrWhiteSpace(url) && !isFreeTrial)
            {
                var neteaseBadge = MusicQualityLevel.FormatActualQualityBadge(actualLevel, encodeType, br, userQuality);
                RunOnUi(() =>
                {
                    song.ResolvedPlatform = "netease";
                    song.PlayMusicUrl = url;
                    song.PlayingQualityBadge = neteaseBadge;
                });
                QualityBadgeChanged?.Invoke(neteaseBadge);
                return url;
            }

            // 若解灰无结果但官方有试听链接，返回试听链接兜底
            if (!string.IsNullOrWhiteSpace(url))
            {
                var trialBadge = $"{MusicQualityLevel.FormatActualQualityBadge(actualLevel, encodeType, br, userQuality)} (试听)";
                RunOnUi(() =>
                {
                    song.ResolvedPlatform = "netease (试听)";
                    song.PlayMusicUrl = url;
                    song.PlayingQualityBadge = trialBadge;
                });
                QualityBadgeChanged?.Invoke(trialBadge);
                return url;
            }
        }

        return null;
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

    private void StartNaudioPipeline(string mediaSource, TimeSpan? initialSeekPosition = null)
    {
        StopInternal();

        _waveReader = CreateWaveReaderRobust(mediaSource);
        if (initialSeekPosition is { TotalSeconds: > 0.5 } seekPos && _waveReader.CanSeek && seekPos < _waveReader.TotalTime)
        {
            _waveReader.CurrentTime = seekPos;
        }

        var sampleProvider = _waveReader.ToSampleProvider();
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

        _eqProvider = new EqualizerSampleProvider(sampleProvider, _configService.Settings.Equalizer)
        {
            PlaybackRate = PlaybackRate
        };
        _volumeProvider = new VolumeSampleProvider(_eqProvider)
        {
            Volume = Math.Clamp(_configService.Settings.Volume, 0f, 1f)
        };

        bool initialized = false;

        if (sourceChannels > 2 && supportsNativeMultichannel)
        {
            var multichannelWaveProvider = new MultichannelSampleToWaveProvider(_volumeProvider, endpointChannelMask);
            try
            {
                var wasapiOut = targetMmDevice != null
                    ? new WasapiOut(targetMmDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 100)
                    : new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 100);
                wasapiOut.PlaybackStopped += OnPlaybackStopped;
                wasapiOut.Init(multichannelWaveProvider);
                _outputDevice = wasapiOut;
                initialized = true;
            }
            catch
            {
                _outputDevice?.Dispose();
                _outputDevice = null;
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
                waveOut.Init(_volumeProvider);
                _outputDevice = waveOut;
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
                var resampled = new WdlResamplingSampleProvider(_volumeProvider, 48000);
                fallbackWaveOut.Init(resampled);
                _outputDevice = fallbackWaveOut;
            }
        }

        _isStoppingManually = false;
        _outputDevice?.Play();

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

        if (_activeBackend == "mpv")
        {
            _isStoppingManually = true;
            _ = _mpvService.PauseAsync();
            IsPlaying = false;
            UpdateSmtcPlayState(false);
            PlayStateChanged?.Invoke(false);
            return;
        }

        if (_outputDevice == null) return;
        _isStoppingManually = true;
        _outputDevice.Pause();
        IsPlaying = false;
        UpdateSmtcPlayState(false);
        PlayStateChanged?.Invoke(false);
    }

    public void Resume()
    {
        if (_activeBackend == "mpv" && !_mpvService.IsIdle)
        {
            _isStoppingManually = false;
            _ = _mpvService.ResumeAsync();
            IsPlaying = true;
            UpdateSmtcPlayState(true);
            PlayStateChanged?.Invoke(true);
            return;
        }

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

        _isStoppingManually = false;
        _outputDevice.Play();
        IsPlaying = true;
        UpdateSmtcPlayState(true);
        PlayStateChanged?.Invoke(true);
    }

    /// <summary>
    /// 停止当前歌曲播放并将播放进度重置为 00:00
    /// </summary>
    public void StopPlayback()
    {
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

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
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
            _outputDevice.Dispose();
            _outputDevice = null;
        }

        if (_waveReader != null)
        {
            _waveReader.Dispose();
            _waveReader = null;
        }

        if (_activeFileStream != null)
        {
            _activeFileStream.Dispose();
            _activeFileStream = null;
        }
    }

    private void StopInternal()
    {
        StopNaudioInternal();
        if (_activeBackend == "mpv")
        {
            _ = _mpvService.StopAsync();
        }
    }

    public void Dispose()
    {
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
        StopNaudioInternal();
        _mpvService.Dispose();
        GC.SuppressFinalize(this);
    }
}
