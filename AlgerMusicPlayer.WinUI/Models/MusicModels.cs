using CommunityToolkit.Mvvm.ComponentModel;

namespace AlgerMusicPlayer.WinUI.Models;

/// <summary>
/// 音乐音源平台枚举
/// </summary>
public enum MusicPlatform
{
    Netease,
    Migu,
    Kugou,
    Kuwo,
    Pyncmd,
    Bilibili,
    Custom,
    Local
}

/// <summary>
/// 播放模式枚举
/// </summary>
public enum PlayMode
{
    Sequence,
    LoopAll,
    LoopOne,
    Random,
    Intelligence
}

/// <summary>
/// 音质等级枚举
/// </summary>
public static class MusicQualityLevel
{
    public const string Standard = "standard";
    public const string Higher = "higher";
    public const string Exhigh = "exhigh";
    public const string Lossless = "lossless";
    public const string Hires = "hires";
    public const string Jyeffect = "jyeffect";
    public const string Sky = "sky";
    public const string Jymaster = "jymaster";

    public static readonly string[] AllOrderedLevels =
        [Standard, Higher, Exhigh, Lossless, Hires, Jyeffect, Sky, Jymaster];

    public static int GetQualityRank(string? level) => (level ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        Standard => 1,
        Higher => 2,
        Exhigh => 3,
        Lossless => 4,
        Hires => 5,
        Jyeffect => 6,
        Sky => 7,
        Jymaster => 8,
        _ => 3
    };

    public static string FormatFileSizeShort(long bytes) => bytes switch
    {
        <= 0 => string.Empty,
        >= 1024L * 1024L => $"{bytes / 1048576.0:F1} MB",
        _ => $"{Math.Max(1.0, bytes / 1024.0):F0} KB"
    };

    /// <summary>
    /// 按接口返回等级或实际流地址/码率/体积推断真实落地的音质档位
    /// </summary>
    public static string InferActualQualityLevel(string? actualLevel, string? url, int kbps, long sizeBytes = 0)
    {
        var normalized = (actualLevel ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is Standard or Higher or Exhigh or Lossless or Hires or Jyeffect or Sky or Jymaster)
        {
            return normalized;
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            if (url.Contains("RS01", StringComparison.OrdinalIgnoreCase))
            {
                return Jymaster;
            }
            if (url.Contains("Q000", StringComparison.OrdinalIgnoreCase))
            {
                return Hires;
            }
        }

        if (kbps >= 1350 || sizeBytes > 42L * 1024 * 1024)
        {
            return Hires;
        }

        bool isFlacUrl = !string.IsNullOrWhiteSpace(url) &&
                         (url.Contains(".flac", StringComparison.OrdinalIgnoreCase) ||
                          url.Contains("F000", StringComparison.OrdinalIgnoreCase));
        if (isFlacUrl || kbps > 400)
        {
            return Lossless;
        }
        if (kbps >= 240 || (!string.IsNullOrWhiteSpace(url) && url.Contains("M800", StringComparison.OrdinalIgnoreCase)))
        {
            return Exhigh;
        }
        if (kbps >= 160)
        {
            return Higher;
        }
        if (kbps > 0 ||
            (!string.IsNullOrWhiteSpace(url) &&
             (url.Contains("C400", StringComparison.OrdinalIgnoreCase) ||
              url.Contains("M500", StringComparison.OrdinalIgnoreCase))))
        {
            return Standard;
        }
        return Exhigh;
    }

    /// <summary>
    /// 判断是否为解析/切换过程中的临时过渡徽章
    /// </summary>
    public static bool IsTransientBadge(string? badge)
    {
        var text = (badge ?? string.Empty).Trim();
        return text is "解析中..." or "切换中..." or "换源中...";
    }

