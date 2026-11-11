using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace AlgerMusicPlayer.WinUI.Models;

/// <summary>
/// 代理配置
/// </summary>
public class ProxyConfig
{
    public bool Enable { get; set; } = false;
    public string Protocol { get; set; } = "http";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7890;
}

/// <summary>
/// 10段均衡器配置与预设
/// </summary>
public class EqualizerConfig
{
    public bool IsEnabled { get; set; } = false;
    public string ActivePresetName { get; set; } = "Flat";
    public float PreampDb { get; set; } = 0f;
    /// <summary>
    /// 10段频点增益 (dB, -12 ~ +12): 31Hz, 62Hz, 125Hz, 250Hz, 500Hz, 1kHz, 2kHz, 4kHz, 8kHz, 16kHz
    /// </summary>
    public float[] BandGains { get; set; } = new float[10];

    public static readonly int[] Frequencies = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    public static readonly Dictionary<string, (string DisplayName, float[] Gains)> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["flat"] = ("平坦", [0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f]),
        ["pop"] = ("流行", [-1.5f, 3.5f, 5.5f, 3.5f, -0.5f, -1.5f, 1.5f, 2.5f, 2.5f, 2.5f]),
        ["rock"] = ("摇滚", [4.5f, 3.5f, 2f, 0.5f, -0.5f, -1f, 0.5f, 2f, 2.5f, 3.5f]),
        ["classical"] = ("古典", [3.5f, 3f, 2.5f, 1.5f, -0.5f, -1.5f, -1.5f, 0.5f, 2f, 3f]),
        ["jazz"] = ("爵士", [3f, 2f, 1.5f, 2f, -1f, -1.5f, -0.5f, 1f, 2.5f, 3f]),
        ["hiphop"] = ("嘻哈", [5f, 4.5f, 3f, 1.5f, -0.5f, -1f, 0.5f, 1.5f, 2f, 2.5f]),
        ["vocal"] = ("人声", [-2f, -1.5f, -1f, 0.5f, 2f, 3.5f, 3f, 1.5f, 0.5f, 0f]),
        ["dance"] = ("舞曲", [4f, 3.5f, 2.5f, 1f, 0f, -0.5f, 1.5f, 2.5f, 3f, 2.5f]),
        ["acoustic"] = ("原声", [2f, 1.5f, 1f, 1.5f, 2f, 1.5f, 2f, 2.5f, 2f, 1.5f])
    };
}

/// <summary>
/// 多声道 5.1 / 7.1 环绕声校准与各音箱独立细节配置
/// </summary>
public class MultichannelConfig
{
    public bool IsEnabled { get; set; } = true;

    // 各声道微调增益 (dB, -12dB ~ +12dB, 默认 0dB)
    public float FrontLeftDb { get; set; } = 0f;
    public float FrontRightDb { get; set; } = 0f;
    public float CenterDb { get; set; } = 0f;
    public float SubwooferDb { get; set; } = 0f;
    public float SurroundLeftDb { get; set; } = 0f;
    public float SurroundRightDb { get; set; } = 0f;
    public float BackLeftDb { get; set; } = 0f;
    public float BackRightDb { get; set; } = 0f;

    // 各声道静音控制
    public bool MuteFrontLeft { get; set; } = false;
    public bool MuteFrontRight { get; set; } = false;
    public bool MuteCenter { get; set; } = false;
    public bool MuteSubwoofer { get; set; } = false;
    public bool MuteSurroundLeft { get; set; } = false;
    public bool MuteSurroundRight { get; set; } = false;
    public bool MuteBackLeft { get; set; } = false;
    public bool MuteBackRight { get; set; } = false;

    public void ResetToDefaults()
    {
        FrontLeftDb = 0f;
        FrontRightDb = 0f;
        CenterDb = 0f;
        SubwooferDb = 0f;
        SurroundLeftDb = 0f;
        SurroundRightDb = 0f;
        BackLeftDb = 0f;
        BackRightDb = 0f;

        MuteFrontLeft = false;
        MuteFrontRight = false;
        MuteCenter = false;
        MuteSubwoofer = false;
        MuteSurroundLeft = false;
        MuteSurroundRight = false;
        MuteBackLeft = false;
        MuteBackRight = false;
    }
}

