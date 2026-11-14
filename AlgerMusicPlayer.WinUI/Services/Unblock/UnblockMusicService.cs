using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.Api;
using AlgerMusicPlayer.WinUI.Services.System;

namespace AlgerMusicPlayer.WinUI.Services.Unblock;

/// <summary>
/// 纯 C# 多音源解灰与匹配服务（对应原 @unblockneteasemusic/server 及单曲自定义音源选择）
/// 支持平台：pyncmd、kuwo (酷我)、kugou (酷狗)、migu (咪咕) 及自定义音源接口
/// </summary>
public class UnblockMusicService
{
    private readonly ConfigService _configService;
    private readonly HttpClient _httpClient;

    public static readonly string[] AllPlatforms = ["kuwo", "kugou", "migu", "pyncmd", "qq", "joox", "gdmusic"];

    private const string GdStudioHost = "music.gdstudio.org";
    private const string GdStudioHomeUrl = "https://music.gdstudio.org/";
    private const string GdStudioBaseUrl = "https://music.gdstudio.org/api.php";
    private const string GdStudioTimeUrl = "https://music.gdstudio.org/time";
    private const string GdStudioPlayerJsUrl = "https://music.gdstudio.org/js/player.js";

    private string _gdStudioVersion = "20261111";
    private DateTimeOffset _gdVersionLastSync = DateTimeOffset.MinValue;
    private long _gdServerTimeOffsetSec;
    private DateTimeOffset _gdServerTimeLastSync = DateTimeOffset.MinValue;
    private bool _useDynamicGdMd5;
    private string _gdCrcJsCode = string.Empty;
    private string _gdCrcTag = string.Empty;
    private readonly SemaphoreSlim _gdSyncLock = new(1, 1);

    public int LastResolvedBitrateKbps { get; private set; }
    public long LastResolvedSizeBytes { get; private set; }
    public string LastResolvedSourceName { get; private set; } = string.Empty;

    public (IReadOnlyList<string> SupportedPlatforms, string QualitySummary) GetScriptCapabilitiesSummary(LxScriptItem script)
        => _lxHost.GetScriptCapabilitiesSummary(script);

    public void PreWarmLxScripts(IEnumerable<LxScriptItem> scripts)
        => _lxHost.PreWarmScripts(scripts);

    public static string MapUserQualityToLxQuality(string? userQuality) =>
        (userQuality ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            MusicQualityLevel.Standard or MusicQualityLevel.Higher => "128k",
            MusicQualityLevel.Exhigh => "320k",
            MusicQualityLevel.Lossless or MusicQualityLevel.Jyeffect or MusicQualityLevel.Sky => "flac",
            MusicQualityLevel.Hires or MusicQualityLevel.Jymaster => "flac24bit",
            _ => "flac24bit"
        };

    public HashSet<string> GetSupportedLxQualities(SongItem song)
        => _lxHost.GetSupportedQualitiesForSong(_configService.Settings.LxMusicScripts, song);

    public string FormatResolvedQualityBadge(string url, SongItem song, string requestedQuality)
    {
        bool isFlac = url.Contains(".flac", StringComparison.OrdinalIgnoreCase);
        int kbps = LastResolvedBitrateKbps;
        long size = LastResolvedSizeBytes;

        // 若返回码率标记为 999 且带有真实文件大小与歌曲时长，按真实体积反推实际码率（kbps）
        if ((kbps == 0 || kbps == 999) && size > 0 && song.DurationMs > 2000)
        {
            int estimated = (int)(size * 8L / song.DurationMs);
            if (estimated is >= 128 and <= 9000)
            {
                kbps = estimated;
            }
        }

        if (isFlac || kbps > 400)
        {
            // 码率 >= 1350kbps 或单曲体积 > 42MB 为 24bit Hi-Res / 超清母带级无损
            if (kbps >= 1350 || size > 42L * 1024 * 1024)
            {
                var label = string.Equals(requestedQuality, MusicQualityLevel.Jymaster, StringComparison.OrdinalIgnoreCase)
                    ? "超清母带"
                    : "Hi-Res";
                return kbps > 0 && kbps != 999 ? $"{label} {kbps}k" : $"{label} 24bit";
            }

            return kbps > 0 && kbps != 999 ? $"无损 {kbps}k" : "无损 FLAC";
        }

        if (kbps is > 0 and < 160)
        {
            return $"标准 {kbps}k";
        }
        if (kbps is >= 160 and < 240)
        {
            return $"较高 {kbps}k";
        }

        return kbps is > 0 and <= 320 ? $"极高 {kbps}k" : "极高 320k";
    }

    // GD 音乐台 crc32.min.js?v=20261112 自定义 MD5 常量表（T[1] 由标准 0xe8c7b756 修改为 0x21c10959；遇 401 时将通过离屏宿主自动同步最新脚本）
    private static readonly uint[] GdMd5T =
    [
        0xd76aa478, 0x21c10959, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
        0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
        0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
        0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
        0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
        0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
        0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391
    ];

    private static readonly int[] GdMd5S =
    [
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5,  9, 14, 20, 5,  9, 14, 20, 5,  9, 14, 20, 5,  9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21
    ];

    private readonly LxScriptHostService _lxHost;
    private readonly HttpClient _downloadHttpClient;

    public UnblockMusicService(ConfigService configService, LxScriptHostService lxHost)
    {
        _configService = configService;
        _lxHost = lxHost;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        _downloadHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        _downloadHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");

        // 启动时后台静默预热 GDStudio 服务器时间与版本号，消除首次切歌 2 秒冷启动同步耗时
        _ = Task.Run(() => EnsureGdStudioReadyAsync(CancellationToken.None));
    }

    /// <summary>
    /// 按启用平台顺序（或指定单一平台）解析无版权/试听/无损歌曲的可播放或可下载 URL（真并发竞速）
    /// </summary>
    public async Task<(string? Url, string Platform)> MatchAudioUrlAsync(
        SongItem song,
        IEnumerable<string>? preferredPlatforms = null,
        CancellationToken cancellationToken = default,
        bool preferLossless = true,
        string? targetQualityLevel = null,
        bool allowFastStart = false)
    {
        LastResolvedBitrateKbps = 0;
        LastResolvedSizeBytes = 0;
        LastResolvedSourceName = string.Empty;

        var platforms = preferredPlatforms?.ToList()
            ?? (_configService.Settings.EnabledMusicSources.Count > 0
                ? _configService.Settings.EnabledMusicSources.ToList()
                : AllPlatforms.ToList());

        bool hasEnabledLx = _configService.Settings.LxMusicScripts.Any(x => x.Enabled);

        // 仅当显式多源自由调度时剔除注定空跑的通道，提升竞速有效率
        if (preferredPlatforms == null)
        {
            if (song.NumericId <= 0)
            {
                platforms.RemoveAll(p => p.Equals("pyncmd", StringComparison.OrdinalIgnoreCase));
            }
            if (!hasEnabledLx)
            {
                platforms.RemoveAll(p => p.Equals("lxmusic", StringComparison.OrdinalIgnoreCase));
            }
        }

        // 若请求无损且未强制指定单一音源，保持落雪与 GD 多源在清单中并参与并发竞速
        if (preferLossless && preferredPlatforms == null)
        {
            platforms.RemoveAll(p => p.Equals("gdmusic", StringComparison.OrdinalIgnoreCase) ||
                                     p.Equals("pyncmd", StringComparison.OrdinalIgnoreCase) ||
                                     (hasEnabledLx && p.Equals("lxmusic", StringComparison.OrdinalIgnoreCase)));
            if (song.NumericId > 0) platforms.Insert(0, "pyncmd");
            platforms.Insert(0, "gdmusic");
            if (hasEnabledLx) platforms.Insert(0, "lxMusic");
        }

        // 去重（不区分大小写）
        platforms = platforms
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 若配置了自定义 Unblock HTTP 服务地址（且未限定于特定非 custom 平台），优先尝试或参与竞速
        bool allowCustomFastCheck = !string.IsNullOrWhiteSpace(_configService.Settings.CustomUnblockApiUrl) &&
                                    (preferredPlatforms == null || platforms.Any(p => p.Equals("custom", StringComparison.OrdinalIgnoreCase)));
        if (allowCustomFastCheck)
        {
            var customUrl = await TryCustomEndpointAsync(song, cancellationToken);
            if (!string.IsNullOrWhiteSpace(customUrl))
            {
                LastResolvedSourceName = "custom";
                return (customUrl, "custom");
            }
            platforms.RemoveAll(p => p.Equals("custom", StringComparison.OrdinalIgnoreCase));
        }

        if (platforms.Count == 0)
        {
            return (null, string.Empty);
        }

        // 真正 Task.WhenAny 并发竞速：谁先返回合格音频流立刻采用并取消其余慢请求，消除队头阻塞
        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallCts.CancelAfter(TimeSpan.FromSeconds(10));
        var pending = platforms
            .Select(p => ResolveSinglePlatformAsync(song, p, preferLossless, targetQualityLevel, overallCts.Token))
            .ToList();

        (string Url, string Platform, ResolveInfo Info)? fallback = null;
        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending);
            pending.Remove(finished);

            (string Url, string Platform, ResolveInfo Info)? result = null;
            try
            {
                result = await finished;
            }
            catch
            {
                continue;
            }

            if (result == null) continue;