    /// <summary>
    /// 从已有徽章文本反推标准音质档位，供一致性校验使用
    /// </summary>
    public static string? InferLevelFromBadgeText(string? badge)
    {
        if (string.IsNullOrWhiteSpace(badge) || IsTransientBadge(badge))
        {
            return null;
        }

        if (badge.Contains("试听", StringComparison.OrdinalIgnoreCase) ||
            badge.Contains("Preview", StringComparison.OrdinalIgnoreCase) ||
            badge.Contains("本地", StringComparison.OrdinalIgnoreCase) ||
            badge.Contains("B站", StringComparison.OrdinalIgnoreCase) ||
            badge.Contains("汽水", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (badge.Contains("超清母带", StringComparison.OrdinalIgnoreCase)) return Jymaster;
        if (badge.Contains("Hi-Res", StringComparison.OrdinalIgnoreCase)) return Hires;
        if (badge.Contains("沉浸环绕", StringComparison.OrdinalIgnoreCase)) return Sky;
        if (badge.Contains("高清臻音", StringComparison.OrdinalIgnoreCase)) return Jyeffect;
        if (badge.Contains("无损", StringComparison.OrdinalIgnoreCase) ||
            badge.Contains("FLAC", StringComparison.OrdinalIgnoreCase)) return Lossless;
        if (badge.Contains("极高", StringComparison.OrdinalIgnoreCase)) return Exhigh;
        if (badge.Contains("较高", StringComparison.OrdinalIgnoreCase)) return Higher;
        if (badge.Contains("标准", StringComparison.OrdinalIgnoreCase)) return Standard;
        return null;
    }

    public static string GetDisplayName(string level) => level switch
    {
        Standard => "标准 (128k)",
        Higher => "较高 (192k)",
        Exhigh => "极高 (320k)",
        Lossless => "无损 (FLAC)",
        Hires => "Hi-Res",
        Jyeffect => "高清臻音",
        Sky => "沉浸环绕声",
        Jymaster => "超清母带",
        _ => "极高 (320k)"
    };

    public static string GetShortBadgeName(string? level) => (level ?? string.Empty).ToLowerInvariant() switch
    {
        Standard => "标准 128k",
        Higher => "较高 192k",
        Exhigh => "极高 320k",
        Lossless => "无损 FLAC",
        Hires => "Hi-Res",
        Jyeffect => "高清臻音",
        Sky => "沉浸环绕",
        Jymaster => "超清母带",
        _ => "无损 FLAC"
    };

    public static string FormatActualQualityBadge(string? actualLevel, string? encodeType, int bitrateBps, string? fallbackUserLevel)
    {
        var lvl = (actualLevel ?? string.Empty).Trim().ToLowerInvariant();
        var ext = (encodeType ?? string.Empty).Trim().TrimStart('.').ToUpperInvariant();
        int kbps = bitrateBps >= 1000 ? bitrateBps / 1000 : bitrateBps;

        if (!string.IsNullOrWhiteSpace(lvl))
        {
            var baseName = lvl switch
            {
                Standard => "标准",
                Higher => "较高",
                Exhigh => "极高",
                Lossless => "无损 FLAC",
                Hires => "Hi-Res",
                Jyeffect => "高清臻音",
                Sky => "沉浸环绕",
                Jymaster => "超清母带",
                _ => GetShortBadgeName(fallbackUserLevel)
            };

            if (kbps > 0 && lvl is Standard or Higher or Exhigh)
            {
                return $"{baseName} {kbps}k";
            }
            if (kbps > 320 && !baseName.Contains("FLAC", StringComparison.OrdinalIgnoreCase))
            {
                return $"{baseName} · {kbps}k";
            }
            return baseName;
        }

        if (ext == "FLAC" || kbps > 350)
        {
            return kbps > 1000 ? $"Hi-Res {kbps}k" : "无损 FLAC";
        }
        if (kbps >= 256)
        {
            return $"极高 {kbps}k";
        }
        if (kbps >= 160)
        {
            return $"较高 {kbps}k";
        }
        if (kbps > 0)
        {
            return $"标准 {kbps}k";
        }

        return GetShortBadgeName(Exhigh);
    }
}

/// <summary>
/// 当前歌曲在当前音源下可用的单个音质选项
/// </summary>
public sealed class SongQualityOption
{
    public string Level { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public bool IsCurrent { get; set; }
    public bool IsSwitchable { get; set; } = true;

    public string MenuDisplayText => string.IsNullOrWhiteSpace(Detail) ? Label : $"{Label}  ({Detail})";
}

/// <summary>
/// 歌手信息模型
/// </summary>
/// <summary>
/// 统一图片地址校验与安全兜底工具，防止空串或目录型 URI 触发 WinUI Image 解码 E_INVALIDARG 崩溃
/// </summary>
internal static class ImageUriHelper
{
    public const string DefaultCoverUri = "ms-appx:///Assets/icon.png";

    public static bool HasRealImageUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var trimmed = raw.Trim();
        if (trimmed.Contains("DefaultCover.png", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals(DefaultCoverUri, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(Normalize(trimmed, string.Empty));
    }

    public static string SanitizeOrEmpty(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        return Normalize(raw, string.Empty);
    }

    public static string Normalize(string? raw, string fallback = DefaultCoverUri)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        var val = raw.Trim();
        if (val.Contains("DefaultCover.png", StringComparison.OrdinalIgnoreCase))
            return fallback;

        if (val.StartsWith("//", StringComparison.Ordinal))
            val = "https:" + val;

        if (!Uri.TryCreate(val, UriKind.Absolute, out var uri))
            return fallback;

        if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(uri.Host))
                return fallback;

            var path = uri.AbsolutePath.Trim('/');
            // 拒绝无实际文件对象的裸路径（如汽水音乐 /img/ 目录前缀）
            if (string.IsNullOrWhiteSpace(path) ||
                uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal) ||
                path.Equals("img", StringComparison.OrdinalIgnoreCase))
            {
                return fallback;
            }

            return uri.AbsoluteUri;
        }

        if (uri.Scheme.Equals("ms-appx", StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals("ms-appdata", StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            return val;
        }

        return fallback;
    }
}

public class ArtistItem
{
    private string _picUrl = string.Empty;

    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string PicUrl
    {
        get => _picUrl;
        set => _picUrl = ImageUriHelper.SanitizeOrEmpty(value);
    }
    public string SafePicUrl => ImageUriHelper.Normalize(_picUrl);
    public int MusicSize { get; set; }
    public int AlbumSize { get; set; }
    public int MvSize { get; set; }
    public string BriefDesc { get; set; } = string.Empty;
    public List<string> Alias { get; set; } = [];
}

/// <summary>
/// 专辑信息模型
/// </summary>
public class AlbumItem
{
    private string _picUrl = string.Empty;

    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string PicUrl
    {
        get => _picUrl;
        set => _picUrl = ImageUriHelper.SanitizeOrEmpty(value);
    }
    public string SafePicUrl => ImageUriHelper.Normalize(_picUrl);
    public string ArtistName { get; set; } = string.Empty;
    public long PublishTime { get; set; }
    public int Size { get; set; }
    public string Description { get; set; } = string.Empty;
    public List<ArtistItem> Artists { get; set; } = [];

    public string PublishDateText => PublishTime > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(PublishTime).ToLocalTime().ToString("yyyy-MM-dd")
        : string.Empty;
}

/// <summary>
/// 歌曲信息模型（支持 UI 绑定状态）
/// </summary>
public partial class SongItem : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public long NumericId => long.TryParse(Id, out var id) ? id : 0;
    public string Name { get; set; } = string.Empty;
    private string _picUrl = string.Empty;

    public string PicUrl
    {
        get => _picUrl;
        set => _picUrl = ImageUriHelper.SanitizeOrEmpty(value);
    }
    public string SafePicUrl => ImageUriHelper.Normalize(_picUrl);
    public List<ArtistItem> Artists { get; set; } = [];
    public AlbumItem Album { get; set; } = new();
    public long DurationMs { get; set; }
    public long MvId { get; set; }
    public int Fee { get; set; }
    public List<string> Alias { get; set; } = [];
    public List<string> Translations { get; set; } = [];
    public string Source { get; set; } = "netease";
    public string? LocalFilePath { get; set; }
    public int? ChannelCount { get; set; }
    public long FileSize { get; set; }
    public long ModifiedTime { get; set; }
    public string? QqSongMid { get; set; }
    public long QqSongId { get; set; }
    public string? EmbeddedLyric { get; set; }
    public string? BilibiliBvid { get; set; }
    public long? BilibiliCid { get; set; }
    public long BilibiliAid { get; set; }

    // 第三方平台原生曲库元数据（供第三方音源专区直连与落雪脚本精准解析使用）
    public string? KugouHash { get; set; }
    public string? Kugou320Hash { get; set; }
    public string? KugouSqHash { get; set; }
    public string? KugouResHash { get; set; }
    public string? KugouAlbumId { get; set; }
    public long KugouCloudFileId { get; set; }
    public string? KuwoRid { get; set; }
    public string? MiguCopyrightId { get; set; }
    public string? MiguContentId { get; set; }
    public string? MiguLrcUrl { get; set; }
    public string? QishuiTrackId { get; set; }
    public string? NativeTrackId { get; set; }
    public string? NativePlayUrl { get; set; }
    public string? YtmSetVideoId { get; set; }
    public string? GdTrackId { get; set; }
    public string? GdSource { get; set; }
    public string? NativeQualityHint { get; set; }

    public Microsoft.UI.Xaml.Visibility NativeQualityBadgeVisibility =>
        string.IsNullOrWhiteSpace(NativeQualityHint)
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

    public string SourcePlatformLabel => (Source ?? string.Empty).ToLowerInvariant() switch
    {
        "qq" or "tx" => "QQ音乐",
        "qishui" => "汽水音乐",
        "kugou" or "kg" => "酷狗",
        "kuwo" or "kw" => "酷我",
        "migu" or "mg" => "咪咕",
        "spotify" => "Spotify",
        "apple" => "Apple Music",
        "deezer" => "Deezer",
        "tidal" => "Tidal",
        "qobuz" => "Qobuz",
        "ytmusic" => "YT Music",
        "bilibili" => "B站",
        "local" => "本地音乐",
        "local_custom" => "本地歌单",
        _ => "网易云"
    };

    public string ChannelText => ChannelCount switch
    {
        1 => "单声道",
        2 => "立体声",
        6 => "5.1",
        8 => "7.1",
        > 0 => $"{ChannelCount} 声道",
        _ => "-"
    };

    public string FileSizeText => FileSize switch
    {
        <= 0 => "-",
        >= 1024L * 1024L * 1024L => $"{FileSize / (1024.0 * 1024.0 * 1024.0):F2} GB",
        >= 1024L * 1024L => $"{FileSize / (1024.0 * 1024.0):F1} MB",
        _ => $"{Math.Max(1.0, FileSize / 1024.0):F0} KB"
    };

    private string? _playMusicUrl;
    public string? PlayMusicUrl
    {
        get => _playMusicUrl;
        set => SetProperty(ref _playMusicUrl, value);
    }

    private string? _resolvedPlatform;
    public string? ResolvedPlatform
    {
        get => _resolvedPlatform;
        set => SetProperty(ref _resolvedPlatform, value);
    }

    private string? _playingQualityBadge;
    public string? PlayingQualityBadge
    {
        get => _playingQualityBadge;
        set => SetProperty(ref _playingQualityBadge, value);
    }

    // 运行期实际输出的音质档位、码率（kbps）与文件体积，持久化供启动恢复与动态音质菜单校准使用
    public string? PlayingQualityLevel { get; set; }

    public int PlayingBitrateKbps { get; set; }

    public long PlayingSizeBytes { get; set; }

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayingIndicatorVisibility));
                OnPropertyChanged(nameof(NotPlayingVisibility));
                OnPropertyChanged(nameof(ActivePlayGlyph));
            }
        }
    }

    private bool _isAudioActivelyPlaying;
    public bool IsAudioActivelyPlaying
    {
        get => _isAudioActivelyPlaying;
        set
        {
            if (SetProperty(ref _isAudioActivelyPlaying, value))
            {
                OnPropertyChanged(nameof(ActivePlayGlyph));
            }
        }
    }

    public string ActivePlayGlyph => (_isPlaying && _isAudioActivelyPlaying) ? "\uE769" : "\uE768";

    public Microsoft.UI.Xaml.Visibility PlayingIndicatorVisibility => _isPlaying
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility NotPlayingVisibility => _isPlaying
        ? Microsoft.UI.Xaml.Visibility.Collapsed
        : Microsoft.UI.Xaml.Visibility.Visible;

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    private bool _isLiked;
    public bool IsLiked
    {
        get => _isLiked;
        set
        {
            if (SetProperty(ref _isLiked, value))
            {
                OnPropertyChanged(nameof(LikedVisibility));
                OnPropertyChanged(nameof(NotLikedVisibility));
            }
        }
    }

    public Microsoft.UI.Xaml.Visibility LikedVisibility => _isLiked
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility NotLikedVisibility => _isLiked
        ? Microsoft.UI.Xaml.Visibility.Collapsed
        : Microsoft.UI.Xaml.Visibility.Visible;

    private int _playCount;
    public int PlayCount
    {
        get => _playCount;
        set => SetProperty(ref _playCount, value);
    }

    private int _displayIndex = 1;
    public int DisplayIndex
    {
        get => _displayIndex;
        set => SetProperty(ref _displayIndex, value);
    }

    private bool _isBatchSelectable;
    public bool IsBatchSelectable
    {
        get => _isBatchSelectable;
        set
        {
            if (SetProperty(ref _isBatchSelectable, value))
            {
                OnPropertyChanged(nameof(BatchSelectVisibility));
            }
        }
    }

    public Microsoft.UI.Xaml.Visibility BatchSelectVisibility => _isBatchSelectable
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    private bool _isBatchSelected;
    public bool IsBatchSelected
    {
        get => _isBatchSelected;
        set => SetProperty(ref _isBatchSelected, value);
    }

    public bool IsLocalSong => string.Equals(Source, "local", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(LocalFilePath);

    public string ArtistText => Artists.Count > 0
        ? string.Join(" / ", Artists.Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)))
        : "未知歌手";

    public string AlbumText => !string.IsNullOrWhiteSpace(Album?.Name) ? Album.Name : "未知专辑";

    public string DurationText
    {
        get
        {
            var ts = TimeSpan.FromMilliseconds(DurationMs);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
    }

    public string SubtitleText => Translations.Count > 0
        ? Translations[0]
        : (Alias.Count > 0 ? Alias[0] : string.Empty);

    public string BracketSubtitleText => string.IsNullOrWhiteSpace(SubtitleText)
        ? string.Empty
        : $" ({SubtitleText})";

    public string CoverThumbnailUrl => BuildSongCoverThumbnailUrl(PicUrl);

    private static string BuildSongCoverThumbnailUrl(string? url)
    {
        var normalized = ImageUriHelper.Normalize(url, ImageUriHelper.DefaultCoverUri);
        if (!normalized.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return normalized;

        if (normalized.Contains("126.net", StringComparison.OrdinalIgnoreCase) && !normalized.Contains('?'))
            return $"{normalized}?param=120y120";
        if (normalized.Contains("gtimg.cn", StringComparison.OrdinalIgnoreCase))
            return normalized.Replace("R500x500", "R150x150", StringComparison.OrdinalIgnoreCase)
                             .Replace("R300x300", "R150x150", StringComparison.OrdinalIgnoreCase);
        if (normalized.Contains("kugou.com", StringComparison.OrdinalIgnoreCase))
            return normalized.Replace("/stdmusic/400/", "/stdmusic/150/", StringComparison.OrdinalIgnoreCase);
        if (normalized.Contains("kuwo.cn", StringComparison.OrdinalIgnoreCase))
            return normalized.Replace("/albumcover/500/", "/albumcover/120/", StringComparison.OrdinalIgnoreCase)
                             .Replace("/albumcover/300/", "/albumcover/120/", StringComparison.OrdinalIgnoreCase);
        if (normalized.Contains("douyinpic.com", StringComparison.OrdinalIgnoreCase))
            return normalized.Replace("resize:300:300", "resize:150:150", StringComparison.OrdinalIgnoreCase);
        if (normalized.Contains("mzstatic.com", StringComparison.OrdinalIgnoreCase))
            return normalized.Replace("600x600bb", "150x150bb", StringComparison.OrdinalIgnoreCase)
                             .Replace("300x300bb", "150x150bb", StringComparison.OrdinalIgnoreCase);
        if (normalized.Contains("dzcdn.net", StringComparison.OrdinalIgnoreCase))
            return normalized.Replace("/500x500-", "/250x250-", StringComparison.OrdinalIgnoreCase);

        return normalized;
    }

    public string HighResCoverUrl => BuildSongHighResCoverUrl(PicUrl);
    public string ShortPlatformTag => PlatformVisualHelper.GetShortLabel(Source);
    [System.Text.Json.Serialization.JsonIgnore]
    public Microsoft.UI.Xaml.Media.SolidColorBrush? SourcePlatformBrush => PlatformVisualHelper.GetPlatformBrush(Source);

    public string DiscoverQualityTag
    {
        get
        {
            var hint = (NativeQualityHint ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(hint))
            {
                return string.Empty;
            }
            if (hint.Contains("母带", StringComparison.OrdinalIgnoreCase) || hint.Contains("master", StringComparison.OrdinalIgnoreCase))
                return "母带";
            if (hint.Contains("hi-res", StringComparison.OrdinalIgnoreCase) || hint.Contains("hires", StringComparison.OrdinalIgnoreCase) || hint.Contains("hr", StringComparison.OrdinalIgnoreCase) || hint.Contains("臻音", StringComparison.OrdinalIgnoreCase))
                return "HI-RES";
            if (hint.Contains("无损", StringComparison.OrdinalIgnoreCase) || hint.Contains("flac", StringComparison.OrdinalIgnoreCase) || hint.Contains("sq", StringComparison.OrdinalIgnoreCase))
                return "SQ 无损";
            if (hint.Contains("320", StringComparison.OrdinalIgnoreCase) || hint.Contains("hq", StringComparison.OrdinalIgnoreCase) || hint.Contains("极高", StringComparison.OrdinalIgnoreCase))
                return "HQ 320k";
            if (hint.Contains("aac", StringComparison.OrdinalIgnoreCase))
                return hint.Contains("256") ? "AAC 256k" : "AAC";
            if (hint.Contains("m4a", StringComparison.OrdinalIgnoreCase))
                return "M4A 128k";
            if (hint.Contains("mp3", StringComparison.OrdinalIgnoreCase))
                return "MP3";
            if (hint.Contains("preview", StringComparison.OrdinalIgnoreCase) || hint.Contains("试听", StringComparison.OrdinalIgnoreCase))
                return "试听";
            return hint.Length <= 8 ? hint.ToUpperInvariant() : hint[..8];
        }
    }

    public Microsoft.UI.Xaml.Visibility DiscoverQualityBadgeVisibility =>
        string.IsNullOrEmpty(DiscoverQualityTag)
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

    private static string BuildSongHighResCoverUrl(string? url)
    {
        var normalized = ImageUriHelper.Normalize(url, ImageUriHelper.DefaultCoverUri);
        if (!normalized.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return normalized;

        if (normalized.Contains("{size}", StringComparison.OrdinalIgnoreCase))
            normalized = normalized.Replace("{size}", "400", StringComparison.OrdinalIgnoreCase);

        if (normalized.Contains("126.net", StringComparison.OrdinalIgnoreCase))
        {
            var qIdx = normalized.IndexOf('?');
            if (qIdx >= 0)
            {
                var baseUrl = normalized[..qIdx];
                return $"{baseUrl}?param=500y500";
            }
            return $"{normalized}?param=500y500";
        }
        if (normalized.Contains("gtimg.cn", StringComparison.OrdinalIgnoreCase))
            return normalized.Replace("R150x150", "R500x500", StringComparison.OrdinalIgnoreCase)
                             .Replace("R300x300", "R500x500", StringComparison.OrdinalIgnoreCase);
        if (normalized.Contains("kugou.com", StringComparison.OrdinalIgnoreCase))
            return normalized.Replace("/stdmusic/150/", "/stdmusic/400/", StringComparison.OrdinalIgnoreCase);
        if (normalized.Contains("kuwo.cn", StringComparison.OrdinalIgnoreCase))
            return normalized.Replace("/albumcover/120/", "/albumcover/500/", StringComparison.OrdinalIgnoreCase)
                             .Replace("/albumcover/300/", "/albumcover/500/", StringComparison.OrdinalIgnoreCase);
        if (normalized.Contains("douyinpic.com", StringComparison.OrdinalIgnoreCase))
            return normalized.Replace("resize:150:150", "resize:500:500", StringComparison.OrdinalIgnoreCase)
                             .Replace("resize:300:300", "resize:500:500", StringComparison.OrdinalIgnoreCase);
        if (normalized.Contains("mzstatic.com", StringComparison.OrdinalIgnoreCase))
            return normalized.Replace("150x150bb", "600x600bb", StringComparison.OrdinalIgnoreCase)
                             .Replace("300x300bb", "600x600bb", StringComparison.OrdinalIgnoreCase);
        if (normalized.Contains("dzcdn.net", StringComparison.OrdinalIgnoreCase))
            return normalized.Replace("/250x250-", "/500x500-", StringComparison.OrdinalIgnoreCase);

        return normalized;
    }
}

/// <summary>
/// 歌单模型
/// </summary>
public class PlaylistItem
{
    public long Id { get; set; }
    public string Source { get; set; } = "netease";
    public string QqDissId { get; set; } = string.Empty;
    public string ThirdPartyId { get; set; } = string.Empty;
    public int QqDirId { get; set; }
    public string Name { get; set; } = string.Empty;
    private string _coverImgUrl = string.Empty;

    public string CoverImgUrl
    {
        get => _coverImgUrl;
        set => _coverImgUrl = ImageUriHelper.SanitizeOrEmpty(value);
    }
    public string SafeCoverImgUrl => ImageUriHelper.Normalize(_coverImgUrl);
    public string Description { get; set; } = string.Empty;
    public long PlayCount { get; set; }
    public int TrackCount { get; set; }
    public long CreatorUserId { get; set; }
    public string CreatorName { get; set; } = string.Empty;
    public string CreatorAvatarUrl { get; set; } = string.Empty;
    public long CreateTime { get; set; }
    public long UpdateTime { get; set; }
    public string UpdateFrequency { get; set; } = string.Empty;
    public bool Subscribed { get; set; }
    public List<string> Tags { get; set; } = [];
    public List<SongItem> Tracks { get; set; } = [];

    public bool IsLocalCustom => string.Equals(Source, "local_custom", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 是否为平台系统默认歌单（如“我喜欢的音乐”“默认收藏夹”，禁止删除歌单本体）
    /// </summary>
    public bool IsSystemDefaultPlaylist { get; set; }

    public Microsoft.UI.Xaml.Visibility LocalCustomDeleteVisibility => IsLocalCustom
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    public string TrackCountBadgeText => $"{TrackCount} 首";

    public string PlayCountText => PlayCount switch
    {
        >= 100_000_000 => $"{PlayCount / 100_000_000.0:F1}亿",
        >= 10_000 => $"{PlayCount / 10_000.0:F1}万",
        _ => PlayCount.ToString()
    };
}

/// <summary>
/// 首页轮播图模型
/// </summary>
public class BannerItem
{
    public string ImageUrl { get; set; } = string.Empty;
    public string TypeTitle { get; set; } = string.Empty;
    public long TargetId { get; set; }
    public int TargetType { get; set; }
    public string? Url { get; set; }
}

/// <summary>
/// MV 模型
/// </summary>
public class MvItem
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Cover { get; set; } = string.Empty;
    public string ArtistName { get; set; } = string.Empty;
    public long PlayCount { get; set; }
    public long DurationMs { get; set; }
    public string BriefDesc { get; set; } = string.Empty;
    public string PublishTime { get; set; } = string.Empty;

    public string CoverThumbnailUrl
    {
        get
        {
            var normalized = ImageUriHelper.Normalize(Cover, ImageUriHelper.DefaultCoverUri);
            return normalized.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !normalized.Contains("?param=")
                ? $"{normalized}?param=400y225"
                : normalized;
        }
    }

    public string PlayCountText => PlayCount switch
    {
        >= 100_000_000 => $"{PlayCount / 100_000_000.0:F1}亿",
        >= 10_000 => $"{PlayCount / 10_000.0:F1}万",
        _ => PlayCount.ToString()
    };

    public string DurationText
    {
        get
        {
            if (DurationMs <= 0) return string.Empty;
            var ts = TimeSpan.FromMilliseconds(DurationMs);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
    }
}

/// <summary>
/// Bilibili 视频与音频模型
/// </summary>
public class BilibiliVideoItem
{
    public long Aid { get; set; }
    public string Bvid { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    private string _pic = string.Empty;

    public string Pic
    {
        get => _pic;
        set => _pic = ImageUriHelper.SanitizeOrEmpty(value);
    }
    public string SafePic => ImageUriHelper.Normalize(_pic);
    public string Description { get; set; } = string.Empty;
    public string Duration { get; set; } = string.Empty;
    public long ViewCount { get; set; }
    public List<BilibiliPageItem> Pages { get; set; } = [];
}

public class BilibiliPageItem
{
    public long Cid { get; set; }
    public int Page { get; set; }
    public string Part { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
}

/// <summary>
/// 评论模型
/// </summary>
public class CommentItem
{
    public long CommentId { get; set; }
    public string Content { get; set; } = string.Empty;
    public long Time { get; set; }
    public string TimeStr { get; set; } = string.Empty;
    public int LikedCount { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string UserAvatarUrl { get; set; } = string.Empty;
}

/// <summary>
/// 用户资料模型
/// </summary>
public class UserProfile
{
    public long UserId { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public string BackgroundUrl { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public int VipType { get; set; }
    public int Level { get; set; }
    public int ListenSongs { get; set; }
    public int PlaylistCount { get; set; }
    public int Follows { get; set; }
    public int Followeds { get; set; }
}

/// <summary>
/// 播客电台模型（对应原 views/podcast）
/// </summary>
public class RadioStationItem
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    private string _picUrl = string.Empty;

    public string PicUrl
    {
        get => _picUrl;
        set => _picUrl = ImageUriHelper.SanitizeOrEmpty(value);
    }
    public string SafePicUrl => ImageUriHelper.Normalize(_picUrl);
    public string DjName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string RcmdText { get; set; } = string.Empty;
    public long SubCount { get; set; }
    public int ProgramCount { get; set; }

    public string SubInfoText => $"{Category} · {ProgramCount} 期节目";
}

/// <summary>
/// 电台单期节目模型
/// </summary>
public class RadioProgramItem
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DjName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    private string _coverUrl = string.Empty;

    public string CoverUrl
    {
        get => _coverUrl;
        set => _coverUrl = ImageUriHelper.SanitizeOrEmpty(value);
    }
    public string SafeCoverUrl => ImageUriHelper.Normalize(_coverUrl);
    public long CreateTime { get; set; }
    public long DurationMs { get; set; }
    public long ListenerCount { get; set; }
    public SongItem MainSong { get; set; } = new();

    public string ListenerCountText => MetaText;

    public string MetaText
    {
        get
        {
            var date = CreateTime > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(CreateTime).ToLocalTime().ToString("yyyy-MM-dd")
                : "";
            var ts = TimeSpan.FromMilliseconds(DurationMs > 0 ? DurationMs : MainSong.DurationMs);
            return $"{date} · {(int)ts.TotalMinutes:D2}:{ts.Seconds:D2} · {ListenerCount} 次收听";
        }
    }
}

/// <summary>
/// 用户听歌排行项（对应原 views/user/index.vue recordList）
/// </summary>
public class PlayRecordItem
{
    public int Rank { get; set; }
    public int PlayCount { get; set; }
    public int Score { get; set; }
    public SongItem Song { get; set; } = new();

    public string RankText => $"#{Rank}";
    public string PlayCountText => $"{PlayCount} 次";
}

/// <summary>
/// 落雪音源 (LX Music) 脚本配置项（支持多脚本启用开关与首选标记）
/// </summary>
public partial class LxScriptItem : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ScriptContent { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                OnPropertyChanged(nameof(RowOpacity));
            }
        }
    }

    private bool _isPrimary;
    [global::System.Text.Json.Serialization.JsonIgnore]
    public bool IsPrimary
    {
        get => _isPrimary;
        set
        {
            if (SetProperty(ref _isPrimary, value))
            {
                OnPropertyChanged(nameof(PrimaryBadgeVisibility));
                OnPropertyChanged(nameof(SetPrimaryButtonVisibility));
            }
        }
    }

    public double RowOpacity => _enabled ? 1.0 : 0.55;
    public Microsoft.UI.Xaml.Visibility PrimaryBadgeVisibility =>
        _isPrimary ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility SetPrimaryButtonVisibility =>
        _isPrimary ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public string DisplayMeta => $"v{Version}" + (string.IsNullOrWhiteSpace(Author) ? "" : $" · {Author}");
}

/// <summary>
/// 下载音质与音源选项（供用户选择下载特定音源的特定规格）
/// </summary>
public class DownloadQualityOption
{
    public string Platform { get; set; } = string.Empty;
    public string PlatformName { get; set; } = string.Empty;
    public string QualityBadge { get; set; } = string.Empty;
    public string Extension { get; set; } = ".mp3";
    public int BitrateKbps { get; set; }
    public long SizeBytes { get; set; }
    public string SizeText { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public bool IsRecommended { get; set; }
    public string BrandColorHex { get; set; } = "#10B981";
    public int QualityRank { get; set; }
}

/// <summary>
/// 捐赠者名单项（对应原 DonationList.vue）
/// </summary>
public class DonorItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Amount { get; set; } = "0";
    public string Message { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;

    public string Initial => string.IsNullOrWhiteSpace(Name) ? "♥" : Name.Trim()[..1].ToUpperInvariant();
    public string AmountText => $"¥{Amount}";
    public string SubtitleText => string.IsNullOrWhiteSpace(Message) ? Date : $"\"{Message}\"";
}

/// <summary>
/// QQ 音乐用户资料模型（对应 .worktrees/local-music-folder-tree 新增 views/qq-music 与 gateway/qqServices.ts）
/// </summary>
public class QqUserProfile
{
    public string Uin { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;

    public string DisplayAvatarUrl => ImageUriHelper.HasRealImageUrl(AvatarUrl)
        ? ImageUriHelper.Normalize(AvatarUrl)
        : (!string.IsNullOrWhiteSpace(Uin) ? $"https://qlogo4.store.qq.com/qzone/{Uin}/{Uin}/100" : ImageUriHelper.DefaultCoverUri);
}

/// <summary>
/// 本地音乐文件夹树节点模型（对应 .worktrees/local-music-folder-tree 新增 utils/localMusicTree.ts &amp; FolderTreeNode.vue）
/// </summary>
public partial class LocalMusicFolderNode : ObservableObject
{
    public string Key { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Level { get; set; }
    public List<SongItem> Songs { get; set; } = [];
    public List<LocalMusicFolderNode> Children { get; set; } = [];

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ExpandGlyph));
                OnPropertyChanged(nameof(FolderGlyph));
                OnPropertyChanged(nameof(ExpandRotation));
            }
        }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public Microsoft.UI.Xaml.Thickness IndentMargin => new(Level * 18, 0, 0, 0);
    public bool HasChildren => Children.Count > 0;
    public Microsoft.UI.Xaml.Visibility ExpandIconVisibility => Children.Count > 0
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;
    public float ExpandRotation => IsExpanded ? 90f : 0f;
    public string ExpandGlyph => Children.Count == 0 ? string.Empty : (IsExpanded ? "\uE70D" : "\uE76C");
    public string FolderGlyph => IsExpanded && Children.Count > 0 ? "\uE8DA" : "\uE8B7";
    public string SongCountText => Songs.Count.ToString();
}

/// <summary>
/// 已下载完成的音乐文件记录项（对应 DownloadPage.vue downloaded Tab 与 downloadManager.ts getDownloadedMusic）
/// </summary>
public class CompletedDownloadItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ArtistText { get; set; } = "未知歌手";
    public string AlbumText { get; set; } = "未知专辑";
    public string PicUrl { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public double DurationSeconds { get; set; }
    public DateTime CreatedTime { get; set; } = DateTime.Now;

    public string CoverThumbnailUrl
    {
        get
        {
            var normalized = ImageUriHelper.Normalize(PicUrl, ImageUriHelper.DefaultCoverUri);
            return normalized.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !normalized.Contains("param=")
                ? $"{normalized}?param=120y120"
                : normalized;
        }
    }

    public string FileSizeText => DownloadTaskItem.FormatBytes(FileSize);

    public string ShortenedPath => ShortenPath(FilePath);

    public static string ShortenPath(string fullPath, int maxLength = 40)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return string.Empty;
        var normalized = fullPath.Replace('\\', '/');
        if (normalized.Length <= maxLength) return normalized;

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2) return normalized;

        var firstTwo = string.Join("/", parts.Take(2));
        var lastTwo = string.Join("/", parts.Skip(Math.Max(2, parts.Length - 2)));
        return $"{firstTwo}/.../{lastTwo}";
    }

    public SongItem ToSongItem()
    {
        return new SongItem
        {
            Id = Math.Abs(FilePath.GetHashCode()).ToString(),
            Name = string.IsNullOrWhiteSpace(DisplayName) ? Path.GetFileNameWithoutExtension(FilePath) : DisplayName,
            PicUrl = PicUrl,
            DurationMs = (long)(DurationSeconds * 1000),
            PlayMusicUrl = FilePath,
            LocalFilePath = FilePath,
            FileSize = FileSize,
            Source = "local",
            Album = new AlbumItem { Name = AlbumText, PicUrl = PicUrl },
            Artists = [new ArtistItem { Name = ArtistText }]
        };
    }
}

