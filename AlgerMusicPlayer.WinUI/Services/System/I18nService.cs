namespace AlgerMusicPlayer.WinUI.Services.System;

/// <summary>
/// 多语言国际化资源管理服务（支持 zh-CN 与 en-US）
/// </summary>
public class I18nService
{
    private readonly ConfigService _configService;

    private static readonly Dictionary<string, Dictionary<string, string>> Translations = new()
    {
        ["zh-CN"] = new()
        {
            ["AppTitle"] = "Alger Music Player",
            ["NavHome"] = "为我推荐",
            ["NavToplist"] = "排行榜",
            ["NavPlaylists"] = "歌单广场",
            ["NavMv"] = "MV 视频",
            ["NavBilibili"] = "B站音乐",
            ["NavHistoryFavorite"] = "历史与收藏",
            ["NavLocalMusic"] = "本地音乐",
            ["NavDownloads"] = "下载管理",
            ["NavUser"] = "个人中心",
            ["NavSettings"] = "系统设置",
            ["SearchPlaceholder"] = "搜索单曲、歌手、专辑、歌单或 B站 BV号...",
            ["DailyRecommend"] = "每日推荐",
            ["RecommendPlaylists"] = "推荐歌单",
            ["HotArtists"] = "热门歌手",
            ["NewSongs"] = "推荐新音乐",
            ["NewAlbums"] = "最新上架专辑",
            ["PlayAll"] = "播放全部",
            ["DesktopLyric"] = "桌面歌词",
            ["Equalizer"] = "均衡器 (EQ)"
        },
        ["en-US"] = new()
        {
            ["AppTitle"] = "Alger Music Player",
            ["NavHome"] = "For You",
            ["NavToplist"] = "Top Charts",
            ["NavPlaylists"] = "Playlists",
            ["NavMv"] = "Music Videos",
            ["NavBilibili"] = "Bilibili Audio",
            ["NavHistoryFavorite"] = "History & Liked",
            ["NavLocalMusic"] = "Local Music",
            ["NavDownloads"] = "Downloads",
            ["NavUser"] = "My Profile",
            ["NavSettings"] = "Settings",
            ["SearchPlaceholder"] = "Search songs, artists, albums, playlists or BVID...",
            ["DailyRecommend"] = "Daily Recommend",
            ["RecommendPlaylists"] = "Recommended Playlists",
            ["HotArtists"] = "Hot Artists",
            ["NewSongs"] = "New Releases",
            ["NewAlbums"] = "New Albums",
            ["PlayAll"] = "Play All",
            ["DesktopLyric"] = "Desktop Lyric",
            ["Equalizer"] = "Equalizer (EQ)"
        }
    };

    public event Action? LanguageChanged;

    public I18nService(ConfigService configService)
    {
        _configService = configService;
    }

    public string CurrentLanguage => _configService.Settings.Language;

    public string T(string key)
    {
        var lang = Translations.ContainsKey(CurrentLanguage) ? CurrentLanguage : "zh-CN";
        if (Translations[lang].TryGetValue(key, out var val))
        {
            return val;
        }
        return Translations["zh-CN"].GetValueOrDefault(key, key);
    }

    public async Task SetLanguageAsync(string language)
    {
        if (_configService.Settings.Language == language) return;
        _configService.Settings.Language = language;
        await _configService.SaveSettingsAsync();
        LanguageChanged?.Invoke();
    }
}
