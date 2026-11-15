using System.Text.Json;
using System.Text.RegularExpressions;
using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.Auth;

namespace AlgerMusicPlayer.WinUI.Services.Api;

/// <summary>
/// Bilibili 视频与音频搜索、分P详情与 DASH 音视频流解析服务
/// </summary>
public partial class BilibiliService
{
    private readonly HttpClient _httpClient;
    private readonly PlatformAuthService _platformAuth;

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    public BilibiliService(PlatformAuthService platformAuth)
    {
        _platformAuth = platformAuth;
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = global::System.Net.DecompressionMethods.GZip | global::System.Net.DecompressionMethods.Deflate,
            UseCookies = false
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
    }

    private async Task<string> SendGetStringAsync(string url, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var injected = await _platformAuth.ApplyAuthHeadersAsync(AuthMusicPlatform.Bilibili, req, ct);
        if (!injected)
        {
            req.Headers.TryAddWithoutValidation("Cookie", "buvid3=AlgerMusicPlayerWinUI;");
        }

        using var resp = await _httpClient.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// 搜索 B站视频（或直接输入 BV 号解析）
    /// </summary>
    public async Task<List<BilibiliVideoItem>> SearchVideosAsync(string keyword, int page = 1, int pageSize = 20)
    {
        var list = new List<BilibiliVideoItem>();
        if (string.IsNullOrWhiteSpace(keyword)) return list;

        var trimmed = keyword.Trim();
        if (trimmed.StartsWith("BV", StringComparison.OrdinalIgnoreCase))
        {
            var detail = await GetVideoDetailAsync(trimmed);
            if (detail != null) list.Add(detail);
            return list;
        }

        try
        {
            var url = $"https://api.bilibili.com/x/web-interface/search/type?search_type=video&keyword={Uri.EscapeDataString(trimmed)}&page={page}&page_size={pageSize}";
            var json = await SendGetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("result", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var rawPic = item.GetStringProp("pic");
                    if (rawPic.StartsWith("//")) rawPic = "https:" + rawPic;

                    var rawTitle = HtmlTagRegex().Replace(item.GetStringProp("title"), string.Empty);
                    list.Add(new BilibiliVideoItem
                    {
                        Aid = item.GetLongProp("aid"),
                        Bvid = item.GetStringProp("bvid"),
                        Title = global::System.Net.WebUtility.HtmlDecode(rawTitle),
                        Author = item.GetStringProp("author"),
                        Pic = rawPic,
                        Description = item.GetStringProp("description"),
                        Duration = item.GetStringProp("duration"),
                        ViewCount = item.GetLongProp("play")
                    });
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
    /// 获取 B站视频详情及分P列表
    /// </summary>
    public async Task<BilibiliVideoItem?> GetVideoDetailAsync(string bvid)
    {
        try
        {
            var url = $"https://api.bilibili.com/x/web-interface/view?bvid={Uri.EscapeDataString(bvid)}";
            var json = await SendGetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var pic = d.GetStringProp("pic");
            if (pic.StartsWith("//")) pic = "https:" + pic;

            var video = new BilibiliVideoItem
            {
                Aid = d.GetLongProp("aid"),
                Bvid = d.GetStringProp("bvid"),
                Title = d.GetStringProp("title"),
                Description = d.GetStringProp("desc"),
                Pic = pic,
                Author = d.TryGetProperty("owner", out var owner) ? owner.GetStringProp("name") : string.Empty,
                ViewCount = d.TryGetProperty("stat", out var stat) ? stat.GetLongProp("view") : 0
            };

            if (d.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pages.EnumerateArray())
                {
                    video.Pages.Add(new BilibiliPageItem
                    {
                        Cid = p.GetLongProp("cid"),
                        Page = p.GetIntProp("page"),
                        Part = p.GetStringProp("part"),
                        DurationSeconds = p.GetIntProp("duration")
                    });
                }
            }

            return video;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 获取 B站分P的 DASH 音频流或 MP4 播放地址
    /// </summary>
    public async Task<string?> GetAudioStreamUrlAsync(string bvid, long cid)
    {
        try
        {
            if (cid <= 0)
            {
                var detail = await GetVideoDetailAsync(bvid);
                cid = detail?.Pages.FirstOrDefault()?.Cid ?? 0;
                if (cid <= 0) return null;
            }

            // 登录状态下开启 fnval=4048 + qn=127 以获取 Hi-Res FLAC / Dolby / 最高码率 DASH 音频流
            var loggedIn = _platformAuth.HasValidSession(AuthMusicPlatform.Bilibili);
            var dashUrl = loggedIn
                ? $"https://api.bilibili.com/x/player/playurl?bvid={Uri.EscapeDataString(bvid)}&cid={cid}&fnval=4048&qn=127&fourk=1"
                : $"https://api.bilibili.com/x/player/playurl?bvid={Uri.EscapeDataString(bvid)}&cid={cid}&fnval=16";
            var json = await SendGetStringAsync(dashUrl);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("dash", out var dash))
                {
                    if (loggedIn && dash.TryGetProperty("flac", out var flac) &&
                        flac.ValueKind == JsonValueKind.Object &&
                        flac.TryGetProperty("audio", out var flacAudio) &&
                        flacAudio.ValueKind == JsonValueKind.Object)
                    {
                        var flacUrl = flacAudio.GetStringProp("baseUrl", flacAudio.GetStringProp("base_url"));
                        if (!string.IsNullOrWhiteSpace(flacUrl)) return flacUrl;
                    }

                    if (loggedIn && dash.TryGetProperty("dolby", out var dolby) &&
                        dolby.ValueKind == JsonValueKind.Object &&
                        dolby.TryGetProperty("audio", out var dolbyArr) &&
                        dolbyArr.ValueKind == JsonValueKind.Array &&
                        dolbyArr.GetArrayLength() > 0)
                    {
                        var dolbyUrl = dolbyArr[0].GetStringProp("baseUrl", dolbyArr[0].GetStringProp("base_url"));
                        if (!string.IsNullOrWhiteSpace(dolbyUrl)) return dolbyUrl;
                    }

                    if (dash.TryGetProperty("audio", out var audioArr) &&
                        audioArr.ValueKind == JsonValueKind.Array &&
                        audioArr.GetArrayLength() > 0)
                    {
                        var best = audioArr.EnumerateArray()
                            .OrderByDescending(a => a.GetLongProp("bandwidth"))
                            .FirstOrDefault();
                        var baseUrl = best.GetStringProp("baseUrl", best.GetStringProp("base_url"));
                        if (!string.IsNullOrWhiteSpace(baseUrl)) return baseUrl;
                    }
                }

                if (data.TryGetProperty("durl", out var durlArr) &&
                    durlArr.ValueKind == JsonValueKind.Array &&
                    durlArr.GetArrayLength() > 0)
                {
                    return durlArr[0].GetStringProp("url");
                }
            }
        }
        catch
        {
            // 忽略解析异常
        }
        return null;
    }

    /// <summary>
    /// 下载 B站音频流到本地缓存文件以解决 Referer 防盗链限制并供 NAudio 播放
    /// </summary>
    public async Task<string?> DownloadAudioToCacheAsync(string streamUrl, string bvid, long cid, string cacheDir, CancellationToken ct = default)
    {
        try
        {
            var cacheFile = Path.Combine(cacheDir, $"bili_{bvid}_{cid}.m4a");
            if (File.Exists(cacheFile) && new FileInfo(cacheFile).Length > 1024)
            {
                return cacheFile;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            req.Headers.Referrer = new Uri($"https://www.bilibili.com/video/{bvid}");
            var injected = await _platformAuth.ApplyAuthHeadersAsync(AuthMusicPlatform.Bilibili, req, ct);
            if (!injected)
            {
                req.Headers.TryAddWithoutValidation("Cookie", "buvid3=AlgerMusicPlayerWinUI;");
            }
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            await using var fs = new FileStream(cacheFile, FileMode.Create, FileAccess.Write, FileShare.Read);
            await resp.Content.CopyToAsync(fs, ct);
            return cacheFile;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 获取已登录 Bilibili 账号昵称与创建/订阅的收藏夹列表
    /// </summary>
    public async Task<(string Nickname, List<PlaylistItem> Created, List<PlaylistItem> Collected)> GetLoggedInProfileAndFoldersAsync(CancellationToken ct = default)
    {
        var created = new List<PlaylistItem>();
        var collected = new List<PlaylistItem>();
        if (!_platformAuth.HasValidSession(AuthMusicPlatform.Bilibili))
        {
            return (string.Empty, created, collected);
        }

        try
        {
            var navJson = await SendGetStringAsync("https://api.bilibili.com/x/web-interface/nav", ct);
            using var navDoc = JsonDocument.Parse(navJson);
            if (!navDoc.RootElement.TryGetProperty("data", out var navData) ||
                !navData.TryGetProperty("isLogin", out var isLoginProp) ||
                isLoginProp.ValueKind != JsonValueKind.True)
            {
                return (string.Empty, created, collected);
            }

            var uname = navData.GetStringProp("uname");
            var mid = navData.GetLongProp("mid");
            if (mid <= 0)
            {
                return (uname, created, collected);
            }

            // 创建的收藏夹
            try
            {
                var createdJson = await SendGetStringAsync($"https://api.bilibili.com/x/v3/fav/folder/created/list-all?up_mid={mid}", ct);
                using var cDoc = JsonDocument.Parse(createdJson);
                if (cDoc.RootElement.TryGetProperty("data", out var cData) &&
                    cData.TryGetProperty("list", out var cList) &&
                    cList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in cList.EnumerateArray())
                    {
                        var folderId = item.GetLongProp("id");
                        if (folderId <= 0) continue;
                        var cover = item.GetStringProp("cover");
                        if (cover.StartsWith("//")) cover = "https:" + cover;
                        var title = item.GetStringProp("title", "B站收藏夹");
                        created.Add(new PlaylistItem
                        {
                            Id = folderId,
                            ThirdPartyId = folderId.ToString(),
                            Source = "bilibili",
                            Name = title,
                            CoverImgUrl = cover,
                            TrackCount = item.GetIntProp("media_count"),
                            CreatorUserId = mid,
                            CreatorName = uname,
                            IsSystemDefaultPlaylist = created.Count == 0 && string.Equals(title, "默认收藏夹", StringComparison.Ordinal)
                        });
                    }
                }
            }
            catch
            {
                // 忽略创建收藏夹异常
            }

            // 订阅的收藏夹 / 合集（分页拉取并区分 type=21 视频合集与 type=11 标准收藏夹）
            try
            {
                var seenCollected = new HashSet<long>();
                for (int pn = 1; pn <= 3; pn++)
                {
                    var colJson = await SendGetStringAsync($"https://api.bilibili.com/x/v3/fav/folder/collected/list?up_mid={mid}&pn={pn}&ps=40", ct);
                    using var colDoc = JsonDocument.Parse(colJson);
                    if (!colDoc.RootElement.TryGetProperty("data", out var colData) ||
                        !colData.TryGetProperty("list", out var colList) ||
                        colList.ValueKind != JsonValueKind.Array ||
                        colList.GetArrayLength() == 0)
                    {
                        break;
                    }

                    foreach (var item in colList.EnumerateArray())
                    {
                        var folderId = item.GetLongProp("id");
                        if (folderId <= 0 || !seenCollected.Add(folderId)) continue;
                        var cover = item.GetStringProp("cover");
                        if (cover.StartsWith("//")) cover = "https:" + cover;
                        long upperMid = 0;
                        var upperName = string.Empty;
                        if (item.TryGetProperty("upper", out var up) && up.ValueKind == JsonValueKind.Object)
                        {
                            upperMid = up.GetLongProp("mid");
                            upperName = up.GetStringProp("name");
                        }
                        int folderType = item.GetIntProp("type");
                        string tpId = folderType == 21 && upperMid > 0
                            ? $"season:{folderId}:{upperMid}"
                            : folderId.ToString();

                        collected.Add(new PlaylistItem
                        {
                            Id = folderId,
                            ThirdPartyId = tpId,
                            Source = "bilibili",
                            Name = item.GetStringProp("title", "B站订阅合集"),
                            CoverImgUrl = cover,
                            TrackCount = item.GetIntProp("media_count"),
                            CreatorName = upperName
                        });
                    }

                    bool hasMore = colData.TryGetProperty("has_more", out var hmProp) &&
                                   hmProp.ValueKind == JsonValueKind.True;
                    if (!hasMore) break;
                }
            }
            catch
            {
                // 忽略订阅收藏夹异常
            }

            return (uname, created, collected);
        }
        catch
        {
            return (string.Empty, created, collected);
        }
    }

    /// <summary>
    /// 获取 Bilibili 收藏夹或订阅合集内的音频曲目列表
    /// </summary>
    public async Task<List<SongItem>> GetFavoriteFolderSongsAsync(string mediaId, CancellationToken ct = default)
    {
        var songs = new List<SongItem>();
        if (string.IsNullOrWhiteSpace(mediaId)) return songs;

        try
        {
            if (mediaId.StartsWith("season:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = mediaId.Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    var seasonId = parts[1];
                    var upperMid = parts[2];
                    var seenBvid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int pageNum = 1; pageNum <= 3; pageNum++)
                    {
                        var seasonUrl = $"https://api.bilibili.com/x/polymer/web-space/seasons_archives_list?mid={Uri.EscapeDataString(upperMid)}&season_id={Uri.EscapeDataString(seasonId)}&sort_reverse=false&page_num={pageNum}&page_size=100";
                        var seasonJson = await SendGetStringAsync(seasonUrl, ct);
                        using var sDoc = JsonDocument.Parse(seasonJson);
                        if (!sDoc.RootElement.TryGetProperty("data", out var sData) ||
                            !sData.TryGetProperty("archives", out var archives) ||
                            archives.ValueKind != JsonValueKind.Array ||
                            archives.GetArrayLength() == 0)
                        {
                            break;
                        }

                        var metaName = sData.TryGetProperty("meta", out var metaEl) ? metaEl.GetStringProp("name") : string.Empty;
                        int pageAdded = 0;
                        foreach (var item in archives.EnumerateArray())
                        {
                            var bvid = item.GetStringProp("bvid");
                            if (string.IsNullOrWhiteSpace(bvid) || !seenBvid.Add(bvid)) continue;
                            var cover = item.GetStringProp("pic");
                            if (cover.StartsWith("//")) cover = "https:" + cover;
                            songs.Add(new SongItem
                            {
                                Id = $"bili_{bvid}_0",
                                Name = item.GetStringProp("title", bvid),
                                PicUrl = cover,
                                Artists = [new ArtistItem { Name = string.IsNullOrWhiteSpace(metaName) ? $"UP #{upperMid}" : metaName }],
                                Album = new AlbumItem { Name = $"Bilibili ({bvid})", PicUrl = cover },
                                DurationMs = item.GetLongProp("duration") * 1000L,
                                Source = "bilibili",
                                BilibiliBvid = bvid,
                                BilibiliCid = 0
                            });
                            pageAdded++;
                        }

                        if (pageAdded == 0 || archives.GetArrayLength() < 100) break;
                    }
                }
                return songs;
            }

            var seenFavBvid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int pn = 1; pn <= 5; pn++)
            {
                var url = $"https://api.bilibili.com/x/v3/fav/resource/list?media_id={Uri.EscapeDataString(mediaId)}&pn={pn}&ps=40&platform=web";
                var json = await SendGetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("medias", out var medias) ||
                    medias.ValueKind != JsonValueKind.Array ||
                    medias.GetArrayLength() == 0)
                {
                    break;
                }

                int pageAdded = 0;
                foreach (var item in medias.EnumerateArray())
                {
                    var bvid = item.GetStringProp("bvid");
                    if (string.IsNullOrWhiteSpace(bvid) || !seenFavBvid.Add(bvid)) continue;
                    var cid = item.TryGetProperty("ugc", out var ugc) ? ugc.GetLongProp("first_cid") : 0;
                    var cover = item.GetStringProp("cover");
                    if (cover.StartsWith("//")) cover = "https:" + cover;
                    var author = item.TryGetProperty("upper", out var upper) ? upper.GetStringProp("name") : string.Empty;

                    songs.Add(new SongItem
                    {
                        Id = $"bili_{bvid}_{cid}",
                        Name = item.GetStringProp("title", bvid),
                        PicUrl = cover,
                        Artists = [new ArtistItem { Name = author }],
                        Album = new AlbumItem { Name = $"Bilibili ({bvid})", PicUrl = cover },
                        DurationMs = item.GetLongProp("duration") * 1000L,
                        Source = "bilibili",
                        BilibiliBvid = bvid,
                        BilibiliCid = cid,
                        BilibiliAid = item.GetLongProp("id")
                    });
                    pageAdded++;
                }

                bool hasMore = data.TryGetProperty("has_more", out var hmProp) &&
                               hmProp.ValueKind == JsonValueKind.True;
                if (!hasMore || pageAdded == 0) break;
            }

            // 兜底：部分无 upper_mid 的旧合集直接尝试 x/space/fav/season/list
            if (songs.Count == 0 && mediaId.All(char.IsDigit))
            {
                var fallbackUrl = $"https://api.bilibili.com/x/space/fav/season/list?season_id={Uri.EscapeDataString(mediaId)}&pn=1&ps=40";
                var fallbackJson = await SendGetStringAsync(fallbackUrl, ct);
                using var fDoc = JsonDocument.Parse(fallbackJson);
                if (fDoc.RootElement.TryGetProperty("data", out var fData) &&
                    fData.TryGetProperty("medias", out var fMedias) &&
                    fMedias.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in fMedias.EnumerateArray())
                    {
                        var bvid = item.GetStringProp("bvid");
                        if (string.IsNullOrWhiteSpace(bvid) || !seenFavBvid.Add(bvid)) continue;
                        var cid = item.TryGetProperty("ugc", out var ugc) ? ugc.GetLongProp("first_cid") : 0;
                        var cover = item.GetStringProp("cover");
                        if (cover.StartsWith("//")) cover = "https:" + cover;
                        var author = item.TryGetProperty("upper", out var upper) ? upper.GetStringProp("name") : string.Empty;
                        songs.Add(new SongItem
                        {
                            Id = $"bili_{bvid}_{cid}",
                            Name = item.GetStringProp("title", bvid),
                            PicUrl = cover,
                            Artists = [new ArtistItem { Name = author }],
                            Album = new AlbumItem { Name = $"Bilibili ({bvid})", PicUrl = cover },
                            DurationMs = item.GetLongProp("duration") * 1000L,
                            Source = "bilibili",
                            BilibiliBvid = bvid,
                            BilibiliCid = cid,
                            BilibiliAid = item.GetLongProp("id")
                        });
                    }
                }
            }
        }
        catch
        {
            // 忽略网络或解析异常
        }

        return songs;
    }

    private string ExtractBiliJct()
    {
        var cookie = _platformAuth.TryGetSession(AuthMusicPlatform.Bilibili)?.CookieHeader ?? string.Empty;
        var m = Regex.Match(cookie, @"(?:^|;\s*)bili_jct=([^;\s]+)", RegexOptions.IgnoreCase);
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value.Trim()) : string.Empty;
    }

