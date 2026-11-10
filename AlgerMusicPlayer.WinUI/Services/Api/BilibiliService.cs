using System.Text.Json;
using System.Text.RegularExpressions;
using AlgerMusicPlayer.WinUI.Models;

namespace AlgerMusicPlayer.WinUI.Services.Api;

/// <summary>
/// Bilibili 视频与音频搜索、分P详情与 DASH 音视频流解析服务
/// </summary>
public partial class BilibiliService
{
    private readonly HttpClient _httpClient;

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    public BilibiliService()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = global::System.Net.DecompressionMethods.GZip | global::System.Net.DecompressionMethods.Deflate
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "buvid3=AlgerMusicPlayerWinUI;");
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
            var json = await _httpClient.GetStringAsync(url);
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
            var json = await _httpClient.GetStringAsync(url);
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

            // 先请求 fnval=16 获取 DASH 纯音频流
            var dashUrl = $"https://api.bilibili.com/x/player/playurl?bvid={Uri.EscapeDataString(bvid)}&cid={cid}&fnval=16";
            var json = await _httpClient.GetStringAsync(dashUrl);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("dash", out var dash) &&
                    dash.TryGetProperty("audio", out var audioArr) &&
                    audioArr.ValueKind == JsonValueKind.Array &&
                    audioArr.GetArrayLength() > 0)
                {
                    var baseUrl = audioArr[0].GetStringProp("baseUrl", audioArr[0].GetStringProp("base_url"));
                    if (!string.IsNullOrWhiteSpace(baseUrl)) return baseUrl;
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
            BilibiliCid = cid
        };
    }
}
