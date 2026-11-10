using System.Collections.ObjectModel;
using System.Text;
using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.Audio;
using AlgerMusicPlayer.WinUI.Services.Local;
using AlgerMusicPlayer.WinUI.Services.Lyric;
using AlgerMusicPlayer.WinUI.Services.System;
using Microsoft.UI.Dispatching;
using TagLib;

namespace AlgerMusicPlayer.WinUI.Services.Download;

/// <summary>
/// 音乐下载管理与 ID3v2 / FLAC 元数据及封面内嵌服务（1:1 对齐 downloadManager.ts 与 store/modules/download.ts）
/// </summary>
public class DownloadManagerService
{
    private static readonly HashSet<string> SupportedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".opus", ".ape", ".wma"
    };

    private readonly ConfigService _configService;
    private readonly AudioPlayerService _playerService;
    private readonly LyricService _lyricService;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(15) };
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly object _queueLock = new();
    private int _activeCount;

    /// <summary>
    /// 正在下载 / 排队中 / 已暂停的任务列表（对应 DownloadPage.vue 下载中 Tab）
    /// </summary>
    public ObservableCollection<DownloadTaskItem> Tasks { get; } = [];

    /// <summary>
    /// 已下载完成的音乐记录列表（对应 DownloadPage.vue 已下载 Tab）
    /// </summary>
    public ObservableCollection<CompletedDownloadItem> CompletedTasks { get; } = [];

    /// <summary>
    /// 当队列数量、进度或已完成列表变化时触发
    /// </summary>
    public event Action? StateChanged;

    public DownloadManagerService(
        ConfigService configService,
        AudioPlayerService playerService,
        LyricService lyricService)
    {
        _configService = configService;
        _playerService = playerService;
        _lyricService = lyricService;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public double GetTotalProgress()
    {
        if (Tasks.Count == 0) return 0;
        return Math.Round(Tasks.Average(t => t.Progress), 1);
    }

    /// <summary>
    /// 按用户配置的命名模板格式化文件名（支持 {songName}、{artistName}、{albumName}）
    /// </summary>
    public string FormatDownloadFileName(SongItem song, string? customFormat = null)
    {
        var format = string.IsNullOrWhiteSpace(customFormat)
            ? _configService.Settings.DownloadNameFormat
            : customFormat;
        if (string.IsNullOrWhiteSpace(format))
        {
            format = "{songName} - {artistName}";
        }

        var songName = string.IsNullOrWhiteSpace(song.Name) ? "未知歌曲" : song.Name.Trim();
        var artistName = string.IsNullOrWhiteSpace(song.ArtistText) ? "未知歌手" : song.ArtistText.Trim().Replace("/", "、");
        var albumName = string.IsNullOrWhiteSpace(song.AlbumText) ? "未知专辑" : song.AlbumText.Trim();

        var formatted = format
            .Replace("{songName}", songName, StringComparison.OrdinalIgnoreCase)
            .Replace("{artistName}", artistName, StringComparison.OrdinalIgnoreCase)
            .Replace("{albumName}", albumName, StringComparison.OrdinalIgnoreCase);

        return SanitizeFileName(formatted);
    }

    public Task EnqueueDownloadAsync(SongItem song)
    {
        if (song == null) return Task.CompletedTask;

        var existing = Tasks.FirstOrDefault(t =>
            t.Song.Id == song.Id && t.State is "queued" or "downloading" or "paused");
        if (existing != null) return Task.CompletedTask;

        var formattedName = FormatDownloadFileName(song);
        var taskItem = new DownloadTaskItem
        {
            Song = song,
            Filename = formattedName,
            State = "queued",
            Status = "等待下载",
            Progress = 0
        };
        Tasks.Insert(0, taskItem);
        NotifyStateChanged();

        ProcessQueue();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 将歌曲按用户指定的音源直链及音质规格加入下载队列（跳过自动重复解析，确保下载所选音质）
    /// </summary>
    public Task EnqueueDownloadWithSourceAsync(SongItem song, string directUrl, string extHint, string qualityBadge)
    {
        if (song == null) return Task.CompletedTask;

        var existing = Tasks.FirstOrDefault(t =>
            t.Song.Id == song.Id && t.State is "queued" or "downloading" or "paused");
        if (existing != null) return Task.CompletedTask;

        var formattedName = FormatDownloadFileName(song);
        var taskItem = new DownloadTaskItem
        {
            Song = song,
            Filename = formattedName,
            State = "queued",
            Status = $"等待下载 ({qualityBadge})",
            Progress = 0,
            DirectSourceUrl = directUrl,
            DirectExtensionHint = extHint,
            SelectedQualityBadge = qualityBadge
        };
        Tasks.Insert(0, taskItem);
        NotifyStateChanged();

        ProcessQueue();
        return Task.CompletedTask;
    }

    public void PauseTask(string taskId)
    {
        var task = Tasks.FirstOrDefault(t => t.TaskId == taskId);
        if (task == null || task.State != "downloading") return;

        task.State = "paused";
        task.Status = "已暂停";
        try
        {
            task.Cts?.Cancel();
        }
        catch
        {
            // 忽略取消异常
        }
        NotifyStateChanged();
    }

    public void ResumeTask(string taskId)
    {
        var task = Tasks.FirstOrDefault(t => t.TaskId == taskId);
        if (task == null || task.State is not ("paused" or "error")) return;

        task.State = "queued";
        task.Status = "等待下载";
        task.ErrorMessage = null;
        NotifyStateChanged();
        ProcessQueue();
    }

    public void CancelTask(string taskId)
    {
        var task = Tasks.FirstOrDefault(t => t.TaskId == taskId);
        if (task == null) return;

        task.State = "cancelled";
        task.Status = "已取消";
        try
        {
            task.Cts?.Cancel();
        }
        catch
        {
            // 忽略取消异常
        }

        var partialPath = task.FilePath;
        Tasks.Remove(task);
        NotifyStateChanged();

        if (!string.IsNullOrWhiteSpace(partialPath) && global::System.IO.File.Exists(partialPath))
        {
            try
            {
                global::System.IO.File.Delete(partialPath);
            }
            catch
            {
                // 忽略临时文件占用
            }
        }

        ProcessQueue();
    }

    private void ProcessQueue()
    {
        while (true)
        {
            DownloadTaskItem? nextTask = null;
            int maxConcurrent = Math.Clamp(_configService.Settings.DownloadMaxConcurrent, 1, 5);

            lock (_queueLock)
            {
                if (_activeCount >= maxConcurrent)
                {
                    return;
                }

                nextTask = Tasks.FirstOrDefault(t => t.State == "queued");
                if (nextTask == null)
                {
                    return;
                }

                nextTask.State = "downloading";
                nextTask.Status = "解析音源";
                _activeCount++;
            }

            NotifyStateChanged();
            _ = RunSingleTaskAndContinueQueueAsync(nextTask);
        }
    }

    private async Task RunSingleTaskAndContinueQueueAsync(DownloadTaskItem taskItem)
    {
        try
        {
            await ExecuteDownloadTaskAsync(taskItem);
        }
        finally
        {
            lock (_queueLock)
            {
                _activeCount = Math.Max(0, _activeCount - 1);
            }
            NotifyStateChanged();
            ProcessQueue();
        }
    }

    private async Task ExecuteDownloadTaskAsync(DownloadTaskItem taskItem)
    {
        using var cts = new CancellationTokenSource();
        taskItem.Cts = cts;
        var token = cts.Token;
        string? tempPath = null;

        try
        {
            RunOnUiThread(() =>
            {
                taskItem.Status = !string.IsNullOrWhiteSpace(taskItem.SelectedQualityBadge)
                    ? $"准备下载 ({taskItem.SelectedQualityBadge})"
                    : "解析无损音源";
                NotifyStateChanged();
            });

            var (sourceUrl, extHint) = !string.IsNullOrWhiteSpace(taskItem.DirectSourceUrl)
                ? (taskItem.DirectSourceUrl, taskItem.DirectExtensionHint ?? ".flac")
                : await _playerService.ResolveDownloadSourceAsync(taskItem.Song, token);
            token.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(sourceUrl))
            {
                RunOnUiThread(() =>
                {
                    taskItem.State = "error";
                    taskItem.Status = "下载失败";
                    taskItem.ErrorMessage = "无法获取可用下载链接";
                    NotifyStateChanged();
                });
                return;
            }

            var downloadDir = _configService.Settings.DownloadPath;
            Directory.CreateDirectory(downloadDir);

            var safeFileName = FormatDownloadFileName(taskItem.Song);
            tempPath = Path.Combine(downloadDir, $".{taskItem.TaskId}_{safeFileName}.tmp");
            string? contentType = null;

            RunOnUiThread(() =>
            {
                taskItem.Filename = safeFileName;
                taskItem.FilePath = tempPath;
                taskItem.Status = "正在下载";
                NotifyStateChanged();
            });

            if (global::System.IO.File.Exists(sourceUrl))
            {
                var fi = new FileInfo(sourceUrl);
                global::System.IO.File.Copy(sourceUrl, tempPath, overwrite: true);
                RunOnUiThread(() =>
                {
                    taskItem.TotalBytes = fi.Length;
                    taskItem.DownloadedBytes = fi.Length;
                    taskItem.Progress = 100;
                    NotifyStateChanged();
                });
            }
            else
            {
                using var resp = await _httpClient.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, token);
                resp.EnsureSuccessStatusCode();
                contentType = resp.Content.Headers.ContentType?.MediaType;

                long total = resp.Content.Headers.ContentLength ?? 0;
                RunOnUiThread(() =>
                {
                    taskItem.TotalBytes = total;
                    NotifyStateChanged();
                });

                await using (var netStream = await resp.Content.ReadAsStreamAsync(token))
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[32768];
                    long downloaded = 0;
                    int read;
                    long lastUiUpdateTick = 0;

                    while ((read = await netStream.ReadAsync(buffer, token)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), token);
                        downloaded += read;

                        long nowTick = Environment.TickCount64;
                        if (nowTick - lastUiUpdateTick >= 80 || (total > 0 && downloaded >= total))
                        {
                            lastUiUpdateTick = nowTick;
                            long currentDownloaded = downloaded;
                            RunOnUiThread(() =>
                            {
                                taskItem.DownloadedBytes = currentDownloaded;
                                if (total > 0)
                                {
                                    taskItem.Progress = Math.Round(currentDownloaded * 100.0 / total, 1);
                                }
                                NotifyStateChanged();
                            });
                        }
                    }
                }
            }

            token.ThrowIfCancellationRequested();

            // 1:1 对齐 downloadManager.ts finalizeDownload：通过文件头魔数 (Magic Bytes) 与 Content-Type 检测真实无损/有损音频格式
            var realExt = DetectRealAudioExtension(tempPath, sourceUrl, contentType, extHint);
            var targetPath = Path.Combine(downloadDir, $"{safeFileName}{realExt}");
            global::System.IO.File.Move(tempPath, targetPath, overwrite: true);
            tempPath = null;

            RunOnUiThread(() =>
            {
                taskItem.FilePath = targetPath;
                NotifyStateChanged();
            });

            // 写入音频内嵌标签与单独保存 .lrc 歌词文件
            if (_configService.Settings.EmbedCoverAndLyricOnDownload || _configService.Settings.DownloadSaveLyricFile)
            {
                RunOnUiThread(() =>
                {
                    taskItem.Status = "写入标签";
                    NotifyStateChanged();
                });
                await WriteAudioTagsAndSidecarLyricAsync(targetPath, taskItem.Song);
            }

            _configService.Settings.ClearedCompletedDownloadPaths.Remove(targetPath);
            await _configService.SaveSettingsAsync();

            RunOnUiThread(() =>
            {
                taskItem.Progress = 100;
                taskItem.State = "completed";
                taskItem.Status = "下载完成";
                NotifyStateChanged();
            });

            await Task.Delay(350, CancellationToken.None);

            RunOnUiThread(() =>
            {
                Tasks.Remove(taskItem);
                NotifyStateChanged();
            });

            await RefreshCompletedDownloadsAsync();
        }
        catch (OperationCanceledException)
        {
            if (taskItem.State == "paused")
            {
                RunOnUiThread(() =>
                {
                    taskItem.Status = "已暂停";
                    NotifyStateChanged();
                });
            }
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(tempPath) && global::System.IO.File.Exists(tempPath))
            {
                try
                {
                    global::System.IO.File.Delete(tempPath);
                }
                catch
                {
                    // 忽略临时文件清理异常
                }
            }

            RunOnUiThread(() =>
            {
                taskItem.State = "error";
                taskItem.Status = "下载失败";
                taskItem.ErrorMessage = ex.Message;
                NotifyStateChanged();
            });
        }
        finally
        {
            taskItem.Cts = null;
        }
    }

    private async Task WriteAudioTagsAndSidecarLyricAsync(string filePath, SongItem song)
    {
        try
        {
            var parsedLyric = await _lyricService.GetParsedLyricAsync(song);

            // 若开启了“单独保存歌词文件”，在同目录生成同名 .lrc 文件
            if (_configService.Settings.DownloadSaveLyricFile && !string.IsNullOrWhiteSpace(parsedLyric.RawLrc))
            {
                try
                {
                    var lrcPath = Path.ChangeExtension(filePath, ".lrc");
                    await global::System.IO.File.WriteAllTextAsync(lrcPath, parsedLyric.RawLrc, Encoding.UTF8);
                }
                catch
                {
                    // 忽略单独歌词写入异常
                }
            }

            if (!_configService.Settings.EmbedCoverAndLyricOnDownload)
            {
                return;
            }

            byte[]? coverBytes = null;
            if (!string.IsNullOrWhiteSpace(song.PicUrl) && song.PicUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var coverUrl = song.PicUrl.Contains("param=") ? song.PicUrl : $"{song.PicUrl}?param=500y500";
                    coverBytes = await _httpClient.GetByteArrayAsync(coverUrl);
                }
                catch
                {
                    // 忽略封面下载失败
                }
            }

            await Task.Run(() =>
            {
                using var tfile = TagLib.File.Create(filePath);
                tfile.Tag.Title = song.Name;
                tfile.Tag.Performers = song.Artists.Select(a => a.Name).ToArray();
                tfile.Tag.Album = song.AlbumText;
                if (!string.IsNullOrWhiteSpace(parsedLyric.RawLrc))
                {
                    tfile.Tag.Lyrics = parsedLyric.RawLrc;
                }

                if (coverBytes != null && coverBytes.Length > 0)
                {
                    var pic = new Picture(new ByteVector(coverBytes))
                    {
                        Type = PictureType.FrontCover,
                        MimeType = "image/jpeg",
                        Description = "Cover"
                    };
                    tfile.Tag.Pictures = [pic];
                }

                tfile.Save();
            });
        }
        catch
        {
            // 部分特殊流格式不支持标签写入时跳过，保留音频文件本身
        }
    }

    /// <summary>
    /// 扫描下载目录并刷新“已下载”列表（对应 downloadManager.ts getDownloadedMusic）
    /// </summary>
    public async Task RefreshCompletedDownloadsAsync()
    {
        var downloadDir = _configService.Settings.DownloadPath;
        if (string.IsNullOrWhiteSpace(downloadDir) || !Directory.Exists(downloadDir))
        {
            RunOnUiThread(() =>
            {
                CompletedTasks.Clear();
                NotifyStateChanged();
            });
            return;
        }

        var clearedSet = new HashSet<string>(_configService.Settings.ClearedCompletedDownloadPaths, StringComparer.OrdinalIgnoreCase);
        var activeFilePaths = new HashSet<string>(
            Tasks.Where(t => !string.IsNullOrWhiteSpace(t.FilePath)).Select(t => t.FilePath),
            StringComparer.OrdinalIgnoreCase);

        var items = await Task.Run(() =>
        {
            var result = new List<CompletedDownloadItem>();
            try
            {
                var files = Directory.EnumerateFiles(downloadDir, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => SupportedAudioExtensions.Contains(Path.GetExtension(f)));

                foreach (var file in files)
                {
                    if (clearedSet.Contains(file) || activeFilePaths.Contains(file))
                    {
                        continue;
                    }

                    try
                    {
                        var fi = new FileInfo(file);
                        string filename = Path.GetFileNameWithoutExtension(file);
                        string title = filename;
                        string artist = "未知歌手";
                        string album = "未知专辑";
                        double durationSec = 0;

                        try
                        {
                            using var tfile = TagLib.File.Create(file);
                            var repairedTitle = LocalMusicScannerService.RepairLegacyChineseText(tfile.Tag.Title);
                            if (!string.IsNullOrWhiteSpace(repairedTitle))
                            {
                                title = repairedTitle;
                            }

                            if (tfile.Tag.Performers is { Length: > 0 })
                            {
                                var performers = tfile.Tag.Performers
                                    .Select(LocalMusicScannerService.RepairLegacyChineseText)
                                    .Where(s => !string.IsNullOrWhiteSpace(s))
                                    .ToArray();
                                if (performers.Length > 0)
                                {
                                    artist = string.Join(" / ", performers);
                                }
                            }

                            var repairedAlbum = LocalMusicScannerService.RepairLegacyChineseText(tfile.Tag.Album);
                            if (!string.IsNullOrWhiteSpace(repairedAlbum))
                            {
                                album = repairedAlbum;
                            }

                            durationSec = tfile.Properties.Duration.TotalSeconds;
                        }
                        catch
                        {
                            // 若文件无标准标签，按文件名推断
                        }

                        result.Add(new CompletedDownloadItem
                        {
                            Id = Math.Abs(file.GetHashCode()).ToString(),
                            FilePath = file,
                            Filename = filename,
                            DisplayName = title,
                            ArtistText = artist,
                            AlbumText = album,
                            FileSize = fi.Length,
                            DurationSeconds = durationSec,
                            CreatedTime = fi.LastWriteTime
                        });
                    }
                    catch
                    {
                        // 忽略损坏或被锁定的文件
                    }
                }
            }
            catch
            {
                // 忽略目录访问异常
            }

            return result.OrderByDescending(x => x.CreatedTime).ToList();
        });

        RunOnUiThread(() =>
        {
            CompletedTasks.Clear();
            foreach (var item in items)
            {
                CompletedTasks.Add(item);
            }
            NotifyStateChanged();
        });
    }

    /// <summary>
    /// 删除单首已下载音乐文件及同名 .lrc 歌词文件
    /// </summary>
    public async Task<bool> DeleteCompletedDownloadAsync(CompletedDownloadItem item)
    {
        if (item == null) return false;

        try
        {
            if (global::System.IO.File.Exists(item.FilePath))
            {
                global::System.IO.File.Delete(item.FilePath);
            }

            var lrcPath = Path.ChangeExtension(item.FilePath, ".lrc");
            if (global::System.IO.File.Exists(lrcPath))
            {
                global::System.IO.File.Delete(lrcPath);
            }

            _configService.Settings.ClearedCompletedDownloadPaths.Remove(item.FilePath);
            await _configService.SaveSettingsAsync();

            RunOnUiThread(() =>
            {
                CompletedTasks.Remove(item);
                NotifyStateChanged();
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 清空所有下载记录（保留磁盘上的已下载音乐文件，对应 DownloadPage.vue confirmClearHistory）
    /// </summary>
    public async Task ClearCompletedRecordsAsync()
    {
        foreach (var item in CompletedTasks)
        {
            if (!string.IsNullOrWhiteSpace(item.FilePath))
            {
                _configService.Settings.ClearedCompletedDownloadPaths.Add(item.FilePath);
            }
        }

        await _configService.SaveSettingsAsync();

        RunOnUiThread(() =>
        {
            CompletedTasks.Clear();
            NotifyStateChanged();
        });
    }

    private void RunOnUiThread(DispatcherQueueHandler action)
    {
        if (_dispatcherQueue != null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(action);
        }
        else
        {
            action();
        }
    }

    private void NotifyStateChanged()
    {
        StateChanged?.Invoke();
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? $"Track_{DateTimeOffset.Now.ToUnixTimeSeconds()}" : cleaned;
    }

    /// <summary>
    /// 通过文件头魔数 (Magic Bytes)、HTTP Content-Type、接口类型提示及 URL 精准识别音频扩展名（1:1 对齐 downloadManager.ts fileTypeFromFile）
    /// </summary>
    private static string DetectRealAudioExtension(string filePath, string url, string? contentType, string? extHint)
    {
        try
        {
            if (global::System.IO.File.Exists(filePath))
            {
                Span<byte> header = stackalloc byte[16];
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                int read = fs.Read(header);
                if (read >= 4)
                {
                    // FLAC: "fLaC" (0x66 0x4C 0x61 0x43)
                    if (header[0] == 0x66 && header[1] == 0x4C && header[2] == 0x61 && header[3] == 0x43)
                    {
                        return ".flac";
                    }

                    // WAV: "RIFF" .... "WAVE"
                    if (read >= 12 &&
                        header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                        header[8] == 0x57 && header[9] == 0x41 && header[10] == 0x56 && header[11] == 0x45)
                    {
                        return ".wav";
                    }

                    // Ogg: "OggS" (0x4F 0x67 0x67 0x53)
                    if (header[0] == 0x4F && header[1] == 0x67 && header[2] == 0x67 && header[3] == 0x53)
                    {
                        return ".ogg";
                    }

                    // MP4 / M4A / AAC container: bytes 4..7 == "ftyp" (0x66 0x74 0x79 0x70)
                    if (read >= 8 &&
                        header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70)
                    {
                        return ".m4a";
                    }

                    // APE: "MAC " (0x4D 0x41 0x43 0x20)
                    if (header[0] == 0x4D && header[1] == 0x41 && header[2] == 0x43 && header[3] == 0x20)
                    {
                        return ".ape";
                    }

                    // MP3: "ID3" or MPEG frame sync (0xFF 0xFB/0xF3/0xF2/0xFA)
                    if ((header[0] == 0x49 && header[1] == 0x44 && header[2] == 0x33) ||
                        (header[0] == 0xFF && (header[1] & 0xE0) == 0xE0))
                    {
                        return ".mp3";
                    }
                }
            }
        }
        catch
        {
            // 忽略文件头读取异常，继续根据提示推断
        }

        if (!string.IsNullOrWhiteSpace(extHint))
        {
            var normalized = extHint.StartsWith('.') ? extHint.ToLowerInvariant() : $".{extHint.ToLowerInvariant()}";
            if (SupportedAudioExtensions.Contains(normalized))
            {
                return normalized;
            }
        }

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var ct = contentType.ToLowerInvariant();
            if (ct.Contains("flac")) return ".flac";
            if (ct.Contains("wav")) return ".wav";
            if (ct.Contains("ogg")) return ".ogg";
            if (ct.Contains("mp4") || ct.Contains("m4a") || ct.Contains("aac")) return ".m4a";
            if (ct.Contains("mpeg") || ct.Contains("mp3")) return ".mp3";
        }

        return GuessExtension(url);
    }

    private static string GuessExtension(string url)
    {
        var lower = url.ToLowerInvariant();
        if (lower.Contains(".flac")) return ".flac";
        if (lower.Contains(".m4a")) return ".m4a";
        if (lower.Contains(".wav")) return ".wav";
        if (lower.Contains(".ogg")) return ".ogg";
        return ".mp3";
    }
}