    private async Task<string?> SendPostFormAsync(
        string url,
        IEnumerable<KeyValuePair<string, string>> form,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form)
        };
        if (!await _platformAuth.ApplyAuthHeadersAsync(AuthMusicPlatform.Bilibili, req, ct))
        {
            return null;
        }
        req.Headers.TryAddWithoutValidation("Origin", "https://space.bilibili.com");
        req.Headers.Referrer = new Uri("https://space.bilibili.com/");

        using var resp = await _httpClient.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task<long> ResolveVideoAidAsync(SongItem song, CancellationToken ct)
    {
        if (song.BilibiliAid > 0) return song.BilibiliAid;
        if (!string.IsNullOrWhiteSpace(song.BilibiliBvid))
        {
            var detail = await GetVideoDetailAsync(song.BilibiliBvid);
            if (detail != null && detail.Aid > 0)
            {
                song.BilibiliAid = detail.Aid;
                return detail.Aid;
            }
        }

        var keyword = $"{song.Name} {song.ArtistText}".Replace("未知歌手", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(keyword)) return 0;

        var candidates = await SearchVideosAsync(keyword, 1, 5);
        var matched = candidates.FirstOrDefault(v => v.Aid > 0);
        return matched?.Aid ?? 0;
    }

    /// <summary>
    /// 创建 B站个人收藏夹（需含 SESSDATA 与 bili_jct CSRF）
    /// </summary>
    public async Task<(PlaylistItem? Playlist, string Message)> CreateFavoriteFolderAsync(
        string title,
        bool isPrivate,
        CancellationToken ct = default)
    {
        var csrf = ExtractBiliJct();
        if (string.IsNullOrWhiteSpace(csrf))
        {
            return (null, "创建 B站收藏夹缺少 bili_jct 凭证，请重新登录 B站账号");
        }

        try
        {
            var json = await SendPostFormAsync(
                "https://api.bilibili.com/x/v3/fav/folder/add",
                [
                    new("title", title.Trim()),
                    new("intro", string.Empty),
                    new("privacy", isPrivate ? "1" : "0"),
                    new("csrf", csrf)
                ],
                ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                return (null, "创建 B站收藏夹请求失败");
            }

            using var doc = JsonDocument.Parse(json);
            int code = doc.RootElement.GetIntProp("code", -1);
            if (code == 0 && doc.RootElement.TryGetProperty("data", out var data))
            {
                long id = data.GetLongProp("id");
                var pl = new PlaylistItem
                {
                    Id = id,
                    Source = "bilibili",
                    ThirdPartyId = id.ToString(),
                    Name = data.GetStringProp("title", title.Trim()),
                    TrackCount = 0
                };
                return (pl, $"已创建 B站收藏夹《{pl.Name}》");
            }
            return (null, doc.RootElement.GetStringProp("message", $"创建 B站收藏夹失败 (code={code})"));
        }
        catch (Exception ex)
        {
            return (null, $"创建 B站收藏夹异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 删除本人自建 B站收藏夹（media_ids，仅支持标准收藏夹且默认收藏夹不可删）
    /// </summary>
    public async Task<(bool Success, string Message)> DeleteFavoriteFolderAsync(string folderId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderId) || !folderId.All(char.IsDigit))
        {
            return (false, "仅支持删除本人自建的标准 B站收藏夹");
        }

        var csrf = ExtractBiliJct();
        if (string.IsNullOrWhiteSpace(csrf))
        {
            return (false, "删除 B站收藏夹缺少 bili_jct 凭证，请重新登录 B站账号");
        }

        try
        {
            var json = await SendPostFormAsync(
                "https://api.bilibili.com/x/v3/fav/folder/del",
                [
                    new("media_ids", folderId),
                    new("platform", "web"),
                    new("csrf", csrf)
                ],
                ct);
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = JsonDocument.Parse(json);
                int code = doc.RootElement.GetIntProp("code", -1);
                if (code == 0)
                {
                    return (true, "已删除 B站收藏夹");
                }
                return (false, doc.RootElement.GetStringProp("message", $"删除 B站收藏夹失败 (code={code})"));
            }
        }
        catch (Exception ex)
        {
            return (false, $"删除 B站收藏夹异常：{ex.Message}");
        }

        return (false, "删除 B站收藏夹失败");
    }

    /// <summary>
    /// 向 B站自建收藏夹添加视频曲目（支持跨平台按关键词自动匹配 B站视频）
    /// </summary>
    public async Task<(bool Success, int AddedCount, string Message)> AddSongsToFavoriteFolderAsync(
        string folderId,
        IReadOnlyList<SongItem> songs,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderId) || !folderId.All(char.IsDigit))
        {
            return (false, 0, "仅支持向标准自建 B站收藏夹添加内容（订阅合集为只读）");
        }

        var csrf = ExtractBiliJct();
        if (string.IsNullOrWhiteSpace(csrf))
        {
            return (false, 0, "操作 B站收藏夹缺少 bili_jct 凭证，请重新登录 B站账号");
        }

        int added = 0;
        string? lastError = null;
        foreach (var song in songs)
        {
            long aid = await ResolveVideoAidAsync(song, ct);
            if (aid <= 0)
            {
                lastError ??= $"未能在 B站匹配到《{song.Name}》";
                continue;
            }

            try
            {
                var json = await SendPostFormAsync(
                    "https://api.bilibili.com/x/v3/fav/resource/deal",
                    [
                        new("rid", aid.ToString()),
                        new("type", "2"),
                        new("add_media_ids", folderId),
                        new("del_media_ids", string.Empty),
                        new("platform", "web"),
                        new("csrf", csrf)
                    ],
                    ct);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.GetIntProp("code", -1) == 0)
                    {
                        added++;
                    }
                    else
                    {
                        lastError ??= doc.RootElement.GetStringProp("message", "B站收藏夹添加失败");
                    }
                }
            }
            catch (Exception ex)
            {
                lastError ??= ex.Message;
            }
        }

        if (added > 0)
        {
            return (true, added, $"已添加 {added} 首到 B站收藏夹");
        }
        return (false, 0, lastError ?? "添加到 B站收藏夹失败");
    }

    /// <summary>
    /// 从 B站自建收藏夹单曲或批量移除视频条目
    /// </summary>
    public async Task<(bool Success, int RemovedCount, string Message)> RemoveSongsFromFavoriteFolderAsync(
        string folderId,
        IReadOnlyList<SongItem> songs,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderId) || !folderId.All(char.IsDigit))
        {
            return (false, 0, "订阅合集为只读，无法删除条目");
        }

        var csrf = ExtractBiliJct();
        if (string.IsNullOrWhiteSpace(csrf))
        {
            return (false, 0, "删除 B站收藏夹条目缺少 bili_jct 凭证，请重新登录 B站账号");
        }

        var aids = new List<long>();
        var seen = new HashSet<long>();
        foreach (var s in songs)
        {
            long aid = await ResolveVideoAidAsync(s, ct);
            if (aid > 0 && seen.Add(aid))
            {
                aids.Add(aid);
            }
        }

        if (aids.Count == 0)
        {
            return (false, 0, "未能解析待删除条目的 B站视频 ID");
        }

        try
        {
            var json = await SendPostFormAsync(
                "https://api.bilibili.com/x/v3/fav/resource/batch-del",
                [
                    new("resources", string.Join(",", aids.Select(a => $"{a}:2"))),
                    new("media_id", folderId),
                    new("platform", "web"),
                    new("csrf", csrf)
                ],
                ct);
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.GetIntProp("code", -1) == 0)
                {
                    return (true, aids.Count, $"已从 B站收藏夹删除 {aids.Count} 首条目");
                }
                return (false, 0, doc.RootElement.GetStringProp("message", "从 B站收藏夹删除失败"));
            }
        }
        catch (Exception ex)
        {
            return (false, 0, $"删除 B站收藏夹条目异常：{ex.Message}");
        }

        return (false, 0, "从 B站收藏夹删除失败");
    }

    /// <summary>
    /// 将 B站视频分P转换为播放器通用的 SongItem
    /// </summary>
    public static SongItem ToSongItem(BilibiliVideoItem video, BilibiliPageItem? page = null)
    {
        var p = page ?? video.Pages.FirstOrDefault();
        var cid = p?.Cid ?? 0;
        var title = p != null && video.Pages.Count > 1 ? $"{video.Title} - P{p.Page} {p.Part}" : video.Title;

        return new SongItem
        {
            Id = $"bili_{video.Bvid}_{cid}",
            Name = title,
            PicUrl = video.Pic,
            Artists = [new ArtistItem { Name = video.Author }],
            Album = new AlbumItem { Name = $"Bilibili ({video.Bvid})", PicUrl = video.Pic },
            DurationMs = (p?.DurationSeconds ?? 0) * 1000L,
            Source = "bilibili",
            BilibiliBvid = video.Bvid,
            BilibiliCid = cid,
            BilibiliAid = video.Aid
        };
    }
}
