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

        return GetShortBadgeName(fallbackUserLevel);
    }
}

/// <summary>
/// 歌手信息模型
/// </summary>
public class ArtistItem
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string PicUrl { get; set; } = string.Empty;
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
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string PicUrl { get; set; } = string.Empty;
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
    public string PicUrl { get; set; } = string.Empty;
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

    public string CoverThumbnailUrl => string.IsNullOrWhiteSpace(PicUrl)
        ? "ms-appx:///Assets/DefaultCover.png"
        : (PicUrl.StartsWith("http") && !PicUrl.Contains("?param=") ? $"{PicUrl}?param=200y200" : PicUrl);
}

/// <summary>
/// 歌单模型
/// </summary>
public class PlaylistItem
{
    public long Id { get; set; }
    public string Source { get; set; } = "netease";
    public string QqDissId { get; set; } = string.Empty;
    public int QqDirId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CoverImgUrl { get; set; } = string.Empty;
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

    public string CoverThumbnailUrl => string.IsNullOrWhiteSpace(Cover)
        ? "ms-appx:///Assets/DefaultCover.png"
        : (Cover.StartsWith("http") && !Cover.Contains("?param=") ? $"{Cover}?param=400y225" : Cover);

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
    public string Pic { get; set; } = string.Empty;
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
    public string PicUrl { get; set; } = string.Empty;
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
    public string CoverUrl { get; set; } = string.Empty;
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
/// 落雪音源 (LX Music) 脚本配置项（对应原 MusicSourceSettings.vue）
/// </summary>
public class LxScriptItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ScriptContent { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;

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

    public string DisplayAvatarUrl => !string.IsNullOrWhiteSpace(AvatarUrl)
        ? AvatarUrl
        : (!string.IsNullOrWhiteSpace(Uin) ? $"https://qlogo4.store.qq.com/qzone/{Uin}/{Uin}/100" : "ms-appx:///Assets/DefaultCover.png");
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

    public string CoverThumbnailUrl => string.IsNullOrWhiteSpace(PicUrl)
        ? "ms-appx:///Assets/DefaultCover.png"
        : (PicUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !PicUrl.Contains("param=") ? $"{PicUrl}?param=120y120" : PicUrl);

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
        ? "ms-appx:///Assets/DefaultCover.png"
        : $"https://q.qlogo.cn/headimg_dl?dst_uin={Uin}&spec=100";

    public string DisplayNickname => string.IsNullOrWhiteSpace(Nickname) ? $"QQ用户 {Uin}" : Nickname;
    public string DisplayUin => $"QQ: {Uin}";
}