/// <summary>
/// 下载文件名格式排序组件项（对应 DownloadPage.vue formatComponents: songName / artistName / albumName）
/// </summary>
public class DownloadFormatComponentItem
{
    public string Type { get; set; } = "songName";
    public int Index { get; set; }
    public bool CanMoveUp { get; set; }
    public bool CanMoveDown { get; set; }
    public bool CanRemove { get; set; }

    public string DisplayName => Type switch
    {
        "songName" => "歌曲名",
        "artistName" => "歌手名",
        "albumName" => "专辑名",
        _ => Type
    };
}

/// <summary>
/// 本机已登录的 PC QQ / NTQQ 账号项（通过 localhost.ptlogin2.qq.com 本地快速登录服务检测）
/// </summary>
public partial class LocalQqAccountItem : ObservableObject
{
    public string Uin { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string Account { get; set; } = string.Empty;
    public int ClientType { get; set; }
    public string Scheme { get; set; } = "https";
    public int Port { get; set; } = 4301;
    public string PtLocalToken { get; set; } = string.Empty;
    public string PtLoginSig { get; set; } = string.Empty;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(SelectedBadgeVisibility));
                OnPropertyChanged(nameof(UnselectedBadgeVisibility));
            }
        }
    }

    public Microsoft.UI.Xaml.Visibility SelectedBadgeVisibility =>
        _isSelected ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility UnselectedBadgeVisibility =>
        _isSelected ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public string AvatarUrl => string.IsNullOrWhiteSpace(Uin)
        ? ImageUriHelper.DefaultCoverUri
        : $"https://q.qlogo.cn/headimg_dl?dst_uin={Uin}&spec=100";

    public string DisplayNickname => string.IsNullOrWhiteSpace(Nickname) ? $"QQ用户 {Uin}" : Nickname;
    public string DisplayUin => $"QQ: {Uin}";
}

