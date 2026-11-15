using System.Text.Json;
using AlgerMusicPlayer.WinUI.Models;

namespace AlgerMusicPlayer.WinUI.Services.System;

/// <summary>
/// 本地配置与状态持久化服务（对应原 electron-store 与 Pinia 持久化）
/// </summary>
public class ConfigService
{
    private readonly string _appDataDir;
    private readonly string _settingsFilePath;
    private readonly string _historyFilePath;
    private readonly string _favoritesFilePath;
    private readonly string _queueFilePath;
    private readonly string _localSongsFilePath;
    private readonly string _customPlaylistsFilePath;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppSettings Settings { get; private set; } = new();
    public List<SongItem> PlayHistory { get; private set; } = [];
    public List<SongItem> LocalFavorites { get; private set; } = [];
    public List<SongItem> SavedQueue { get; private set; } = [];
    public List<SongItem> CachedLocalSongs { get; private set; } = [];
    public List<PlaylistItem> CustomPlaylists { get; private set; } = [];

    public event Action? SettingsChanged;

    public ConfigService()
    {
        _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AlgerMusicPlayer.WinUI");
        Directory.CreateDirectory(_appDataDir);

        _settingsFilePath = Path.Combine(_appDataDir, "settings.json");
        _historyFilePath = Path.Combine(_appDataDir, "history.json");
        _favoritesFilePath = Path.Combine(_appDataDir, "favorites.json");
        _queueFilePath = Path.Combine(_appDataDir, "queue.json");
        _localSongsFilePath = Path.Combine(_appDataDir, "local_songs.json");
        _customPlaylistsFilePath = Path.Combine(_appDataDir, "custom_playlists.json");

        LoadAllSync();
    }

    public string AppDataDirectory => _appDataDir;
    public string CacheDirectory
    {
        get
        {
            var dir = !string.IsNullOrWhiteSpace(Settings.DiskCacheDir)
                ? Settings.DiskCacheDir
                : Path.Combine(_appDataDir, "Cache");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private void LoadAllSync()
    {
        Settings = ReadJsonSafe<AppSettings>(_settingsFilePath) ?? new AppSettings();
        PlayHistory = ReadJsonSafe<List<SongItem>>(_historyFilePath) ?? [];
        LocalFavorites = ReadJsonSafe<List<SongItem>>(_favoritesFilePath) ?? [];
        SavedQueue = ReadJsonSafe<List<SongItem>>(_queueFilePath) ?? [];
        CachedLocalSongs = ReadJsonSafe<List<SongItem>>(_localSongsFilePath) ?? [];
        CustomPlaylists = ReadJsonSafe<List<PlaylistItem>>(_customPlaylistsFilePath) ?? [];
        foreach (var cp in CustomPlaylists)
        {
            cp.Source = "local_custom";
            if (string.IsNullOrWhiteSpace(cp.ThirdPartyId))
            {
                cp.ThirdPartyId = $"local_{Guid.NewGuid():N}"[..18];
            }
            cp.Tracks ??= [];
            cp.TrackCount = cp.Tracks.Count;
            if (!ImageUriHelper.HasRealImageUrl(cp.CoverImgUrl) &&
                cp.Tracks.FirstOrDefault(t => ImageUriHelper.HasRealImageUrl(t.PicUrl)) is { } firstCoverTrack)
            {
                cp.CoverImgUrl = firstCoverTrack.PicUrl;
            }
        }

        if (string.IsNullOrWhiteSpace(Settings.DownloadPath))
        {
            Settings.DownloadPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "AlgerMusicPlayer");
        }
    }

    public async Task SaveSettingsAsync()
    {
        await WriteJsonSafeAsync(_settingsFilePath, Settings);
        SettingsChanged?.Invoke();
    }

    public void SaveSettingsSync()
    {
        try
        {
            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
        }
    }

    public async Task AddPlayHistoryAsync(SongItem song)
    {
        if (song == null || string.IsNullOrWhiteSpace(song.Id)) return;

        var existing = PlayHistory.FirstOrDefault(s => s.Id == song.Id);
        if (existing != null)
        {
            song.PlayCount = existing.PlayCount + 1;
            PlayHistory.Remove(existing);
        }
        else
        {
            song.PlayCount = Math.Max(1, song.PlayCount);
        }

        PlayHistory.Insert(0, song);
        if (PlayHistory.Count > 500)
        {
            PlayHistory.RemoveRange(500, PlayHistory.Count - 500);
        }

        await WriteJsonSafeAsync(_historyFilePath, PlayHistory);
    }

    public async Task ClearPlayHistoryAsync()
    {
        PlayHistory.Clear();
        await WriteJsonSafeAsync(_historyFilePath, PlayHistory);
    }

    public bool IsFavorite(string songId)
    {
        return LocalFavorites.Any(s => s.Id == songId);
    }

    public async Task<bool> ToggleFavoriteAsync(SongItem song)
    {
        var existing = LocalFavorites.FirstOrDefault(s => s.Id == song.Id);
        bool isNowLiked;
        if (existing != null)
        {
            LocalFavorites.Remove(existing);
            song.IsLiked = false;
            isNowLiked = false;
        }
        else
        {
            song.IsLiked = true;
            LocalFavorites.Insert(0, song);
            isNowLiked = true;
        }

        await WriteJsonSafeAsync(_favoritesFilePath, LocalFavorites);
        return isNowLiked;
    }

    public async Task SaveQueueAsync(IEnumerable<SongItem> queue)
    {
        SavedQueue = queue.Take(1000).ToList();
        await WriteJsonSafeAsync(_queueFilePath, SavedQueue);
    }

    public async Task SaveLocalSongsAsync(IEnumerable<SongItem> songs)
    {
        CachedLocalSongs = songs.ToList();
        await WriteJsonSafeAsync(_localSongsFilePath, CachedLocalSongs);
    }

    /// <summary>
    /// 创建本地跨平台混合歌单并持久化
    /// </summary>
    public async Task<PlaylistItem> CreateCustomPlaylistAsync(string name)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var pl = new PlaylistItem
        {
            Id = nowMs,
            Source = "local_custom",
            ThirdPartyId = $"local_{Guid.NewGuid():N}"[..18],
            Name = name.Trim(),
            Description = "本地跨平台混合歌单（支持任意音源单曲混编与直接播放）",
            CreatorName = "本地跨平台歌单",
            CreateTime = nowMs,
            UpdateTime = nowMs,
            TrackCount = 0,
            Tracks = []
        };
        CustomPlaylists.Insert(0, pl);
        await WriteJsonSafeAsync(_customPlaylistsFilePath, CustomPlaylists);
        return pl;
    }

