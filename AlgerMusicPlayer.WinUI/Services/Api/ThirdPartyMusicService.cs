using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AlgerMusicPlayer.WinUI.Models;

namespace AlgerMusicPlayer.WinUI.Services.Api;

/// <summary>
/// 第三方平台原生曲库服务（支持 QQ音乐 tx / 酷狗 kg / 酷我 kw / 咪咕 mg 的原生搜索、热歌榜单与跨源参数补齐）
/// </summary>
public sealed class ThirdPartyMusicService
{
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _qishuiTokenLock = new(1, 1);
    private string _qishuiClientToken = string.Empty;
    private DateTimeOffset _qishuiTokenExpireUtc = DateTimeOffset.MinValue;
    private readonly string _qishuiDeviceId = GenerateQishuiDeviceId();
    private readonly string _qishuiInstallId = GenerateQishuiDeviceId();
    private readonly global::System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset CachedAt, PlaylistItem Playlist)> _spotifyEmbedCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly global::System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset CachedAt, List<(string Url, int BitrateKbps, long SizeBytes)> List)> _qishuiCandidatesCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _spotifyTokenLock = new(1, 1);
    private string _spotifyAccessToken = string.Empty;
    private long _spotifyTokenExpireMs;

    public ThirdPartyMusicService()
    {
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = global::System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true
        })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
    }

    /// <summary>
    /// 获取各音乐平台专区的标题、徽章、默认副标题、搜索框提示及洛雪脚本状态开关
    /// </summary>
    public static (string ZoneTitle, string BadgeText, string DefaultSubtitle, string SearchPlaceholder, bool SupportsLxHint) GetPlatformZoneMeta(string sourceKey) =>
        sourceKey.ToLowerInvariant() switch
        {
            "tx" or "qq" => ("QQ 音乐专区", "原生曲库 + 账号歌单", "浏览 QQ 音乐分类歌单、巅峰榜单与个人 QQ 歌单，支持原生 songmid 无损直连", "搜索 QQ 音乐单曲、歌手...", true),
            "qishui" => ("汽水音乐专区", "抖音热歌直通", "同步抖音潮流热歌、精选歌单与病毒式神曲榜，原生无加密音频流秒播", "搜索汽水音乐 / 抖音热歌、BGM...", false),
            "kg" or "kugou" => ("酷狗音乐专区", "原生 Hash 直连", "汇聚酷狗热门歌单与 TOP500 榜单，携带原生 Hash 直取高码率与 FLAC 无损", "搜索酷狗音乐单曲、歌手...", true),
            "kw" or "kuwo" => ("酷我音乐专区", "无损曲库直通", "精选酷我华语、古风、车载 DJ 歌单与热歌榜，原生 RID 直连播放", "搜索酷我音乐单曲、歌手...", true),
            "mg" or "migu" => ("咪咕音乐专区", "正版华语曲库", "畅听咪咕尖叫榜与经典华语歌单，自带原生版权 ID 与官方歌词", "搜索咪咕音乐单曲、歌手...", true),
            "wy" or "netease" => ("网易云曲库浏览", "官方歌单 / 热歌榜", "浏览网易云精选推荐歌单与热歌榜单，点播时交由当前所选音源通道解析", "搜索网易云单曲、歌手...", true),
            "spotify" => ("Spotify 全球专区", "Global Charts", "同步 Spotify 官方全球 / 欧美 / 日韩 Top 50 榜单与精选歌单，按官方原生 API 播放", "搜索 Spotify 全球曲库、欧美日韩歌手...", false),
            "apple" => ("Apple Music 专区", "Official API", "实时获取美区 / 英区 / 日本 / 韩国 / 港台 Apple Music 热歌与热门专辑，按官方原生 API 播放", "搜索 Apple Music 全球单曲、歌手...", false),
            "deezer" => ("Deezer 全球馆", "Millions Playlists", "探索 Deezer 全球高赞流派歌单与各国热播榜，按官方原生 API 播放", "搜索 Deezer 全球单曲、歌手...", false),
            "tidal" or "qobuz" or "ytmusic" => ("Tidal & YT Music 专区", "Native API", "直连 Tidal 官方歌单曲库与 YouTube Music 全球原生音源检索播放", "搜索 Tidal / YouTube Music 曲目...", false),
            _ => ("音乐专区", "原生曲库", "发现各大音乐平台精选歌单与排行榜单", "搜索歌曲、歌手...", false)
        };

    public static IReadOnlyList<(string Key, string Title)> GetBoards(string sourceKey) =>
        sourceKey.ToLowerInvariant() switch
        {
            "tx" or "qq" => [("hot", "热歌榜"), ("soar", "飙升榜"), ("new", "新歌榜"), ("mainland", "内地榜"), ("hktw", "港台榜")],
            "qishui" => [("hot", "抖音热歌榜"), ("soar", "汽水飙升榜"), ("new", "新歌热播榜"), ("bgm", "爆款BGM榜"), ("west", "欧美流行榜")],
            "kg" or "kugou" => [("hot", "TOP500热歌"), ("soar", "酷狗飙升榜"), ("new", "华语新歌榜"), ("net", "网络红歌榜")],
            "kw" or "kuwo" => [("hot", "酷我热歌榜"), ("soar", "酷我飙升榜"), ("new", "酷我新歌榜"), ("douyin", "抖音热歌榜")],
            "mg" or "migu" => [("hot", "尖叫热歌榜"), ("new", "尖叫新歌榜"), ("original", "尖叫原创榜")],
            "wy" or "netease" => [("3778678", "热歌榜"), ("19723756", "飙升榜"), ("3779629", "新歌榜"), ("2884035", "原创榜")],
            "spotify" =>
            [
                ("37i9dQZEVXbMDoHDwVN2tF", "全球 Top 50"), ("37i9dQZEVXbLRQDuF5jeBp", "美国 Top 50"),
                ("37i9dQZEVXbNG2KDcFcKOF", "全球周榜 Top Songs"), ("37i9dQZEVXbLnolsZ8PSNw", "英国 Top 50"),
                ("37i9dQZEVXbKXQ4mDTEBXq", "日本 Top 50"), ("37i9dQZEVXbNxXF4SkHj9F", "韩国 Top 50")
            ],
            "apple" => [("us", "美区 Top 50"), ("gb", "英区 Top 50"), ("jp", "日本 Top 50"), ("kr", "韩国 Top 50"), ("hk", "中国香港 Top 50"), ("tw", "中国台湾 Top 50")],
            "deezer" => [("0", "全球热歌榜"), ("1313621735", "全球 Top 100"), ("1111141961", "美国榜"), ("1111142221", "英国榜"), ("1362516565", "日本榜"), ("1362508955", "韩国榜")],
            "tidal" or "qobuz" or "ytmusic" => [("audiophile", "Hi-Res 试音金曲"), ("global", "全球热播榜"), ("us", "欧美流行榜"), ("jp", "日系精选榜"), ("ost", "史诗原声榜")],
            _ => [("hot", "热歌榜")]
        };

    public static IReadOnlyList<(string Key, string Title)> GetPlaylistCategories(string sourceKey) =>
        sourceKey.ToLowerInvariant() switch
        {
            "tx" or "qq" =>
            [
                ("10000000", "全部推荐"), ("165", "华语"), ("6", "流行"), ("136", "经典"),
                ("145", "国风"), ("11", "摇滚"), ("28", "民谣"), ("24", "电子"), ("39", "ACG"), ("15", "轻音乐")
            ],
            "qishui" =>
            [
                ("抖音热歌", "抖音热歌"), ("华语流行", "华语流行"), ("车载DJ", "车载DJ"), ("伤感情歌", "伤感情歌"),
                ("欧美热歌", "欧美热歌"), ("民谣治愈", "民谣治愈"), ("国风古风", "国风古风"), ("说唱Rap", "说唱Rap"), ("轻音乐", "轻音乐")
            ],
            "kg" or "kugou" =>
            [
                ("all", "精选推荐"), ("9", "网络热歌"), ("12", "经典怀旧"), ("13", "伤感情歌"),
                ("27", "粤语金曲"), ("30", "华语R&B"), ("33", "电音派对"), ("68", "情歌对唱"), ("15", "安静治愈")
            ],
            "kw" or "kuwo" =>
            [
                ("hot", "热门推荐"), ("new", "最新精选"), ("37", "华语"), ("393", "流行"),
                ("1265", "经典"), ("127", "古风"), ("173", "轻音乐"), ("391", "电子"), ("168", "DJ热曲"), ("35", "欧美")
            ],
            "mg" or "migu" =>
            [
                ("rec", "热门推荐"), ("1001076096", "网络热歌"), ("1000001672", "流行"), ("1000001635", "经典老歌"),
                ("1000001675", "国风"), ("1000001775", "民谣"), ("1000001827", "KTV金曲"), ("1000001795", "伤感"), ("1000001777", "欧美")
            ],
            "wy" or "netease" =>
            [
                ("全部", "全部推荐"), ("华语", "华语"), ("流行", "流行"), ("古风", "古风"),
                ("民谣", "民谣"), ("摇滚", "摇滚"), ("电子", "电子"), ("ACG", "ACG"), ("轻音乐", "轻音乐")
            ],
            "spotify" =>
            [
                ("top", "王牌编辑精选"), ("pop", "欧美流行"), ("hiphop", "说唱与电音"),
                ("kpop", "日韩潮流"), ("chill", "轻松与Lofi"), ("rock", "摇滚与经典")
            ],
            "apple" =>
            [
                ("us", "美区热门专辑"), ("jp", "日本热门专辑"), ("kr", "韩国热门专辑"),
                ("gb", "英区热门专辑"), ("hk", "港台热门专辑")
            ],
            "deezer" =>
            [
                ("all", "全球热门歌单"), ("Pop", "流行 Pop"), ("EDM", "电音 EDM"),
                ("Hip Hop", "说唱 Hip-Hop"), ("K-Pop", "韩流 K-Pop"), ("J-Pop", "日音 J-Pop"),
                ("Rock", "摇滚 Rock"), ("Lofi", "Lofi 氛围"), ("Jazz", "爵士 Jazz")
            ],
            "tidal" or "qobuz" or "ytmusic" =>
            [
                ("Hi-Res Audiophile", "发烧试音精选"), ("Pop Hits", "全球流行"),
                ("Classical Masterpieces", "古典交响"), ("Jazz Classics", "发烧爵士"),
                ("Soundtrack", "电影原声"), ("Anime J-Pop", "ACG 与日音")
            ],
            _ => [("all", "全部推荐")]
        };

    /// <summary>
    /// 获取指定第三方或国际平台的推荐/分类歌单列表
    /// </summary>
    public async Task<List<PlaylistItem>> GetRecommendPlaylistsAsync(
        string sourceKey, string categoryKey = "", int page = 1, int pageSize = 30, CancellationToken ct = default)
    {
        var normalized = sourceKey.ToLowerInvariant();
        return normalized switch
        {
            "tx" or "qq" => await GetQqRecommendPlaylistsAsync(categoryKey, page, pageSize, ct),
            "qishui" => await GetQishuiRecommendPlaylistsAsync(categoryKey, page, ct),
            "kg" or "kugou" => await GetKugouRecommendPlaylistsAsync(categoryKey, page, pageSize, ct),
            "kw" or "kuwo" => await GetKuwoRecommendPlaylistsAsync(categoryKey, page, pageSize, ct),
            "mg" or "migu" => await GetMiguRecommendPlaylistsAsync(categoryKey, page, ct),
            "spotify" => await GetSpotifyRecommendPlaylistsAsync(categoryKey, page, pageSize, ct),
            "apple" => await GetAppleRecommendPlaylistsAsync(categoryKey, ct),
            "deezer" => await GetDeezerRecommendPlaylistsAsync(categoryKey, page, pageSize, ct),
            "tidal" => await GetTidalRecommendPlaylistsAsync(categoryKey, page, pageSize, ct),
            "ytmusic" => await GetYtMusicNativePlaylistsAsync(categoryKey, page, pageSize, ct),
            "qobuz" => [],
            _ => []
        };
    }

    /// <summary>
    /// 获取指定第三方或国际平台的歌单/专辑详情及完整原生曲目列表
    /// </summary>
    public async Task<PlaylistItem?> GetPlaylistDetailAsync(
        PlaylistItem summary, CancellationToken ct = default)
    {
        if (summary == null) return null;
        var src = summary.Source.ToLowerInvariant();
        var detail = src switch
        {
            "kugou" or "kg" => await GetKugouPlaylistDetailAsync(summary, ct),
            "kuwo" or "kw" => await GetKuwoPlaylistDetailAsync(summary, ct),
            "migu" or "mg" => await GetMiguPlaylistDetailAsync(summary, ct),
            "qishui" => await GetQishuiPlaylistDetailAsync(summary, ct),
            "spotify" => await GetSpotifyEmbedPlaylistAsync(summary.ThirdPartyId, ct),
            "apple" => await GetAppleAlbumDetailAsync(summary, ct),
            "deezer" => await GetDeezerPlaylistDetailAsync(summary, ct),
            "tidal" => await GetTidalNativePlaylistDetailAsync(summary, ct),
            "ytmusic" => await GetYtMusicNativePlaylistDetailAsync(summary, ct),
            "qobuz" => null,
            _ => null
        };

        if (detail != null)
        {
            for (int i = 0; i < detail.Tracks.Count; i++)
            {
                detail.Tracks[i].DisplayIndex = i + 1;
            }
        }
        return detail;
    }

    /// <summary>
    /// 获取指定第三方或国际平台的榜单歌曲列表（自带原生 ID / Hash 与最高音质标识）
    /// </summary>
    public async Task<List<SongItem>> GetBoardSongsAsync(string sourceKey, string boardKey = "hot", CancellationToken ct = default)
    {
        var normalized = sourceKey.ToLowerInvariant();
        var list = normalized switch
        {
            "tx" or "qq" => await GetQqBoardAsync(boardKey, ct),
            "qishui" => await GetQishuiBoardAsync(boardKey, ct),
            "kg" or "kugou" => await GetKugouBoardAsync(boardKey, ct),
            "kw" or "kuwo" => await GetKuwoBoardAsync(boardKey, ct),
            "mg" or "migu" => await GetMiguBoardAsync(boardKey, ct),
            "spotify" => await GetSpotifyBoardAsync(boardKey, ct),
            "apple" => await GetAppleBoardAsync(boardKey, ct),
            "deezer" => await GetDeezerBoardAsync(boardKey, ct),
            "tidal" => await GetTidalBoardAsync(boardKey, ct),
            "ytmusic" => await GetYtMusicBoardAsync(boardKey, ct),
            "qobuz" => [],
            _ => []
        };

        for (int i = 0; i < list.Count; i++) list[i].DisplayIndex = i + 1;
        return list;
    }

    /// <summary>
    /// 在指定第三方或国际平台原生曲库中按关键词搜索歌曲
    /// </summary>
    public async Task<List<SongItem>> SearchSongsAsync(
        string sourceKey, string keyword, int page = 1, int pageSize = 35, CancellationToken ct = default)
    {
        var trimmed = keyword?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed)) return [];

        var normalized = sourceKey.ToLowerInvariant();
        var list = normalized switch
        {
            "tx" or "qq" => await SearchQqAsync(trimmed, page, pageSize, ct),
            "qishui" => await SearchQishuiAsync(trimmed, page, pageSize, ct),
            "kg" or "kugou" => await SearchKugouAsync(trimmed, page, pageSize, ct),
            "kw" or "kuwo" => await SearchKuwoAsync(trimmed, page, pageSize, ct),
            "mg" or "migu" => await SearchMiguAsync(trimmed, page, pageSize, ct),
            "spotify" => await SearchSpotifyAsync(trimmed, page, pageSize, ct),
            "apple" => await SearchAppleAsync(trimmed, page, pageSize, ct),
            "deezer" => await SearchDeezerAsync(trimmed, page, pageSize, ct),
            "tidal" => await SearchTidalNativeAsync(trimmed, page, pageSize, ct),
            "ytmusic" => await SearchYtMusicNativeAsync(trimmed, page, pageSize, ct),
            "qobuz" => [],
            _ => []
        };

        for (int i = 0; i < list.Count; i++) list[i].DisplayIndex = i + 1;
        return list;
    }

    /// <summary>
    /// 返回首个非空白字符串（解决 GetStringProp 缺失字段返回空串导致 ?? 失效的问题）
    /// </summary>
    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return string.Empty;
    }

    #region 汽水音乐（抖音 OpenSDK 免登录曲库与无加密流）

    private const string QishuiUa = "LunaOpenSDK/1.0.10";
    private const string QishuiPrefix = "/api/luna/v1/platform";

    private static string GenerateQishuiDeviceId()
    {
        Span<byte> buf = stackalloc byte[8];
        global::System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
        ulong raw = BitConverter.ToUInt64(buf);
        ulong id = (raw % 9000000000000000000UL) + 1000000000000000000UL;
        return id.ToString();
    }

    private string BuildQishuiQueryString(string? triggerType = null)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = global::System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 65536).ToString();
        var sb = new StringBuilder(512);
        sb.Append("aid=765213&app_id=765213&app_name=byte_autoservice_lite")
          .Append("&version_code=100010&version_name=1.0.10&channel=bydSong")
          .Append("&sdk_aid=753999&caller_name=luna_open_sdk&sdk_version_code=100000100")
          .Append("&sdk_version_name=0.0.1&sdk_channel=Test")
          .Append("&vehicle_type=BYD&device_brand=BYD&device_type=BYD")
          .Append("&device_platform=android&resolution=1920x1080&os_version=13&ac=wifi")
          .Append("&device_id=").Append(_qishuiDeviceId)
          .Append("&did=").Append(_qishuiDeviceId)
          .Append("&iid=").Append(_qishuiInstallId)
          .Append("&vehicle_id=qishui-").Append(_qishuiDeviceId)
          .Append("&_timestamp=").Append(ts)
          .Append("&_nonce=").Append(nonce);
        if (!string.IsNullOrWhiteSpace(triggerType))
        {
            sb.Append("&trigger_type=").Append(Uri.EscapeDataString(triggerType));
        }
        return sb.ToString();
    }

    private async Task<string?> EnsureQishuiTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && !string.IsNullOrWhiteSpace(_qishuiClientToken) && DateTimeOffset.UtcNow < _qishuiTokenExpireUtc)
        {
            return _qishuiClientToken;
        }

        await _qishuiTokenLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && !string.IsNullOrWhiteSpace(_qishuiClientToken) && DateTimeOffset.UtcNow < _qishuiTokenExpireUtc)
            {
                return _qishuiClientToken;
            }

            var url = $"https://open.qishui.com{QishuiPrefix}/client/token/get/?{BuildQishuiQueryString("cold_start")}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", QishuiUa);
            using var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = global::System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? token = root.GetStringProp("client_token");
            if (string.IsNullOrWhiteSpace(token) && root.TryGetProperty("data", out var dataEl))
            {
                token = dataEl.GetStringProp("client_token");
            }
            if (string.IsNullOrWhiteSpace(token)) return null;

            _qishuiClientToken = token;
            _qishuiTokenExpireUtc = DateTimeOffset.UtcNow.AddHours(2);
            return token;
        }
        catch
        {
            return null;
        }
        finally
        {
            _qishuiTokenLock.Release();
        }
    }

    private async Task<global::System.Text.Json.JsonDocument?> PostQishuiPlatformAsync(string path, object bodyObj, CancellationToken ct)
    {
        var token = await EnsureQishuiTokenAsync(false, ct);
        if (string.IsNullOrWhiteSpace(token)) return null;

        var payloadJson = global::System.Text.Json.JsonSerializer.Serialize(bodyObj);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var url = $"https://open.douyin.com{QishuiPrefix}{path}?{BuildQishuiQueryString()}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("User-Agent", QishuiUa);
            req.Headers.TryAddWithoutValidation("access-token", token);

            using var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var text = await resp.Content.ReadAsStringAsync(ct);
            var doc = global::System.Text.Json.JsonDocument.Parse(text);
            var errNo = doc.RootElement.GetLongProp("err_no");
            if (errNo == 28001008 || errNo == 28001007 || errNo == 28001003)
            {
                doc.Dispose();
                if (attempt == 0)
                {
                    token = await EnsureQishuiTokenAsync(true, ct);
                    if (!string.IsNullOrWhiteSpace(token)) continue;
                }
                return null;
            }
            if (errNo != 0)
            {
                doc.Dispose();
                return null;
            }
            return doc;
        }
        return null;
    }

    private static string ExtractQishuiCoverUrl(global::System.Text.Json.JsonElement urlCover)
    {
        if (urlCover.ValueKind == global::System.Text.Json.JsonValueKind.String)
        {
            return ImageUriHelper.SanitizeOrEmpty(urlCover.GetString());
        }
        if (urlCover.ValueKind != global::System.Text.Json.JsonValueKind.Object)
        {
            return string.Empty;
        }

        string? firstUrl = null;
        if (urlCover.TryGetProperty("urls", out var urlsEl) && urlsEl.ValueKind == global::System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in urlsEl.EnumerateArray())
            {
                if (item.ValueKind == global::System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    firstUrl = item.GetString()!.Trim();
                    break;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(firstUrl))
        {
            firstUrl = urlCover.GetStringProp("url").Trim();
        }
        if (string.IsNullOrWhiteSpace(firstUrl)) return string.Empty;

        bool needCompleteUrl = GetBoolProp(urlCover, "need_complete_url");
        var uri = urlCover.GetStringProp("uri").Trim();
        var templatePrefix = urlCover.GetStringProp("template_prefix").Trim();

        bool isDirectoryPrefix = firstUrl.EndsWith("/", StringComparison.Ordinal) ||
                                 firstUrl.EndsWith("/img", StringComparison.OrdinalIgnoreCase);

        // 当 urls[0] 仅为目录前缀（如 /img/）且存在 uri 时，需按 template_prefix 拼全图片地址，否则触发 E_INVALIDARG
        string candidate = firstUrl;
        if ((needCompleteUrl || isDirectoryPrefix) && !string.IsNullOrWhiteSpace(uri))
        {
            var basePart = firstUrl.TrimEnd('/');
            var uriPart = uri.TrimStart('/');
            candidate = !string.IsNullOrWhiteSpace(templatePrefix)
                ? $"{basePart}/{uriPart}~{templatePrefix}-resize:300:300.jpeg"
                : $"{basePart}/{uriPart}";
        }

        return ImageUriHelper.SanitizeOrEmpty(candidate);
    }

    private static string TryExtractQishuiNodeCover(global::System.Text.Json.JsonElement node)
    {
        if (node.ValueKind != global::System.Text.Json.JsonValueKind.Object) return string.Empty;

        if (node.TryGetProperty("display_info", out var di) && di.ValueKind == global::System.Text.Json.JsonValueKind.Object)
        {
            if (di.TryGetProperty("url_cover", out var uc0))
            {
                var c = ExtractQishuiCoverUrl(uc0);
                if (!string.IsNullOrWhiteSpace(c)) return c;
            }
        }
        if (node.TryGetProperty("base_info", out var bi) && bi.ValueKind == global::System.Text.Json.JsonValueKind.Object)
        {
            if (bi.TryGetProperty("url_cover", out var uc1))
            {
                var c = ExtractQishuiCoverUrl(uc1);
                if (!string.IsNullOrWhiteSpace(c)) return c;
            }
        }
        if (node.TryGetProperty("url_cover", out var uc2))
        {
            var c = ExtractQishuiCoverUrl(uc2);
            if (!string.IsNullOrWhiteSpace(c)) return c;
        }
        if (node.TryGetProperty("cover_url", out var uc3))
        {
            var c = ExtractQishuiCoverUrl(uc3);
            if (!string.IsNullOrWhiteSpace(c)) return c;
        }
        return string.Empty;
    }

    private static SongItem? ParseQishuiTrackItem(global::System.Text.Json.JsonElement raw)
    {
        if (raw.ValueKind != global::System.Text.Json.JsonValueKind.Object) return null;

        var track = raw;
        if (track.TryGetProperty("track_entity", out var te) && te.ValueKind == global::System.Text.Json.JsonValueKind.Object)
            track = te;
        else if (track.TryGetProperty("entity", out var ent) && ent.ValueKind == global::System.Text.Json.JsonValueKind.Object)
        {
            if (ent.TryGetProperty("media", out var med) && med.TryGetProperty("track_entity", out var mte) && mte.ValueKind == global::System.Text.Json.JsonValueKind.Object)
                track = mte;
            else if (ent.TryGetProperty("track_entity", out var ete) && ete.ValueKind == global::System.Text.Json.JsonValueKind.Object)
                track = ete;
            else if (ent.TryGetProperty("track", out var et) && et.ValueKind == global::System.Text.Json.JsonValueKind.Object)
                track = et;
        }
        else if (track.TryGetProperty("track", out var trk) && trk.ValueKind == global::System.Text.Json.JsonValueKind.Object)
            track = trk;

        var info = track.TryGetProperty("base_info", out var bi) && bi.ValueKind == global::System.Text.Json.JsonValueKind.Object ? bi : track;
        var trackId = FirstNonEmpty(info.GetStringProp("id"), track.GetStringProp("id"), raw.GetStringProp("media_id"));
        if (string.IsNullOrWhiteSpace(trackId)) return null;

        var name = FirstNonEmpty(info.GetStringProp("name"), info.GetStringProp("title"), track.GetStringProp("name"));
        if (string.IsNullOrWhiteSpace(name)) return null;

        var artists = new List<ArtistItem>();
        if (info.TryGetProperty("artists", out var artsEl) && artsEl.ValueKind == global::System.Text.Json.JsonValueKind.Array)
        {
            foreach (var a in artsEl.EnumerateArray())
            {
                var aname = FirstNonEmpty(a.GetStringProp("name"), a.GetStringProp("simple_display_name"), a.GetStringProp("title"));
                if (!string.IsNullOrWhiteSpace(aname))
                {
                    artists.Add(new ArtistItem { Name = aname });
                }
            }
        }
        if (artists.Count == 0 &&
            track.TryGetProperty("related_info", out var relInfo) &&
            relInfo.ValueKind == global::System.Text.Json.JsonValueKind.Object &&
            relInfo.TryGetProperty("artist_links", out var artistLinks) &&
            artistLinks.ValueKind == global::System.Text.Json.JsonValueKind.Array)
        {
            foreach (var a in artistLinks.EnumerateArray())
            {
                var aname = FirstNonEmpty(a.GetStringProp("name"), a.GetStringProp("simple_display_name"), a.GetStringProp("title"));
                if (!string.IsNullOrWhiteSpace(aname))
                {
                    artists.Add(new ArtistItem { Name = aname });
                }
            }
        }
        if (artists.Count == 0 && info.TryGetProperty("artist", out var artObj))
        {
            var aname = artObj.ValueKind == global::System.Text.Json.JsonValueKind.String
                ? artObj.GetString()
                : FirstNonEmpty(artObj.GetStringProp("name"), artObj.GetStringProp("title"));
            if (!string.IsNullOrWhiteSpace(aname))
            {
                artists.Add(new ArtistItem { Name = aname.Trim() });
            }
        }
        if (artists.Count == 0) artists.Add(new ArtistItem { Name = "未知歌手" });

        string albumName = string.Empty;
        string coverUrl = string.Empty;
        if (info.TryGetProperty("album", out var albEl) && albEl.ValueKind == global::System.Text.Json.JsonValueKind.Object)
        {
            albumName = FirstNonEmpty(albEl.GetStringProp("name"), albEl.GetStringProp("title"));
            coverUrl = TryExtractQishuiNodeCover(albEl);
        }
        if (track.TryGetProperty("related_info", out var relForAlb) &&
            relForAlb.ValueKind == global::System.Text.Json.JsonValueKind.Object &&
            relForAlb.TryGetProperty("album_link", out var albLink) &&
            albLink.ValueKind == global::System.Text.Json.JsonValueKind.Object)
        {
            if (string.IsNullOrWhiteSpace(albumName))
            {
                albumName = FirstNonEmpty(albLink.GetStringProp("name"), albLink.GetStringProp("title"));
            }
            if (string.IsNullOrWhiteSpace(coverUrl))
            {
                coverUrl = TryExtractQishuiNodeCover(albLink);
            }
        }
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            coverUrl = TryExtractQishuiNodeCover(track);
        }

        long rawDuration = info.GetLongProp("duration_ms");
        if (rawDuration <= 0) rawDuration = info.GetLongProp("duration");
        long durationMs = rawDuration > 0 && rawDuration < 10000 ? rawDuration * 1000L : rawDuration;

        return new SongItem
        {
            Id = $"qishui_{trackId}",
            Name = GlobalHtmlDecode(name),
            Artists = artists,
            Album = new AlbumItem { Name = GlobalHtmlDecode(albumName), PicUrl = coverUrl },
            DurationMs = durationMs,
            Source = "qishui",
            QishuiTrackId = trackId,
            PicUrl = coverUrl,
            NativeQualityHint = "汽水臻音"
        };
    }

    private async Task<List<PlaylistItem>> GetQishuiRecommendPlaylistsAsync(string categoryKeyword, int page, CancellationToken ct)
    {
        var query = string.IsNullOrWhiteSpace(categoryKeyword) || categoryKeyword == "all" ? "抖音热歌" : categoryKeyword.Trim();
        var cursor = Math.Max(0, (page - 1) * 20).ToString();
        var searchId = Guid.NewGuid().ToString();
        using var doc = await PostQishuiPlatformAsync("/search/", new
        {
            q = query,
            cursor,
            search_id = searchId,
            from_search_id = searchId,
            search_method = "input",
            search_type = "playlist",
            search_scene = "normal",
            scene_name = "search",
            ab_param = "",
            last_viewed_media = new { },
            common_params = new { }
        }, ct);

        var result = new List<PlaylistItem>();
        if (doc == null || !doc.RootElement.TryGetProperty("data", out var dataEl)) return result;
        if (!dataEl.TryGetProperty("result_groups", out var groups) || groups.ValueKind != global::System.Text.Json.JsonValueKind.Array) return result;

        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("data", out var items) || items.ValueKind != global::System.Text.Json.JsonValueKind.Array) continue;
            foreach (var entry in items.EnumerateArray())
            {
                if (!entry.TryGetProperty("entity", out var ent)) continue;
                if (!ent.TryGetProperty("playlist", out var pl) || pl.ValueKind != global::System.Text.Json.JsonValueKind.Object) continue;

                var bInfo = pl.TryGetProperty("base_info", out var bObj) && bObj.ValueKind == global::System.Text.Json.JsonValueKind.Object ? bObj : pl;
                var pid = FirstNonEmpty(bInfo.GetStringProp("id"), pl.GetStringProp("id"));
                var title = FirstNonEmpty(bInfo.GetStringProp("title"), bInfo.GetStringProp("name"), pl.GetStringProp("title"));
                if (string.IsNullOrWhiteSpace(pid) || string.IsNullOrWhiteSpace(title)) continue;

                string cover = TryExtractQishuiNodeCover(pl);

                int trackCnt = 0;
                if (pl.TryGetProperty("resource_cnt", out var rc)) trackCnt = rc.GetIntProp("track_cnt");
                else if (bInfo.TryGetProperty("resource_cnt", out var rc2)) trackCnt = rc2.GetIntProp("track_cnt");

                long collected = 0;
                if (pl.TryGetProperty("stats", out var st)) collected = st.GetLongProp("count_collected");

                string creatorName = string.Empty;
                if (pl.TryGetProperty("relation_info", out var rel) &&
                    rel.ValueKind == global::System.Text.Json.JsonValueKind.Object &&
                    rel.TryGetProperty("owner", out var owner) &&
                    owner.ValueKind == global::System.Text.Json.JsonValueKind.Object)
                {
                    creatorName = FirstNonEmpty(owner.GetStringProp("nickname"), owner.GetStringProp("name"));
                }

                var desc = FirstNonEmpty(bInfo.GetStringProp("desc"), bInfo.GetStringProp("description"));
                result.Add(new PlaylistItem
                {
                    Source = "qishui",
                    ThirdPartyId = pid,
                    Name = GlobalHtmlDecode(title),
                    Description = GlobalHtmlDecode(desc),
                    CoverImgUrl = cover,
                    TrackCount = trackCnt,
                    PlayCount = collected > 0 ? collected * 37L : 10000L,
                    CreatorName = string.IsNullOrWhiteSpace(creatorName) ? "汽水音乐精选" : creatorName
                });
            }
        }
        return result;
    }

    private async Task<PlaylistItem?> GetQishuiPlaylistDetailAsync(PlaylistItem summary, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(summary.ThirdPartyId)) return null;
        using var doc = await PostQishuiPlatformAsync("/playlist/detail/", new
        {
            playlist_id = summary.ThirdPartyId,
            owner_id = "",
            count = 100,
            cursor = "0",
            type = (string?)null,
            common_params = new { }
        }, ct);

        if (doc == null || !doc.RootElement.TryGetProperty("data", out var dataEl)) return null;
        var tracks = new List<SongItem>();
        if (dataEl.TryGetProperty("media_entities", out var entities) && entities.ValueKind == global::System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in entities.EnumerateArray())
            {
                var song = ParseQishuiTrackItem(item);
                if (song != null) tracks.Add(song);
            }
        }

        string title = summary.Name;
        string desc = summary.Description;
        string cover = summary.CoverImgUrl;
        string creatorName = summary.CreatorName;
        if (dataEl.TryGetProperty("playlist", out var pl) && pl.ValueKind == global::System.Text.Json.JsonValueKind.Object)
        {
            var bInfo = pl.TryGetProperty("base_info", out var bObj) && bObj.ValueKind == global::System.Text.Json.JsonValueKind.Object ? bObj : pl;
            title = FirstNonEmpty(bInfo.GetStringProp("title"), bInfo.GetStringProp("name"), title);
            desc = FirstNonEmpty(bInfo.GetStringProp("desc"), bInfo.GetStringProp("description"), desc);
            var parsedCover = TryExtractQishuiNodeCover(pl);
            if (!string.IsNullOrWhiteSpace(parsedCover)) cover = parsedCover;

            if (pl.TryGetProperty("relation_info", out var rel) &&
                rel.ValueKind == global::System.Text.Json.JsonValueKind.Object &&
                rel.TryGetProperty("owner", out var owner) &&
                owner.ValueKind == global::System.Text.Json.JsonValueKind.Object)
            {
                creatorName = FirstNonEmpty(owner.GetStringProp("nickname"), owner.GetStringProp("name"), creatorName);
            }
        }

        if (ImageUriHelper.HasRealImageUrl(cover))
        {
            foreach (var t in tracks)
            {
                if (string.IsNullOrWhiteSpace(t.PicUrl))
                {
                    t.PicUrl = cover;
                    if (t.Album != null) t.Album.PicUrl = cover;
                }
            }
        }

        return new PlaylistItem
        {
            Source = "qishui",
            ThirdPartyId = summary.ThirdPartyId,
            Name = GlobalHtmlDecode(title),
            Description = GlobalHtmlDecode(desc),
            CoverImgUrl = cover,
            CreatorName = string.IsNullOrWhiteSpace(creatorName) ? "汽水音乐" : creatorName,
            TrackCount = tracks.Count,
            PlayCount = summary.PlayCount,
            Tracks = tracks
        };
    }

    private async Task<List<SongItem>> GetQishuiBoardAsync(string boardKey, CancellationToken ct)
    {
        var kw = boardKey switch
        {
            "soar" => "汽水飙升榜",
            "new" => "抖音新歌榜",
            "bgm" => "抖音爆款BGM",
            "west" => "抖音欧美热歌",
            _ => "抖音热歌榜"
        };

        var playlists = await GetQishuiRecommendPlaylistsAsync(kw, 1, ct);
        var best = playlists.OrderByDescending(p => p.TrackCount).FirstOrDefault();
        if (best != null)
        {
            var detail = await GetQishuiPlaylistDetailAsync(best, ct);
            if (detail != null && detail.Tracks.Count >= 10)
            {
                return detail.Tracks;
            }
        }

        // 若榜单歌单曲目不足，直接按榜单词检索单曲，最后再兜底汽水信息流
        var searched = await SearchQishuiAsync(kw, 1, 35, ct);
        if (searched.Count >= 5) return searched;

        using var feedDoc = await PostQishuiPlatformAsync("/feed/song-tab/", new
        {
            played_media = Array.Empty<object>(),
            is_first_request = true,
            with_client_cache = false,
            is_did_first_request = false,
            count = 30,
            common_params = new { }
        }, ct);
        var feedTracks = new List<SongItem>();
        if (feedDoc != null && feedDoc.RootElement.TryGetProperty("data", out var dataEl) &&
            dataEl.TryGetProperty("items", out var items) && items.ValueKind == global::System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var song = ParseQishuiTrackItem(item);
                if (song != null) feedTracks.Add(song);
            }
        }
        return feedTracks;
    }

    private async Task<List<SongItem>> SearchQishuiAsync(string keyword, int page, int pageSize, CancellationToken ct)
    {
        var cursor = Math.Max(0, (page - 1) * pageSize).ToString();
        var searchId = Guid.NewGuid().ToString();
        using var doc = await PostQishuiPlatformAsync("/search/", new
        {
            q = keyword,
            cursor,
            search_id = searchId,
            from_search_id = searchId,
            search_method = "input",
            search_type = "track",
            search_scene = "normal",
            scene_name = "search",
            ab_param = "",
            last_viewed_media = new { },
            common_params = new { }
        }, ct);

        var result = new List<SongItem>();
        if (doc == null || !doc.RootElement.TryGetProperty("data", out var dataEl)) return result;
        if (!dataEl.TryGetProperty("result_groups", out var groups) || groups.ValueKind != global::System.Text.Json.JsonValueKind.Array) return result;

        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("data", out var items) || items.ValueKind != global::System.Text.Json.JsonValueKind.Array) continue;
            foreach (var entry in items.EnumerateArray())
            {
                var song = ParseQishuiTrackItem(entry);
                if (song != null) result.Add(song);
            }
            if (result.Count >= pageSize) break;
        }
        return result.Take(pageSize).ToList();
    }

    /// <summary>
    /// 同步读取汽水单曲候选音质流缓存（有效期内秒返，供 UI 弹出菜单即时使用）
    /// </summary>
    public bool TryGetCachedQishuiCandidates(
        string? trackId,
        out List<(string Url, int BitrateKbps, long SizeBytes)> candidates)
    {
        var cleanTrackId = trackId?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(cleanTrackId) &&
            _qishuiCandidatesCache.TryGetValue(cleanTrackId, out var entry) &&
            DateTimeOffset.UtcNow - entry.CachedAt < TimeSpan.FromMinutes(8))
        {
            candidates = entry.List;
            return true;
        }

        candidates = [];
        return false;
    }

    /// <summary>
    /// 获取汽水音乐单曲全部无加密可用音质流列表（并缓存 8 分钟）
    /// </summary>
    public async Task<List<(string Url, int BitrateKbps, long SizeBytes)>> GetQishuiPlayCandidatesAsync(
        string? trackId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trackId)) return [];
        var cleanTrackId = trackId.Trim();
        if (TryGetCachedQishuiCandidates(cleanTrackId, out var cached))
        {
            return cached;
        }

        static bool IsEncryptedStream(global::System.Text.Json.JsonElement el)
        {
            if (el.TryGetProperty("encrypt_info", out var enc) && enc.ValueKind == global::System.Text.Json.JsonValueKind.Object)
            {
                if (GetBoolProp(enc, "encrypt")) return true;
                if (!string.IsNullOrWhiteSpace(enc.GetStringProp("spade_a"))) return true;
            }
            if (!string.IsNullOrWhiteSpace(el.GetStringProp("spade_a"))) return true;
            if (!string.IsNullOrWhiteSpace(el.GetStringProp("PlayAuth"))) return true;
            if (!string.IsNullOrWhiteSpace(el.GetStringProp("play_auth"))) return true;
            if (GetBoolProp(el, "encrypt") || el.GetIntProp("Encrypt") > 0) return true;
            return false;
        }

        static void CollectFromVideoElement(global::System.Text.Json.JsonElement el, List<(string Url, int BitrateKbps, long SizeBytes)> list)
        {
            if (el.ValueKind != global::System.Text.Json.JsonValueKind.Object || IsEncryptedStream(el)) return;
            var rawUrl = FirstNonEmpty(el.GetStringProp("main_url"), el.GetStringProp("MainPlayUrl"));
            var url = TryDecodeBase64Url(rawUrl);
            if (string.IsNullOrWhiteSpace(url)) return;

            long rawBr = 0;
            long size = 0;
            if (el.TryGetProperty("video_meta", out var vm) && vm.ValueKind == global::System.Text.Json.JsonValueKind.Object)
            {
                rawBr = vm.GetLongProp("bitrate");
                size = vm.GetLongProp("size");
            }
            if (rawBr <= 0) rawBr = el.GetLongProp("bitrate");
            if (rawBr <= 0) rawBr = el.GetLongProp("Bitrate");
            if (size <= 0) size = el.GetLongProp("size");
            if (size <= 0) size = el.GetLongProp("Size");

            int kbps = rawBr > 10000 ? (int)(rawBr / 1000) : (int)rawBr;
            list.Add((url, kbps, size));
        }

        var candidates = new List<(string Url, int BitrateKbps, long SizeBytes)>();

        using var doc = await PostQishuiPlatformAsync("/media/mget/", new
        {
            media_list = new[] { new { id = cleanTrackId, type = "track" } },
            card_context = new { },
            common_params = new { }
        }, ct);

        if (doc != null &&
            doc.RootElement.TryGetProperty("data", out var dataEl) &&
            dataEl.TryGetProperty("media_list", out var mediaList) &&
            mediaList.ValueKind == global::System.Text.Json.JsonValueKind.Array)
        {
            global::System.Text.Json.JsonElement? targetTrack = null;
            foreach (var media in mediaList.EnumerateArray())
            {
                if (media.TryGetProperty("track_entity", out var te) && te.ValueKind == global::System.Text.Json.JsonValueKind.Object)
                {
                    targetTrack = te;
                    break;
                }
            }

            if (targetTrack.HasValue &&
                targetTrack.Value.TryGetProperty("player_info", out var playerInfo) &&
                playerInfo.TryGetProperty("full", out var fullInfo) &&
                fullInfo.TryGetProperty("video_model_info", out var vmInfo))
            {
                if (vmInfo.TryGetProperty("video_model", out var vmProp))
                {
                    global::System.Text.Json.JsonDocument? nestedVmDoc = null;
                    try
                    {
                        global::System.Text.Json.JsonElement vmRoot;
                        if (vmProp.ValueKind == global::System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(vmProp.GetString()))
                        {
                            nestedVmDoc = global::System.Text.Json.JsonDocument.Parse(vmProp.GetString()!);
                            vmRoot = nestedVmDoc.RootElement;
                        }
                        else
                        {
                            vmRoot = vmProp;
                        }

                        if (vmRoot.ValueKind == global::System.Text.Json.JsonValueKind.Object &&
                            vmRoot.TryGetProperty("video_list", out var videoList))
                        {
                            if (videoList.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                            {
                                foreach (var item in videoList.EnumerateArray())
                                {
                                    CollectFromVideoElement(item, candidates);
                                }
                            }
                            else if (videoList.ValueKind == global::System.Text.Json.JsonValueKind.Object)
                            {
                                foreach (var prop in videoList.EnumerateObject())
                                {
                                    CollectFromVideoElement(prop.Value, candidates);
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        nestedVmDoc?.Dispose();
                    }
                }

                if (candidates.Count == 0)
                {
                    var urlPlayerInfo = TryDecodeBase64Url(vmInfo.GetStringProp("url_player_info"));
                    if (!string.IsNullOrWhiteSpace(urlPlayerInfo))
                    {
                        try
                        {
                            var playJson = await _httpClient.GetStringAsync(urlPlayerInfo, ct);
                            using var playDoc = global::System.Text.Json.JsonDocument.Parse(playJson);
                            var r = playDoc.RootElement;
                            global::System.Text.Json.JsonElement listEl = default;
                            if (r.TryGetProperty("Result", out var res) && res.TryGetProperty("Data", out var resData) && resData.TryGetProperty("PlayInfoList", out var p1))
                                listEl = p1;
                            else if (r.TryGetProperty("data", out var d2) && d2.TryGetProperty("PlayInfoList", out var p2))
                                listEl = p2;
                            else if (r.TryGetProperty("PlayInfoList", out var p3))
                                listEl = p3;

                            if (listEl.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                            {
                                foreach (var item in listEl.EnumerateArray())
                                {
                                    CollectFromVideoElement(item, candidates);
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        // OpenSDK 返回全为 spade_a/cenc 加密流时，回退分享页 SSR 无加密音频流
        if (candidates.Count == 0)
        {
            var shareStream = await TryGetQishuiSharePlayUrlAsync(cleanTrackId, ct);
            if (!string.IsNullOrWhiteSpace(shareStream.Url))
            {
                candidates.Add((shareStream.Url!, shareStream.BitrateKbps, shareStream.SizeBytes));
            }
        }

        if (candidates.Count > 0)
        {
            _qishuiCandidatesCache[cleanTrackId] = (DateTimeOffset.UtcNow, candidates);
        }
        return candidates;
    }

    /// <summary>
    /// 获取汽水音乐单曲无加密直连播放地址（自动过滤 DRM/Spade 加密流，支持按目标音质档位挑选最近似音频流）
    /// </summary>
    public async Task<(string? Url, int BitrateKbps, long SizeBytes)> GetQishuiPlayUrlAsync(
        string? trackId, bool preferLossless, CancellationToken ct = default, string? targetQualityLevel = null)
    {
        var candidates = await GetQishuiPlayCandidatesAsync(trackId, ct);
        if (candidates.Count == 0) return (null, 0, 0);

        var target = (targetQualityLevel ?? string.Empty).Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(target))
        {
            var exact = target switch
            {
                MusicQualityLevel.Standard => candidates
                    .OrderByDescending(c => c.BitrateKbps is > 0 and < 160 ? 1 : 0)
                    .ThenBy(c => c.BitrateKbps)
                    .First(),
                MusicQualityLevel.Higher => candidates
                    .OrderByDescending(c => c.BitrateKbps is >= 160 and < 240 ? 1 : 0)
                    .ThenBy(c => Math.Abs((c.BitrateKbps > 0 ? c.BitrateKbps : 192) - 192))
                    .First(),
                MusicQualityLevel.Exhigh => candidates
                    .OrderByDescending(c => c.BitrateKbps is >= 240 and <= 400 ? 1 : 0)
                    .ThenByDescending(c => c.BitrateKbps)
                    .First(),
                _ => candidates.OrderByDescending(c => c.BitrateKbps).First()
            };
            return exact;
        }

        var chosen = preferLossless
            ? candidates.OrderByDescending(c => c.BitrateKbps).First()
            : candidates
                .OrderByDescending(c => c.BitrateKbps is >= 240 and <= 380 ? 1 : 0)
                .ThenByDescending(c => c.BitrateKbps)
                .First();
        return chosen;
    }

    private async Task<(string? Url, int BitrateKbps, long SizeBytes)> TryGetQishuiSharePlayUrlAsync(string trackId, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://music.douyin.com/qishui/share/track?track_id={Uri.EscapeDataString(trackId)}");
            req.Headers.Referrer = new Uri("https://music.douyin.com/");
            using var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return (null, 0, 0);

            var html = await resp.Content.ReadAsStringAsync(ct);
            var routerJson = ExtractBalancedJsonAfterMarker(html, "_ROUTER_DATA");
            if (string.IsNullOrWhiteSpace(routerJson)) return (null, 0, 0);

            using var doc = global::System.Text.Json.JsonDocument.Parse(routerJson);
            if (doc.RootElement.TryGetProperty("loaderData", out var ld) &&
                ld.TryGetProperty("track_page", out var tp) &&
                tp.TryGetProperty("audioWithLyricsOption", out var awm) &&
                awm.ValueKind == global::System.Text.Json.JsonValueKind.Object &&
                !GetBoolProp(awm, "encrypt"))
            {
                var url = FirstNonEmpty(awm.GetStringProp("url"), awm.GetStringProp("main_url"));
                if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    return (url, 160, 0);
                }
            }
        }
        catch
        {
        }
        return (null, 0, 0);
    }

    private static string? ExtractBalancedJsonAfterMarker(string text, string marker)
    {
        if (string.IsNullOrEmpty(text)) return null;
        int idx = text.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        int start = text.IndexOf('{', idx);
        if (start < 0) return null;

        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int i = start; i < text.Length; i++)
        {
            char ch = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
            }
            else
            {
                if (ch == '"') inString = true;
                else if (ch == '{') depth++;
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0) return text[start..(i + 1)];
                }
            }
        }
        return null;
    }

    #endregion

    #region 国际音乐平台（Spotify / Apple Music / Deezer / Tidal & YT Music）

    private static readonly Dictionary<string, string[]> SpotifyCategorySeeds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["top"] = ["37i9dQZEVXbNG2KDcFcKOF", "37i9dQZEVXbMDoHDwVN2tF", "37i9dQZF1DXcBWIGoYBM5M", "37i9dQZF1DXbYM3nMM0oPk", "37i9dQZF1DX0XUsuxWHRQd", "37i9dQZF1DX4WYpdgoIcn6"],
        ["pop"] = ["37i9dQZF1DXcBWIGoYBM5M", "37i9dQZF1DXbYM3nMM0oPk", "37i9dQZF1DX5Ejj0EkURtP", "37i9dQZF1DX4JAvHpjipBk"],
        ["hiphop"] = ["37i9dQZF1DX0XUsuxWHRQd", "37i9dQZF1DXaXB8fQg7xif", "37i9dQZF1DX8FwnYE6PRvL"],
        ["kpop"] = ["37i9dQZF1DX9tPFwDMOaN1", "37i9dQZF1DX4JAvHpjipBk", "37i9dQZF1DXd8cOUiye1o2"],
        ["chill"] = ["37i9dQZF1DX4WYpdgoIcn6", "37i9dQZF1DWWQRwui0ExPn", "37i9dQZF1DX889U0CL85jj", "37i9dQZF1DX3rxVfibe1L0"],
        ["rock"] = ["37i9dQZF1DWXRqgorJj26U", "37i9dQZF1DXcF6B6QPhFDv", "37i9dQZF1DX1rVvRgjX59F", "37i9dQZF1DX4o1oenSJRJd"]
    };

    private static readonly global::System.Text.RegularExpressions.Regex SpotifyNextDataRegex =
        new("<script\\s+id=\"__NEXT_DATA__\"[^>]*>(.*?)</script>", global::System.Text.RegularExpressions.RegexOptions.Singleline | global::System.Text.RegularExpressions.RegexOptions.Compiled);

    private async Task<PlaylistItem?> GetSpotifyEmbedPlaylistAsync(string playlistId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(playlistId)) return null;
        if (_spotifyEmbedCache.TryGetValue(playlistId, out var cached) &&
            (DateTimeOffset.UtcNow - cached.CachedAt).TotalMinutes < 30 &&
            cached.Playlist.Tracks.Count > 0)
        {
            return cached.Playlist;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://open.spotify.com/embed/playlist/{Uri.EscapeDataString(playlistId)}");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            using var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var html = await resp.Content.ReadAsStringAsync(ct);
            var m = SpotifyNextDataRegex.Match(html);
            if (!m.Success) return null;

            using var doc = global::System.Text.Json.JsonDocument.Parse(m.Groups[1].Value);
            if (!doc.RootElement.TryGetProperty("props", out var props) ||
                !props.TryGetProperty("pageProps", out var pageProps) ||
                !pageProps.TryGetProperty("state", out var state))
            {
                return null;
            }

            if (state.TryGetProperty("settings", out var settings) &&
                settings.TryGetProperty("session", out var session))
            {
                var token = session.GetStringProp("accessToken");
                var expMs = session.GetLongProp("accessTokenExpirationTimestampMs");
                if (!string.IsNullOrWhiteSpace(token) &&
                    expMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30_000)
                {
                    _spotifyAccessToken = token;
                    _spotifyTokenExpireMs = expMs;
                }
            }

            if (!state.TryGetProperty("data", out var dataEl) ||
                !dataEl.TryGetProperty("entity", out var entity))
            {
                return null;
            }

            var title = FirstNonEmpty(entity.GetStringProp("title"), entity.GetStringProp("name"), "Spotify Playlist");
            string cover = string.Empty;
            if (entity.TryGetProperty("coverArt", out var ca) && ca.TryGetProperty("sources", out var sources) && sources.ValueKind == global::System.Text.Json.JsonValueKind.Array)
            {
                foreach (var src in sources.EnumerateArray())
                {
                    var u = src.GetStringProp("url");
                    if (!string.IsNullOrWhiteSpace(u)) { cover = u; break; }
                }
            }
            if (string.IsNullOrWhiteSpace(cover) && entity.TryGetProperty("visualIdentity", out var vi) &&
                vi.TryGetProperty("image", out var imgs) && imgs.ValueKind == global::System.Text.Json.JsonValueKind.Array)
            {
                foreach (var img in imgs.EnumerateArray())
                {
                    var u = img.GetStringProp("url");
                    if (!string.IsNullOrWhiteSpace(u)) { cover = u; break; }
                }
            }

            var tracks = new List<SongItem>();
            if (entity.TryGetProperty("trackList", out var trackList) && trackList.ValueKind == global::System.Text.Json.JsonValueKind.Array)
            {
                foreach (var tr in trackList.EnumerateArray())
                {
                    var tName = tr.GetStringProp("title");
                    if (string.IsNullOrWhiteSpace(tName)) continue;

                    var uri = tr.GetStringProp("uri");
                    var hasTrackUri = uri.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase);
                    var tid = hasTrackUri
                        ? uri["spotify:track:".Length..]
                        : Guid.NewGuid().ToString("N")[..12];

                    var subTitle = tr.GetStringProp("subtitle").Replace('\u00A0', ' ').Trim();
                    var artistList = SplitArtists(string.IsNullOrWhiteSpace(subTitle) ? "Spotify Artist" : subTitle);
                    long durMs = tr.GetLongProp("duration");
                    var previewUrl = tr.TryGetProperty("audioPreview", out var ap)
                        ? ap.GetStringProp("url")
                        : string.Empty;

                    tracks.Add(new SongItem
                    {
                        Id = $"spotify_{tid}",
                        Name = GlobalHtmlDecode(tName),
                        Artists = artistList,
                        Album = new AlbumItem { Name = GlobalHtmlDecode(title), PicUrl = cover },
                        DurationMs = durMs,
                        Source = "spotify",
                        PicUrl = cover,
                        NativeTrackId = hasTrackUri ? tid : string.Empty,
                        NativePlayUrl = previewUrl,
                        NativeQualityHint = !string.IsNullOrWhiteSpace(previewUrl) ? "Preview 试听" : "Spotify 原生",
                        DisplayIndex = tracks.Count + 1
                    });
                }
            }

            if (tracks.Count == 0) return null;
            var playlist = new PlaylistItem
            {
                Source = "spotify",
                ThirdPartyId = playlistId,
                Name = GlobalHtmlDecode(title),
                Description = "Spotify 官方编辑精选歌单",
                CoverImgUrl = cover,
                CreatorName = "Spotify",
                TrackCount = tracks.Count,
                PlayCount = 1280000L,
                Tracks = tracks
            };
            _spotifyEmbedCache[playlistId] = (DateTimeOffset.UtcNow, playlist);
            return playlist;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> EnsureSpotifyAccessTokenAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!string.IsNullOrWhiteSpace(_spotifyAccessToken) && _spotifyTokenExpireMs > now + 60_000)
        {
            return _spotifyAccessToken;
        }

        await _spotifyTokenLock.WaitAsync(ct);
        try
        {
            now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!string.IsNullOrWhiteSpace(_spotifyAccessToken) && _spotifyTokenExpireMs > now + 60_000)
            {
                return _spotifyAccessToken;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, "https://open.spotify.com/embed/playlist/37i9dQZEVXbNG2KDcFcKOF");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            using var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var html = await resp.Content.ReadAsStringAsync(ct);
            var m = SpotifyNextDataRegex.Match(html);
            if (!m.Success) return null;

            using var doc = global::System.Text.Json.JsonDocument.Parse(m.Groups[1].Value);
            if (doc.RootElement.TryGetProperty("props", out var props) &&
                props.TryGetProperty("pageProps", out var pageProps) &&
                pageProps.TryGetProperty("state", out var state) &&
                state.TryGetProperty("settings", out var settings) &&
                settings.TryGetProperty("session", out var session))
            {
                var token = session.GetStringProp("accessToken");
                var expMs = session.GetLongProp("accessTokenExpirationTimestampMs");
                if (!string.IsNullOrWhiteSpace(token))
                {
                    _spotifyAccessToken = token;
                    _spotifyTokenExpireMs = expMs > now ? expMs : now + 1800_000L;
                    return _spotifyAccessToken;
                }
            }
        }
        catch
        {
        }
        finally
        {
            _spotifyTokenLock.Release();
        }
        return null;
    }

    private async Task<string?> QuerySpotifyPathfinderSearchAsync(string keyword, int limit, int offset, CancellationToken ct)
    {
        var token = await EnsureSpotifyAccessTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(token)) return null;

        var variablesJson = global::System.Text.Json.JsonSerializer.Serialize(new
        {
            searchTerm = keyword,
            offset = Math.Max(0, offset),
            limit = Math.Clamp(limit, 1, 50),
            numberOfTopResults = 5,
            includeAudiobooks = false,
            includePreReleases = false
        });
        var extensionsJson = global::System.Text.Json.JsonSerializer.Serialize(new
        {
            persistedQuery = new
            {
                version = 1,
                sha256Hash = "fcad5a3e0d5af727fb76966f06971c19cfa2275e6ff7671196753e008611873c"
            }
        });
        var url = "https://api-partner.spotify.com/pathfinder/v1/query?operationName=searchDesktop"
                + $"&variables={Uri.EscapeDataString(variablesJson)}"
                + $"&extensions={Uri.EscapeDataString(extensionsJson)}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            req.Headers.TryAddWithoutValidation("app-platform", "WebPlayer");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("Origin", "https://open.spotify.com");
            req.Headers.TryAddWithoutValidation("Referer", "https://open.spotify.com/");

            using var resp = await _httpClient.SendAsync(req, ct);
            if ((int)resp.StatusCode == 401)
            {
                _spotifyAccessToken = string.Empty;
                _spotifyTokenExpireMs = 0;
                return null;
            }
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<PlaylistItem>> GetSpotifyRecommendPlaylistsAsync(string categoryKey, int page, int pageSize, CancellationToken ct)
    {
        var key = string.IsNullOrWhiteSpace(categoryKey) ? "top" : categoryKey.Trim();
        var list = new List<PlaylistItem>();

        if (page == 1 && SpotifyCategorySeeds.TryGetValue(key, out var seeds))
        {
            using var gate = new SemaphoreSlim(5, 5);
            var tasks = seeds.Select(async id =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));
                    return await GetSpotifyEmbedPlaylistAsync(id, timeoutCts.Token);
                }
                catch
                {
                    return null;
                }
                finally
                {
                    gate.Release();
                }
            });
            var fetched = await Task.WhenAll(tasks);
            foreach (var pl in fetched)
            {
                if (pl != null && list.All(x => !string.Equals(x.ThirdPartyId, pl.ThirdPartyId, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(pl);
                }
            }
        }

        // 仅使用 Spotify 原生 Pathfinder GraphQL 补充同流派公开歌单，绝不混入其他平台
        var searchQuery = key.ToLowerInvariant() switch
        {
            "pop" => "Pop Hits",
            "hiphop" => "Hip Hop Rap",
            "kpop" => "K-Pop",
            "chill" => "Chill Lofi",
            "rock" => "Rock Classics",
            "top" => "Top Hits",
            _ => key
        };
        var extraCount = Math.Max(12, pageSize - list.Count);
        var offset = page == 1 ? 0 : Math.Max(0, (page - 1) * pageSize);
        var raw = await QuerySpotifyPathfinderSearchAsync(searchQuery, extraCount, offset, ct);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                using var doc = global::System.Text.Json.JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("data", out var dataEl) &&
                    dataEl.TryGetProperty("searchV2", out var s2) &&
                    s2.TryGetProperty("playlists", out var pls) &&
                    pls.TryGetProperty("items", out var items) &&
                    items.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var entry in items.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("data", out var plData) || plData.ValueKind != global::System.Text.Json.JsonValueKind.Object)
                            continue;

                        var uri = plData.GetStringProp("uri");
                        if (!uri.StartsWith("spotify:playlist:", StringComparison.OrdinalIgnoreCase)) continue;
                        var pid = uri["spotify:playlist:".Length..];
                        var name = plData.GetStringProp("name");
                        if (string.IsNullOrWhiteSpace(pid) || string.IsNullOrWhiteSpace(name)) continue;

                        if (list.Any(x => string.Equals(x.ThirdPartyId, pid, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        var creator = plData.TryGetProperty("ownerV2", out var ownerV2) &&
                                      ownerV2.TryGetProperty("data", out var ownerData)
                            ? FirstNonEmpty(ownerData.GetStringProp("name"), "Spotify")
                            : "Spotify";

                        string cover = string.Empty;
                        if (plData.TryGetProperty("images", out var imgs) &&
                            imgs.TryGetProperty("items", out var imgItems) &&
                            imgItems.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var imgItem in imgItems.EnumerateArray())
                            {
                                if (imgItem.TryGetProperty("sources", out var srcs) && srcs.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                                {
                                    foreach (var src in srcs.EnumerateArray())
                                    {
                                        var u = src.GetStringProp("url");
                                        if (!string.IsNullOrWhiteSpace(u)) { cover = u; break; }
                                    }
                                }
                                if (!string.IsNullOrWhiteSpace(cover)) break;
                            }
                        }

                        list.Add(new PlaylistItem
                        {
                            Source = "spotify",
                            ThirdPartyId = pid,
                            Name = GlobalHtmlDecode(name),
                            Description = $"Spotify 原生精选歌单 · {creator}",
                            CoverImgUrl = cover,
                            CreatorName = GlobalHtmlDecode(creator),
                            PlayCount = 650000L
                        });
                    }
                }
            }
            catch
            {
            }
        }
        return list;
    }

    private async Task<List<SongItem>> GetSpotifyBoardAsync(string boardKey, CancellationToken ct)
    {
        var resolvedId = string.Equals(boardKey, "hot", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(boardKey)
            ? "37i9dQZEVXbMDoHDwVN2tF"
            : boardKey.Trim();
        var embed = await GetSpotifyEmbedPlaylistAsync(resolvedId, ct);
        return embed?.Tracks ?? [];
    }

    private async Task<List<SongItem>> SearchSpotifyAsync(string keyword, int page, int pageSize, CancellationToken ct)
    {
        var offset = Math.Max(0, (page - 1) * pageSize);
        var raw = await QuerySpotifyPathfinderSearchAsync(keyword, pageSize, offset, ct);
        if (string.IsNullOrWhiteSpace(raw)) return [];

        try
        {
            using var doc = global::System.Text.Json.JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
                !dataEl.TryGetProperty("searchV2", out var s2) ||
                !s2.TryGetProperty("tracksV2", out var tv2) ||
                !tv2.TryGetProperty("items", out var items) ||
                items.ValueKind != global::System.Text.Json.JsonValueKind.Array)
            {
                return [];
            }

            var list = new List<SongItem>();
            foreach (var entry in items.EnumerateArray())
            {
                if (!entry.TryGetProperty("item", out var itemObj) ||
                    !itemObj.TryGetProperty("data", out var data) ||
                    data.ValueKind != global::System.Text.Json.JsonValueKind.Object)
                {
                    continue;
                }

                var tName = data.GetStringProp("name");
                if (string.IsNullOrWhiteSpace(tName)) continue;

                var tid = data.GetStringProp("id");
                if (string.IsNullOrWhiteSpace(tid))
                {
                    var uri = data.GetStringProp("uri");
                    if (uri.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase))
                        tid = uri["spotify:track:".Length..];
                }
                if (string.IsNullOrWhiteSpace(tid)) continue;

                var artists = new List<ArtistItem>();
                if (data.TryGetProperty("artists", out var artObj) &&
                    artObj.TryGetProperty("items", out var artItems) &&
                    artItems.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var artItem in artItems.EnumerateArray())
                    {
                        if (artItem.TryGetProperty("profile", out var profile))
                        {
                            var aName = profile.GetStringProp("name");
                            if (!string.IsNullOrWhiteSpace(aName))
                                artists.Add(new ArtistItem { Name = GlobalHtmlDecode(aName) });
                        }
                    }
                }
                if (artists.Count == 0) artists.Add(new ArtistItem { Name = "Spotify Artist" });

                string albumName = string.Empty;
                string cover = string.Empty;
                if (data.TryGetProperty("albumOfTrack", out var alb) && alb.ValueKind == global::System.Text.Json.JsonValueKind.Object)
                {
                    albumName = alb.GetStringProp("name");
                    if (alb.TryGetProperty("coverArt", out var ca) &&
                        ca.TryGetProperty("sources", out var srcs) &&
                        srcs.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                    {
                        int bestW = 0;
                        foreach (var src in srcs.EnumerateArray())
                        {
                            var u = src.GetStringProp("url");
                            var w = src.GetIntProp("width");
                            if (!string.IsNullOrWhiteSpace(u) && (w >= bestW || string.IsNullOrEmpty(cover)))
                            {
                                cover = u;
                                bestW = w;
                            }
                        }
                    }
                }

                long durMs = data.TryGetProperty("duration", out var durObj)
                    ? durObj.GetLongProp("totalMilliseconds")
                    : 0L;

                list.Add(new SongItem
                {
                    Id = $"spotify_{tid}",
                    Name = GlobalHtmlDecode(tName),
                    Artists = artists,
                    Album = new AlbumItem { Name = GlobalHtmlDecode(albumName), PicUrl = cover },
                    DurationMs = durMs,
                    Source = "spotify",
                    PicUrl = cover,
                    NativeTrackId = tid,
                    NativeQualityHint = "Preview 试听",
                    DisplayIndex = list.Count + 1
                });
            }
            return list;
        }
        catch
        {
            return [];
        }
    }

    private static string UpgradeAppleArtwork(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        return url
            .Replace("170x170bb", "300x300bb", StringComparison.OrdinalIgnoreCase)
            .Replace("100x100bb", "300x300bb", StringComparison.OrdinalIgnoreCase)
            .Replace("60x60bb", "300x300bb", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<SongItem>> GetAppleBoardAsync(string countryCode, CancellationToken ct)
    {
        var cc = string.IsNullOrWhiteSpace(countryCode) ? "us" : countryCode.Trim().ToLowerInvariant();
        var url = $"https://itunes.apple.com/{Uri.EscapeDataString(cc)}/rss/topsongs/limit=50/json";
        try
        {
            var json = await _httpClient.GetStringAsync(url, ct);
            using var doc = global::System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("feed", out var feed) || !feed.TryGetProperty("entry", out var entryEl)) return [];

            var entries = new List<global::System.Text.Json.JsonElement>();
            if (entryEl.ValueKind == global::System.Text.Json.JsonValueKind.Array)
            {
                foreach (var e in entryEl.EnumerateArray()) entries.Add(e);
            }
            else if (entryEl.ValueKind == global::System.Text.Json.JsonValueKind.Object)
            {
                entries.Add(entryEl);
            }

            var result = new List<SongItem>();
            foreach (var item in entries)
            {
                var name = item.TryGetProperty("im:name", out var nm) ? nm.GetStringProp("label") : null;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var artist = FirstNonEmpty(item.TryGetProperty("im:artist", out var art) ? art.GetStringProp("label") : null, "Apple Artist");
                var album = item.TryGetProperty("im:collection", out var col) && col.TryGetProperty("im:name", out var cnm)
                    ? cnm.GetStringProp("label")
                    : string.Empty;

                string cover = string.Empty;
                if (item.TryGetProperty("im:image", out var imgs) && imgs.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var img in imgs.EnumerateArray())
                    {
                        var l = img.GetStringProp("label");
                        if (!string.IsNullOrWhiteSpace(l)) cover = UpgradeAppleArtwork(l);
                    }
                }

                string previewUrl = string.Empty;
                if (item.TryGetProperty("link", out var linkEl) && linkEl.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var lk in linkEl.EnumerateArray())
                    {
                        if (lk.TryGetProperty("attributes", out var la) &&
                            string.Equals(la.GetStringProp("rel"), "enclosure", StringComparison.OrdinalIgnoreCase))
                        {
                            var href = la.GetStringProp("href");
                            if (!string.IsNullOrWhiteSpace(href)) { previewUrl = href; break; }
                        }
                    }
                }

                string rawTrackId = string.Empty;
                if (item.TryGetProperty("id", out var idObj) && idObj.TryGetProperty("attributes", out var attrs))
                {
                    rawTrackId = attrs.GetStringProp("im:id");
                }
                var trackId = string.IsNullOrWhiteSpace(rawTrackId) ? Guid.NewGuid().ToString("N")[..10] : rawTrackId;

                result.Add(new SongItem
                {
                    Id = $"apple_{trackId}",
                    Name = GlobalHtmlDecode(name),
                    Artists = SplitArtists(artist),
                    Album = new AlbumItem { Name = GlobalHtmlDecode(album), PicUrl = cover },
                    Source = "apple",
                    PicUrl = cover,
                    NativeTrackId = rawTrackId,
                    NativePlayUrl = previewUrl,
                    NativeQualityHint = "AAC 256k Preview"
                });
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    private async Task<List<PlaylistItem>> GetAppleRecommendPlaylistsAsync(string countryCode, CancellationToken ct)
    {
        var cc = string.IsNullOrWhiteSpace(countryCode) ? "us" : countryCode.Trim().ToLowerInvariant();
        var url = $"https://itunes.apple.com/{Uri.EscapeDataString(cc)}/rss/topalbums/limit=30/json";
        try
        {
            var json = await _httpClient.GetStringAsync(url, ct);
            using var doc = global::System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("feed", out var feed) || !feed.TryGetProperty("entry", out var entryEl)) return [];

            var entries = new List<global::System.Text.Json.JsonElement>();
            if (entryEl.ValueKind == global::System.Text.Json.JsonValueKind.Array)
            {
                foreach (var e in entryEl.EnumerateArray()) entries.Add(e);
            }
            else if (entryEl.ValueKind == global::System.Text.Json.JsonValueKind.Object)
            {
                entries.Add(entryEl);
            }

            var result = new List<PlaylistItem>();
            foreach (var item in entries)
            {
                var albumName = item.TryGetProperty("im:name", out var nm) ? nm.GetStringProp("label") : null;
                if (string.IsNullOrWhiteSpace(albumName)) continue;

                var artistName = FirstNonEmpty(item.TryGetProperty("im:artist", out var art) ? art.GetStringProp("label") : null, "Apple Music");
                int itemCount = 0;
                if (item.TryGetProperty("im:itemCount", out var ic) && int.TryParse(ic.GetStringProp("label"), out var cnt))
                    itemCount = cnt;

                string cover = string.Empty;
                if (item.TryGetProperty("im:image", out var imgs) && imgs.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var img in imgs.EnumerateArray())
                    {
                        var l = img.GetStringProp("label");
                        if (!string.IsNullOrWhiteSpace(l)) cover = UpgradeAppleArtwork(l);
                    }
                }

                string colId = string.Empty;
                if (item.TryGetProperty("id", out var idObj) && idObj.TryGetProperty("attributes", out var attrs))
                {
                    colId = attrs.GetStringProp("im:id");
                }
                if (string.IsNullOrWhiteSpace(colId)) continue;

                result.Add(new PlaylistItem
                {
                    Source = "apple",
                    ThirdPartyId = colId,
                    Name = GlobalHtmlDecode(albumName),
                    Description = $"Apple Music ({cc.ToUpperInvariant()}) 热门专辑 · {artistName}",
                    CoverImgUrl = cover,
                    CreatorName = GlobalHtmlDecode(artistName),
                    TrackCount = itemCount,
                    PlayCount = 320000L
                });
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    private async Task<PlaylistItem?> GetAppleAlbumDetailAsync(PlaylistItem summary, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(summary.ThirdPartyId)) return null;
        string[] countries = ["US", "JP", "KR", "HK", "GB", "TW"];
        var tracks = new List<SongItem>();

        foreach (var cc in countries)
        {
            try
            {
                var url = $"https://itunes.apple.com/lookup?id={Uri.EscapeDataString(summary.ThirdPartyId)}&entity=song&country={cc}";
                var json = await _httpClient.GetStringAsync(url, ct);
                using var doc = global::System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != global::System.Text.Json.JsonValueKind.Array)
                    continue;

                foreach (var item in results.EnumerateArray())
                {
                    if (!string.Equals(item.GetStringProp("wrapperType"), "track", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var tName = item.GetStringProp("trackName");
                    if (string.IsNullOrWhiteSpace(tName)) continue;

                    var tid = item.GetLongProp("trackId");
                    var aName = FirstNonEmpty(item.GetStringProp("artistName"), summary.CreatorName, "Apple Artist");
                    var cName = FirstNonEmpty(item.GetStringProp("collectionName"), summary.Name);
                    var durMs = item.GetLongProp("trackTimeMillis");
                    var previewUrl = item.GetStringProp("previewUrl");
                    var cover = UpgradeAppleArtwork(item.GetStringProp("artworkUrl100"));
                    if (string.IsNullOrWhiteSpace(cover)) cover = summary.CoverImgUrl;

                    tracks.Add(new SongItem
                    {
                        Id = tid > 0 ? $"apple_{tid}" : $"apple_{Guid.NewGuid():N}",
                        Name = GlobalHtmlDecode(tName),
                        Artists = SplitArtists(aName),
                        Album = new AlbumItem { Name = GlobalHtmlDecode(cName), PicUrl = cover },
                        DurationMs = durMs,
                        Source = "apple",
                        PicUrl = cover,
                        NativeTrackId = tid > 0 ? tid.ToString() : string.Empty,
                        NativePlayUrl = previewUrl,
                        NativeQualityHint = "AAC 256k Preview"
                    });
                }

                if (tracks.Count > 0) break;
            }
            catch
            {
            }
        }

        return new PlaylistItem
        {
            Source = "apple",
            ThirdPartyId = summary.ThirdPartyId,
            Name = summary.Name,
            Description = summary.Description,
            CoverImgUrl = summary.CoverImgUrl,
            CreatorName = summary.CreatorName,
            TrackCount = tracks.Count,
            PlayCount = summary.PlayCount,
            Tracks = tracks
        };
    }

    private async Task<List<SongItem>> SearchAppleAsync(string keyword, int page, int pageSize, CancellationToken ct)
    {
        var offset = Math.Max(0, (page - 1) * pageSize);
        var url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(keyword)}&country=US&media=music&entity=song&limit={pageSize}&offset={offset}";
        try
        {
            var json = await _httpClient.GetStringAsync(url, ct);
            using var doc = global::System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != global::System.Text.Json.JsonValueKind.Array)
                return [];

            var list = new List<SongItem>();
            foreach (var item in results.EnumerateArray())
            {
                var tName = item.GetStringProp("trackName");
                if (string.IsNullOrWhiteSpace(tName)) continue;

                var tid = item.GetLongProp("trackId");
                var artist = FirstNonEmpty(item.GetStringProp("artistName"), "Apple Artist");
                var album = item.GetStringProp("collectionName");
                var durMs = item.GetLongProp("trackTimeMillis");
                var previewUrl = item.GetStringProp("previewUrl");
                var cover = UpgradeAppleArtwork(item.GetStringProp("artworkUrl100"));

                list.Add(new SongItem
                {
                    Id = tid > 0 ? $"apple_{tid}" : $"apple_{Guid.NewGuid():N}",
                    Name = GlobalHtmlDecode(tName),
                    Artists = SplitArtists(artist),
                    Album = new AlbumItem { Name = GlobalHtmlDecode(album), PicUrl = cover },
                    DurationMs = durMs,
                    Source = "apple",
                    PicUrl = cover,
                    NativeTrackId = tid > 0 ? tid.ToString() : string.Empty,
                    NativePlayUrl = previewUrl,
                    NativeQualityHint = "AAC 256k Preview"
                });
            }
            return list;
        }
        catch
        {
            return [];
        }
    }

    private async Task<List<PlaylistItem>> GetDeezerRecommendPlaylistsAsync(
        string categoryKey, int page, int pageSize, CancellationToken ct)
    {
        var cat = string.IsNullOrWhiteSpace(categoryKey) ? "all" : categoryKey.Trim();
        var index = Math.Max(0, (page - 1) * pageSize);
        var url = cat == "all" && page == 1
            ? $"https://api.deezer.com/chart/0/playlists?limit={pageSize}"
            : $"https://api.deezer.com/search/playlist?q={Uri.EscapeDataString(cat == "all" ? "Top Hits" : cat)}&index={index}&limit={pageSize}";

        try
        {
            var json = await _httpClient.GetStringAsync(url, ct);
            using var doc = global::System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != global::System.Text.Json.JsonValueKind.Array)
                return [];

            var list = new List<PlaylistItem>();
            foreach (var item in dataEl.EnumerateArray())
            {
                var id = item.GetLongProp("id");
                var title = item.GetStringProp("title");
                if (id <= 0 || string.IsNullOrWhiteSpace(title)) continue;

                var cover = FirstNonEmpty(item.GetStringProp("picture_big"), item.GetStringProp("picture_medium"));
                var nbTracks = item.GetIntProp("nb_tracks");
                var creator = FirstNonEmpty(item.TryGetProperty("user", out var u) ? u.GetStringProp("name") : null, "Deezer");

                list.Add(new PlaylistItem
                {
                    Source = "deezer",
                    ThirdPartyId = id.ToString(),
                    Name = GlobalHtmlDecode(title),
                    Description = $"Deezer 精选歌单 · {creator}",
                    CoverImgUrl = cover,
                    CreatorName = GlobalHtmlDecode(creator),
                    TrackCount = nbTracks,
                    PlayCount = 450000L
                });
            }
            return list;
        }
        catch
        {
            return [];
        }
    }

    private async Task<PlaylistItem?> GetDeezerPlaylistDetailAsync(PlaylistItem summary, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(summary.ThirdPartyId)) return null;

        try
        {
            var json = await _httpClient.GetStringAsync($"https://api.deezer.com/playlist/{Uri.EscapeDataString(summary.ThirdPartyId)}", ct);
            using var doc = global::System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var title = FirstNonEmpty(root.GetStringProp("title"), summary.Name);
            var desc = FirstNonEmpty(root.GetStringProp("description"), summary.Description);
            var cover = FirstNonEmpty(root.GetStringProp("picture_big"), root.GetStringProp("picture_medium"), summary.CoverImgUrl);

            var tracks = new List<SongItem>();
            if (root.TryGetProperty("tracks", out var tObj) &&
                tObj.TryGetProperty("data", out var dataEl) &&
                dataEl.ValueKind == global::System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in dataEl.EnumerateArray())
                {
                    var song = ParseDeezerTrackItem(item, tracks.Count + 1);
                    if (song != null) tracks.Add(song);
                }
            }

            return new PlaylistItem
            {
                Source = "deezer",
                ThirdPartyId = summary.ThirdPartyId,
                Name = GlobalHtmlDecode(title),
                Description = GlobalHtmlDecode(desc),
                CoverImgUrl = cover,
                CreatorName = summary.CreatorName,
                TrackCount = tracks.Count,
                PlayCount = summary.PlayCount,
                Tracks = tracks
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<SongItem>> GetDeezerBoardAsync(string boardKey, CancellationToken ct)
    {
        var key = string.IsNullOrWhiteSpace(boardKey) ? "0" : boardKey.Trim();
        if (key == "0")
        {
            try
            {
                var json = await _httpClient.GetStringAsync("https://api.deezer.com/chart/0/tracks?limit=50", ct);
                using var doc = global::System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != global::System.Text.Json.JsonValueKind.Array)
                    return [];

                var list = new List<SongItem>();
                foreach (var item in dataEl.EnumerateArray())
                {
                    var song = ParseDeezerTrackItem(item, list.Count + 1);
                    if (song != null) list.Add(song);
                }
                return list;
            }
            catch
            {
                return [];
            }
        }

        var detail = await GetDeezerPlaylistDetailAsync(new PlaylistItem
        {
            Source = "deezer",
            ThirdPartyId = key,
            Name = "Deezer Chart"
        }, ct);
        return detail?.Tracks ?? [];
    }

    private async Task<List<SongItem>> SearchDeezerAsync(string keyword, int page, int pageSize, CancellationToken ct)
    {
        var index = Math.Max(0, (page - 1) * pageSize);
        var url = $"https://api.deezer.com/search?q={Uri.EscapeDataString(keyword)}&index={index}&limit={pageSize}";
        try
        {
            var json = await _httpClient.GetStringAsync(url, ct);
            using var doc = global::System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != global::System.Text.Json.JsonValueKind.Array)
                return [];

            var list = new List<SongItem>();
            foreach (var item in dataEl.EnumerateArray())
            {
                var song = ParseDeezerTrackItem(item, list.Count + 1);
                if (song != null) list.Add(song);
            }
            return list;
        }
        catch
        {
            return [];
        }
    }

    private static SongItem? ParseDeezerTrackItem(global::System.Text.Json.JsonElement item, int displayIndex = 0)
    {
        if (item.ValueKind != global::System.Text.Json.JsonValueKind.Object) return null;
        var tid = item.GetLongProp("id");
        var title = FirstNonEmpty(item.GetStringProp("title"), item.GetStringProp("title_short"));
        if (tid <= 0 || string.IsNullOrWhiteSpace(title)) return null;

        var artistName = FirstNonEmpty(item.TryGetProperty("artist", out var art) ? art.GetStringProp("name") : null, "Unknown Artist");
        string albumName = string.Empty;
        string cover = string.Empty;
        if (item.TryGetProperty("album", out var alb) && alb.ValueKind == global::System.Text.Json.JsonValueKind.Object)
        {
            albumName = alb.GetStringProp("title");
            cover = FirstNonEmpty(alb.GetStringProp("cover_big"), alb.GetStringProp("cover_medium"));
        }
        var durSec = item.GetLongProp("duration");
        var previewUrl = item.GetStringProp("preview");

        return new SongItem
        {
            Id = $"deezer_{tid}",
            Name = GlobalHtmlDecode(title),
            Artists = SplitArtists(artistName),
            Album = new AlbumItem { Name = GlobalHtmlDecode(albumName), PicUrl = cover },
            DurationMs = durSec * 1000L,
            Source = "deezer",
            PicUrl = cover,
            NativeTrackId = tid.ToString(),
            NativePlayUrl = previewUrl,
            NativeQualityHint = "MP3 128k Preview",
            DisplayIndex = displayIndex
        };
    }

    // ---------- Tidal 原生 v1 API ----------
    private const string TidalPublicClientToken = "CzET4vdadNUFQ5JU";

    private static string BuildTidalCoverUrl(string? imageUuid)
    {
        if (string.IsNullOrWhiteSpace(imageUuid)) return string.Empty;
        return $"https://resources.tidal.com/images/{imageUuid.Trim().Replace('-', '/')}/320x320.jpg";
    }

    private async Task<string?> SendTidalGetAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("x-tidal-token", TidalPublicClientToken);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    private static SongItem? ParseTidalTrackElement(global::System.Text.Json.JsonElement rawEl, string fallbackCover = "", int displayIndex = 0)
    {
        var item = rawEl;
        if (rawEl.ValueKind == global::System.Text.Json.JsonValueKind.Object &&
            rawEl.TryGetProperty("item", out var inner) &&
            inner.ValueKind == global::System.Text.Json.JsonValueKind.Object)
        {
            item = inner;
        }
        if (item.ValueKind != global::System.Text.Json.JsonValueKind.Object) return null;

        var tid = item.GetLongProp("id");
        var title = item.GetStringProp("title");
        if (tid <= 0 || string.IsNullOrWhiteSpace(title)) return null;

        var artists = new List<ArtistItem>();
        if (item.TryGetProperty("artists", out var artArr) && artArr.ValueKind == global::System.Text.Json.JsonValueKind.Array)
        {
            foreach (var a in artArr.EnumerateArray())
            {
                var an = a.GetStringProp("name");
                if (!string.IsNullOrWhiteSpace(an))
                    artists.Add(new ArtistItem { Name = GlobalHtmlDecode(an) });
            }
        }
        if (artists.Count == 0 && item.TryGetProperty("artist", out var artObj))
        {
            var an = artObj.GetStringProp("name");
            if (!string.IsNullOrWhiteSpace(an))
                artists.Add(new ArtistItem { Name = GlobalHtmlDecode(an) });
        }
        if (artists.Count == 0) artists.Add(new ArtistItem { Name = "Tidal Artist" });

        string albumName = string.Empty;
        string cover = fallbackCover;
        if (item.TryGetProperty("album", out var alb) && alb.ValueKind == global::System.Text.Json.JsonValueKind.Object)
        {
            albumName = alb.GetStringProp("title");
            var albumCover = BuildTidalCoverUrl(alb.GetStringProp("cover"));
            if (!string.IsNullOrWhiteSpace(albumCover)) cover = albumCover;
        }
        var durSec = item.GetLongProp("duration");

        return new SongItem
        {
            Id = $"tidal_{tid}",
            Name = GlobalHtmlDecode(title),
            Artists = artists,
            Album = new AlbumItem { Name = GlobalHtmlDecode(albumName), PicUrl = cover },
            DurationMs = durSec * 1000L,
            Source = "tidal",
            PicUrl = cover,
            NativeTrackId = tid.ToString(),
            NativeQualityHint = "Tidal AAC 96k Preview",
            DisplayIndex = displayIndex
        };
    }

    private async Task<List<PlaylistItem>> GetTidalRecommendPlaylistsAsync(
        string categoryKey, int page, int pageSize, CancellationToken ct)
    {
        var q = string.IsNullOrWhiteSpace(categoryKey) || string.Equals(categoryKey, "all", StringComparison.OrdinalIgnoreCase)
            ? "Top Hits"
            : categoryKey.Trim();
        var limit = Math.Clamp(pageSize, 1, 50);
        var offset = Math.Max(0, (page - 1) * limit);
        var url = $"https://api.tidal.com/v1/search/playlists?query={Uri.EscapeDataString(q)}&limit={limit}&offset={offset}&countryCode=US";

        var json = await SendTidalGetAsync(url, ct);
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var doc = global::System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != global::System.Text.Json.JsonValueKind.Array)
                return [];

            var list = new List<PlaylistItem>();
            foreach (var item in items.EnumerateArray())
            {
                var uuid = item.GetStringProp("uuid");
                var title = item.GetStringProp("title");
                if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(title)) continue;

                var cover = BuildTidalCoverUrl(FirstNonEmpty(item.GetStringProp("squareImage"), item.GetStringProp("image")));
                var creator = FirstNonEmpty(item.TryGetProperty("creator", out var cr) ? cr.GetStringProp("name") : null, "TIDAL");
                var nbTracks = item.GetIntProp("numberOfTracks");

                list.Add(new PlaylistItem
                {
                    Source = "tidal",
                    ThirdPartyId = uuid,
                    Name = GlobalHtmlDecode(title),
                    Description = $"Tidal 原生精选歌单 · {creator}",
                    CoverImgUrl = cover,
                    CreatorName = GlobalHtmlDecode(creator),
                    TrackCount = nbTracks,
                    PlayCount = 380000L
                });
            }
            return list;
        }
        catch
        {
            return [];
        }
    }

    private async Task<PlaylistItem?> GetTidalNativePlaylistDetailAsync(PlaylistItem summary, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(summary.ThirdPartyId)) return null;
        var url = $"https://api.tidal.com/v1/playlists/{Uri.EscapeDataString(summary.ThirdPartyId)}/items?limit=100&countryCode=US";
        var json = await SendTidalGetAsync(url, ct);
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = global::System.Text.Json.JsonDocument.Parse(json);
            var tracks = new List<SongItem>();
            if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == global::System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var song = ParseTidalTrackElement(item, summary.CoverImgUrl, tracks.Count + 1);
                    if (song != null) tracks.Add(song);
                }
            }

            return new PlaylistItem
            {
                Source = "tidal",
                ThirdPartyId = summary.ThirdPartyId,
                Name = summary.Name,
                Description = summary.Description,
                CoverImgUrl = summary.CoverImgUrl,
                CreatorName = summary.CreatorName,
                TrackCount = tracks.Count,
                PlayCount = summary.PlayCount,
                Tracks = tracks
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<SongItem>> GetTidalBoardAsync(string boardKey, CancellationToken ct)
    {
        var query = boardKey.ToLowerInvariant() switch
        {
            "audiophile" => "Jazz Audiophile",
            "global" => "Global Top Hits",
            "us" => "US Pop Hits",
            "jp" => "J-Pop Hits",
            "ost" => "Hans Zimmer Soundtrack",
            _ => "Top Hits"
        };

        var playlists = await GetTidalRecommendPlaylistsAsync(query, 1, 3, ct);
        foreach (var pl in playlists)
        {
            var detail = await GetTidalNativePlaylistDetailAsync(pl, ct);
            if (detail != null && detail.Tracks.Count > 0)
            {
                return detail.Tracks;
            }
        }
        return await SearchTidalNativeAsync(query, 1, 40, ct);
    }

    private async Task<List<SongItem>> SearchTidalNativeAsync(string keyword, int page, int pageSize, CancellationToken ct)
    {
        var limit = Math.Clamp(pageSize, 1, 50);
        var offset = Math.Max(0, (page - 1) * limit);
        var url = $"https://api.tidal.com/v1/search/tracks?query={Uri.EscapeDataString(keyword)}&limit={limit}&offset={offset}&countryCode=US";

        var json = await SendTidalGetAsync(url, ct);
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var doc = global::System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != global::System.Text.Json.JsonValueKind.Array)
                return [];

            var list = new List<SongItem>();
            foreach (var item in items.EnumerateArray())
            {
                var song = ParseTidalTrackElement(item, string.Empty, list.Count + 1);
                if (song != null) list.Add(song);
            }
            return list;
        }
        catch
        {
            return [];
        }
    }

    // ---------- YouTube Music 原生 Innertube API ----------
    private async Task<string?> PostYtMusicInnertubeAsync(string endpoint, object bodyObj, CancellationToken ct)
    {
        var url = $"https://music.youtube.com/youtubei/v1/{endpoint}?prettyPrint=false";
        var jsonBody = global::System.Text.Json.JsonSerializer.Serialize(bodyObj);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonBody, global::System.Text.Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("Origin", "https://music.youtube.com");
            req.Headers.TryAddWithoutValidation("Referer", "https://music.youtube.com/");
            req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            using var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    private static object BuildYtmWebRemixContext() => new
    {
        client = new
        {
            clientName = "WEB_REMIX",
            clientVersion = "1.20261110.04.00",
            hl = "zh-CN",
            gl = "US"
        }
    };

    private static void CollectYtmRenderers(global::System.Text.Json.JsonElement el, List<global::System.Text.Json.JsonElement> output)
    {
        if (el.ValueKind == global::System.Text.Json.JsonValueKind.Object)
        {
            if (el.TryGetProperty("musicResponsiveListItemRenderer", out var renderer) &&
                renderer.ValueKind == global::System.Text.Json.JsonValueKind.Object)
            {
                output.Add(renderer);
                return;
            }
            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Value.ValueKind is global::System.Text.Json.JsonValueKind.Object or global::System.Text.Json.JsonValueKind.Array)
                {
                    CollectYtmRenderers(prop.Value, output);
                }
            }
        }
        else if (el.ValueKind == global::System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind is global::System.Text.Json.JsonValueKind.Object or global::System.Text.Json.JsonValueKind.Array)
                {
                    CollectYtmRenderers(item, output);
                }
            }
        }
    }

    private static string GetYtmRunsText(global::System.Text.Json.JsonElement textObj)
    {
        if (textObj.ValueKind != global::System.Text.Json.JsonValueKind.Object ||
            !textObj.TryGetProperty("runs", out var runs) ||
            runs.ValueKind != global::System.Text.Json.JsonValueKind.Array)
        {
            return string.Empty;
        }

        var sb = new global::System.Text.StringBuilder();
        foreach (var r in runs.EnumerateArray())
        {
            sb.Append(r.GetStringProp("text"));
        }
        return sb.ToString().Trim();
    }

    private static string GetYtmBestThumbnail(global::System.Text.Json.JsonElement renderer)
    {
        global::System.Text.Json.JsonElement thumbs = default;
        bool hasThumbs = false;
        if (renderer.TryGetProperty("thumbnail", out var t))
        {
            if (t.TryGetProperty("musicThumbnailRenderer", out var mtr) &&
                mtr.TryGetProperty("thumbnail", out var mtrThumb) &&
                mtrThumb.TryGetProperty("thumbnails", out thumbs) &&
                thumbs.ValueKind == global::System.Text.Json.JsonValueKind.Array)
            {
                hasThumbs = true;
            }
            else if (t.TryGetProperty("thumbnails", out thumbs) &&
                     thumbs.ValueKind == global::System.Text.Json.JsonValueKind.Array)
            {
                hasThumbs = true;
            }
        }

        if (!hasThumbs) return string.Empty;
        string best = string.Empty;
        foreach (var item in thumbs.EnumerateArray())
        {
            var u = item.GetStringProp("url");
            if (!string.IsNullOrWhiteSpace(u)) best = u;
        }
        return best;
    }

    private static long TryParseDurationTextMs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0L;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3) return 0L;
        long totalSec = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!long.TryParse(parts[i], out var v) || v < 0 || (i > 0 && v >= 60))
                return 0L;
            totalSec = totalSec * 60 + v;
        }
        return totalSec > 0 ? totalSec * 1000L : 0L;
    }

    private static string ExtractYtmBrowseId(global::System.Text.Json.JsonElement renderer)
    {
        if (renderer.TryGetProperty("navigationEndpoint", out var nav) &&
            nav.TryGetProperty("browseEndpoint", out var be))
        {
            var bid = be.GetStringProp("browseId");
            if (!string.IsNullOrWhiteSpace(bid)) return bid;
        }
        if (renderer.TryGetProperty("overlay", out var ov) &&
            ov.TryGetProperty("musicItemThumbnailOverlayRenderer", out var mior) &&
            mior.TryGetProperty("content", out var cont) &&
            cont.TryGetProperty("musicPlayButtonRenderer", out var mpbr) &&
            mpbr.TryGetProperty("playNavigationEndpoint", out var pne) &&
            pne.TryGetProperty("watchPlaylistEndpoint", out var wpe))
        {
            var pid = wpe.GetStringProp("playlistId");
            if (!string.IsNullOrWhiteSpace(pid))
            {
                return pid.StartsWith("VL", StringComparison.OrdinalIgnoreCase) ? pid : $"VL{pid}";
            }
        }
        return string.Empty;
    }

    private static string ExtractYtmVideoId(global::System.Text.Json.JsonElement renderer)
    {
        if (renderer.TryGetProperty("playlistItemData", out var pid))
        {
            var v = pid.GetStringProp("videoId");
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        if (renderer.TryGetProperty("overlay", out var ov) &&
            ov.TryGetProperty("musicItemThumbnailOverlayRenderer", out var mior) &&
            mior.TryGetProperty("content", out var cont) &&
            cont.TryGetProperty("musicPlayButtonRenderer", out var mpbr) &&
            mpbr.TryGetProperty("playNavigationEndpoint", out var pne) &&
            pne.TryGetProperty("watchEndpoint", out var we))
        {
            var v = we.GetStringProp("videoId");
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        if (renderer.TryGetProperty("flexColumns", out var fc) &&
            fc.ValueKind == global::System.Text.Json.JsonValueKind.Array &&
            fc.GetArrayLength() > 0 &&
            fc[0].TryGetProperty("musicResponsiveListItemFlexColumnRenderer", out var col0) &&
            col0.TryGetProperty("text", out var txt0) &&
            txt0.TryGetProperty("runs", out var runs0) &&
            runs0.ValueKind == global::System.Text.Json.JsonValueKind.Array)
        {
            foreach (var r in runs0.EnumerateArray())
            {
                if (r.TryGetProperty("navigationEndpoint", out var nav) &&
                    nav.TryGetProperty("watchEndpoint", out var rwe))
                {
                    var v = rwe.GetStringProp("videoId");
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }
        }
        return string.Empty;
    }

    private static string GetYtmFlexColumnText(global::System.Text.Json.JsonElement renderer, int index)
    {
        if (!renderer.TryGetProperty("flexColumns", out var fc) ||
            fc.ValueKind != global::System.Text.Json.JsonValueKind.Array ||
            index < 0 || index >= fc.GetArrayLength())
        {
            return string.Empty;
        }
        if (fc[index].TryGetProperty("musicResponsiveListItemFlexColumnRenderer", out var col) &&
            col.TryGetProperty("text", out var txt))
        {
            return GetYtmRunsText(txt);
        }
        return string.Empty;
    }

    private static PlaylistItem? ParseYtmPlaylistRenderer(global::System.Text.Json.JsonElement renderer)
    {
        var browseId = ExtractYtmBrowseId(renderer);
        if (string.IsNullOrWhiteSpace(browseId)) return null;
        if (!browseId.StartsWith("VL", StringComparison.OrdinalIgnoreCase))
        {
            if (browseId.StartsWith("PL", StringComparison.OrdinalIgnoreCase) ||
                browseId.StartsWith("OLAK", StringComparison.OrdinalIgnoreCase) ||
                browseId.StartsWith("RDCLAK", StringComparison.OrdinalIgnoreCase))
            {
                browseId = $"VL{browseId}";
            }
            else
            {
                return null;
            }
        }

        var title = GetYtmFlexColumnText(renderer, 0);
        if (string.IsNullOrWhiteSpace(title)) return null;

        var subtitle = GetYtmFlexColumnText(renderer, 1);
        var creator = "YouTube Music";
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            var segs = subtitle.Split(['•', '·'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var seg in segs)
            {
                if (seg.Equals("Playlist", StringComparison.OrdinalIgnoreCase) ||
                    seg.Equals("播放列表", StringComparison.OrdinalIgnoreCase) ||
                    seg.Contains("views", StringComparison.OrdinalIgnoreCase) ||
                    seg.Contains("tracks", StringComparison.OrdinalIgnoreCase) ||
                    seg.Contains("首歌曲", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                creator = seg;
                break;
            }
        }

        return new PlaylistItem
        {
            Source = "ytmusic",
            ThirdPartyId = browseId,
            Name = GlobalHtmlDecode(title),
            Description = string.IsNullOrWhiteSpace(subtitle) ? "YouTube Music 原生歌单" : GlobalHtmlDecode(subtitle),
            CoverImgUrl = GetYtmBestThumbnail(renderer),
            CreatorName = GlobalHtmlDecode(creator),
            PlayCount = 420000L
        };
    }

    private static SongItem? ParseYtmTrackRenderer(
        global::System.Text.Json.JsonElement renderer,
        string fallbackAlbum = "",
        string fallbackCover = "",
        int displayIndex = 0)
    {
        var videoId = ExtractYtmVideoId(renderer);
        var title = GetYtmFlexColumnText(renderer, 0);
        if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(title)) return null;

        var artists = new List<ArtistItem>();
        var fallbackArtistTokens = new List<string>();
        string albumName = fallbackAlbum;
        long durMs = 0L;

        if (renderer.TryGetProperty("flexColumns", out var fc) && fc.ValueKind == global::System.Text.Json.JsonValueKind.Array)
        {
            for (int i = 1; i < fc.GetArrayLength(); i++)
            {
                if (!fc[i].TryGetProperty("musicResponsiveListItemFlexColumnRenderer", out var col) ||
                    !col.TryGetProperty("text", out var txt) ||
                    !txt.TryGetProperty("runs", out var runs) ||
                    runs.ValueKind != global::System.Text.Json.JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var r in runs.EnumerateArray())
                {
                    var rt = r.GetStringProp("text").Trim();
                    if (string.IsNullOrWhiteSpace(rt) || rt is "•" or "·" or "&" or "," or "、") continue;

                    var parsedMs = TryParseDurationTextMs(rt);
                    if (parsedMs > 0)
                    {
                        if (durMs == 0) durMs = parsedMs;
                        continue;
                    }

                    string pageType = string.Empty;
                    if (r.TryGetProperty("navigationEndpoint", out var nav) &&
                        nav.TryGetProperty("browseEndpoint", out var be) &&
                        be.TryGetProperty("browseEndpointContextSupportedConfigs", out var cfg) &&
                        cfg.TryGetProperty("browseEndpointContextMusicConfig", out var mcfg))
                    {
                        pageType = mcfg.GetStringProp("pageType");
                    }

                    if (pageType is "MUSIC_PAGE_TYPE_ARTIST" or "MUSIC_PAGE_TYPE_USER_CHANNEL")
                    {
                        if (artists.All(a => !string.Equals(a.Name, rt, StringComparison.OrdinalIgnoreCase)))
                            artists.Add(new ArtistItem { Name = GlobalHtmlDecode(rt) });
                    }
                    else if (pageType == "MUSIC_PAGE_TYPE_ALBUM")
                    {
                        if (string.IsNullOrWhiteSpace(albumName) || albumName == fallbackAlbum)
                            albumName = rt;
                    }
                    else if (i == 1 &&
                             !rt.Equals("Song", StringComparison.OrdinalIgnoreCase) &&
                             !rt.Equals("Video", StringComparison.OrdinalIgnoreCase) &&
                             !rt.Equals("歌曲", StringComparison.OrdinalIgnoreCase) &&
                             !rt.Contains("plays", StringComparison.OrdinalIgnoreCase) &&
                             !rt.Contains("views", StringComparison.OrdinalIgnoreCase) &&
                             !rt.Contains("次播放", StringComparison.OrdinalIgnoreCase))
                    {
                        fallbackArtistTokens.Add(rt);
                    }
                }
            }
        }

        if (durMs == 0 && renderer.TryGetProperty("fixedColumns", out var fixc) && fixc.ValueKind == global::System.Text.Json.JsonValueKind.Array)
        {
            foreach (var colWrap in fixc.EnumerateArray())
            {
                if (colWrap.TryGetProperty("musicResponsiveListItemFixedColumnRenderer", out var fcol) &&
                    fcol.TryGetProperty("text", out var ftxt))
                {
                    var parsedMs = TryParseDurationTextMs(GetYtmRunsText(ftxt));
                    if (parsedMs > 0) { durMs = parsedMs; break; }
                }
            }
        }

        if (artists.Count == 0 && fallbackArtistTokens.Count > 0)
        {
            artists.Add(new ArtistItem { Name = GlobalHtmlDecode(fallbackArtistTokens[0]) });
        }
        if (artists.Count == 0)
        {
            artists.Add(new ArtistItem { Name = "YouTube Music Artist" });
        }

        var cover = FirstNonEmpty(GetYtmBestThumbnail(renderer), fallbackCover);

        return new SongItem
        {
            Id = $"ytmusic_{videoId}",
            Name = GlobalHtmlDecode(title),
            Artists = artists,
            Album = new AlbumItem { Name = GlobalHtmlDecode(albumName), PicUrl = cover },
            DurationMs = durMs,
            Source = "ytmusic",
            PicUrl = cover,
            NativeTrackId = videoId,
            NativeQualityHint = "M4A 128k (YouTube)",
            DisplayIndex = displayIndex
        };
    }

    private async Task<List<PlaylistItem>> GetYtMusicNativePlaylistsAsync(
        string categoryKey, int page, int pageSize, CancellationToken ct)
    {
        if (page > 1) return [];
        var q = string.IsNullOrWhiteSpace(categoryKey) || string.Equals(categoryKey, "all", StringComparison.OrdinalIgnoreCase)
            ? "Top Hits"
            : categoryKey.Trim();
        var json = await PostYtMusicInnertubeAsync("search", new
        {
            context = BuildYtmWebRemixContext(),
            query = q,
            @params = "EgWKAQIoAWoKEAkQBRAKEAMQBA=="
        }, ct);
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var doc = global::System.Text.Json.JsonDocument.Parse(json);
            var renderers = new List<global::System.Text.Json.JsonElement>();
            CollectYtmRenderers(doc.RootElement, renderers);

            var list = new List<PlaylistItem>();
            foreach (var r in renderers)
            {
                var pl = ParseYtmPlaylistRenderer(r);
                if (pl != null && list.All(x => !string.Equals(x.ThirdPartyId, pl.ThirdPartyId, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(pl);
                    if (list.Count >= pageSize) break;
                }
            }
            return list;
        }
        catch
        {
            return [];
        }
    }

    private async Task<PlaylistItem?> GetYtMusicNativePlaylistDetailAsync(PlaylistItem summary, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(summary.ThirdPartyId)) return null;
        var browseId = summary.ThirdPartyId.Trim();
        if (!browseId.StartsWith("VL", StringComparison.OrdinalIgnoreCase))
            browseId = $"VL{browseId}";

        var json = await PostYtMusicInnertubeAsync("browse", new
        {
            context = BuildYtmWebRemixContext(),
            browseId
        }, ct);
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = global::System.Text.Json.JsonDocument.Parse(json);
            var renderers = new List<global::System.Text.Json.JsonElement>();
            CollectYtmRenderers(doc.RootElement, renderers);

            var tracks = new List<SongItem>();
            foreach (var r in renderers)
            {
                var song = ParseYtmTrackRenderer(r, summary.Name, summary.CoverImgUrl, tracks.Count + 1);
                if (song != null) tracks.Add(song);
            }

            return new PlaylistItem
            {
                Source = "ytmusic",
                ThirdPartyId = summary.ThirdPartyId,
                Name = summary.Name,
                Description = summary.Description,
                CoverImgUrl = summary.CoverImgUrl,
                CreatorName = summary.CreatorName,
                TrackCount = tracks.Count,
                PlayCount = summary.PlayCount,
                Tracks = tracks
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<SongItem>> GetYtMusicBoardAsync(string boardKey, CancellationToken ct)
    {
        var q = boardKey.ToLowerInvariant() switch
        {
            "audiophile" => "Audiophile Acoustic Jazz",
            "global" => "Global Top Hits 2025",
            "us" => "US Top 50 Pop Hits",
            "jp" => "J-Pop Top Hits",
            "ost" => "Epic Movie Soundtrack",
            _ => "Top Hits"
        };
        return await SearchYtMusicNativeAsync(q, 1, 40, ct);
    }

    private async Task<List<SongItem>> SearchYtMusicNativeAsync(string keyword, int page, int pageSize, CancellationToken ct)
    {
        if (page > 1) return [];
        var json = await PostYtMusicInnertubeAsync("search", new
        {
            context = BuildYtmWebRemixContext(),
            query = keyword,
            @params = "EgWKAQIIAWoKEAkQBRAKEAMQBA=="
        }, ct);
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var doc = global::System.Text.Json.JsonDocument.Parse(json);
            var renderers = new List<global::System.Text.Json.JsonElement>();
            CollectYtmRenderers(doc.RootElement, renderers);

            var list = new List<SongItem>();
            foreach (var r in renderers)
            {
                var song = ParseYtmTrackRenderer(r, string.Empty, string.Empty, list.Count + 1);
                if (song != null)
                {
                    list.Add(song);
                    if (list.Count >= pageSize) break;
                }
            }
            return list;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 国际平台严格按各平台官方原生 API 解析直链，绝不跨平台或跳转国内源。
    /// </summary>
    public async Task<(string? Url, int BitrateKbps)> GetInternationalNativePlayUrlAsync(SongItem song, CancellationToken ct = default)
    {
        if (song == null) return (null, 0);
        var src = song.Source?.Trim().ToLowerInvariant() ?? string.Empty;

        switch (src)
        {
            case "spotify":
            {
                if (!string.IsNullOrWhiteSpace(song.NativePlayUrl))
                    return (song.NativePlayUrl, 128);

                var prefix = "spotify_";
                var tid = FirstNonEmpty(
                    song.NativeTrackId,
                    song.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? song.Id[prefix.Length..] : string.Empty);
                if (string.IsNullOrWhiteSpace(tid)) return (null, 0);

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"https://open.spotify.com/embed/track/{Uri.EscapeDataString(tid)}");
                    req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                    using var resp = await _httpClient.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode) return (null, 0);

                    var html = await resp.Content.ReadAsStringAsync(ct);
                    var m = SpotifyNextDataRegex.Match(html);
                    if (!m.Success) return (null, 0);

                    using var doc = global::System.Text.Json.JsonDocument.Parse(m.Groups[1].Value);
                    if (doc.RootElement.TryGetProperty("props", out var props) &&
                        props.TryGetProperty("pageProps", out var pageProps) &&
                        pageProps.TryGetProperty("state", out var state) &&
                        state.TryGetProperty("data", out var dataEl) &&
                        dataEl.TryGetProperty("entity", out var entity) &&
                        entity.TryGetProperty("audioPreview", out var ap))
                    {
                        var previewUrl = ap.GetStringProp("url");
                        if (!string.IsNullOrWhiteSpace(previewUrl))
                        {
                            song.NativePlayUrl = previewUrl;
                            return (previewUrl, 128);
                        }
                    }
                }
                catch
                {
                }
                return (null, 0);
            }

            case "apple":
            {
                if (!string.IsNullOrWhiteSpace(song.NativePlayUrl))
                    return (song.NativePlayUrl, 128);

                var prefix = "apple_";
                var tid = FirstNonEmpty(
                    song.NativeTrackId,
                    song.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? song.Id[prefix.Length..] : string.Empty);
                if (string.IsNullOrWhiteSpace(tid) || !tid.All(char.IsDigit))
                    return (null, 0);

                foreach (var cc in new[] { "US", "JP", "GB", "KR", "HK", "TW" })
                {
                    try
                    {
                        var json = await _httpClient.GetStringAsync($"https://itunes.apple.com/lookup?id={tid}&country={cc}", ct);
                        using var doc = global::System.Text.Json.JsonDocument.Parse(json);
                        if (!doc.RootElement.TryGetProperty("results", out var results) ||
                            results.ValueKind != global::System.Text.Json.JsonValueKind.Array)
                            continue;

                        foreach (var item in results.EnumerateArray())
                        {
                            var previewUrl = item.GetStringProp("previewUrl");
                            if (!string.IsNullOrWhiteSpace(previewUrl))
                            {
                                song.NativePlayUrl = previewUrl;
                                return (previewUrl, 128);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
                return (null, 0);
            }

            case "deezer":
            {
                var prefix = "deezer_";
                var tid = FirstNonEmpty(
                    song.NativeTrackId,
                    song.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? song.Id[prefix.Length..] : string.Empty);
                if (!string.IsNullOrWhiteSpace(tid))
                {
                    try
                    {
                        var json = await _httpClient.GetStringAsync($"https://api.deezer.com/track/{Uri.EscapeDataString(tid)}", ct);
                        using var doc = global::System.Text.Json.JsonDocument.Parse(json);
                        var fresh = doc.RootElement.GetStringProp("preview");
                        if (!string.IsNullOrWhiteSpace(fresh))
                        {
                            song.NativePlayUrl = fresh;
                            return (fresh, 128);
                        }
                    }
                    catch
                    {
                    }
                }
                return !string.IsNullOrWhiteSpace(song.NativePlayUrl) ? (song.NativePlayUrl, 128) : (null, 0);
            }

            case "tidal":
            {
                var prefix = "tidal_";
                var tid = FirstNonEmpty(
                    song.NativeTrackId,
                    song.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? song.Id[prefix.Length..] : string.Empty);
                if (!string.IsNullOrWhiteSpace(tid))
                {
                    var json = await SendTidalGetAsync($"https://api.tidal.com/v1/tracks/{Uri.EscapeDataString(tid)}/previewurl?countryCode=US", ct);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        try
                        {
                            using var doc = global::System.Text.Json.JsonDocument.Parse(json);
                            var url = doc.RootElement.GetStringProp("url");
                            if (!string.IsNullOrWhiteSpace(url))
                            {
                                song.NativePlayUrl = url;
                                return (url, 96);
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                return !string.IsNullOrWhiteSpace(song.NativePlayUrl) ? (song.NativePlayUrl, 96) : (null, 0);
            }

            case "ytmusic":
            {
                // YouTube 裸 googlevideo.com 直链在无 PoToken 与普通 HTTP GET 下必报 HTTP 403 / 0x80070005 (E_ACCESSDENIED)，
                // 统一交由 UnblockMusicService.ResolveInternationalAudioAsync 走纯国际无损检索与代理缓存播放
                return (null, 0);
            }

            default:
                return (null, 0);
        }
    }

    private static bool GetBoolProp(global::System.Text.Json.JsonElement el, string prop)
    {
        if (el.ValueKind != global::System.Text.Json.JsonValueKind.Object || !el.TryGetProperty(prop, out var val))
            return false;
        return val.ValueKind switch
        {
            global::System.Text.Json.JsonValueKind.True => true,
            global::System.Text.Json.JsonValueKind.Number => val.TryGetInt64(out var n) && n != 0,
            global::System.Text.Json.JsonValueKind.String => bool.TryParse(val.GetString(), out var b) && b,
            _ => false
        };
    }

    private static string? TryDecodeBase64Url(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        try
        {
            var bytes = Convert.FromBase64String(trimmed);
            var decoded = Encoding.UTF8.GetString(bytes).Trim();
            return decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? decoded : null;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    /// <summary>
    /// 确保歌曲具备指定落雪子源（wy/tx/kg/kw/mg）所需的原生 ID/Hash；若缺失则按歌名+歌手自动检索补齐并缓存到 SongItem
    /// </summary>
    public async Task<bool> EnsurePlatformMetaAsync(SongItem song, string lxSourceKey, CancellationToken ct = default)
    {
        if (song == null) return false;

        switch (lxSourceKey)
        {
            case "wy":
                return song.NumericId > 0;
            case "tx":
                if (!string.IsNullOrWhiteSpace(song.QqSongMid)) return true;
                break;
            case "kg":
                if (!string.IsNullOrWhiteSpace(song.KugouHash) ||
                    !string.IsNullOrWhiteSpace(song.Kugou320Hash) ||
                    !string.IsNullOrWhiteSpace(song.KugouSqHash) ||
                    !string.IsNullOrWhiteSpace(song.KugouResHash))
                {
                    return true;
                }
                break;
            case "kw":
                if (!string.IsNullOrWhiteSpace(song.KuwoRid)) return true;
                break;
            case "mg":
                if (!string.IsNullOrWhiteSpace(song.MiguCopyrightId)) return true;
                break;
            default:
                return false;
        }

        var keyword = $"{song.Name} {song.ArtistText}".Trim();
        if (string.IsNullOrWhiteSpace(keyword)) return false;

        var candidates = await SearchSongsAsync(lxSourceKey, keyword, page: 1, pageSize: 6, ct);
        var matched = PickBestMatch(candidates, song);
        if (matched == null) return false;

        switch (lxSourceKey)
        {
            case "tx":
                song.QqSongMid = matched.QqSongMid;
                if (matched.QqSongId > 0) song.QqSongId = matched.QqSongId;
                return !string.IsNullOrWhiteSpace(song.QqSongMid);
            case "kg":
                song.KugouHash = matched.KugouHash;
                song.Kugou320Hash = matched.Kugou320Hash;
                song.KugouSqHash = matched.KugouSqHash;
                song.KugouResHash = matched.KugouResHash;
                song.KugouAlbumId = matched.KugouAlbumId;
                return !string.IsNullOrWhiteSpace(song.KugouHash) || !string.IsNullOrWhiteSpace(song.KugouSqHash);
            case "kw":
                song.KuwoRid = matched.KuwoRid;
                return !string.IsNullOrWhiteSpace(song.KuwoRid);
            case "mg":
                song.MiguCopyrightId = matched.MiguCopyrightId;
                song.MiguContentId = matched.MiguContentId;
                song.MiguLrcUrl ??= matched.MiguLrcUrl;
                return !string.IsNullOrWhiteSpace(song.MiguCopyrightId);
            default:
                return false;
        }
    }

    // ==================== 1. QQ 音乐 (tx) ====================

    private async Task<List<SongItem>> SearchQqAsync(string keyword, int page, int pageSize, CancellationToken ct)
    {
        var list = new List<SongItem>();
        try
        {
            var payload = new
            {
                comm = new { ct = 11, cv = "1003006", v = "1003006" },
                req_0 = new
                {
                    module = "music.search.SearchCgiService",
                    method = "DoSearchForQQMusicMobile",
                    param = new
                    {
                        num_per_page = pageSize,
                        page_num = Math.Max(1, page),
                        query = keyword,
                        search_type = 0
                    }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Referrer = new Uri("https://y.qq.com/");

            using var resp = await _httpClient.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("req_0", out var req0) &&
                req0.TryGetProperty("data", out var data) &&
                data.TryGetProperty("body", out var body) &&
                body.TryGetProperty("item_song", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var parsed = ParseQqSongElement(item);
                    if (parsed != null) list.Add(parsed);
                }
            }
        }
        catch
        {
            // 网络或解析异常返回已解析列表
        }
        return list;
    }

    private async Task<List<SongItem>> GetQqBoardAsync(string boardKey, CancellationToken ct)
    {
        int topId = boardKey switch
        {
            "soar" => 62,
            "new" => 27,
            "mainland" => 5,
            "hktw" => 59,
            _ => 26
        };

        var list = new List<SongItem>();
        try
        {
            var payload = new
            {
                req_0 = new
                {
                    module = "musicToplist.ToplistInfoServer",
                    method = "GetDetail",
                    param = new { topId, offset = 0, num = 50 }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Referrer = new Uri("https://y.qq.com/");

            using var resp = await _httpClient.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("req_0", out var req0) &&
                req0.TryGetProperty("data", out var data) &&
                data.TryGetProperty("songInfoList", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var parsed = ParseQqSongElement(item);
                    if (parsed != null) list.Add(parsed);
                }
            }
        }
        catch
        {
        }
        return list;
    }

    private static SongItem? ParseQqSongElement(JsonElement item)
    {
        var mid = item.GetStringProp("mid");
        if (string.IsNullOrWhiteSpace(mid)) mid = item.GetStringProp("songmid");
        var name = item.GetStringProp("name");
        if (string.IsNullOrWhiteSpace(name)) name = item.GetStringProp("title");
        if (string.IsNullOrWhiteSpace(mid) || string.IsNullOrWhiteSpace(name)) return null;

        long songId = item.GetLongProp("id");
        int intervalSec = item.GetIntProp("interval");

        var artists = new List<ArtistItem>();
        if (item.TryGetProperty("singer", out var singers) && singers.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in singers.EnumerateArray())
            {
                var sName = s.GetStringProp("name");
                if (!string.IsNullOrWhiteSpace(sName))
                {
                    artists.Add(new ArtistItem { Id = s.GetLongProp("id"), Name = sName });
                }
            }
        }

        string albumName = string.Empty;
        string albumMid = string.Empty;
        long albumId = 0;
        if (item.TryGetProperty("album", out var alb) && alb.ValueKind == JsonValueKind.Object)
        {
            albumName = alb.GetStringProp("name");
            albumMid = alb.GetStringProp("mid");
            albumId = alb.GetLongProp("id");
        }

        string picUrl = !string.IsNullOrWhiteSpace(albumMid)
            ? $"https://y.gtimg.cn/music/photo_new/T002R300x300M000{albumMid}.jpg"
            : string.Empty;

        string qualityHint = "320k";
        if (item.TryGetProperty("file", out var fileObj) && fileObj.ValueKind == JsonValueKind.Object)
        {
            if (fileObj.GetLongProp("size_hires") > 0 || fileObj.GetLongProp("size_new") > 0)
                qualityHint = "Hi-Res";
            else if (fileObj.GetLongProp("size_flac") > 0)
                qualityHint = "FLAC";
            else if (fileObj.GetLongProp("size_320mp3") > 0)
                qualityHint = "320k";
            else
                qualityHint = "128k";
        }

        return new SongItem
        {
            Id = $"qq_{mid}",
            Name = name,
            Artists = artists,
            Album = new AlbumItem { Id = albumId, Name = albumName, PicUrl = picUrl },
            PicUrl = picUrl,
            DurationMs = intervalSec * 1000L,
            Source = "qq",
            QqSongMid = mid,
            QqSongId = songId,
            NativeQualityHint = qualityHint
        };
    }

    // ==================== 2. 酷狗音乐 (kg) ====================

    private async Task<List<SongItem>> SearchKugouAsync(string keyword, int page, int pageSize, CancellationToken ct)
    {
        var list = new List<SongItem>();
        try
        {
            var url = $"http://mobilecdn.kugou.com/api/v3/search/song?format=json&keyword={Uri.EscapeDataString(keyword)}&page={Math.Max(1, page)}&pagesize={pageSize}&showtype=1";
            var json = await _httpClient.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("info", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var parsed = ParseKugouSongElement(item);
                    if (parsed != null) list.Add(parsed);
                }
            }
        }
        catch
        {
        }
        return list;
    }

    private async Task<List<SongItem>> GetKugouBoardAsync(string boardKey, CancellationToken ct)
    {
        int rankId = boardKey switch
        {
            "soar" => 6666,
            "new" => 31308,
            "net" => 23784,
            _ => 8888
        };

        var list = new List<SongItem>();
        try
        {
            var url = $"http://mobilecdnbj.kugou.com/api/v3/rank/song?version=9108&plat=0&pagesize=50&page=1&rankid={rankId}";
            var json = await _httpClient.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("info", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var parsed = ParseKugouSongElement(item);
                    if (parsed != null) list.Add(parsed);
                }
            }
        }
        catch
        {
        }
        return list;
    }

    private static SongItem? ParseKugouSongElement(JsonElement item)
    {
        var stdHash = item.GetStringProp("hash");
        var hqHash = item.GetStringProp("320hash");
        var sqHash = item.GetStringProp("sqhash");
        if (string.IsNullOrWhiteSpace(sqHash)) sqHash = item.GetStringProp("SQFileHash");
        var resHash = item.GetStringProp("ResFileHash");
        if (string.IsNullOrWhiteSpace(resHash)) resHash = item.GetStringProp("reshash");
        if (string.IsNullOrWhiteSpace(resHash)) resHash = item.GetStringProp("hash_super");

        var primaryHash = !string.IsNullOrWhiteSpace(sqHash) ? sqHash : (!string.IsNullOrWhiteSpace(hqHash) ? hqHash : stdHash);
        if (string.IsNullOrWhiteSpace(primaryHash)) return null;

        var name = item.GetStringProp("songname");
        var singerRaw = item.GetStringProp("singername");
        if (string.IsNullOrWhiteSpace(singerRaw) &&
            item.TryGetProperty("authors", out var authors) &&
            authors.ValueKind == JsonValueKind.Array)
        {
            singerRaw = string.Join("、", authors.EnumerateArray()
                .Select(a => a.GetStringProp("author_name"))
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            var filename = item.GetStringProp("filename");
            int sep = filename.IndexOf(" - ", StringComparison.Ordinal);
            if (sep > 0)
            {
                if (string.IsNullOrWhiteSpace(singerRaw)) singerRaw = filename[..sep].Trim();
                name = filename[(sep + 3)..].Trim();
            }
            else
            {
                name = filename;
            }
        }
        if (string.IsNullOrWhiteSpace(name)) return null;

        var albumName = item.GetStringProp("album_name");
        if (string.IsNullOrWhiteSpace(albumName)) albumName = item.GetStringProp("remark");
        var albumId = item.GetStringProp("album_id");
        int durationSec = item.GetIntProp("duration");

        var cover = item.GetStringProp("album_sizable_cover");
        if (string.IsNullOrWhiteSpace(cover) &&
            item.TryGetProperty("trans_param", out var tp) &&
            tp.ValueKind == JsonValueKind.Object)
        {
            cover = tp.GetStringProp("union_cover");
        }
        if (!string.IsNullOrWhiteSpace(cover))
        {
            cover = cover.Replace("\\/", "/").Replace("{size}", "400");
        }

        string qualityHint = !string.IsNullOrWhiteSpace(resHash) ? "Hi-Res"
            : !string.IsNullOrWhiteSpace(sqHash) ? "FLAC"
            : !string.IsNullOrWhiteSpace(hqHash) ? "320k"
            : "128k";

        var artists = string.IsNullOrWhiteSpace(singerRaw)
            ? new List<ArtistItem>()
            : singerRaw.Split(['、', '/', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => new ArtistItem { Name = s.Trim() })
                .ToList();

        return new SongItem
        {
            Id = $"kg_{primaryHash.ToLowerInvariant()}",
            Name = name,
            Artists = artists,
            Album = new AlbumItem { Name = albumName, PicUrl = cover },
            PicUrl = cover,
            DurationMs = durationSec * 1000L,
            Source = "kugou",
            KugouHash = stdHash,
            Kugou320Hash = hqHash,
            KugouSqHash = sqHash,
            KugouResHash = resHash,
            KugouAlbumId = albumId,
            NativeQualityHint = qualityHint
        };
    }

    // ==================== 3. 酷我音乐 (kw) ====================

    private async Task<List<SongItem>> SearchKuwoAsync(string keyword, int page, int pageSize, CancellationToken ct)
    {
        var list = new List<SongItem>();
        try
        {
            int pn = Math.Max(0, page - 1);
            var url = $"http://search.kuwo.cn/r.s?client=kt&all={Uri.EscapeDataString(keyword)}&pn={pn}&rn={pageSize}&uid=794762570&ver=kwplayer_ar_9.2.2.1&vipver=1&show_copyright_off=1&newver=1&ft=music&cluster=0&strategy=2012&encoding=utf8&rformat=json&vermerge=1&mobi=1&issubtitle=1";
            var raw = await _httpClient.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(raw.Replace('\'', '"'));
            if (doc.RootElement.TryGetProperty("abslist", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var rid = item.GetStringProp("DC_TARGETID");
                    if (string.IsNullOrWhiteSpace(rid))
                        rid = item.GetStringProp("MUSICRID").Replace("MUSIC_", "", StringComparison.OrdinalIgnoreCase);
                    var name = item.GetStringProp("SONGNAME");
                    if (string.IsNullOrWhiteSpace(name)) name = item.GetStringProp("NAME");
                    if (string.IsNullOrWhiteSpace(rid) || string.IsNullOrWhiteSpace(name)) continue;

                    var artistRaw = GlobalHtmlDecode(item.GetStringProp("ARTIST"));
                    var albumName = GlobalHtmlDecode(item.GetStringProp("ALBUM"));
                    int durationSec = item.GetIntProp("DURATION");

                    var shortPic = item.GetStringProp("web_albumpic_short");
                    string picUrl = !string.IsNullOrWhiteSpace(shortPic)
                        ? $"https://img4.kuwo.cn/star/albumcover/{shortPic.Replace("120/", "300/")}"
                        : item.GetStringProp("hts_MVPIC");

                    var minfo = item.GetStringProp("MINFO") + ";" + item.GetStringProp("N_MINFO");
                    string qualityHint = minfo.Contains("bitrate:4000", StringComparison.OrdinalIgnoreCase) ? "Hi-Res"
                        : minfo.Contains("flac", StringComparison.OrdinalIgnoreCase) ? "FLAC"
                        : minfo.Contains("320", StringComparison.OrdinalIgnoreCase) ? "320k"
                        : "128k";

                    list.Add(new SongItem
                    {
                        Id = $"kw_{rid}",
                        Name = GlobalHtmlDecode(name),
                        Artists = SplitArtists(artistRaw),
                        Album = new AlbumItem { Name = albumName, PicUrl = picUrl },
                        PicUrl = picUrl,
                        DurationMs = durationSec * 1000L,
                        Source = "kuwo",
                        KuwoRid = rid,
                        NativeQualityHint = qualityHint
                    });
                }
            }
        }
        catch
        {
        }
        return list;
    }

    private async Task<List<SongItem>> GetKuwoBoardAsync(string boardKey, CancellationToken ct)
    {
        int bangId = boardKey switch
        {
            "soar" => 93,
            "new" => 17,
            "douyin" => 158,
            _ => 16
        };

        var list = new List<SongItem>();
        try
        {
            var url = $"http://kbangserver.kuwo.cn/ksong.s?from=pc&fmt=json&pn=0&rn=50&type=bang&data=content&id={bangId}&show_copyright_off=0&pcmp4=1&isbang=1";
            var json = await _httpClient.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("musiclist", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var rid = item.GetStringProp("id");
                    var name = GlobalHtmlDecode(item.GetStringProp("name"));
                    if (string.IsNullOrWhiteSpace(rid) || string.IsNullOrWhiteSpace(name)) continue;

                    var artistRaw = GlobalHtmlDecode(item.GetStringProp("artist"));
                    var albumName = GlobalHtmlDecode(item.GetStringProp("album"));
                    int durationSec = item.GetIntProp("song_duration");
                    if (durationSec <= 0) durationSec = item.GetIntProp("duration");

                    var formats = item.GetStringProp("formats");
                    string qualityHint = formats.Contains("ZP", StringComparison.OrdinalIgnoreCase) ? "Hi-Res"
                        : formats.Contains("FLAC", StringComparison.OrdinalIgnoreCase) ? "FLAC"
                        : formats.Contains("MP3H", StringComparison.OrdinalIgnoreCase) ? "320k"
                        : "128k";

                    list.Add(new SongItem
                    {
                        Id = $"kw_{rid}",
                        Name = name,
                        Artists = SplitArtists(artistRaw),
                        Album = new AlbumItem { Name = albumName },
                        DurationMs = durationSec * 1000L,
                        Source = "kuwo",
                        KuwoRid = rid,
                        NativeQualityHint = qualityHint
                    });
                }
            }
        }
        catch
        {
        }
        return list;
    }

    // ==================== 4. 咪咕音乐 (mg) ====================

    private async Task<List<SongItem>> SearchMiguAsync(string keyword, int page, int pageSize, CancellationToken ct)
    {
        var list = new List<SongItem>();
        try
        {
            var url = $"https://c.musicapp.migu.cn/v1.0/content/search_all.do?text={Uri.EscapeDataString(keyword)}&pageNo={Math.Max(1, page)}&pageSize={pageSize}&searchSwitch=%7B%22song%22%3A1%7D";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("channel", "0146951");

            using var resp = await _httpClient.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("songResultData", out var data) &&
                data.TryGetProperty("result", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var parsed = ParseMiguSongElement(item);
                    if (parsed != null) list.Add(parsed);
                }
            }
        }
        catch
        {
        }
        return list;
    }

    private async Task<List<SongItem>> GetMiguBoardAsync(string boardKey, CancellationToken ct)
    {
        string columnId = boardKey switch
        {
            "new" => "27553319",
            "original" => "27553408",
            _ => "27186466"
        };

        var list = new List<SongItem>();
        try
        {
            var url = $"https://app.c.nf.migu.cn/MIGUM2.0/v1.0/content/querycontentbyId.do?columnId={columnId}&needAll=0";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("channel", "0146951");

            using var resp = await _httpClient.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("columnInfo", out var col) &&
                col.TryGetProperty("contents", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in arr.EnumerateArray())
                {
                    if (entry.TryGetProperty("objectInfo", out var obj) && obj.ValueKind == JsonValueKind.Object)
                    {
                        var parsed = ParseMiguSongElement(obj);
                        if (parsed != null) list.Add(parsed);
                    }
                }
            }
        }
        catch
        {
        }
        return list;
    }

    private static SongItem? ParseMiguSongElement(JsonElement item)
    {
        var copyrightId = item.GetStringProp("copyrightId");
        var contentId = item.GetStringProp("contentId");
        var name = item.GetStringProp("name");
        if (string.IsNullOrWhiteSpace(name)) name = item.GetStringProp("songName");
        if (string.IsNullOrWhiteSpace(copyrightId) || string.IsNullOrWhiteSpace(name)) return null;

        var artists = new List<ArtistItem>();
        if (item.TryGetProperty("singers", out var singers) && singers.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in singers.EnumerateArray())
            {
                var sn = s.GetStringProp("name");
                if (!string.IsNullOrWhiteSpace(sn)) artists.Add(new ArtistItem { Name = sn });
            }
        }
        else if (item.TryGetProperty("singerList", out var singerList) && singerList.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in singerList.EnumerateArray())
            {
                var sn = s.GetStringProp("name");
                if (!string.IsNullOrWhiteSpace(sn)) artists.Add(new ArtistItem { Name = sn });
            }
        }
        else
        {
            artists = SplitArtists(item.GetStringProp("singer"));
        }

        string albumName = item.GetStringProp("album");
        if (string.IsNullOrWhiteSpace(albumName) &&
            item.TryGetProperty("albums", out var albums) &&
            albums.ValueKind == JsonValueKind.Array &&
            albums.GetArrayLength() > 0)
        {
            albumName = albums[0].GetStringProp("name");
        }

        string picUrl = string.Empty;
        if (item.TryGetProperty("imgItems", out var imgs) && imgs.ValueKind == JsonValueKind.Array && imgs.GetArrayLength() > 0)
        {
            picUrl = NormalizeMiguImageUrl(imgs[0].GetStringProp("img"));
        }
        else if (item.TryGetProperty("albumImgs", out var aimgs) && aimgs.ValueKind == JsonValueKind.Array && aimgs.GetArrayLength() > 0)
        {
            picUrl = NormalizeMiguImageUrl(aimgs[0].GetStringProp("img"));
        }
        else
        {
            var imgRaw = item.GetStringProp("img3");
            if (string.IsNullOrWhiteSpace(imgRaw)) imgRaw = item.GetStringProp("img2");
            if (string.IsNullOrWhiteSpace(imgRaw)) imgRaw = item.GetStringProp("img1");
            if (!string.IsNullOrWhiteSpace(imgRaw))
            {
                picUrl = NormalizeMiguImageUrl(imgRaw);
            }
        }

        var lrcUrl = item.GetStringProp("lyricUrl");
        if (string.IsNullOrWhiteSpace(lrcUrl)) lrcUrl = item.GetStringProp("lrcUrl");

        long durationMs = ParseMiguDurationMs(item.GetStringProp("length"));
        if (durationMs <= 0)
        {
            int durSec = item.GetIntProp("duration");
            if (durSec > 0) durationMs = durSec * 1000L;
        }

        string qualityHint = "320k";
        if (item.TryGetProperty("newRateFormats", out var rates) && rates.ValueKind == JsonValueKind.Array)
        {
            bool hasZq = false, hasSq = false, hasHq = false;
            long pqBytes = 0;
            foreach (var r in rates.EnumerateArray())
            {
                var ft = r.GetStringProp("formatType");
                if (ft == "ZQ") hasZq = true;
                else if (ft == "SQ") hasSq = true;
                else if (ft == "HQ") hasHq = true;
                else if (ft == "PQ") pqBytes = r.GetLongProp("size");
            }
            qualityHint = hasZq ? "Hi-Res" : hasSq ? "FLAC" : hasHq ? "320k" : "128k";
            if (durationMs <= 0 && pqBytes > 16000)
            {
                durationMs = pqBytes / 16L; // 128kbps = 16KB/s
            }
        }
        else if (item.TryGetProperty("audioFormats", out var audioFormats) && audioFormats.ValueKind == JsonValueKind.Array)
        {
            bool hasZq = false, hasSq = false, hasHq = false;
            foreach (var r in audioFormats.EnumerateArray())
            {
                var ft = r.GetStringProp("formatType");
                if (ft is "ZQ" or "Z3D") hasZq = true;
                else if (ft == "SQ") hasSq = true;
                else if (ft == "HQ") hasHq = true;
            }
            qualityHint = hasZq ? "Hi-Res" : hasSq ? "FLAC" : hasHq ? "320k" : "128k";
        }

        return new SongItem
        {
            Id = $"mg_{copyrightId}",
            Name = name,
            Artists = artists,
            Album = new AlbumItem { Name = albumName, PicUrl = picUrl },
            PicUrl = picUrl,
            DurationMs = durationMs,
            Source = "migu",
            MiguCopyrightId = copyrightId,
            MiguContentId = contentId,
            MiguLrcUrl = lrcUrl,
            NativeQualityHint = qualityHint
        };
    }

    // ==================== 5. 四大平台推荐/分类歌单与歌单详情 ====================

    private async Task<List<PlaylistItem>> GetQqRecommendPlaylistsAsync(
        string categoryKey, int page, int pageSize, CancellationToken ct)
    {
        var list = new List<PlaylistItem>();
        try
        {
            var catId = string.IsNullOrWhiteSpace(categoryKey) ? "10000000" : categoryKey;
            int sin = Math.Max(0, (page - 1) * pageSize);
            int ein = sin + pageSize - 1;
            var url = $"https://c.y.qq.com/splcloud/fcgi-bin/fcg_get_diss_by_tag.fcg?picmid=1&rnd=0.5&g_tk=5381&loginUin=0&hostUin=0&format=json&inCharset=utf8&outCharset=utf-8&notice=0&platform=yqq.json&needNewCode=0&categoryId={Uri.EscapeDataString(catId)}&sortId=5&sin={sin}&ein={ein}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://y.qq.com/");

            using var resp = await _httpClient.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("list", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var dissid = item.GetStringProp("dissid");
                    var name = GlobalHtmlDecode(item.GetStringProp("dissname"));
                    if (string.IsNullOrWhiteSpace(dissid) || string.IsNullOrWhiteSpace(name)) continue;

                    var img = item.GetStringProp("imgurl");
                    if (img.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                        img = "https://" + img[7..];

                    string creator = "QQ音乐";
                    if (item.TryGetProperty("creator", out var cr) && cr.ValueKind == JsonValueKind.Object)
                    {
                        var cn = GlobalHtmlDecode(cr.GetStringProp("name"));
                        if (!string.IsNullOrWhiteSpace(cn)) creator = cn;
                    }

                    list.Add(new PlaylistItem
                    {
                        Id = long.TryParse(dissid, out long lid) ? lid : Math.Abs(dissid.GetHashCode()),
                        Source = "qq",
                        QqDissId = dissid,
                        ThirdPartyId = dissid,
                        Name = name,
                        CoverImgUrl = string.IsNullOrWhiteSpace(img) ? ImageUriHelper.DefaultCoverUri : img,
                        PlayCount = item.GetLongProp("listennum"),
                        CreatorName = creator,
                        Description = GlobalHtmlDecode(item.GetStringProp("introduction"))
                    });
                }
            }
        }
        catch
        {
        }
        return list;
    }

    private async Task<List<PlaylistItem>> GetKugouRecommendPlaylistsAsync(
        string categoryKey, int page, int pageSize, CancellationToken ct)
    {
        var list = new List<PlaylistItem>();
        try
        {
            // m.kugou.com 会因默认桌面 UA 302 跳转 HTML，统一改走 mobilecdnbj JSON 接口
            var tagId = string.IsNullOrWhiteSpace(categoryKey) || categoryKey == "all" ? "0" : categoryKey.Trim();
            var url = $"http://mobilecdnbj.kugou.com/api/v3/tag/specialList?plat=0&page={Math.Max(1, page)}&tagid={Uri.EscapeDataString(tagId)}&pagesize={pageSize}";

            var json = await _httpClient.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("info", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var specialId = item.GetStringProp("specialid");
                    var name = GlobalHtmlDecode(item.GetStringProp("specialname"));
                    if (string.IsNullOrWhiteSpace(specialId) || string.IsNullOrWhiteSpace(name)) continue;

                    var img = item.GetStringProp("imgurl").Replace("\\/", "/").Replace("{size}", "400");
                    var creator = FirstNonEmpty(item.GetStringProp("username"), item.GetStringProp("nickname"), "酷狗歌单");
                    list.Add(new PlaylistItem
                    {
                        Id = long.TryParse(specialId, out long lid) ? lid : Math.Abs(specialId.GetHashCode()),
                        Source = "kugou",
                        ThirdPartyId = specialId,
                        Name = name,
                        CoverImgUrl = string.IsNullOrWhiteSpace(img) ? ImageUriHelper.DefaultCoverUri : img,
                        PlayCount = item.GetLongProp("playcount"),
                        TrackCount = item.GetIntProp("songcount"),
                        CreatorName = GlobalHtmlDecode(creator),
                        Description = GlobalHtmlDecode(item.GetStringProp("intro"))
                    });
                }
            }
        }
        catch
        {
        }
        return list;
    }

    private async Task<PlaylistItem?> GetKugouPlaylistDetailAsync(PlaylistItem summary, CancellationToken ct)
    {
        var specialId = !string.IsNullOrWhiteSpace(summary.ThirdPartyId) ? summary.ThirdPartyId : summary.Id.ToString();
        if (string.IsNullOrWhiteSpace(specialId)) return null;

        try
        {
            var detail = new PlaylistItem
            {
                Id = summary.Id,
                Source = "kugou",
                ThirdPartyId = specialId,
                Name = summary.Name,
                CoverImgUrl = summary.CoverImgUrl,
                Description = summary.Description,
                CreatorName = string.IsNullOrWhiteSpace(summary.CreatorName) ? "酷狗音乐" : summary.CreatorName,
                PlayCount = summary.PlayCount,
                TrackCount = summary.TrackCount
            };

            try
            {
                var infoUrl = $"http://mobilecdnbj.kugou.com/api/v3/special/info?plat=0&specialid={Uri.EscapeDataString(specialId)}&version=9108";
                var infoJson = await _httpClient.GetStringAsync(infoUrl, ct);
                using var infoDoc = JsonDocument.Parse(infoJson);
                if (infoDoc.RootElement.TryGetProperty("data", out var meta) &&
                    meta.ValueKind == JsonValueKind.Object)
                {
                    var title = GlobalHtmlDecode(meta.GetStringProp("specialname"));
                    if (!string.IsNullOrWhiteSpace(title)) detail.Name = title;
                    var img = meta.GetStringProp("imgurl").Replace("\\/", "/").Replace("{size}", "400");
                    if (!string.IsNullOrWhiteSpace(img)) detail.CoverImgUrl = img;
                    var intro = GlobalHtmlDecode(meta.GetStringProp("intro"));
                    if (!string.IsNullOrWhiteSpace(intro)) detail.Description = intro;
                    var nick = FirstNonEmpty(meta.GetStringProp("nickname"), meta.GetStringProp("username"));
                    if (!string.IsNullOrWhiteSpace(nick)) detail.CreatorName = GlobalHtmlDecode(nick);
                    long pc = meta.GetLongProp("playcount");
                    if (pc > 0) detail.PlayCount = pc;
                    int sc = meta.GetIntProp("songcount");
                    if (sc > 0) detail.TrackCount = sc;
                }
            }
            catch
            {
            }

            const int pageSize = 100;
            int totalExpected = detail.TrackCount;
            for (int page = 1; page <= 4; page++)
            {
                var songsUrl = $"http://mobilecdnbj.kugou.com/api/v3/special/song?plat=0&specialid={Uri.EscapeDataString(specialId)}&page={page}&pagesize={pageSize}&version=9108";
                var songsJson = await _httpClient.GetStringAsync(songsUrl, ct);
                using var songsDoc = JsonDocument.Parse(songsJson);
                if (!songsDoc.RootElement.TryGetProperty("data", out var dataEl) ||
                    dataEl.ValueKind != JsonValueKind.Object)
                {
                    break;
                }

                int apiTotal = dataEl.GetIntProp("total");
                if (apiTotal > 0) totalExpected = apiTotal;

                int fetched = 0;
                if (dataEl.TryGetProperty("info", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        fetched++;
                        var song = ParseKugouSongElement(item);
                        if (song != null)
                        {
                            if (string.IsNullOrWhiteSpace(song.PicUrl) && ImageUriHelper.HasRealImageUrl(detail.CoverImgUrl))
                            {
                                song.PicUrl = detail.CoverImgUrl;
                                if (song.Album != null) song.Album.PicUrl = detail.CoverImgUrl;
                            }
                            detail.Tracks.Add(song);
                        }
                    }
                }

                if (fetched < pageSize || (totalExpected > 0 && detail.Tracks.Count >= totalExpected))
                    break;
            }

            if (detail.Tracks.Count > 0) detail.TrackCount = detail.Tracks.Count;
            return detail;
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<PlaylistItem>> GetKuwoRecommendPlaylistsAsync(
        string categoryKey, int page, int pageSize, CancellationToken ct)
    {
        var list = new List<PlaylistItem>();
        try
        {
            var key = string.IsNullOrWhiteSpace(categoryKey) ? "hot" : categoryKey;
            var url = (key is "hot" or "new")
                ? $"http://wapi.kuwo.cn/api/pc/classify/playlist/getRcmPlayList?loginUid=0&loginSid=0&appUid=76039576&pn={Math.Max(1, page)}&rn={pageSize}&order={key}"
                : $"http://wapi.kuwo.cn/api/pc/classify/playlist/getTagPlayList?loginUid=0&loginSid=0&appUid=76039576&pn={Math.Max(1, page)}&rn={pageSize}&id={Uri.EscapeDataString(key)}";

            var json = await _httpClient.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("data", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var pid = item.GetStringProp("id");
                    var name = GlobalHtmlDecode(item.GetStringProp("name"));
                    if (string.IsNullOrWhiteSpace(pid) || string.IsNullOrWhiteSpace(name)) continue;

                    var img = item.GetStringProp("img");
                    list.Add(new PlaylistItem
                    {
                        Id = long.TryParse(pid, out long lid) ? lid : Math.Abs(pid.GetHashCode()),
                        Source = "kuwo",
                        ThirdPartyId = pid,
                        Name = name,
                        CoverImgUrl = string.IsNullOrWhiteSpace(img) ? ImageUriHelper.DefaultCoverUri : img,
                        PlayCount = item.GetLongProp("listencnt"),
                        TrackCount = item.GetIntProp("total"),
                        CreatorName = GlobalHtmlDecode(item.GetStringProp("uname", "酷我音乐")),
                        Description = GlobalHtmlDecode(item.GetStringProp("desc"))
                    });
                }
            }
        }
        catch
        {
        }
        return list;
    }

    private async Task<PlaylistItem?> GetKuwoPlaylistDetailAsync(PlaylistItem summary, CancellationToken ct)
    {
        var pid = !string.IsNullOrWhiteSpace(summary.ThirdPartyId) ? summary.ThirdPartyId : summary.Id.ToString();
        if (string.IsNullOrWhiteSpace(pid)) return null;

        try
        {
            var url = $"http://nplserver.kuwo.cn/pl.svc?op=getlistinfo&pid={Uri.EscapeDataString(pid)}&pn=0&rn=100&encode=utf8&keyset=pl2012&identity=kuwo&pcmp4=1&vipver=MUSIC_9.0.5.0_W1&newver=1";
            var json = await _httpClient.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var cover = root.GetStringProp("pic");
            if (!string.IsNullOrWhiteSpace(cover))
                cover = cover.Replace("_150.jpg", "_500.jpg", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(cover))
                cover = summary.CoverImgUrl;

            var rawDesc = GlobalHtmlDecode(root.GetStringProp("info"))
                .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
                .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
                .Trim();

            var detail = new PlaylistItem
            {
                Id = summary.Id,
                Source = "kuwo",
                ThirdPartyId = pid,
                Name = GlobalHtmlDecode(root.GetStringProp("title", summary.Name)),
                CoverImgUrl = cover,
                Description = !string.IsNullOrWhiteSpace(rawDesc) ? rawDesc : summary.Description,
                CreatorName = GlobalHtmlDecode(root.GetStringProp("uname", summary.CreatorName)),
                PlayCount = root.GetLongProp("playnum") > 0 ? root.GetLongProp("playnum") : summary.PlayCount
            };

            if (root.TryGetProperty("musiclist", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var rid = item.GetStringProp("id");
                    var name = GlobalHtmlDecode(item.GetStringProp("name"));
                    if (string.IsNullOrWhiteSpace(rid) || string.IsNullOrWhiteSpace(name)) continue;

                    var artistRaw = GlobalHtmlDecode(item.GetStringProp("artist"));
                    var albumName = GlobalHtmlDecode(item.GetStringProp("album"));
                    int durationSec = item.GetIntProp("duration");
                    var minfo = item.GetStringProp("MINFO") + ";" + item.GetStringProp("N_MINFO") + ";" + item.GetStringProp("formats");

                    string qualityHint = (minfo.Contains("ZP", StringComparison.OrdinalIgnoreCase) || minfo.Contains("4000", StringComparison.OrdinalIgnoreCase))
                        ? "Hi-Res"
                        : minfo.Contains("flac", StringComparison.OrdinalIgnoreCase)
                            ? "FLAC"
                            : (minfo.Contains("MP3H", StringComparison.OrdinalIgnoreCase) || minfo.Contains("320", StringComparison.OrdinalIgnoreCase))
                                ? "320k"
                                : "128k";

                    detail.Tracks.Add(new SongItem
                    {
                        Id = $"kw_{rid}",
                        Name = name,
                        Artists = SplitArtists(artistRaw),
                        Album = new AlbumItem { Name = albumName, PicUrl = cover },
                        PicUrl = cover,
                        DurationMs = durationSec * 1000L,
                        Source = "kuwo",
                        KuwoRid = rid,
                        NativeQualityHint = qualityHint
                    });
                }
                detail.TrackCount = detail.Tracks.Count;
            }

            return detail;
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<PlaylistItem>> GetMiguRecommendPlaylistsAsync(
        string categoryKey, int page, CancellationToken ct)
    {
        var list = new List<PlaylistItem>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            bool isRec = string.IsNullOrWhiteSpace(categoryKey) || categoryKey == "rec";
            if (isRec && page <= 1)
            {
                using var hReq = new HttpRequestMessage(HttpMethod.Get, "https://app.c.nf.migu.cn/MIGUM3.0/v1.0/template/musiclistplaza-header");
                hReq.Headers.TryAddWithoutValidation("channel", "0146951");
                using var hResp = await _httpClient.SendAsync(hReq, ct);
                var hJson = await hResp.Content.ReadAsStringAsync(ct);
                using var hDoc = JsonDocument.Parse(hJson);
                if (hDoc.RootElement.TryGetProperty("data", out var hData) &&
                    hData.TryGetProperty("contentItemList", out var hArr) &&
                    hArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in hArr.EnumerateArray())
                    {
                        var pid = ExtractMiguPlaylistId(item.GetStringProp("actionUrl"));
                        var title = item.GetStringProp("title");
                        if (string.IsNullOrWhiteSpace(pid) || string.IsNullOrWhiteSpace(title) || !seenIds.Add(pid))
                            continue;

                        list.Add(new PlaylistItem
                        {
                            Id = long.TryParse(pid, out long lid) ? lid : Math.Abs(pid.GetHashCode()),
                            Source = "migu",
                            ThirdPartyId = pid,
                            Name = title,
                            CoverImgUrl = NormalizeMiguImageUrl(item.GetStringProp("imageUrl")),
                            PlayCount = ParseChineseCount(item.GetStringProp("subTitle")),
                            CreatorName = item.GetStringProp("creator", "咪咕音乐")
                        });
                    }
                }
            }

            var tagId = isRec ? "1001076096" : categoryKey;
            var tagUrl = $"https://app.c.nf.migu.cn/MIGUM3.0/v1.0/template/musiclistplaza-listbytag/release?pageNumber={Math.Max(1, page)}&templateVersion=2&tagId={Uri.EscapeDataString(tagId)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, tagUrl);
            req.Headers.TryAddWithoutValidation("channel", "0146951");
            using var resp = await _httpClient.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("contentItemList", out var groups) &&
                groups.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in groups.EnumerateArray())
                {
                    if (!group.TryGetProperty("itemList", out var items) || items.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var item in items.EnumerateArray())
                    {
                        string pid = string.Empty;
                        if (item.TryGetProperty("logEvent", out var le) && le.ValueKind == JsonValueKind.Object)
                        {
                            pid = le.GetStringProp("contentId");
                        }
                        if (string.IsNullOrWhiteSpace(pid))
                        {
                            pid = ExtractMiguPlaylistId(item.GetStringProp("actionUrl"));
                        }

                        var title = item.GetStringProp("title");
                        if (string.IsNullOrWhiteSpace(pid) || string.IsNullOrWhiteSpace(title) || !seenIds.Add(pid))
                            continue;

                        long playCount = 0;
                        if (item.TryGetProperty("barList", out var bars) &&
                            bars.ValueKind == JsonValueKind.Array &&
                            bars.GetArrayLength() > 0)
                        {
                            playCount = ParseChineseCount(bars[0].GetStringProp("title"));
                        }

                        list.Add(new PlaylistItem
                        {
                            Id = long.TryParse(pid, out long lid) ? lid : Math.Abs(pid.GetHashCode()),
                            Source = "migu",
                            ThirdPartyId = pid,
                            Name = title,
                            CoverImgUrl = NormalizeMiguImageUrl(item.GetStringProp("imageUrl")),
                            PlayCount = playCount,
                            CreatorName = "咪咕音乐"
                        });
                    }
                }
            }
        }
        catch
        {
        }
        return list;
    }

    private async Task<PlaylistItem?> GetMiguPlaylistDetailAsync(PlaylistItem summary, CancellationToken ct)
    {
        var pid = !string.IsNullOrWhiteSpace(summary.ThirdPartyId) ? summary.ThirdPartyId : summary.Id.ToString();
        if (string.IsNullOrWhiteSpace(pid)) return null;

        try
        {
            var detail = new PlaylistItem
            {
                Id = summary.Id,
                Source = "migu",
                ThirdPartyId = pid,
                Name = summary.Name,
                CoverImgUrl = summary.CoverImgUrl,
                Description = summary.Description,
                CreatorName = string.IsNullOrWhiteSpace(summary.CreatorName) ? "咪咕音乐" : summary.CreatorName,
                PlayCount = summary.PlayCount
            };

            var infoTask = SendMiguGetAsync($"https://app.c.nf.migu.cn/MIGUM2.0/v1.0/content/resourceinfo.do?needSimple=01&resourceId={Uri.EscapeDataString(pid)}&resourceType=2021", ct);
            var songsTask = SendMiguGetAsync($"https://app.c.nf.migu.cn/MIGUM3.0/resource/playlist/song/v2.0?pageNo=1&pageSize=80&playlistId={Uri.EscapeDataString(pid)}", ct);
            await Task.WhenAll(infoTask, songsTask);

            var infoJson = await infoTask;
            if (!string.IsNullOrWhiteSpace(infoJson))
            {
                using var infoDoc = JsonDocument.Parse(infoJson);
                if (infoDoc.RootElement.TryGetProperty("resource", out var resArr) &&
                    resArr.ValueKind == JsonValueKind.Array &&
                    resArr.GetArrayLength() > 0)
                {
                    var res = resArr[0];
                    var title = res.GetStringProp("title");
                    if (!string.IsNullOrWhiteSpace(title)) detail.Name = title;
                    var desc = res.GetStringProp("summary");
                    if (!string.IsNullOrWhiteSpace(desc)) detail.Description = desc;
                    var owner = res.GetStringProp("ownerName");
                    if (!string.IsNullOrWhiteSpace(owner)) detail.CreatorName = owner;
                    if (res.TryGetProperty("imgItem", out var imgObj) && imgObj.ValueKind == JsonValueKind.Object)
                    {
                        var img = NormalizeMiguImageUrl(imgObj.GetStringProp("img"));
                        if (!string.IsNullOrWhiteSpace(img)) detail.CoverImgUrl = img;
                    }
                }
            }

            var songsJson = await songsTask;
            if (!string.IsNullOrWhiteSpace(songsJson))
            {
                using var songsDoc = JsonDocument.Parse(songsJson);
                if (songsDoc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("songList", out var songList) &&
                    songList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in songList.EnumerateArray())
                    {
                        var song = ParseMiguSongElement(s);
                        if (song != null)
                        {
                            if (string.IsNullOrWhiteSpace(song.PicUrl))
                            {
                                song.PicUrl = detail.CoverImgUrl;
                                if (song.Album != null) song.Album.PicUrl = detail.CoverImgUrl;
                            }
                            detail.Tracks.Add(song);
                        }
                    }
                    detail.TrackCount = detail.Tracks.Count;
                }
            }

            return detail;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> SendMiguGetAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("channel", "0146951");
            using var resp = await _httpClient.SendAsync(req, ct);
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeMiguImageUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ImageUriHelper.DefaultCoverUri;
        var cleaned = raw.Replace("\\/", "/").Trim();
        if (cleaned.StartsWith("//", StringComparison.Ordinal)) return "https:" + cleaned;
        if (cleaned.StartsWith('/')) return "https://d.musicapp.migu.cn" + cleaned;
        return cleaned;
    }

    private static string ExtractMiguPlaylistId(string actionUrl)
    {
        if (string.IsNullOrWhiteSpace(actionUrl)) return string.Empty;
        if (actionUrl.All(char.IsDigit)) return actionUrl;
        var m = Regex.Match(actionUrl, @"(?:id=|/)(\d{5,})");
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    private static long ParseChineseCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var cleaned = text.Trim();
        if (cleaned.EndsWith("亿", StringComparison.Ordinal) &&
            double.TryParse(cleaned[..^1], out double yi))
        {
            return (long)(yi * 100_000_000);
        }
        if (cleaned.EndsWith("万", StringComparison.Ordinal) &&
            double.TryParse(cleaned[..^1], out double wan))
        {
            return (long)(wan * 10_000);
        }
        return long.TryParse(cleaned, out long plain) ? plain : 0;
    }

    private static long ParseMiguDurationMs(string lengthText)
    {
        if (string.IsNullOrWhiteSpace(lengthText)) return 0;
        var parts = lengthText.Split(':');
        if (parts.Length == 3 &&
            int.TryParse(parts[0], out int h) &&
            int.TryParse(parts[1], out int m) &&
            int.TryParse(parts[2], out int s))
        {
            return (h * 3600L + m * 60L + s) * 1000L;
        }
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out int m2) &&
            int.TryParse(parts[1], out int s2))
        {
            return (m2 * 60L + s2) * 1000L;
        }
        return long.TryParse(lengthText, out long sec) ? sec * 1000L : 0;
    }

    // ==================== 匹配与清洗辅助 ====================

    private static SongItem? PickBestMatch(List<SongItem> candidates, SongItem expected)
    {
        if (candidates.Count == 0) return null;
        var expectedName = NormalizeText(expected.Name);
        var expectedArtists = expected.Artists
            .Select(a => NormalizeText(a.Name))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        SongItem? best = null;
        int bestScore = -1;

        foreach (var cand in candidates)
        {
            var candName = NormalizeText(cand.Name);
            if (string.IsNullOrWhiteSpace(candName)) continue;

            bool exactName = candName == expectedName;
            bool partialName = !exactName && (candName.Contains(expectedName) || expectedName.Contains(candName));
            if (!exactName && !partialName) continue;

            // 若双方都有明确时长且相差超过 25 秒，跳过（避免匹配到翻唱剪辑版/伴奏短片）
            if (expected.DurationMs > 45_000 && cand.DurationMs > 45_000 &&
                Math.Abs(expected.DurationMs - cand.DurationMs) > 25_000)
            {
                continue;
            }

            int score = exactName ? 10 : 5;
            if (expectedArtists.Count > 0)
            {
                var candArtist = NormalizeText(cand.ArtistText);
                bool artistMatched = expectedArtists.Any(ea => candArtist.Contains(ea) || ea.Contains(candArtist));
                if (!artistMatched) continue;
                score += 10;
            }

            if (cand.NativeQualityHint is "Hi-Res" or "FLAC") score += 2;
            if (score > bestScore)
            {
                bestScore = score;
                best = cand;
            }
        }

        return best ?? candidates[0];
    }

    private static string NormalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lower = text.ToLowerInvariant();
        var stripped = Regex.Replace(lower, @"[（(【\[].*?[)）】\]]", "");
        stripped = Regex.Replace(stripped, @"[\s\-—_·・'""‘’“”!！?？.,，。&＆+/、]", "");
        return !string.IsNullOrWhiteSpace(stripped)
            ? stripped
            : Regex.Replace(lower, @"[\s\-—_·・'""‘’“”!！?？.,，。&＆+/、]", "");
    }

    private static List<ArtistItem> SplitArtists(string raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(['&', '、', '/', ','], StringSplitOptions.RemoveEmptyEntries)
                 .Select(s => new ArtistItem { Name = s.Trim() })
                 .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                 .ToList();

    private static string GlobalHtmlDecode(string input) =>
        string.IsNullOrEmpty(input) ? string.Empty : global::System.Net.WebUtility.HtmlDecode(input);
}
