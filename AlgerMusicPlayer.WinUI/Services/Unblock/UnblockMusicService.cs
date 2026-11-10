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
    private const string GdStudioBaseUrl = "https://music.gdstudio.org/api.php";
    private const string GdStudioTimeUrl = "https://music.gdstudio.org/time";
    private const string GdStudioVersion = "20261106";

    private long _gdServerTimeOffsetSec;
    private DateTimeOffset _gdServerTimeLastSync = DateTimeOffset.MinValue;

    public int LastResolvedBitrateKbps { get; private set; }
    public long LastResolvedSizeBytes { get; private set; }

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

        return kbps is > 0 and <= 320 ? $"极高 {kbps}k" : "极高 320k";
    }

    // GD 音乐台 crc32.min.js 自定义 MD5 常量表（T[32] 由标准 0xfffa3942 修改为 0x59951e3e）
    private static readonly uint[] GdMd5T =
    [
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
        0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
        0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
        0x59951e3e, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
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

    public UnblockMusicService(ConfigService configService)
    {
        _configService = configService;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
    }

    /// <summary>
    /// 按启用平台顺序（或指定单一平台）解析无版权/试听/无损歌曲的可播放或可下载 URL
    /// </summary>
    public async Task<(string? Url, string Platform)> MatchAudioUrlAsync(
        SongItem song,
        IEnumerable<string>? preferredPlatforms = null,
        CancellationToken cancellationToken = default,
        bool preferLossless = true)
    {
        LastResolvedBitrateKbps = 0;
        LastResolvedSizeBytes = 0;

        var platforms = preferredPlatforms?.ToList()
            ?? (_configService.Settings.EnabledMusicSources.Count > 0
                ? _configService.Settings.EnabledMusicSources.ToList()
                : AllPlatforms.ToList());

        // 若请求无损且未强制指定单一音源，始终将支持 999kbps FLAC/Hi-Res 的 gdmusic 与 pyncmd 排在最前面
        if (preferLossless && preferredPlatforms == null)
        {
            platforms.RemoveAll(p => p.Equals("gdmusic", StringComparison.OrdinalIgnoreCase) ||
                                     p.Equals("pyncmd", StringComparison.OrdinalIgnoreCase));
            platforms.Insert(0, "pyncmd");
            platforms.Insert(0, "gdmusic");
        }

        // 若配置了自定义 Unblock HTTP 服务地址，优先尝试
        if (!string.IsNullOrWhiteSpace(_configService.Settings.CustomUnblockApiUrl))
        {
            var customUrl = await TryCustomEndpointAsync(song, cancellationToken);
            if (!string.IsNullOrWhiteSpace(customUrl))
            {
                return (customUrl, "custom");
            }
        }

        string? fallbackNonFlacUrl = null;
        string fallbackPlatform = string.Empty;

        foreach (var platform in platforms)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                string? url = platform.ToLowerInvariant() switch
                {
                    "pyncmd" => await TryPyncmdAsync(song, preferLossless, cancellationToken),
                    "kuwo" => await TryKuwoAsync(song, preferLossless, cancellationToken),
                    "kugou" => await TryKugouAsync(song, preferLossless, cancellationToken),
                    "migu" => await TryGdStudioSourceAsync(song, "migu", preferLossless, cancellationToken),
                    "qq" => await TryGdStudioSourceAsync(song, "tencent", preferLossless, cancellationToken),
                    "joox" => await TryGdStudioSourceAsync(song, "joox", preferLossless, cancellationToken),
                    "gdmusic" => await TryGdMusicMultiSourceAsync(song, preferLossless, cancellationToken),
                    _ => null
                };

                if (!string.IsNullOrWhiteSpace(url))
                {
                    if (!preferLossless || url.Contains(".flac", StringComparison.OrdinalIgnoreCase))
                    {
                        return (url, platform);
                    }

                    if (string.IsNullOrWhiteSpace(fallbackNonFlacUrl))
                    {
                        fallbackNonFlacUrl = url;
                        fallbackPlatform = platform;
                    }
                }
            }
            catch
            {
                // 单个音源失败继续尝试下一个平台
            }
        }

        if (!string.IsNullOrWhiteSpace(fallbackNonFlacUrl))
        {
            return (fallbackNonFlacUrl, fallbackPlatform);
        }

        return (null, string.Empty);
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
    /// PyNCMD / GDStudio 新版直连与 Meting 镜像源解析（通过网易云歌曲 ID 直接获取 24bit Hi-Res / 16bit FLAC br=999 或高音质音频流）
    /// </summary>
    private async Task<string?> TryPyncmdAsync(SongItem song, bool preferLossless, CancellationToken ct)
    {
        if (song.NumericId <= 0) return null;

        // 1. 优先调用 GD 音乐台最新签名接口 (music.gdstudio.org/api.php) 直接按网易云 ID 获取 999k (24bit Hi-Res / 16bit FLAC) 无损直链
        var gdDirectUrl = await TryGdStudioDirectUrlByIdAsync(song.NumericId.ToString(), "netease", preferLossless, ct);
        if (!string.IsNullOrWhiteSpace(gdDirectUrl))
        {
            return gdDirectUrl;
        }

        // 2. 尝试落月 API (api.vkeys.cn) 网易云解析
        var vkeysUrl = await TryVkeysNeteaseAsync(song.NumericId, preferLossless, ct);
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
    /// 酷我音乐 (Kuwo) 音源匹配与解析（优先尝试 FLAC 无损，再回退 MP3）
    /// </summary>
    private async Task<string?> TryKuwoAsync(SongItem song, bool preferLossless, CancellationToken ct)
    {
        if (preferLossless)
        {
            var gdKuwoUrl = await TryGdStudioSourceAsync(song, "kuwo", preferLossless: true, ct);
            if (!string.IsNullOrWhiteSpace(gdKuwoUrl))
            {
                return gdKuwoUrl;
            }
        }

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
        var musicRid = first.GetStringProp("MUSICRID").Replace("MUSIC_", "");
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
    /// 酷狗音乐 (Kugou) 音源匹配与解析（优先尝试 sqhash 无损 FLAC，再回退 320hash / hash）
    /// </summary>
    private async Task<string?> TryKugouAsync(SongItem song, bool preferLossless, CancellationToken ct)
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
        var albumId = first.GetStringProp("album_id");
        var resHash = first.GetStringProp("ResFileHash");
        if (string.IsNullOrWhiteSpace(resHash)) resHash = first.GetStringProp("reshash");
        var sqHash = first.GetStringProp("sqhash");
        if (string.IsNullOrWhiteSpace(sqHash)) sqHash = first.GetStringProp("SQFileHash");
        var hqHash = first.GetStringProp("320hash");
        var stdHash = first.GetStringProp("hash");

        var candidateHashes = new List<(string Hash, string Br)>();
        if (preferLossless && !string.IsNullOrWhiteSpace(resHash))
        {
            candidateHashes.Add((resHash, "flac"));
        }
        if (preferLossless && !string.IsNullOrWhiteSpace(sqHash))
        {
            candidateHashes.Add((sqHash, "flac"));
        }
        if (!string.IsNullOrWhiteSpace(hqHash))
        {
            candidateHashes.Add((hqHash, "hq"));
        }
        if (!string.IsNullOrWhiteSpace(stdHash))
        {
            candidateHashes.Add((stdHash, "hq"));
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
    /// 优先尝试网易云 ID 直取无损、JOOX 无损、GD 自动换源 (autosource) 以及多音源搜索，获取 br=999 (FLAC/Hi-Res 无损)
    /// </summary>
    private async Task<string?> TryGdMusicMultiSourceAsync(SongItem song, bool preferLossless, CancellationToken ct)
    {
        // 1. 若有网易云 NumericId，直接请求 GDStudio netease 999k 无损直链（无需搜索，最快且直接返回 m701.music.126.net Hi-Res/FLAC）
        if (song.NumericId > 0)
        {
            var directNetease = await TryGdStudioDirectUrlByIdAsync(song.NumericId.ToString(), "netease", preferLossless, ct);
            if (!string.IsNullOrWhiteSpace(directNetease))
            {
                return directNetease;
            }
        }

        // 2. 依次尝试 joox (支持周杰伦等港台曲库 F000 FLAC 无损) / netease / qobuz / tidal / kuwo
        string[] sources = ["joox", "netease", "qobuz", "tidal", "kuwo"];
        string? fallbackNonFlac = null;
        foreach (var src in sources)
        {
            if (ct.IsCancellationRequested) break;
            var url = await TryGdStudioSourceAsync(song, src, preferLossless, ct);
            if (!string.IsNullOrWhiteSpace(url))
            {
                if (!preferLossless || url.Contains(".flac", StringComparison.OrdinalIgnoreCase))
                {
                    return url;
                }
                fallbackNonFlac ??= url;
            }
        }

        // 3. 尝试 GD 音乐台自研全网自动换源 (types=autosource&br=999)
        var autoUrl = await TryGdStudioAutoSourceAsync(song, preferLossless, ct);
        if (!string.IsNullOrWhiteSpace(autoUrl))
        {
            if (!preferLossless || autoUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(fallbackNonFlac))
            {
                return autoUrl;
            }
        }

        return fallbackNonFlac;
    }

    /// <summary>
    /// GD 音乐台按曲目 ID 直接请求播放/下载直链（带最新 crc32.min.js 签名与 /time 时间戳校准）
    /// </summary>
    public async Task<string?> TryGdStudioDirectUrlByIdAsync(string trackId, string source, bool preferLossless, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(trackId)) return null;

        var encodedId = GdUrlEncode(trackId);
        string[] bitrates = preferLossless ? ["999", "740", "320"] : ["320"];

        foreach (var br in bitrates)
        {
            try
            {
                var sign = await ComputeGdStudioSignAsync(encodedId, ct);
                var urlEndpoint = $"{GdStudioBaseUrl}?types=url&id={encodedId}&source={source}&br={br}&s={sign}";
                var urlJson = await _httpClient.GetStringAsync(urlEndpoint, ct);
                using var urlDoc = JsonDocument.Parse(urlJson);
                var resolved = urlDoc.RootElement.GetStringProp("url").Replace("\\/", "/");
                if (!string.IsNullOrWhiteSpace(resolved) && resolved.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    LastResolvedBitrateKbps = urlDoc.RootElement.GetIntProp("br");
                    LastResolvedSizeBytes = urlDoc.RootElement.GetLongProp("size");
                    return resolved;
                }
            }
            catch
            {
                // 当前码率失败则回退下一档
            }
        }

        return null;
    }

    /// <summary>
    /// GD 音乐台全网自动换源接口 (types=autosource&br=999)
    /// </summary>
    private async Task<string?> TryGdStudioAutoSourceAsync(SongItem song, bool preferLossless, CancellationToken ct)
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
            var br = preferLossless ? "999" : "320";
            var sign = await ComputeGdStudioSignAsync(encodedPayload, ct);

            var endpoint = $"{GdStudioBaseUrl}?types=autosource&source=netease&br={br}&name={encodedPayload}&s={sign}";
            var respJson = await _httpClient.GetStringAsync(endpoint, ct);
            using var doc = JsonDocument.Parse(respJson);
            var resolved = doc.RootElement.GetStringProp("url").Replace("\\/", "/");
            if (!string.IsNullOrWhiteSpace(resolved) && resolved.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                LastResolvedBitrateKbps = doc.RootElement.GetIntProp("br");
                LastResolvedSizeBytes = doc.RootElement.GetLongProp("size");
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
    /// GD 音乐台指定音源搜索与解析（支持 joox / qobuz / tidal / netease / tencent / migu / kuwo，优先 br=999 无损 FLAC）
    /// </summary>
    public async Task<string?> TryGdStudioSourceAsync(SongItem song, string gdSource, bool preferLossless, CancellationToken ct)
    {
        // 若是 tencent 源且有 QqSongMid，先尝试落月 API (api.vkeys.cn)
        if (gdSource == "tencent" && !string.IsNullOrWhiteSpace(song.QqSongMid))
        {
            var vkeysQqUrl = await TryVkeysTencentAsync(song.QqSongMid, preferLossless, ct);
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
            var searchSign = await ComputeGdStudioSignAsync(encodedKw, ct);
            var url = $"{GdStudioBaseUrl}?types=search&count=5&source={gdSource}&pages=1&name={encodedKw}&s={searchSign}";
            var searchJson = await _httpClient.GetStringAsync(url, ct);
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

            return await TryGdStudioDirectUrlByIdAsync(trackId, source, preferLossless, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 落月 API (api.vkeys.cn) 网易云音乐解析通道
    /// </summary>
    private async Task<string?> TryVkeysNeteaseAsync(long songId, bool preferLossless, CancellationToken ct)
    {
        int[] qualities = preferLossless ? [9, 4] : [4];
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
    public async Task<string?> TryVkeysTencentAsync(string songMid, bool preferLossless, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(songMid)) return null;
        int[] qualities = preferLossless ? [10, 8] : [8];
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
    /// 计算 GD 音乐台 (music.gdstudio.org) 请求签名参数 s
    /// 算法：GdCustomMd5("{ts9}|music.gdstudio.org|20261106|{encodedParam}") 取末尾 8 位大写十六进制
    /// </summary>
    private async Task<string> ComputeGdStudioSignAsync(string encodedParam, CancellationToken ct)
    {
        if ((DateTimeOffset.UtcNow - _gdServerTimeLastSync).TotalMinutes > 3)
        {
            try
            {
                var timeStr = (await _httpClient.GetStringAsync(GdStudioTimeUrl, ct)).Trim();
                if (long.TryParse(timeStr, out var serverSec) && serverSec > 1_000_000_000)
                {
                    _gdServerTimeOffsetSec = serverSec - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    _gdServerTimeLastSync = DateTimeOffset.UtcNow;
                }
            }
            catch
            {
                // 若 /time 请求失败，回退使用本机 UTC 时间
            }
        }

        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _gdServerTimeOffsetSec;
        var tsStr = nowSec.ToString();
        var ts9 = tsStr.Length >= 9 ? tsStr[..9] : tsStr;
        var raw = $"{ts9}|{GdStudioHost}|{GdStudioVersion}|{encodedParam}";
        var hashHex = ComputeGdCustomMd5Hex(raw);
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
    /// 1:1 对应 GD 音乐台 crc32.min.js 中的魔改 MD5（第 33 轮常量 T[32] = 0x59951e3e）
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
            bool nameMatched = expectedName == candName || candName.Contains(expectedName) || expectedName.Contains(candName);
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

        // 若严格匹配未命中且原曲无歌手或仅有一条同名结果，兜底取第一条
        if (best == null && candidatesArray.GetArrayLength() > 0)
        {
            return candidatesArray[0];
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
