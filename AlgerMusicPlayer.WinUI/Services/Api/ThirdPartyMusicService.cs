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

    public static IReadOnlyList<(string Key, string Title)> GetBoards(string sourceKey) =>
        sourceKey.ToLowerInvariant() switch
        {
            "tx" or "qq" => [("hot", "热歌榜"), ("soar", "飙升榜"), ("new", "新歌榜"), ("mainland", "内地榜"), ("hktw", "港台榜")],
            "kg" or "kugou" => [("hot", "TOP500热歌"), ("soar", "酷狗飙升榜"), ("new", "华语新歌榜"), ("net", "网络红歌榜")],
            "kw" or "kuwo" => [("hot", "酷我热歌榜"), ("soar", "酷我飙升榜"), ("new", "酷我新歌榜"), ("douyin", "抖音热歌榜")],
            "mg" or "migu" => [("hot", "尖叫热歌榜"), ("new", "尖叫新歌榜"), ("original", "尖叫原创榜")],
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
            _ => [("all", "全部推荐")]
        };

    /// <summary>
    /// 获取指定第三方平台的推荐/分类歌单列表
    /// </summary>
    public async Task<List<PlaylistItem>> GetRecommendPlaylistsAsync(
        string sourceKey, string categoryKey = "", int page = 1, int pageSize = 30, CancellationToken ct = default)
    {
        return sourceKey.ToLowerInvariant() switch
        {
            "tx" or "qq" => await GetQqRecommendPlaylistsAsync(categoryKey, page, pageSize, ct),
            "kg" or "kugou" => await GetKugouRecommendPlaylistsAsync(categoryKey, page, pageSize, ct),
            "kw" or "kuwo" => await GetKuwoRecommendPlaylistsAsync(categoryKey, page, pageSize, ct),
            "mg" or "migu" => await GetMiguRecommendPlaylistsAsync(categoryKey, page, ct),
            _ => []
        };
    }

    /// <summary>
    /// 获取指定第三方平台的歌单详情及完整原生曲目列表（酷狗/酷我/咪咕）
    /// </summary>
    public async Task<PlaylistItem?> GetPlaylistDetailAsync(
        PlaylistItem summary, CancellationToken ct = default)
    {
        if (summary == null) return null;
        var detail = summary.Source.ToLowerInvariant() switch
        {
            "kugou" or "kg" => await GetKugouPlaylistDetailAsync(summary, ct),
            "kuwo" or "kw" => await GetKuwoPlaylistDetailAsync(summary, ct),
            "migu" or "mg" => await GetMiguPlaylistDetailAsync(summary, ct),
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
    /// 获取指定第三方平台的榜单歌曲列表（自带原生 ID / Hash 与最高音质标识）
    /// </summary>
    public async Task<List<SongItem>> GetBoardSongsAsync(string sourceKey, string boardKey = "hot", CancellationToken ct = default)
    {
        var list = sourceKey.ToLowerInvariant() switch
        {
            "tx" or "qq" => await GetQqBoardAsync(boardKey, ct),
            "kg" or "kugou" => await GetKugouBoardAsync(boardKey, ct),
            "kw" or "kuwo" => await GetKuwoBoardAsync(boardKey, ct),
            "mg" or "migu" => await GetMiguBoardAsync(boardKey, ct),
            _ => []
        };

        for (int i = 0; i < list.Count; i++) list[i].DisplayIndex = i + 1;
        return list;
    }

    /// <summary>
    /// 在指定第三方平台原生曲库中按关键词搜索歌曲
    /// </summary>
    public async Task<List<SongItem>> SearchSongsAsync(
        string sourceKey, string keyword, int page = 1, int pageSize = 35, CancellationToken ct = default)
    {
        var trimmed = keyword?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed)) return [];

        var list = sourceKey.ToLowerInvariant() switch
        {
            "tx" or "qq" => await SearchQqAsync(trimmed, page, pageSize, ct),
            "kg" or "kugou" => await SearchKugouAsync(trimmed, page, pageSize, ct),
            "kw" or "kuwo" => await SearchKuwoAsync(trimmed, page, pageSize, ct),
            "mg" or "migu" => await SearchMiguAsync(trimmed, page, pageSize, ct),
            _ => []
        };

        for (int i = 0; i < list.Count; i++) list[i].DisplayIndex = i + 1;
        return list;
    }

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
            picUrl = imgs[0].GetStringProp("img");
        }
        else if (item.TryGetProperty("albumImgs", out var aimgs) && aimgs.ValueKind == JsonValueKind.Array && aimgs.GetArrayLength() > 0)
        {
            picUrl = aimgs[0].GetStringProp("img");
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
                        CoverImgUrl = string.IsNullOrWhiteSpace(img) ? "ms-appx:///Assets/DefaultCover.png" : img,
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
            bool useAll = string.IsNullOrWhiteSpace(categoryKey) || categoryKey == "all";
            var url = useAll
                ? $"https://m.kugou.com/plist/index&json=true&page={Math.Max(1, page)}"
                : $"http://mobilecdnbj.kugou.com/api/v3/tag/specialList?plat=0&page={Math.Max(1, page)}&tagid={Uri.EscapeDataString(categoryKey)}&pagesize={pageSize}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.Clear();
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148");

            using var resp = await _httpClient.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            JsonElement arr = default;
            bool hasArr = false;
            if (useAll &&
                doc.RootElement.TryGetProperty("plist", out var plist) &&
                plist.TryGetProperty("list", out var plistInner) &&
                plistInner.TryGetProperty("info", out arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                hasArr = true;
            }
            else if (doc.RootElement.TryGetProperty("data", out var data) &&
                     data.TryGetProperty("info", out arr) &&
                     arr.ValueKind == JsonValueKind.Array)
            {
                hasArr = true;
            }

            if (hasArr)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var specialId = item.GetStringProp("specialid");
                    var name = GlobalHtmlDecode(item.GetStringProp("specialname"));
                    if (string.IsNullOrWhiteSpace(specialId) || string.IsNullOrWhiteSpace(name)) continue;

                    var img = item.GetStringProp("imgurl").Replace("\\/", "/").Replace("{size}", "400");
                    list.Add(new PlaylistItem
                    {
                        Id = long.TryParse(specialId, out long lid) ? lid : Math.Abs(specialId.GetHashCode()),
                        Source = "kugou",
                        ThirdPartyId = specialId,
                        Name = name,
                        CoverImgUrl = string.IsNullOrWhiteSpace(img) ? "ms-appx:///Assets/DefaultCover.png" : img,
                        PlayCount = item.GetLongProp("playcount"),
                        TrackCount = item.GetIntProp("songcount"),
                        CreatorName = item.GetStringProp("username", "酷狗歌单"),
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
            var url = $"https://m.kugou.com/plist/list/{Uri.EscapeDataString(specialId)}?json=true&page=1&pagesize=100";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.Clear();
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148");

            using var resp = await _httpClient.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

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

            if (doc.RootElement.TryGetProperty("info", out var infoOuter) &&
                infoOuter.TryGetProperty("list", out var meta) &&
                meta.ValueKind == JsonValueKind.Object)
            {
                var title = GlobalHtmlDecode(meta.GetStringProp("specialname"));
                if (!string.IsNullOrWhiteSpace(title)) detail.Name = title;
                var img = meta.GetStringProp("imgurl").Replace("\\/", "/").Replace("{size}", "400");
                if (!string.IsNullOrWhiteSpace(img)) detail.CoverImgUrl = img;
                var intro = GlobalHtmlDecode(meta.GetStringProp("intro"));
                if (!string.IsNullOrWhiteSpace(intro)) detail.Description = intro;
                var nick = meta.GetStringProp("nickname");
                if (!string.IsNullOrWhiteSpace(nick)) detail.CreatorName = nick;
                long pc = meta.GetLongProp("playcount");
                if (pc > 0) detail.PlayCount = pc;
            }

            if (doc.RootElement.TryGetProperty("list", out var listOuter) &&
                listOuter.TryGetProperty("list", out var listInner) &&
                listInner.TryGetProperty("info", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var song = ParseKugouSongElement(item);
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
                        CoverImgUrl = string.IsNullOrWhiteSpace(img) ? "ms-appx:///Assets/DefaultCover.png" : img,
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
        if (string.IsNullOrWhiteSpace(raw)) return "ms-appx:///Assets/DefaultCover.png";
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