/// <summary>
/// 跨平台视觉辅助工具（统一平台短徽章、筛选匹配与 UI 线程笔刷懒缓存）
/// </summary>
public static class PlatformVisualHelper
{
    private static readonly Dictionary<string, Microsoft.UI.Xaml.Media.SolidColorBrush> _brushCache = new(StringComparer.OrdinalIgnoreCase);

    public static string NormalizeKey(string? source)
    {
        var s = (source ?? string.Empty).Trim().ToLowerInvariant();
        return s switch
        {
            "wy" or "netease" or "music163" => "netease",
            "tx" or "qq" or "qqmusic" => "qq",
            "kg" or "kugou" => "kugou",
            "kw" or "kuwo" => "kuwo",
            "mg" or "migu" => "migu",
            "qishui" or "soda" => "qishui",
            "bili" or "bilibili" => "bilibili",
            "apple" or "applemusic" => "apple",
            "ytmusic" or "youtube" or "yt" => "ytmusic",
            _ => s
        };
    }

    public static bool MatchesFilter(string? source, string filterTag)
    {
        if (string.IsNullOrWhiteSpace(filterTag) || string.Equals(filterTag, "all", StringComparison.OrdinalIgnoreCase))
            return true;
        return string.Equals(NormalizeKey(source), NormalizeKey(filterTag), StringComparison.OrdinalIgnoreCase);
    }

