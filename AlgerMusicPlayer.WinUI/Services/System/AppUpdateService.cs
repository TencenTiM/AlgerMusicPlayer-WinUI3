using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace AlgerMusicPlayer.WinUI.Services.System;

/// <summary>
/// 软件内一键更新：检查 GitHub Release → 下载安装包（直连失败自动切换镜像）→ 静默覆盖安装并重启
/// </summary>
public static class AppUpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/TencenTiM/AlgerMusicPlayer-WinUI3/releases/latest";
    private const string SetupAssetName = "AlgerMusicPlayer-Setup-x64.exe";

    // 直连失败后依次尝试的 GitHub 下载加速镜像（前缀 + 原始下载地址）
    private static readonly string[] MirrorPrefixes =
    {
        "",
        "https://ghfast.top/",
        "https://gh-proxy.com/",
        "https://ghproxy.net/"
    };

    private static readonly HttpClient Http = CreateClient();

    public sealed record ReleaseInfo(Version Version, string Tag, string Notes, string SetupUrl, long SetupSize);

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AlgerMusicPlayer-WinUI3-Updater");
        return client;
    }

    /// <summary>
    /// 获取最新 Release；无新版本返回 null
    /// </summary>
    public static async Task<ReleaseInfo?> CheckForUpdateAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var body = await Http.GetStringAsync(LatestReleaseApi, cts.Token);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            throw new InvalidOperationException($"无法识别的版本号：{tag}");

        if (latest <= CurrentVersion)
            return null;

        string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        string setupUrl = string.Empty;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                if (string.Equals(a.GetProperty("name").GetString(), SetupAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    setupUrl = a.GetProperty("browser_download_url").GetString() ?? "";
                    size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(setupUrl))
            throw new InvalidOperationException($"最新版本 {tag} 中未找到安装包 {SetupAssetName}");

        return new ReleaseInfo(latest, tag, notes, setupUrl, size);
    }

    /// <summary>
    /// 下载安装包到临时目录，直连失败或过慢时自动切换镜像；progress 回调参数：(已下载字节, 总字节, 当前线路说明)
    /// </summary>
    public static async Task<string> DownloadSetupAsync(ReleaseInfo info, Action<long, long, string> progress, CancellationToken token)
    {
        string dir = Path.Combine(Path.GetTempPath(), "AlgerMusicPlayerUpdate");
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, $"AlgerMusicPlayer-Setup-{info.Version}.exe");

        Exception? lastError = null;
        foreach (var prefix in MirrorPrefixes)
        {
            token.ThrowIfCancellationRequested();
            string url = prefix + info.SetupUrl;
            string lineName = prefix.Length == 0 ? "GitHub 直连" : $"镜像 {new Uri(prefix).Host}";
            try
            {
                await DownloadOnceAsync(url, target, info.SetupSize, lineName, progress, token);
                return target;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                try { File.Delete(target); } catch { }
            }
        }

        throw new InvalidOperationException($"所有下载线路均失败：{lastError?.Message}");
    }

    private static async Task DownloadOnceAsync(string url, string target, long expectedSize, string lineName,
        Action<long, long, string> progress, CancellationToken token)
    {
        // 连接阶段超时 15 秒；下载中若连续 20 秒没有数据则视为线路卡死，切换下一条
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        connectCts.CancelAfter(TimeSpan.FromSeconds(15));
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, connectCts.Token);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? expectedSize;
        await using var src = await resp.Content.ReadAsStreamAsync(token);
        await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long done = 0;
        var lastReport = Stopwatch.StartNew();
        while (true)
        {
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            stallCts.CancelAfter(TimeSpan.FromSeconds(20));
            int read;
            try
            {
                read = await src.ReadAsync(buffer, stallCts.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException($"{lineName} 下载停滞");
            }

            if (read == 0) break;
            await dst.WriteAsync(buffer.AsMemory(0, read), token);
            done += read;
            if (lastReport.ElapsedMilliseconds >= 200)
            {
                progress(done, total, lineName);
                lastReport.Restart();
            }
        }

        progress(done, total, lineName);
        if (expectedSize > 0 && done != expectedSize)
            throw new InvalidOperationException($"{lineName} 下载文件不完整（{done}/{expectedSize} 字节）");
    }

    /// <summary>
    /// 以管理员权限启动安装包静默覆盖安装到当前程序目录（安装完成后由安装器自动重启主程序）
    /// </summary>
    public static void LaunchSilentInstaller(string setupPath)
    {
        string installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        Process.Start(new ProcessStartInfo
        {
            FileName = setupPath,
            Arguments = $"--silent \"--dir={installDir}\"",
            UseShellExecute = true,
            Verb = "runas"
        });
    }
}
