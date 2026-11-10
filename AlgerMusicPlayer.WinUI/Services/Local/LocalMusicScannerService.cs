using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.System;

namespace AlgerMusicPlayer.WinUI.Services.Local;

/// <summary>
/// 本地音乐多目录扫描、GB18030 乱码自动修复与层级文件夹树构建服务
/// （1:1 对应 .worktrees/local-music-folder-tree 新增 localMusicScanner.ts、localMusicText.ts 与 localMusicTree.ts）
/// </summary>
public partial class LocalMusicScannerService
{
    private readonly ConfigService _configService;
    private static readonly Encoding Gb18030Encoding;
    private static readonly Encoding Latin1Encoding = Encoding.Latin1;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".ape", ".wma"
    };

    [GeneratedRegex(@"[\u0080-\u00FF]")]
    private static partial Regex Latin1SupplementRegex();

    [GeneratedRegex(@"[\u3400-\u9FFF]")]
    private static partial Regex CjkUnifiedRegex();

    static LocalMusicScannerService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gb18030Encoding = Encoding.GetEncoding("GB18030");
    }

    public LocalMusicScannerService(ConfigService configService)
    {
        _configService = configService;
    }

    /// <summary>
    /// 获取当前已配置的全部扫描根目录（保持用户排序并去重）
    /// </summary>
    public List<string> GetConfiguredRootFolders()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in _configService.Settings.LocalMusicDirectories)
        {
            var normalized = NormalizeFolderPath(raw);
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        if (result.Count == 0)
        {
            var defaultMusic = NormalizeFolderPath(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
            if (!string.IsNullOrWhiteSpace(defaultMusic))
            {
                result.Add(defaultMusic);
                _configService.Settings.LocalMusicDirectories = [defaultMusic];
            }
        }

        return result;
    }

    public static string NormalizeFolderPath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return string.Empty;
        var trimmed = rawPath.Trim().Replace('/', '\\');
        if (trimmed.Length > 3)
        {
            trimmed = trimmed.TrimEnd('\\');
        }
        return trimmed;
    }

    /// <summary>
    /// 扫描所有已配置的本地音乐根目录（支持进度回调与已移除路径过滤）
    /// </summary>
    public async Task<List<SongItem>> ScanAllDirectoriesAsync(Action<int>? onProgress = null)
    {
        var dirs = GetConfiguredRootFolders();
        var removedSet = _configService.Settings.RemovedLocalSongPaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return await Task.Run(() =>
        {
            var songs = new List<SongItem>();
            var visitedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;

                IEnumerable<string> files;
                try
                {
                    var enumOpts = new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = true,
                        AttributesToSkip = FileAttributes.System
                    };
                    files = Directory.EnumerateFiles(dir, "*.*", enumOpts)
                        .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)));
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    var normalizedFile = NormalizeFolderPath(file);
                    if (!visitedFiles.Add(normalizedFile)) continue;
                    if (removedSet.Contains(normalizedFile)) continue;

                    var song = ParseLocalAudioFile(normalizedFile);
                    if (song != null)
                    {
                        songs.Add(song);
                        if (songs.Count % 15 == 0)
                        {
                            onProgress?.Invoke(songs.Count);
                        }
                    }
                }
            }

            onProgress?.Invoke(songs.Count);
            return songs
                .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(s => s.LocalFilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });
    }

    /// <summary>
    /// 检测并修复旧版 MP3 ID3v1/v2 标签常见的 Latin-1 误解码 GBK/GB18030 中文乱码（对应 shared/localMusicText.ts）
    /// </summary>
    public static string RepairLegacyChineseText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Replace("\0", "").Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;

        // 若已包含正常中日韩汉字，直接返回
        if (CjkUnifiedRegex().IsMatch(normalized))
        {
            return normalized;
        }

        // 若包含 0x80~0xFF 扩展拉丁字符，尝试按 Latin-1 还原原始字节并用 GB18030 重新解码
        if (Latin1SupplementRegex().IsMatch(normalized))
        {
            try
            {
                var rawBytes = Latin1Encoding.GetBytes(normalized);
                var decoded = Gb18030Encoding.GetString(rawBytes).Replace("\0", "").Trim();
                if (!decoded.Contains('\uFFFD') && CjkUnifiedRegex().IsMatch(decoded))
                {
                    return decoded;
                }
            }
            catch
            {
                // 忽略编码转换异常
            }
        }

        return normalized;
    }

    private SongItem? ParseLocalAudioFile(string filePath)
    {
        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists) return null;

            var fileNameNoExt = Path.GetFileNameWithoutExtension(filePath);
            string fallbackTitle = fileNameNoExt;
            string fallbackArtist = "未知歌手";

            if (fileNameNoExt.Contains(" - "))
            {
                var parts = fileNameNoExt.Split(" - ", 2);
                fallbackArtist = parts[0].Trim();
                fallbackTitle = parts[1].Trim();
            }

            string title = fallbackTitle;
            string artistName = fallbackArtist;
            string albumName = "未知专辑";
            long durationMs = 0;
            int? channelCount = null;
            string? embeddedLyric = null;
            string coverPath = string.Empty;

            try
            {
                using var tfile = TagLib.File.Create(new SharedReadFileAbstraction(filePath));
                var repairedTitle = RepairLegacyChineseText(tfile.Tag.Title);
                if (!string.IsNullOrWhiteSpace(repairedTitle))
                {
                    title = repairedTitle;
                }

                if (tfile.Tag.Performers is { Length: > 0 })
                {
                    var performers = tfile.Tag.Performers
                        .Select(RepairLegacyChineseText)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();
                    if (performers.Count > 0)
                    {
                        artistName = string.Join(" / ", performers);
                    }
                }

                var repairedAlbum = RepairLegacyChineseText(tfile.Tag.Album);
                if (!string.IsNullOrWhiteSpace(repairedAlbum))
                {
                    albumName = repairedAlbum;
                }

                if (!string.IsNullOrWhiteSpace(tfile.Tag.Lyrics))
                {
                    embeddedLyric = RepairLegacyChineseText(tfile.Tag.Lyrics);
                }

                durationMs = (long)tfile.Properties.Duration.TotalMilliseconds;
                if (tfile.Properties.AudioChannels > 0)
                {
                    channelCount = tfile.Properties.AudioChannels;
                }

                if (tfile.Tag.Pictures is { Length: > 0 })
                {
                    var pic = tfile.Tag.Pictures[0];
                    if (pic.Data?.Data is { Length: > 0 } bytes)
                    {
                        var coversDir = Path.Combine(_configService.AppDataDirectory, "AudioCovers");
                        Directory.CreateDirectory(coversDir);
                        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
                        var cachedCover = Path.Combine(coversDir, $"{hash}.jpg");
                        if (!File.Exists(cachedCover))
                        {
                            File.WriteAllBytes(cachedCover, bytes);
                        }
                        coverPath = cachedCover;
                    }
                }
            }
            catch
            {
                // 若音频标签损坏仍保留文件名与文件大小信息
            }

            var pathHash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(filePath.ToLowerInvariant())))[..16];
            return new SongItem
            {
                Id = $"local_{pathHash}",
                Name = title,
                PicUrl = coverPath,
                Artists = [new ArtistItem { Name = artistName }],
                Album = new AlbumItem { Name = albumName, PicUrl = coverPath },
                DurationMs = durationMs,
                ChannelCount = channelCount,
                FileSize = fi.Length,
                ModifiedTime = fi.LastWriteTimeUtc.Ticks,
                EmbeddedLyric = embeddedLyric,
                Source = "local",
                LocalFilePath = filePath,
                ResolvedPlatform = "local"
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 按最长前缀匹配构建多根目录本地音乐文件夹树（1:1 对应 src/renderer/utils/localMusicTree.ts buildLocalMusicFolderTree）
    /// </summary>
    public List<LocalMusicFolderNode> BuildLocalMusicFolderTree(IReadOnlyList<SongItem> songs, IReadOnlyList<string>? customRoots = null)
    {
        var roots = (customRoots ?? GetConfiguredRootFolders())
            .Select(NormalizeFolderPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rootNodes = new List<LocalMusicFolderNode>();
        var nodeMap = new Dictionary<string, LocalMusicFolderNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var rootPath in roots)
        {
            var rootName = Path.GetFileName(rootPath);
            if (string.IsNullOrWhiteSpace(rootName)) rootName = rootPath;

            var node = new LocalMusicFolderNode
            {
                Key = rootPath.ToLowerInvariant(),
                Path = rootPath,
                Name = rootName,
                Level = 0,
                IsExpanded = true
            };
            rootNodes.Add(node);
            nodeMap[node.Key] = node;
        }

        foreach (var song in songs)
        {
            if (string.IsNullOrWhiteSpace(song.LocalFilePath)) continue;
            var dirPath = NormalizeFolderPath(Path.GetDirectoryName(song.LocalFilePath));
            if (string.IsNullOrWhiteSpace(dirPath)) continue;

            // 最长前缀匹配所属根目录，避免父子目录同时配置时歌曲重复归属
            string? matchedRoot = roots
                .Where(r => IsSameOrSubPath(dirPath, r))
                .OrderByDescending(r => r.Length)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(matchedRoot))
            {
                matchedRoot = dirPath;
                var fallbackKey = matchedRoot.ToLowerInvariant();
                if (!nodeMap.ContainsKey(fallbackKey))
                {
                    var fallbackName = Path.GetFileName(matchedRoot);
                    if (string.IsNullOrWhiteSpace(fallbackName)) fallbackName = matchedRoot;
                    var fallbackNode = new LocalMusicFolderNode
                    {
                        Key = fallbackKey,
                        Path = matchedRoot,
                        Name = fallbackName,
                        Level = 0,
                        IsExpanded = true
                    };
                    rootNodes.Add(fallbackNode);
                    nodeMap[fallbackKey] = fallbackNode;
                }
            }

            var targetNode = EnsureFolderChain(matchedRoot, dirPath, nodeMap);
            targetNode.Songs.Add(song);
        }

        foreach (var root in rootNodes)
        {
            SortFolderNodeRecursively(root);
        }

        return rootNodes;
    }

    private static LocalMusicFolderNode EnsureFolderChain(
        string rootPath,
        string targetDirPath,
        Dictionary<string, LocalMusicFolderNode> nodeMap)
    {
        var currentNode = nodeMap[rootPath.ToLowerInvariant()];
        if (string.Equals(rootPath, targetDirPath, StringComparison.OrdinalIgnoreCase))
        {
            return currentNode;
        }

        var relative = targetDirPath[rootPath.Length..].TrimStart('\\');
        var segments = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var currentPath = rootPath;

        foreach (var segment in segments)
        {
            currentPath = Path.Combine(currentPath, segment);
            var key = currentPath.ToLowerInvariant();
            if (!nodeMap.TryGetValue(key, out var childNode))
            {
                childNode = new LocalMusicFolderNode
                {
                    Key = key,
                    Path = currentPath,
                    Name = segment,
                    Level = currentNode.Level + 1,
                    IsExpanded = false
                };
                currentNode.Children.Add(childNode);
                nodeMap[key] = childNode;
            }
            currentNode = childNode;
        }

        return currentNode;
    }

    private static void SortFolderNodeRecursively(LocalMusicFolderNode node)
    {
        node.Songs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        node.Children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        foreach (var child in node.Children)
        {
            SortFolderNodeRecursively(child);
        }
    }

    /// <summary>
    /// 将树形结构按当前展开状态展平为 ListView 可直接渲染的扁平列表
    /// </summary>
    public static List<LocalMusicFolderNode> FlattenVisibleFolderTree(IEnumerable<LocalMusicFolderNode> roots)
    {
        var list = new List<LocalMusicFolderNode>();
        foreach (var root in roots)
        {
            FlattenNodeRecursive(root, list);
        }
        return list;
    }

    private static void FlattenNodeRecursive(LocalMusicFolderNode node, List<LocalMusicFolderNode> output)
    {
        output.Add(node);
        if (node.IsExpanded)
        {
            foreach (var child in node.Children)
            {
                FlattenNodeRecursive(child, output);
            }
        }
    }

    /// <summary>
    /// 展开目标文件夹节点的所有祖先节点并返回该节点（用于「定位当前播放歌曲」）
    /// </summary>
    public static LocalMusicFolderNode? ExpandAndFindNodeByPath(IEnumerable<LocalMusicFolderNode> roots, string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return null;
        var targetKey = NormalizeFolderPath(folderPath).ToLowerInvariant();

        foreach (var root in roots)
        {
            var found = ExpandAndSearchRecursive(root, targetKey);
            if (found != null) return found;
        }
        return null;
    }

    private static LocalMusicFolderNode? ExpandAndSearchRecursive(LocalMusicFolderNode current, string targetKey)
    {
        if (string.Equals(current.Key, targetKey, StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        foreach (var child in current.Children)
        {
            var found = ExpandAndSearchRecursive(child, targetKey);
            if (found != null)
            {
                current.IsExpanded = true;
                return found;
            }
        }

        return null;
    }

    private static bool IsSameOrSubPath(string candidatePath, string rootPath)
    {
        if (string.Equals(candidatePath, rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var prefix = rootPath.EndsWith('\\') ? rootPath : rootPath + "\\";
        return candidatePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SharedReadFileAbstraction(string filePath) : TagLib.File.IFileAbstraction
    {
        public string Name => filePath;

        public Stream ReadStream => new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 32768);

        public Stream WriteStream => throw new NotSupportedException("只读扫描不支持写入流");

        public void CloseStream(Stream stream)
        {
            stream?.Dispose();
        }
    }
}