    public static string GetShortLabel(string? source) => NormalizeKey(source) switch
    {
        "netease" => "网易云",
        "qq" => "QQ音乐",
        "qishui" => "汽水",
        "kugou" => "酷狗",
        "kuwo" => "酷我",
        "migu" => "咪咕",
        "bilibili" => "B站",
        "spotify" => "Spotify",
        "apple" => "Apple",
        "deezer" => "Deezer",
        "tidal" => "Tidal",
        "ytmusic" => "YTMusic",
        _ => "聚合"
    };

    public static Windows.UI.Color GetAccentColor(string? source) => NormalizeKey(source) switch
    {
        "netease" => Windows.UI.Color.FromArgb(255, 230, 0, 38),
        "qq" => Windows.UI.Color.FromArgb(255, 18, 183, 106),
        "qishui" => Windows.UI.Color.FromArgb(255, 0, 196, 154),
        "kugou" => Windows.UI.Color.FromArgb(255, 16, 142, 255),
        "kuwo" => Windows.UI.Color.FromArgb(255, 245, 158, 11),
        "migu" => Windows.UI.Color.FromArgb(255, 232, 23, 107),
        "bilibili" => Windows.UI.Color.FromArgb(255, 251, 114, 153),
        "spotify" => Windows.UI.Color.FromArgb(255, 29, 185, 84),
        "apple" => Windows.UI.Color.FromArgb(255, 250, 35, 59),
        "deezer" => Windows.UI.Color.FromArgb(255, 155, 77, 255),
        "tidal" => Windows.UI.Color.FromArgb(255, 59, 130, 246),
        "ytmusic" => Windows.UI.Color.FromArgb(255, 255, 0, 51),
        _ => Windows.UI.Color.FromArgb(255, 16, 185, 129)
    };