/// <summary>
/// 快捷键配置项（1:1 对齐 src/shared/shortcuts.ts 与 ShortcutSettings.vue）
/// </summary>
public partial class ShortcutItem : ObservableObject
{
    public string ActionId { get; set; } = string.Empty;
    public string GroupKey { get; set; } = "playback"; // playback | sound | window
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    private string _key = string.Empty;
    public string Key
    {
        get => _key;
        set
        {
            if (SetProperty(ref _key, value))
            {
                OnPropertyChanged(nameof(FormattedKey));
            }
        }
    }

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    private string _scope = "global"; // global | app
    public string Scope
    {
        get => _scope;
        set
        {
            if (SetProperty(ref _scope, value))
            {
                OnPropertyChanged(nameof(ScopeIndex));
            }
        }
    }

    [JsonIgnore]
    public int ScopeIndex
    {
        get => string.Equals(Scope, "app", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        set => Scope = value == 1 ? "app" : "global";
    }

    private bool _isRecording;
    [JsonIgnore]
    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            if (SetProperty(ref _isRecording, value))
            {
                OnPropertyChanged(nameof(HintText));
            }
        }
    }

    private string _issueText = string.Empty;
    [JsonIgnore]
    public string IssueText
    {
        get => _issueText;
        set
        {
            if (SetProperty(ref _issueText, value))
            {
                OnPropertyChanged(nameof(IssueVisibility));
            }
        }
    }

    [JsonIgnore]
    public Visibility IssueVisibility => string.IsNullOrEmpty(IssueText) ? Visibility.Collapsed : Visibility.Visible;

    [JsonIgnore]
    public string HintText => IsRecording ? "录制中..." : "点击后按下组合键";

    [JsonIgnore]
    public string FormattedKey => FormatKeyForDisplay(Key);

    // 兼容旧版序列化字段
    public string GlobalKeyGesture
    {
        get => Key;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(_key))
            {
                Key = value;
            }
        }
    }

    public string AppKeyGesture { get; set; } = string.Empty;

    public static string FormatKeyForDisplay(string rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey)) return "未设置";
        var parts = rawKey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var formatted = parts.Select(p => p.ToLowerInvariant() switch
        {
            "commandorcontrol" or "cmdorctrl" or "control" or "ctrl" => "Ctrl",
            "alt" or "menu" => "Alt",
            "shift" => "Shift",
            "plus" => "+",
            "minus" => "-",
            "left" or "arrowleft" => "Left",
            "right" or "arrowright" => "Right",
            "up" or "arrowup" => "Up",
            "down" or "arrowdown" => "Down",
            _ => p
        });
        return string.Join(" + ", formatted);
    }

    public ShortcutItem Clone() => new()
    {
        ActionId = ActionId,
        GroupKey = GroupKey,
        DisplayName = DisplayName,
        Description = Description,
        Key = Key,
        Enabled = Enabled,
        Scope = Scope
    };
}

/// <summary>
/// 下载任务项（支持实时进度绑定、暂停/恢复/取消状态控制，对应原 DownloadPage.vue / store/modules/download.ts）
/// </summary>
public partial class DownloadTaskItem : ObservableObject
{
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N");
    public SongItem Song { get; set; } = new();
    public string FilePath { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public string? DirectSourceUrl { get; set; }
    public string? DirectExtensionHint { get; set; }
    public string? SelectedQualityBadge { get; set; }

    [JsonIgnore]
    public CancellationTokenSource? Cts { get; set; }

    private string _filename = string.Empty;
    public string Filename
    {
        get => string.IsNullOrWhiteSpace(_filename) ? Song.Name : _filename;
        set => SetProperty(ref _filename, value);
    }

    private string _state = "queued"; // queued | downloading | paused | completed | error | cancelled
    public string State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(PauseVisibility));
                OnPropertyChanged(nameof(ResumeVisibility));
                OnPropertyChanged(nameof(CancelVisibility));
            }
        }
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        set
        {
            if (SetProperty(ref _progress, value))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    private string _status = "等待下载";
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    private long _totalBytes;
    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (SetProperty(ref _totalBytes, value))
            {
                OnPropertyChanged(nameof(SizeProgressText));
            }
        }
    }

    private long _downloadedBytes;
    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set
        {
            if (SetProperty(ref _downloadedBytes, value))
            {
                OnPropertyChanged(nameof(SizeProgressText));
            }
        }
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public string ProgressText => $"{Progress:F1}%";

    public string SizeProgressText => $"{FormatBytes(DownloadedBytes)} / {FormatBytes(TotalBytes)}";

    public Visibility PauseVisibility => State == "downloading" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ResumeVisibility => State == "paused" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CancelVisibility => State is "queued" or "downloading" or "paused" ? Visibility.Visible : Visibility.Collapsed;

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] sizes = ["B", "KB", "MB", "GB"];
        int order = 0;
        double len = bytes;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:F1} {sizes[order]}";
    }
}

