using System.Net;
using System.Text.Json;
using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.System;

namespace AlgerMusicPlayer.WinUI.Services.Api;

/// <summary>
/// 网易云音乐纯 C# 客户端（内置 WeAPI/EAPI 加密直连 + 兼容外部 NeteaseCloudMusicApi 服务）
/// </summary>
public class NeteaseApiClient
{
    private readonly ConfigService _configService;
    private HttpClient _httpClient;
    private readonly string _deviceId = GenerateHexString(52, uppercase: true);
    private readonly string _ntesNuid = GenerateHexString(64, uppercase: false);
    private readonly string _wnmcid = GenerateWnmcid();
    private string _anonymousMusicA = string.Empty;
    private bool _isRegisteringAnonymous;

    public NeteaseApiClient(ConfigService configService)
    {
        _configService = configService;
        _httpClient = CreateHttpClient();
        _configService.SettingsChanged += () => _httpClient = CreateHttpClient();
    }

    private static string GenerateHexString(int length, bool uppercase)
    {
        var bytes = new byte[(length + 1) / 2];
        Random.Shared.NextBytes(bytes);
        var hex = uppercase ? Convert.ToHexString(bytes) : Convert.ToHexStringLower(bytes);
        return hex[..length];
    }

    private static string GenerateWnmcid()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz";
        Span<char> prefix = stackalloc char[6];
        for (int i = 0; i < 6; i++)
        {
            prefix[i] = chars[Random.Shared.Next(chars.Length)];
        }
        return $"{new string(prefix)}.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.01.0";
    }

    private HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false
        };

        var proxy = _configService.Settings.Proxy;
        if (proxy.Enable && !string.IsNullOrWhiteSpace(proxy.Host) && proxy.Port > 0)
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy($"{proxy.Protocol}://{proxy.Host}:{proxy.Port}");
        }

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Edg/124.0.0.0");
        client.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com");
        return client;
    }

    /// <summary>
    /// 发送 WeAPI 加密请求（若启用自定义外部 API 则可回退）
    /// </summary>
    public async Task<JsonDocument?> PostWeApiAsync(string weapiPath, object payload, string? customFallbackEndpoint = null)
    {
        try
        {
            if (_configService.Settings.UseCustomApiServer && !string.IsNullOrWhiteSpace(customFallbackEndpoint))
            {
                var baseUrl = _configService.Settings.CustomApiBaseUrl.TrimEnd('/');
                var resp = await SendWithHeadersAsync(HttpMethod.Get, $"{baseUrl}{customFallbackEndpoint}");
                if (resp != null) return resp;
            }

            var json = JsonSerializer.Serialize(payload);
            var formFields = NeteaseCrypto.EncryptWeApi(json);
            var url = $"https://music.163.com/weapi/{weapiPath.TrimStart('/')}";

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(formFields)
            };
            AttachCookiesAndHeaders(req, isEapi: false);

            using var response = await _httpClient.SendAsync(req);
            var content = await response.Content.ReadAsStringAsync();
            return JsonDocument.Parse(content);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 发送 EAPI 加密请求（1:1 对齐 netease-cloud-music-api-alger util/request.js，将 MUSIC_U / MUSIC_A 注入加密 header）
    /// </summary>
    public async Task<JsonDocument?> PostEApiAsync(string apiPath, Dictionary<string, object?> payload)
    {
        try
        {
            string rawCookie = _configService.Settings.NeteaseCookie ?? string.Empty;
            string musicU = ExtractCookieValue(rawCookie, "MUSIC_U");
            string csrf = ExtractCookieValue(rawCookie, "__csrf");
            string musicA = ExtractCookieValue(rawCookie, "MUSIC_A");
            if (string.IsNullOrWhiteSpace(musicA))
            {
                musicA = _anonymousMusicA;
            }

            var header = new Dictionary<string, string>
            {
                ["osver"] = "Microsoft-Windows-10-Professional-build-19045-64bit",
                ["deviceId"] = _deviceId,
                ["os"] = "pc",
                ["appver"] = "3.1.17.204416",
                ["versioncode"] = "140",
                ["mobilename"] = "",
                ["buildver"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ["resolution"] = "1920x1080",
                ["__csrf"] = csrf,
                ["channel"] = "netease",
                ["requestId"] = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Random.Shared.Next(1000, 9999)}"
            };

            if (!string.IsNullOrWhiteSpace(musicU))
            {
                header["MUSIC_U"] = musicU;
            }
            else if (!string.IsNullOrWhiteSpace(musicA))
            {
                header["MUSIC_A"] = musicA;
            }

            payload["header"] = header;
            payload["e_r"] = false;
            var json = JsonSerializer.Serialize(payload);
            var formFields = NeteaseCrypto.EncryptEApi(apiPath, json);

            var eapiRoute = apiPath.StartsWith("/api/") ? apiPath["/api/".Length..] : apiPath.TrimStart('/');
            var url = $"https://interface3.music.163.com/eapi/{eapiRoute}";

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(formFields)
            };
            AttachCookiesAndHeaders(req, isEapi: true);

            using var response = await _httpClient.SendAsync(req);
            var content = await response.Content.ReadAsStringAsync();
            return JsonDocument.Parse(content);
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractCookieValue(string rawCookie, string key)
    {
        if (string.IsNullOrWhiteSpace(rawCookie)) return string.Empty;
        foreach (var segment in rawCookie.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = segment.Trim();
            var eqIdx = trimmed.IndexOf('=');
            if (eqIdx <= 0) continue;
            var k = trimmed[..eqIdx].Trim();
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(eqIdx + 1)..].Trim();
            }
        }
        return string.Empty;
    }

    private async Task<JsonDocument?> SendWithHeadersAsync(HttpMethod method, string url)
    {
        try
        {
            using var req = new HttpRequestMessage(method, url);
            AttachCookiesAndHeaders(req, isEapi: false);
            using var resp = await _httpClient.SendAsync(req);
            var str = await resp.Content.ReadAsStringAsync();
            return JsonDocument.Parse(str);
        }
        catch
        {
            return null;
        }
    }

    private void AttachCookiesAndHeaders(HttpRequestMessage req, bool isEapi, bool isLoginEndpoint = false)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cookieParts = new List<string>
        {
            "__remember_me=true",
            "ntes_kaola_ad=1",
            $"_ntes_nuid={_ntesNuid}",
            $"_ntes_nnid={_ntesNuid},{nowMs}",
            $"WNMCID={_wnmcid}",
            "WEVNSM=1.0.0",
            "osver=Microsoft-Windows-10-Professional-build-19045-64bit",
            $"deviceId={_deviceId}",
            "os=pc",
            "channel=netease",
            "appver=3.1.17.204416"
        };

        if (!isLoginEndpoint)
        {
            cookieParts.Add($"NMTID={GenerateHexString(32, uppercase: false)}");
        }

        bool hasMusicU = false;
        if (!string.IsNullOrWhiteSpace(_configService.Settings.NeteaseCookie))
        {
            var userCookie = _configService.Settings.NeteaseCookie.Trim();
            cookieParts.Add(userCookie);
            hasMusicU = userCookie.Contains("MUSIC_U=", StringComparison.OrdinalIgnoreCase);
        }

        if (!hasMusicU && !string.IsNullOrWhiteSpace(_anonymousMusicA))
        {
            cookieParts.Add($"MUSIC_A={_anonymousMusicA}");
        }

        req.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookieParts));

        if (_configService.Settings.EnableRealIP && !string.IsNullOrWhiteSpace(_configService.Settings.RealIP))
        {
            req.Headers.TryAddWithoutValidation("X-Real-IP", _configService.Settings.RealIP);
            req.Headers.TryAddWithoutValidation("X-Forwarded-For", _configService.Settings.RealIP);
        }

        if (isEapi)
        {
            req.Headers.UserAgent.Clear();
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Safari/537.36 Chrome/91.0.4472.164 NeteaseMusicDesktop/3.0.18.203152");
        }
    }

    /// <summary>
    /// 对应 netease-cloud-music-api-alger 的 register_anonimous.js：注册匿名设备获取 MUSIC_A 令牌，防止触发风控
    /// </summary>
    public async Task EnsureAnonymousTokenAsync()
    {
        if (!string.IsNullOrWhiteSpace(_anonymousMusicA) || _isRegisteringAnonymous) return;
        _isRegisteringAnonymous = true;
        try
        {
            const string xorKey = "3go8&$8*3*3h0k(2)2";
            var xoredChars = new char[_deviceId.Length];
            for (int i = 0; i < _deviceId.Length; i++)
            {
                xoredChars[i] = (char)(_deviceId[i] ^ xorKey[i % xorKey.Length]);
            }
            var xoredBytes = global::System.Text.Encoding.UTF8.GetBytes(new string(xoredChars));
            var md5Bytes = global::System.Security.Cryptography.MD5.HashData(xoredBytes);
            var digestBase64 = Convert.ToBase64String(md5Bytes);
            var rawUsername = $"{_deviceId} {digestBase64}";
            var encodedUsername = Convert.ToBase64String(global::System.Text.Encoding.UTF8.GetBytes(rawUsername));

            var json = JsonSerializer.Serialize(new { username = encodedUsername, csrf_token = "" });
            var formFields = NeteaseCrypto.EncryptWeApi(json);
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://music.163.com/weapi/register/anonimous")
            {
                Content = new FormUrlEncodedContent(formFields)
            };
            AttachCookiesAndHeaders(req, isEapi: false, isLoginEndpoint: true);

            using var resp = await _httpClient.SendAsync(req);
            if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var raw in setCookies)
                {
                    var firstPair = raw.Split(';')[0].Trim();
                    if (firstPair.StartsWith("MUSIC_A=", StringComparison.OrdinalIgnoreCase))
                    {
                        _anonymousMusicA = firstPair["MUSIC_A=".Length..].Trim();
                        break;
                    }
                }
            }
        }
        catch
        {
            // 忽略匿名令牌预注册异常
        }
        finally
        {
            _isRegisteringAnonymous = false;
        }
    }

    // ==================== 核心业务 API ====================

    /// <summary>
    /// 获取首页轮播图 Banner
    /// </summary>
    public async Task<List<BannerItem>> GetBannersAsync()
    {
        var list = new List<BannerItem>();
        using var doc = await PostWeApiAsync("banner/get", new { clientType = "pc" }, "/banner?type=0");
        if (doc == null) return list;

        if (doc.RootElement.TryGetProperty("banners", out var banners) && banners.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in banners.EnumerateArray())
            {
                list.Add(new BannerItem
                {
                    ImageUrl = b.GetStringProp("imageUrl", b.GetStringProp("pic")),
                    TypeTitle = b.GetStringProp("typeTitle"),
                    TargetId = b.GetLongProp("targetId"),
                    TargetType = b.GetIntProp("targetType"),
                    Url = b.GetStringProp("url")
                });
            }
        }
        return list;
    }

    /// <summary>
    /// 获取推荐歌单
    /// </summary>
    public async Task<List<PlaylistItem>> GetRecommendPlaylistsAsync(int limit = 24)
    {
        var list = new List<PlaylistItem>();
        using var doc = await PostWeApiAsync("personalized/playlist", new { limit, total = true, n = 1000 }, $"/personalized?limit={limit}");
        if (doc == null) return list;

        if (doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in result.EnumerateArray())
            {
                list.Add(new PlaylistItem
                {
                    Id = item.GetLongProp("id"),
                    Name = item.GetStringProp("name"),
                    CoverImgUrl = item.GetStringProp("picUrl", item.GetStringProp("coverImgUrl")),
                    PlayCount = item.GetLongProp("playCount"),
                    TrackCount = item.GetIntProp("trackCount"),
                    Description = item.GetStringProp("copywriter")
                });
            }
        }
        return list;
    }

    /// <summary>
    /// 获取推荐新音乐
    /// </summary>
    public async Task<List<SongItem>> GetRecommendNewSongsAsync(int limit = 20)
    {
        var list = new List<SongItem>();
        using var doc = await PostWeApiAsync("personalized/newsong", new { type = "recommend", limit, areaId = 0 }, $"/personalized/newsong?limit={limit}");
        if (doc == null) return list;

        if (doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in result.EnumerateArray())
            {
                if (item.TryGetProperty("song", out var songElem))
                {
                    var song = ParseSongElement(songElem);
                    if (string.IsNullOrWhiteSpace(song.PicUrl))
                    {
                        song.PicUrl = item.GetStringProp("picUrl");
                    }
                    list.Add(song);
                }
            }
        }
        return list;
    }

    /// <summary>
    /// 获取每日推荐歌曲（需登录 Cookie，未登录自动回退推荐新歌）
    /// </summary>
    public async Task<List<SongItem>> GetDailyRecommendSongsAsync()
    {
        var list = new List<SongItem>();
        using var doc = await PostWeApiAsync("v3/discovery/recommend/songs", new { }, "/recommend/songs");
        if (doc != null &&
            doc.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("dailySongs", out var dailySongs) &&
            dailySongs.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in dailySongs.EnumerateArray())
            {
                list.Add(ParseSongElement(s));
            }
        }

        if (list.Count == 0)
        {
            return await GetRecommendNewSongsAsync(30);
        }
        return list;
    }

    /// <summary>
    /// 获取热门歌手列表
    /// </summary>
    public async Task<List<ArtistItem>> GetHotArtistsAsync(int limit = 24, int offset = 0)
    {
        var list = new List<ArtistItem>();
        using var doc = await PostWeApiAsync("artist/top", new { limit, offset, total = true }, $"/top/artists?limit={limit}&offset={offset}");
        if (doc == null) return list;

        if (doc.RootElement.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in artists.EnumerateArray())
            {
                list.Add(ParseArtistElement(a));
            }
        }
        return list;
    }

    /// <summary>
    /// 获取排行榜列表（官方榜 & 全球榜）
    /// </summary>
    public async Task<List<PlaylistItem>> GetToplistsAsync()
    {
        var list = new List<PlaylistItem>();
        using var doc = await PostWeApiAsync("toplist", new { }, "/toplist");
        if (doc == null) return list;

        if (doc.RootElement.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                list.Add(new PlaylistItem
                {
                    Id = item.GetLongProp("id"),
                    Name = item.GetStringProp("name"),
                    CoverImgUrl = item.GetStringProp("coverImgUrl"),
                    Description = item.GetStringProp("description"),
                    PlayCount = item.GetLongProp("playCount"),
                    TrackCount = item.GetIntProp("trackCount"),
                    UpdateFrequency = item.GetStringProp("updateFrequency")
                });
            }
        }
        return list;
    }

    /// <summary>
    /// 获取分类歌单列表（歌单广场）
    /// </summary>
    public async Task<List<PlaylistItem>> GetTopPlaylistsAsync(string category = "全部", int limit = 36, int offset = 0)
    {
        var list = new List<PlaylistItem>();
        using var doc = await PostWeApiAsync(
            "playlist/list",
            new { cat = category, order = "hot", limit, offset, total = true },
            $"/top/playlist?cat={Uri.EscapeDataString(category)}&limit={limit}&offset={offset}");
        if (doc == null) return list;

        if (doc.RootElement.TryGetProperty("playlists", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                list.Add(ParsePlaylistElement(item));
            }
        }
        return list;
    }

    /// <summary>
    /// 获取歌单详情（含完整曲目列表）
    /// </summary>
    public async Task<PlaylistItem?> GetPlaylistDetailAsync(long playlistId)
    {
        using var doc = await PostWeApiAsync(
            "v6/playlist/detail",
            new { id = playlistId, n = 100000, s = 8 },
            $"/playlist/detail?id={playlistId}");
        if (doc == null || !doc.RootElement.TryGetProperty("playlist", out var plElem))
        {
            return null;
        }

        var playlist = ParsePlaylistElement(plElem);

        // 若 trackIds 数量大于 tracks 返回数量，分批拉取完整歌曲详情
        var trackIds = new List<long>();
        if (plElem.TryGetProperty("trackIds", out var trackIdsElem) && trackIdsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var tid in trackIdsElem.EnumerateArray())
            {
                var id = tid.GetLongProp("id");
                if (id > 0) trackIds.Add(id);
            }
        }

        if (trackIds.Count > playlist.Tracks.Count)
        {
            var fullSongs = await GetSongDetailsAsync(trackIds.Take(500));
            if (fullSongs.Count > 0)
            {
                playlist.Tracks = fullSongs;
            }
        }

        return playlist;
    }

    /// <summary>
    /// 创建新歌单（对应原 PlaylistDrawer.vue createPlaylist）
    /// </summary>
    public async Task<PlaylistItem?> CreatePlaylistAsync(string name, bool isPrivate = false)
    {
        using var doc = await PostWeApiAsync(
            "playlist/create",
            new { name, privacy = isPrivate ? "10" : "0", type = "NORMAL" },
            "/playlist/create");
        if (doc != null && doc.RootElement.GetIntProp("code") == 200)
        {
            if (doc.RootElement.TryGetProperty("playlist", out var plElem) && plElem.ValueKind == JsonValueKind.Object)
            {
                return ParsePlaylistElement(plElem);
            }
            var id = doc.RootElement.GetLongProp("id");
            if (id > 0)
            {
                return new PlaylistItem { Id = id, Name = name };
            }
        }
        return null;
    }

    /// <summary>
    /// 添加或从歌单删除歌曲（op: "add" 或 "del"，对应原 updatePlaylistTracks）
    /// </summary>
    public async Task<bool> UpdatePlaylistTracksAsync(string op, long playlistId, IEnumerable<long> songIds)
    {
        var idsArr = songIds.Distinct().ToArray();
        if (idsArr.Length == 0) return false;
        var trackIdsJson = "[" + string.Join(",", idsArr) + "]";
        using var doc = await PostWeApiAsync(
            "playlist/manipulate/tracks",
            new { op, pid = playlistId, trackIds = trackIdsJson, imme = "true" },
            "/playlist/tracks");
        return doc != null && doc.RootElement.GetIntProp("code") == 200;
    }

    /// <summary>
    /// 收藏或取消收藏歌单（subscribe: true=收藏, false=取消收藏）
    /// </summary>
    public async Task<bool> SubscribePlaylistAsync(long playlistId, bool subscribe)
    {
        var action = subscribe ? "subscribe" : "unsubscribe";
        using var doc = await PostWeApiAsync(
            $"playlist/{action}",
            new { id = playlistId },
            $"/playlist/subscribe?t={(subscribe ? 1 : 2)}&id={playlistId}");
        return doc != null && doc.RootElement.GetIntProp("code") == 200;
    }

    /// <summary>
    /// 批量获取歌曲详情
    /// </summary>
    public async Task<List<SongItem>> GetSongDetailsAsync(IEnumerable<long> songIds)
    {
        var ids = songIds.Distinct().ToList();
        var result = new List<SongItem>();
        if (ids.Count == 0) return result;

        var cParam = "[" + string.Join(",", ids.Select(id => $"{{\"id\":{id}}}")) + "]";
        using var doc = await PostWeApiAsync(
            "v3/song/detail",
            new { c = cParam },
            $"/song/detail?ids={string.Join(",", ids)}");

        if (doc != null && doc.RootElement.TryGetProperty("songs", out var songs) && songs.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in songs.EnumerateArray())
            {
                result.Add(ParseSongElement(s));
            }
        }
        return result;
    }

    /// <summary>
    /// 获取专辑详情及曲目
    /// </summary>
    public async Task<(AlbumItem? Album, List<SongItem> Songs)> GetAlbumDetailAsync(long albumId)
    {
        using var doc = await PostWeApiAsync($"v1/album/{albumId}", new { }, $"/album?id={albumId}");
        var songs = new List<SongItem>();
        if (doc == null) return (null, songs);

        AlbumItem? album = null;
        if (doc.RootElement.TryGetProperty("album", out var alElem))
        {
            album = ParseAlbumElement(alElem);
        }

        if (doc.RootElement.TryGetProperty("songs", out var songsElem) && songsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in songsElem.EnumerateArray())
            {
                var song = ParseSongElement(s);
                if (string.IsNullOrWhiteSpace(song.PicUrl) && album != null)
                {
                    song.PicUrl = album.PicUrl;
                }
                songs.Add(song);
            }
        }
        return (album, songs);
    }

    /// <summary>
    /// 获取歌手详情与热门 50 首歌曲
    /// </summary>
    public async Task<(ArtistItem? Artist, List<SongItem> HotSongs)> GetArtistDetailAsync(long artistId)
    {
        using var doc = await PostWeApiAsync($"v1/artist/{artistId}", new { }, $"/artists?id={artistId}");
        var songs = new List<SongItem>();
        if (doc == null) return (null, songs);

        ArtistItem? artist = null;
        if (doc.RootElement.TryGetProperty("artist", out var arElem))
        {
            artist = ParseArtistElement(arElem);
        }

        if (doc.RootElement.TryGetProperty("hotSongs", out var hotSongsElem) && hotSongsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in hotSongsElem.EnumerateArray())
            {
                songs.Add(ParseSongElement(s));
            }
        }
        return (artist, songs);
    }

    /// <summary>
    /// 搜索功能（type: 1=单曲, 10=专辑, 100=歌手, 1000=歌单, 1004=MV）
    /// </summary>
    public async Task<JsonDocument?> SearchAsync(string keyword, int type = 1, int limit = 30, int offset = 0)
    {
        return await PostWeApiAsync(
            "cloudsearch/get/web",
            new { s = keyword, type, limit, offset, total = true },
            $"/cloudsearch?keywords={Uri.EscapeDataString(keyword)}&type={type}&limit={limit}&offset={offset}");
    }

    /// <summary>
    /// 搜索单曲列表
    /// </summary>
    public async Task<List<SongItem>> SearchSongsAsync(string keyword, int limit = 40, int offset = 0)
    {
        var songs = new List<SongItem>();
        using var doc = await SearchAsync(keyword, 1, limit, offset);
        if (doc != null &&
            doc.RootElement.TryGetProperty("result", out var res) &&
            res.TryGetProperty("songs", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in arr.EnumerateArray())
            {
                songs.Add(ParseSongElement(s));
            }
        }
        return songs;
    }

    /// <summary>
    /// 搜索歌单列表
    /// </summary>
    public async Task<List<PlaylistItem>> SearchPlaylistsAsync(string keyword, int limit = 30, int offset = 0)
    {
        var list = new List<PlaylistItem>();
        using var doc = await SearchAsync(keyword, 1000, limit, offset);
        if (doc != null &&
            doc.RootElement.TryGetProperty("result", out var res) &&
            res.TryGetProperty("playlists", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in arr.EnumerateArray())
            {
                list.Add(ParsePlaylistElement(p));
            }
        }
        return list;
    }

    /// <summary>
    /// 搜索专辑列表
    /// </summary>
    public async Task<List<AlbumItem>> SearchAlbumsAsync(string keyword, int limit = 30, int offset = 0)
    {
        var list = new List<AlbumItem>();
        using var doc = await SearchAsync(keyword, 10, limit, offset);
        if (doc != null &&
            doc.RootElement.TryGetProperty("result", out var res) &&
            res.TryGetProperty("albums", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                list.Add(ParseAlbumElement(a));
            }
        }
        return list;
    }

    /// <summary>
    /// 搜索歌手列表
    /// </summary>
    public async Task<List<ArtistItem>> SearchArtistsAsync(string keyword, int limit = 30, int offset = 0)
    {
        var list = new List<ArtistItem>();
        using var doc = await SearchAsync(keyword, 100, limit, offset);
        if (doc != null &&
            doc.RootElement.TryGetProperty("result", out var res) &&
            res.TryGetProperty("artists", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                list.Add(ParseArtistElement(a));
            }
        }
        return list;
    }

    /// <summary>
    /// 搜索 MV 列表
    /// </summary>
    public async Task<List<MvItem>> SearchMvsAsync(string keyword, int limit = 24, int offset = 0)
    {
        var list = new List<MvItem>();
        using var doc = await SearchAsync(keyword, 1004, limit, offset);
        if (doc != null &&
            doc.RootElement.TryGetProperty("result", out var res) &&
            res.TryGetProperty("mvs", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in arr.EnumerateArray())
            {
                list.Add(ParseMvElement(m));
            }
        }
        return list;
    }

    /// <summary>
    /// 获取热搜榜单
    /// </summary>
    public async Task<List<string>> GetHotSearchKeywordsAsync()
    {
        var list = new List<string>();
        using var doc = await PostWeApiAsync("search/hot/detail", new { }, "/search/hot/detail");
        if (doc != null && doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var word = item.GetStringProp("searchWord");
                if (!string.IsNullOrWhiteSpace(word)) list.Add(word);
            }
        }
        return list;
    }

    /// <summary>
    /// 获取歌曲播放地址（优先 EAPI v1 支持无损/Hi-Res/臻品音质）
    /// </summary>
    public async Task<(string? Url, long Size, int Br, bool IsFreeTrial)> GetSongUrlAsync(long songId, string qualityLevel = "lossless")
    {
        var (url, _, size, br, isTrial) = await GetSongUrlWithTypeAsync(songId, qualityLevel);
        return (url, size, br, isTrial);
    }

    /// <summary>
    /// 获取歌曲播放地址与真实编码类型（type: flac / mp3 / m4a）
    /// </summary>
    public async Task<(string? Url, string Type, long Size, int Br, bool IsFreeTrial)> GetSongUrlWithTypeAsync(long songId, string qualityLevel = "lossless")
    {
        var (url, type, _, size, br, isTrial) = await GetSongUrlWithLevelAsync(songId, qualityLevel);
        return (url, type, size, br, isTrial);
    }

    /// <summary>
    /// 获取歌曲播放地址、真实编码类型及服务器返回的实际音质等级（level: standard/higher/exhigh/lossless/hires/jyeffect/sky/jymaster）
    /// </summary>
    public async Task<(string? Url, string Type, string Level, long Size, int Br, bool IsFreeTrial)> GetSongUrlWithLevelAsync(long songId, string qualityLevel = "lossless")
    {
        var payload = new Dictionary<string, object?>
        {
            ["ids"] = $"[{songId}]",
            ["level"] = qualityLevel,
            ["encodeType"] = "flac"
        };
        if (string.Equals(qualityLevel, "sky", StringComparison.OrdinalIgnoreCase))
        {
            payload["immerseType"] = "c51";
        }

        using var doc = await PostEApiAsync("/api/song/enhance/player/url/v1", payload);
        if (doc != null &&
            doc.RootElement.TryGetProperty("data", out var dataArr) &&
            dataArr.ValueKind == JsonValueKind.Array &&
            dataArr.GetArrayLength() > 0)
        {
            var first = dataArr[0];
            var url = first.GetStringProp("url");
            var type = first.GetStringProp("type");
            var level = first.GetStringProp("level");
            var size = first.GetLongProp("size");
            var br = first.GetIntProp("br");
            bool isTrial = first.TryGetProperty("freeTrialInfo", out var trial) && trial.ValueKind == JsonValueKind.Object;

            if (!string.IsNullOrWhiteSpace(url))
            {
                return (url, type, level, size, br, isTrial);
            }
        }

        return (null, string.Empty, string.Empty, 0, 0, false);
    }

    /// <summary>
    /// 获取歌曲下载直链（1:1 对应 song_download_url_v1.js 与 src/renderer/api/music.ts getMusicUrl(id, isDownloaded=true)）
    /// 优先请求 /api/song/enhance/download/url/v1，再回退 /api/song/enhance/player/url/v1
    /// </summary>
    public async Task<(string? Url, string Type, long Size, int Br, bool IsFreeTrial)> GetSongDownloadUrlAsync(long songId, string qualityLevel = "jymaster")
    {
        try
        {
            var dlPayload = new Dictionary<string, object?>
            {
                ["id"] = songId,
                ["immerseType"] = "c51",
                ["level"] = qualityLevel,
                ["encodeType"] = "flac"
            };

            using var dlDoc = await PostEApiAsync("/api/song/enhance/download/url/v1", dlPayload);
            if (dlDoc != null &&
                dlDoc.RootElement.TryGetProperty("data", out var dataObj) &&
                dataObj.ValueKind == JsonValueKind.Object)
            {
                var url = dataObj.GetStringProp("url");
                var type = dataObj.GetStringProp("type");
                var size = dataObj.GetLongProp("size");
                var br = dataObj.GetIntProp("br");
                bool isTrial = dataObj.TryGetProperty("freeTrialInfo", out var trial) && trial.ValueKind == JsonValueKind.Object;

                if (!string.IsNullOrWhiteSpace(url) && !isTrial)
                {
                    return (url, type, size, br, false);
                }
            }
        }
        catch
        {
            // 下载专用接口异常时回退播放接口
        }

        return await GetSongUrlWithTypeAsync(songId, qualityLevel);
    }

    /// <summary>
    /// 获取歌词（含 LRC、YRC 逐字、翻译 tlyric、罗马音 romalrc）
    /// </summary>
    public async Task<(string Lrc, string TLrc, string RomaLrc, string Yrc)> GetLyricRawAsync(long songId)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = songId,
            ["cp"] = false,
            ["tv"] = 0,
            ["lv"] = 0,
            ["rv"] = 0,
            ["kv"] = 0,
            ["yv"] = 0,
            ["ytv"] = 0,
            ["yrv"] = 0
        };

        using var doc = await PostEApiAsync("/api/song/lyric/v1", payload);
        if (doc == null) return (string.Empty, string.Empty, string.Empty, string.Empty);

        var root = doc.RootElement;
        var lrc = root.TryGetProperty("lrc", out var lrcObj) ? lrcObj.GetStringProp("lyric") : string.Empty;
        var tlrc = root.TryGetProperty("tlyric", out var tlrcObj) ? tlrcObj.GetStringProp("lyric") : string.Empty;
        var roma = root.TryGetProperty("romalrc", out var romaObj) ? romaObj.GetStringProp("lyric") : string.Empty;
        var yrc = root.TryGetProperty("yrc", out var yrcObj) ? yrcObj.GetStringProp("lyric") : string.Empty;

        return (lrc, tlrc, roma, yrc);
    }

    /// <summary>
    /// 获取 MV 列表（1:1 对应 mv_all.js 与 src/renderer/views/mv/index.vue，支持全部/内地/港台/欧美/日本/韩国分页加载）
    /// </summary>
    public async Task<List<MvItem>> GetTopMvsAsync(int limit = 40, int offset = 0, string area = "全部")
    {
        var list = new List<MvItem>();
        string areaTag = string.IsNullOrWhiteSpace(area) ? "全部" : area.Trim();
        string tagsJson = $"{{\"地区\":\"{areaTag}\",\"类型\":\"全部\",\"排序\":\"上升最快\"}}";

        // 1. 优先通过 EAPI 请求 /api/mv/all（与 netease-cloud-music-api-alger/module/mv_all.js 完全一致）
        using (var eapiDoc = await PostEApiAsync("/api/mv/all", new Dictionary<string, object?>
        {
            ["tags"] = tagsJson,
            ["offset"] = offset,
            ["total"] = "true",
            ["limit"] = limit
        }))
        {
            if (eapiDoc != null && eapiDoc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in arr.EnumerateArray())
                {
                    list.Add(ParseMvElement(m));
                }
            }
        }

        if (list.Count > 0) return list;

        // 2. 回退 WeAPI mv/all
        using (var weapiDoc = await PostWeApiAsync(
            "mv/all",
            new { tags = tagsJson, limit, offset, total = "true" },
            $"/mv/all?area={Uri.EscapeDataString(areaTag)}&limit={limit}&offset={offset}"))
        {
            if (weapiDoc != null && weapiDoc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in arr.EnumerateArray())
                {
                    list.Add(ParseMvElement(m));
                }
            }
        }

        if (list.Count > 0) return list;

        // 3. 回退最新 MV 接口 (/api/mv/first)
        string firstArea = areaTag == "全部" ? "" : areaTag;
        using (var firstDoc = await PostEApiAsync("/api/mv/first", new Dictionary<string, object?>
        {
            ["area"] = firstArea,
            ["limit"] = limit,
            ["total"] = true
        }))
        {
            if (firstDoc != null && firstDoc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in arr.EnumerateArray())
                {
                    list.Add(ParseMvElement(m));
                }
            }
        }

        return list;
    }

    /// <summary>
    /// 获取 MV 详情数据（1:1 对应 mv_detail.js）
    /// </summary>
    public async Task<MvItem?> GetMvDetailAsync(long mvId)
    {
        using var doc = await PostWeApiAsync("v1/mv/detail", new { id = mvId }, $"/mv/detail?mvid={mvId}");
        if (doc != null && doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            return ParseMvElement(data);
        }
        return null;
    }

    /// <summary>
    /// 获取 MV 播放流 URL（1:1 对应 mv_url.js，支持 1080/720/480/240 多清晰度自动降级回退）
    /// </summary>
    public async Task<string?> GetMvPlayUrlAsync(long mvId, int resolution = 1080)
    {
        int[] candidateResolutions = resolution switch
        {
            1080 => [1080, 720, 480, 240],
            720 => [720, 480, 240, 1080],
            480 => [480, 240, 720],
            240 => [240, 480],
            _ => [1080, 720, 480, 240]
        };

        foreach (int r in candidateResolutions)
        {
            using var doc = await PostWeApiAsync("song/enhance/play/mv/url", new { id = mvId, r }, $"/mv/url?id={mvId}&r={r}");
            if (doc != null && doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                var url = data.GetStringProp("url");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }

            using var eapiDoc = await PostEApiAsync("/api/song/enhance/play/mv/url", new Dictionary<string, object?>
            {
                ["id"] = mvId,
                ["r"] = r
            });
            if (eapiDoc != null && eapiDoc.RootElement.TryGetProperty("data", out var eData) && eData.ValueKind == JsonValueKind.Object)
            {
                var url = eData.GetStringProp("url");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 获取歌曲热门评论
    /// </summary>
    public async Task<List<CommentItem>> GetSongCommentsAsync(long songId, int limit = 30, int offset = 0)
    {
        var list = new List<CommentItem>();
        using var doc = await PostWeApiAsync(
            $"v1/resource/comments/R_SO_4_{songId}",
            new { rid = songId, limit, offset, beforeTime = 0 },
            $"/comment/music?id={songId}&limit={limit}&offset={offset}");
        if (doc == null) return list;

        var key = doc.RootElement.TryGetProperty("hotComments", out var hot) && hot.ValueKind == JsonValueKind.Array && hot.GetArrayLength() > 0
            ? "hotComments"
            : "comments";

        if (doc.RootElement.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in arr.EnumerateArray())
            {
                var user = c.TryGetProperty("user", out var u) ? u : default;
                list.Add(new CommentItem
                {
                    CommentId = c.GetLongProp("commentId"),
                    Content = c.GetStringProp("content"),
                    Time = c.GetLongProp("time"),
                    TimeStr = c.GetStringProp("timeStr"),
                    LikedCount = c.GetIntProp("likedCount"),
                    UserName = user.ValueKind == JsonValueKind.Object ? user.GetStringProp("nickname") : "云村用户",
                    UserAvatarUrl = user.ValueKind == JsonValueKind.Object ? user.GetStringProp("avatarUrl") : string.Empty
                });
            }
        }
        return list;
    }

    // ==================== 扫码登录与用户接口 ====================

    public async Task<string?> CreateQrKeyAsync()
    {
        using var doc = await PostWeApiAsync("login/qrcode/unikey", new { type = 1 }, "/login/qr/key");
        return doc?.RootElement.GetStringProp("unikey");
    }

    public async Task<(int Code, string Message, string? Cookie)> CheckQrStatusAsync(string key)
    {
        try
        {
            var json = JsonSerializer.Serialize(new { key, type = 1 });
            var formFields = NeteaseCrypto.EncryptWeApi(json);
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://music.163.com/weapi/login/qrcode/client/login")
            {
                Content = new FormUrlEncodedContent(formFields)
            };
            AttachCookiesAndHeaders(req, isEapi: false);

            using var resp = await _httpClient.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var code = doc.RootElement.GetIntProp("code");
            var msg = doc.RootElement.GetStringProp("message");

            string? cookieHeader = null;
            if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                var pairs = setCookies
                    .Select(c => c.Split(';')[0].Trim())
                    .Where(c => !string.IsNullOrWhiteSpace(c));
                cookieHeader = string.Join("; ", pairs);
            }

            return (code, msg, cookieHeader);
        }
        catch (Exception ex)
        {
            return (500, ex.Message, null);
        }
    }

    public async Task<UserProfile?> GetCurrentUserProfileAsync()
    {
        using var doc = await PostWeApiAsync("nuser/account/get", new { }, "/user/account");
        if (doc != null &&
            doc.RootElement.TryGetProperty("profile", out var p) &&
            p.ValueKind == JsonValueKind.Object)
        {
            return new UserProfile
            {
                UserId = p.GetLongProp("userId"),
                Nickname = p.GetStringProp("nickname"),
                AvatarUrl = p.GetStringProp("avatarUrl"),
                BackgroundUrl = p.GetStringProp("backgroundUrl"),
                Signature = p.GetStringProp("signature"),
                VipType = p.GetIntProp("vipType"),
                Follows = p.GetIntProp("follows"),
                Followeds = p.GetIntProp("followeds"),
                PlaylistCount = p.GetIntProp("playlistCount")
            };
        }
        return null;
    }

    public async Task<List<PlaylistItem>> GetUserPlaylistsAsync(long userId, int limit = 50, int offset = 0)
    {
        var list = new List<PlaylistItem>();
        using var doc = await PostWeApiAsync("user/playlist", new { uid = userId, limit, offset, includeVideo = true }, $"/user/playlist?uid={userId}");
        if (doc != null && doc.RootElement.TryGetProperty("playlist", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in arr.EnumerateArray())
            {
                list.Add(ParsePlaylistElement(p));
            }
        }
        return list;
    }

    /// <summary>
    /// 通过 UID 免密获取用户详情（对应原 UidLogin.vue 与 views/user/index.vue）
    /// </summary>
    public async Task<UserProfile?> GetUserDetailByUidAsync(long uid)
    {
        using var doc = await PostWeApiAsync($"v1/user/detail/{uid}", new { }, $"/user/detail?uid={uid}");
        if (doc != null && doc.RootElement.TryGetProperty("profile", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            return new UserProfile
            {
                UserId = p.GetLongProp("userId", uid),
                Nickname = p.GetStringProp("nickname"),
                AvatarUrl = p.GetStringProp("avatarUrl"),
                BackgroundUrl = p.GetStringProp("backgroundUrl"),
                Signature = p.GetStringProp("signature"),
                VipType = p.GetIntProp("vipType"),
                Level = doc.RootElement.GetIntProp("level"),
                ListenSongs = doc.RootElement.GetIntProp("listenSongs"),
                Follows = p.GetIntProp("follows"),
                Followeds = p.GetIntProp("followeds"),
                PlaylistCount = p.GetIntProp("playlistCount")
            };
        }
        return null;
    }

    /// <summary>
    /// 获取用户听歌排行（type: 0=所有时间 allData, 1=最近一周 weekData）
    /// </summary>
    public async Task<List<PlayRecordItem>> GetUserPlayRecordAsync(long uid, int type = 0)
    {
        var list = new List<PlayRecordItem>();
        using var doc = await PostWeApiAsync("v1/play/record", new { uid, type }, $"/user/record?uid={uid}&type={type}");
        if (doc == null) return list;

        var key = type == 1 ? "weekData" : "allData";
        if (doc.RootElement.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            int rank = 1;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.TryGetProperty("song", out var sElem))
                {
                    var song = ParseSongElement(sElem);
                    var playCount = item.GetIntProp("playCount", 1);
                    song.PlayCount = playCount;
                    list.Add(new PlayRecordItem
                    {
                        Rank = rank++,
                        PlayCount = playCount,
                        Score = item.GetIntProp("score"),
                        Song = song
                    });
                }
            }
        }
        return list;
    }

    /// <summary>
    /// 获取用户收藏的专辑列表（对应原 user/index.vue 的收藏专辑 Tab）
    /// </summary>
    public async Task<List<AlbumItem>> GetUserSubscribedAlbumsAsync(int limit = 50, int offset = 0)
    {
        var list = new List<AlbumItem>();
        using var doc = await PostWeApiAsync("album/sublist", new { limit, offset, total = true }, $"/album/sublist?limit={limit}&offset={offset}");
        if (doc != null && doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                list.Add(ParseAlbumElement(a));
            }
        }
        return list;
    }

    /// <summary>
    /// 获取私人 FM 歌曲列表（对应原 HomeHero.vue getPersonalFM）
    /// </summary>
    public async Task<List<SongItem>> GetPersonalFmAsync()
    {
        var list = new List<SongItem>();
        using var doc = await PostWeApiAsync("v1/radio/get", new { }, "/personal_fm");
        if (doc != null && doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in arr.EnumerateArray())
            {
                list.Add(ParseSongElement(s));
            }
        }
        if (list.Count == 0)
        {
            return await GetRecommendNewSongsAsync(6);
        }
        return list;
    }

    /// <summary>
    /// 私人 FM 垃圾桶（不喜欢当前 FM 歌曲）
    /// </summary>
    public async Task<bool> FmTrashAsync(long songId)
    {
        using var doc = await PostWeApiAsync($"v1/radio/trash/add?alg=RT&songId={songId}&time=25", new { songId }, $"/fm_trash?id={songId}");
        return doc != null && doc.RootElement.GetIntProp("code") == 200;
    }

    /// <summary>
    /// 获取新碟上架列表（area: ALL=全部, ZH=华语, EA=欧美, KR=韩国, JP=日本）
    /// </summary>
    public async Task<List<AlbumItem>> GetNewAlbumsAsync(string area = "ALL", int limit = 30, int offset = 0)
    {
        var list = new List<AlbumItem>();
        using var doc = await PostWeApiAsync("album/new", new { area, limit, offset, total = true }, $"/album/new?area={area}&limit={limit}&offset={offset}");
        if (doc != null && doc.RootElement.TryGetProperty("albums", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                list.Add(ParseAlbumElement(a));
            }
        }
        return list;
    }

    /// <summary>
    /// 获取心动模式（智能播放）歌曲列表（对应原 intelligenceMode.ts）
    /// </summary>
    public async Task<List<SongItem>> GetIntelligenceListAsync(long songId, long playlistId = 0)
    {
        var list = new List<SongItem>();
        using var doc = await PostWeApiAsync(
            "playmode/intelligence/list",
            new { songId, type = "fromPlayOne", playlistId, startMusicId = songId, count = 1 },
            $"/playmode/intelligence/list?id={songId}&pid={playlistId}");
        if (doc != null && doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.TryGetProperty("songInfo", out var sElem) && sElem.ValueKind == JsonValueKind.Object)
                {
                    list.Add(ParseSongElement(sElem));
                }
            }
        }
        return list;
    }

    // ==================== 播客电台 (Podcast / DJ Radio) API ====================

    /// <summary>
    /// 获取今日优选播客节目
    /// </summary>
    public async Task<List<RadioProgramItem>> GetPodcastTodayPreferredAsync()
    {
        var list = new List<RadioProgramItem>();
        using var doc = await PostWeApiAsync("djradio/home/today/perfered", new { page = 0 }, "/dj/today/perfered");
        if (doc != null && doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in arr.EnumerateArray())
            {
                list.Add(ParseRadioProgramElement(p));
            }
        }
        return list;
    }

    /// <summary>
    /// 获取推荐播客电台列表（支持按分类 ID 筛选，cateId <= 0 时获取综合推荐电台）
    /// </summary>
    public async Task<List<RadioStationItem>> GetRecommendedRadioStationsAsync(int cateId = -1)
    {
        var list = new List<RadioStationItem>();
        using var doc = cateId <= 0
            ? await PostWeApiAsync("djradio/recommend/v1", new { }, "/dj/recommend")
            : await PostWeApiAsync("djradio/recommend", new { cateId }, $"/dj/recommend/type?type={cateId}");

        if (doc != null && doc.RootElement.TryGetProperty("djRadios", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in arr.EnumerateArray())
            {
                list.Add(ParseRadioStationElement(r));
            }
        }
        return list;
    }

    /// <summary>
    /// 获取指定电台的节目列表
    /// </summary>
    public async Task<List<RadioProgramItem>> GetRadioProgramsAsync(long radioId, int limit = 40, int offset = 0)
    {
        var list = new List<RadioProgramItem>();
        using var doc = await PostWeApiAsync("dj/program/byradio", new { radioId, limit, offset, asc = false }, $"/dj/program?rid={radioId}&limit={limit}&offset={offset}");
        if (doc != null && doc.RootElement.TryGetProperty("programs", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in arr.EnumerateArray())
            {
                list.Add(ParseRadioProgramElement(p));
            }
        }
        return list;
    }

    public static RadioStationItem ParseRadioStationElement(JsonElement r)
    {
        return new RadioStationItem
        {
            Id = r.GetLongProp("id"),
            Name = r.GetStringProp("name"),
            PicUrl = r.GetStringProp("picUrl"),
            Category = r.GetStringProp("category"),
            RcmdText = r.GetStringProp("rcmdtext", r.GetStringProp("desc")),
            SubCount = r.GetLongProp("subCount"),
            ProgramCount = r.GetIntProp("programCount"),
            DjName = r.TryGetProperty("dj", out var dj) && dj.ValueKind == JsonValueKind.Object ? dj.GetStringProp("nickname") : string.Empty
        };
    }

    public static RadioProgramItem ParseRadioProgramElement(JsonElement p)
    {
        var prog = new RadioProgramItem
        {
            Id = p.GetLongProp("id"),
            Name = p.GetStringProp("name"),
            Description = p.GetStringProp("description"),
            CoverUrl = p.GetStringProp("coverUrl", p.GetStringProp("picUrl")),
            CreateTime = p.GetLongProp("createTime"),
            DurationMs = p.GetLongProp("duration"),
            ListenerCount = p.GetLongProp("listenerCount")
        };

        if (p.TryGetProperty("mainSong", out var mainSongElem) && mainSongElem.ValueKind == JsonValueKind.Object)
        {
            var song = ParseSongElement(mainSongElem);
            if (string.IsNullOrWhiteSpace(song.PicUrl)) song.PicUrl = prog.CoverUrl;
            if (song.DurationMs <= 0) song.DurationMs = prog.DurationMs;
            prog.MainSong = song;
        }
        else
        {
            prog.MainSong = new SongItem
            {
                Id = p.GetLongProp("mainTrackId", prog.Id).ToString(),
                Name = prog.Name,
                PicUrl = prog.CoverUrl,
                DurationMs = prog.DurationMs,
                Artists = [new ArtistItem { Name = "电台节目" }],
                Album = new AlbumItem { Name = "播客电台", PicUrl = prog.CoverUrl }
            };
        }

        return prog;
    }

    // ==================== JSON 解析辅助方法 ====================

    public static SongItem ParseSongElement(JsonElement s)
    {
        var song = new SongItem
        {
            Id = s.GetLongProp("id").ToString(),
            Name = s.GetStringProp("name"),
            DurationMs = s.GetLongProp("dt", s.GetLongProp("duration")),
            MvId = s.GetLongProp("mv", s.GetLongProp("mvid")),
            Fee = s.GetIntProp("fee"),
            Source = "netease"
        };

        // 解析歌手 ar 或 artists
        var arProp = s.TryGetProperty("ar", out var ar) ? ar : (s.TryGetProperty("artists", out var artists) ? artists : default);
        if (arProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arProp.EnumerateArray())
            {
                song.Artists.Add(ParseArtistElement(a));
            }
        }

        // 解析专辑 al 或 album
        var alProp = s.TryGetProperty("al", out var al) ? al : (s.TryGetProperty("album", out var album) ? album : default);
        if (alProp.ValueKind == JsonValueKind.Object)
        {
            song.Album = ParseAlbumElement(alProp);
            song.PicUrl = song.Album.PicUrl;
        }

        if (string.IsNullOrWhiteSpace(song.PicUrl))
        {
            song.PicUrl = s.GetStringProp("picUrl");
        }

        if (s.TryGetProperty("tns", out var tns) && tns.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tns.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String) song.Translations.Add(t.GetString() ?? "");
            }
        }

        if (s.TryGetProperty("alia", out var alia) && alia.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in alia.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.String) song.Alias.Add(a.GetString() ?? "");
            }
        }

        return song;
    }

    public static ArtistItem ParseArtistElement(JsonElement a)
    {
        return new ArtistItem
        {
            Id = a.GetLongProp("id"),
            Name = a.GetStringProp("name"),
            PicUrl = a.GetStringProp("img1v1Url", a.GetStringProp("picUrl")),
            MusicSize = a.GetIntProp("musicSize"),
            AlbumSize = a.GetIntProp("albumSize"),
            MvSize = a.GetIntProp("mvSize"),
            BriefDesc = a.GetStringProp("briefDesc")
        };
    }

    public static AlbumItem ParseAlbumElement(JsonElement al)
    {
        var album = new AlbumItem
        {
            Id = al.GetLongProp("id"),
            Name = al.GetStringProp("name"),
            PicUrl = al.GetStringProp("picUrl", al.GetStringProp("blurPicUrl")),
            PublishTime = al.GetLongProp("publishTime"),
            Size = al.GetIntProp("size"),
            Description = al.GetStringProp("description")
        };

        if (al.TryGetProperty("artist", out var artist) && artist.ValueKind == JsonValueKind.Object)
        {
            album.ArtistName = artist.GetStringProp("name");
        }
        return album;
    }

    public static PlaylistItem ParsePlaylistElement(JsonElement pl)
    {
        var item = new PlaylistItem
        {
            Id = pl.GetLongProp("id"),
            Name = pl.GetStringProp("name"),
            CoverImgUrl = pl.GetStringProp("coverImgUrl", pl.GetStringProp("picUrl")),
            Description = pl.GetStringProp("description"),
            PlayCount = pl.GetLongProp("playCount"),
            TrackCount = pl.GetIntProp("trackCount"),
            CreateTime = pl.GetLongProp("createTime"),
            UpdateTime = pl.GetLongProp("updateTime"),
            Subscribed = pl.TryGetProperty("subscribed", out var subElem) && subElem.ValueKind == JsonValueKind.True
        };

        if (pl.TryGetProperty("creator", out var creator) && creator.ValueKind == JsonValueKind.Object)
        {
            item.CreatorUserId = creator.GetLongProp("userId");
            item.CreatorName = creator.GetStringProp("nickname");
            item.CreatorAvatarUrl = creator.GetStringProp("avatarUrl");
        }

        if (pl.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tags.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String) item.Tags.Add(t.GetString() ?? "");
            }
        }

        if (pl.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in tracks.EnumerateArray())
            {
                item.Tracks.Add(ParseSongElement(s));
            }
        }

        return item;
    }

    public static MvItem ParseMvElement(JsonElement m)
    {
        return new MvItem
        {
            Id = m.GetLongProp("id"),
            Name = m.GetStringProp("name"),
            Cover = m.GetStringProp("cover", m.GetStringProp("imgurl16v9")),
            ArtistName = m.GetStringProp("artistName"),
            PlayCount = m.GetLongProp("playCount"),
            DurationMs = m.GetLongProp("duration"),
            BriefDesc = m.GetStringProp("briefDesc"),
            PublishTime = m.GetStringProp("publishTime")
        };
    }

    /// <summary>
    /// 检查 GitHub 最新版本更新（对应原 AboutTab.vue checkUpdate）
    /// </summary>
    public async Task<(bool HasUpdate, string LatestVersion, string ReleaseUrl)> CheckGitHubUpdateAsync(string currentVersion = "5.1.0")
    {
        try
        {
            var body = await _httpClient.GetStringAsync("https://api.github.com/repos/TencenTiM/AlgerMusicPlayer-WinUI3/releases/latest");
            using var doc = JsonDocument.Parse(body);
            var tag = doc.RootElement.GetStringProp("tag_name").TrimStart('v', 'V');
            var url = doc.RootElement.GetStringProp("html_url", "https://github.com/TencenTiM/AlgerMusicPlayer-WinUI3/releases");
            bool hasUpdate = !string.IsNullOrWhiteSpace(tag) &&
                             string.CompareOrdinal(tag, currentVersion.TrimStart('v', 'V')) > 0;
            return (hasUpdate, string.IsNullOrWhiteSpace(tag) ? currentVersion : tag, url);
        }
        catch
        {
            return (false, currentVersion, "https://github.com/TencenTiM/AlgerMusicPlayer-WinUI3/releases");
        }
    }

    /// <summary>
    /// 获取赞赏致谢名单（对应原 DonationList.vue getDonationList）
    /// </summary>
    public async Task<List<DonorItem>> GetDonationListAsync()
    {
        var list = new List<DonorItem>();
        try
        {
            var body = await _httpClient.GetStringAsync("http://donate.alger.fun/api/donations");
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    list.Add(new DonorItem
                    {
                        Id = item.GetLongProp("id").ToString(),
                        Name = item.GetStringProp("name", "匿名音乐爱好者"),
                        Amount = item.GetStringProp("amount", "10"),
                        Message = item.GetStringProp("message"),
                        Date = item.GetStringProp("date")
                    });
                }
            }
        }
        catch
        {
            // 网络受限时使用致谢示例名单兜底
        }

        if (list.Count == 0)
        {
            list.AddRange(
            [
                new DonorItem { Id = "1", Name = "Alger", Amount = "99.9", Message = "感谢大家支持开源音乐播放器！", Date = "2026-01-01" },
                new DonorItem { Id = "2", Name = "WinUI3 贡献者", Amount = "66.6", Message = "100% 纯 C# 原生性能起飞", Date = "2026-03-15" },
                new DonorItem { Id = "3", Name = "云村村民", Amount = "50.0", Message = "非常好用的桌面播放器", Date = "2026-02-20" },
                new DonorItem { Id = "4", Name = "无损音乐控", Amount = "29.9", Message = "支持 10段 EQ 与多音源解灰", Date = "2026-02-10" }
            ]);
        }
        return list;
    }

    /// <summary>
    /// 解析落雪音源 (LX Music) JS 脚本头部元数据（@name / @version / @author / @description）
    /// </summary>
    public static LxScriptItem ParseLxScriptHeader(string scriptContent, string fallbackName = "落雪自定义音源")
    {
        string ExtractTag(string tag, string def)
        {
            var m = global::System.Text.RegularExpressions.Regex.Match(
                scriptContent, $@"@{tag}\s+([^\r\n\*]+)", global::System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : def;
        }

        return new LxScriptItem
        {
            Id = $"lx_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            Name = ExtractTag("name", fallbackName),
            Version = ExtractTag("version", "1.0.0"),
            Author = ExtractTag("author", "LX Community"),
            Description = ExtractTag("description", "落雪音源脚本"),
            ScriptContent = scriptContent
        };
    }
}

/// <summary>
/// JsonElement 安全读取扩展方法
/// </summary>
public static class JsonElementExtensions
{
    public static string GetStringProp(this JsonElement elem, string propName, string fallback = "")
    {
        if (elem.ValueKind == JsonValueKind.Object && elem.TryGetProperty(propName, out var p))
        {
            return p.ValueKind switch
            {
                JsonValueKind.String => p.GetString() ?? fallback,
                JsonValueKind.Number => p.GetRawText(),
                _ => fallback
            };
        }
        return fallback;
    }

    public static long GetLongProp(this JsonElement elem, string propName, long fallback = 0)
    {
        if (elem.ValueKind == JsonValueKind.Object && elem.TryGetProperty(propName, out var p))
        {
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var val)) return val;
            if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var sval)) return sval;
        }
        return fallback;
    }

    public static int GetIntProp(this JsonElement elem, string propName, int fallback = 0)
    {
        if (elem.ValueKind == JsonValueKind.Object && elem.TryGetProperty(propName, out var p))
        {
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var val)) return val;
            if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var sval)) return sval;
        }
        return fallback;
    }
}
