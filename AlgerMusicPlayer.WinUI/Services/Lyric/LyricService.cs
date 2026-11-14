using System.Text.Json;
using System.Text.RegularExpressions;
using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.Api;
using AlgerMusicPlayer.WinUI.Services.System;

namespace AlgerMusicPlayer.WinUI.Services.Lyric;

/// <summary>
/// 歌词解析与本地缓存服务（支持标准 LRC、网易云 YRC 逐字歌词、翻译与罗马音对齐）
/// </summary>
public partial class LyricService
{
    private readonly NeteaseApiClient _apiClient;
    private readonly QqMusicService _qqMusicService;
    private readonly ConfigService _configService;

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:\.(\d{1,3}))?\]")]
    private static partial Regex LrcTimeRegex();

    [GeneratedRegex(@"^\[(\d+),(\d+)\](.*)$")]
    private static partial Regex YrcLineRegex();

    [GeneratedRegex(@"\((\d+),(\d+),\d+\)([^\(\r\n]*)")]
    private static partial Regex YrcWordRegex();

    public LyricService(NeteaseApiClient apiClient, QqMusicService qqMusicService, ConfigService configService)
    {
        _apiClient = apiClient;
        _qqMusicService = qqMusicService;
        _configService = configService;
    }

    /// <summary>
    /// 获取并解析歌曲歌词（优先读取内嵌歌词、同名 .lrc、本地缓存、QQ音乐歌词或网易云歌词）
    /// </summary>
    public async Task<ParsedLyric> GetParsedLyricAsync(SongItem song)
    {
        if (song == null || string.IsNullOrWhiteSpace(song.Id))
        {
            return CreateFallbackLyric("暂无播放歌曲");
        }

        // 本地音乐优先读取音频标签内嵌歌词或同名 .lrc 文件
        if (!string.IsNullOrWhiteSpace(song.EmbeddedLyric))
        {
            var embeddedParsed = ParseLyric(song.Id, song.EmbeddedLyric, string.Empty, string.Empty, string.Empty);
            if (embeddedParsed.Lines.Count > 0) return embeddedParsed;
        }

        if (!string.IsNullOrWhiteSpace(song.LocalFilePath) && File.Exists(song.LocalFilePath))
        {
            var localLrcPath = Path.ChangeExtension(song.LocalFilePath, ".lrc");
            if (File.Exists(localLrcPath))
            {
                var rawLocal = await File.ReadAllTextAsync(localLrcPath);
                return ParseLyric(song.Id, rawLocal, string.Empty, string.Empty, string.Empty);
            }
        }

        // QQ 音乐曲目获取 QQ 音乐官方歌词
        if (!string.IsNullOrWhiteSpace(song.QqSongMid))
        {
            var (qqLrc, qqTrans) = await _qqMusicService.GetLyricAsync(song.QqSongMid);
            if (!string.IsNullOrWhiteSpace(qqLrc))
            {
                return ParseLyric(song.Id, qqLrc, qqTrans, string.Empty, string.Empty);
            }
        }

        // 咪咕曲目自带直链歌词
        if (!string.IsNullOrWhiteSpace(song.MiguLrcUrl) && song.MiguLrcUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var miguLrc = await http.GetStringAsync(song.MiguLrcUrl);
                var miguParsed = ParseLyric(song.Id, miguLrc, string.Empty, string.Empty, string.Empty);
                if (miguParsed.Lines.Count > 0) return miguParsed;
            }
            catch
            {
            }
        }

        bool isInternational = song.Source is "spotify" or "apple" or "deezer" or "tidal" or "qobuz" or "ytmusic";
        if (isInternational)
        {
            var lrcLibParsed = await TryFetchLrcLibAsync(song);
            if (lrcLibParsed != null && lrcLibParsed.Lines.Count > 0)
            {
                return lrcLibParsed;
            }
        }

        long targetNeteaseId = song.NumericId;
        if (targetNeteaseId <= 0 && !string.IsNullOrWhiteSpace(song.Name))
        {
            // 第三方音源歌曲自动匹配网易云同名歌曲歌词（支持翻译与 YRC 逐字歌词）
            try
            {
                var kw = $"{song.Name} {song.ArtistText}".Trim();
                var matches = await _apiClient.SearchSongsAsync(kw, limit: 3);
                var matched = isInternational
                    ? matches.FirstOrDefault(m => IsRoughTitleMatch(song.Name, m.Name))
                    : matches.FirstOrDefault();
                if (matched != null && matched.NumericId > 0)
                {
                    targetNeteaseId = matched.NumericId;
                }
            }
            catch
            {
            }
        }

        if (targetNeteaseId <= 0)
        {
            if (!isInternational)
            {
                var lrcLibFallback = await TryFetchLrcLibAsync(song);
                if (lrcLibFallback != null && lrcLibFallback.Lines.Count > 0)
                {
                    return lrcLibFallback;
                }
            }
            return CreateFallbackLyric("纯音乐或外部音源，请欣赏");
        }

        var cachePath = Path.Combine(_configService.CacheDirectory, $"lyric_{targetNeteaseId}.json");
        if (File.Exists(cachePath))
        {
            try
            {
                var cachedJson = await File.ReadAllTextAsync(cachePath);
                var cached = JsonSerializer.Deserialize<ParsedLyric>(cachedJson);
                if (cached != null && cached.Lines.Count > 0) return cached;
            }
            catch
            {
                // 缓存损坏重新拉取
            }
        }

        var (lrc, tlrc, roma, yrc) = await _apiClient.GetLyricRawAsync(targetNeteaseId);
        var parsed = ParseLyric(song.Id, lrc, tlrc, roma, yrc);

        if (parsed.Lines.Count > 0)
        {
            try
            {
                await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(parsed));
            }
            catch
            {
                // 忽略缓存写入错误
            }
            return parsed;
        }

        if (!isInternational)
        {
            var lrcLibSecondary = await TryFetchLrcLibAsync(song);
            if (lrcLibSecondary != null && lrcLibSecondary.Lines.Count > 0)
            {
                return lrcLibSecondary;
            }
        }

        return parsed;
    }

    private static bool IsRoughTitleMatch(string targetTitle, string candidateTitle)
    {
        static string Normalize(string s)
        {
            var sb = new global::System.Text.StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        var a = Normalize(targetTitle);
        var b = Normalize(candidateTitle);
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return a == b || a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);
    }

    /// <summary>
    /// 从 LRCLIB 公共同步歌词库拉取国际/第三方歌曲歌词并写入本地缓存
    /// </summary>
    private async Task<ParsedLyric?> TryFetchLrcLibAsync(SongItem song)
    {
        if (string.IsNullOrWhiteSpace(song.Name)) return null;

        var idBytes = global::System.Security.Cryptography.MD5.HashData(global::System.Text.Encoding.UTF8.GetBytes(song.Id));
        var hash = Convert.ToHexString(idBytes).ToLowerInvariant();
        var cachePath = Path.Combine(_configService.CacheDirectory, $"lyric_ext_{hash}.json");
        if (File.Exists(cachePath))
        {
            try
            {
                var cachedJson = await File.ReadAllTextAsync(cachePath);
                var cached = JsonSerializer.Deserialize<ParsedLyric>(cachedJson);
                if (cached != null && cached.Lines.Count > 0) return cached;
            }
            catch
            {
            }
        }

        var firstArtist = song.Artists.FirstOrDefault()?.Name?.Trim() ?? string.Empty;
        if (firstArtist is "未知歌手" or "Unknown Artist") firstArtist = string.Empty;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AlgerMusicPlayer/1.0");

            string? bestRawLrc = null;
            var trackEsc = Uri.EscapeDataString(song.Name.Trim());
            var artistEsc = Uri.EscapeDataString(firstArtist);

            var getUrl = string.IsNullOrWhiteSpace(firstArtist)
                ? $"https://lrclib.net/api/get?track_name={trackEsc}"
                : $"https://lrclib.net/api/get?track_name={trackEsc}&artist_name={artistEsc}";

            using (var resp = await http.GetAsync(getUrl))
            {
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    bestRawLrc = doc.RootElement.GetStringProp("syncedLyrics");
                    if (string.IsNullOrWhiteSpace(bestRawLrc))
                    {
                        bestRawLrc = doc.RootElement.GetStringProp("plainLyrics");
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(bestRawLrc))
            {
                var searchUrl = string.IsNullOrWhiteSpace(firstArtist)
                    ? $"https://lrclib.net/api/search?track_name={trackEsc}"
                    : $"https://lrclib.net/api/search?track_name={trackEsc}&artist_name={artistEsc}";
                using var sResp = await http.GetAsync(searchUrl);
                if (sResp.IsSuccessStatusCode)
                {
                    var json = await sResp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        string? plainFallback = null;
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            var synced = item.GetStringProp("syncedLyrics");
                            if (!string.IsNullOrWhiteSpace(synced))
                            {
                                bestRawLrc = synced;
                                break;
                            }
                            if (string.IsNullOrWhiteSpace(plainFallback))
                            {
                                plainFallback = item.GetStringProp("plainLyrics");
                            }
                        }
                        if (string.IsNullOrWhiteSpace(bestRawLrc)) bestRawLrc = plainFallback;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(bestRawLrc)) return null;

            var parsed = ParseLyric(song.Id, bestRawLrc, string.Empty, string.Empty, string.Empty);
            if (parsed.Lines.Count > 0)
            {
                try
                {
                    await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(parsed));
                }
                catch
                {
                }
                return parsed;
            }
        }
        catch
        {
        }

        return null;
    }

    public static ParsedLyric ParseLyric(string songId, string lrc, string tlrc, string romaLrc, string yrc)
    {
        var result = new ParsedLyric
        {
            SongId = songId,
            RawLrc = lrc,
            RawTranslatedLrc = tlrc
        };

        var lines = new List<LyricLine>();

        // 优先解析 YRC 逐字歌词
        if (!string.IsNullOrWhiteSpace(yrc))
        {
            lines = ParseYrcLines(yrc);
            if (lines.Count > 0)
            {
                result.HasWordByWord = true;
            }
        }

        // 若无 YRC，解析标准 LRC
        if (lines.Count == 0 && !string.IsNullOrWhiteSpace(lrc))
        {
            lines = ParseLrcLines(lrc);
        }

        if (lines.Count == 0)
        {
            return CreateFallbackLyric("纯音乐，请欣赏");
        }

        // 解析翻译歌词并按最接近的时间戳匹配
        if (!string.IsNullOrWhiteSpace(tlrc))
        {
            var transMap = ParseLrcLines(tlrc);
            foreach (var tLine in transMap)
            {
                var target = lines
                    .OrderBy(l => Math.Abs(l.StartTimeMs - tLine.StartTimeMs))
                    .FirstOrDefault(l => Math.Abs(l.StartTimeMs - tLine.StartTimeMs) < 1200);
                if (target != null && string.IsNullOrWhiteSpace(target.TranslationText))
                {
                    target.TranslationText = tLine.Text;
                    result.HasTranslation = true;
                }
            }
        }

        // 解析罗马音并按最接近的时间戳匹配
        if (!string.IsNullOrWhiteSpace(romaLrc))
        {
            var romaMap = ParseLrcLines(romaLrc);
            foreach (var rLine in romaMap)
            {
                var target = lines
                    .OrderBy(l => Math.Abs(l.StartTimeMs - rLine.StartTimeMs))
                    .FirstOrDefault(l => Math.Abs(l.StartTimeMs - rLine.StartTimeMs) < 1200);
                if (target != null && string.IsNullOrWhiteSpace(target.RomajiText))
                {
                    target.RomajiText = rLine.Text;
                    result.HasRomaji = true;
                }
            }
        }

        // 排序并补齐每行时长与序号
        lines = lines.OrderBy(l => l.StartTimeMs).ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            lines[i].Index = i;
            if (lines[i].DurationMs <= 0)
            {
                lines[i].DurationMs = i < lines.Count - 1
                    ? Math.Max(500, lines[i + 1].StartTimeMs - lines[i].StartTimeMs)
                    : 5000;
            }
        }

        result.Lines = lines;
        return result;
    }

    private static List<LyricLine> ParseLrcLines(string lrcText)
    {
        var list = new List<LyricLine>();
        var rawLines = lrcText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var raw in rawLines)
        {
            var matches = LrcTimeRegex().Matches(raw);
            if (matches.Count == 0) continue;

            var lastMatch = matches[^1];
            var content = raw[(lastMatch.Index + lastMatch.Length)..].Trim();
            if (string.IsNullOrWhiteSpace(content)) continue;

            foreach (Match m in matches)
            {
                int min = int.Parse(m.Groups[1].Value);
                int sec = int.Parse(m.Groups[2].Value);
                int ms = 0;
                if (m.Groups[3].Success)
                {
                    var frac = m.Groups[3].Value;
                    ms = frac.Length switch
                    {
                        1 => int.Parse(frac) * 100,
                        2 => int.Parse(frac) * 10,
                        _ => int.Parse(frac[..3])
                    };
                }

                double totalMs = (min * 60 + sec) * 1000.0 + ms;
                list.Add(new LyricLine
                {
                    StartTimeMs = totalMs,
                    Text = content
                });
            }
        }
        return list;
    }

    private static List<LyricLine> ParseYrcLines(string yrcText)
    {
        var list = new List<LyricLine>();
        var rawLines = yrcText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var raw in rawLines)
        {
            var lineMatch = YrcLineRegex().Match(raw.Trim());
            if (!lineMatch.Success) continue;

            double lineStart = double.Parse(lineMatch.Groups[1].Value);
            double lineDuration = double.Parse(lineMatch.Groups[2].Value);
            var body = lineMatch.Groups[3].Value;

            var words = new List<LyricWord>();
            var wordMatches = YrcWordRegex().Matches(body);
            foreach (Match wm in wordMatches)
            {
                words.Add(new LyricWord
                {
                    StartTimeMs = double.Parse(wm.Groups[1].Value),
                    DurationMs = double.Parse(wm.Groups[2].Value),
                    Text = wm.Groups[3].Value
                });
            }

            var fullText = string.Concat(words.Select(w => w.Text)).Trim();
            if (string.IsNullOrWhiteSpace(fullText)) continue;

            list.Add(new LyricLine
            {
                StartTimeMs = lineStart,
                DurationMs = lineDuration,
                Text = fullText,
                Words = words
            });
        }
        return list;
    }

    private static ParsedLyric CreateFallbackLyric(string text)
    {
        return new ParsedLyric
        {
            Lines =
            [
                new LyricLine
                {
                    Index = 0,
                    StartTimeMs = 0,
                    DurationMs = 60000,
                    Text = text,
                    IsCurrent = true
                }
            ]
        };
    }
}