    /// <summary>
    /// 删除指定本地跨平台混合歌单
    /// </summary>
    public async Task<bool> DeleteCustomPlaylistAsync(string thirdPartyId)
    {
        if (string.IsNullOrWhiteSpace(thirdPartyId)) return false;
        int removed = CustomPlaylists.RemoveAll(p => string.Equals(p.ThirdPartyId, thirdPartyId, StringComparison.OrdinalIgnoreCase));
        if (removed <= 0) return false;
        await WriteJsonSafeAsync(_customPlaylistsFilePath, CustomPlaylists);
        return true;
    }

    /// <summary>
    /// 向指定本地跨平台混合歌单添加歌曲（保留原平台与全部原生元数据）
    /// </summary>
    public async Task<(int AddedCount, int SkippedCount)> AddSongsToCustomPlaylistAsync(
        string thirdPartyId,
        IEnumerable<SongItem> songs)
    {
        var pl = CustomPlaylists.FirstOrDefault(p => string.Equals(p.ThirdPartyId, thirdPartyId, StringComparison.OrdinalIgnoreCase));
        if (pl == null) return (0, 0);

        int added = 0;
        int skipped = 0;
        foreach (var song in songs)
        {
            if (song == null || string.IsNullOrWhiteSpace(song.Id)) continue;
            if (pl.Tracks.Any(t => string.Equals(t.Id, song.Id, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }
            pl.Tracks.Add(CloneSongForStorage(song));
            added++;
        }

        if (added > 0)
        {
            pl.TrackCount = pl.Tracks.Count;
            pl.UpdateTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!ImageUriHelper.HasRealImageUrl(pl.CoverImgUrl) &&
                pl.Tracks.FirstOrDefault(t => ImageUriHelper.HasRealImageUrl(t.PicUrl)) is { } firstCover)
            {
                pl.CoverImgUrl = firstCover.PicUrl;
            }
            await WriteJsonSafeAsync(_customPlaylistsFilePath, CustomPlaylists);
        }

        return (added, skipped);
    }

    /// <summary>
    /// 从指定本地跨平台混合歌单删除一批歌曲
    /// </summary>
    public async Task<int> RemoveSongsFromCustomPlaylistAsync(string thirdPartyId, IEnumerable<string> songIds)
    {
        var pl = CustomPlaylists.FirstOrDefault(p => string.Equals(p.ThirdPartyId, thirdPartyId, StringComparison.OrdinalIgnoreCase));
        if (pl == null) return 0;

        var idSet = new HashSet<string>(songIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
        if (idSet.Count == 0) return 0;

        int removed = pl.Tracks.RemoveAll(t => idSet.Contains(t.Id));
        if (removed > 0)
        {
            pl.TrackCount = pl.Tracks.Count;
            pl.UpdateTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var nextCover = pl.Tracks.FirstOrDefault(t => ImageUriHelper.HasRealImageUrl(t.PicUrl))?.PicUrl ?? string.Empty;
            if (pl.Tracks.Count == 0 ||
                !ImageUriHelper.HasRealImageUrl(pl.CoverImgUrl) ||
                pl.Tracks.All(t => !string.Equals(t.PicUrl, pl.CoverImgUrl, StringComparison.OrdinalIgnoreCase)))
            {
                pl.CoverImgUrl = nextCover;
            }
            await WriteJsonSafeAsync(_customPlaylistsFilePath, CustomPlaylists);
        }

        return removed;
    }

    private static SongItem CloneSongForStorage(SongItem src) => new()
    {
        Id = src.Id,
        Name = src.Name,
        PicUrl = src.PicUrl,
        Artists = src.Artists.Select(a => new ArtistItem { Id = a.Id, Name = a.Name, PicUrl = a.PicUrl }).ToList(),
        Album = new AlbumItem
        {
            Id = src.Album.Id,
            Name = src.Album.Name,
            PicUrl = src.Album.PicUrl,
            PublishTime = src.Album.PublishTime,
            Size = src.Album.Size,
            ArtistName = src.Album.ArtistName
        },
        DurationMs = src.DurationMs,
        MvId = src.MvId,
        Fee = src.Fee,
        Alias = [.. src.Alias],
        Translations = [.. src.Translations],
        Source = src.Source,
        LocalFilePath = src.LocalFilePath,
        ChannelCount = src.ChannelCount,
        FileSize = src.FileSize,
        ModifiedTime = src.ModifiedTime,
        QqSongMid = src.QqSongMid,
        QqSongId = src.QqSongId,
        EmbeddedLyric = src.EmbeddedLyric,
        BilibiliBvid = src.BilibiliBvid,
        BilibiliCid = src.BilibiliCid,
        BilibiliAid = src.BilibiliAid,
        KugouHash = src.KugouHash,
        Kugou320Hash = src.Kugou320Hash,
        KugouSqHash = src.KugouSqHash,
        KugouResHash = src.KugouResHash,
        KugouAlbumId = src.KugouAlbumId,
        KugouCloudFileId = src.KugouCloudFileId,
        KuwoRid = src.KuwoRid,
        MiguCopyrightId = src.MiguCopyrightId,
        MiguContentId = src.MiguContentId,
        MiguLrcUrl = src.MiguLrcUrl,
        QishuiTrackId = src.QishuiTrackId,
        NativeTrackId = src.NativeTrackId,
        NativePlayUrl = src.NativePlayUrl,
        YtmSetVideoId = src.YtmSetVideoId,
        GdTrackId = src.GdTrackId,
        GdSource = src.GdSource,
        NativeQualityHint = src.NativeQualityHint,
        IsLiked = src.IsLiked
    };

    /// <summary>
    /// 统计缓存目录磁盘占用（音乐缓存字节、歌词缓存字节、总缓存字节、音频文件数、歌词文件数、占用百分比）
    /// </summary>
    public (long MusicBytes, long LyricBytes, long TotalBytes, int MusicCount, int LyricCount, double UsagePercent) GetCacheSizeStatsFull()
    {
        try
        {
            var dirInfo = new DirectoryInfo(CacheDirectory);
            if (!dirInfo.Exists) return (0, 0, 0, 0, 0, 0);

            long musicBytes = 0;
            long lyricBytes = 0;
            long totalBytes = 0;
            int musicCount = 0;
            int lyricCount = 0;

            foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                totalBytes += file.Length;
                if (file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                    file.Extension.Equals(".lrc", StringComparison.OrdinalIgnoreCase))
                {
                    lyricBytes += file.Length;
                    lyricCount++;
                }
                else
                {
                    musicBytes += file.Length;
                    musicCount++;
                }
            }

            long limitBytes = Math.Max(256L, Settings.DiskCacheMaxSizeMB) * 1024L * 1024L;
            double usagePct = Math.Clamp(totalBytes * 100.0 / limitBytes, 0.0, 100.0);
            return (musicBytes, lyricBytes, totalBytes, musicCount, lyricCount, usagePct);
        }
        catch
        {
            return (0, 0, 0, 0, 0, 0);
        }
    }

    public (long MusicBytes, long LyricBytes, long TotalBytes) GetCacheSizeStats()
    {
        var (m, l, t, _, _, _) = GetCacheSizeStatsFull();
        return (m, l, t);
    }

    /// <summary>
    /// 按原版 ClearCacheSettings.vue 7 大分类清除应用缓存与数据
    /// </summary>
    public async Task ClearAppDataByScopesAsync(IEnumerable<string> scopes)
    {
        foreach (var scope in scopes)
        {
            switch (scope)
            {
                case "history":
                    await ClearPlayHistoryAsync();
                    break;
                case "favorite":
                    LocalFavorites.Clear();
                    await WriteJsonSafeAsync(_favoritesFilePath, LocalFavorites);
                    break;
                case "user":
                    Settings.NeteaseCookie = string.Empty;
                    Settings.SavedUid = 0;
                    Settings.LoginType = string.Empty;
                    Settings.QqMusicCookie = string.Empty;
                    Settings.QqMusicUin = string.Empty;
                    Settings.QqMusicNickname = string.Empty;
                    try
                    {
                        var credFile = Path.Combine(_appDataDir, "Credentials", "accounts.dat");
                        if (File.Exists(credFile)) File.Delete(credFile);
                    }
                    catch
                    {
                    }
                    await SaveSettingsAsync();
                    break;
                case "settings":
                    Settings = new AppSettings();
                    await SaveSettingsAsync();
                    break;
                case "resources":
                    ClearCacheFiles("music");
                    break;
                case "lyrics":
                    ClearCacheFiles("lyric");
                    break;
            }
        }
    }

    /// <summary>
    /// 清理指定类型的缓存文件（music / lyric / all）
    /// </summary>
    public int ClearCacheFiles(string cacheType = "all")
    {
        int count = 0;
        try
        {
            var dirInfo = new DirectoryInfo(CacheDirectory);
            if (!dirInfo.Exists) return 0;

            foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                bool isLyric = file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                               file.Extension.Equals(".lrc", StringComparison.OrdinalIgnoreCase);

                if (cacheType == "all" ||
                    (cacheType == "lyric" && isLyric) ||
                    (cacheType == "music" && !isLyric))
                {
                    try
                    {
                        file.Delete();
                        count++;
                    }
                    catch
                    {
                        // 正在播放的文件可能被占用，跳过
                    }
                }
            }
        }
        catch
        {
            // 忽略
        }
        return count;
    }

