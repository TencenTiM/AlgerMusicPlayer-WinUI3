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
    private readonly HttpClient _authedHttpClient;
    private readonly HttpClient _kuwoAppHttpClient;
    private readonly global::AlgerMusicPlayer.WinUI.Services.Auth.PlatformAuthService _platformAuth;
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
    private string _authedSpotifyBearer = string.Empty;
    private long _authedSpotifyBearerExpireMs;
    private string _applePublicDevToken = string.Empty;
    private DateTimeOffset _applePublicDevTokenExpireUtc = DateTimeOffset.MinValue;
    private long _tidalSessionUserId;
    private string _tidalSessionCountryCode = "US";
    private long _lastKugouTotalVer;

    public ThirdPartyMusicService(global::AlgerMusicPlayer.WinUI.Services.Auth.PlatformAuthService platformAuth)
    {
        _platformAuth = platformAuth;
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

        _authedHttpClient = new HttpClient(new HttpClientHandler
        {
            UseCookies = false,
            AutomaticDecompression = global::System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true
        })
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        _authedHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");

        _kuwoAppHttpClient = new HttpClient(new HttpClientHandler
        {
            UseCookies = false,
            AutomaticDecompression = global::System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true
        })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _kuwoAppHttpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "okhttp/3.10.0");
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
            "spotify" => await GetSpotifyPlaylistDetailWithAuthAsync(summary, ct),
            "apple" => (summary.ThirdPartyId.StartsWith("p.", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(summary.ThirdPartyId, "__lib_songs__", StringComparison.OrdinalIgnoreCase))
                ? await GetAppleLibraryPlaylistDetailAsync(summary, ct)
                : await GetAppleAlbumDetailAsync(summary, ct),
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
            var qishuiAuthed = _platformAuth.HasValidSession(AuthMusicPlatform.Qishui) &&
                               await _platformAuth.ApplyAuthHeadersAsync(AuthMusicPlatform.Qishui, req, ct);

            using var resp = await (qishuiAuthed ? _authedHttpClient : _httpClient).SendAsync(req, ct);
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

        var tracks = new List<SongItem>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        string title = summary.Name;
        string desc = summary.Description;
        string cover = summary.CoverImgUrl;
        string creatorName = summary.CreatorName;
        string cursor = "0";

        // 汽水 /playlist/detail/ 单页上限约 40-50 首，按 has_more + next_cursor 最多翻页拉取 5 页（上限 200 首）
        for (int page = 0; page < 5 && tracks.Count < 200; page++)
        {
            using var doc = await PostQishuiPlatformAsync("/playlist/detail/", new
            {
                playlist_id = summary.ThirdPartyId,
                owner_id = "",
                count = 100,
                cursor,
                type = (string?)null,
                common_params = new { }
            }, ct);

            if (doc == null || !doc.RootElement.TryGetProperty("data", out var dataEl))
            {
                if (page == 0) return null;
                break;
            }

            if (page == 0 && dataEl.TryGetProperty("playlist", out var pl) && pl.ValueKind == global::System.Text.Json.JsonValueKind.Object)
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

            int newlyAdded = 0;
            if (dataEl.TryGetProperty("media_entities", out var entities) && entities.ValueKind == global::System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in entities.EnumerateArray())
                {
                    var song = ParseQishuiTrackItem(item);
                    if (song != null && seenIds.Add(song.Id))
                    {
                        tracks.Add(song);
                        newlyAdded++;
                        if (tracks.Count >= 200) break;
                    }
                }
            }

            bool hasMore = dataEl.TryGetProperty("has_more", out var hmEl) && hmEl.ValueKind == global::System.Text.Json.JsonValueKind.True;
            string nextCursor = string.Empty;
            if (dataEl.TryGetProperty("next_cursor", out var ncEl))
            {
                nextCursor = ncEl.ValueKind switch
                {
                    global::System.Text.Json.JsonValueKind.String => ncEl.GetString() ?? string.Empty,
                    global::System.Text.Json.JsonValueKind.Number => ncEl.GetRawText(),
                    _ => string.Empty
                };
            }

            if (hasMore && !string.IsNullOrWhiteSpace(nextCursor) && nextCursor != cursor && newlyAdded > 0)
            {
                cursor = nextCursor;
                continue;
            }
            break;
        }

        // 若 OpenSDK 未返回曲目（如个人私密歌单 / “我喜欢的音乐”），回退汽水网页端登录态 API 与分享 SSR 页面
        if (tracks.Count == 0 && _platformAuth.HasValidSession(AuthMusicPlatform.Qishui))
        {
            string webCursor = string.Empty;
            for (int page = 0; page < 4 && tracks.Count < 200; page++)
            {
                var query = $"playlist_id={Uri.EscapeDataString(summary.ThirdPartyId)}&count=50" +
                            (string.IsNullOrEmpty(webCursor) ? string.Empty : $"&cursor={Uri.EscapeDataString(webCursor)}");
                using var webDoc = await GetQishuiWebApiAsync("/luna/pc/playlist/detail", query, ct);
                if (webDoc == null) break;

                var rootEl = webDoc.RootElement.TryGetProperty("data", out var innerData) &&
                             innerData.ValueKind == global::System.Text.Json.JsonValueKind.Object
                    ? innerData
                    : webDoc.RootElement;

                if (page == 0 && rootEl.TryGetProperty("playlist", out var plObj) && plObj.ValueKind == global::System.Text.Json.JsonValueKind.Object)
                {
                    title = FirstNonEmpty(plObj.GetStringProp("title"), plObj.GetStringProp("name"), title);
                    var parsedCover = TryExtractQishuiNodeCover(plObj);
                    if (!string.IsNullOrWhiteSpace(parsedCover)) cover = parsedCover;
                    if (plObj.TryGetProperty("owner", out var ownEl) && ownEl.ValueKind == global::System.Text.Json.JsonValueKind.Object)
                    {
                        creatorName = FirstNonEmpty(ownEl.GetStringProp("nickname"), ownEl.GetStringProp("name"), creatorName);
                    }
                }

                int added = 0;
                foreach (var arrName in new[] { "media_resources", "media_entities", "tracks" })
                {
                    if (rootEl.TryGetProperty(arrName, out var arrEl) && arrEl.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var item in arrEl.EnumerateArray())
                        {
                            var song = ParseQishuiTrackItem(item);
                            if (song != null && seenIds.Add(song.Id))
                            {
                                tracks.Add(song);
                                added++;
                                if (tracks.Count >= 200) break;
                            }
                        }
                    }
                }

                bool moreWeb = GetBoolProp(rootEl, "has_more");
                string nextWebCursor = rootEl.GetStringProp("next_cursor");
                if (!moreWeb || added == 0 || string.IsNullOrWhiteSpace(nextWebCursor) || nextWebCursor == webCursor)
                    break;
                webCursor = nextWebCursor;
            }
        }

        if (tracks.Count == 0)
        {
            await TryPopulateQishuiSharePlaylistFallbackAsync(summary.ThirdPartyId, tracks, seenIds, ct);
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

    private async Task<global::System.Text.Json.JsonDocument?> GetQishuiWebApiAsync(
        string path, string extraQuery, CancellationToken ct)
    {
        try
        {
            var baseQuery = $"aid=386088&app_name=luna_pc&version_code=30050100&iid={_qishuiInstallId}&region=cn&geo_region=cn&os_region=cn&sim_region=";
            var fullQuery = string.IsNullOrWhiteSpace(extraQuery) ? baseQuery : $"{baseQuery}&{extraQuery}";
            var url = $"https://api.qishui.com{path}?{fullQuery}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Origin", "https://music.douyin.com");
            req.Headers.TryAddWithoutValidation("Referer", "https://music.douyin.com/");
            await _platformAuth.ApplyAuthHeadersAsync(AuthMusicPlatform.Qishui, req, ct);
            using var resp = await _authedHttpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            return global::System.Text.Json.JsonDocument.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    private async Task TryPopulateQishuiSharePlaylistFallbackAsync(
        string playlistId, List<SongItem> tracks, HashSet<string> seenIds, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://music.douyin.com/qishui/share/playlist?playlist_id={Uri.EscapeDataString(playlistId)}");
            req.Headers.Referrer = new Uri("https://music.douyin.com/");
            await _platformAuth.ApplyAuthHeadersAsync(AuthMusicPlatform.Qishui, req, ct);
            using var resp = await _authedHttpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return;

            var html = await resp.Content.ReadAsStringAsync(ct);
            var routerJson = ExtractBalancedJsonAfterMarker(html, "_ROUTER_DATA");
            if (string.IsNullOrWhiteSpace(routerJson)) return;

            using var doc = global::System.Text.Json.JsonDocument.Parse(routerJson);
            if (!doc.RootElement.TryGetProperty("loaderData", out var ld)) return;
            foreach (var prop in ld.EnumerateObject())
            {
                if (prop.Value.ValueKind != global::System.Text.Json.JsonValueKind.Object) continue;
                foreach (var arrName in new[] { "trackList", "media_entities", "tracks" })
                {
                    if (prop.Value.TryGetProperty(arrName, out var arr) && arr.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var item in arr.EnumerateArray())
                        {
                            var song = ParseQishuiTrackItem(item);
                            if (song != null && seenIds.Add(song.Id))
                            {
                                tracks.Add(song);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }
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

        PlaylistItem? publicDetail = null;
        try
        {
            var json = await _httpClient.GetStringAsync($"https://api.deezer.com/playlist/{Uri.EscapeDataString(summary.ThirdPartyId)}", ct);
            using var doc = global::System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("error", out _))
            {
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

                publicDetail = new PlaylistItem
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
        }
        catch
        {
            publicDetail = null;
        }

        if (publicDetail is { Tracks.Count: > 0 })
        {
            return publicDetail;
        }

        // 私有歌单、未公开“我喜欢”等无法通过公开 API 读取时，若已登录则回退 Deezer Web GW 接口
        if (_platformAuth.HasValidSession(AuthMusicPlatform.Deezer))
        {
            var gwDetail = await GetDeezerGwPlaylistDetailAsync(summary, ct);
            if (gwDetail is { Tracks.Count: > 0 } || publicDetail == null)
            {
                return gwDetail ?? publicDetail;
            }
        }

        return publicDetail;
    }

    private async Task<(long UserId, string Nickname, string ApiToken, string LovedTracksId)?> GetDeezerGwSessionAsync(
        CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://www.deezer.com/ajax/gw-light.php?method=deezer.getUserData&input=3&api_version=1.0&api_token=");
            req.Headers.TryAddWithoutValidation("Origin", "https://www.deezer.com");
            req.Headers.TryAddWithoutValidation("Referer", "https://www.deezer.com/");
            await _platformAuth.ApplyAuthHeadersAsync(AuthMusicPlatform.Deezer, req, ct);
            using var resp = await _authedHttpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = global::System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                !results.TryGetProperty("USER", out var user))
            {
                return null;
            }

            var userId = ReadDeezerFlexLong(user, "USER_ID");
            if (userId <= 0) return null;

            var apiToken = results.GetStringProp("checkForm");
            var lovedId = ReadDeezerFlexString(user, "LOVEDTRACKS_ID");
            var nickname = FirstNonEmpty(user.GetStringProp("BLOG_NAME"), $"Deezer #{userId}");
            return (userId, nickname, apiToken, lovedId);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> PostDeezerGwAsync(string method, string apiToken, string jsonBody, CancellationToken ct)
    {
        try
        {
            var url = $"https://www.deezer.com/ajax/gw-light.php?method={Uri.EscapeDataString(method)}&input=3&api_version=1.0&api_token={Uri.EscapeDataString(apiToken ?? string.Empty)}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonBody, global::System.Text.Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("Origin", "https://www.deezer.com");
            req.Headers.TryAddWithoutValidation("Referer", "https://www.deezer.com/");
            await _platformAuth.ApplyAuthHeadersAsync(AuthMusicPlatform.Deezer, req, ct);
            using var resp = await _authedHttpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    private async Task<PlaylistItem?> GetDeezerGwPlaylistDetailAsync(PlaylistItem summary, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(summary.ThirdPartyId)) return null;

        var gw = await GetDeezerGwSessionAsync(ct);
        if (gw == null || string.IsNullOrWhiteSpace(gw.Value.ApiToken)) return null;

        var body = global::System.Text.Json.JsonSerializer.Serialize(new
        {
            playlist_id = summary.ThirdPartyId,
            lang = "en",
            nb = 200,
            start = 0,
            tab = 0,
            tags = true,
            header = true
        });
        var json = await PostDeezerGwAsync("deezer.pagePlaylist", gw.Value.ApiToken, body, ct);
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = global::System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results)) return null;

            string title = summary.Name;
            string desc = summary.Description;
            string cover = summary.CoverImgUrl;
            string creator = summary.CreatorName;

            if (results.TryGetProperty("DATA", out var data) && data.ValueKind == global::System.Text.Json.JsonValueKind.Object)
            {
                title = FirstNonEmpty(data.GetStringProp("TITLE"), title);
                desc = FirstNonEmpty(data.GetStringProp("DESCRIPTION"), desc);
                creator = FirstNonEmpty(data.GetStringProp("PARENT_USERNAME"), creator);
                var pic = data.GetStringProp("PLAYLIST_PICTURE");
                if (!string.IsNullOrWhiteSpace(pic) && string.IsNullOrWhiteSpace(cover))
                {
                    cover = $"https://e-cdns-images.dzcdn.net/images/playlist/{pic}/500x500-000000-80-0-0.jpg";
                }
            }

            var tracks = new List<SongItem>();
            if (results.TryGetProperty("SONGS", out var songsObj) &&
                songsObj.TryGetProperty("data", out var songsData) &&
                songsData.ValueKind == global::System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in songsData.EnumerateArray())
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
                CreatorName = GlobalHtmlDecode(creator),
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

    private static long ReadDeezerFlexLong(global::System.Text.Json.JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (!el.TryGetProperty(k, out var val)) continue;
            if (val.ValueKind == global::System.Text.Json.JsonValueKind.Number && val.TryGetInt64(out var n) && n > 0)
                return n;
            if (val.ValueKind == global::System.Text.Json.JsonValueKind.String &&
                long.TryParse(val.GetString(), out var pn) && pn > 0)
                return pn;
        }
        return 0;
    }

    private static string ReadDeezerFlexString(global::System.Text.Json.JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (!el.TryGetProperty(k, out var val)) continue;
            if (val.ValueKind == global::System.Text.Json.JsonValueKind.String)
            {
                var s = val.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            if (val.ValueKind == global::System.Text.Json.JsonValueKind.Number)
                return val.GetRawText();
        }
        return string.Empty;
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
        var tid = ReadDeezerFlexLong(item, "id", "SNG_ID", "SONG_ID");
        var title = FirstNonEmpty(item.GetStringProp("title"), item.GetStringProp("title_short"), item.GetStringProp("SNG_TITLE"));
        if (tid <= 0 || string.IsNullOrWhiteSpace(title)) return null;

        var artistName = item.TryGetProperty("artist", out var art) ? art.GetStringProp("name") : null;
        if (string.IsNullOrWhiteSpace(artistName))
        {
            artistName = item.GetStringProp("ART_NAME");
        }
        if (string.IsNullOrWhiteSpace(artistName) &&
            item.TryGetProperty("ARTISTS", out var artList) &&
            artList.ValueKind == global::System.Text.Json.JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var a in artList.EnumerateArray())
            {
                var n = a.GetStringProp("ART_NAME");
                if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
            }
            if (names.Count > 0) artistName = string.Join(" / ", names);
        }
        artistName = FirstNonEmpty(artistName, "Unknown Artist");

        string albumName = string.Empty;
        string cover = string.Empty;
        if (item.TryGetProperty("album", out var alb) && alb.ValueKind == global::System.Text.Json.JsonValueKind.Object)
        {
            albumName = alb.GetStringProp("title");
            cover = FirstNonEmpty(alb.GetStringProp("cover_big"), alb.GetStringProp("cover_medium"));
        }
        if (string.IsNullOrWhiteSpace(albumName))
        {
            albumName = item.GetStringProp("ALB_TITLE");
        }
        if (string.IsNullOrWhiteSpace(cover))
        {
            var albPic = item.GetStringProp("ALB_PICTURE");
            if (!string.IsNullOrWhiteSpace(albPic))
            {
                cover = $"https://e-cdns-images.dzcdn.net/images/cover/{albPic}/500x500-000000-80-0-0.jpg";
            }
        }
        var durSec = ReadDeezerFlexLong(item, "duration", "DURATION");
        var previewUrl = item.GetStringProp("preview");
        if (string.IsNullOrWhiteSpace(previewUrl) &&
            item.TryGetProperty("MEDIA", out var mediaEl) &&
            mediaEl.ValueKind == global::System.Text.Json.JsonValueKind.Array)
        {
            foreach (var m in mediaEl.EnumerateArray())
            {
                var href = m.GetStringProp("HREF");
                if (!string.IsNullOrWhiteSpace(href))
                {
                    previewUrl = href;
                    break;
                }
            }
        }

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
            var authed = _platformAuth.HasValidSession(AuthMusicPlatform.Tidal) &&
                         await _platformAuth.ApplyAuthHeadersAsync(AuthMusicPlatform.Tidal, req, ct);
            if (!req.Headers.Contains("Authorization"))
            {
                req.Headers.TryAddWithoutValidation("x-tidal-token", TidalPublicClientToken);
            }
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var resp = await (authed ? _authedHttpClient : _httpClient).SendAsync(req, ct);
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

    private async Task EnsureTidalSessionInfoAsync(CancellationToken ct)
    {
        if (_tidalSessionUserId > 0 || !_platformAuth.HasValidSession(AuthMusicPlatform.Tidal))
            return;

        var json = await SendTidalGetAsync("https://api.tidal.com/v1/sessions", ct);
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            using var doc = global::System.Text.Json.JsonDocument.Parse(json);
            var uid = doc.RootElement.GetLongProp("userId");
            if (uid > 0) _tidalSessionUserId = uid;
            var cc = doc.RootElement.GetStringProp("countryCode");
            if (!string.IsNullOrWhiteSpace(cc)) _tidalSessionCountryCode = cc.Trim().ToUpperInvariant();
        }
        catch
        {
        }
    }

    private async Task<PlaylistItem?> GetTidalNativePlaylistDetailAsync(PlaylistItem summary, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(summary.ThirdPartyId)) return null;

        if (_platformAuth.HasValidSession(AuthMusicPlatform.Tidal) && _tidalSessionUserId <= 0)
        {
            await EnsureTidalSessionInfoAsync(ct);
        }

        var countryCode = string.IsNullOrWhiteSpace(_tidalSessionCountryCode) ? "US" : _tidalSessionCountryCode;
        bool isFavTracks = string.Equals(summary.ThirdPartyId, "__fav_tracks__", StringComparison.Ordinal);
        if (isFavTracks && _tidalSessionUserId <= 0) return null;

        var tracks = new List<SongItem>();
        for (int page = 0; page < 2; page++)
        {
            int offset = page * 100;
            var url = isFavTracks
                ? $"https://api.tidal.com/v1/users/{_tidalSessionUserId}/favorites/tracks?countryCode={Uri.EscapeDataString(countryCode)}&limit=100&offset={offset}"
                : $"https://api.tidal.com/v1/playlists/{Uri.EscapeDataString(summary.ThirdPartyId)}/items?limit=100&offset={offset}&countryCode={Uri.EscapeDataString(countryCode)}";

            var json = await SendTidalGetAsync(url, ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                if (page == 0) return null;
                break;
            }

            try
            {
                using var doc = global::System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("items", out var items) ||
                    items.ValueKind != global::System.Text.Json.JsonValueKind.Array)
                {
                    break;
                }

                int added = 0;
                foreach (var item in items.EnumerateArray())
                {
                    var song = ParseTidalTrackElement(item, summary.CoverImgUrl, tracks.Count + 1);
                    if (song != null)
                    {
                        tracks.Add(song);
                        added++;
                    }
                }

                if (added == 0 || items.GetArrayLength() < 100) break;
            }
            catch
            {
                if (page == 0) return null;
                break;
            }
        }

        return new PlaylistItem
        {
            Source = "tidal",
            ThirdPartyId = summary.ThirdPartyId,
            Name = summary.Name,
            Description = summary.Description,
            CoverImgUrl = FirstNonEmpty(summary.CoverImgUrl, tracks.FirstOrDefault()?.PicUrl),
            CreatorName = summary.CreatorName,
            TrackCount = tracks.Count > 0 ? tracks.Count : summary.TrackCount,
            PlayCount = summary.PlayCount,
            Tracks = tracks
        };
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
            var authed = _platformAuth.HasValidSession(AuthMusicPlatform.YouTubeMusic) &&
                         await _platformAuth.ApplyAuthHeadersAsync(AuthMusicPlatform.YouTubeMusic, req, ct);
            using var resp = await (authed ? _authedHttpClient : _httpClient).SendAsync(req, ct);
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
        var setVideoId = TryExtractYtmSetVideoId(renderer);

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
            YtmSetVideoId = setVideoId,
            DisplayIndex = displayIndex
        };
    }

    private static string TryExtractYtmSetVideoId(JsonElement renderer)
    {
        var direct = renderer.GetStringProp("setVideoId");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;

        if (renderer.TryGetProperty("playlistItemData", out var pid) && pid.ValueKind == JsonValueKind.Object)
        {
            var fromData = FirstNonEmpty(pid.GetStringProp("playlistSetVideoId"), pid.GetStringProp("setVideoId"));
            if (!string.IsNullOrWhiteSpace(fromData)) return fromData;
        }

        if (renderer.TryGetProperty("menu", out var menu) &&
            menu.TryGetProperty("menuRenderer", out var mr) &&
            mr.TryGetProperty("items", out var mItems) &&
            mItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in mItems.EnumerateArray())
            {
                if (item.TryGetProperty("menuServiceItemRenderer", out var msr) &&
                    msr.TryGetProperty("serviceEndpoint", out var ep) &&
                    ep.TryGetProperty("playlistEditEndpoint", out var edit) &&
                    edit.TryGetProperty("actions", out var actions) &&
                    actions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var act in actions.EnumerateArray())
                    {
                        var svid = act.GetStringProp("setVideoId");
                        if (!string.IsNullOrWhiteSpace(svid)) return svid;
                    }
                }
            }
        }

        return string.Empty;
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
        if (!browseId.StartsWith("VL", StringComparison.OrdinalIgnoreCase) &&
            !browseId.Equals("LM", StringComparison.OrdinalIgnoreCase))
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
                    if (_platformAuth.HasValidSession(AuthMusicPlatform.Tidal))
                    {
                        var fullJson = await SendTidalGetAsync(
                            $"https://api.tidal.com/v1/tracks/{Uri.EscapeDataString(tid)}/playbackinfopostpaywall?audioquality=LOSSLESS&playbackmode=STREAM&assetpresentation=FULL&countryCode=US",
                            ct);
                        if (!string.IsNullOrWhiteSpace(fullJson))
                        {
                            try
                            {
                                using var fullDoc = global::System.Text.Json.JsonDocument.Parse(fullJson);
                                var manifestB64 = fullDoc.RootElement.GetStringProp("manifest");
                                if (!string.IsNullOrWhiteSpace(manifestB64))
                                {
                                    var decoded = global::System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(manifestB64));
                                    if (decoded.TrimStart().StartsWith('{'))
                                    {
                                        using var manifestDoc = global::System.Text.Json.JsonDocument.Parse(decoded);
                                        if (manifestDoc.RootElement.TryGetProperty("urls", out var urlsArr) &&
                                            urlsArr.ValueKind == global::System.Text.Json.JsonValueKind.Array &&
                                            urlsArr.GetArrayLength() > 0)
                                        {
                                            var fullUrl = urlsArr[0].GetString();
                                            if (!string.IsNullOrWhiteSpace(fullUrl) &&
                                                fullUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                            {
                                                song.NativePlayUrl = fullUrl;
                                                return (fullUrl, 1411);
                                            }
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // 降级回退至试听接口
                            }
                        }
                    }

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

    private static string DecodeKugouEscapedText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var decodedUnicode = Regex.Replace(raw, @"%u([0-9a-fA-F]{4})", m =>
            ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        try
        {
            return Uri.UnescapeDataString(decodedUnicode);
        }
        catch
        {
            return decodedUnicode;
        }
    }

    private static (string UserId, string Token, string Nickname, string Dfid, string Mid, string AppId)
        ParseKugouCompositeCookie(AccountSession? session)
    {
        if (session == null) return (string.Empty, string.Empty, string.Empty, "-", string.Empty, "1014");

        var rawCookie = session.CookieHeader ?? string.Empty;
        var outer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in rawCookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int idx = part.IndexOf('=');
            if (idx <= 0) continue;
            outer[part[..idx].Trim()] = part[(idx + 1)..].Trim();
        }

        var inner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? kgComposite = outer.TryGetValue("KuGoo", out var kgRaw)
            ? kgRaw
            : (rawCookie.Contains("KugooID=", StringComparison.OrdinalIgnoreCase) ? rawCookie : null);

        if (!string.IsNullOrWhiteSpace(kgComposite))
        {
            var normalized = kgComposite.Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
            if (normalized.Contains("%3D", StringComparison.OrdinalIgnoreCase) && normalized.Contains("%26", StringComparison.OrdinalIgnoreCase))
            {
                normalized = DecodeKugouEscapedText(normalized);
            }
            foreach (var kv in normalized.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int idx = kv.IndexOf('=');
                if (idx <= 0) continue;
                var k = kv[..idx].Trim();
                var v = kv[(idx + 1)..].Trim();
                if (k.Equals("KugooID", StringComparison.OrdinalIgnoreCase) && v == "0") continue;
                inner[k] = v;
            }
        }

        string userId = FirstNonEmpty(
            inner.GetValueOrDefault("KugooID"),
            outer.GetValueOrDefault("KugooID"),
            inner.GetValueOrDefault("userid"),
            outer.GetValueOrDefault("userid"),
            session.UserId);
        if (userId == "0") userId = string.Empty;

        string token = FirstNonEmpty(
            inner.GetValueOrDefault("t"),
            outer.GetValueOrDefault("t"),
            inner.GetValueOrDefault("token"),
            outer.GetValueOrDefault("token"),
            session.AccessToken);

        string nickRaw = FirstNonEmpty(
            inner.GetValueOrDefault("NickName"),
            inner.GetValueOrDefault("nickname"),
            outer.GetValueOrDefault("NickName"),
            session.Nickname);
        string nickname = DecodeKugouEscapedText(nickRaw);

        string dfid = FirstNonEmpty(
            outer.GetValueOrDefault("kg_dfid"),
            inner.GetValueOrDefault("dfid"),
            outer.GetValueOrDefault("dfid"),
            "-");

        string mid = FirstNonEmpty(
            outer.GetValueOrDefault("kg_mid"),
            inner.GetValueOrDefault("mid"),
            outer.GetValueOrDefault("mid"));
        if (string.IsNullOrWhiteSpace(mid) && !string.IsNullOrWhiteSpace(userId))
        {
            var bytes = global::System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("kugou_" + userId));
            mid = Convert.ToHexString(bytes).ToLowerInvariant();
        }

        string appId = FirstNonEmpty(inner.GetValueOrDefault("a_id"), outer.GetValueOrDefault("a_id"), "1014");
        return (userId, token, nickname, dfid, mid, appId);
    }

    private static string ComputeKugouGatewaySignature(
        IReadOnlyDictionary<string, string> queryParams,
        string bodyJson,
        string salt)
    {
        var sb = new StringBuilder();
        sb.Append(salt);
        foreach (var kv in queryParams)
        {
            sb.Append(kv.Key).Append('=').Append(kv.Value);
        }
        sb.Append(bodyJson);
        sb.Append(salt);
        var hash = global::System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<string?> PostKugouCloudListGatewayAsync(
        string path,
        Dictionary<string, object> bodyFields,
        CancellationToken ct)
    {
        var (okJson, _) = await PostKugouCloudListGatewayWithErrorAsync(path, bodyFields, ct);
        return okJson;
    }

    /// <summary>
    /// 请求酷狗云歌单网关（支持多 AppId 签名回退，并返回失败时的原始响应以便透传 error_code）
    /// </summary>
    private async Task<(string? OkJson, string? LastErrorJson)> PostKugouCloudListGatewayWithErrorAsync(
        string path,
        Dictionary<string, object> bodyFields,
        CancellationToken ct)
    {
        var session = _platformAuth.TryGetSession(AuthMusicPlatform.KuGou);
        var creds = ParseKugouCompositeCookie(session);
        if (string.IsNullOrWhiteSpace(creds.UserId) || string.IsNullOrWhiteSpace(creds.Token))
        {
            return (null, null);
        }

        object uidObj = long.TryParse(creds.UserId, out long numUid) ? numUid : creds.UserId;
        bodyFields["userid"] = uidObj;
        bodyFields["token"] = creds.Token;
        var bodyJson = JsonSerializer.Serialize(bodyFields);

        var attempts = new List<(string AppId, string ClientVer, string Plat, string? SrcAppId, string Salt)>
        {
            ("1014", "20000", "4", "2919", "NVPh5oo715z5DIWAeQlhMDsWXXQV4hwt"),
            ("1005", "20489", "1", null, "OIlwieks28dk2k092lksi2UIkp"),
            ("3116", "11440", "1", null, "LnT6xpN3khm36zse0QzvmgTZ3waWdRSA")
        };
        int preferredIndex = attempts.FindIndex(c => string.Equals(c.AppId, creds.AppId, StringComparison.OrdinalIgnoreCase));
        if (preferredIndex > 0)
        {
            var preferred = attempts[preferredIndex];
            attempts.RemoveAt(preferredIndex);
            attempts.Insert(0, preferred);
        }

        string? lastErrorJson = null;
        foreach (var cfg in attempts)
        {
            try
            {
                var clientTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                var qParams = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["appid"] = cfg.AppId,
                    ["clienttime"] = clientTime,
                    ["clientver"] = cfg.ClientVer,
                    ["dfid"] = creds.Dfid,
                    ["mid"] = creds.Mid,
                    ["plat"] = cfg.Plat,
                    ["token"] = creds.Token,
                    ["userid"] = creds.UserId,
                    ["uuid"] = "-"
                };
                if (!string.IsNullOrWhiteSpace(cfg.SrcAppId))
                {
                    qParams["srcappid"] = cfg.SrcAppId;
                }

                var sig = ComputeKugouGatewaySignature(qParams, bodyJson, cfg.Salt);
                var query = string.Join("&", qParams.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
                var url = $"https://gateway.kugou.com{path}?{query}&signature={sig}";

                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
                };
                req.Headers.TryAddWithoutValidation("x-router", "cloudlist.service.kugou.com");
                req.Headers.TryAddWithoutValidation("dfid", creds.Dfid);
                req.Headers.TryAddWithoutValidation("mid", creds.Mid);
                req.Headers.TryAddWithoutValidation("clienttime", clientTime);
                req.Headers.TryAddWithoutValidation("Origin", "https://www.kugou.com");
                req.Headers.TryAddWithoutValidation("Referer", "https://www.kugou.com/");
                await _platformAuth.ApplyAuthHeadersAsync(AuthMusicPlatform.KuGou, req, ct);

                using var resp = await _authedHttpClient.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) continue;

                var respText = await resp.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(respText)) continue;

                using var doc = JsonDocument.Parse(respText);
                int status = doc.RootElement.GetIntProp("status");
                int errCode = doc.RootElement.GetIntProp("err_code");
                if (errCode == 0) errCode = doc.RootElement.GetIntProp("error_code");
                if (errCode == 0) errCode = doc.RootElement.GetIntProp("errcode");
                if (status == 1 && errCode == 0) return (respText, null);

                lastErrorJson = respText;
                if (errCode is 20017 or 20006 or 20010) continue;
            }
            catch
            {
            }
        }

        return (null, lastErrorJson);
    }

    /// <summary>
    /// 拉取酷狗云歌单最新集合版本号（total_ver），用于删单接口版本校验
    /// </summary>
    private async Task<long> FetchKugouTotalVerAsync(CancellationToken ct)
    {
        try
        {
            var body = new Dictionary<string, object>
            {
                ["total_ver"] = 0,
                ["type"] = 2,
                ["page"] = 1,
                ["pagesize"] = 1
            };

            var json = await PostKugouCloudListGatewayAsync("/v7/get_all_list", body, ct);
            if (string.IsNullOrWhiteSpace(json)) return 0;

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object)
            {
                long tv = dataEl.GetLongProp("total_ver");
                if (tv > 0)
                {
                    _lastKugouTotalVer = tv;
                    return tv;
                }
            }

            long rootTv = doc.RootElement.GetLongProp("total_ver");
            if (rootTv > 0)
            {
                _lastKugouTotalVer = rootTv;
                return rootTv;
            }
        }
        catch
        {
        }

        return 0;
    }

    private static SongItem? ParseKugouCloudFileElement(JsonElement item, string fallbackCover, int displayIndex)
    {
        var stdHash = item.GetStringProp("hash");
        var hqHash = item.GetStringProp("320hash");
        var sqHash = item.GetStringProp("sqhash");
        var resHash = item.GetStringProp("reshash");

        string unionCover = string.Empty;
        if (item.TryGetProperty("trans_param", out var tp) && tp.ValueKind == JsonValueKind.Object)
        {
            if (string.IsNullOrWhiteSpace(hqHash)) hqHash = tp.GetStringProp("ogg_320_hash");
            unionCover = tp.GetStringProp("union_cover");
        }

        if (item.TryGetProperty("relate_goods", out var goods) && goods.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in goods.EnumerateArray())
            {
                int lvl = g.GetIntProp("level");
                var gHash = g.GetStringProp("hash");
                if (string.IsNullOrWhiteSpace(gHash)) continue;
                if (lvl == 3 && string.IsNullOrWhiteSpace(hqHash)) hqHash = gHash;
                else if (lvl == 4 && string.IsNullOrWhiteSpace(sqHash)) sqHash = gHash;
                else if (lvl == 5 && string.IsNullOrWhiteSpace(resHash)) resHash = gHash;
            }
        }

        var primaryHash = FirstNonEmpty(sqHash, hqHash, stdHash, resHash);
        if (string.IsNullOrWhiteSpace(primaryHash)) return null;

        var rawName = GlobalHtmlDecode(FirstNonEmpty(item.GetStringProp("name"), item.GetStringProp("songname")));
        rawName = Regex.Replace(rawName, @"\.(mp3|flac|wav|aac|m4a|ogg|wma)$", string.Empty, RegexOptions.IgnoreCase).Trim();

        string songName = GlobalHtmlDecode(item.GetStringProp("songname"));
        string parsedArtist = string.Empty;
        if (string.IsNullOrWhiteSpace(songName))
        {
            int sep = rawName.IndexOf(" - ", StringComparison.Ordinal);
            if (sep > 0)
            {
                parsedArtist = rawName[..sep].Trim();
                songName = rawName[(sep + 3)..].Trim();
            }
            else
            {
                songName = rawName;
            }
        }
        if (string.IsNullOrWhiteSpace(songName)) return null;

        var artists = new List<ArtistItem>();
        if (item.TryGetProperty("singerinfo", out var singerArr) && singerArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in singerArr.EnumerateArray())
            {
                var sn = GlobalHtmlDecode(s.GetStringProp("name"));
                if (!string.IsNullOrWhiteSpace(sn)) artists.Add(new ArtistItem { Name = sn });
            }
        }
        if (artists.Count == 0 && !string.IsNullOrWhiteSpace(parsedArtist))
        {
            artists = SplitArtists(parsedArtist);
        }

        string albumName = string.Empty;
        string albumId = string.Empty;
        if (item.TryGetProperty("albuminfo", out var alb) && alb.ValueKind == JsonValueKind.Object)
        {
            albumName = GlobalHtmlDecode(alb.GetStringProp("name"));
            if (alb.TryGetProperty("id", out var aidEl))
            {
                var rawId = aidEl.ValueKind == JsonValueKind.Number ? aidEl.GetRawText() : aidEl.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(rawId) && rawId != "0") albumId = rawId;
            }
        }
        if (string.IsNullOrWhiteSpace(albumName)) albumName = GlobalHtmlDecode(item.GetStringProp("album_name"));
        if (string.IsNullOrWhiteSpace(albumId)) albumId = item.GetStringProp("album_id");

        long tl = item.GetLongProp("timelen");
        long durationMs = tl > 2000 ? tl : tl > 0 ? tl * 1000L : item.GetLongProp("duration") * 1000L;

        var cover = FirstNonEmpty(item.GetStringProp("cover"), unionCover, fallbackCover);
        if (!string.IsNullOrWhiteSpace(cover))
        {
            cover = cover.Replace("\\/", "/").Replace("{size}", "400");
        }

        string qualityHint = !string.IsNullOrWhiteSpace(resHash) ? "Hi-Res"
            : !string.IsNullOrWhiteSpace(sqHash) ? "FLAC"
            : !string.IsNullOrWhiteSpace(hqHash) ? "320k"
            : "128k";

        long cloudFileId = item.GetLongProp("fileid");
        if (cloudFileId <= 0 && long.TryParse(item.GetStringProp("fileid"), out var parsedFileId))
            cloudFileId = parsedFileId;
        if (cloudFileId <= 0)
            cloudFileId = item.GetLongProp("file_id");

        return new SongItem
        {
            Id = $"kg_{primaryHash.ToLowerInvariant()}",
            Name = songName,
            Artists = artists,
            Album = new AlbumItem { Name = albumName, PicUrl = cover },
            PicUrl = cover,
            DurationMs = durationMs,
            Source = "kugou",
            KugouHash = stdHash,
            Kugou320Hash = hqHash,
            KugouSqHash = sqHash,
            KugouResHash = resHash,
            KugouAlbumId = albumId,
            KugouCloudFileId = cloudFileId,
            NativeQualityHint = qualityHint,
            DisplayIndex = displayIndex
        };
    }

    private async Task<PlaylistItem?> GetKugouCloudPlaylistDetailAsync(string listId, PlaylistItem summary, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(listId)) return null;

        var detail = new PlaylistItem
        {
            Id = summary.Id,
            Source = "kugou",
            ThirdPartyId = summary.ThirdPartyId,
            Name = summary.Name,
            CoverImgUrl = summary.CoverImgUrl,
            Description = summary.Description,
            CreatorName = string.IsNullOrWhiteSpace(summary.CreatorName) ? "酷狗音乐" : summary.CreatorName,
            PlayCount = summary.PlayCount,
            TrackCount = summary.TrackCount
        };

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        object listIdObj = long.TryParse(listId, out long numListId) ? numListId : listId;
        const int pageSize = 100;

        for (int page = 1; page <= 5; page++)
        {
            var body = new Dictionary<string, object>
            {
                ["listid"] = listIdObj,
                ["type"] = 0,
                ["page"] = page,
                ["pagesize"] = pageSize,
                ["area_code"] = 1,
                ["allplatform"] = 1,
                ["show_cover"] = 1
            };

            var json = await PostKugouCloudListGatewayAsync("/v4/get_list_all_file_v3", body, ct);
            if (string.IsNullOrWhiteSpace(json)) break;

            int fetched = 0;
            int totalCount = detail.TrackCount;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
                    dataEl.ValueKind != JsonValueKind.Object)
                {
                    break;
                }

                if (page == 1 && dataEl.TryGetProperty("list_info", out var listInfo) && listInfo.ValueKind == JsonValueKind.Object)
                {
                    var name = GlobalHtmlDecode(listInfo.GetStringProp("name"));
                    if (!string.IsNullOrWhiteSpace(name)) detail.Name = name;
                    var pic = listInfo.GetStringProp("pic").Replace("\\/", "/").Replace("{size}", "400");
                    if (!string.IsNullOrWhiteSpace(pic)) detail.CoverImgUrl = pic;
                    var intro = GlobalHtmlDecode(listInfo.GetStringProp("intro"));
                    if (!string.IsNullOrWhiteSpace(intro)) detail.Description = intro;
                    var creator = GlobalHtmlDecode(listInfo.GetStringProp("list_create_username"));
                    if (!string.IsNullOrWhiteSpace(creator)) detail.CreatorName = creator;
                    int lc = listInfo.GetIntProp("count");
                    if (lc > 0) totalCount = lc;
                }

                int dataCount = dataEl.GetIntProp("count");
                if (dataCount > 0) totalCount = dataCount;

                JsonElement arr = default;
                bool hasArr = (dataEl.TryGetProperty("info", out arr) && arr.ValueKind == JsonValueKind.Array) ||
                              (dataEl.TryGetProperty("songs", out arr) && arr.ValueKind == JsonValueKind.Array);
                if (!hasArr) break;

                foreach (var item in arr.EnumerateArray())
                {
                    fetched++;
                    var song = ParseKugouCloudFileElement(item, detail.CoverImgUrl, detail.Tracks.Count + 1);
                    if (song != null && seenIds.Add(song.Id))
                    {
                        if (!ImageUriHelper.HasRealImageUrl(detail.CoverImgUrl) && ImageUriHelper.HasRealImageUrl(song.PicUrl))
                        {
                            detail.CoverImgUrl = song.PicUrl;
                        }
                        detail.Tracks.Add(song);
                    }
                }
            }
            catch
            {
                break;
            }

            if (fetched < pageSize || (totalCount > 0 && detail.Tracks.Count >= totalCount))
                break;
        }

        if (detail.Tracks.Count > 0) detail.TrackCount = detail.Tracks.Count;
        return detail;
    }

    private async Task<PlaylistItem?> GetKugouPlaylistDetailAsync(PlaylistItem summary, CancellationToken ct)
    {
        var specialId = !string.IsNullOrWhiteSpace(summary.ThirdPartyId) ? summary.ThirdPartyId : summary.Id.ToString();
        if (string.IsNullOrWhiteSpace(specialId)) return null;

        if (specialId.StartsWith("kgcloud:", StringComparison.OrdinalIgnoreCase))
        {
            return await GetKugouCloudPlaylistDetailAsync(specialId["kgcloud:".Length..], summary, ct);
        }

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
            PlaylistItem? detail = null;
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int pn = 0; pn < 3; pn++)
            {
                var url = $"http://nplserver.kuwo.cn/pl.svc?op=getlistinfo&pid={Uri.EscapeDataString(pid)}&pn={pn}&rn=100&encode=utf8&keyset=pl2012&identity=kuwo&pcmp4=1&vipver=MUSIC_9.0.5.0_W1&newver=1";
                string json;
                try
                {
                    json = await _kuwoAppHttpClient.GetStringAsync(url, ct);
                }
                catch
                {
                    if (pn == 0) return null;
                    break;
                }

                if (string.IsNullOrWhiteSpace(json))
                {
                    if (pn == 0) return null;
                    break;
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (detail == null)
                {
                    var cover = root.GetStringProp("pic");
                    if (!string.IsNullOrWhiteSpace(cover))
                        cover = cover.Replace("_150.jpg", "_500.jpg", StringComparison.OrdinalIgnoreCase);
                    if (string.IsNullOrWhiteSpace(cover))
                        cover = summary.CoverImgUrl;

                    var rawDesc = GlobalHtmlDecode(root.GetStringProp("info"))
                        .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
                        .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
                        .Trim();

                    detail = new PlaylistItem
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
                }

                int pageRawCount = 0;
                int newlyAdded = 0;
                if (root.TryGetProperty("musiclist", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        pageRawCount++;
                        var rid = item.GetStringProp("id");
                        var name = GlobalHtmlDecode(item.GetStringProp("name"));
                        if (string.IsNullOrWhiteSpace(rid) || string.IsNullOrWhiteSpace(name)) continue;

                        var songId = $"kw_{rid}";
                        if (!seenIds.Add(songId)) continue;

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
                            Id = songId,
                            Name = name,
                            Artists = SplitArtists(artistRaw),
                            Album = new AlbumItem { Name = albumName, PicUrl = detail.CoverImgUrl },
                            PicUrl = detail.CoverImgUrl,
                            DurationMs = durationSec * 1000L,
                            Source = "kuwo",
                            KuwoRid = rid,
                            NativeQualityHint = qualityHint,
                            DisplayIndex = detail.Tracks.Count + 1
                        });
                        newlyAdded++;
                    }
                }

                if (pageRawCount < 100 || newlyAdded == 0)
                    break;
            }

            if (detail != null)
            {
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

    private static bool TryFindMiguSongArray(JsonElement root, out JsonElement songArray)
    {
        string[] arrayKeys = ["songList", "contents", "list", "items", "collectionList", "songs"];
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in arrayKeys)
            {
                if (data.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    songArray = arr;
                    return true;
                }
            }
        }
        foreach (var key in arrayKeys)
        {
            if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                songArray = arr;
                return true;
            }
        }
        songArray = default;
        return false;
    }

    private static (int RawCount, int AddedCount) ExtractMiguSongsFromJson(
        string json,
        List<SongItem> tracks,
        HashSet<string> seenIds,
        string fallbackCover)
    {
        using var doc = JsonDocument.Parse(json);
        if (!TryFindMiguSongArray(doc.RootElement, out var arr))
        {
            return (0, 0);
        }

        int rawCount = 0;
        int addedCount = 0;
        foreach (var item in arr.EnumerateArray())
        {
            rawCount++;
            SongItem? song = ParseMiguSongElement(item);
            if (song == null && item.ValueKind == JsonValueKind.Object)
            {
                foreach (var nestedKey in new[] { "content", "song", "songInfo", "resourceInfo" })
                {
                    if (item.TryGetProperty(nestedKey, out var nested) && nested.ValueKind == JsonValueKind.Object)
                    {
                        song = ParseMiguSongElement(nested);
                        if (song != null) break;
                    }
                }
            }
            if (song == null || !seenIds.Add(song.Id)) continue;

            if (string.IsNullOrWhiteSpace(song.PicUrl) && !string.IsNullOrWhiteSpace(fallbackCover))
            {
                song.PicUrl = fallbackCover;
                if (song.Album != null) song.Album.PicUrl = fallbackCover;
            }
            song.DisplayIndex = tracks.Count + 1;
            tracks.Add(song);
            addedCount++;
        }

        return (rawCount, addedCount);
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

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.Equals(pid, "__liked__", StringComparison.Ordinal))
            {
                bool useH5Fallback = false;
                for (int pageNo = 1; pageNo <= 3; pageNo++)
                {
                    var url = useH5Fallback
                        ? $"https://app.c.nf.migu.cn/user/h5/user/collection/v1.0?pageNo={pageNo}&type=1&OPType=03&pageSize=100&resourceType=2"
                        : $"https://app.c.nf.migu.cn/pc/v1.0/user/collections.do?pageNo={pageNo}&pageSize=100&type=1";

                    var songsJson = await SendMiguGetAsync(url, ct);
                    var (rawCount, addedCount) = string.IsNullOrWhiteSpace(songsJson)
                        ? (0, 0)
                        : ExtractMiguSongsFromJson(songsJson, detail.Tracks, seenIds, detail.CoverImgUrl);

                    if (pageNo == 1 && addedCount == 0 && !useH5Fallback)
                    {
                        useH5Fallback = true;
                        var fbJson = await SendMiguGetAsync(
                            "https://app.c.nf.migu.cn/user/h5/user/collection/v1.0?pageNo=1&type=1&OPType=03&pageSize=100&resourceType=2", ct);
                        if (!string.IsNullOrWhiteSpace(fbJson))
                        {
                            (rawCount, addedCount) = ExtractMiguSongsFromJson(fbJson, detail.Tracks, seenIds, detail.CoverImgUrl);
                        }
                    }

                    if (rawCount < 100 || addedCount == 0) break;
                }

                if (!ImageUriHelper.HasRealImageUrl(detail.CoverImgUrl) &&
                    detail.Tracks.FirstOrDefault(t => ImageUriHelper.HasRealImageUrl(t.PicUrl)) is { } firstWithCover)
                {
                    detail.CoverImgUrl = firstWithCover.PicUrl;
                }
                detail.TrackCount = detail.Tracks.Count > 0 ? detail.Tracks.Count : summary.TrackCount;
                return detail;
            }

            var infoTask = SendMiguGetAsync($"https://app.c.nf.migu.cn/MIGUM2.0/v1.0/content/resourceinfo.do?needSimple=01&resourceId={Uri.EscapeDataString(pid)}&resourceType=2021", ct);
            var firstSongsTask = SendMiguGetAsync($"https://app.c.nf.migu.cn/MIGUM3.0/resource/playlist/song/v2.0?pageNo=1&pageSize=80&playlistId={Uri.EscapeDataString(pid)}", ct);
            await Task.WhenAll(infoTask, firstSongsTask);

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

            var page1Json = await firstSongsTask;
            var (p1Raw, p1Added) = string.IsNullOrWhiteSpace(page1Json)
                ? (0, 0)
                : ExtractMiguSongsFromJson(page1Json, detail.Tracks, seenIds, detail.CoverImgUrl);

            if (p1Raw >= 80 && p1Added > 0)
            {
                for (int pageNo = 2; pageNo <= 3; pageNo++)
                {
                    var nextJson = await SendMiguGetAsync(
                        $"https://app.c.nf.migu.cn/MIGUM3.0/resource/playlist/song/v2.0?pageNo={pageNo}&pageSize=80&playlistId={Uri.EscapeDataString(pid)}", ct);
                    if (string.IsNullOrWhiteSpace(nextJson)) break;
                    var (rawCount, addedCount) = ExtractMiguSongsFromJson(nextJson, detail.Tracks, seenIds, detail.CoverImgUrl);
                    if (rawCount < 80 || addedCount == 0) break;
                }
            }

            // 个人自建或私密歌单若公开接口无曲目，走 H5 个人歌单曲目接口兜底
            if (detail.Tracks.Count == 0 && _platformAuth.HasValidSession(AuthMusicPlatform.Migu))
            {
                for (int pageNo = 1; pageNo <= 3; pageNo++)
                {
                    var myJson = await SendMiguGetAsync(
                        $"https://app.c.nf.migu.cn/resource/h5/my/playlist/song/v1.0?pageNo={pageNo}&pageSize=100&playlistId={Uri.EscapeDataString(pid)}", ct);
                    if (string.IsNullOrWhiteSpace(myJson)) break;
                    var (rawCount, addedCount) = ExtractMiguSongsFromJson(myJson, detail.Tracks, seenIds, detail.CoverImgUrl);
                    if (rawCount < 100 || addedCount == 0) break;
                }
            }

            detail.TrackCount = detail.Tracks.Count;
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
            bool authed = _platformAuth.HasValidSession(AuthMusicPlatform.Migu) &&
                          await _platformAuth.ApplyAuthHeadersAsync(AuthMusicPlatform.Migu, req, ct);
            if (!req.Headers.Contains("channel"))
            {
                req.Headers.TryAddWithoutValidation("channel", "0146951");
            }
            req.Headers.TryAddWithoutValidation("Origin", "https://music.migu.cn");
            req.Headers.TryAddWithoutValidation("Referer", "https://music.migu.cn/");
            using var resp = await (authed ? _authedHttpClient : _httpClient).SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
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

    // =========================================================================
    // 已登录账号资料与个人歌单库拉取（10 平台统一鉴权扩展）
    // =========================================================================

    /// <summary>
    /// 同步各第三方平台已登录账号的昵称、自建歌单与收藏歌单
    /// </summary>
    public async Task<(string Nickname, List<PlaylistItem> Created, List<PlaylistItem> Collected)> GetUserAccountLibraryAsync(
        AuthMusicPlatform platform,
        CancellationToken ct = default)
    {
        if (!_platformAuth.HasValidSession(platform))
        {
            return (string.Empty, [], []);
        }

        var existingName = _platformAuth.TryGetSession(platform)?.Nickname ?? string.Empty;
        try
        {
            return platform switch
            {
                AuthMusicPlatform.Spotify => await GetSpotifyUserLibraryAsync(existingName, ct),
                AuthMusicPlatform.AppleMusic => await GetAppleMusicUserLibraryAsync(existingName, ct),
                AuthMusicPlatform.Deezer => await GetDeezerUserLibraryAsync(existingName, ct),
                AuthMusicPlatform.Tidal => await GetTidalUserLibraryAsync(existingName, ct),
                AuthMusicPlatform.YouTubeMusic => await GetYouTubeMusicUserLibraryAsync(existingName, ct),
                AuthMusicPlatform.KuGou => await GetKuGouUserLibraryAsync(existingName, ct),
                AuthMusicPlatform.KuWo => await GetKuWoUserLibraryAsync(existingName, ct),
                AuthMusicPlatform.Qishui => await GetQishuiUserLibraryAsync(existingName, ct),
                AuthMusicPlatform.Migu => await GetMiguUserLibraryAsync(existingName, ct),
                _ => (existingName, [], [])
            };
        }
        catch
        {
            return (existingName, [], []);
        }
    }

    private async Task<string?> EnsureAuthedSpotifyBearerAsync(CancellationToken ct)
    {
        var session = _platformAuth.TryGetSession(AuthMusicPlatform.Spotify);
        if (session == null) return null;

        // 优先使用 OAuth PKCE 或 WebViewCookie 中缓存且未过期的 AccessToken
        if (!string.IsNullOrWhiteSpace(session.AccessToken))
        {
            if (session.FlowType == AuthFlowType.OAuth2Pkce)
            {
                using var probeReq = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me");
                if (await _platformAuth.ApplyAuthHeadersAsync(AuthMusicPlatform.Spotify, probeReq, ct) &&
                    probeReq.Headers.Authorization?.Parameter is { Length: > 0 } refreshedBearer)
                {
                    return refreshedBearer;
                }
            }
            else if (!session.ExpiresAt.HasValue || session.ExpiresAt.Value > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return session.AccessToken;
            }
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!string.IsNullOrWhiteSpace(_authedSpotifyBearer) && _authedSpotifyBearerExpireMs > now + 60_000)
        {
            return _authedSpotifyBearer;
        }

        // 无 Key 浏览器 Cookie 模式：优先携带 Cookie 拉取 open.spotify.com 首页 session JSON
        if (!string.IsNullOrWhiteSpace(session.CookieHeader))
        {
            try
            {
                using var pageReq = new HttpRequestMessage(HttpMethod.Get, "https://open.spotify.com/");
                pageReq.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
                pageReq.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                using var pageResp = await _authedHttpClient.SendAsync(pageReq, ct);
                if (pageResp.IsSuccessStatusCode)
                {
                    var html = await pageResp.Content.ReadAsStringAsync(ct);
                    if (TryExtractSpotifyWebSessionToken(html, now, out var webToken, out var webExpMs))
                    {
                        _authedSpotifyBearer = webToken;
                        _authedSpotifyBearerExpireMs = webExpMs;
                        return _authedSpotifyBearer;
                    }
                }
            }
            catch
            {
                // 忽略首页 HTML 提取失败，继续走 get_access_token
            }

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://open.spotify.com/get_access_token?reason=transport&productType=web-player");
                req.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                using var resp = await _authedHttpClient.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = global::System.Text.Json.JsonDocument.Parse(json);
                    var token = doc.RootElement.GetStringProp("accessToken");
                    var expMs = doc.RootElement.GetLongProp("accessTokenExpirationTimestampMs");
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        _authedSpotifyBearer = token;
                        _authedSpotifyBearerExpireMs = expMs > now ? expMs : now + 1800_000L;
                        return _authedSpotifyBearer;
                    }
                }
            }
            catch
            {
                // 忽略换取失败
            }
        }

        return null;
    }

    private static bool TryExtractSpotifyWebSessionToken(
        string html,
        long nowMs,
        out string token,
        out long expireMs)
    {
        token = string.Empty;
        expireMs = nowMs + 1800_000L;
        if (string.IsNullOrWhiteSpace(html)) return false;

        foreach (var pattern in new[]
                 {
                     @"<script[^>]*id\s*=\s*[""']session[""'][^>]*>([\s\S]*?)</script>",
                     @"<script[^>]*id\s*=\s*[""']__NEXT_DATA__[""'][^>]*>([\s\S]*?)</script>"
                 })
        {
            var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var rawJson = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(rawJson)) continue;

            try
            {
                using var doc = global::System.Text.Json.JsonDocument.Parse(rawJson);
                if (TryFindJsonAccessToken(doc.RootElement, out var foundToken, out var foundExp))
                {
                    token = foundToken;
                    if (foundExp > nowMs) expireMs = foundExp;
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    private static bool TryFindJsonAccessToken(
        global::System.Text.Json.JsonElement el,
        out string token,
        out long expireMs)
    {
        token = string.Empty;
        expireMs = 0;

        if (el.ValueKind == global::System.Text.Json.JsonValueKind.Object)
        {
            var direct = el.GetStringProp("accessToken");
            if (!string.IsNullOrWhiteSpace(direct))
            {
                token = direct;
                expireMs = el.GetLongProp("accessTokenExpirationTimestampMs");
                return true;
            }

            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Value.ValueKind is global::System.Text.Json.JsonValueKind.Object or global::System.Text.Json.JsonValueKind.Array &&
                    TryFindJsonAccessToken(prop.Value, out token, out expireMs))
                {
                    return true;
                }
            }
        }
        else if (el.ValueKind == global::System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind is global::System.Text.Json.JsonValueKind.Object or global::System.Text.Json.JsonValueKind.Array &&
                    TryFindJsonAccessToken(item, out token, out expireMs))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<(string Nickname, List<PlaylistItem> Created, List<PlaylistItem> Collected)> GetSpotifyUserLibraryAsync(
        string fallbackName, CancellationToken ct)
    {
        var created = new List<PlaylistItem>();
        var collected = new List<PlaylistItem>();
        var bearer = await EnsureAuthedSpotifyBearerAsync(ct);
        if (string.IsNullOrWhiteSpace(bearer))
        {
            return (fallbackName, created, collected);
        }

        string userId = string.Empty;
        string nickname = fallbackName;
        using (var meReq = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me"))
        {
            meReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
            using var meResp = await _authedHttpClient.SendAsync(meReq, ct);
            if (meResp.IsSuccessStatusCode)
            {
                using var meDoc = global::System.Text.Json.JsonDocument.Parse(await meResp.Content.ReadAsStringAsync(ct));
                userId = meDoc.RootElement.GetStringProp("id");
                nickname = FirstNonEmpty(meDoc.RootElement.GetStringProp("display_name"), userId, fallbackName);
            }
        }

        // 添加“我喜欢的音乐 (Spotify)”快捷入口
        try
        {
            using var likedReq = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/tracks?limit=1");
            likedReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
            using var likedResp = await _authedHttpClient.SendAsync(likedReq, ct);
            if (likedResp.IsSuccessStatusCode)
            {
                using var likedDoc = global::System.Text.Json.JsonDocument.Parse(await likedResp.Content.ReadAsStringAsync(ct));
                var total = likedDoc.RootElement.GetIntProp("total");
                created.Add(new PlaylistItem
                {
                    Source = "spotify",
                    ThirdPartyId = "__liked__",
                    Name = "我喜欢的音乐 (Spotify)",
                    CreatorName = nickname,
                    TrackCount = total,
                    IsSystemDefaultPlaylist = true
                });
            }
        }
        catch
        {
            // 忽略 Liked Tracks 权限缺失
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? nextUrl = "https://api.spotify.com/v1/me/playlists?limit=50";
        for (int page = 0; page < 4 && !string.IsNullOrWhiteSpace(nextUrl); page++)
        {
            using var plReq = new HttpRequestMessage(HttpMethod.Get, nextUrl);
            plReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
            using var plResp = await _authedHttpClient.SendAsync(plReq, ct);
            if (!plResp.IsSuccessStatusCode) break;

            using var plDoc = global::System.Text.Json.JsonDocument.Parse(await plResp.Content.ReadAsStringAsync(ct));
            if (plDoc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == global::System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != global::System.Text.Json.JsonValueKind.Object) continue;
                    var pid = item.GetStringProp("id");
                    if (string.IsNullOrWhiteSpace(pid) || !seenIds.Add(pid)) continue;
                    var ownerId = item.TryGetProperty("owner", out var ow) ? ow.GetStringProp("id") : string.Empty;
                    var ownerName = item.TryGetProperty("owner", out var ow2) ? ow2.GetStringProp("display_name") : string.Empty;
                    var cover = string.Empty;
                    if (item.TryGetProperty("images", out var imgs) && imgs.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var img in imgs.EnumerateArray())
                        {
                            cover = img.GetStringProp("url");
                            if (!string.IsNullOrWhiteSpace(cover)) break;
                        }
                    }

                    var trackTotal = item.TryGetProperty("tracks", out var tr) ? tr.GetIntProp("total") : 0;
                    var pl = new PlaylistItem
                    {
                        Source = "spotify",
                        ThirdPartyId = pid,
                        Name = item.GetStringProp("name", "Spotify 歌单"),
                        Description = item.GetStringProp("description"),
                        CoverImgUrl = cover,
                        CreatorName = FirstNonEmpty(ownerName, ownerId),
                        TrackCount = trackTotal
                    };

                    if (!string.IsNullOrWhiteSpace(userId) && string.Equals(ownerId, userId, StringComparison.OrdinalIgnoreCase))
                    {
                        created.Add(pl);
                    }
                    else
                    {
                        collected.Add(pl);
                    }
                }
            }

            nextUrl = plDoc.RootElement.GetStringProp("next");
        }

        return (nickname, created, collected);
    }

    private async Task<PlaylistItem?> GetSpotifyPlaylistDetailWithAuthAsync(PlaylistItem summary, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(summary.ThirdPartyId) && _platformAuth.HasValidSession(AuthMusicPlatform.Spotify))
        {
            var bearer = await EnsureAuthedSpotifyBearerAsync(ct);
            if (!string.IsNullOrWhiteSpace(bearer))
            {
                try
                {
                    var isLiked = string.Equals(summary.ThirdPartyId, "__liked__", StringComparison.OrdinalIgnoreCase);
                    string? nextUrl = isLiked
                        ? "https://api.spotify.com/v1/me/tracks?limit=50"
                        : $"https://api.spotify.com/v1/playlists/{Uri.EscapeDataString(summary.ThirdPartyId)}/tracks?limit=100";

                    var tracks = new List<SongItem>();
                    bool anyPageOk = false;

                    for (int page = 0; page < 4 && !string.IsNullOrWhiteSpace(nextUrl) && tracks.Count < 200; page++)
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, nextUrl);
                        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
                        using var resp = await _authedHttpClient.SendAsync(req, ct);
                        if (!resp.IsSuccessStatusCode) break;

                        anyPageOk = true;
                        using var doc = global::System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                        if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var entry in items.EnumerateArray())
                            {
                                var trackObj = entry.TryGetProperty("track", out var innerTrack) && innerTrack.ValueKind == global::System.Text.Json.JsonValueKind.Object
                                    ? innerTrack
                                    : entry;
                                var tid = trackObj.GetStringProp("id");
                                var name = trackObj.GetStringProp("name");
                                if (string.IsNullOrWhiteSpace(name)) continue;

                                var artists = new List<ArtistItem>();
                                if (trackObj.TryGetProperty("artists", out var aArr) && aArr.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                                {
                                    foreach (var a in aArr.EnumerateArray())
                                    {
                                        var an = a.GetStringProp("name");
                                        if (!string.IsNullOrWhiteSpace(an)) artists.Add(new ArtistItem { Name = an });
                                    }
                                }
                                if (artists.Count == 0) artists.Add(new ArtistItem { Name = "Spotify Artist" });

                                string albumName = string.Empty, albumCover = summary.CoverImgUrl;
                                if (trackObj.TryGetProperty("album", out var alb) && alb.ValueKind == global::System.Text.Json.JsonValueKind.Object)
                                {
                                    albumName = alb.GetStringProp("name");
                                    if (alb.TryGetProperty("images", out var aImgs) && aImgs.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                                    {
                                        foreach (var ai in aImgs.EnumerateArray())
                                        {
                                            var u = ai.GetStringProp("url");
                                            if (!string.IsNullOrWhiteSpace(u)) { albumCover = u; break; }
                                        }
                                    }
                                }

                                tracks.Add(new SongItem
                                {
                                    Id = !string.IsNullOrWhiteSpace(tid) ? $"spotify_{tid}" : $"spotify_{Guid.NewGuid():N}",
                                    Name = name,
                                    Artists = artists,
                                    Album = new AlbumItem { Name = albumName, PicUrl = albumCover },
                                    DurationMs = trackObj.GetLongProp("duration_ms"),
                                    Source = "spotify",
                                    PicUrl = albumCover,
                                    NativeTrackId = tid,
                                    NativePlayUrl = trackObj.GetStringProp("preview_url"),
                                    DisplayIndex = tracks.Count + 1
                                });

                                if (tracks.Count >= 200) break;
                            }
                        }

                        nextUrl = doc.RootElement.GetStringProp("next");
                    }

                    if (anyPageOk && (tracks.Count > 0 || isLiked))
                    {
                        return new PlaylistItem
                        {
                            Source = "spotify",
                            ThirdPartyId = summary.ThirdPartyId,
                            Name = summary.Name,
                            Description = summary.Description,
                            CoverImgUrl = FirstNonEmpty(summary.CoverImgUrl, tracks.FirstOrDefault()?.PicUrl),
                            CreatorName = summary.CreatorName,
                            TrackCount = tracks.Count,
                            PlayCount = summary.PlayCount,
                            Tracks = tracks
                        };
                    }
                }
                catch
                {
                    // 回退至公开 Embed 解析
                }
            }
        }

        return await GetSpotifyEmbedPlaylistAsync(summary.ThirdPartyId, ct);
    }

    private static bool IsAppleEs256KidJwt(string jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return false;
        var parts = jwt.Split('.');
        if (parts.Length != 3) return false;
        try
        {
            var headerB64 = parts[0].Replace('-', '+').Replace('_', '/');
            switch (headerB64.Length % 4)
            {
                case 2: headerB64 += "=="; break;
                case 3: headerB64 += "="; break;
            }
            var headerJson = Encoding.UTF8.GetString(Convert.FromBase64String(headerB64));
            return headerJson.Contains("ES256", StringComparison.Ordinal) &&
                   headerJson.Contains("kid", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private async Task<HttpResponseMessage> SendAppleMusicLibraryGetAsync(string url, string devToken, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        await _platformAuth.ApplyAuthHeadersAsync(AuthMusicPlatform.AppleMusic, req, ct);
        if (!req.Headers.Contains("Authorization"))
        {
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {devToken}");
        }
        req.Headers.TryAddWithoutValidation("Origin", "https://music.apple.com");
        req.Headers.TryAddWithoutValidation("Referer", "https://music.apple.com/");
        return await _authedHttpClient.SendAsync(req, ct);
    }

    private async Task<string?> EnsureAppleDeveloperTokenForLibraryAsync(CancellationToken ct)
    {
        using var probeReq = new HttpRequestMessage(HttpMethod.Get, "https://api.music.apple.com/v1/me/library/playlists");
        if (await _platformAuth.ApplyAuthHeadersAsync(AuthMusicPlatform.AppleMusic, probeReq, ct) &&
            probeReq.Headers.Authorization?.Parameter is { Length: > 0 } configuredDevJwt)
        {
            return configuredDevJwt;
        }

        if (!string.IsNullOrWhiteSpace(_applePublicDevToken) && _applePublicDevTokenExpireUtc > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return _applePublicDevToken;
        }

        // 无 .p8 凭证时，从 Apple Music Web 页面公开脚本提取 ES256 JWT（兼容 header 字段顺序差异）
        try
        {
            var scannedScripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pageUrl in new[] { "https://music.apple.com/us/browse", "https://music.apple.com/" })
            {
                string html;
                try
                {
                    html = await _httpClient.GetStringAsync(pageUrl, ct);
                }
                catch
                {
                    continue;
                }

                var scriptMatches = Regex.Matches(html, @"<script[^>]+src\s*=\s*[""']([^""']*/assets/[^""']+\.js[^""']*)[""']", RegexOptions.IgnoreCase);
                foreach (Match sm in scriptMatches)
                {
                    var src = sm.Groups[1].Value;
                    if (string.IsNullOrWhiteSpace(src)) continue;
                    var fullJsUrl = src.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? src
                        : "https://music.apple.com" + (src.StartsWith('/') ? src : "/" + src);
                    if (!scannedScripts.Add(fullJsUrl) || scannedScripts.Count > 12) continue;

                    try
                    {
                        var jsText = await _httpClient.GetStringAsync(fullJsUrl, ct);
                        var jwtMatches = Regex.Matches(jsText, @"eyJ[A-Za-z0-9_-]{20,}\.eyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{40,}");
                        foreach (Match jm in jwtMatches)
                        {
                            if (IsAppleEs256KidJwt(jm.Value))
                            {
                                _applePublicDevToken = jm.Value;
                                _applePublicDevTokenExpireUtc = DateTimeOffset.UtcNow.AddHours(6);
                                return _applePublicDevToken;
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
            // 忽略网络提取失败
        }

        return null;
    }

    private async Task<(string Nickname, List<PlaylistItem> Created, List<PlaylistItem> Collected)> GetAppleMusicUserLibraryAsync(
        string fallbackName, CancellationToken ct)
    {
        var created = new List<PlaylistItem>();
        var collected = new List<PlaylistItem>();
        var devToken = await EnsureAppleDeveloperTokenForLibraryAsync(ct);
        if (string.IsNullOrWhiteSpace(devToken))
        {
            return (fallbackName, created, collected);
        }

        // 探测用户音乐资料库歌曲，生成“资料库歌曲 (Apple Music)”快捷入口
        try
        {
            using var libSongsResp = await SendAppleMusicLibraryGetAsync("https://api.music.apple.com/v1/me/library/songs?limit=1", devToken, ct);
            if (libSongsResp.IsSuccessStatusCode)
            {
                using var songsDoc = global::System.Text.Json.JsonDocument.Parse(await libSongsResp.Content.ReadAsStringAsync(ct));
                int dataCount = songsDoc.RootElement.TryGetProperty("data", out var sArr) && sArr.ValueKind == global::System.Text.Json.JsonValueKind.Array
                    ? sArr.GetArrayLength()
                    : 0;
                int metaTotal = songsDoc.RootElement.TryGetProperty("meta", out var metaEl) ? metaEl.GetIntProp("total") : 0;
                int trackTotal = Math.Max(metaTotal, dataCount);
                if (trackTotal > 0)
                {
                    created.Add(new PlaylistItem
                    {
                        Source = "apple",
                        ThirdPartyId = "__lib_songs__",
                        Name = "资料库歌曲 (Apple Music)",
                        CreatorName = fallbackName,
                        TrackCount = trackTotal,
                        IsSystemDefaultPlaylist = true
                    });
                }
            }
        }
        catch
        {
        }

        string? nextUrl = "https://api.music.apple.com/v1/me/library/playlists?limit=100";
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int page = 0; page < 3 && !string.IsNullOrWhiteSpace(nextUrl); page++)
        {
            using var resp = await SendAppleMusicLibraryGetAsync(nextUrl, devToken, ct);
            if (!resp.IsSuccessStatusCode) break;

            using var doc = global::System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == global::System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var pid = item.GetStringProp("id");
                    if (string.IsNullOrWhiteSpace(pid) || !seenIds.Add(pid)) continue;
                    var attrs = item.TryGetProperty("attributes", out var a) ? a : default;
                    var name = attrs.ValueKind == global::System.Text.Json.JsonValueKind.Object
                        ? attrs.GetStringProp("name", "Apple Music 歌单")
                        : "Apple Music 歌单";
                    var cover = string.Empty;
                    if (attrs.ValueKind == global::System.Text.Json.JsonValueKind.Object &&
                        attrs.TryGetProperty("artwork", out var art) &&
                        art.ValueKind == global::System.Text.Json.JsonValueKind.Object)
                    {
                        cover = art.GetStringProp("url").Replace("{w}", "300").Replace("{h}", "300");
                    }

                    bool canEdit = attrs.ValueKind != global::System.Text.Json.JsonValueKind.Object ||
                                   !attrs.TryGetProperty("canEdit", out var ceEl) ||
                                   ceEl.ValueKind != global::System.Text.Json.JsonValueKind.False;

                    var pl = new PlaylistItem
                    {
                        Source = "apple",
                        ThirdPartyId = pid,
                        Name = name,
                        CoverImgUrl = cover,
                        CreatorName = fallbackName
                    };

                    if (canEdit) created.Add(pl);
                    else collected.Add(pl);
                }
            }

            var nextRel = doc.RootElement.GetStringProp("next");
            nextUrl = string.IsNullOrWhiteSpace(nextRel)
                ? null
                : (nextRel.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? nextRel
                    : "https://api.music.apple.com" + (nextRel.StartsWith('/') ? nextRel : "/" + nextRel));
        }

        return (fallbackName, created, collected);
    }

    private async Task<PlaylistItem?> GetAppleLibraryPlaylistDetailAsync(PlaylistItem summary, CancellationToken ct)
    {
        var devToken = await EnsureAppleDeveloperTokenForLibraryAsync(ct);
        if (string.IsNullOrWhiteSpace(devToken) || string.IsNullOrWhiteSpace(summary.ThirdPartyId))
        {
            return null;
        }

        bool isLibSongs = string.Equals(summary.ThirdPartyId, "__lib_songs__", StringComparison.Ordinal);
        string? nextUrl = isLibSongs
            ? "https://api.music.apple.com/v1/me/library/songs?limit=100"
            : $"https://api.music.apple.com/v1/me/library/playlists/{Uri.EscapeDataString(summary.ThirdPartyId)}/tracks?limit=100";

        var tracks = new List<SongItem>();
        for (int page = 0; page < 2 && !string.IsNullOrWhiteSpace(nextUrl); page++)
        {
            using var resp = await SendAppleMusicLibraryGetAsync(nextUrl, devToken, ct);
            if (!resp.IsSuccessStatusCode)
            {
                if (page == 0) return null;
                break;
            }

            using var doc = global::System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == global::System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (!item.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != global::System.Text.Json.JsonValueKind.Object)
                        continue;

                    var tid = item.GetStringProp("id");
                    var name = attrs.GetStringProp("name");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var artist = attrs.GetStringProp("artistName", "Apple Artist");
                    var album = attrs.GetStringProp("albumName");
                    var cover = summary.CoverImgUrl;
                    if (attrs.TryGetProperty("artwork", out var art) && art.ValueKind == global::System.Text.Json.JsonValueKind.Object)
                    {
                        var u = art.GetStringProp("url").Replace("{w}", "300").Replace("{h}", "300");
                        if (!string.IsNullOrWhiteSpace(u)) cover = u;
                    }

                    var catalogId = attrs.TryGetProperty("playParams", out var pp) ? pp.GetStringProp("catalogId") : string.Empty;
                    tracks.Add(new SongItem
                    {
                        Id = !string.IsNullOrWhiteSpace(catalogId) ? $"apple_{catalogId}" : $"apple_lib_{tid}",
                        Name = name,
                        Artists = SplitArtists(artist),
                        Album = new AlbumItem { Name = album, PicUrl = cover },
                        DurationMs = attrs.GetLongProp("durationInMillis"),
                        Source = "apple",
                        PicUrl = cover,
                        NativeTrackId = FirstNonEmpty(catalogId, tid),
                        DisplayIndex = tracks.Count + 1
                    });
                }
            }

            var nextRel = doc.RootElement.GetStringProp("next");
            nextUrl = string.IsNullOrWhiteSpace(nextRel)
                ? null
                : (nextRel.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? nextRel
                    : "https://api.music.apple.com" + (nextRel.StartsWith('/') ? nextRel : "/" + nextRel));
        }

        return new PlaylistItem
        {
            Source = "apple",
            ThirdPartyId = summary.ThirdPartyId,
            Name = summary.Name,
            Description = summary.Description,
            CoverImgUrl = FirstNonEmpty(summary.CoverImgUrl, tracks.FirstOrDefault()?.PicUrl),
            CreatorName = summary.CreatorName,
            TrackCount = tracks.Count,
            Tracks = tracks
        };
    }

    private async Task<(string Nickname, List<PlaylistItem> Created, List<PlaylistItem> Collected)> GetDeezerUserLibraryAsync(
        string fallbackName, CancellationToken ct)
    {
        var created = new List<PlaylistItem>();
        var collected = new List<PlaylistItem>();

        var gw = await GetDeezerGwSessionAsync(ct);
        if (gw == null) return (fallbackName, created, collected);

        var userId = gw.Value.UserId;
        var nickname = FirstNonEmpty(gw.Value.Nickname, fallbackName, $"Deezer #{userId}");
        var apiToken = gw.Value.ApiToken;
        var lovedId = gw.Value.LovedTracksId;
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 先通过认证后的 GW pageProfile 读取全部自建/私有/收藏歌单，再合并公开 API 数据
        var gwTask = !string.IsNullOrWhiteSpace(apiToken)
            ? PostDeezerGwAsync("deezer.pageProfile", apiToken,
                global::System.Text.Json.JsonSerializer.Serialize(new { user_id = userId, tab = "playlists", nb = 100 }), ct)
            : Task.FromResult<string?>(null);
        var pubTask = _httpClient.GetStringAsync($"https://api.deezer.com/user/{userId}/playlists?limit=100", ct);

        await Task.WhenAll(gwTask.ContinueWith(_ => { }, TaskScheduler.Default), pubTask.ContinueWith(_ => { }, TaskScheduler.Default));

        try
        {
            var gwJson = gwTask.IsCompletedSuccessfully ? gwTask.Result : null;
            if (!string.IsNullOrWhiteSpace(gwJson))
            {
                using var gwDoc = global::System.Text.Json.JsonDocument.Parse(gwJson);
                if (gwDoc.RootElement.TryGetProperty("results", out var res) &&
                    res.TryGetProperty("TAB", out var tab) &&
                    tab.TryGetProperty("playlists", out var pls) &&
                    pls.TryGetProperty("data", out var gwArr) &&
                    gwArr.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in gwArr.EnumerateArray())
                    {
                        var pidStr = ReadDeezerFlexString(item, "PLAYLIST_ID", "id");
                        if (string.IsNullOrWhiteSpace(pidStr) || !seenIds.Add(pidStr)) continue;

                        long.TryParse(pidStr, out long pidNum);
                        var parentUid = ReadDeezerFlexLong(item, "PARENT_USER_ID");
                        var pic = item.GetStringProp("PLAYLIST_PICTURE");
                        var picType = FirstNonEmpty(item.GetStringProp("PICTURE_TYPE"), "playlist");
                        var cover = string.IsNullOrWhiteSpace(pic)
                            ? string.Empty
                            : $"https://e-cdns-images.dzcdn.net/images/{picType}/{pic}/500x500-000000-80-0-0.jpg";
                        bool isLoved = (!string.IsNullOrWhiteSpace(lovedId) && pidStr == lovedId) ||
                                       item.GetIntProp("TYPE") == 4;

                        var pl = new PlaylistItem
                        {
                            Id = pidNum,
                            Source = "deezer",
                            ThirdPartyId = pidStr,
                            Name = isLoved ? "我喜欢的音乐 (Deezer)" : GlobalHtmlDecode(item.GetStringProp("TITLE", "Deezer 歌单")),
                            CoverImgUrl = cover,
                            TrackCount = item.GetIntProp("NB_SONG"),
                            CreatorName = GlobalHtmlDecode(FirstNonEmpty(item.GetStringProp("PARENT_USERNAME"), nickname)),
                            IsSystemDefaultPlaylist = isLoved
                        };

                        if (isLoved) created.Insert(0, pl);
                        else if (parentUid == userId || parentUid <= 0) created.Add(pl);
                        else collected.Add(pl);
                    }
                }
            }
        }
        catch
        {
        }

        try
        {
            var plJson = pubTask.IsCompletedSuccessfully ? pubTask.Result : null;
            if (!string.IsNullOrWhiteSpace(plJson))
            {
                using var plDoc = global::System.Text.Json.JsonDocument.Parse(plJson);
                if (plDoc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        var pid = item.GetLongProp("id");
                        if (pid <= 0) continue;
                        var pidStr = pid.ToString();
                        if (!seenIds.Add(pidStr)) continue;

                        var creatorId = item.TryGetProperty("creator", out var cr) ? cr.GetLongProp("id") : 0;
                        var creatorName = item.TryGetProperty("creator", out var cr2) ? cr2.GetStringProp("name") : string.Empty;
                        bool isLoved = (!string.IsNullOrWhiteSpace(lovedId) && pidStr == lovedId) ||
                                       (item.TryGetProperty("is_loved_track", out var ilt) && ilt.ValueKind == global::System.Text.Json.JsonValueKind.True);

                        var pl = new PlaylistItem
                        {
                            Id = pid,
                            Source = "deezer",
                            ThirdPartyId = pidStr,
                            Name = isLoved ? "我喜欢的音乐 (Deezer)" : GlobalHtmlDecode(item.GetStringProp("title", "Deezer 歌单")),
                            CoverImgUrl = FirstNonEmpty(item.GetStringProp("picture_medium"), item.GetStringProp("picture")),
                            TrackCount = item.GetIntProp("nb_tracks"),
                            CreatorName = GlobalHtmlDecode(creatorName),
                            IsSystemDefaultPlaylist = isLoved
                        };

                        if (isLoved) created.Insert(0, pl);
                        else if (creatorId == userId) created.Add(pl);
                        else collected.Add(pl);
                    }
                }
            }
        }
        catch
        {
        }

        if (!string.IsNullOrWhiteSpace(lovedId) && seenIds.Add(lovedId))
        {
            long.TryParse(lovedId, out long lovedNum);
            created.Insert(0, new PlaylistItem
            {
                Id = lovedNum,
                Source = "deezer",
                ThirdPartyId = lovedId,
                Name = "我喜欢的音乐 (Deezer)",
                CreatorName = nickname,
                IsSystemDefaultPlaylist = true
            });
        }

        return (nickname, created, collected);
    }

    private async Task<(string Nickname, List<PlaylistItem> Created, List<PlaylistItem> Collected)> GetTidalUserLibraryAsync(
        string fallbackName, CancellationToken ct)
    {
        var created = new List<PlaylistItem>();
        var collected = new List<PlaylistItem>();

        var sessionsJson = await SendTidalGetAsync("https://api.tidal.com/v1/sessions", ct);
        if (string.IsNullOrWhiteSpace(sessionsJson)) return (fallbackName, created, collected);

        using var sDoc = global::System.Text.Json.JsonDocument.Parse(sessionsJson);
        var userId = sDoc.RootElement.GetLongProp("userId");
        var countryCode = FirstNonEmpty(sDoc.RootElement.GetStringProp("countryCode"), "US").Trim().ToUpperInvariant();
        var nickname = FirstNonEmpty(sDoc.RootElement.GetStringProp("userName"), sDoc.RootElement.GetStringProp("firstName"), fallbackName, userId > 0 ? $"Tidal #{userId}" : string.Empty);
        if (userId <= 0) return (nickname, created, collected);

        _tidalSessionUserId = userId;
        _tidalSessionCountryCode = countryCode;

        var ownedJson = await SendTidalGetAsync($"https://api.tidal.com/v1/users/{userId}/playlists?countryCode={countryCode}&limit=50", ct);
        if (!string.IsNullOrWhiteSpace(ownedJson))
        {
            using var pDoc = global::System.Text.Json.JsonDocument.Parse(ownedJson);
            if (pDoc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == global::System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var uuid = item.GetStringProp("uuid");
                    if (string.IsNullOrWhiteSpace(uuid)) continue;
                    var imgGuid = item.GetStringProp("squareImage", item.GetStringProp("image"));
                    var cover = string.IsNullOrWhiteSpace(imgGuid) ? string.Empty : $"https://resources.tidal.com/images/{imgGuid.Replace("-", "/")}/320x320.jpg";
                    created.Add(new PlaylistItem
                    {
                        Source = "tidal",
                        ThirdPartyId = uuid,
                        Name = item.GetStringProp("title", "Tidal 歌单"),
                        Description = item.GetStringProp("description"),
                        CoverImgUrl = cover,
                        TrackCount = item.GetIntProp("numberOfTracks"),
                        CreatorName = nickname
                    });
                }
            }
        }

        var favPlJson = await SendTidalGetAsync($"https://api.tidal.com/v1/users/{userId}/favorites/playlists?countryCode={countryCode}&limit=50", ct);
        if (!string.IsNullOrWhiteSpace(favPlJson))
        {
            using var fpDoc = global::System.Text.Json.JsonDocument.Parse(favPlJson);
            if (fpDoc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == global::System.Text.Json.JsonValueKind.Array)
            {
                foreach (var entry in items.EnumerateArray())
                {
                    var item = entry.TryGetProperty("playlist", out var plEl) ? plEl : entry;
                    var uuid = item.GetStringProp("uuid");
                    if (string.IsNullOrWhiteSpace(uuid)) continue;
                    var imgGuid = item.GetStringProp("squareImage", item.GetStringProp("image"));
                    var cover = string.IsNullOrWhiteSpace(imgGuid) ? string.Empty : $"https://resources.tidal.com/images/{imgGuid.Replace("-", "/")}/320x320.jpg";
                    collected.Add(new PlaylistItem
                    {
                        Source = "tidal",
                        ThirdPartyId = uuid,
                        Name = item.GetStringProp("title", "Tidal 收藏歌单"),
                        Description = item.GetStringProp("description"),
                        CoverImgUrl = cover,
                        TrackCount = item.GetIntProp("numberOfTracks")
                    });
                }
            }
        }

        var favTracksJson = await SendTidalGetAsync($"https://api.tidal.com/v1/users/{userId}/favorites/tracks?countryCode={countryCode}&limit=50", ct);
        if (!string.IsNullOrWhiteSpace(favTracksJson))
        {
            using var ftDoc = global::System.Text.Json.JsonDocument.Parse(favTracksJson);
            int totalFav = ftDoc.RootElement.GetIntProp("totalNumberOfItems");
            var favTracks = new List<SongItem>();
            if (ftDoc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == global::System.Text.Json.JsonValueKind.Array)
            {
                foreach (var entry in items.EnumerateArray())
                {
                    var trackObj = entry.TryGetProperty("item", out var inner) ? inner : entry;
                    var song = ParseTidalTrackElement(trackObj, string.Empty, favTracks.Count + 1);
                    if (song != null) favTracks.Add(song);
                }
            }

            int displayTotal = Math.Max(totalFav, favTracks.Count);
            if (displayTotal > 0)
            {
                created.Insert(0, new PlaylistItem
                {
                    Source = "tidal",
                    ThirdPartyId = "__fav_tracks__",
                    Name = "我喜欢的音乐 (Tidal)",
                    CoverImgUrl = favTracks.FirstOrDefault()?.PicUrl ?? string.Empty,
                    CreatorName = nickname,
                    TrackCount = displayTotal,
                    IsSystemDefaultPlaylist = true,
                    // 超过 50 首时留空 Tracks，以便打开详情时触发 GetTidalNativePlaylistDetailAsync 分页加载最多 200 首
                    Tracks = displayTotal > 50 ? [] : favTracks
                });
            }
        }

        return (nickname, created, collected);
    }

    private static string ExtractYtmAccountNickname(string? menuJson)
    {
        if (string.IsNullOrWhiteSpace(menuJson)) return string.Empty;
        try
        {
            using var doc = global::System.Text.Json.JsonDocument.Parse(menuJson);
            return FindAccountNameRecursive(doc.RootElement);
        }
        catch
        {
            return string.Empty;
        }

        static string FindAccountNameRecursive(global::System.Text.Json.JsonElement el)
        {
            if (el.ValueKind == global::System.Text.Json.JsonValueKind.Object)
            {
                if (el.TryGetProperty("accountName", out var anEl) && anEl.ValueKind == global::System.Text.Json.JsonValueKind.Object)
                {
                    var text = ExtractYtmRunsText(anEl);
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.Value.ValueKind is global::System.Text.Json.JsonValueKind.Object or global::System.Text.Json.JsonValueKind.Array)
                    {
                        var found = FindAccountNameRecursive(prop.Value);
                        if (!string.IsNullOrWhiteSpace(found)) return found;
                    }
                }
            }
            else if (el.ValueKind == global::System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind is global::System.Text.Json.JsonValueKind.Object or global::System.Text.Json.JsonValueKind.Array)
                    {
                        var found = FindAccountNameRecursive(item);
                        if (!string.IsNullOrWhiteSpace(found)) return found;
                    }
                }
            }
            return string.Empty;
        }
    }

    private static string ExtractYtmRunsText(global::System.Text.Json.JsonElement obj)
    {
        if (obj.ValueKind != global::System.Text.Json.JsonValueKind.Object) return string.Empty;
        var simple = obj.GetStringProp("simpleText");
        if (!string.IsNullOrWhiteSpace(simple)) return simple;
        if (obj.TryGetProperty("runs", out var runs) && runs.ValueKind == global::System.Text.Json.JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var r in runs.EnumerateArray())
            {
                sb.Append(r.GetStringProp("text"));
            }
            return sb.ToString().Trim();
        }
        return string.Empty;
    }

    private async Task<(string Nickname, List<PlaylistItem> Created, List<PlaylistItem> Collected)> GetYouTubeMusicUserLibraryAsync(
        string fallbackName, CancellationToken ct)
    {
        var created = new List<PlaylistItem>();
        var collected = new List<PlaylistItem>();

        var ctx = BuildYtmWebRemixContext();
        var menuTask = PostYtMusicInnertubeAsync("account/account_menu", new { context = ctx }, ct);
        var likedTask = PostYtMusicInnertubeAsync("browse", new { context = ctx, browseId = "FEmusic_liked_playlists" }, ct);
        var landingTask = PostYtMusicInnertubeAsync("browse", new { context = ctx, browseId = "FEmusic_library_landing" }, ct);
        await Task.WhenAll(menuTask, likedTask, landingTask);

        var nickname = FirstNonEmpty(ExtractYtmAccountNickname(await menuTask), fallbackName);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var json in new[] { await likedTask, await landingTask })
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                using var doc = global::System.Text.Json.JsonDocument.Parse(json);
                CollectYtmPlaylistsRecursive(doc.RootElement, created, collected, seen, nickname);
            }
            catch
            {
            }
        }

        // 确保“我喜欢的音乐 (YouTube Music)”快捷入口始终位于创建歌单首位
        int lmIndex = created.FindIndex(p => string.Equals(p.ThirdPartyId, "LM", StringComparison.OrdinalIgnoreCase));
        if (lmIndex > 0)
        {
            var lm = created[lmIndex];
            created.RemoveAt(lmIndex);
            created.Insert(0, lm);
        }
        else if (lmIndex < 0)
        {
            created.Insert(0, new PlaylistItem
            {
                Source = "ytmusic",
                ThirdPartyId = "LM",
                Name = "我喜欢的音乐 (YouTube Music)",
                CreatorName = FirstNonEmpty(nickname, "YouTube Music"),
                IsSystemDefaultPlaylist = true
            });
        }

        return (nickname, created, collected);
    }

    private static void CollectYtmPlaylistsRecursive(
        global::System.Text.Json.JsonElement el,
        List<PlaylistItem> created,
        List<PlaylistItem> collected,
        HashSet<string> seen,
        string nickname)
    {
        if (el.ValueKind == global::System.Text.Json.JsonValueKind.Object)
        {
            if (el.TryGetProperty("musicTwoRowItemRenderer", out var row) && row.ValueKind == global::System.Text.Json.JsonValueKind.Object)
            {
                var browseId = row.TryGetProperty("navigationEndpoint", out var nav) && nav.TryGetProperty("browseEndpoint", out var be)
                    ? be.GetStringProp("browseId")
                    : string.Empty;
                var title = row.TryGetProperty("title", out var titleEl) ? ExtractYtmRunsText(titleEl) : string.Empty;
                var subtitle = row.TryGetProperty("subtitle", out var subEl) ? ExtractYtmRunsText(subEl) : string.Empty;
                var cover = ExtractYtmLastThumbnailUrl(row, "thumbnailRenderer");
                TryAddYtmPlaylistItem(browseId, title, subtitle, cover, created, collected, seen, nickname);
                return;
            }

            if (el.TryGetProperty("musicResponsiveListItemRenderer", out var listRow) && listRow.ValueKind == global::System.Text.Json.JsonValueKind.Object)
            {
                var browseId = listRow.TryGetProperty("navigationEndpoint", out var nav) && nav.TryGetProperty("browseEndpoint", out var be)
                    ? be.GetStringProp("browseId")
                    : string.Empty;
                string title = string.Empty;
                var subtitleParts = new List<string>();
                if (listRow.TryGetProperty("flexColumns", out var cols) && cols.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                {
                    int colIdx = 0;
                    foreach (var col in cols.EnumerateArray())
                    {
                        if (col.TryGetProperty("musicResponsiveListItemFlexColumnRenderer", out var fcr) &&
                            fcr.TryGetProperty("text", out var txtEl))
                        {
                            var colText = ExtractYtmRunsText(txtEl);
                            if (colIdx == 0)
                            {
                                title = colText;
                                if (string.IsNullOrWhiteSpace(browseId) &&
                                    txtEl.TryGetProperty("runs", out var runs) &&
                                    runs.ValueKind == global::System.Text.Json.JsonValueKind.Array &&
                                    runs.GetArrayLength() > 0 &&
                                    runs[0].TryGetProperty("navigationEndpoint", out var rNav) &&
                                    rNav.TryGetProperty("browseEndpoint", out var rBe))
                                {
                                    browseId = rBe.GetStringProp("browseId");
                                }
                            }
                            else if (!string.IsNullOrWhiteSpace(colText))
                            {
                                subtitleParts.Add(colText);
                            }
                        }
                        colIdx++;
                    }
                }
                var cover = ExtractYtmLastThumbnailUrl(listRow, "thumbnail");
                TryAddYtmPlaylistItem(browseId, title, string.Join(" • ", subtitleParts), cover, created, collected, seen, nickname);
                return;
            }

            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Value.ValueKind is global::System.Text.Json.JsonValueKind.Object or global::System.Text.Json.JsonValueKind.Array)
                {
                    CollectYtmPlaylistsRecursive(prop.Value, created, collected, seen, nickname);
                }
            }
        }
        else if (el.ValueKind == global::System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind is global::System.Text.Json.JsonValueKind.Object or global::System.Text.Json.JsonValueKind.Array)
                {
                    CollectYtmPlaylistsRecursive(item, created, collected, seen, nickname);
                }
            }
        }

        static string ExtractYtmLastThumbnailUrl(global::System.Text.Json.JsonElement container, string thumbPropName)
        {
            if (container.TryGetProperty(thumbPropName, out var tr) &&
                tr.TryGetProperty("musicThumbnailRenderer", out var mtr) &&
                mtr.TryGetProperty("thumbnail", out var th) &&
                th.TryGetProperty("thumbnails", out var ths) &&
                ths.ValueKind == global::System.Text.Json.JsonValueKind.Array &&
                ths.GetArrayLength() > 0)
            {
                return ths[ths.GetArrayLength() - 1].GetStringProp("url");
            }
            return string.Empty;
        }

        static void TryAddYtmPlaylistItem(
            string rawBrowseId,
            string title,
            string subtitle,
            string cover,
            List<PlaylistItem> created,
            List<PlaylistItem> collected,
            HashSet<string> seen,
            string nickname)
        {
            if (string.IsNullOrWhiteSpace(rawBrowseId)) return;
            if (!rawBrowseId.StartsWith("VL", StringComparison.OrdinalIgnoreCase) &&
                !rawBrowseId.Equals("LM", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var normalizedId = rawBrowseId.StartsWith("VL", StringComparison.OrdinalIgnoreCase)
                ? rawBrowseId[2..]
                : rawBrowseId;
            if (string.IsNullOrWhiteSpace(normalizedId) || !seen.Add(normalizedId)) return;

            int trackCount = 0;
            var countMatch = Regex.Match(subtitle, @"(\d[\d,]*)\s*(?:songs?|tracks?|首)", RegexOptions.IgnoreCase);
            if (countMatch.Success)
            {
                int.TryParse(countMatch.Groups[1].Value.Replace(",", string.Empty), out trackCount);
            }

            string creatorPart = string.Empty;
            foreach (var seg in subtitle.Split(['•', '·'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (seg.Equals("Playlist", StringComparison.OrdinalIgnoreCase) ||
                    seg.Equals("播放列表", StringComparison.OrdinalIgnoreCase) ||
                    seg.Equals("歌单", StringComparison.OrdinalIgnoreCase) ||
                    Regex.IsMatch(seg, @"^\d[\d,]*\s*(?:songs?|tracks?|首)", RegexOptions.IgnoreCase))
                {
                    continue;
                }
                creatorPart = seg;
                break;
            }

            bool isLm = normalizedId.Equals("LM", StringComparison.OrdinalIgnoreCase);
            bool isOwned = isLm ||
                           string.IsNullOrWhiteSpace(creatorPart) ||
                           creatorPart.Equals("You", StringComparison.OrdinalIgnoreCase) ||
                           (!string.IsNullOrWhiteSpace(nickname) && creatorPart.Contains(nickname, StringComparison.OrdinalIgnoreCase));

            var pl = new PlaylistItem
            {
                Source = "ytmusic",
                ThirdPartyId = normalizedId,
                Name = isLm ? "我喜欢的音乐 (YouTube Music)" : FirstNonEmpty(title, "YouTube Music 歌单"),
                CoverImgUrl = cover,
                TrackCount = trackCount,
                CreatorName = isOwned ? FirstNonEmpty(creatorPart, nickname, "YouTube Music") : creatorPart,
                IsSystemDefaultPlaylist = isLm
            };

            if (isOwned) created.Add(pl);
            else collected.Add(pl);
        }
    }

    private async Task<(string Nickname, List<PlaylistItem> Created, List<PlaylistItem> Collected)> GetKuGouUserLibraryAsync(
        string fallbackName, CancellationToken ct)
    {
        var session = _platformAuth.TryGetSession(AuthMusicPlatform.KuGou);
        var creds = ParseKugouCompositeCookie(session);
        var nickname = FirstNonEmpty(
            creds.Nickname,
            fallbackName,
            !string.IsNullOrWhiteSpace(creds.UserId) ? $"酷狗用户 #{creds.UserId}" : string.Empty);

        var created = new List<PlaylistItem>();
        var collected = new List<PlaylistItem>();
        if (string.IsNullOrWhiteSpace(creds.UserId) || string.IsNullOrWhiteSpace(creds.Token))
        {
            return (nickname, created, collected);
        }

        try
        {
            var body = new Dictionary<string, object>
            {
                ["total_ver"] = 0,
                ["type"] = 2,
                ["page"] = 1,
                ["pagesize"] = 100
            };

            var json = await PostKugouCloudListGatewayAsync("/v7/get_all_list", body, ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                return (nickname, created, collected);
            }

            using var doc = global::System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var dataEl))
            {
                return (nickname, created, collected);
            }

            long totalVer = dataEl.GetLongProp("total_ver");
            if (totalVer <= 0) totalVer = doc.RootElement.GetLongProp("total_ver");
            if (totalVer > 0) _lastKugouTotalVer = totalVer;

            if (!dataEl.TryGetProperty("info", out var infoArr) ||
                infoArr.ValueKind != global::System.Text.Json.JsonValueKind.Array)
            {
                return (nickname, created, collected);
            }

            var seenListIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in infoArr.EnumerateArray())
            {
                if (item.GetIntProp("is_del") == 1) continue;

                var listId = ReadDeezerFlexString(item, "listid");
                var name = GlobalHtmlDecode(item.GetStringProp("name"));
                if (string.IsNullOrWhiteSpace(listId) || string.IsNullOrWhiteSpace(name) || !seenListIds.Add(listId))
                {
                    continue;
                }

                int isDef = item.GetIntProp("is_def");
                int count = item.GetIntProp("count");
                var pic = item.GetStringProp("pic").Replace("\\/", "/").Replace("{size}", "400");
                var createUid = ReadDeezerFlexString(item, "list_create_userid");
                var creatorName = GlobalHtmlDecode(FirstNonEmpty(item.GetStringProp("list_create_username"), nickname, "酷狗用户"));

                var pl = new PlaylistItem
                {
                    Id = long.TryParse(listId, out long lid) ? lid : Math.Abs(listId.GetHashCode()),
                    Source = "kugou",
                    ThirdPartyId = $"kgcloud:{listId}",
                    Name = name,
                    CoverImgUrl = string.IsNullOrWhiteSpace(pic) ? ImageUriHelper.DefaultCoverUri : pic,
                    TrackCount = count,
                    CreatorName = creatorName,
                    Description = GlobalHtmlDecode(item.GetStringProp("intro")),
                    IsSystemDefaultPlaylist = isDef > 0
                };

                if (isDef == 2)
                {
                    created.Insert(0, pl);
                }
                else if (isDef > 0 || string.Equals(createUid, creds.UserId, StringComparison.OrdinalIgnoreCase))
                {
                    created.Add(pl);
                }
                else
                {
                    collected.Add(pl);
                }
            }
        }
        catch
        {
        }

        return (nickname, created, collected);
    }

    private static string ComputeKuwoSecretHeader(string tokenValue, string cookieName)
    {
        if (string.IsNullOrEmpty(tokenValue) || string.IsNullOrEmpty(cookieName)) return string.Empty;

        var nSb = new StringBuilder(cookieName.Length * 3);
        foreach (char ch in cookieName)
        {
            nSb.Append(((int)ch).ToString());
        }
        string nStr = nSb.ToString();
        int step = nStr.Length / 5;
        if (step <= 0 || 5 * step >= nStr.Length) return string.Empty;

        var rStr = string.Concat(nStr[step], nStr[2 * step], nStr[3 * step], nStr[4 * step], nStr[5 * step]);
        if (!long.TryParse(rStr, out long r) || r < 2) return string.Empty;

        long c = (cookieName.Length + 1) / 2;
        const long l = 2147483647L;
        long d = Random.Shared.NextInt64(0, 100_000_000L);
        nStr += d.ToString();
        while (nStr.Length > 10)
        {
            long left = long.TryParse(nStr[..10], out var lv) ? lv : 0;
            long right = long.TryParse(nStr[10..], out var rv) ? rv : 0;
            nStr = (left + right).ToString();
        }

        long n = long.TryParse(nStr, out var nv) ? nv : 0;
        n = (r * n + c) % l;

        var hexSb = new StringBuilder(tokenValue.Length * 2 + 8);
        foreach (char ch in tokenValue)
        {
            int mask = (int)Math.Floor((double)n / l * 255.0);
            int code = ((int)ch) ^ mask;
            hexSb.Append(code.ToString("x2"));
            n = (r * n + c) % l;
        }

        hexSb.Append(d.ToString("x").PadLeft(8, '0'));
        return hexSb.ToString();
    }

    private async Task<(string Nickname, List<PlaylistItem> Created, List<PlaylistItem> Collected)> GetKuWoUserLibraryAsync(
        string fallbackName, CancellationToken ct)
    {
        var created = new List<PlaylistItem>();
        var collected = new List<PlaylistItem>();

        var session = _platformAuth.TryGetSession(AuthMusicPlatform.KuWo);
        var cookieStr = session?.CookieHeader ?? string.Empty;
        string uid = !string.IsNullOrWhiteSpace(session?.UserId) && session.UserId != "0" ? session.UserId : string.Empty;
        if (string.IsNullOrWhiteSpace(uid))
        {
            var uidMatch = Regex.Match(cookieStr, @"(?:^|;\s*)(?:userid|uid|loginUid)=(\d+)", RegexOptions.IgnoreCase);
            if (uidMatch.Success && uidMatch.Groups[1].Value != "0")
            {
                uid = uidMatch.Groups[1].Value;
            }
        }

        string nickname = fallbackName;

        // 若缺少昵称或 uid，尝试借助 www.kuwo.cn Hm_Iuvt Token + Secret 算法补充用户信息
        if ((string.IsNullOrWhiteSpace(nickname) || string.IsNullOrWhiteSpace(uid)) && session != null)
        {
            const string kwTokenKey = "Hm_Iuvt_cdb524f42f23cer9b268564v7y735ewrq2324";
            try
            {
                string hmToken = string.Empty;
                var existingTokenMatch = Regex.Match(cookieStr, $@"{kwTokenKey}=([^;\s]+)");
                if (existingTokenMatch.Success)
                {
                    hmToken = existingTokenMatch.Groups[1].Value;
                }

                if (string.IsNullOrWhiteSpace(hmToken))
                {
                    using var homeResp = await _authedHttpClient.GetAsync("https://www.kuwo.cn/", ct);
                    if (homeResp.Headers.TryGetValues("Set-Cookie", out var setCookies))
                    {
                        foreach (var sc in setCookies)
                        {
                            var m = Regex.Match(sc, $@"{kwTokenKey}=([^;\s]+)");
                            if (m.Success)
                            {
                                hmToken = m.Groups[1].Value;
                                break;
                            }
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(hmToken))
                {
                    var secret = ComputeKuwoSecretHeader(hmToken, kwTokenKey);
                    var infoUrl = $"https://www.kuwo.cn/api/www/user/info?httpsStatus=1&reqId={Guid.NewGuid():D}";
                    using var infoReq = new HttpRequestMessage(HttpMethod.Get, infoUrl);
                    var combinedCookie = string.IsNullOrWhiteSpace(cookieStr)
                        ? $"{kwTokenKey}={hmToken}"
                        : (cookieStr.Contains(kwTokenKey, StringComparison.Ordinal) ? cookieStr : $"{cookieStr}; {kwTokenKey}={hmToken}");
                    infoReq.Headers.TryAddWithoutValidation("Cookie", combinedCookie);
                    if (!string.IsNullOrWhiteSpace(secret)) infoReq.Headers.TryAddWithoutValidation("Secret", secret);
                    infoReq.Headers.TryAddWithoutValidation("Referer", "https://www.kuwo.cn/");

                    using var infoResp = await _authedHttpClient.SendAsync(infoReq, ct);
                    if (infoResp.IsSuccessStatusCode)
                    {
                        using var doc = global::System.Text.Json.JsonDocument.Parse(await infoResp.Content.ReadAsStringAsync(ct));
                        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == global::System.Text.Json.JsonValueKind.Object)
                        {
                            var nick = FirstNonEmpty(data.GetStringProp("nickname"), data.GetStringProp("username"));
                            if (!string.IsNullOrWhiteSpace(nick)) nickname = nick;
                            if (string.IsNullOrWhiteSpace(uid))
                            {
                                var parsedUid = ReadDeezerFlexString(data, "uid", "userid");
                                if (!string.IsNullOrWhiteSpace(parsedUid) && parsedUid != "0") uid = parsedUid;
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        if (string.IsNullOrWhiteSpace(uid))
        {
            return (nickname, created, collected);
        }

        try
        {
            var plUrl = $"http://nplserver.kuwo.cn/pl.svc?op=getlistbyuid&bigid=1&uid={Uri.EscapeDataString(uid)}&encode=utf8";
            var plJson = await _kuwoAppHttpClient.GetStringAsync(plUrl, ct);
            using var plDoc = global::System.Text.Json.JsonDocument.Parse(plJson);
            if (plDoc.RootElement.TryGetProperty("plist", out var plistArr) &&
                plistArr.ValueKind == global::System.Text.Json.JsonValueKind.Array)
            {
                var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in plistArr.EnumerateArray())
                {
                    var pid = ReadDeezerFlexString(item, "id");
                    var title = GlobalHtmlDecode(item.GetStringProp("title"));
                    if (string.IsNullOrWhiteSpace(pid) || string.IsNullOrWhiteSpace(title) || !seenIds.Add(pid))
                    {
                        continue;
                    }

                    var pic = item.GetStringProp("pic");
                    if (!string.IsNullOrWhiteSpace(pic))
                    {
                        pic = pic.Replace("_150.jpg", "_500.jpg", StringComparison.OrdinalIgnoreCase);
                    }
                    int count = item.GetIntProp("musicnum");
                    var type = item.GetStringProp("type");
                    var ownerUid = ReadDeezerFlexString(item, "uid");
                    var creator = GlobalHtmlDecode(FirstNonEmpty(item.GetStringProp("uname"), nickname, "酷我用户"));

                    var pl = new PlaylistItem
                    {
                        Id = long.TryParse(pid, out long lpid) ? lpid : Math.Abs(pid.GetHashCode()),
                        Source = "kuwo",
                        ThirdPartyId = pid,
                        Name = title,
                        CoverImgUrl = string.IsNullOrWhiteSpace(pic) ? ImageUriHelper.DefaultCoverUri : pic,
                        TrackCount = count,
                        CreatorName = creator,
                        IsSystemDefaultPlaylist = type.Equals("MYFAVORITE", StringComparison.OrdinalIgnoreCase)
                    };

                    if (type.Equals("MYFAVORITE", StringComparison.OrdinalIgnoreCase))
                    {
                        created.Insert(0, pl);
                    }
                    else if (string.IsNullOrWhiteSpace(ownerUid) ||
                             ownerUid == "0" ||
                             string.Equals(ownerUid, uid, StringComparison.OrdinalIgnoreCase))
                    {
                        created.Add(pl);
                    }
                    else
                    {
                        collected.Add(pl);
                    }
                }
            }
        }
        catch
        {
        }

        return (FirstNonEmpty(nickname, $"酷我用户 #{uid}"), created, collected);
    }

    private async Task<(string Nickname, List<PlaylistItem> Created, List<PlaylistItem> Collected)> GetQishuiUserLibraryAsync(
        string fallbackName, CancellationToken ct)
    {
        var created = new List<PlaylistItem>();
        var collected = new List<PlaylistItem>();
        string nickname = fallbackName;

        try
        {
            var meTask = GetQishuiWebApiAsync("/luna/pc/me", string.Empty, ct);
            var myPlTask = GetQishuiWebApiAsync("/luna/pc/me/playlist", "count=50", ct);
            var colPlTask = GetQishuiWebApiAsync("/luna/pc/me/collection/mixed", "count=50", ct);
            await Task.WhenAll(meTask, myPlTask, colPlTask);

            using var meDoc = await meTask;
            using var myPlDoc = await myPlTask;
            using var colPlDoc = await colPlTask;

            if (meDoc != null)
            {
                var root = meDoc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == global::System.Text.Json.JsonValueKind.Object
                    ? d
                    : meDoc.RootElement;
                foreach (var uKey in new[] { "user_info", "user" })
                {
                    if (root.TryGetProperty(uKey, out var uEl) && uEl.ValueKind == global::System.Text.Json.JsonValueKind.Object)
                    {
                        var nick = FirstNonEmpty(uEl.GetStringProp("nickname"), uEl.GetStringProp("name"));
                        if (!string.IsNullOrWhiteSpace(nick))
                        {
                            nickname = nick;
                            break;
                        }
                    }
                }
            }

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (myPlDoc != null)
            {
                CollectQishuiPlaylistsFromElement(myPlDoc.RootElement, nickname, seenIds, created);
            }
            if (colPlDoc != null)
            {
                CollectQishuiPlaylistsFromElement(colPlDoc.RootElement, string.Empty, seenIds, collected);
            }
        }
        catch
        {
        }

        return (nickname, created, collected);

        static void CollectQishuiPlaylistsFromElement(
            global::System.Text.Json.JsonElement el,
            string creatorFallback,
            HashSet<string> seenIds,
            List<PlaylistItem> output)
        {
            if (el.ValueKind == global::System.Text.Json.JsonValueKind.Object)
            {
                foreach (var arrKey in new[] { "playlists", "playlist_list", "collected_playlists", "items", "list" })
                {
                    if (el.TryGetProperty(arrKey, out var arr) && arr.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var rawNode in arr.EnumerateArray())
                        {
                            if (rawNode.ValueKind != global::System.Text.Json.JsonValueKind.Object) continue;
                            var node = rawNode.TryGetProperty("playlist", out var innerPl) && innerPl.ValueKind == global::System.Text.Json.JsonValueKind.Object
                                ? innerPl
                                : rawNode;
                            var pid = ReadDeezerFlexString(node, "id", "playlist_id");
                            var title = GlobalHtmlDecode(FirstNonEmpty(node.GetStringProp("title"), node.GetStringProp("name")));
                            if (string.IsNullOrWhiteSpace(pid) || string.IsNullOrWhiteSpace(title) || !seenIds.Add(pid))
                            {
                                continue;
                            }

                            int count = node.GetIntProp("count_tracks");
                            if (count <= 0) count = node.GetIntProp("track_count");
                            var cover = FirstNonEmpty(TryExtractQishuiNodeCover(node), ImageUriHelper.DefaultCoverUri);
                            string ownerName = creatorFallback;
                            if (node.TryGetProperty("owner", out var own) && own.ValueKind == global::System.Text.Json.JsonValueKind.Object)
                            {
                                ownerName = FirstNonEmpty(own.GetStringProp("nickname"), own.GetStringProp("name"), ownerName);
                            }

                            bool isQishuiFav = title.Contains("我喜欢", StringComparison.Ordinal) ||
                                node.GetStringProp("playlist_type").Equals("favorite", StringComparison.OrdinalIgnoreCase);
                            var pl = new PlaylistItem
                            {
                                Id = long.TryParse(pid, out long lpid) ? lpid : Math.Abs(pid.GetHashCode()),
                                Source = "qishui",
                                ThirdPartyId = pid,
                                Name = title,
                                Description = GlobalHtmlDecode(node.GetStringProp("desc")),
                                CoverImgUrl = cover,
                                TrackCount = count,
                                CreatorName = FirstNonEmpty(ownerName, "汽水音乐"),
                                IsSystemDefaultPlaylist = isQishuiFav
                            };

                            if (isQishuiFav)
                            {
                                output.Insert(0, pl);
                            }
                            else
                            {
                                output.Add(pl);
                            }
                        }
                    }
                }

                if (el.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == global::System.Text.Json.JsonValueKind.Object)
                {
                    CollectQishuiPlaylistsFromElement(dataEl, creatorFallback, seenIds, output);
                }
            }
        }
    }

    private async Task<(string Nickname, List<PlaylistItem> Created, List<PlaylistItem> Collected)> GetMiguUserLibraryAsync(
        string fallbackName, CancellationToken ct)
    {
        var created = new List<PlaylistItem>();
        var collected = new List<PlaylistItem>();
        string nickname = fallbackName;

        try
        {
            var infoUrls = new[]
            {
                "https://app.c.nf.migu.cn/pc/user/h5/queryUserInfo/v1.0",
                "https://app.c.nf.migu.cn/pc/user/home-page/trust-uid/v1.0",
                "https://music.migu.cn/v3/api/my/listen/getUserInfo"
            };
            foreach (var infoUrl in infoUrls)
            {
                var infoJson = await SendMiguGetAsync(infoUrl, ct);
                if (string.IsNullOrWhiteSpace(infoJson)) continue;
                var parsedNick = ExtractMiguNickname(infoJson);
                if (!string.IsNullOrWhiteSpace(parsedNick))
                {
                    nickname = parsedNick;
                    break;
                }
            }

            // 探测“我喜欢的音乐 (咪咕)”数量并添加顶部快捷歌单
            var likedProbeUrls = new[]
            {
                "https://app.c.nf.migu.cn/pc/v1.0/user/collections.do?pageNo=1&pageSize=1&type=1",
                "https://app.c.nf.migu.cn/user/h5/user/collection/v1.0?pageNo=1&type=1&OPType=03&pageSize=1&resourceType=2"
            };
            foreach (var likedUrl in likedProbeUrls)
            {
                var likedJson = await SendMiguGetAsync(likedUrl, ct);
                if (TryParseMiguLikedCount(likedJson, out int likedCount))
                {
                    created.Add(new PlaylistItem
                    {
                        Source = "migu",
                        ThirdPartyId = "__liked__",
                        Name = "我喜欢的音乐 (咪咕)",
                        CreatorName = FirstNonEmpty(nickname, "咪咕用户"),
                        TrackCount = likedCount,
                        IsSystemDefaultPlaylist = true
                    });
                    break;
                }
            }

            var seenCreated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cUrl in new[]
                     {
                         "https://app.c.nf.migu.cn/user/h5/my-music-list/v1.0?queryType=0&pageNo=1&pageSize=50",
                         "https://app.c.nf.migu.cn/pc/v1.0/user/collections.do?pageNo=1&pageSize=50&type=2"
                     })
            {
                var cJson = await SendMiguGetAsync(cUrl, ct);
                ExtractMiguPlaylistsFromJson(cJson, FirstNonEmpty(nickname, "咪咕用户"), seenCreated, created);
            }

            var seenCollected = new HashSet<string>(seenCreated, StringComparer.OrdinalIgnoreCase);
            foreach (var fUrl in new[]
                     {
                         "https://app.c.nf.migu.cn/pc/v1.0/user/collections.do?pageNo=1&pageSize=50&type=21",
                         "https://app.c.nf.migu.cn/user/h5/user/collection/v1.0?pageNo=1&type=1&OPType=03&pageSize=50&resourceType=2021"
                     })
            {
                var fJson = await SendMiguGetAsync(fUrl, ct);
                ExtractMiguPlaylistsFromJson(fJson, "咪咕音乐", seenCollected, collected);
            }
        }
        catch
        {
        }

        return (nickname, created, collected);

        static string ExtractMiguNickname(string json)
        {
            try
            {
                using var doc = global::System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("data", out var data) && data.ValueKind == global::System.Text.Json.JsonValueKind.Object)
                {
                    var nick = FirstNonEmpty(data.GetStringProp("nickName"), data.GetStringProp("nickname"), data.GetStringProp("userName"));
                    if (!string.IsNullOrWhiteSpace(nick)) return nick;
                    if (data.TryGetProperty("userInfo", out var ui) && ui.ValueKind == global::System.Text.Json.JsonValueKind.Object)
                    {
                        var uNick = FirstNonEmpty(ui.GetStringProp("nickName"), ui.GetStringProp("nickname"), ui.GetStringProp("userName"));
                        if (!string.IsNullOrWhiteSpace(uNick)) return uNick;
                    }
                }
                return FirstNonEmpty(root.GetStringProp("nickName"), root.GetStringProp("nickname"), root.GetStringProp("userName"));
            }
            catch
            {
                return string.Empty;
            }
        }

        static bool TryParseMiguLikedCount(string? json, out int likedCount)
        {
            likedCount = 0;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                using var doc = global::System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                var code = FirstNonEmpty(root.GetStringProp("code"), root.GetStringProp("returnCode"));
                bool hasData = root.TryGetProperty("data", out var data) && data.ValueKind == global::System.Text.Json.JsonValueKind.Object;
                if (code != "000000" && !hasData) return false;

                if (hasData)
                {
                    likedCount = Math.Max(data.GetIntProp("totalCount"), data.GetIntProp("total"));
                    if (likedCount == 0 && TryFindMiguSongArray(root, out var arr))
                    {
                        likedCount = arr.GetArrayLength();
                    }
                }
                return likedCount > 0 || code == "000000";
            }
            catch
            {
                return false;
            }
        }

        static void ExtractMiguPlaylistsFromJson(
            string? json,
            string defaultCreator,
            HashSet<string> seenIds,
            List<PlaylistItem> output)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            try
            {
                using var doc = global::System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                string[] arrKeys = ["musicLists", "myMusicList", "contents", "list", "items", "collectionList"];
                global::System.Text.Json.JsonElement targetArr = default;
                bool foundArr = false;

                if (root.TryGetProperty("data", out var data) && data.ValueKind == global::System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var k in arrKeys)
                    {
                        if (data.TryGetProperty(k, out var arr) && arr.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                        {
                            targetArr = arr;
                            foundArr = true;
                            break;
                        }
                    }
                }
                if (!foundArr)
                {
                    foreach (var k in arrKeys)
                    {
                        if (root.TryGetProperty(k, out var arr) && arr.ValueKind == global::System.Text.Json.JsonValueKind.Array)
                        {
                            targetArr = arr;
                            foundArr = true;
                            break;
                        }
                    }
                }
                if (!foundArr) return;

                foreach (var rawNode in targetArr.EnumerateArray())
                {
                    if (rawNode.ValueKind != global::System.Text.Json.JsonValueKind.Object) continue;
                    var node = rawNode;
                    foreach (var wrap in new[] { "content", "playlist", "resourceInfo" })
                    {
                        if (rawNode.TryGetProperty(wrap, out var inner) && inner.ValueKind == global::System.Text.Json.JsonValueKind.Object)
                        {
                            node = inner;
                            break;
                        }
                    }

                    var pid = ReadDeezerFlexString(node, "musicListId", "playlistId", "id", "resourceId");
                    var name = GlobalHtmlDecode(FirstNonEmpty(
                        node.GetStringProp("musicListName"),
                        node.GetStringProp("title"),
                        node.GetStringProp("name"),
                        node.GetStringProp("resourceName")));
                    if (string.IsNullOrWhiteSpace(pid) || string.IsNullOrWhiteSpace(name) || !seenIds.Add(pid))
                    {
                        continue;
                    }

                    string rawCover = FirstNonEmpty(
                        node.GetStringProp("image"),
                        node.GetStringProp("img"),
                        node.GetStringProp("picUrl"),
                        node.GetStringProp("cover"));
                    if (string.IsNullOrWhiteSpace(rawCover) &&
                        node.TryGetProperty("imgItem", out var imgEl))
                    {
                        rawCover = imgEl.ValueKind == global::System.Text.Json.JsonValueKind.Object
                            ? imgEl.GetStringProp("img")
                            : (imgEl.GetString() ?? string.Empty);
                    }
                    var cover = string.IsNullOrWhiteSpace(rawCover) ? ImageUriHelper.DefaultCoverUri : NormalizeMiguImageUrl(rawCover);

                    int count = node.GetIntProp("musicNum");
                    if (count <= 0) count = node.GetIntProp("songCount");
                    if (count <= 0) count = node.GetIntProp("count");

                    output.Add(new PlaylistItem
                    {
                        Id = long.TryParse(pid, out long lpid) ? lpid : Math.Abs(pid.GetHashCode()),
                        Source = "migu",
                        ThirdPartyId = pid,
                        Name = name,
                        CoverImgUrl = cover,
                        TrackCount = count,
                        CreatorName = FirstNonEmpty(GlobalHtmlDecode(node.GetStringProp("userName")), GlobalHtmlDecode(node.GetStringProp("ownerName")), defaultCreator)
                    });
                }
            }
            catch
            {
            }
        }
    }

    // ==================== 10. 跨平台歌单写入与曲目匹配（新建 / 添加 / 删除） ====================

    public static string NormalizeWritablePlatformKey(string? source) =>
        (source ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "kg" or "kugou" => "kugou",
            "mg" or "migu" => "migu",
            "spotify" => "spotify",
            "apple" => "apple",
            "deezer" => "deezer",
            "tidal" => "tidal",
            "ytmusic" => "ytmusic",
            _ => string.Empty
        };

    public static bool SupportsCloudPlaylistWrite(string? source)
        => !string.IsNullOrEmpty(NormalizeWritablePlatformKey(source));

    public static bool SupportsCloudPlaylistTrackRemove(string? source, string? thirdPartyId = null)
    {
        var key = NormalizeWritablePlatformKey(source);
        if (string.IsNullOrEmpty(key)) return false;
        // Apple Music 官方网页库 API 不支持从自建/资料库歌单单曲移除
        if (key == "apple") return false;
        return true;
    }

    public static bool IsSystemDefaultThirdPartyPlaylist(PlaylistItem? playlist)
    {
        if (playlist == null) return false;
        if (playlist.IsSystemDefaultPlaylist) return true;
        var tid = (playlist.ThirdPartyId ?? string.Empty).Trim();
        return tid.Equals("__liked__", StringComparison.OrdinalIgnoreCase) ||
               tid.Equals("__lib_songs__", StringComparison.OrdinalIgnoreCase) ||
               tid.Equals("__fav_tracks__", StringComparison.OrdinalIgnoreCase) ||
               tid.Equals("LM", StringComparison.OrdinalIgnoreCase) ||
               tid.Equals("VLLM", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<(bool Success, string Message)> DeleteUserPlaylistAsync(
        PlaylistItem playlist,
        CancellationToken ct = default)
    {
        if (playlist == null) return (false, "未选择要删除的歌单");
        if (IsSystemDefaultThirdPartyPlaylist(playlist))
            return (false, "系统默认喜欢歌单不支持删除");

        var key = NormalizeWritablePlatformKey(playlist.Source);
        if (string.IsNullOrEmpty(key))
            return (false, "该平台暂不支持云端删除歌单");

        return key switch
        {
            "kugou" => await DeleteKugouPlaylistAsync(playlist, ct),
            "migu" => await DeleteMiguPlaylistAsync(playlist, ct),
            "spotify" => await DeleteSpotifyPlaylistAsync(playlist, ct),
            "apple" => await DeleteApplePlaylistAsync(playlist, ct),
            "deezer" => await DeleteDeezerPlaylistAsync(playlist, ct),
            "tidal" => await DeleteTidalPlaylistAsync(playlist, ct),
            "ytmusic" => await DeleteYtMusicPlaylistAsync(playlist, ct),
            _ => (false, "该平台暂不支持云端删除歌单")
        };
    }

    public async Task<PlaylistItem?> CreateUserPlaylistAsync(
        string platformSource,
        string name,
        bool isPrivate = false,
        CancellationToken ct = default)
    {
        var title = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title)) return null;

        return NormalizeWritablePlatformKey(platformSource) switch
        {
            "kugou" => await CreateKugouPlaylistAsync(title, isPrivate, ct),
            "migu" => await CreateMiguPlaylistAsync(title, ct),
            "spotify" => await CreateSpotifyPlaylistAsync(title, isPrivate, ct),
            "apple" => await CreateApplePlaylistAsync(title, ct),
            "deezer" => await CreateDeezerPlaylistAsync(title, isPrivate, ct),
            "tidal" => await CreateTidalPlaylistAsync(title, ct),
            "ytmusic" => await CreateYtMusicPlaylistAsync(title, isPrivate, ct),
            _ => null
        };
    }

    public async Task<(bool Success, int ProcessedCount, string Message)> AddSongsToUserPlaylistAsync(
        PlaylistItem playlist,
        IReadOnlyList<SongItem> sourceSongs,
        CancellationToken ct = default)
    {
        if (playlist == null || sourceSongs.Count == 0)
            return (false, 0, "未选择要添加的歌曲或歌单");

        var key = NormalizeWritablePlatformKey(playlist.Source);
        if (string.IsNullOrEmpty(key))
            return (false, 0, "该平台暂不支持云端歌单写入");

        var resolved = await ResolveSongsForTargetPlatformAsync(sourceSongs, key, ct);
        if (resolved.Count == 0)
            return (false, 0, $"未在目标平台匹配到可写入的歌曲（共尝试 {sourceSongs.Count} 首）");

        (bool Success, int ProcessedCount, string Message) result = key switch
        {
            "kugou" => await AddKugouPlaylistSongsAsync(playlist.ThirdPartyId, resolved, ct),
            "migu" => await AddMiguPlaylistSongsAsync(playlist.ThirdPartyId, resolved, ct),
            "spotify" => await AddSpotifyPlaylistSongsAsync(playlist.ThirdPartyId, resolved, ct),
            "apple" => await AddApplePlaylistSongsAsync(playlist.ThirdPartyId, resolved, ct),
            "deezer" => await AddDeezerPlaylistSongsAsync(playlist.ThirdPartyId, resolved, ct),
            "tidal" => await AddTidalPlaylistSongsAsync(playlist.ThirdPartyId, resolved, ct),
            "ytmusic" => await AddYtMusicPlaylistSongsAsync(playlist.ThirdPartyId, resolved, ct),
            _ => (false, 0, "暂不支持该平台")
        };

        if (result.Success && resolved.Count < sourceSongs.Count)
        {
            return (true, result.ProcessedCount, $"{result.Message}（跨平台匹配成功 {resolved.Count}/{sourceSongs.Count} 首）");
        }
        return result;
    }

    public async Task<(bool Success, int ProcessedCount, string Message)> RemoveSongsFromUserPlaylistAsync(
        PlaylistItem playlist,
        IReadOnlyList<SongItem> songsToRemove,
        CancellationToken ct = default)
    {
        if (playlist == null || songsToRemove.Count == 0)
            return (false, 0, "未选择要移除的歌曲");

        var key = NormalizeWritablePlatformKey(playlist.Source);
        if (!SupportsCloudPlaylistTrackRemove(key, playlist.ThirdPartyId))
            return (false, 0, "该平台歌单暂不支持云端移除单曲");

        return key switch
        {
            "kugou" => await RemoveKugouPlaylistSongsAsync(playlist, songsToRemove, ct),
            "migu" => await RemoveMiguPlaylistSongsAsync(playlist.ThirdPartyId, songsToRemove, ct),
            "spotify" => await RemoveSpotifyPlaylistSongsAsync(playlist.ThirdPartyId, songsToRemove, ct),
            "deezer" => await RemoveDeezerPlaylistSongsAsync(playlist.ThirdPartyId, songsToRemove, ct),
            "tidal" => await RemoveTidalPlaylistSongsAsync(playlist.ThirdPartyId, songsToRemove, ct),
            "ytmusic" => await RemoveYtMusicPlaylistSongsAsync(playlist, songsToRemove, ct),
            _ => (false, 0, "暂不支持从该平台歌单移除歌曲")
        };
    }

    public async Task<List<SongItem>> ResolveSongsForTargetPlatformAsync(
        IReadOnlyList<SongItem> sourceSongs,
        string targetPlatform,
        CancellationToken ct = default)
    {
        var key = NormalizeWritablePlatformKey(targetPlatform);
        if (string.IsNullOrEmpty(key) || sourceSongs.Count == 0) return [];

        string searchSourceKey = key switch
        {
            "kugou" => "kg",
            "migu" => "mg",
            _ => key
        };

        using var throttle = new SemaphoreSlim(4, 4);
        var tasks = sourceSongs.Select(async song =>
        {
            if (HasNativeWriteIdentity(song, key)) return song;
            var keyword = $"{song.Name} {song.ArtistText}".Trim();
            if (string.IsNullOrWhiteSpace(keyword)) return null;

            await throttle.WaitAsync(ct);
            try
            {
                var candidates = await SearchSongsAsync(searchSourceKey, keyword, page: 1, pageSize: 8, ct);
                if (candidates.Count == 0) return null;
                var best = PickBestMatch(candidates, song) ?? candidates[0];
                return HasNativeWriteIdentity(best, key) ? best : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                throttle.Release();
            }
        });

        var matched = await Task.WhenAll(tasks);
        return matched.Where(static s => s != null).Cast<SongItem>().ToList();
    }

    private static bool HasNativeWriteIdentity(SongItem? song, string normalizedPlatform)
    {
        if (song == null) return false;
        return normalizedPlatform switch
        {
            "kugou" => !string.IsNullOrWhiteSpace(FirstNonEmpty(song.KugouHash, song.Kugou320Hash, song.KugouSqHash, song.KugouResHash)),
            "migu" => string.Equals(song.Source, "migu", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(song.MiguContentId),
            "spotify" => string.Equals(song.Source, "spotify", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(ExtractTrackNativeId(song, "spotify_")),
            "apple" => string.Equals(song.Source, "apple", StringComparison.OrdinalIgnoreCase) && long.TryParse(ExtractTrackNativeId(song, "apple_"), out var appleId) && appleId > 0,
            "deezer" => string.Equals(song.Source, "deezer", StringComparison.OrdinalIgnoreCase) && long.TryParse(ExtractTrackNativeId(song, "deezer_"), out var dzId) && dzId > 0,
            "tidal" => string.Equals(song.Source, "tidal", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(ExtractTrackNativeId(song, "tidal_")),
            "ytmusic" => string.Equals(song.Source, "ytmusic", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(ExtractTrackNativeId(song, "ytmusic_")),
            _ => false
        };
    }

    private static string ExtractTrackNativeId(SongItem song, string prefix)
    {
        if (!string.IsNullOrWhiteSpace(song.NativeTrackId)) return song.NativeTrackId.Trim();
        var id = (song.Id ?? string.Empty).Trim();
        return id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? id[prefix.Length..] : id;
    }

    // ---------- KuGou CloudList Write ----------

    private static string StripKugouCloudListId(string? thirdPartyId)
    {
        var raw = (thirdPartyId ?? string.Empty).Trim();
        return raw.StartsWith("kgcloud:", StringComparison.OrdinalIgnoreCase)
            ? raw["kgcloud:".Length..].Trim()
            : raw;
    }

    private static bool IsKugouGatewayOk(string? json, out JsonElement root, out string errMsg)
    {
        root = default;
        errMsg = string.Empty;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
            int status = root.GetIntProp("status");
            int errCode = root.GetIntProp("err_code");
            if (errCode == 0) errCode = root.GetIntProp("error_code");
            if (errCode == 0) errCode = root.GetIntProp("errcode");
            if (errCode == 0 && (status == 1 || !root.TryGetProperty("status", out _)))
                return true;
            if (status == 1 && errCode == 0)
                return true;
            errMsg = FirstNonEmpty(
                root.GetStringProp("error"),
                root.GetStringProp("errmsg"),
                root.GetStringProp("msg"),
                errCode != 0 ? $"error_code={errCode}" : string.Empty);
            return false;
        }
        catch
        {
            return false;
        }
    }

    private async Task<PlaylistItem?> CreateKugouPlaylistAsync(string name, bool isPrivate, CancellationToken ct)
    {
        var session = _platformAuth.TryGetSession(AuthMusicPlatform.KuGou);
        var creds = ParseKugouCompositeCookie(session);
        if (string.IsNullOrWhiteSpace(creds.UserId) || string.IsNullOrWhiteSpace(creds.Token))
            return null;

        foreach (var endpoint in new[] { "/v5/add_list", "/v4/add_list" })
        {
            var body = new Dictionary<string, object>
            {
                ["name"] = name,
                ["total_ver"] = 0,
                ["list_ver"] = 0,
                ["type"] = 0,
                ["is_pri"] = isPrivate ? 1 : 0,
                ["intro"] = string.Empty,
                ["source"] = 1
            };
            var json = await PostKugouCloudListGatewayAsync(endpoint, body, ct);
            if (!IsKugouGatewayOk(json, out var root, out _)) continue;

            long listId = 0;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                long newTotalVer = data.GetLongProp("total_ver");
                listId = data.GetLongProp("listid");
                if (data.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
                {
                    if (listId <= 0) listId = info.GetLongProp("listid");
                    if (newTotalVer <= 0) newTotalVer = info.GetLongProp("total_ver");
                }
                if (newTotalVer > 0) _lastKugouTotalVer = newTotalVer;
            }
            if (listId <= 0) listId = root.GetLongProp("listid");
            if (listId <= 0) continue;

            return new PlaylistItem
            {
                Id = listId,
                Source = "kugou",
                ThirdPartyId = $"kgcloud:{listId}",
                Name = name,
                CreatorName = string.IsNullOrWhiteSpace(creds.Nickname) ? "酷狗云歌单" : creds.Nickname,
                TrackCount = 0
            };
        }

        return null;
    }

    private async Task<(bool Success, int ProcessedCount, string Message)> AddKugouPlaylistSongsAsync(
        string thirdPartyId, IReadOnlyList<SongItem> songs, CancellationToken ct)
    {
        var listIdStr = StripKugouCloudListId(thirdPartyId);
        if (string.IsNullOrWhiteSpace(listIdStr))
            return (false, 0, "仅支持向本人酷狗云歌单添加歌曲");

        var entries = new List<Dictionary<string, object>>();
        foreach (var s in songs)
        {
            var hash = FirstNonEmpty(s.KugouHash, s.Kugou320Hash, s.KugouSqHash, s.KugouResHash);
            if (string.IsNullOrWhiteSpace(hash)) continue;
            entries.Add(new Dictionary<string, object>
            {
                ["hash"] = hash,
                ["name"] = string.IsNullOrWhiteSpace(s.ArtistText) ? s.Name : $"{s.ArtistText} - {s.Name}",
                ["album_id"] = long.TryParse(s.KugouAlbumId, out var aid) ? aid : 0L,
                ["timelen"] = s.DurationMs > 0 ? s.DurationMs : 0L
            });
        }
        if (entries.Count == 0)
            return (false, 0, "未找到有效的酷狗歌曲 Hash");

        var body = new Dictionary<string, object>
        {
            ["listid"] = long.TryParse(listIdStr, out var lid) ? lid : listIdStr,
            ["type"] = 0,
            ["list_ver"] = 0,
            ["data"] = entries
        };

        var json = await PostKugouCloudListGatewayAsync("/v6/add_song", body, ct);
        if (IsKugouGatewayOk(json, out _, out var errMsg))
            return (true, entries.Count, $"已添加到酷狗歌单（{entries.Count} 首）");

        return (false, 0, string.IsNullOrWhiteSpace(errMsg) ? "添加到酷狗歌单失败，请检查酷狗登录状态" : $"酷狗写入失败: {errMsg}");
    }

    private async Task<(bool Success, int ProcessedCount, string Message)> RemoveKugouPlaylistSongsAsync(
        PlaylistItem playlist, IReadOnlyList<SongItem> songsToRemove, CancellationToken ct)
    {
        var listIdStr = StripKugouCloudListId(playlist.ThirdPartyId);
        if (string.IsNullOrWhiteSpace(listIdStr))
            return (false, 0, "仅支持从本人酷狗云歌单移除歌曲");

        var fileIds = new HashSet<long>();
        var unresolved = new List<SongItem>();
        foreach (var s in songsToRemove)
        {
            if (s.KugouCloudFileId > 0) fileIds.Add(s.KugouCloudFileId);
            else unresolved.Add(s);
        }

        if (unresolved.Count > 0)
        {
            var detail = await GetPlaylistDetailAsync(playlist, ct);
            if (detail?.Tracks != null)
            {
                foreach (var u in unresolved)
                {
                    var uHash = FirstNonEmpty(u.KugouHash, u.Kugou320Hash, u.KugouSqHash, u.KugouResHash);
                    var found = detail.Tracks.FirstOrDefault(t =>
                        t.KugouCloudFileId > 0 &&
                        ((!string.IsNullOrWhiteSpace(uHash) &&
                          string.Equals(FirstNonEmpty(t.KugouHash, t.Kugou320Hash, t.KugouSqHash, t.KugouResHash), uHash, StringComparison.OrdinalIgnoreCase)) ||
                         string.Equals(t.Id, u.Id, StringComparison.OrdinalIgnoreCase)));
                    if (found != null) fileIds.Add(found.KugouCloudFileId);
                }
            }
        }

        if (fileIds.Count == 0)
            return (false, 0, "未找到待移除歌曲的酷狗云端 fileid，请刷新歌单后重试");

        var body = new Dictionary<string, object>
        {
            ["listid"] = long.TryParse(listIdStr, out var lid) ? lid : listIdStr,
            ["type"] = 0,
            ["list_ver"] = 0,
            ["data"] = fileIds.Select(fid => new Dictionary<string, object> { ["fileid"] = fid }).ToList()
        };

        var json = await PostKugouCloudListGatewayAsync("/v4/delete_songs", body, ct);
        if (IsKugouGatewayOk(json, out _, out var errMsg))
            return (true, fileIds.Count, $"已从酷狗歌单移除 {fileIds.Count} 首歌曲");

        return (false, 0, string.IsNullOrWhiteSpace(errMsg) ? "从酷狗歌单移除失败" : $"酷狗移除失败: {errMsg}");
    }

    // ---------- Migu V5 Playlist Write ----------

    private async Task<string?> SendMiguPostJsonAsync(string url, object payload, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            await _platformAuth.ApplyAuthHeadersAsync(AuthMusicPlatform.Migu, req, ct);
            req.Headers.TryAddWithoutValidation("Origin", "https://music.migu.cn");
            req.Headers.TryAddWithoutValidation("Referer", "https://music.migu.cn/v5/");
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await _authedHttpClient.SendAsync(req, ct);
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsMiguApiSuccess(string? json, out JsonElement root, out string msg)
    {
        root = default;
        msg = string.Empty;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
            var code = FirstNonEmpty(root.GetStringProp("code"), root.GetStringProp("returnCode"));
            msg = FirstNonEmpty(root.GetStringProp("info"), root.GetStringProp("msg"), root.GetStringProp("message"));
            return code is "000000" or "200" or "0";
        }
        catch
        {
            return false;
        }
    }

    private async Task<PlaylistItem?> CreateMiguPlaylistAsync(string name, CancellationToken ct)
    {
        var json = await SendMiguPostJsonAsync(
            "https://app.c.nf.migu.cn/pc/open/api/music-list/add/v2.0",
            new { title = name, channel = "23", type = "self_build" },
            ct);
        if (!IsMiguApiSuccess(json, out var root, out _)) return null;

        string pid = string.Empty;
        if (root.TryGetProperty("data", out var data))
        {
            pid = data.ValueKind == JsonValueKind.String
                ? data.GetString() ?? string.Empty
                : FirstNonEmpty(data.GetStringProp("id"), data.GetStringProp("musicListId"), data.GetStringProp("playlistId"));
        }
        if (string.IsNullOrWhiteSpace(pid)) pid = root.GetStringProp("id");
        if (string.IsNullOrWhiteSpace(pid)) return null;

        var nick = _platformAuth.TryGetSession(AuthMusicPlatform.Migu)?.Nickname;
        return new PlaylistItem
        {
            Id = long.TryParse(pid, out var lpid) ? lpid : Math.Abs(pid.GetHashCode()),
            Source = "migu",
            ThirdPartyId = pid,
            Name = name,
            CreatorName = string.IsNullOrWhiteSpace(nick) ? "咪咕自建歌单" : nick,
            TrackCount = 0
        };
    }

    private async Task<(bool Success, int ProcessedCount, string Message)> AddMiguPlaylistSongsAsync(
        string thirdPartyId, IReadOnlyList<SongItem> songs, CancellationToken ct)
    {
        var contentIds = songs
            .Select(static s => (s.MiguContentId ?? string.Empty).Trim())
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (contentIds.Count == 0)
            return (false, 0, "未找到咪咕歌曲 contentId，无法写入");

        if (string.Equals(thirdPartyId, "__liked__", StringComparison.OrdinalIgnoreCase))
        {
            var likedJson = await SendMiguPostJsonAsync(
                "https://app.c.nf.migu.cn/pc/user/api/add-music-list-song/v1.0",
                new { contentIds },
                ct);
            if (IsMiguApiSuccess(likedJson, out _, out var errLiked))
                return (true, contentIds.Count, $"已添加到咪咕喜欢歌单（{contentIds.Count} 首）");
            return (false, 0, string.IsNullOrWhiteSpace(errLiked) ? "添加到咪咕喜欢失败" : $"咪咕写入失败: {errLiked}");
        }

        var json = await SendMiguPostJsonAsync(
            "https://app.c.nf.migu.cn/pc/user/api/add-music-list-song/v1.0",
            new { id = thirdPartyId, contentIds },
            ct);
        if (IsMiguApiSuccess(json, out _, out var errMsg))
            return (true, contentIds.Count, $"已添加到咪咕歌单（{contentIds.Count} 首）");

        return (false, 0, string.IsNullOrWhiteSpace(errMsg) ? "添加到咪咕歌单失败" : $"咪咕写入失败: {errMsg}");
    }

    private async Task<(bool Success, int ProcessedCount, string Message)> RemoveMiguPlaylistSongsAsync(
        string thirdPartyId, IReadOnlyList<SongItem> songsToRemove, CancellationToken ct)
    {
        var contentIds = songsToRemove
            .Select(static s => (s.MiguContentId ?? string.Empty).Trim())
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (contentIds.Count == 0)
            return (false, 0, "未找到待移除歌曲的咪咕 contentId");

        if (string.Equals(thirdPartyId, "__liked__", StringComparison.OrdinalIgnoreCase))
        {
            int ok = 0;
            foreach (var cid in contentIds)
            {
                var delJson = await SendMiguGetAsync(
                    $"https://app.c.nf.migu.cn/pc/v1.0/user/del_collection.do?resourceId={Uri.EscapeDataString(cid)}&resourceType=2",
                    ct);
                if (IsMiguApiSuccess(delJson, out _, out _)) ok++;
            }
            return ok > 0
                ? (true, ok, $"已从咪咕喜欢移除 {ok} 首歌曲")
                : (false, 0, "从咪咕喜欢移除失败，请检查登录状态");
        }

        var json = await SendMiguPostJsonAsync(
            "https://app.c.nf.migu.cn/pc/user/h5-import-musiclist/v1.0",
            new
            {
                channel = "23",
                songflag = "2",
                contentId = string.Join("_", contentIds),
                id = thirdPartyId
            },
            ct);
        if (IsMiguApiSuccess(json, out _, out var errMsg))
            return (true, contentIds.Count, $"已从咪咕歌单移除 {contentIds.Count} 首歌曲");

        return (false, 0, string.IsNullOrWhiteSpace(errMsg) ? "从咪咕歌单移除失败" : $"咪咕移除失败: {errMsg}");
    }

    // ---------- Spotify Web API Write ----------

    private async Task<(bool Ok, int StatusCode, string Body)> SendSpotifyApiAsync(
        HttpMethod method, string url, object? payload, string bearer, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new global::System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
            req.Headers.TryAddWithoutValidation("Origin", "https://open.spotify.com");
            req.Headers.TryAddWithoutValidation("Referer", "https://open.spotify.com/");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (payload != null)
            {
                req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            }
            using var resp = await _authedHttpClient.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    private async Task<PlaylistItem?> CreateSpotifyPlaylistAsync(string name, bool isPrivate, CancellationToken ct)
    {
        var bearer = await EnsureAuthedSpotifyBearerAsync(ct);
        if (string.IsNullOrWhiteSpace(bearer)) return null;

        string userId = string.Empty;
        string displayName = "Spotify";
        var meRes = await SendSpotifyApiAsync(HttpMethod.Get, "https://api.spotify.com/v1/me", null, bearer, ct);
        if (meRes.Ok && !string.IsNullOrWhiteSpace(meRes.Body))
        {
            try
            {
                using var meDoc = JsonDocument.Parse(meRes.Body);
                userId = meDoc.RootElement.GetStringProp("id");
                displayName = FirstNonEmpty(meDoc.RootElement.GetStringProp("display_name"), userId, "Spotify");
            }
            catch
            {
            }
        }

        var payload = new Dictionary<string, object>
        {
            ["name"] = name,
            ["description"] = string.Empty,
            ["public"] = !isPrivate
        };

        var createUrl = !string.IsNullOrWhiteSpace(userId)
            ? $"https://api.spotify.com/v1/users/{Uri.EscapeDataString(userId)}/playlists"
            : "https://api.spotify.com/v1/me/playlists";
        var res = await SendSpotifyApiAsync(HttpMethod.Post, createUrl, payload, bearer, ct);
        if (!res.Ok && createUrl != "https://api.spotify.com/v1/me/playlists")
        {
            res = await SendSpotifyApiAsync(HttpMethod.Post, "https://api.spotify.com/v1/me/playlists", payload, bearer, ct);
        }
        if (!res.Ok || string.IsNullOrWhiteSpace(res.Body)) return null;

        try
        {
            using var doc = JsonDocument.Parse(res.Body);
            var pid = doc.RootElement.GetStringProp("id");
            if (string.IsNullOrWhiteSpace(pid)) return null;
            return new PlaylistItem
            {
                Source = "spotify",
                ThirdPartyId = pid,
                Name = name,
                CreatorName = displayName,
                TrackCount = 0
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<(bool Success, int ProcessedCount, string Message)> AddSpotifyPlaylistSongsAsync(
        string thirdPartyId, IReadOnlyList<SongItem> songs, CancellationToken ct)
    {
        var bearer = await EnsureAuthedSpotifyBearerAsync(ct);
        if (string.IsNullOrWhiteSpace(bearer))
            return (false, 0, "请先在个人中心登录 Spotify");

        var ids = songs
            .Select(s => ExtractTrackNativeId(s, "spotify_"))
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
            return (false, 0, "未找到有效的 Spotify 曲目 ID");

        bool isLiked = string.Equals(thirdPartyId, "__liked__", StringComparison.OrdinalIgnoreCase);
        int chunkSize = isLiked ? 50 : 100;
        int okCount = 0;
        string lastErr = string.Empty;

        for (int i = 0; i < ids.Count; i += chunkSize)
        {
            var chunk = ids.Skip(i).Take(chunkSize).ToList();
            if (isLiked)
            {
                var r = await SendSpotifyApiAsync(HttpMethod.Put, "https://api.spotify.com/v1/me/tracks", new { ids = chunk }, bearer, ct);
                if (r.Ok) okCount += chunk.Count;
                else lastErr = $"HTTP {r.StatusCode}";
            }
            else
            {
                var uris = chunk.Select(id => $"spotify:track:{id}").ToList();
                var r = await SendSpotifyApiAsync(
                    HttpMethod.Post,
                    $"https://api.spotify.com/v1/playlists/{Uri.EscapeDataString(thirdPartyId)}/tracks",
                    new { uris },
                    bearer,
                    ct);
                if (r.Ok) okCount += chunk.Count;
                else lastErr = $"HTTP {r.StatusCode}";
            }
        }

        return okCount > 0
            ? (true, okCount, $"已添加到 Spotify {(isLiked ? "喜欢的歌曲" : "歌单")}（{okCount} 首）")
            : (false, 0, $"添加到 Spotify 失败 ({lastErr})");
    }

    private async Task<(bool Success, int ProcessedCount, string Message)> RemoveSpotifyPlaylistSongsAsync(
        string thirdPartyId, IReadOnlyList<SongItem> songsToRemove, CancellationToken ct)
    {
        var bearer = await EnsureAuthedSpotifyBearerAsync(ct);
        if (string.IsNullOrWhiteSpace(bearer))
            return (false, 0, "请先在个人中心登录 Spotify");

        var ids = songsToRemove
            .Select(s => ExtractTrackNativeId(s, "spotify_"))
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
            return (false, 0, "未找到待移除歌曲的 Spotify ID");

        bool isLiked = string.Equals(thirdPartyId, "__liked__", StringComparison.OrdinalIgnoreCase);
        int chunkSize = isLiked ? 50 : 100;
        int okCount = 0;
        string lastErr = string.Empty;

        for (int i = 0; i < ids.Count; i += chunkSize)
        {
            var chunk = ids.Skip(i).Take(chunkSize).ToList();
            if (isLiked)
            {
                var r = await SendSpotifyApiAsync(HttpMethod.Delete, "https://api.spotify.com/v1/me/tracks", new { ids = chunk }, bearer, ct);
                if (r.Ok) okCount += chunk.Count;
                else lastErr = $"HTTP {r.StatusCode}";
            }
            else
            {
                var tracks = chunk.Select(id => new { uri = $"spotify:track:{id}" }).ToList();
                var r = await SendSpotifyApiAsync(
                    HttpMethod.Delete,
                    $"https://api.spotify.com/v1/playlists/{Uri.EscapeDataString(thirdPartyId)}/tracks",
                    new { tracks },
                    bearer,
                    ct);
                if (r.Ok) okCount += chunk.Count;
                else lastErr = $"HTTP {r.StatusCode}";
            }
        }

        return okCount > 0
            ? (true, okCount, $"已从 Spotify {(isLiked ? "喜欢的歌曲" : "歌单")}移除 {okCount} 首")
            : (false, 0, $"从 Spotify 移除失败 ({lastErr})");
    }

    // ---------- Apple Music Library Write ----------

    private async Task<(bool Ok, int StatusCode, string Body)> SendAppleMusicLibraryWriteAsync(
        HttpMethod method, string url, object? payload, string devToken, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(method, url);
            await _platformAuth.ApplyAuthHeadersAsync(AuthMusicPlatform.AppleMusic, req, ct);
            req.Headers.Authorization = new global::System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", devToken);
            req.Headers.TryAddWithoutValidation("Origin", "https://music.apple.com");
            req.Headers.TryAddWithoutValidation("Referer", "https://music.apple.com/");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (payload != null)
            {
                req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            }
            using var resp = await _authedHttpClient.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    private async Task<PlaylistItem?> CreateApplePlaylistAsync(string name, CancellationToken ct)
    {
        var devToken = await EnsureAppleDeveloperTokenForLibraryAsync(ct);
        var session = _platformAuth.TryGetSession(AuthMusicPlatform.AppleMusic);
        if (string.IsNullOrWhiteSpace(devToken) || session == null || string.IsNullOrWhiteSpace(session.AccessToken))
            return null;

        var res = await SendAppleMusicLibraryWriteAsync(
            HttpMethod.Post,
            "https://api.music.apple.com/v1/me/library/playlists",
            new Dictionary<string, object> { ["attributes"] = new { name, description = string.Empty } },
            devToken,
            ct);
        if (!res.Ok || string.IsNullOrWhiteSpace(res.Body)) return null;

        try
        {
            using var doc = JsonDocument.Parse(res.Body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                return null;
            var pid = data[0].GetStringProp("id");
            if (string.IsNullOrWhiteSpace(pid)) return null;
            return new PlaylistItem
            {
                Source = "apple",
                ThirdPartyId = pid,
                Name = name,
                CreatorName = "Apple Music 我的资料库",
                TrackCount = 0
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<(bool Success, int ProcessedCount, string Message)> AddApplePlaylistSongsAsync(
        string thirdPartyId, IReadOnlyList<SongItem> songs, CancellationToken ct)
    {
        var devToken = await EnsureAppleDeveloperTokenForLibraryAsync(ct);
        var session = _platformAuth.TryGetSession(AuthMusicPlatform.AppleMusic);
        if (string.IsNullOrWhiteSpace(devToken) || session == null || string.IsNullOrWhiteSpace(session.AccessToken))
            return (false, 0, "请先在个人中心登录 Apple Music 并授权 Music-User-Token");

        var ids = songs
            .Select(s => ExtractTrackNativeId(s, "apple_"))
            .Where(static id => long.TryParse(id, out var n) && n > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
            return (false, 0, "未找到有效的 Apple Music 目录歌曲 ID");

        bool isLibSongs = string.Equals(thirdPartyId, "__lib_songs__", StringComparison.OrdinalIgnoreCase);
        int okCount = 0;
        string lastErr = string.Empty;

        for (int i = 0; i < ids.Count; i += 25)
        {
            var chunk = ids.Skip(i).Take(25).ToList();
            if (isLibSongs)
            {
                var url = $"https://api.music.apple.com/v1/me/library?ids[songs]={Uri.EscapeDataString(string.Join(",", chunk))}";
                var r = await SendAppleMusicLibraryWriteAsync(HttpMethod.Post, url, null, devToken, ct);
                if (r.Ok) okCount += chunk.Count;
                else lastErr = $"HTTP {r.StatusCode}";
            }
            else
            {
                var payload = new Dictionary<string, object>
                {
                    ["data"] = chunk.Select(id => new { id, type = "songs" }).ToList()
                };
                var r = await SendAppleMusicLibraryWriteAsync(
                    HttpMethod.Post,
                    $"https://api.music.apple.com/v1/me/library/playlists/{Uri.EscapeDataString(thirdPartyId)}/tracks",
                    payload,
                    devToken,
                    ct);
                if (r.Ok) okCount += chunk.Count;
                else lastErr = $"HTTP {r.StatusCode}";
            }
        }

        return okCount > 0
            ? (true, okCount, $"已添加到 Apple Music {(isLibSongs ? "资料库歌曲" : "歌单")}（{okCount} 首）")
            : (false, 0, $"添加到 Apple Music 失败 ({lastErr})");
    }

    // ---------- Deezer GW Write ----------

    private static bool IsDeezerGwSuccess(string? json, out JsonElement results)
    {
        results = default;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Array && err.GetArrayLength() > 0) return false;
                if (err.ValueKind == JsonValueKind.Object && err.EnumerateObject().Any()) return false;
            }
            if (root.TryGetProperty("results", out var res))
                results = res.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<PlaylistItem?> CreateDeezerPlaylistAsync(string name, bool isPrivate, CancellationToken ct)
    {
        var gw = await GetDeezerGwSessionAsync(ct);
        if (gw == null || string.IsNullOrWhiteSpace(gw.Value.ApiToken)) return null;

        var body = JsonSerializer.Serialize(new
        {
            title = name,
            description = string.Empty,
            status = isPrivate ? 1 : 0,
            songs = Array.Empty<object>()
        });
        var json = await PostDeezerGwAsync("playlist.create", gw.Value.ApiToken, body, ct);
        if (!IsDeezerGwSuccess(json, out var res)) return null;

        string pid = res.ValueKind switch
        {
            JsonValueKind.Number => res.GetInt64().ToString(),
            JsonValueKind.String => res.GetString() ?? string.Empty,
            JsonValueKind.Object => FirstNonEmpty(res.GetStringProp("PLAYLIST_ID"), res.GetStringProp("id")),
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(pid) || pid == "0") return null;

        return new PlaylistItem
        {
            Id = long.TryParse(pid, out var lid) ? lid : Math.Abs(pid.GetHashCode()),
            Source = "deezer",
            ThirdPartyId = pid,
            Name = name,
            CreatorName = string.IsNullOrWhiteSpace(gw.Value.Nickname) ? "Deezer" : gw.Value.Nickname,
            TrackCount = 0
        };
    }

    private async Task<(bool Success, int ProcessedCount, string Message)> AddDeezerPlaylistSongsAsync(
        string thirdPartyId, IReadOnlyList<SongItem> songs, CancellationToken ct)
    {
        var gw = await GetDeezerGwSessionAsync(ct);
        if (gw == null || string.IsNullOrWhiteSpace(gw.Value.ApiToken))
            return (false, 0, "请先在个人中心登录 Deezer");

        var tids = songs
            .Select(s => long.TryParse(ExtractTrackNativeId(s, "deezer_"), out var id) ? id : 0L)
            .Where(static id => id > 0)
            .Distinct()
            .ToList();
        if (tids.Count == 0)
            return (false, 0, "未找到有效的 Deezer 曲目 ID");

        bool isLoved = !string.IsNullOrWhiteSpace(gw.Value.LovedTracksId) &&
            string.Equals(thirdPartyId, gw.Value.LovedTracksId, StringComparison.OrdinalIgnoreCase);

        if (isLoved)
        {
            int ok = 0;
            foreach (var tid in tids)
            {
                var r = await PostDeezerGwAsync("favorite_song.add", gw.Value.ApiToken, JsonSerializer.Serialize(new { SNG_ID = tid }), ct);
                if (IsDeezerGwSuccess(r, out _)) ok++;
            }
            return ok > 0
                ? (true, ok, $"已收藏到 Deezer 喜欢的歌曲（{ok} 首）")
                : (false, 0, "添加到 Deezer 喜欢的歌曲失败");
        }

        var payload = JsonSerializer.Serialize(new
        {
            playlist_id = thirdPartyId,
            songs = tids.Select(t => new object[] { t, 0 }).ToList(),
            offset = -1
        });
        var json = await PostDeezerGwAsync("playlist.addSongs", gw.Value.ApiToken, payload, ct);
        return IsDeezerGwSuccess(json, out _)
            ? (true, tids.Count, $"已添加到 Deezer 歌单（{tids.Count} 首）")
            : (false, 0, "添加到 Deezer 歌单失败");
    }

    private async Task<(bool Success, int ProcessedCount, string Message)> RemoveDeezerPlaylistSongsAsync(
        string thirdPartyId, IReadOnlyList<SongItem> songsToRemove, CancellationToken ct)
    {
        var gw = await GetDeezerGwSessionAsync(ct);
        if (gw == null || string.IsNullOrWhiteSpace(gw.Value.ApiToken))
            return (false, 0, "请先在个人中心登录 Deezer");

        var tids = songsToRemove
            .Select(s => long.TryParse(ExtractTrackNativeId(s, "deezer_"), out var id) ? id : 0L)
            .Where(static id => id > 0)
            .Distinct()
            .ToList();
        if (tids.Count == 0)
            return (false, 0, "未找到待移除歌曲的 Deezer ID");

        bool isLoved = !string.IsNullOrWhiteSpace(gw.Value.LovedTracksId) &&
            string.Equals(thirdPartyId, gw.Value.LovedTracksId, StringComparison.OrdinalIgnoreCase);

        if (isLoved)
        {
            int ok = 0;
            foreach (var tid in tids)
            {
                var r = await PostDeezerGwAsync("favorite_song.remove", gw.Value.ApiToken, JsonSerializer.Serialize(new { SNG_ID = tid }), ct);
                if (IsDeezerGwSuccess(r, out _)) ok++;
            }
            return ok > 0
                ? (true, ok, $"已从 Deezer 喜欢的歌曲移除 {ok} 首")
                : (false, 0, "从 Deezer 喜欢的歌曲移除失败");
        }

        var payload = JsonSerializer.Serialize(new
        {
            playlist_id = thirdPartyId,
            songs = tids.Select(t => new object[] { t, 0 }).ToList()
        });
        var json = await PostDeezerGwAsync("playlist.deleteSongs", gw.Value.ApiToken, payload, ct);
        return IsDeezerGwSuccess(json, out _)
            ? (true, tids.Count, $"已从 Deezer 歌单移除 {tids.Count} 首歌曲")
            : (false, 0, "从 Deezer 歌单移除失败");
    }

    // ---------- Tidal API v1 Write ----------

    private async Task<(bool Ok, int StatusCode, string Body, string? ETag)> SendTidalWriteAsync(
        HttpMethod method, string url, HttpContent? content, CancellationToken ct, string? ifNoneMatch = null)
    {
        try
        {
            using var req = new HttpRequestMessage(method, url);
            await _platformAuth.ApplyAuthHeadersAsync(AuthMusicPlatform.Tidal, req, ct);
            req.Headers.TryAddWithoutValidation("x-tidal-token", TidalPublicClientToken);
            req.Headers.TryAddWithoutValidation("Origin", "https://listen.tidal.com");
            req.Headers.TryAddWithoutValidation("Referer", "https://listen.tidal.com/");
            if (!string.IsNullOrWhiteSpace(ifNoneMatch))
                req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
            if (content != null)
                req.Content = content;

            using var resp = await _authedHttpClient.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var etag = resp.Headers.ETag?.Tag;
            if (string.IsNullOrWhiteSpace(etag) && resp.Headers.TryGetValues("ETag", out var etVals))
                etag = etVals.FirstOrDefault();
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body, etag);
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message, null);
        }
    }

    private async Task<PlaylistItem?> CreateTidalPlaylistAsync(string name, CancellationToken ct)
    {
        await EnsureTidalSessionInfoAsync(ct);
        if (_tidalSessionUserId <= 0) return null;

        var cc = Uri.EscapeDataString(string.IsNullOrWhiteSpace(_tidalSessionCountryCode) ? "US" : _tidalSessionCountryCode);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["title"] = name,
            ["description"] = string.Empty
        });
        var res = await SendTidalWriteAsync(
            HttpMethod.Post,
            $"https://api.tidal.com/v1/users/{_tidalSessionUserId}/playlists?countryCode={cc}",
            form,
            ct);
        if (!res.Ok || string.IsNullOrWhiteSpace(res.Body)) return null;

        try
        {
            using var doc = JsonDocument.Parse(res.Body);
            var uuid = doc.RootElement.GetStringProp("uuid");
            if (string.IsNullOrWhiteSpace(uuid)) return null;
            return new PlaylistItem
            {
                Source = "tidal",
                ThirdPartyId = uuid,
                Name = name,
                CreatorName = "Tidal 我的歌单",
                TrackCount = 0
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<(bool Success, int ProcessedCount, string Message)> AddTidalPlaylistSongsAsync(
        string thirdPartyId, IReadOnlyList<SongItem> songs, CancellationToken ct)
    {
        await EnsureTidalSessionInfoAsync(ct);
        if (_tidalSessionUserId <= 0)
            return (false, 0, "请先在个人中心登录 Tidal");

        var tids = songs
            .Select(s => ExtractTrackNativeId(s, "tidal_"))
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tids.Count == 0)
            return (false, 0, "未找到有效的 Tidal 曲目 ID");

        var cc = Uri.EscapeDataString(string.IsNullOrWhiteSpace(_tidalSessionCountryCode) ? "US" : _tidalSessionCountryCode);
        if (string.Equals(thirdPartyId, "__fav_tracks__", StringComparison.OrdinalIgnoreCase))
        {
            using var favForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["trackIds"] = string.Join(",", tids)
            });
            var r = await SendTidalWriteAsync(
                HttpMethod.Post,
                $"https://api.tidal.com/v1/users/{_tidalSessionUserId}/favorites/tracks?countryCode={cc}",
                favForm,
                ct);
            return r.Ok
                ? (true, tids.Count, $"已收藏到 Tidal 我的收藏（{tids.Count} 首）")
                : (false, 0, $"添加到 Tidal 收藏失败 (HTTP {r.StatusCode})");
        }

        var metaRes = await SendTidalWriteAsync(
            HttpMethod.Get,
            $"https://api.tidal.com/v1/playlists/{Uri.EscapeDataString(thirdPartyId)}?countryCode={cc}",
            null,
            ct);
        var etag = string.IsNullOrWhiteSpace(metaRes.ETag) ? "*" : metaRes.ETag;

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["trackIds"] = string.Join(",", tids),
            ["onDupes"] = "SKIP"
        });
        var addRes = await SendTidalWriteAsync(
            HttpMethod.Post,
            $"https://api.tidal.com/v1/playlists/{Uri.EscapeDataString(thirdPartyId)}/items?countryCode={cc}",
            form,
            ct,
            etag);

        return addRes.Ok
            ? (true, tids.Count, $"已添加到 Tidal 歌单（{tids.Count} 首）")
            : (false, 0, $"添加到 Tidal 歌单失败 (HTTP {addRes.StatusCode})");
    }

    private async Task<(bool Success, int ProcessedCount, string Message)> RemoveTidalPlaylistSongsAsync(
        string thirdPartyId, IReadOnlyList<SongItem> songsToRemove, CancellationToken ct)
    {
        await EnsureTidalSessionInfoAsync(ct);
        if (_tidalSessionUserId <= 0)
            return (false, 0, "请先在个人中心登录 Tidal");

        var targetIds = songsToRemove
            .Select(s => ExtractTrackNativeId(s, "tidal_"))
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targetIds.Count == 0)
            return (false, 0, "未找到待移除歌曲的 Tidal ID");

        var cc = Uri.EscapeDataString(string.IsNullOrWhiteSpace(_tidalSessionCountryCode) ? "US" : _tidalSessionCountryCode);
        if (string.Equals(thirdPartyId, "__fav_tracks__", StringComparison.OrdinalIgnoreCase))
        {
            int ok = 0;
            foreach (var tid in targetIds)
            {
                var r = await SendTidalWriteAsync(
                    HttpMethod.Delete,
                    $"https://api.tidal.com/v1/users/{_tidalSessionUserId}/favorites/tracks/{Uri.EscapeDataString(tid)}?countryCode={cc}",
                    null,
                    ct);
                if (r.Ok) ok++;
            }
            return ok > 0
                ? (true, ok, $"已从 Tidal 收藏移除 {ok} 首歌曲")
                : (false, 0, "从 Tidal 收藏移除失败");
        }

        var itemsRes = await SendTidalWriteAsync(
            HttpMethod.Get,
            $"https://api.tidal.com/v1/playlists/{Uri.EscapeDataString(thirdPartyId)}/items?countryCode={cc}&limit=100",
            null,
            ct);
        if (!itemsRes.Ok || string.IsNullOrWhiteSpace(itemsRes.Body))
            return (false, 0, "无法读取 Tidal 歌单条目索引与 ETag");

        var indices = new List<int>();
        try
        {
            using var doc = JsonDocument.Parse(itemsRes.Body);
            if (doc.RootElement.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                int idx = 0;
                foreach (var entry in arr.EnumerateArray())
                {
                    var itemObj = entry.TryGetProperty("item", out var inner) ? inner : entry;
                    var tid = itemObj.GetStringProp("id");
                    if (string.IsNullOrWhiteSpace(tid))
                    {
                        var lid = itemObj.GetLongProp("id");
                        if (lid > 0) tid = lid.ToString();
                    }
                    if (!string.IsNullOrWhiteSpace(tid) && targetIds.Contains(tid))
                        indices.Add(idx);
                    idx++;
                }
            }
        }
        catch
        {
            return (false, 0, "解析 Tidal 歌单条目索引失败");
        }

        if (indices.Count == 0)
            return (false, 0, "未在 Tidal 歌单前 100 首中定位到该曲目");

        var etag = string.IsNullOrWhiteSpace(itemsRes.ETag) ? "*" : itemsRes.ETag;
        var indexCsv = string.Join(",", indices);
        var delRes = await SendTidalWriteAsync(
            HttpMethod.Delete,
            $"https://api.tidal.com/v1/playlists/{Uri.EscapeDataString(thirdPartyId)}/items/{indexCsv}?countryCode={cc}",
            null,
            ct,
            etag);

        return delRes.Ok
            ? (true, indices.Count, $"已从 Tidal 歌单移除 {indices.Count} 首歌曲")
            : (false, 0, $"从 Tidal 歌单移除失败 (HTTP {delRes.StatusCode})");
    }

    // ---------- YouTube Music Innertube Write ----------

    private static string StripYtmVlPrefix(string? thirdPartyId)
    {
        var raw = (thirdPartyId ?? string.Empty).Trim();
        return raw.StartsWith("VL", StringComparison.OrdinalIgnoreCase) && raw.Length > 2
            ? raw[2..]
            : raw;
    }

    private async Task<PlaylistItem?> CreateYtMusicPlaylistAsync(string name, bool isPrivate, CancellationToken ct)
    {
        var json = await PostYtMusicInnertubeAsync("playlist/create", new
        {
            context = BuildYtmWebRemixContext(),
            title = name,
            privacyStatus = isPrivate ? "PRIVATE" : "UNLISTED"
        }, ct);
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var pid = doc.RootElement.GetStringProp("playlistId");
            if (string.IsNullOrWhiteSpace(pid)) return null;
            return new PlaylistItem
            {
                Source = "ytmusic",
                ThirdPartyId = pid.StartsWith("VL", StringComparison.OrdinalIgnoreCase) ? pid : $"VL{pid}",
                Name = name,
                CreatorName = "YouTube Music",
                TrackCount = 0
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<(bool Success, int ProcessedCount, string Message)> AddYtMusicPlaylistSongsAsync(
        string thirdPartyId, IReadOnlyList<SongItem> songs, CancellationToken ct)
    {
        var videoIds = songs
            .Select(s => ExtractTrackNativeId(s, "ytmusic_"))
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (videoIds.Count == 0)
            return (false, 0, "未找到有效的 YouTube Music videoId");

        if (string.Equals(thirdPartyId, "LM", StringComparison.OrdinalIgnoreCase))
        {
            int ok = 0;
            foreach (var vid in videoIds)
            {
                var r = await PostYtMusicInnertubeAsync("like/like", new
                {
                    context = BuildYtmWebRemixContext(),
                    target = new { videoId = vid }
                }, ct);
                if (!string.IsNullOrWhiteSpace(r)) ok++;
            }
            return ok > 0
                ? (true, ok, $"已点赞收藏到 YouTube Music（{ok} 首）")
                : (false, 0, "添加到 YouTube Music 顶过的音乐失败，请检查登录 Cookie");
        }

        var playlistId = StripYtmVlPrefix(thirdPartyId);
        var json = await PostYtMusicInnertubeAsync("browse/edit_playlist", new
        {
            context = BuildYtmWebRemixContext(),
            playlistId,
            actions = videoIds.Select(vid => new { action = "ACTION_ADD_VIDEO", addedVideoId = vid }).ToArray()
        }, ct);

        if (!string.IsNullOrWhiteSpace(json))
            return (true, videoIds.Count, $"已添加到 YouTube Music 歌单（{videoIds.Count} 首）");

        return (false, 0, "添加到 YouTube Music 歌单失败，请检查登录 Cookie");
    }

    private async Task<(bool Success, int ProcessedCount, string Message)> RemoveYtMusicPlaylistSongsAsync(
        PlaylistItem playlist, IReadOnlyList<SongItem> songsToRemove, CancellationToken ct)
    {
        if (string.Equals(playlist.ThirdPartyId, "LM", StringComparison.OrdinalIgnoreCase))
        {
            int ok = 0;
            foreach (var s in songsToRemove)
            {
                var vid = ExtractTrackNativeId(s, "ytmusic_");
                if (string.IsNullOrWhiteSpace(vid)) continue;
                var r = await PostYtMusicInnertubeAsync("like/removelike", new
                {
                    context = BuildYtmWebRemixContext(),
                    target = new { videoId = vid }
                }, ct);
                if (!string.IsNullOrWhiteSpace(r)) ok++;
            }
            return ok > 0
                ? (true, ok, $"已从 YouTube Music 顶过的音乐移除 {ok} 首")
                : (false, 0, "从 YouTube Music 顶过的音乐移除失败");
        }

        var pairs = new List<(string VideoId, string SetVideoId)>();
        var unresolved = new List<SongItem>();
        foreach (var s in songsToRemove)
        {
            var vid = ExtractTrackNativeId(s, "ytmusic_");
            if (string.IsNullOrWhiteSpace(vid)) continue;
            if (!string.IsNullOrWhiteSpace(s.YtmSetVideoId))
                pairs.Add((vid, s.YtmSetVideoId.Trim()));
            else
                unresolved.Add(s);
        }

        if (unresolved.Count > 0)
        {
            var detail = await GetPlaylistDetailAsync(playlist, ct);
            if (detail?.Tracks != null)
            {
                foreach (var u in unresolved)
                {
                    var uVid = ExtractTrackNativeId(u, "ytmusic_");
                    var matched = detail.Tracks.FirstOrDefault(t =>
                        !string.IsNullOrWhiteSpace(t.YtmSetVideoId) &&
                        string.Equals(ExtractTrackNativeId(t, "ytmusic_"), uVid, StringComparison.OrdinalIgnoreCase));
                    if (matched != null)
                        pairs.Add((uVid, matched.YtmSetVideoId!.Trim()));
                }
            }
        }

        if (pairs.Count == 0)
            return (false, 0, "未获取到 YouTube Music 曲目 setVideoId，无法从歌单移除");

        var playlistId = StripYtmVlPrefix(playlist.ThirdPartyId);
        var json = await PostYtMusicInnertubeAsync("browse/edit_playlist", new
        {
            context = BuildYtmWebRemixContext(),
            playlistId,
            actions = pairs.Select(p => new
            {
                action = "ACTION_REMOVE_VIDEO",
                removedVideoId = p.VideoId,
                setVideoId = p.SetVideoId
            }).ToArray()
        }, ct);

        return !string.IsNullOrWhiteSpace(json)
            ? (true, pairs.Count, $"已从 YouTube Music 歌单移除 {pairs.Count} 首歌曲")
            : (false, 0, "从 YouTube Music 歌单移除失败");
    }

    // ---------- 跨平台自建歌单删除实现 ----------

    private void CaptureKugouTotalVer(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object)
        {
            long tv = dataEl.GetLongProp("total_ver");
            if (tv > 0)
            {
                _lastKugouTotalVer = tv;
                return;
            }
        }

        long rootTv = root.GetLongProp("total_ver");
        if (rootTv > 0) _lastKugouTotalVer = rootTv;
    }

    private static string TryDecryptKugouCloudResponse(byte[] rawBytes, byte[] aesKey, byte[] aesIv)
    {
        if (rawBytes.Length == 0) return string.Empty;

        // 优先尝试解密网关直接返回的原始二进制 AES-128-CBC 密文
        if (rawBytes.Length % 16 == 0)
        {
            try
            {
                using var aes = global::System.Security.Cryptography.Aes.Create();
                aes.Mode = global::System.Security.Cryptography.CipherMode.CBC;
                aes.Padding = global::System.Security.Cryptography.PaddingMode.PKCS7;
                aes.Key = aesKey;
                aes.IV = aesIv;
                var plainBytes = aes.CreateDecryptor().TransformFinalBlock(rawBytes, 0, rawBytes.Length);
                var decoded = Encoding.UTF8.GetString(plainBytes).Trim('\0', ' ', '\r', '\n');
                if (decoded.StartsWith('{') || decoded.StartsWith('['))
                {
                    return decoded;
                }
            }
            catch
            {
            }
        }

        var rawText = Encoding.UTF8.GetString(rawBytes).Trim();
        if (rawText.StartsWith('{') || rawText.StartsWith('['))
        {
            return rawText;
        }

        // 回退尝试解密 Base64 编码的 AES 密文
        try
        {
            var b64Bytes = Convert.FromBase64String(rawText);
            if (b64Bytes.Length > 0 && b64Bytes.Length % 16 == 0)
            {
                using var aes = global::System.Security.Cryptography.Aes.Create();
                aes.Mode = global::System.Security.Cryptography.CipherMode.CBC;
                aes.Padding = global::System.Security.Cryptography.PaddingMode.PKCS7;
                aes.Key = aesKey;
                aes.IV = aesIv;
                var plainBytes = aes.CreateDecryptor().TransformFinalBlock(b64Bytes, 0, b64Bytes.Length);
                var decoded = Encoding.UTF8.GetString(plainBytes).Trim('\0', ' ', '\r', '\n');
                if (decoded.StartsWith('{') || decoded.StartsWith('['))
                {
                    return decoded;
                }
            }
        }
        catch
        {
        }

        return rawText;
    }

    /// <summary>
    /// 调用酷狗 /v2/delete_list（RSA + AES-128-CBC 加密协议）删除指定云歌单
    /// </summary>
    private async Task<(bool Ok, JsonElement Root, string ErrMsg)> PostKugouEncryptedDeleteListAsync(
        (string UserId, string Token, string Nickname, string Dfid, string Mid, string AppId) creds,
        long listId,
        long totalVer,
        CancellationToken ct)
    {
        const string standardPublicKeyPem =
            "-----BEGIN PUBLIC KEY-----\nMIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDIAG7QOELSYoIJvTFJhMpe1s/gbjDJX51HBNnEl5HXqTW6lQ7LC8jr9fWZTwusknp+sVGzwd40MwP6U5yDE27M/X1+UR4tvOGOqp94TJtQ1EPnWGWXngpeIW5GxoQGao1rmYWAu6oi1z9XkChrsUdC6DJE5E221wf/4WLFxwAtRQIDAQAB\n-----END PUBLIC KEY-----";
        const string litePublicKeyPem =
            "-----BEGIN PUBLIC KEY-----\nMIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDECi0Np2UR87scwrvTr72L6oO01rBbbBPriSDFPxr3Z5syug0O24QyQO8bg27+0+4kBzTBTBOZ/WWU0WryL1JSXRTXLgFVxtzIY41Pe7lPOgsfTCn5kZcvKhYKJesKnnJDNr5/abvTGf+rHG3YRwsCHcQ08/q6ifSioBszvb3QiwIDAQAB\n-----END PUBLIC KEY-----";

        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var seedChars = new char[6];
        for (int i = 0; i < seedChars.Length; i++)
        {
            seedChars[i] = alphabet[global::System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)];
        }
        var seed = new string(seedChars);

        var seedMd5 = Convert.ToHexString(global::System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
        var aesKey = Encoding.UTF8.GetBytes(seedMd5[..16]);
        var aesIv = Encoding.UTF8.GetBytes(seedMd5[16..32]);

        var plainJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["listid"] = listId,
            ["total_ver"] = totalVer,
            ["type"] = 0
        });

        string encBodyBase64;
        using (var aes = global::System.Security.Cryptography.Aes.Create())
        {
            aes.Mode = global::System.Security.Cryptography.CipherMode.CBC;
            aes.Padding = global::System.Security.Cryptography.PaddingMode.PKCS7;
            aes.Key = aesKey;
            aes.IV = aesIv;
            var plainBytes = Encoding.UTF8.GetBytes(plainJson);
            var cipherBytes = aes.CreateEncryptor().TransformFinalBlock(plainBytes, 0, plainBytes.Length);
            encBodyBase64 = Convert.ToBase64String(cipherBytes);
        }

        object uidObj = long.TryParse(creds.UserId, out long numUid) ? numUid : creds.UserId;
        var portraitPlainJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["aes"] = seed,
            ["uid"] = uidObj,
            ["token"] = creds.Token
        });

        var configs = new List<(string AppId, string ClientVer, string Salt, string PublicKeyPem)>
        {
            ("1005", "20489", "OIlwieks28dk2k092lksi2UIkp", standardPublicKeyPem),
            ("3116", "11440", "LnT6xpN3khm36zse0QzvmgTZ3waWdRSA", litePublicKeyPem)
        };
        if (string.Equals(creds.AppId, "3116", StringComparison.OrdinalIgnoreCase))
        {
            configs.Reverse();
        }

        string lastErr = string.Empty;
        foreach (var cfg in configs)
        {
            try
            {
                using var rsa = global::System.Security.Cryptography.RSA.Create();
                rsa.ImportFromPem(cfg.PublicKeyPem);
                var pBytes = rsa.Encrypt(
                    Encoding.UTF8.GetBytes(portraitPlainJson),
                    global::System.Security.Cryptography.RSAEncryptionPadding.Pkcs1);
                var pHex = Convert.ToHexString(pBytes).ToUpperInvariant();

                var clientTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                var key = Convert.ToHexString(
                    global::System.Security.Cryptography.MD5.HashData(
                        Encoding.UTF8.GetBytes($"{cfg.AppId}{cfg.Salt}{cfg.ClientVer}{clientTime}")))
                    .ToLowerInvariant();

                var qParams = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["appid"] = cfg.AppId,
                    ["clienttime"] = clientTime,
                    ["clientver"] = cfg.ClientVer,
                    ["dfid"] = creds.Dfid,
                    ["key"] = key,
                    ["last_area"] = "gztx",
                    ["last_time"] = clientTime,
                    ["mid"] = creds.Mid,
                    ["p"] = pHex,
                    ["token"] = creds.Token,
                    ["userid"] = creds.UserId,
                    ["uuid"] = "-"
                };

                var sig = ComputeKugouGatewaySignature(qParams, encBodyBase64, cfg.Salt);
                var query = string.Join("&", qParams.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
                var url = $"https://gateway.kugou.com/v2/delete_list?{query}&signature={sig}";

                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(encBodyBase64, Encoding.UTF8, "application/json")
                };
                req.Headers.TryAddWithoutValidation("x-router", "cloudlist.service.kugou.com");
                req.Headers.TryAddWithoutValidation("dfid", creds.Dfid);
                req.Headers.TryAddWithoutValidation("mid", creds.Mid);
                req.Headers.TryAddWithoutValidation("clienttime", clientTime);
                req.Headers.TryAddWithoutValidation("kg-rc", "1");
                await _platformAuth.ApplyAuthHeadersAsync(AuthMusicPlatform.KuGou, req, ct);

                using var resp = await _authedHttpClient.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) continue;

                var rawBytes = await resp.Content.ReadAsByteArrayAsync(ct);
                var decodedJson = TryDecryptKugouCloudResponse(rawBytes, aesKey, aesIv);
                if (IsKugouGatewayOk(decodedJson, out var root, out var errMsg))
                {
                    return (true, root, string.Empty);
                }
                if (!string.IsNullOrWhiteSpace(errMsg)) lastErr = errMsg;
            }
            catch
            {
            }
        }

        return (false, default, lastErr);
    }

    private async Task<(bool Success, string Message)> DeleteKugouPlaylistAsync(PlaylistItem playlist, CancellationToken ct)
    {
        var session = _platformAuth.TryGetSession(AuthMusicPlatform.KuGou);
        var creds = ParseKugouCompositeCookie(session);
        if (string.IsNullOrWhiteSpace(creds.UserId) || string.IsNullOrWhiteSpace(creds.Token))
            return (false, "请先在个人中心登录酷狗账号");

        var listIdStr = StripKugouCloudListId(playlist.ThirdPartyId);
        if (string.IsNullOrWhiteSpace(listIdStr) && playlist.Id > 0)
            listIdStr = playlist.Id.ToString();
        if (!long.TryParse(listIdStr, out long listId) || listId <= 0)
            return (false, "仅支持删除本人酷狗云歌单");

        // 酷狗云歌单集合级删单接口强制校验 total_ver，优先使用当前同步到的最新版本号
        long currentVer = _lastKugouTotalVer > 0 ? _lastKugouTotalVer : await FetchKugouTotalVerAsync(ct);
        long[] candidateVers = currentVer > 0 ? [currentVer, 0L] : [0L];

        string lastErr = string.Empty;
        foreach (long ver in candidateVers)
        {
            // 通道 A：明文 /v3/delete_list（使用必填 total_ver 替代错误的 list_ver）
            var v3Body = new Dictionary<string, object>
            {
                ["listid"] = listId,
                ["total_ver"] = ver,
                ["type"] = 0
            };
            var (v3OkJson, v3ErrJson) = await PostKugouCloudListGatewayWithErrorAsync("/v3/delete_list", v3Body, ct);
            if (IsKugouGatewayOk(v3OkJson, out var rootV3, out _))
            {
                CaptureKugouTotalVer(rootV3);
                return (true, $"已删除酷狗歌单《{playlist.Name}》");
            }
            if (!IsKugouGatewayOk(v3ErrJson, out _, out var v3ErrMsg) && !string.IsNullOrWhiteSpace(v3ErrMsg))
            {
                lastErr = v3ErrMsg;
            }

            // 通道 B：加密 /v2/delete_list（RSA + AES-128-CBC 加密请求与解密回包）
            var (v2Ok, rootV2, v2ErrMsg) = await PostKugouEncryptedDeleteListAsync(creds, listId, ver, ct);
            if (v2Ok)
            {
                CaptureKugouTotalVer(rootV2);
                return (true, $"已删除酷狗歌单《{playlist.Name}》");
            }
            if (!string.IsNullOrWhiteSpace(v2ErrMsg))
            {
                lastErr = v2ErrMsg;
            }
        }

        // 回读兜底：若接口实际删单成功但响应格式异常，核实云端歌单列表已移除后按成功处理
        try
        {
            var (_, latestCreated, latestCollected) = await GetKuGouUserLibraryAsync(creds.Nickname, ct);
            if ((latestCreated.Count > 0 || latestCollected.Count > 0) &&
                !latestCreated.Any(p => string.Equals(StripKugouCloudListId(p.ThirdPartyId), listIdStr, StringComparison.OrdinalIgnoreCase) || p.Id == listId))
            {
                return (true, $"已删除酷狗歌单《{playlist.Name}》");
            }
        }
        catch
        {
        }

        if (lastErr.Contains("20017", StringComparison.OrdinalIgnoreCase))
            return (false, "酷狗登录状态已失效或网页端令牌权限受限 (20017)，请在个人中心重新扫码登录酷狗音乐");

        return (false, string.IsNullOrWhiteSpace(lastErr) ? "删除酷狗歌单失败" : $"酷狗删单失败: {lastErr}");
    }

    private async Task<(bool Success, string Message)> DeleteMiguPlaylistAsync(PlaylistItem playlist, CancellationToken ct)
    {
        var pid = (playlist.ThirdPartyId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(pid) && playlist.Id > 0) pid = playlist.Id.ToString();
        if (string.IsNullOrWhiteSpace(pid))
            return (false, "未找到咪咕歌单 ID");

        var json = await SendMiguGetAsync(
            $"https://app.c.nf.migu.cn/pc/v1.0/user/deleteMusicList.do?channel=23&id={Uri.EscapeDataString(pid)}",
            ct);
        if (IsMiguApiSuccess(json, out _, out var errMsg))
            return (true, $"已删除咪咕歌单《{playlist.Name}》");

        return (false, string.IsNullOrWhiteSpace(errMsg) ? "删除咪咕歌单失败" : $"咪咕删单失败: {errMsg}");
    }

    private async Task<(bool Success, string Message)> DeleteSpotifyPlaylistAsync(PlaylistItem playlist, CancellationToken ct)
    {
        var bearer = await EnsureAuthedSpotifyBearerAsync(ct);
        if (string.IsNullOrWhiteSpace(bearer))
            return (false, "请先在个人中心登录 Spotify");

        var pid = (playlist.ThirdPartyId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(pid))
            return (false, "未找到 Spotify 歌单 ID");

        var res = await SendSpotifyApiAsync(
            HttpMethod.Delete,
            $"https://api.spotify.com/v1/playlists/{Uri.EscapeDataString(pid)}/followers",
            null,
            bearer,
            ct);
        return res.Ok
            ? (true, $"已删除 Spotify 歌单《{playlist.Name}》")
            : (false, $"删除 Spotify 歌单失败 (HTTP {res.StatusCode})");
    }

    private async Task<(bool Success, string Message)> DeleteApplePlaylistAsync(PlaylistItem playlist, CancellationToken ct)
    {
        var devToken = await EnsureAppleDeveloperTokenForLibraryAsync(ct);
        var session = _platformAuth.TryGetSession(AuthMusicPlatform.AppleMusic);
        if (string.IsNullOrWhiteSpace(devToken) || session == null || string.IsNullOrWhiteSpace(session.AccessToken))
            return (false, "请先在个人中心登录 Apple Music 并授权 Music-User-Token");

        var pid = (playlist.ThirdPartyId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(pid))
            return (false, "未找到 Apple Music 歌单 ID");

        int lastCode = 0;
        foreach (var host in new[] { "amp-api.music.apple.com", "api.music.apple.com" })
        {
            var res = await SendAppleMusicLibraryWriteAsync(
                HttpMethod.Delete,
                $"https://{host}/v1/me/library/playlists/{Uri.EscapeDataString(pid)}",
                null,
                devToken,
                ct);
            if (res.Ok)
                return (true, $"已删除 Apple Music 歌单《{playlist.Name}》");
            lastCode = res.StatusCode;
        }

        return (false, lastCode is 401 or 403 or 404 or 405
            ? "Apple Music 官方网页接口限制第三方删除歌单，请在 Apple Music 客户端操作"
            : $"删除 Apple Music 歌单失败 (HTTP {lastCode})");
    }

    private async Task<(bool Success, string Message)> DeleteDeezerPlaylistAsync(PlaylistItem playlist, CancellationToken ct)
    {
        var gw = await GetDeezerGwSessionAsync(ct);
        if (gw == null || string.IsNullOrWhiteSpace(gw.Value.ApiToken))
            return (false, "请先在个人中心登录 Deezer");

        var pid = (playlist.ThirdPartyId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(pid) && playlist.Id > 0) pid = playlist.Id.ToString();
        if (string.IsNullOrWhiteSpace(pid) ||
            (!string.IsNullOrWhiteSpace(gw.Value.LovedTracksId) &&
             string.Equals(pid, gw.Value.LovedTracksId, StringComparison.OrdinalIgnoreCase)))
        {
            return (false, "Deezer 系统喜欢歌单不支持删除");
        }

        var body = JsonSerializer.Serialize(new { playlist_id = pid });
        var json = await PostDeezerGwAsync("playlist.delete", gw.Value.ApiToken, body, ct);
        return IsDeezerGwSuccess(json, out _)
            ? (true, $"已删除 Deezer 歌单《{playlist.Name}》")
            : (false, "删除 Deezer 歌单失败");
    }

    private async Task<(bool Success, string Message)> DeleteTidalPlaylistAsync(PlaylistItem playlist, CancellationToken ct)
    {
        await EnsureTidalSessionInfoAsync(ct);
        if (_tidalSessionUserId <= 0)
            return (false, "请先在个人中心登录 Tidal");

        var uuid = (playlist.ThirdPartyId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(uuid))
            return (false, "未找到 Tidal 歌单 UUID");

        var cc = Uri.EscapeDataString(string.IsNullOrWhiteSpace(_tidalSessionCountryCode) ? "US" : _tidalSessionCountryCode);
        var metaRes = await SendTidalWriteAsync(
            HttpMethod.Get,
            $"https://api.tidal.com/v1/playlists/{Uri.EscapeDataString(uuid)}?countryCode={cc}",
            null,
            ct);
        var etag = string.IsNullOrWhiteSpace(metaRes.ETag) ? "*" : metaRes.ETag;

        var delRes = await SendTidalWriteAsync(
            HttpMethod.Delete,
            $"https://api.tidal.com/v1/playlists/{Uri.EscapeDataString(uuid)}?countryCode={cc}",
            null,
            ct,
            etag);
        return delRes.Ok
            ? (true, $"已删除 Tidal 歌单《{playlist.Name}》")
            : (false, $"删除 Tidal 歌单失败 (HTTP {delRes.StatusCode})");
    }

    private async Task<(bool Success, string Message)> DeleteYtMusicPlaylistAsync(PlaylistItem playlist, CancellationToken ct)
    {
        if (!_platformAuth.HasValidSession(AuthMusicPlatform.YouTubeMusic))
            return (false, "请先在个人中心登录 YouTube Music");

        var playlistId = StripYtmVlPrefix(playlist.ThirdPartyId);
        if (string.IsNullOrWhiteSpace(playlistId) || string.Equals(playlistId, "LM", StringComparison.OrdinalIgnoreCase))
            return (false, "该 YouTube Music 歌单不支持删除");

        var json = await PostYtMusicInnertubeAsync("playlist/delete", new
        {
            context = BuildYtmWebRemixContext(),
            playlistId
        }, ct);
        if (string.IsNullOrWhiteSpace(json))
            return (false, "删除 YouTube Music 歌单失败，请检查登录 Cookie");

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out _))
                return (false, "删除 YouTube Music 歌单被拒绝");
        }
        catch
        {
        }

        return (true, $"已删除 YouTube Music 歌单《{playlist.Name}》");
    }
}