/// <summary>
/// 全局应用设置（对齐原 set.json 及扩展配置）
/// </summary>
public class AppSettings
{
    public string Language { get; set; } = "zh-CN";
    public bool AutoTheme { get; set; } = false;
    public string ManualTheme { get; set; } = "Dark"; // Light | Dark
    public string LyricTranslationEngine { get; set; } = "none"; // none | opencc
    public string FontScope { get; set; } = "global"; // global | lyric
    public bool NoAnimate { get; set; } = false;
    public double AnimationSpeed { get; set; } = 1.0;
    public bool EnableGpuAcceleration { get; set; } = true;

    public string MusicQuality { get; set; } = MusicQualityLevel.Jymaster;
    public int AudioOutputDeviceNumber { get; set; } = -1;
    public string CloseAction { get; set; } = "minimize"; // ask | minimize | close
    public bool AlwaysShowDownloadButton { get; set; } = true;
    public bool UnlimitedDownload { get; set; } = false;
    public bool AutoPlay { get; set; } = false;
    public float Volume { get; set; } = 0.8f;
    public float PlaybackRate { get; set; } = 1.0f;
    public PlayMode PlayMode { get; set; } = PlayMode.LoopAll;
    public bool EnableAudioFade { get; set; } = false;
    public double AudioFadeDurationSeconds { get; set; } = 1.0;

    // 上次播放歌曲与进度记忆（退出重进后精准恢复当前歌曲、播放进度与选中文件夹）
    public string LastPlayedSongId { get; set; } = string.Empty;
    public string LastPlayedSongPath { get; set; } = string.Empty;
    public double LastPlayedPositionSeconds { get; set; } = 0;
    public SongItem? LastPlayedSong { get; set; }
    public string LastSelectedLocalFolderKey { get; set; } = string.Empty;

    // 网易云与 QQ 音乐双平台连接配置
    public bool IsMenuExpanded { get; set; } = true;
    public string LoginType { get; set; } = "cookie"; // qr | phone | cookie | uid
    public long SavedUid { get; set; } = 0;
    public int MusicApiPort { get; set; } = 30488;
    public bool UseCustomApiServer { get; set; } = false;
    public string CustomApiBaseUrl { get; set; } = "http://127.0.0.1:30488";
    public string NeteaseCookie { get; set; } = string.Empty;
    public string QqMusicCookie { get; set; } = string.Empty;
    public string QqMusicUin { get; set; } = string.Empty;
    public string QqMusicNickname { get; set; } = string.Empty;
    public bool EnableRealIP { get; set; } = false;
    public string RealIP { get; set; } = "116.25.146.177";
    public ProxyConfig Proxy { get; set; } = new();

    // 音源解灰与落雪/自定义插件配置
    public bool EnableMusicUnblock { get; set; } = true;
    public List<string> EnabledMusicSources { get; set; } = ["migu", "kugou", "kuwo", "pyncmd", "gdmusic"];
    public string CustomUnblockApiUrl { get; set; } = string.Empty;
    public string CustomApiPluginName { get; set; } = string.Empty;
    public List<LxScriptItem> LxMusicScripts { get; set; } = [];
    public string ActiveLxMusicApiId { get; set; } = string.Empty;