            bool isLosslessResult = result.Value.Url.Contains(".flac", StringComparison.OrdinalIgnoreCase) ||
                                    result.Value.Info.Kbps > 400;
            if (!preferLossless || isLosslessResult || allowFastStart)
            {
                overallCts.Cancel();
                LastResolvedBitrateKbps = result.Value.Info.Kbps;
                LastResolvedSizeBytes = result.Value.Info.Size;
                var resolvedPlat = !string.IsNullOrWhiteSpace(result.Value.Info.SourceDetailName)
                    ? result.Value.Info.SourceDetailName
                    : result.Value.Platform;
                LastResolvedSourceName = resolvedPlat;
                return (result.Value.Url, resolvedPlat);
            }

            if (fallback == null || result.Value.Info.Kbps > fallback.Value.Info.Kbps)
            {
                bool firstLossyHit = fallback == null;
                fallback = result;
                // 无损优先但不想先播有损时，拿到首个可播有损后最多再等 2.2 秒让无损超车
                if (firstLossyHit)
                {
                    overallCts.CancelAfter(TimeSpan.FromMilliseconds(2200));
                }
            }
        }

        if (fallback != null)
        {
            LastResolvedBitrateKbps = fallback.Value.Info.Kbps;
            LastResolvedSizeBytes = fallback.Value.Info.Size;
            var fallbackPlat = !string.IsNullOrWhiteSpace(fallback.Value.Info.SourceDetailName)
                ? fallback.Value.Info.SourceDetailName
                : fallback.Value.Platform;
            LastResolvedSourceName = fallbackPlat;
            return (fallback.Value.Url, fallbackPlat);
        }

        return (null, string.Empty);
    }

    // 并发解析时每个平台独立记录码率/体积与具体脚本名称，避免互相覆盖 LastResolved* 字段
    private sealed class ResolveInfo
    {
        public int Kbps;
        public long Size;
        public string SourceDetailName = string.Empty;
    }

    private readonly AsyncLocal<ResolveInfo?> _resolveInfo = new();

    private (int Kbps, long Size) GetCurrentResolvedInfo()
    {
        var info = _resolveInfo.Value;
        return info != null ? (info.Kbps, info.Size) : (LastResolvedBitrateKbps, LastResolvedSizeBytes);
    }

    private void SetResolvedInfo(int kbps, long size, string? detailName = null)
    {
        if (!string.IsNullOrWhiteSpace(detailName))
        {
            LastResolvedSourceName = detailName;
        }
        var info = _resolveInfo.Value;
        if (info != null)
        {
            info.Kbps = kbps;
            info.Size = size;
            if (!string.IsNullOrWhiteSpace(detailName))
            {
                info.SourceDetailName = detailName;
            }
        }
        else
        {
            LastResolvedBitrateKbps = kbps;
            LastResolvedSizeBytes = size;
        }
    }

    /// <summary>
    /// 解析单个平台并校验结果是否为完整歌曲（异常或不合格返回 null）
    /// </summary>
    private async Task<(string Url, string Platform, ResolveInfo Info)?> ResolveSinglePlatformAsync(
        SongItem song, string platform, bool preferLossless, string? targetQualityLevel, CancellationToken ct)
    {
        var info = new ResolveInfo();
        _resolveInfo.Value = info;
        try
        {
            string? url = platform.ToLowerInvariant() switch
            {
                "pyncmd" => await TryPyncmdAsync(song, preferLossless, ct, targetQualityLevel),
                "kuwo" => await TryKuwoAsync(song, preferLossless, ct, targetQualityLevel),
                "kugou" => await TryKugouAsync(song, preferLossless, ct, targetQualityLevel),
                "migu" => await TryGdStudioSourceAsync(song, "migu", preferLossless, ct, targetQualityLevel),
                "qq" => await TryGdStudioSourceAsync(song, "tencent", preferLossless, ct, targetQualityLevel),
                "joox" => await TryGdStudioSourceAsync(song, "joox", preferLossless, ct, targetQualityLevel),
                "gdmusic" => await TryGdMusicMultiSourceAsync(song, preferLossless, ct, targetQualityLevel),
                "lxmusic" => await TryLxMusicAsync(song, preferLossless, ct, targetQualityLevel),
                "custom" => await TryCustomEndpointAsync(song, ct),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(url)) return null;
            if (!await IsLikelyFullTrackAsync(url, song, ct, info.Size, info.Kbps)) return null;
            return (url, platform, info);
        }
        catch
        {
            // 单个音源失败不影响其他平台
            return null;
        }
    }

    /// <summary>
    /// 通过文件体积粗略校验是否为完整歌曲：
    /// 酷我/酷狗等对受限歌曲会返回几秒钟的“请使用客户端收听”提示音，部分音源会返回 30 秒试听片段，二者体积远小于完整歌曲
    /// </summary>
    private async Task<bool> IsLikelyFullTrackAsync(
        string url, SongItem song, CancellationToken ct, long knownSizeBytes = 0, int knownKbps = 0)
    {
        // 无可靠时长（或本身就是短音频）时不做校验
        if (song.DurationMs < 60_000) return true;

        var currentInfo = GetCurrentResolvedInfo();
        if (knownSizeBytes <= 0) knownSizeBytes = currentInfo.Size;
        if (knownKbps <= 0) knownKbps = currentInfo.Kbps;

        bool isFlac = url.Contains(".flac", StringComparison.OrdinalIgnoreCase) || knownKbps > 400;
        int assumedKbps = isFlac ? 600 : 128;
        long minBytes = (long)(song.DurationMs / 1000.0 * assumedKbps * 125 * 0.4);

        // 若上游接口已明确返回真实完整文件字节数且不是需二次跳转校错的 GD 代理路径，直接凭已知体积秒判（省去 1 次 HTTP 往返）
        if (knownSizeBytes > 0 && !url.Contains("music-proxy.gdstudio.org", StringComparison.OrdinalIgnoreCase))
        {
            return knownSizeBytes >= minBytes;
        }

        long size;
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(2));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new global::System.Net.Http.Headers.RangeHeaderValue(0, 0);
            if (url.Contains(GdStudioHost, StringComparison.OrdinalIgnoreCase))
            {
                req.Headers.Referrer = new Uri(GdStudioHomeUrl);
            }
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, probeCts.Token);

            // 拦截 400 / 401 / 403 / 404 / 5xx 等所有无效状态（如 music-proxy 返回 400 Invalid target URL 或 CDN 403）
            if ((int)resp.StatusCode >= 400)
            {
                return false;
            }

            size = resp.Content.Headers.ContentRange?.Length
                   ?? (resp.StatusCode == global::System.Net.HttpStatusCode.OK ? resp.Content.Headers.ContentLength ?? 0 : 0);
        }
        catch
        {
            // 整体已取消则视为不合格；单纯探测超时/失败时不阻断播放
            return !ct.IsCancellationRequested;
        }

        if (size <= 0) return true;
        return size >= minBytes;
    }

    /// <summary>
    /// 获取酷狗搜索热词建议（对应原 otherApi.ts 的 get-search-suggestions）
    /// </summary>
    public async Task<List<string>> GetSearchSuggestionsAsync(string keyword)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(keyword)) return list;

        try
        {
            var url = $"http://msearchcdn.kugou.com/new/app/i/search.php?cmd=302&keyword={Uri.EscapeDataString(keyword.Trim())}";
            var json = await _httpClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var str = item.GetStringProp("keyword");
                    if (!string.IsNullOrWhiteSpace(str)) list.Add(str);
                }
            }
        }
        catch
        {
            // 忽略网络错误
        }
        return list;
    }

    /// <summary>
    /// PyNCMD / GDStudio 新版直连与 Meting 镜像源解析（通过网易云歌曲 ID 直接获取 24bit Hi-Res / 16bit FLAC br=999 或指定音质音频流）
    /// </summary>
    private async Task<string?> TryPyncmdAsync(SongItem song, bool preferLossless, CancellationToken ct, string? targetQualityLevel = null)
    {
        if (song.NumericId <= 0) return null;

        // 1. 优先调用 GD 音乐台最新签名接口 (music.gdstudio.org/api.php) 直接按网易云 ID 获取指定/最高音质直链
        var gdDirectUrl = await TryGdStudioDirectUrlByIdAsync(song.NumericId.ToString(), "netease", preferLossless, ct, targetQualityLevel);
        if (!string.IsNullOrWhiteSpace(gdDirectUrl))
        {
            return gdDirectUrl;
        }

        // 2. 尝试落月 API (api.vkeys.cn) 网易云解析
        var vkeysUrl = await TryVkeysNeteaseAsync(song.NumericId, preferLossless, ct, targetQualityLevel);
        if (!string.IsNullOrWhiteSpace(vkeysUrl))
        {
            return vkeysUrl;
        }

        // 3. 尝试 Meting 公共节点兜底
        var endpoints = new[]
        {
            $"https://api.injahow.cn/meting/?type=url&id={song.NumericId}",
            $"https://api.i-meto.com/meting/api?server=netease&type=url&id={song.NumericId}"
        };

        foreach (var endpoint in endpoints)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode is global::System.Net.HttpStatusCode.Moved or global::System.Net.HttpStatusCode.Redirect)
                {
                    var redirect = resp.Headers.Location?.ToString();
                    if (!string.IsNullOrWhiteSpace(redirect)) return redirect;
                }

                var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (contentType.Contains("audio"))
                {
                    return resp.RequestMessage?.RequestUri?.ToString();
                }

                var text = await resp.Content.ReadAsStringAsync(ct);
                if (text.TrimStart().StartsWith('{'))
                {
                    using var doc = JsonDocument.Parse(text);
                    var url = doc.RootElement.GetStringProp("url").Replace("\\/", "/");
                    if (!string.IsNullOrWhiteSpace(url) && url.StartsWith("http"))
                    {
                        return url;
                    }
                }
            }
            catch
            {
                // 尝试下一个节点
            }
        }
        return null;
    }

    /// <summary>
    /// 酷我音乐 (Kuwo) 音源匹配与解析（原生 KuwoRid 优先直连免搜索，支持指定音质或优先 FLAC 无损）
    /// </summary>
    private async Task<string?> TryKuwoAsync(SongItem song, bool preferLossless, CancellationToken ct, string? targetQualityLevel = null)
    {
        if (!string.IsNullOrWhiteSpace(song.KuwoRid))
        {
            var directGdKuwo = await TryGdStudioDirectUrlByIdAsync(song.KuwoRid, "kuwo", preferLossless, ct, targetQualityLevel);
            if (!string.IsNullOrWhiteSpace(directGdKuwo))
            {
                return directGdKuwo;
            }
        }
        else if (preferLossless || !string.IsNullOrWhiteSpace(targetQualityLevel))
        {
            var gdKuwoUrl = await TryGdStudioSourceAsync(song, "kuwo", preferLossless, ct, targetQualityLevel);
            if (!string.IsNullOrWhiteSpace(gdKuwoUrl))
            {
                return gdKuwoUrl;
            }
        }

        string? musicRid = song.KuwoRid;
        if (string.IsNullOrWhiteSpace(musicRid))
        {
            var keyword = $"{song.Name} {song.ArtistText}".Trim();
            var searchUrl = $"http://search.kuwo.cn/r.s?client=kt&all={Uri.EscapeDataString(keyword)}&pn=0&rn=5&uid=794762570&ver=kwplayer_ar_9.2.2.1&vipver=1&show_copyright_off=1&newver=1&ft=music&cluster=0&strategy=2012&encoding=utf8&rformat=json&vermerge=1&mobi=1&issubtitle=1";

            var raw = await _httpClient.GetStringAsync(searchUrl, ct);
            var jsonText = raw.Replace('\'', '"');
            using var doc = JsonDocument.Parse(jsonText);

            if (!doc.RootElement.TryGetProperty("abslist", out var abslist) || abslist.ValueKind != JsonValueKind.Array || abslist.GetArrayLength() == 0)
            {
                return null;
            }

            var first = abslist[0];
            musicRid = first.GetStringProp("MUSICRID").Replace("MUSIC_", "");
        }
        if (string.IsNullOrWhiteSpace(musicRid)) return null;

        string[] formats = preferLossless ? ["flac|mp3", "mp3"] : ["mp3"];
        foreach (var fmt in formats)
        {
            try
            {
                var playEndpoint = $"http://antiserver.kuwo.cn/anti.s?type=convert_url&format={fmt}&response=url&rid=MUSIC_{musicRid}";
                var playUrl = (await _httpClient.GetStringAsync(playEndpoint, ct)).Trim();
                if (playUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    return playUrl;
                }
            }
            catch
            {
                // 尝试下一个格式
            }
        }

        return null;
    }

    /// <summary>
    /// 酷狗音乐 (Kugou) 音源匹配与解析（支持按目标音质或优先顺序匹配 reshash / sqhash / 320hash / hash）
    /// </summary>
    private async Task<string?> TryKugouAsync(SongItem song, bool preferLossless, CancellationToken ct, string? targetQualityLevel = null)
    {
        string? albumId = song.KugouAlbumId;
        string? resHash = song.KugouResHash;
        string? sqHash = song.KugouSqHash;
        string? hqHash = song.Kugou320Hash;
        string? stdHash = song.KugouHash;

        if (string.IsNullOrWhiteSpace(resHash) && string.IsNullOrWhiteSpace(sqHash) &&
            string.IsNullOrWhiteSpace(hqHash) && string.IsNullOrWhiteSpace(stdHash))
        {
            var keyword = $"{song.Name} {song.ArtistText}".Trim();
            var searchUrl = $"http://mobilecdn.kugou.com/api/v3/search/song?format=json&keyword={Uri.EscapeDataString(keyword)}&page=1&pagesize=5&showtype=1";

            var json = await _httpClient.GetStringAsync(searchUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("info", out var infoArr) ||
                infoArr.ValueKind != JsonValueKind.Array ||
                infoArr.GetArrayLength() == 0)
            {
                return null;
            }

            var first = infoArr[0];
            albumId = first.GetStringProp("album_id");
            resHash = first.GetStringProp("ResFileHash");
            if (string.IsNullOrWhiteSpace(resHash)) resHash = first.GetStringProp("reshash");
            sqHash = first.GetStringProp("sqhash");
            if (string.IsNullOrWhiteSpace(sqHash)) sqHash = first.GetStringProp("SQFileHash");
            hqHash = first.GetStringProp("320hash");
            stdHash = first.GetStringProp("hash");
        }

        var target = (targetQualityLevel ?? string.Empty).Trim().ToLowerInvariant();
        var candidateHashes = new List<(string Hash, string Br)>();
        if (target is MusicQualityLevel.Standard or MusicQualityLevel.Higher)
        {
            if (!string.IsNullOrWhiteSpace(stdHash)) candidateHashes.Add((stdHash, "128"));
            if (!string.IsNullOrWhiteSpace(hqHash)) candidateHashes.Add((hqHash, "hq"));
        }
        else if (target == MusicQualityLevel.Exhigh)
        {
            if (!string.IsNullOrWhiteSpace(hqHash)) candidateHashes.Add((hqHash, "hq"));
            if (!string.IsNullOrWhiteSpace(stdHash)) candidateHashes.Add((stdHash, "128"));
        }
        else if (target == MusicQualityLevel.Lossless)
        {
            if (!string.IsNullOrWhiteSpace(sqHash)) candidateHashes.Add((sqHash, "flac"));
            if (!string.IsNullOrWhiteSpace(resHash)) candidateHashes.Add((resHash, "flac"));
            if (!string.IsNullOrWhiteSpace(hqHash)) candidateHashes.Add((hqHash, "hq"));
            if (!string.IsNullOrWhiteSpace(stdHash)) candidateHashes.Add((stdHash, "128"));
        }
        else
        {
            if (preferLossless && !string.IsNullOrWhiteSpace(resHash)) candidateHashes.Add((resHash, "flac"));
            if (preferLossless && !string.IsNullOrWhiteSpace(sqHash)) candidateHashes.Add((sqHash, "flac"));
            if (!string.IsNullOrWhiteSpace(hqHash)) candidateHashes.Add((hqHash, "hq"));
            if (!string.IsNullOrWhiteSpace(stdHash)) candidateHashes.Add((stdHash, "128"));
        }

        foreach (var (hash, br) in candidateHashes)
        {
            try
            {
                var keyBytes = global::System.Security.Cryptography.MD5.HashData(
                    global::System.Text.Encoding.UTF8.GetBytes($"{hash.ToLowerInvariant()}kgcloudv2"));
                var keyHex = Convert.ToHexStringLower(keyBytes);

                var trackUrl = $"http://trackercdn.kugou.com/i/v2/?key={keyHex}&hash={hash.ToLowerInvariant()}&br={br}&appid=1005&pid=2&cmd=25&behavior=play&album_id={albumId}";
                var trackJson = await _httpClient.GetStringAsync(trackUrl, ct);
                using var trackDoc = JsonDocument.Parse(trackJson);
                if (trackDoc.RootElement.TryGetProperty("url", out var urlArr) && urlArr.ValueKind == JsonValueKind.Array && urlArr.GetArrayLength() > 0)
                {
                    var u = urlArr[0].GetString();
                    if (!string.IsNullOrWhiteSpace(u) && u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        return u;
                    }
                }
            }
            catch
            {
                // 继续回退低一档 hash
            }
        }

        return null;
    }

    /// <summary>
    /// 1:1 对应原版 src/renderer/api/gdmusic.ts parseFromGDMusic(id, data, '999') 并升级为最新 music.gdstudio.org 签名协议
    /// 支持原生 ID 快通道与高命中多源并发竞速（消除串行轮询 10+ 秒等待与漏判无损导致空跑问题）
    /// </summary>
    private async Task<string?> TryGdMusicMultiSourceAsync(
        SongItem song, bool preferLossless, CancellationToken ct, string? targetQualityLevel = null)
    {
        static bool IsLosslessCandidate(string url, int kbps) =>
            url.Contains(".flac", StringComparison.OrdinalIgnoreCase) || kbps > 400;

        string? fallbackUrl = null;
        int fallbackKbps = 0;
        long fallbackSize = 0;

        void ConsiderFallback(string url, int kbps, long size)
        {
            if (string.IsNullOrWhiteSpace(fallbackUrl) || kbps > fallbackKbps)
            {
                fallbackUrl = url;
                fallbackKbps = kbps;
                fallbackSize = size;
            }
        }

        // 1. 原生直连快通道（网易云 NumericId / 酷我 KuwoRid：0 搜索单次直达）
        if (song.NumericId > 0)
        {
            var directNetease = await TryGdStudioDirectUrlByIdAsync(song.NumericId.ToString(), "netease", preferLossless, ct, targetQualityLevel);
            if (!string.IsNullOrWhiteSpace(directNetease))
            {
                var info = GetCurrentResolvedInfo();
                if (!preferLossless || IsLosslessCandidate(directNetease, info.Kbps))
                {
                    return directNetease;
                }
                ConsiderFallback(directNetease, info.Kbps, info.Size);
            }
        }

        if (!string.IsNullOrWhiteSpace(song.KuwoRid))
        {
            var directKuwo = await TryGdStudioDirectUrlByIdAsync(song.KuwoRid, "kuwo", preferLossless, ct, targetQualityLevel);
            if (!string.IsNullOrWhiteSpace(directKuwo))
            {
                var info = GetCurrentResolvedInfo();
                if (!preferLossless || IsLosslessCandidate(directKuwo, info.Kbps))
                {
                    return directKuwo;
                }
                ConsiderFallback(directKuwo, info.Kbps, info.Size);
            }
        }

        // 2. 按歌曲来源精选前 3 个高命中源并发竞速（避免 5 源串行检索耗时 8~12 秒）
        var primarySources = new List<string>();
        var rawSource = (song.Source ?? string.Empty).Trim().ToLowerInvariant();
        if (rawSource == "qq") primarySources.Add("tencent");
        else if (rawSource == "migu") primarySources.Add("migu");
        primarySources.Add("joox");
        primarySources.Add("kuwo");
        primarySources.Add("netease");
        var topSources = primarySources
            .Where(s => !string.Equals(s, "kuwo", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(song.KuwoRid))
            .Where(s => !string.Equals(s, "netease", StringComparison.OrdinalIgnoreCase) || song.NumericId <= 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        if (topSources.Count > 0 && !ct.IsCancellationRequested)
        {
            using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var branchTasks = topSources.Select(async src =>
            {
                var branchInfo = new ResolveInfo();
                _resolveInfo.Value = branchInfo;
                var url = await TryGdStudioSourceAsync(song, src, preferLossless, raceCts.Token, targetQualityLevel);
                return (Url: url, Info: branchInfo);
            }).ToList();

            while (branchTasks.Count > 0)
            {
                var finished = await Task.WhenAny(branchTasks);
                branchTasks.Remove(finished);
                try
                {
                    var res = await finished;
                    if (string.IsNullOrWhiteSpace(res.Url)) continue;
                    if (!preferLossless || IsLosslessCandidate(res.Url, res.Info.Kbps))
                    {
                        raceCts.Cancel();
                        SetResolvedInfo(res.Info.Kbps, res.Info.Size);
                        return res.Url;
                    }
                    ConsiderFallback(res.Url, res.Info.Kbps, res.Info.Size);
                }
                catch
                {
                    // 单分支失败继续等待其余分支
                }
            }
        }

        // 3. 若主源未命中无损且尚未拿到任何可用兜底，尝试 GD 全网自动换源
        if (string.IsNullOrWhiteSpace(fallbackUrl) && !ct.IsCancellationRequested)
        {
            var autoInfo = new ResolveInfo();
            var prevInfo = _resolveInfo.Value;
            _resolveInfo.Value = autoInfo;
            try
            {
                var autoUrl = await TryGdStudioAutoSourceAsync(song, preferLossless, ct, targetQualityLevel);
                if (!string.IsNullOrWhiteSpace(autoUrl))
                {
                    fallbackUrl = autoUrl;
                    fallbackKbps = autoInfo.Kbps;
                    fallbackSize = autoInfo.Size;
                }
            }
            finally
            {
                _resolveInfo.Value = prevInfo;
            }
        }

        if (!string.IsNullOrWhiteSpace(fallbackUrl))
        {
            SetResolvedInfo(fallbackKbps, fallbackSize);
            return fallbackUrl;
        }

        return null;
    }

    /// <summary>
    /// GD 音乐台按曲目 ID 直接请求播放/下载直链（带最新 crc32.min.js 签名、相对缓存路径补全与 401 自动恢复）
    /// </summary>
    public async Task<string?> TryGdStudioDirectUrlByIdAsync(
        string trackId, string source, bool preferLossless, CancellationToken ct, string? targetQualityLevel = null)
    {
        if (string.IsNullOrWhiteSpace(trackId)) return null;

        var normalizedSrc = (source ?? string.Empty).Trim().ToLowerInvariant();
        var encodedId = GdUrlEncode(trackId);
        var target = (targetQualityLevel ?? string.Empty).Trim().ToLowerInvariant();
        string[] bitrates = normalizedSrc switch
        {
            "ytmusic" => ["192", "128"],
            "apple" => target switch
            {
                MusicQualityLevel.Standard or MusicQualityLevel.Higher or MusicQualityLevel.Exhigh => ["320"],
                _ => preferLossless ? ["999", "320"] : ["320"]
            },
            _ => target switch
            {
                MusicQualityLevel.Standard => ["128", "192", "320"],
                MusicQualityLevel.Higher => ["192", "128", "320"],
                MusicQualityLevel.Exhigh => ["320", "192", "128"],
                MusicQualityLevel.Lossless => ["740", "320"],
                MusicQualityLevel.Hires or MusicQualityLevel.Jymaster or MusicQualityLevel.Jyeffect or MusicQualityLevel.Sky => ["999", "320"],
                _ => preferLossless ? ["999", "320"] : ["320"]
            }
        };

        foreach (var br in bitrates)
        {
            try
            {
                async Task<string> BuildUrlAsync(CancellationToken token)
                {
                    var sign = await ComputeGdStudioSignAsync(encodedId, token);
                    return $"{GdStudioBaseUrl}?types=url&id={encodedId}&source={Uri.EscapeDataString(normalizedSrc)}&br={br}&s={sign}";
                }

                var urlEndpoint = await BuildUrlAsync(ct);
                var urlJson = await SendGdGetStringAsync(urlEndpoint, BuildUrlAsync, ct);
                if (string.IsNullOrWhiteSpace(urlJson)) continue;

                using var urlDoc = JsonDocument.Parse(urlJson);
                var root = urlDoc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                var resolved = root.GetStringProp("url").Replace("\\/", "/").Trim();
                int resolvedBr = root.GetIntProp("br");
                long resolvedSize = root.GetLongProp("size");

                // 过滤空响应、错误标记或仅含代理域名的无效直链
                if (string.IsNullOrWhiteSpace(resolved) ||
                    resolved.Equals("err", StringComparison.OrdinalIgnoreCase) ||
                    resolvedBr <= 0 ||
                    resolved.EndsWith(".gdstudio.org/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 补全 cache_sources（apple / ytmusic 等）返回的相对路径，例如 cache/apple_1440924808.256.m4a
                if (!resolved.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    resolved = $"https://{GdStudioHost}/{resolved.TrimStart('/')}";
                }

                SetResolvedInfo(resolvedBr, resolvedSize);
                return resolved;
            }
            catch
            {
                // 当前码率失败则回退下一档
            }
        }

        return null;
    }

    /// <summary>
    /// 落雪音源：按“首选脚本优先 + 其余已启用脚本分批并发竞速”解析直链，记录真实命中的脚本名称
    /// </summary>
    public async Task<string?> TryLxMusicAsync(
        SongItem song, bool preferLossless, CancellationToken ct, string? targetQualityLevel = null)
    {
        var s = _configService.Settings;
        if (s.LxMusicScripts.Count == 0) return null;

        var lxPrefer = !string.IsNullOrWhiteSpace(targetQualityLevel)
            ? MapUserQualityToLxQuality(targetQualityLevel)
            : (preferLossless ? "flac24bit" : "320k");

        var (url, quality, scriptName) = await _lxHost.GetMusicUrlFromScriptsAsync(
            s.LxMusicScripts,
            s.ActiveLxMusicApiId,
            song,
            lxPrefer,
            u => IsLikelyFullTrackAsync(u, song, ct),
            ct);

        if (string.IsNullOrWhiteSpace(url)) return null;

        var resolvedName = string.IsNullOrWhiteSpace(scriptName) ? "lxMusic" : scriptName;
        SetResolvedInfo(LxQualityToKbps(quality), 0, resolvedName);
        return url;
    }

    /// <summary>
    /// 指定单个已导入洛雪脚本进行定向解析（在独立专区标签或底栏指定音源时使用）
    /// </summary>
    public async Task<(string? Url, string ScriptName)> TrySpecificLxScriptAsync(
        string scriptId, SongItem song, bool preferLossless, CancellationToken ct, string? targetQualityLevel = null)
    {
        var s = _configService.Settings;
        var script = s.LxMusicScripts.FirstOrDefault(x => string.Equals(x.Id, scriptId, StringComparison.OrdinalIgnoreCase));
        if (script == null) return (null, string.Empty);

        var lxPrefer = !string.IsNullOrWhiteSpace(targetQualityLevel)
            ? MapUserQualityToLxQuality(targetQualityLevel)
            : (preferLossless ? "flac24bit" : "320k");

        var (url, quality, scriptName) = await _lxHost.GetMusicUrlFromSingleScriptAsync(
            script,
            song,
            lxPrefer,
            u => IsLikelyFullTrackAsync(u, song, ct),
            ct);

        if (string.IsNullOrWhiteSpace(url)) return (null, string.Empty);

        var resolvedName = string.IsNullOrWhiteSpace(scriptName) ? script.Name : scriptName;
        LastResolvedBitrateKbps = LxQualityToKbps(quality);
        LastResolvedSizeBytes = 0;
        LastResolvedSourceName = resolvedName;
        SetResolvedInfo(LastResolvedBitrateKbps, 0, resolvedName);
        return (url, resolvedName);
    }

    /// <summary>
    /// 落雪音质标识换算为码率（flac24bit 视为 Hi-Res）
    /// </summary>
    public static int LxQualityToKbps(string quality) => quality switch
    {
        "flac24bit" => 2000,
        "flac" => 999,
        "320k" => 320,
        "128k" => 128,
        _ => 0
    };

    /// <summary>
    /// GD 音乐台全网自动换源接口 (types=autosource)
    /// </summary>
    private async Task<string?> TryGdStudioAutoSourceAsync(
        SongItem song, bool preferLossless, CancellationToken ct, string? targetQualityLevel = null)
    {
        if (string.IsNullOrWhiteSpace(song.Name)) return null;

        try
        {
            var firstArtist = song.Artists.FirstOrDefault()?.Name ?? song.ArtistText.Split(['/', ',', '、'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
            var payloadObj = new
            {
                name = song.Name.Trim(),
                artist = firstArtist,
                album = song.Album?.Name?.Trim() ?? "",
                isrc = "",
                duration = (int)(song.DurationMs / 1000)
            };
            var jsonPayload = JsonSerializer.Serialize(payloadObj);
            var encodedPayload = GdUrlEncode(jsonPayload);
            var target = (targetQualityLevel ?? string.Empty).Trim().ToLowerInvariant();
            var br = target switch
            {
                MusicQualityLevel.Standard => "128",
                MusicQualityLevel.Higher => "192",
                MusicQualityLevel.Exhigh => "320",
                MusicQualityLevel.Lossless => "740",
                MusicQualityLevel.Hires or MusicQualityLevel.Jymaster or MusicQualityLevel.Jyeffect or MusicQualityLevel.Sky => "999",
                _ => preferLossless ? "999" : "320"
            };

            async Task<string> BuildAutoUrlAsync(CancellationToken token)
            {
                var sign = await ComputeGdStudioSignAsync(encodedPayload, token);
                return $"{GdStudioBaseUrl}?types=autosource&source=netease&br={br}&name={encodedPayload}&s={sign}";
            }

            var endpoint = await BuildAutoUrlAsync(ct);
            var respJson = await SendGdGetStringAsync(endpoint, BuildAutoUrlAsync, ct);
            if (string.IsNullOrWhiteSpace(respJson)) return null;

            using var doc = JsonDocument.Parse(respJson);
            var resolved = doc.RootElement.GetStringProp("url").Replace("\\/", "/").Trim();
            if (!string.IsNullOrWhiteSpace(resolved) &&
                doc.RootElement.GetIntProp("br") > 0 &&
                !resolved.EndsWith(".gdstudio.org/", StringComparison.OrdinalIgnoreCase))
            {
                if (!resolved.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    resolved = $"https://{GdStudioHost}/{resolved.TrimStart('/')}";
                }
                SetResolvedInfo(doc.RootElement.GetIntProp("br"), doc.RootElement.GetLongProp("size"));
                return resolved;
            }
        }
        catch
        {
            // 忽略自动换源异常
        }

        return null;
    }

    /// <summary>
    /// GD 音乐台指定音源搜索与解析（支持 joox / qobuz / tidal / netease / tencent / migu / kuwo，支持目标音质与无损优先）
    /// </summary>
    public async Task<string?> TryGdStudioSourceAsync(
        SongItem song, string gdSource, bool preferLossless, CancellationToken ct, string? targetQualityLevel = null)
    {
        // 若是 tencent 源且有 QqSongMid，先尝试落月 API (api.vkeys.cn)
        if (gdSource == "tencent" && !string.IsNullOrWhiteSpace(song.QqSongMid))
        {
            var vkeysQqUrl = await TryVkeysTencentAsync(song.QqSongMid, preferLossless, ct, targetQualityLevel);
            if (!string.IsNullOrWhiteSpace(vkeysQqUrl) && (!preferLossless || vkeysQqUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase)))
            {
                return vkeysQqUrl;
            }
        }

        var keyword = $"{song.Name} {song.ArtistText}".Trim();
        if (keyword.Length < 1) return null;

        try
        {
            var encodedKw = GdUrlEncode(keyword);

            async Task<string> BuildSearchUrlAsync(CancellationToken token)
            {
                var searchSign = await ComputeGdStudioSignAsync(encodedKw, token);
                return $"{GdStudioBaseUrl}?types=search&count=5&source={Uri.EscapeDataString(gdSource)}&pages=1&name={encodedKw}&s={searchSign}";
            }

            var url = await BuildSearchUrlAsync(ct);
            var searchJson = await SendGdGetStringAsync(url, BuildSearchUrlAsync, ct);
            if (string.IsNullOrWhiteSpace(searchJson)) return null;

            using var searchDoc = JsonDocument.Parse(searchJson);
            if (searchDoc.RootElement.ValueKind != JsonValueKind.Array || searchDoc.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var matched = PickBestGdCandidate(searchDoc.RootElement, song);
            if (matched == null) return null;

            var trackId = matched.Value.GetStringProp("url_id");
            if (string.IsNullOrWhiteSpace(trackId))
            {
                trackId = matched.Value.GetStringProp("id");
            }
            var source = matched.Value.GetStringProp("source", gdSource);
            if (string.IsNullOrWhiteSpace(trackId)) return null;

            return await TryGdStudioDirectUrlByIdAsync(trackId, source, preferLossless, ct, targetQualityLevel);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 通过 GD 音乐台检索国际 Hi-Res / 流媒体曲库（支持 tidal / qobuz / ytmusic / spotify / apple / deezer）
    /// </summary>
    public async Task<List<SongItem>> SearchGdStudioCatalogAsync(
        string gdSource, string keyword, int page = 1, int count = 35, CancellationToken ct = default)
    {
        var list = new List<SongItem>();
        var trimmed = keyword?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed)) return list;

        try
        {
            var encodedKw = GdUrlEncode(trimmed);

            async Task<string> BuildCatalogSearchUrlAsync(CancellationToken token)
            {
                var sign = await ComputeGdStudioSignAsync(encodedKw, token);
                return $"{GdStudioBaseUrl}?types=search&count={Math.Clamp(count, 1, 50)}&source={Uri.EscapeDataString(gdSource)}&pages={Math.Max(1, page)}&name={encodedKw}&s={sign}";
            }

            var url = await BuildCatalogSearchUrlAsync(ct);
            var json = await SendGdGetStringAsync(url, BuildCatalogSearchUrlAsync, ct);
            if (string.IsNullOrWhiteSpace(json)) return list;

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            var picTasks = new List<(SongItem Song, string PicId, string ItemSource)>();
            int idx = 1;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var id = item.GetStringProp("id");
                var urlId = item.GetStringProp("url_id");
                var trackId = !string.IsNullOrWhiteSpace(urlId) ? urlId : id;
                var name = item.GetStringProp("name");
                if (string.IsNullOrWhiteSpace(trackId) || string.IsNullOrWhiteSpace(name)) continue;

                var itemSource = item.GetStringProp("source", gdSource);
                var artists = new List<ArtistItem>();
                if (item.TryGetProperty("artist", out var artProp))
                {
                    if (artProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var a in artProp.EnumerateArray())
                        {
                            var aName = a.ValueKind == JsonValueKind.String ? a.GetString() : a.GetStringProp("name");
                            if (!string.IsNullOrWhiteSpace(aName))
                                artists.Add(new ArtistItem { Name = aName.Trim() });
                        }
                    }
                    else if (artProp.ValueKind == JsonValueKind.String)
                    {
                        var aRaw = artProp.GetString();
                        if (!string.IsNullOrWhiteSpace(aRaw))
                            artists.Add(new ArtistItem { Name = aRaw.Trim() });
                    }
                }

                var albumName = item.GetStringProp("album");
                var picId = item.GetStringProp("pic_id");
                var directPic = picId.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? picId : string.Empty;

                var song = new SongItem
                {
                    Id = $"{gdSource}_{trackId}",
                    DisplayIndex = idx++,
                    Name = name,
                    Artists = artists,
                    Album = new AlbumItem { Name = albumName, PicUrl = directPic },
                    PicUrl = directPic,
                    Source = gdSource,
                    GdTrackId = trackId,
                    GdSource = itemSource,
                    NativeQualityHint = gdSource is "tidal" or "qobuz" ? "24bit Hi-Res" : "256k AAC"
                };

                list.Add(song);
                if (string.IsNullOrEmpty(directPic) && !string.IsNullOrWhiteSpace(picId) && picTasks.Count < 12)
                {
                    picTasks.Add((song, picId, itemSource));
                }
            }

            // 并发补充前 12 条无直链封面的歌曲封面图（限时 2.5 秒，不阻塞列表首屏展示）
            if (picTasks.Count > 0)
            {
                using var picCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                picCts.CancelAfter(TimeSpan.FromMilliseconds(2500));
                using var sem = new SemaphoreSlim(6);
                await Task.WhenAll(picTasks.Select(async entry =>
                {
                    await sem.WaitAsync(picCts.Token);
                    try
                    {
                        var encPic = GdUrlEncode(entry.PicId);

                        async Task<string> BuildPicUrlAsync(CancellationToken token)
                        {
                            var picSign = await ComputeGdStudioSignAsync(encPic, token);
                            return $"{GdStudioBaseUrl}?types=pic&source={Uri.EscapeDataString(entry.ItemSource)}&id={encPic}&size=300&s={picSign}";
                        }

                        var picApi = await BuildPicUrlAsync(picCts.Token);
                        var picJson = await SendGdGetStringAsync(picApi, BuildPicUrlAsync, picCts.Token);
                        if (string.IsNullOrWhiteSpace(picJson)) return;
                        using var pDoc = JsonDocument.Parse(picJson);
                        var pUrl = pDoc.RootElement.GetStringProp("url").Replace("\\/", "/");
                        if (!string.IsNullOrWhiteSpace(pUrl) && pUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            entry.Song.PicUrl = pUrl;
                            entry.Song.Album.PicUrl = pUrl;
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        sem.Release();
                    }
                })).ConfigureAwait(false);
            }
        }
        catch
        {
        }

        return list;
    }

    /// <summary>
    /// 国际音乐平台（Spotify / Apple Music / Deezer / Tidal / Qobuz / YT Music）纯国际完整曲目解析（严禁跳转国内源）
    /// </summary>
    public async Task<(string? Url, string Platform)> ResolveInternationalAudioAsync(
        SongItem song, bool preferLossless, CancellationToken ct = default, string? targetQualityLevel = null)
    {
        LastResolvedBitrateKbps = 0;
        LastResolvedSizeBytes = 0;
        if (song == null || string.IsNullOrWhiteSpace(song.Name)) return (null, string.Empty);

        var rawSrc = (song.Source ?? string.Empty).Trim().ToLowerInvariant();
        var prefix = $"{rawSrc}_";
        var nativeId = !string.IsNullOrWhiteSpace(song.NativeTrackId)
            ? song.NativeTrackId.Trim()
            : (!string.IsNullOrWhiteSpace(song.GdTrackId)
                ? song.GdTrackId.Trim()
                : (song.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? song.Id[prefix.Length..].Trim()
                    : string.Empty));

        string? fallbackUrl = null;
        string fallbackPlat = string.Empty;
        int fallbackKbps = 0;
        long fallbackSize = 0;

        static bool IsLosslessResult(string u, int kbps) =>
            u.Contains(".flac", StringComparison.OrdinalIgnoreCase) || kbps > 400;

        void SaveFallback(string url, string plat, int kbps, long size)
        {
            if (string.IsNullOrWhiteSpace(fallbackUrl) || kbps > fallbackKbps)
            {
                fallbackUrl = url;
                fallbackPlat = plat;
                fallbackKbps = kbps;
                fallbackSize = size;
            }
        }

        // 1. Apple Music / Tidal 原生曲目 ID 精确直连快通道（跳过同名搜索与错误版本风险）
        if (rawSrc == "tidal" && !string.IsNullOrWhiteSpace(nativeId))
        {
            var directTidal = await TryGdStudioDirectUrlByIdAsync(nativeId, "tidal", preferLossless, ct, targetQualityLevel);
            var info = GetCurrentResolvedInfo();
            if (!string.IsNullOrWhiteSpace(directTidal) && await IsLikelyFullTrackAsync(directTidal, song, ct, info.Size, info.Kbps))
            {
                if (!preferLossless || IsLosslessResult(directTidal, info.Kbps))
                {
                    return (directTidal, "tidal");
                }
                SaveFallback(directTidal, "tidal", info.Kbps, info.Size);
            }
        }
        else if (rawSrc == "apple" && !string.IsNullOrWhiteSpace(nativeId) && nativeId.All(char.IsDigit))
        {
            var directApple = await TryGdStudioDirectUrlByIdAsync(nativeId, "apple", preferLossless, ct, targetQualityLevel);
            var info = GetCurrentResolvedInfo();
            if (!string.IsNullOrWhiteSpace(directApple) && await IsLikelyFullTrackAsync(directApple, song, ct, info.Size, info.Kbps))
            {
                if (!preferLossless || IsLosslessResult(directApple, info.Kbps))
                {
                    return (directApple, "apple");
                }
                SaveFallback(directApple, "apple", info.Kbps, info.Size);
            }
        }

        // 2. 若携带其他有效国际 GDStudio ID（仅限定在纯国际源范围内）
        if (!string.IsNullOrWhiteSpace(song.GdTrackId) &&
            song.GdSource?.Trim().ToLowerInvariant() is "tidal" or "apple" or "joox" or "ytmusic" &&
            !string.Equals(song.GdSource, rawSrc, StringComparison.OrdinalIgnoreCase))
        {
            var gdSrc = song.GdSource.Trim().ToLowerInvariant();
            var directUrl = await TryGdStudioDirectUrlByIdAsync(song.GdTrackId, gdSrc, preferLossless, ct, targetQualityLevel);
            var info = GetCurrentResolvedInfo();
            if (!string.IsNullOrWhiteSpace(directUrl) && await IsLikelyFullTrackAsync(directUrl, song, ct, info.Size, info.Kbps))
            {
                if (!preferLossless || IsLosslessResult(directUrl, info.Kbps))
                {
                    return (directUrl, gdSrc);
                }
                SaveFallback(directUrl, gdSrc, info.Kbps, info.Size);
            }
        }

        // 3. 纯国际候选源并发竞速（严格限定 joox / tidal / apple / ytmusic 原生 ID，严禁调用 autosource 或任何国内源）
        var parallelSpecs = new List<(string Platform, Func<CancellationToken, Task<string?>> Resolver)>();
        bool hasPreciseNativeFallback = !string.IsNullOrWhiteSpace(fallbackUrl) && fallbackPlat is "apple" or "tidal";

        parallelSpecs.Add(("joox", token => TryGdStudioSourceAsync(song, "joox", preferLossless, token, targetQualityLevel)));
        if (!hasPreciseNativeFallback)
        {
            if (fallbackPlat != "tidal")
            {
                parallelSpecs.Add(("tidal", token => TryGdStudioSourceAsync(song, "tidal", preferLossless, token, targetQualityLevel)));
            }
            if (fallbackPlat != "apple" && (!preferLossless || rawSrc == "apple"))
            {
                parallelSpecs.Add(("apple", token => TryGdStudioSourceAsync(song, "apple", preferLossless, token, targetQualityLevel)));
            }
        }

        if (rawSrc == "ytmusic" && !string.IsNullOrWhiteSpace(nativeId))
        {
            parallelSpecs.Add(("ytmusic", token => TryGdStudioDirectUrlByIdAsync(nativeId, "ytmusic", preferLossless: false, token, targetQualityLevel)));
        }

        if (parallelSpecs.Count > 0 && !ct.IsCancellationRequested)
        {
            using var intlCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (!string.IsNullOrWhiteSpace(fallbackUrl))
            {
                // 若前面已握有原生精确高码率链接，最多并发等待 1.5s 看 JOOX 能否提供 FLAC 无损
                intlCts.CancelAfter(TimeSpan.FromMilliseconds(1500));
            }

            var tasks = parallelSpecs.Select(async spec =>
            {
                var branchInfo = new ResolveInfo();
                _resolveInfo.Value = branchInfo;
                var u = await spec.Resolver(intlCts.Token);
                if (string.IsNullOrWhiteSpace(u)) return null;
                if (!await IsLikelyFullTrackAsync(u, song, intlCts.Token, branchInfo.Size, branchInfo.Kbps)) return null;
                return ((string Url, string Plat, ResolveInfo Info)?)(u, spec.Platform, branchInfo);
            }).ToList();

            bool graceStarted = !string.IsNullOrWhiteSpace(fallbackUrl);
            while (tasks.Count > 0)
            {
                var finished = await Task.WhenAny(tasks);
                tasks.Remove(finished);
                (string Url, string Plat, ResolveInfo Info)? res = null;
                try
                {
                    res = await finished;
                }
                catch
                {
                    continue;
                }

                if (res == null) continue;
                var val = res.Value;
                if (!preferLossless || IsLosslessResult(val.Url, val.Info.Kbps))
                {
                    intlCts.Cancel();
                    LastResolvedBitrateKbps = val.Info.Kbps;
                    LastResolvedSizeBytes = val.Info.Size;
                    return (val.Url, val.Plat);
                }

                SaveFallback(val.Url, val.Plat, val.Info.Kbps, val.Info.Size);
                if (!graceStarted)
                {
                    graceStarted = true;
                    // 已拿到首个完整国际高码率流，再给 JOOX FLAC 最多 1.2 秒宽限窗口，超时即刻开播
                    intlCts.CancelAfter(TimeSpan.FromMilliseconds(1200));
                }
            }
        }

        // 4. 非 lossless-only 场景下若上述并发仍未命中且之前跳过 apple，则补一次 apple 搜索兜底
        if (string.IsNullOrWhiteSpace(fallbackUrl) && preferLossless && rawSrc != "apple" && !ct.IsCancellationRequested)
        {
            var appleInfo = new ResolveInfo();
            _resolveInfo.Value = appleInfo;
            var appleUrl = await TryGdStudioSourceAsync(song, "apple", false, ct, targetQualityLevel);
            if (!string.IsNullOrWhiteSpace(appleUrl) && await IsLikelyFullTrackAsync(appleUrl, song, ct, appleInfo.Size, appleInfo.Kbps))
            {
                LastResolvedBitrateKbps = appleInfo.Kbps;
                LastResolvedSizeBytes = appleInfo.Size;
                return (appleUrl, "apple");
            }
        }

        if (!string.IsNullOrWhiteSpace(fallbackUrl))
        {
            LastResolvedBitrateKbps = fallbackKbps;
            LastResolvedSizeBytes = fallbackSize;
            return (fallbackUrl, fallbackPlat);
        }

        return (null, string.Empty);
    }

    /// <summary>
    /// 将国际代理音频流（如 YouTube Music WebM/Opus）下载到本地缓存，供内置 mpv.exe 本地无障碍解码播放并消除 403/E_ACCESSDENIED
    /// </summary>
    public async Task<string?> DownloadInternationalStreamToCacheAsync(
        string streamUrl, string cacheKey, string cacheDir, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(streamUrl) || string.IsNullOrWhiteSpace(cacheDir)) return null;

        try
        {
            Directory.CreateDirectory(cacheDir);
            var safeKey = Regex.Replace(cacheKey ?? "track", @"[^A-Za-z0-9_\-]", "_");
            var cacheFile = Path.Combine(cacheDir, $"ytm_{safeKey}.webm");
            if (File.Exists(cacheFile) && new FileInfo(cacheFile).Length > 4096)
            {
                return cacheFile;
            }

            var tempFile = cacheFile + ".tmp";
            using var req = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            if (streamUrl.Contains(GdStudioHost, StringComparison.OrdinalIgnoreCase))
            {
                req.Headers.Referrer = new Uri(GdStudioHomeUrl);
            }

            using var resp = await _downloadHttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode >= 400) return null;

            await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await resp.Content.CopyToAsync(fs, ct);
            }

            if (!File.Exists(tempFile) || new FileInfo(tempFile).Length <= 4096)
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                return null;
            }

            File.Move(tempFile, cacheFile, overwrite: true);
            return cacheFile;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 落月 API (api.vkeys.cn) 网易云音乐解析通道
    /// </summary>
    private async Task<string?> TryVkeysNeteaseAsync(long songId, bool preferLossless, CancellationToken ct, string? targetQualityLevel = null)
    {
        var target = (targetQualityLevel ?? string.Empty).Trim().ToLowerInvariant();
        int[] qualities = target switch
        {
            MusicQualityLevel.Standard => [2, 4],
            MusicQualityLevel.Higher => [3, 4],
            MusicQualityLevel.Exhigh => [4],
            _ => preferLossless ? [9, 4] : [4]
        };
        foreach (var q in qualities)
        {
            try
            {
                var url = $"https://api.vkeys.cn/v2/music/netease?id={songId}&quality={q}";
                var json = await _httpClient.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    var playUrl = data.GetStringProp("url").Replace("\\/", "/");
                    if (!string.IsNullOrWhiteSpace(playUrl) && playUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        return playUrl;
                    }
                }
            }
            catch
            {
                // 尝试下一档音质
            }
        }
        return null;
    }

    /// <summary>
    /// 落月 API (api.vkeys.cn) QQ 音乐解析通道
    /// </summary>
    public async Task<string?> TryVkeysTencentAsync(string songMid, bool preferLossless, CancellationToken ct, string? targetQualityLevel = null)
    {
        if (string.IsNullOrWhiteSpace(songMid)) return null;
        var target = (targetQualityLevel ?? string.Empty).Trim().ToLowerInvariant();
        int[] qualities = target switch
        {
            MusicQualityLevel.Standard or MusicQualityLevel.Higher => [6, 8],
            MusicQualityLevel.Exhigh => [8, 6],
            _ => preferLossless ? [10, 8] : [8]
        };
        foreach (var q in qualities)
        {
            try
            {
                var url = $"https://api.vkeys.cn/v2/music/tencent?mid={Uri.EscapeDataString(songMid)}&quality={q}";
                var json = await _httpClient.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    var playUrl = data.GetStringProp("url").Replace("\\/", "/");
                    var interval = data.GetStringProp("quality");
                    // 跳过仅试听片段（28kbps 音乐试听）
                    if (!string.IsNullOrWhiteSpace(playUrl) &&
                        playUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                        !interval.Contains("试听", StringComparison.OrdinalIgnoreCase) &&
                        !playUrl.Contains("RS02", StringComparison.OrdinalIgnoreCase))
                    {
                        return playUrl;
                    }
                }
            }
            catch
            {
                // 尝试下一档
            }
        }
        return null;
    }

    /// <summary>
    /// 请求 GD 音乐台接口（统一注入 Referer 并在遇到 401 签名过期时自动同步最新 crc32.min.js 重试一次）
    /// </summary>
    private async Task<string?> SendGdGetStringAsync(
        string url, Func<CancellationToken, Task<string>>? rebuildUrlOn401, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri(GdStudioHomeUrl);
            using var resp = await _httpClient.SendAsync(req, ct);
            if (resp.StatusCode == global::System.Net.HttpStatusCode.Unauthorized &&
                attempt == 0 &&
                rebuildUrlOn401 != null)
            {
                await RefreshGdDynamicSignAsync(ct);
                url = await rebuildUrlOn401(ct);
                continue;
            }

            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }

        return null;
    }

    /// <summary>
    /// 从 music.gdstudio.org 首页与 /time 自动刷新服务器时间、player.js 版本号及 crc32.min.js 动态脚本
    /// </summary>
    private async Task RefreshGdDynamicSignAsync(CancellationToken ct)
    {
        await _gdSyncLock.WaitAsync(ct);
        try
        {
            try
            {
                var timeStr = (await SendGdGetStringAsync(GdStudioTimeUrl, null, ct))?.Trim();
                if (long.TryParse(timeStr, out var serverSec) && serverSec > 1_000_000_000)
                {
                    _gdServerTimeOffsetSec = serverSec - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    _gdServerTimeLastSync = DateTimeOffset.UtcNow;
                }
            }
            catch
            {
            }

            try
            {
                var playerJs = await SendGdGetStringAsync(GdStudioPlayerJsUrl, null, ct);
                if (!string.IsNullOrWhiteSpace(playerJs))
                {
                    var verMatch = Regex.Match(playerJs, @"version\s*:\s*[""']([0-9.]+)[""']");
                    if (verMatch.Success)
                    {
                        var normalized = string.Concat(verMatch.Groups[1].Value
                            .Split('.', StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => p.Length == 1 ? "0" + p : p));
                        if (normalized.Length >= 8)
                        {
                            _gdStudioVersion = normalized;
                            _gdVersionLastSync = DateTimeOffset.UtcNow;
                        }
                    }
                }
            }
            catch
            {
            }

            try
            {
                var indexHtml = await SendGdGetStringAsync(GdStudioHomeUrl, null, ct);
                var crcPath = "js/crc32.min.js";
                if (!string.IsNullOrWhiteSpace(indexHtml))
                {
                    var m = Regex.Match(indexHtml, @"src=[""'](js/crc32\.min\.js(?:\?[^""']*)?)[""']", RegexOptions.IgnoreCase);
                    if (m.Success) crcPath = m.Groups[1].Value;
                }

                var crcUrl = $"{GdStudioHomeUrl}{crcPath.TrimStart('/')}";
                var jsCode = await SendGdGetStringAsync(crcUrl, null, ct);
                if (!string.IsNullOrWhiteSpace(jsCode))
                {
                    _gdCrcJsCode = jsCode;
                    _gdCrcTag = crcPath;
                    _useDynamicGdMd5 = true;
                }
            }
            catch
            {
            }
        }
        finally
        {
            _gdSyncLock.Release();
        }
    }

    /// <summary>
    /// 并发去重同步 GD 音乐台服务器时间与版本号（已预热并在有效期内直接 0ms 返回）
    /// </summary>
    private async Task EnsureGdStudioReadyAsync(CancellationToken ct)
    {
        bool needTime = (DateTimeOffset.UtcNow - _gdServerTimeLastSync).TotalMinutes > 3;
        bool needVersion = (DateTimeOffset.UtcNow - _gdVersionLastSync).TotalMinutes > 30;
        if (!needTime && !needVersion) return;

        await _gdSyncLock.WaitAsync(ct);
        try
        {
            needTime = (DateTimeOffset.UtcNow - _gdServerTimeLastSync).TotalMinutes > 3;
            needVersion = (DateTimeOffset.UtcNow - _gdVersionLastSync).TotalMinutes > 30;
            if (!needTime && !needVersion) return;

            using var syncCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            syncCts.CancelAfter(TimeSpan.FromSeconds(3));

            var timeTask = needTime
                ? SendGdGetStringAsync(GdStudioTimeUrl, null, syncCts.Token)
                : Task.FromResult<string?>(null);
            var versionTask = needVersion
                ? SendGdGetStringAsync(GdStudioPlayerJsUrl, null, syncCts.Token)
                : Task.FromResult<string?>(null);

            try
            {
                await Task.WhenAll(timeTask, versionTask);
            }
            catch
            {
                // 单路同步失败继续读取已完成的结果
            }

            if (needTime && timeTask.IsCompletedSuccessfully)
            {
                var timeStr = timeTask.Result?.Trim();
                if (long.TryParse(timeStr, out var serverSec) && serverSec > 1_000_000_000)
                {
                    _gdServerTimeOffsetSec = serverSec - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    _gdServerTimeLastSync = DateTimeOffset.UtcNow;
                }
            }

            if (needVersion && versionTask.IsCompletedSuccessfully)
            {
                var playerJs = versionTask.Result;
                if (!string.IsNullOrWhiteSpace(playerJs))
                {
                    var verMatch = Regex.Match(playerJs, @"version\s*:\s*[""']([0-9.]+)[""']");
                    if (verMatch.Success)
                    {
                        var normalized = string.Concat(verMatch.Groups[1].Value
                            .Split('.', StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => p.Length == 1 ? "0" + p : p));
                        if (normalized.Length >= 8)
                        {
                            _gdStudioVersion = normalized;
                            _gdVersionLastSync = DateTimeOffset.UtcNow;
                        }
                    }
                }
            }
        }
        catch
        {
            // 若同步异常，回退使用本机 UTC 时间与当前默认版本号
        }
        finally
        {
            _gdSyncLock.Release();
        }
    }

    /// <summary>
    /// 计算 GD 音乐台 (music.gdstudio.org) 请求签名参数 s
    /// 算法：GdCustomMd5("{ts9}|music.gdstudio.org|{version}|{encodedParam}") 取末尾 8 位大写十六进制
    /// 自动并发去重同步 /time 服务器时间与 /js/player.js 最新 version，并在 401 时切换离屏 WebView2 动态脚本签名
    /// </summary>
    private async Task<string> ComputeGdStudioSignAsync(string encodedParam, CancellationToken ct)
    {
        await EnsureGdStudioReadyAsync(ct);

        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _gdServerTimeOffsetSec;
        var tsStr = nowSec.ToString();
        var ts9 = tsStr.Length >= 9 ? tsStr[..9] : tsStr;
        var raw = $"{ts9}|{GdStudioHost}|{_gdStudioVersion}|{encodedParam}";

        string? hashHex = null;
        if (_useDynamicGdMd5 && !string.IsNullOrWhiteSpace(_gdCrcJsCode))
        {
            hashHex = await _lxHost.EvaluateGdStudioMd5Async(_gdCrcJsCode, _gdCrcTag, raw, ct);
        }

        if (string.IsNullOrWhiteSpace(hashHex) || hashHex.Length < 8)
        {
            hashHex = ComputeGdCustomMd5Hex(raw);
        }

        return hashHex[^8..].ToUpperInvariant();
    }

    /// <summary>
    /// 1:1 对应 GD 音乐台 functions.js 中的 urlEncode(a)
    /// </summary>
    private static string GdUrlEncode(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return Uri.EscapeDataString(input)
            .Replace("(", "%28")
            .Replace(")", "%29")
            .Replace("*", "%2A")
            .Replace("'", "%27")
            .Replace("!", "%21");
    }

    /// <summary>
    /// 1:1 对应 GD 音乐台 crc32.min.js?v=20261112 中的魔改 MD5（T[1] = 0x21c10959）
    /// </summary>
    private static string ComputeGdCustomMd5Hex(string input)
    {
        var bytes = global::System.Text.Encoding.UTF8.GetBytes(input);
        int bitLen = bytes.Length * 8;
        int wordCount = (((bytes.Length + 8) >> 6) + 1) * 16;
        var words = new uint[wordCount];

        for (int i = 0; i < bytes.Length; i++)
        {
            words[i >> 2] |= (uint)bytes[i] << ((i % 4) * 8);
        }
        words[bytes.Length >> 2] |= 0x80u << ((bytes.Length % 4) * 8);
        words[(((bytes.Length + 8) >> 6) * 16) + 14] = (uint)bitLen;

        uint a = 0x67452301;
        uint b = 0xefcdab89;
        uint c = 0x98badcfe;
        uint d = 0x10325476;

        for (int i = 0; i < wordCount; i += 16)
        {
            uint aa = a, bb = b, cc = c, dd = d;
            for (int j = 0; j < 64; j++)
            {
                uint f;
                int g;
                if (j < 16)
                {
                    f = (b & c) | (~b & d);
                    g = j;
                }
                else if (j < 32)
                {
                    f = (d & b) | (~d & c);
                    g = (5 * j + 1) % 16;
                }
                else if (j < 48)
                {
                    f = b ^ c ^ d;
                    g = (3 * j + 5) % 16;
                }
                else
                {
                    f = c ^ (b | ~d);
                    g = (7 * j) % 16;
                }

                uint temp = d;
                d = c;
                c = b;
                uint sum = a + f + GdMd5T[j] + words[i + g];
                int s = GdMd5S[j];
                b += (sum << s) | (sum >> (32 - s));
                a = temp;
            }
            a += aa;
            b += bb;
            c += cc;
            d += dd;
        }

        Span<byte> outBytes = stackalloc byte[16];
        global::System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(outBytes[0..4], a);
        global::System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(outBytes[4..8], b);
        global::System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(outBytes[8..12], c);
        global::System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(outBytes[12..16], d);
        return Convert.ToHexStringLower(outBytes);
    }

    /// <summary>
    /// 1:1 对齐 src/renderer/api/gdmusic.ts pickBestCandidate：校验歌名与歌手，避免匹配到翻唱或货不对版歌曲
    /// </summary>
    private static JsonElement? PickBestGdCandidate(JsonElement candidatesArray, SongItem expectedSong)
    {
        JsonElement? best = null;
        int bestScore = 0;
        var expectedName = NormalizeMatchText(expectedSong.Name);
        var expectedArtists = expectedSong.Artists
            .Select(a => NormalizeMatchText(a.Name))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        foreach (var item in candidatesArray.EnumerateArray())
        {
            var id = item.GetStringProp("id");
            if (string.IsNullOrWhiteSpace(id)) continue;

            var candName = NormalizeMatchText(item.GetStringProp("name"));
            if (string.IsNullOrWhiteSpace(expectedName) || string.IsNullOrWhiteSpace(candName)) continue;
            bool nameMatched = expectedName == candName ||
                               (expectedName.Length >= 2 && candName.Contains(expectedName)) ||
                               (candName.Length >= 2 && expectedName.Contains(candName));
            if (!nameMatched) continue;

            string candArtistRaw = string.Empty;
            if (item.TryGetProperty("artist", out var artProp))
            {
                if (artProp.ValueKind == JsonValueKind.Array)
                {
                    candArtistRaw = string.Join(" ", artProp.EnumerateArray().Select(e =>
                        e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetStringProp("name")));
                }
                else if (artProp.ValueKind == JsonValueKind.String)
                {
                    candArtistRaw = artProp.GetString() ?? string.Empty;
                }
            }
            var candArtist = NormalizeMatchText(candArtistRaw);

            int score;
            if (expectedArtists.Count == 0)
            {
                score = 2;
            }
            else if (string.IsNullOrWhiteSpace(candArtist))
            {
                score = 1;
            }
            else
            {
                bool artistMatched = expectedArtists.Any(ea => candArtist.Contains(ea) || ea.Contains(candArtist));
                if (!artistMatched) continue;
                score = 3;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = item;
            }
        }

        // 若未命中歌手校验，仅当原曲未附带歌手信息且第一条候选歌名确实归一化一致时才兜底，防止错配到完全无关曲目
        if (best == null && expectedArtists.Count == 0 && candidatesArray.GetArrayLength() > 0)
        {
            var firstName = NormalizeMatchText(candidatesArray[0].GetStringProp("name"));
            if (!string.IsNullOrWhiteSpace(expectedName) &&
                !string.IsNullOrWhiteSpace(firstName) &&
                (firstName == expectedName ||
                 (expectedName.Length >= 2 && firstName.Contains(expectedName)) ||
                 (firstName.Length >= 2 && expectedName.Contains(firstName))))
            {
                return candidatesArray[0];
            }
        }

        return best;
    }

    private static string NormalizeMatchText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lower = text.ToLowerInvariant();
        var stripped = Regex.Replace(lower, @"[（(【\[].*?[)）】\]]", "");
        stripped = Regex.Replace(stripped, @"[\s\-—_·・'""‘’“”!！?？.,，。&＆+/、]", "");
        if (!string.IsNullOrWhiteSpace(stripped)) return stripped;
        return Regex.Replace(lower, @"[\s\-—_·・'""‘’“”!！?？.,，。&＆+/、]", "");
    }

    private async Task<string?> TryCustomEndpointAsync(SongItem song, CancellationToken ct)
    {
        try
        {
            var template = _configService.Settings.CustomUnblockApiUrl;
            var quality = _configService.Settings.MusicQuality;
            var url = template
                .Replace("{id}", song.Id)
                .Replace("{level}", quality)
                .Replace("{quality}", quality)
                .Replace("{name}", Uri.EscapeDataString(song.Name))
                .Replace("{artist}", Uri.EscapeDataString(song.ArtistText));
            var text = await _httpClient.GetStringAsync(url, ct);
            if (text.TrimStart().StartsWith("http")) return text.Trim();
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var resolved = root.GetStringProp("url");
            if (string.IsNullOrWhiteSpace(resolved) && root.TryGetProperty("data", out var dataElem))
            {
                if (dataElem.ValueKind == JsonValueKind.Object)
                {
                    resolved = dataElem.GetStringProp("url");
                    LastResolvedBitrateKbps = dataElem.GetIntProp("br");
                    LastResolvedSizeBytes = dataElem.GetLongProp("size");
                }
                else if (dataElem.ValueKind == JsonValueKind.Array && dataElem.GetArrayLength() > 0)
                {
                    resolved = dataElem[0].GetStringProp("url");
                    LastResolvedBitrateKbps = dataElem[0].GetIntProp("br");
                    LastResolvedSizeBytes = dataElem[0].GetLongProp("size");
                }
            }
            else
            {
                LastResolvedBitrateKbps = root.GetIntProp("br");
                LastResolvedSizeBytes = root.GetLongProp("size");
            }
            if (LastResolvedBitrateKbps > 10000) LastResolvedBitrateKbps /= 1000;
            return resolved;
        }
        catch
        {
            return null;
        }
    }
}