    /// <summary>
    /// 检查当前缓存目录是否包含已有缓存文件（用于切换缓存目录时弹出「迁移 / 销毁 / 保留」三选一提示）
    /// </summary>
    public bool HasExistingCacheFiles()
    {
        try
        {
            var dirInfo = new DirectoryInfo(CacheDirectory);
            return dirInfo.Exists && dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 切换磁盘缓存目录并按用户选择处理旧缓存文件（migrate = 迁移到新目录，destroy = 销毁旧缓存，keep = 保留旧缓存）
    /// </summary>
    public async Task<(int ProcessedCount, string SummaryMessage)> SwitchDiskCacheDirectoryAsync(string newCacheDir, string mode)
    {
        var oldDir = CacheDirectory;
        Directory.CreateDirectory(newCacheDir);

        int count = 0;
        if (!string.Equals(oldDir, newCacheDir, StringComparison.OrdinalIgnoreCase) && Directory.Exists(oldDir))
        {
            await Task.Run(() =>
            {
                var oldInfo = new DirectoryInfo(oldDir);
                foreach (var file in oldInfo.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (mode == "migrate")
                        {
                            var dest = Path.Combine(newCacheDir, file.Name);
                            File.Copy(file.FullName, dest, overwrite: true);
                            file.Delete();
                            count++;
                        }
                        else if (mode == "destroy")
                        {
                            file.Delete();
                            count++;
                        }
                    }
                    catch
                    {
                        // 忽略占用中文件
                    }
                }
            });
        }

        Settings.DiskCacheDir = newCacheDir;
        await SaveSettingsAsync();

        string msg = mode switch
        {
            "migrate" => $"缓存目录已更新，并成功迁移 {count} 个缓存文件至新目录",
            "destroy" => $"缓存目录已更新，并已销毁旧目录中的 {count} 个缓存文件",
            _ => $"缓存目录已更改为：{newCacheDir}（已保留旧目录缓存）"
        };
        return (count, msg);
    }

    private static T? ReadJsonSafe<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<T>(fs, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 带重试与临时文件原子替换的 JSON 写入（防止 Windows 杀毒或同步软件短暂锁文件报错）
    /// </summary>
    private async Task WriteJsonSafeAsync<T>(string path, T data)
    {
        await _ioLock.WaitAsync();
        try
        {
            var tempPath = path + ".tmp";
            var json = JsonSerializer.Serialize(data, JsonOptions);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await File.WriteAllTextAsync(tempPath, json);
                    File.Move(tempPath, path, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    await Task.Delay(80 * (attempt + 1));
                }
            }
        }
        catch
        {
            // 忽略文件锁定异常，避免应用崩溃
        }
        finally
        {
            _ioLock.Release();
        }
    }
}