    // 下载、本地音乐文件夹树与缓存配置
    public string DownloadPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "AlgerMusicPlayer");
    public string DownloadNameFormat { get; set; } = "{songName} - {artistName}";
    public string DownloadSeparator { get; set; } = " - ";
    public bool DownloadSaveLyricFile { get; set; } = false;
    public int DownloadMaxConcurrent { get; set; } = 3;
    public HashSet<string> ClearedCompletedDownloadPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> LocalMusicDirectories { get; set; } =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
    ];
    public string LocalMusicViewMode { get; set; } = "folder"; // folder | list
    public HashSet<string> RemovedLocalSongPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DislikedSongIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool EmbedCoverAndLyricOnDownload { get; set; } = true;
    public bool EnableDiskCache { get; set; } = true;
    public string DiskCacheDir { get; set; } = string.Empty;
    public int DiskCacheMaxSizeMB { get; set; } = 4096;
    public string DiskCacheCleanupPolicy { get; set; } = "lru"; // lru | fifo

    // 局域网遥控配置
    public bool EnableRemoteControl { get; set; } = false;
    public int RemoteControlPort { get; set; } = 31888;
    public List<string> RemoteControlAllowedIps { get; set; } = [];

    // 窗口与字体
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public bool AlwaysOnTop { get; set; } = false;
    public int WindowWidth { get; set; } = 1560;
    public int WindowHeight { get; set; } = 920;
    public bool IsWindowMaximized { get; set; } = false;

    // 子模块配置
    public ThemeCenterConfig ThemeCenter { get; set; } = new();
    public EqualizerConfig Equalizer { get; set; } = new();
    public MultichannelConfig Multichannel { get; set; } = new();
    public DesktopLyricConfig DesktopLyric { get; set; } = new();
    public ImmersiveLyricConfig ImmersiveLyric { get; set; } = new();
    public List<ShortcutItem> Shortcuts { get; set; } = GetDefaultShortcuts();

    public static List<ShortcutItem> GetDefaultShortcuts() =>
    [
        new() { ActionId = "togglePlay", GroupKey = "playback", DisplayName = "播放/暂停", Description = "切换当前歌曲播放状态", Key = "Ctrl+Alt+P", Scope = "global", Enabled = true },
        new() { ActionId = "prevPlay", GroupKey = "playback", DisplayName = "上一首", Description = "切换到上一首歌曲", Key = "Ctrl+Alt+Left", Scope = "global", Enabled = true },
        new() { ActionId = "nextPlay", GroupKey = "playback", DisplayName = "下一首", Description = "切换到下一首歌曲", Key = "Ctrl+Alt+Right", Scope = "global", Enabled = true },
        new() { ActionId = "volumeUp", GroupKey = "sound", DisplayName = "音量增加", Description = "提高播放器音量", Key = "Ctrl+Alt+Up", Scope = "app", Enabled = true },
        new() { ActionId = "volumeDown", GroupKey = "sound", DisplayName = "音量减少", Description = "降低播放器音量", Key = "Ctrl+Alt+Down", Scope = "app", Enabled = true },
        new() { ActionId = "toggleFavorite", GroupKey = "sound", DisplayName = "收藏/取消收藏", Description = "收藏或取消当前歌曲", Key = "Ctrl+Alt+L", Scope = "app", Enabled = true },
        new() { ActionId = "toggleWindow", GroupKey = "window", DisplayName = "显示/隐藏窗口", Description = "快速显示或隐藏主窗口", Key = "Ctrl+Alt+Shift+M", Scope = "global", Enabled = true }
    ];

    public static List<ShortcutItem> NormalizeShortcuts(List<ShortcutItem>? rawList)
    {
        var defaults = GetDefaultShortcuts();
        if (rawList == null || rawList.Count == 0) return defaults;

        var result = new List<ShortcutItem>();
        foreach (var def in defaults)
        {
            var match = rawList.FirstOrDefault(x =>
                string.Equals(x.ActionId, def.ActionId, StringComparison.OrdinalIgnoreCase) ||
                (def.ActionId == "togglePlay" && string.Equals(x.ActionId, "PlayPause", StringComparison.OrdinalIgnoreCase)) ||
                (def.ActionId == "prevPlay" && string.Equals(x.ActionId, "PrevSong", StringComparison.OrdinalIgnoreCase)) ||
                (def.ActionId == "nextPlay" && string.Equals(x.ActionId, "NextSong", StringComparison.OrdinalIgnoreCase)) ||
                (def.ActionId == "toggleFavorite" && string.Equals(x.ActionId, "LikeSong", StringComparison.OrdinalIgnoreCase)) ||
                (def.ActionId == "toggleWindow" && string.Equals(x.ActionId, "ToggleMiniMode", StringComparison.OrdinalIgnoreCase)));

            var item = def.Clone();
            if (match != null)
            {
                if (!string.IsNullOrWhiteSpace(match.Key))
                {
                    item.Key = match.Key;
                }
                item.Enabled = match.Enabled;
                if (match.Scope is "global" or "app")
                {
                    item.Scope = match.Scope;
                }
            }
            result.Add(item);
        }
        return result;
    }
}