    /// <summary>
    /// 获取平台彩色品牌图标路径（优先返回本地磁盘路径，兼容非打包模式）
    /// </summary>
    public static string GetLogoUri(string? source)
    {
        var fileName = NormalizeKey(source) switch
        {
            "netease" => "netease.png",
            "qq" => "qqmusic.png",
            "qishui" => "qishui.png",
            "kugou" => "kugou.png",
            "kuwo" => "kuwo.png",
            "migu" => "migu.png",
            "bilibili" => "bilibili.png",
            "spotify" => "spotify.png",
            "apple" => "applemusic.png",
            "deezer" => "deezer.png",
            "tidal" => "tidal.png",
            "ytmusic" => "ytmusic.png",
            _ => "netease.png"
        };

        var localPath = global::System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Platforms", fileName);
        return global::System.IO.File.Exists(localPath)
            ? localPath
            : $"ms-appx:///Assets/Platforms/{fileName}";
    }

    /// <summary>
    /// 仅在 UI 线程延迟构造与缓存 SolidColorBrush，严禁在后台线程实例化 WinUI DependencyObject
    /// </summary>
    public static Microsoft.UI.Xaml.Media.SolidColorBrush? GetPlatformBrush(string? source)
    {
        if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() == null)
            return null;

        var key = NormalizeKey(source);
        if (string.IsNullOrEmpty(key)) key = "default";
        if (_brushCache.TryGetValue(key, out var cached))
            return cached;

        var brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(GetAccentColor(key));
        _brushCache[key] = brush;
        return brush;
    }
}

/// <summary>
/// 综合首页顶部三联焦点画报卡片模型
/// </summary>
public partial class DiscoverBannerItem : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string BadgeText { get; set; } = string.Empty;
    public string PlatformKey { get; set; } = "netease";
    public string Headline { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;

    private string _coverUrl = ImageUriHelper.DefaultCoverUri;
    public string CoverUrl
    {
        get => _coverUrl;
        set => SetProperty(ref _coverUrl, ImageUriHelper.Normalize(value));
    }

    public List<SongItem> Songs { get; } = [];
    public SongItem? LeadSong => Songs.Count > 0 ? Songs[0] : null;
    [System.Text.Json.Serialization.JsonIgnore]
    public Microsoft.UI.Xaml.Media.SolidColorBrush? PlatformBrush => PlatformVisualHelper.GetPlatformBrush(PlatformKey);
}

/// <summary>
/// 综合首页跨平台精选歌单卡片模型（支持 3 首曲目预览渐进式水合）
/// </summary>
public partial class DiscoverPlaylistCardItem : ObservableObject
{
    public PlaylistItem Playlist { get; set; } = new();

    public string PlatformKey => PlatformVisualHelper.NormalizeKey(Playlist.Source);
    public string ShortPlatformTag => PlatformVisualHelper.GetShortLabel(Playlist.Source);
    [System.Text.Json.Serialization.JsonIgnore]
    public Microsoft.UI.Xaml.Media.SolidColorBrush? PlatformBrush => PlatformVisualHelper.GetPlatformBrush(Playlist.Source);
    public string CoverUrl => ImageUriHelper.Normalize(Playlist.SafeCoverImgUrl);
    public string Title => string.IsNullOrWhiteSpace(Playlist.Name) ? "精选歌单" : Playlist.Name;
    public string MetaText => !string.IsNullOrWhiteSpace(Playlist.CreatorName)
        ? $"{Playlist.CreatorName} · {Playlist.PlayCountText}"
        : Playlist.PlayCountText;

    private string _previewLine1 = "1. 精选曲目准备中...";
    public string PreviewLine1
    {
        get => _previewLine1;
        set => SetProperty(ref _previewLine1, value);
    }

    private string _previewLine2 = string.Empty;
    public string PreviewLine2
    {
        get => _previewLine2;
        set
        {
            if (SetProperty(ref _previewLine2, value))
                OnPropertyChanged(nameof(PreviewLine2Visibility));
        }
    }

    private string _previewLine3 = string.Empty;
    public string PreviewLine3
    {
        get => _previewLine3;
        set
        {
            if (SetProperty(ref _previewLine3, value))
                OnPropertyChanged(nameof(PreviewLine3Visibility));
        }
    }

    public Microsoft.UI.Xaml.Visibility PreviewLine2Visibility =>
        string.IsNullOrWhiteSpace(_previewLine2)
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

    public Microsoft.UI.Xaml.Visibility PreviewLine3Visibility =>
        string.IsNullOrWhiteSpace(_previewLine3)
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;
}
