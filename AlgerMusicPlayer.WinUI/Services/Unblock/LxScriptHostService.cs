using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.Api;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;

namespace AlgerMusicPlayer.WinUI.Services.Unblock;

/// <summary>
/// 落雪音源 (LX Music) JS 脚本宿主：使用独立离屏消息窗口挂载 CoreWebView2Controller 运行脚本（每个脚本独立 Worker），
/// 不进入主窗口 DirectComposition 视觉树，避免干扰主窗口 144Hz/240Hz 高刷合成；lx.request 由 C# HttpClient 代理以绕过 CORS
/// </summary>
public sealed class LxScriptHostService : IDisposable
{
    private const string HostName = "lxhost.local";
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    private sealed class RunnerState
    {
        public TaskCompletionSource<JsonElement>? InitTcs;
        public JsonElement Sources;
        public bool Initialized;
        public string? LastMusicUrl;
    }

    private readonly ThirdPartyMusicService _thirdPartyService;
    private readonly HttpClient _httpClient;
    private readonly TaskCompletionSource<bool> _hostReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, RunnerState> _runners = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingCalls = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private IntPtr _offscreenHwnd;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private DispatcherQueue? _dispatcher;
    private long _callSeq;

    public LxScriptHostService(ThirdPartyMusicService thirdPartyService)
    {
        _thirdPartyService = thirdPartyService;
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = global::System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public void RemoveScriptRunner(string scriptId)
    {
        if (!string.IsNullOrWhiteSpace(scriptId))
        {
            _runners.TryRemove(scriptId, out _);
        }
    }

    /// <summary>
    /// 在独立离屏隐藏窗口上初始化 CoreWebView2Controller（完全脱离 MainWindow 视觉树，需在 UI 线程调用）
    /// </summary>
    public async Task InitializeOffscreenAsync(DispatcherQueue dispatcher)
    {
        if (_core != null) return;
        try
        {
            _dispatcher = dispatcher;
            var env = await App.GetSharedWebViewEnvironmentAsync();

            _offscreenHwnd = CreateWindowExW(
                WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                "Static",
                "AlgerLxScriptHost",
                WS_POPUP,
                -32000, -32000, 1, 1,
                HWND_MESSAGE,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (_offscreenHwnd == IntPtr.Zero)
            {
                _offscreenHwnd = CreateWindowExW(
                    WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                    "Static",
                    "AlgerLxScriptHost",
                    WS_POPUP,
                    -32000, -32000, 1, 1,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);
            }

            var windowRef = CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)_offscreenHwnd);
            _controller = await env.CreateCoreWebView2ControllerAsync(windowRef);
            _controller.Bounds = new Rect(0, 0, 1, 1);
            _controller.IsVisible = false;

            _core = _controller.CoreWebView2;
            var folder = Path.Combine(AppContext.BaseDirectory, "LxHost");
            _core.SetVirtualHostNameToFolderMapping(HostName, folder, CoreWebView2HostResourceAccessKind.Allow);
            _core.WebMessageReceived += OnWebMessageReceived;
            _core.Navigate($"https://{HostName}/lx-host.html");
        }
        catch (Exception ex)
        {
            _hostReady.TrySetException(ex);
        }
    }

    public void Dispose()
    {
        try { _controller?.Close(); } catch { }
        _controller = null;
        _core = null;
        if (_offscreenHwnd != IntPtr.Zero)
        {
            try { DestroyWindow(_offscreenHwnd); } catch { }
            _offscreenHwnd = IntPtr.Zero;
        }
        _httpClient.Dispose();
    }

    /// <summary>
    /// 分批并发竞速（每批最多 3 个脚本）+ 首选脚本优先解析歌曲直链，尊重单脚本请求频控
    /// </summary>
    public async Task<(string? Url, string Quality, string ScriptName)> GetMusicUrlFromScriptsAsync(
        IReadOnlyList<LxScriptItem> allScripts,
        string? activeScriptId,
        SongItem song,
        string preferQuality,
        Func<string, Task<bool>>? urlValidator,
        CancellationToken ct)
    {
        if (allScripts == null || allScripts.Count == 0) return (null, "", "");

        var ordered = allScripts
            .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.ScriptContent))
            .OrderByDescending(s => !string.IsNullOrWhiteSpace(activeScriptId) && s.Id == activeScriptId)
            .ToList();

        if (ordered.Count == 0) return (null, "", "");

        bool wantsLossless = preferQuality.StartsWith("flac", StringComparison.OrdinalIgnoreCase);
        (string? Url, string Quality, string ScriptName)? fallback = null;
        const int WaveConcurrency = 3;

        for (int i = 0; i < ordered.Count; i += WaveConcurrency)
        {
            if (ct.IsCancellationRequested) break;

            var waveScripts = ordered.Skip(i).Take(WaveConcurrency).ToList();
            using var waveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tasks = waveScripts
                .Select(s => EvaluateSingleScriptAsync(s, song, preferQuality, urlValidator, waveCts.Token))
                .ToList();

            bool startedLosslessGraceWindow = false;
            while (tasks.Count > 0)
            {
                var finished = await Task.WhenAny(tasks);
                tasks.Remove(finished);

                (string? Url, string Quality, string ScriptName) res;
                try
                {
                    res = await finished;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(res.Url)) continue;

                bool isLossless = res.Quality.StartsWith("flac", StringComparison.OrdinalIgnoreCase) ||
                                  res.Url.Contains(".flac", StringComparison.OrdinalIgnoreCase);
                if (!wantsLossless || isLossless)
                {
                    waveCts.Cancel();
                    return res;
                }

                fallback ??= res;
                if (!startedLosslessGraceWindow && tasks.Count > 0)
                {
                    startedLosslessGraceWindow = true;
                    waveCts.CancelAfter(TimeSpan.FromMilliseconds(1800));
                }
            }
        }

        return fallback ?? (null, "", "");
    }

    /// <summary>
    /// 单独调用指定脚本解析歌曲直链（在其独立专区标签或底栏指定源时，未勾选全局自动轮询也允许直连测试）
    /// </summary>
    public async Task<(string? Url, string Quality, string ScriptName)> GetMusicUrlFromSingleScriptAsync(
        LxScriptItem script,
        SongItem song,
        string preferQuality,
        Func<string, Task<bool>>? urlValidator,
        CancellationToken ct)
    {
        if (script == null || string.IsNullOrWhiteSpace(script.ScriptContent))
        {
            return (null, "", "");
        }

        return await EvaluateSingleScriptAsync(script, song, preferQuality, urlValidator, ct);
    }

    private async Task<(string? Url, string Quality, string ScriptName)> EvaluateSingleScriptAsync(
        LxScriptItem script,
        SongItem song,
        string preferQuality,
        Func<string, Task<bool>>? urlValidator,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return (null, "", "");

        var (url, quality) = await GetMusicUrlAsync(script, song, preferQuality, ct);
        if (string.IsNullOrWhiteSpace(url)) return (null, "", "");

        if (urlValidator != null && !await urlValidator(url))
        {
            return (null, "", "");
        }

        return (url, quality, script.Name);
    }

    /// <summary>
    /// 读取指定洛雪脚本支持的子平台与最高音质摘要（已初始化优先读真实 Sources，未初始化先静态识别）
    /// </summary>
    public (IReadOnlyList<string> SupportedPlatforms, string QualitySummary) GetScriptCapabilitiesSummary(LxScriptItem script)
    {
        string[] knownPlatforms = ["tx", "kg", "kw", "mg", "wy"];
        string[] knownQualities = ["flac24bit", "flac", "320k", "128k"];
        var platforms = new List<string>();
        var qualities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (script != null &&
            _runners.TryGetValue(script.Id, out var state) &&
            state.Initialized &&
            state.Sources.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in knownPlatforms)
            {
                if (!state.Sources.TryGetProperty(key, out var cfg) || !HasArrayValue(cfg, "actions", "musicUrl"))
                {
                    continue;
                }

                platforms.Add(key);
                foreach (var q in knownQualities)
                {
                    if (HasArrayValue(cfg, "qualitys", q))
                    {
                        qualities.Add(q);
                    }
                }
            }
        }
        else if (script != null && !string.IsNullOrWhiteSpace(script.ScriptContent))
        {
            var content = script.ScriptContent;
            foreach (var key in knownPlatforms)
            {
                if (content.Contains($"\"{key}\"", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains($"'{key}'", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains($"{key}:", StringComparison.OrdinalIgnoreCase))
                {
                    platforms.Add(key);
                }
            }

            foreach (var q in knownQualities)
            {
                if (content.Contains($"\"{q}\"", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains($"'{q}'", StringComparison.OrdinalIgnoreCase))
                {
                    qualities.Add(q);
                }
            }
        }

        // 若脚本文本经混淆导致静态扫描未提取到标签，提供通用默认全平台能力并触发后台预热
        if (platforms.Count == 0)
        {
            platforms.AddRange(["tx", "wy", "kg", "kw", "mg"]);
        }

        string qualitySummary = qualities.Contains("flac24bit")
            ? "最高 24bit Hi-Res / FLAC"
            : (qualities.Contains("flac")
                ? "最高无损 FLAC / 320k"
                : (qualities.Contains("320k")
                    ? "最高极高 320k"
                    : "Hi-Res / FLAC / 320k"));

        return (platforms, qualitySummary);
    }

    /// <summary>
    /// 后台静默预热已导入脚本，消除切换专区标签与首次点播时的 Worker 冷启动延迟
    /// </summary>
    public void PreWarmScripts(IEnumerable<LxScriptItem> scripts)
    {
        var list = scripts?
            .Where(s => s != null && !string.IsNullOrWhiteSpace(s.ScriptContent))
            .ToList();
        if (list == null || list.Count == 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await _hostReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch
            {
                return;
            }

            foreach (var script in list)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    await EnsureInitializedAsync(script, cts.Token);
                    await Task.Delay(120);
                }
                catch
                {
                    // 单脚本预热失败不影响后续脚本与主流程
                }
            }
        });
    }

    /// <summary>
    /// 使用指定脚本解析歌曲直链，自动匹配最佳子源（tx/kg/kw/mg/wy）并补齐原生 ID/Hash，返回 (url, 实际音质)
    /// </summary>
    public async Task<(string? Url, string Quality)> GetMusicUrlAsync(
        LxScriptItem script, SongItem song, string preferQuality, CancellationToken ct)
    {
        if (script == null || string.IsNullOrWhiteSpace(script.ScriptContent)) return (null, "");

        var state = await EnsureInitializedAsync(script, ct);
        if (state == null || state.Sources.ValueKind != JsonValueKind.Object) return (null, "");

        var nativeLxSource = MapSongSourceToLxKey(song.Source);
        bool wantsLossless = preferQuality.StartsWith("flac", StringComparison.OrdinalIgnoreCase);

        // 收集该脚本支持 musicUrl 的全部子源并按匹配度与无损能力排序
        var candidates = new List<(string Key, JsonElement Cfg, int Score)>();
        foreach (var key in new[] { "tx", "kg", "kw", "mg", "wy" })
        {
            if (!state.Sources.TryGetProperty(key, out var cfg) || !HasArrayValue(cfg, "actions", "musicUrl"))
            {
                continue;
            }

            int score = 0;
            if (key == nativeLxSource) score += 100;
            if (HasArrayValue(cfg, "qualitys", "flac24bit")) score += 30;
            else if (HasArrayValue(cfg, "qualitys", "flac")) score += 20;
            else if (HasArrayValue(cfg, "qualitys", "320k")) score += 10;

            // 普通网易云歌曲若脚本本身对 wy 支持无损，无需跨平台搜歌，小幅优先
            if (nativeLxSource == "wy" && key == "wy" &&
                (HasArrayValue(cfg, "qualitys", "flac") || HasArrayValue(cfg, "qualitys", "flac24bit")))
            {
                score += 15;
            }

            candidates.Add((key, cfg, score));
        }

        if (candidates.Count == 0) return (null, "");

        (string? Url, string Quality)? fallback = null;
        foreach (var (source, srcCfg, _) in candidates.OrderByDescending(c => c.Score))
        {
            if (ct.IsCancellationRequested) break;

            // 确保歌曲具备当前子源所需的原生 songmid / hash（第三方专区歌曲本身已带，0 开销直接通过）
            if (!await _thirdPartyService.EnsurePlatformMetaAsync(song, source, ct))
            {
                continue;
            }

            var qualityCandidates = BuildQualityFallbackChain(srcCfg, song, source, preferQuality);
            foreach (var quality in qualityCandidates)
            {
                if (ct.IsCancellationRequested) break;

                var url = await InvokeMusicUrlOnceAsync(script.Id, state, song, source, quality, ct);
                if (string.IsNullOrWhiteSpace(url)) continue;

                bool gotLossless = quality.StartsWith("flac", StringComparison.OrdinalIgnoreCase) ||
                                   url.Contains(".flac", StringComparison.OrdinalIgnoreCase);
                if (!wantsLossless || gotLossless)
                {
                    return (url, quality);
                }

                fallback ??= (url, quality);
                break; // 当前子源仅拿到有损，继续看下一个子源是否有无损
            }
        }

        return fallback ?? (null, "");
    }

    private async Task<string?> InvokeMusicUrlOnceAsync(
        string scriptId, RunnerState state, SongItem song, string source, string quality, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["source"] = source,
            ["action"] = "musicUrl",
            ["info"] = new JsonObject
            {
                ["type"] = quality,
                ["musicInfo"] = BuildMusicInfo(song, source, quality)
            }
        };

        JsonElement result;
        try
        {
            result = await InvokeAsync(scriptId, payload, ct);
        }
        catch
        {
            return null;
        }

        string? url = null;
        if (result.ValueKind == JsonValueKind.String) url = result.GetString();
        else if (result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String) url = u.GetString();
            else if (result.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String) url = d.GetString();
        }
        if (string.IsNullOrWhiteSpace(url)) url = state.LastMusicUrl;
        state.LastMusicUrl = null;
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;

        return await ResolveAudioUrlAsync(url, ct);
    }

    private static List<string> BuildQualityFallbackChain(
        JsonElement srcCfg, SongItem song, string source, string preferQuality)
    {
        string[] order = preferQuality switch
        {
            "flac24bit" => ["flac24bit", "flac", "320k", "128k"],
            "flac" => ["flac", "320k", "128k"],
            "320k" => ["320k", "128k"],
            _ => ["128k"]
        };

        var result = new List<string>();
        foreach (var q in order)
        {
            if (!HasArrayValue(srcCfg, "qualitys", q)) continue;
            // 酷狗必须具备对应档位的真实文件 hash 才能请求该档位
            if (source == "kg" && string.IsNullOrWhiteSpace(GetKugouHashForQuality(song, q, strict: true)))
            {
                continue;
            }
            result.Add(q);
        }

        if (result.Count == 0)
        {
            var anySupported = new[] { "flac24bit", "flac", "320k", "128k" }
                .FirstOrDefault(q => HasArrayValue(srcCfg, "qualitys", q));
            if (anySupported != null) result.Add(anySupported);
        }
        return result;
    }

    private static string MapSongSourceToLxKey(string? songSource) =>
        (songSource ?? string.Empty).ToLowerInvariant() switch
        {
            "qq" or "tx" => "tx",
            "kugou" or "kg" => "kg",
            "kuwo" or "kw" => "kw",
            "migu" or "mg" => "mg",
            _ => "wy"
        };

    /// <summary>
    /// 同步无阻塞查询当前已启用落雪脚本对指定歌曲支持的音质标识集合（128k / 320k / flac / flac24bit）
    /// </summary>
    public HashSet<string> GetSupportedQualitiesForSong(IReadOnlyList<LxScriptItem>? allScripts, SongItem song)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (allScripts == null || allScripts.Count == 0 || song == null) return result;

        var nativeKey = MapSongSourceToLxKey(song.Source);
        bool restrictToNative = nativeKey != "wy";
        string[] knownQualities = ["128k", "320k", "flac", "flac24bit"];

        foreach (var script in allScripts)
        {
            if (!script.Enabled || string.IsNullOrWhiteSpace(script.ScriptContent)) continue;

            if (_runners.TryGetValue(script.Id, out var state) &&
                state.Initialized &&
                state.Sources.ValueKind == JsonValueKind.Object)
            {
                if (restrictToNative)
                {
                    if (state.Sources.TryGetProperty(nativeKey, out var cfg) &&
                        HasArrayValue(cfg, "actions", "musicUrl"))
                    {
                        foreach (var q in knownQualities)
                        {
                            if (HasArrayValue(cfg, "qualitys", q))
                            {
                                result.Add(q);
                            }
                        }
                    }
                }
                else
                {
                    foreach (var key in new[] { "tx", "kg", "kw", "mg", "wy" })
                    {
                        if (!state.Sources.TryGetProperty(key, out var cfg) ||
                            !HasArrayValue(cfg, "actions", "musicUrl"))
                        {
                            continue;
                        }

                        foreach (var q in knownQualities)
                        {
                            if (HasArrayValue(cfg, "qualitys", q))
                            {
                                result.Add(q);
                            }
                        }
                    }
                }
            }
            else
            {
                foreach (var q in knownQualities)
                {
                    if (script.ScriptContent.Contains($"\"{q}\"", StringComparison.OrdinalIgnoreCase) ||
                        script.ScriptContent.Contains($"'{q}'", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(q);
                    }
                }
            }
        }

        return result;
    }

    private async Task<RunnerState?> EnsureInitializedAsync(LxScriptItem script, CancellationToken ct)
    {
        try
        {
            using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readyCts.CancelAfter(TimeSpan.FromSeconds(15));
            await _hostReady.Task.WaitAsync(readyCts.Token);
        }
        catch
        {
            return null;
        }

        var state = _runners.GetOrAdd(script.Id, _ => new RunnerState());
        if (state.Initialized) return state;

        Task<JsonElement> initTask;
        await _initLock.WaitAsync(ct);
        try
        {
            if (state.Initialized) return state;
            if (state.InitTcs == null)
            {
                state.InitTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                var msg = new JsonObject
                {
                    ["cmd"] = "init",
                    ["scriptId"] = script.Id,
                    ["script"] = script.ScriptContent,
                    ["scriptInfo"] = new JsonObject
                    {
                        ["name"] = script.Name,
                        ["description"] = script.Description,
                        ["version"] = script.Version,
                        ["author"] = script.Author,
                        ["homepage"] = "",
                        ["rawScript"] = script.ScriptContent
                    }
                };
                PostToHost(msg);
            }
            initTask = state.InitTcs.Task;
        }
        finally
        {
            _initLock.Release();
        }

        try
        {
            // 脚本需在 10 秒内调用 lx.send('inited')
            var sources = await initTask.WaitAsync(TimeSpan.FromSeconds(10), ct);
            state.Sources = sources;
            state.Initialized = true;
            return state;
        }
        catch
        {
            // 初始化失败则重置，允许下次重试
            state.InitTcs = null;
            return null;
        }
    }

    private async Task<JsonElement> InvokeAsync(string scriptId, JsonObject payload, CancellationToken ct)
    {
        var callId = $"call_{Interlocked.Increment(ref _callSeq)}";
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCalls[callId] = tcs;
        try
        {
            PostToHost(new JsonObject
            {
                ["cmd"] = "invoke",
                ["scriptId"] = scriptId,
                ["callId"] = callId,
                ["payload"] = payload
            });
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        }
        finally
        {
            _pendingCalls.TryRemove(callId, out _);
        }
    }

    /// <summary>
    /// 在离屏 WebView2 宿主中动态执行 GD 音乐台 crc32.min.js 的 md5 签名函数（401 自愈兜底）
    /// </summary>
    public async Task<string?> EvaluateGdStudioMd5Async(string crcJsCode, string crcTag, string rawInput, CancellationToken ct)
    {
        if (_core == null || string.IsNullOrWhiteSpace(crcJsCode)) return null;

        try
        {
            await _hostReady.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        catch
        {
            return null;
        }

        var callId = $"gd_{Interlocked.Increment(ref _callSeq)}";
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCalls[callId] = tcs;
        try
        {
            PostToHost(new JsonObject
            {
                ["cmd"] = "gd-md5",
                ["scriptId"] = "__gd__",
                ["callId"] = callId,
                ["crcJs"] = crcJsCode,
                ["crcTag"] = crcTag,
                ["raw"] = rawInput
            });

            var res = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            return res.ValueKind == JsonValueKind.String ? res.GetString() : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _pendingCalls.TryRemove(callId, out _);
        }
    }

    private void PostToHost(JsonObject msg)
    {
        var json = msg.ToJsonString();
        void Send()
        {
            try { _core?.PostWebMessageAsJson(json); } catch { }
        }

        if (_dispatcher == null || _dispatcher.HasThreadAccess) Send();
        else _dispatcher.TryEnqueue(Send);
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(args.WebMessageAsJson);
            root = doc.RootElement.Clone();
        }
        catch
        {
            return;
        }

        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        var scriptId = root.TryGetProperty("scriptId", out var s) ? s.GetString() ?? "" : "";

        switch (type)
        {
            case "host-ready":
                _hostReady.TrySetResult(true);
                break;

            case "initialized":
                if (_runners.TryGetValue(scriptId, out var st1) && root.TryGetProperty("data", out var data))
                {
                    var sources = data.TryGetProperty("sources", out var src) ? src.Clone() : default;
                    st1.InitTcs?.TrySetResult(sources);
                }
                break;

            case "script-error":
                if (_runners.TryGetValue(scriptId, out var st2))
                {
                    var msg = root.TryGetProperty("message", out var m) ? m.GetString() : "脚本错误";
                    st2.InitTcs?.TrySetException(new InvalidOperationException(msg));
                }
                break;

            case "invoke-result":
                if (root.TryGetProperty("callId", out var c1) && _pendingCalls.TryGetValue(c1.GetString() ?? "", out var p1))
                {
                    p1.TrySetResult(root.TryGetProperty("result", out var r) ? r.Clone() : default);
                }
                break;

            case "invoke-error":
                if (root.TryGetProperty("callId", out var c2) && _pendingCalls.TryGetValue(c2.GetString() ?? "", out var p2))
                {
                    var msg = root.TryGetProperty("message", out var m) ? m.GetString() : "调用失败";
                    p2.TrySetException(new InvalidOperationException(msg));
                }
                break;

            case "http-request":
                _ = HandleHttpRequestAsync(scriptId, root);
                break;
        }
    }

    /// <summary>
    /// 代理脚本发起的 lx.request 网络请求
    /// </summary>
    private async Task HandleHttpRequestAsync(string scriptId, JsonElement msg)
    {
        var requestId = msg.GetProperty("requestId").GetString() ?? "";
        var url = msg.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
        var options = msg.TryGetProperty("options", out var o) ? o : default;

        var reply = new JsonObject
        {
            ["cmd"] = "http-response",
            ["scriptId"] = scriptId,
            ["requestId"] = requestId
        };

        try
        {
            var method = GetStr(options, "method")?.ToUpperInvariant() ?? "GET";
            var timeoutMs = options.ValueKind == JsonValueKind.Object && options.TryGetProperty("timeout", out var to) && to.TryGetInt32(out var tv) ? tv : 15000;

            using var req = new HttpRequestMessage(new HttpMethod(method), url);
            string? contentType = null;

            if (options.ValueKind == JsonValueKind.Object && options.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
            {
                foreach (var h in headers.EnumerateObject())
                {
                    var val = h.Value.ValueKind == JsonValueKind.String ? h.Value.GetString() ?? "" : h.Value.GetRawText();
                    if (h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { contentType = val; continue; }
                    req.Headers.TryAddWithoutValidation(h.Name, val);
                }
            }
            if (!req.Headers.Contains("User-Agent"))
            {
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) lx-music-desktop/2.8.0 Chrome/120 Safari/537.36");
            }

            if (options.ValueKind == JsonValueKind.Object)
            {
                if (options.TryGetProperty("body", out var body) && body.ValueKind != JsonValueKind.Null && body.ValueKind != JsonValueKind.Undefined)
                {
                    var text = body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : body.GetRawText();
                    req.Content = new StringContent(text, Encoding.UTF8);
                    req.Content.Headers.ContentType = global::System.Net.Http.Headers.MediaTypeHeaderValue.Parse(
                        contentType ?? (body.ValueKind == JsonValueKind.String ? "text/plain" : "application/json"));
                }
                else if (options.TryGetProperty("form", out var form) && form.ValueKind == JsonValueKind.Object)
                {
                    req.Content = new FormUrlEncodedContent(form.EnumerateObject()
                        .Select(p => new KeyValuePair<string, string>(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText())));
                }
                else if (options.TryGetProperty("formData", out var fd) && fd.ValueKind == JsonValueKind.Object)
                {
                    var mp = new MultipartFormDataContent();
                    foreach (var p in fd.EnumerateObject())
                        mp.Add(new StringContent(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText()), p.Name);
                    req.Content = mp;
                }
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 1000, 60000)));
            using var resp = await _httpClient.SendAsync(req, cts.Token);
            var raw = await resp.Content.ReadAsStringAsync(cts.Token);

            JsonNode? bodyNode;
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            {
                try { bodyNode = JsonNode.Parse(raw); } catch { bodyNode = JsonValue.Create(raw); }
            }
            else
            {
                bodyNode = JsonValue.Create(raw);
            }

            // 记录 body.url 作为脚本未返回结果时的后备直链
            if (bodyNode is JsonObject bo && bo["url"] is JsonValue uv && uv.TryGetValue<string>(out var bu) &&
                _runners.TryGetValue(scriptId, out var st))
            {
                st.LastMusicUrl = bu;
            }

            var headerObj = new JsonObject();
            foreach (var h in resp.Headers.Concat(resp.Content.Headers))
                headerObj[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);

            reply["response"] = new JsonObject
            {
                ["statusCode"] = (int)resp.StatusCode,
                ["statusMessage"] = resp.ReasonPhrase ?? "",
                ["headers"] = headerObj,
                ["body"] = bodyNode?.DeepClone()
            };
            reply["body"] = bodyNode;
        }
        catch (Exception ex)
        {
            reply["error"] = ex.Message;
        }

        PostToHost(reply);
    }

    /// <summary>
    /// 若脚本返回的是 API 端点（返回 JSON），继续提取真实音频地址
    /// </summary>
    private async Task<string?> ResolveAudioUrlAsync(string url, CancellationToken ct)
    {
        var lower = url.ToLowerInvariant();
        if (lower.Contains(".flac") || lower.Contains(".mp3") || lower.Contains(".m4a") || lower.Contains(".ogg") || lower.Contains(".aac"))
            return url;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var resp = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (ctype.StartsWith("audio", StringComparison.OrdinalIgnoreCase) || ctype.Contains("octet-stream")) return url;

            if (ctype.Contains("json") || ctype.StartsWith("text", StringComparison.OrdinalIgnoreCase))
            {
                var text = await resp.Content.ReadAsStringAsync(cts.Token);
                using var doc = JsonDocument.Parse(text);
                var r = doc.RootElement;
                string? found = null;
                if (r.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String) found = u.GetString();
                else if (r.TryGetProperty("data", out var d))
                {
                    if (d.ValueKind == JsonValueKind.String) found = d.GetString();
                    else if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("url", out var du) && du.ValueKind == JsonValueKind.String) found = du.GetString();
                }
                return !string.IsNullOrWhiteSpace(found) && found.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? found : null;
            }
            return url;
        }
        catch
        {
            // 探测失败时保持乐观，直接使用原地址
            return url;
        }
    }

    private static JsonObject BuildMusicInfo(SongItem song, string source, string quality)
    {
        var dur = song.DurationMs;
        var interval = $"{dur / 60000:00}:{dur % 60000 / 1000:00}";
        var albumName = song.Album?.Name ?? "";
        var albumId = song.Album?.Id > 0 ? song.Album.Id.ToString() : "";

        var info = new JsonObject
        {
            ["name"] = song.Name,
            ["singer"] = string.Join("、", song.Artists.Select(a => a.Name)),
            ["album"] = albumName,
            ["albumName"] = albumName,
            ["albumId"] = albumId,
            ["source"] = source,
            ["interval"] = interval,
            ["img"] = song.PicUrl ?? ""
        };

        switch (source)
        {
            case "tx":
                info["songmid"] = song.QqSongMid ?? "";
                info["strMediaMid"] = song.QqSongMid ?? "";
                if (song.QqSongId > 0) info["songId"] = song.QqSongId;
                break;

            case "kg":
                var activeHash = GetKugouHashForQuality(song, quality, strict: false);
                info["hash"] = activeHash;
                info["songmid"] = activeHash;
                info["albumId"] = song.KugouAlbumId ?? albumId;
                var kgTypes = new JsonObject();
                if (!string.IsNullOrWhiteSpace(song.KugouHash))
                    kgTypes["128k"] = new JsonObject { ["size"] = "", ["hash"] = song.KugouHash };
                if (!string.IsNullOrWhiteSpace(song.Kugou320Hash))
                    kgTypes["320k"] = new JsonObject { ["size"] = "", ["hash"] = song.Kugou320Hash };
                if (!string.IsNullOrWhiteSpace(song.KugouSqHash))
                    kgTypes["flac"] = new JsonObject { ["size"] = "", ["hash"] = song.KugouSqHash };
                if (!string.IsNullOrWhiteSpace(song.KugouResHash))
                    kgTypes["flac24bit"] = new JsonObject { ["size"] = "", ["hash"] = song.KugouResHash };
                info["_types"] = kgTypes;
                break;

            case "kw":
                info["songmid"] = song.KuwoRid ?? "";
                break;

            case "mg":
                info["copyrightId"] = song.MiguCopyrightId ?? "";
                info["songmid"] = song.MiguCopyrightId ?? "";
                info["id"] = !string.IsNullOrWhiteSpace(song.MiguContentId) ? song.MiguContentId : (song.MiguCopyrightId ?? "");
                info["lrcUrl"] = song.MiguLrcUrl ?? "";
                break;

            default: // wy
                info["songmid"] = song.NumericId > 0 ? JsonValue.Create(song.NumericId) : JsonValue.Create(song.Id);
                break;
        }

        if (!info.ContainsKey("_types"))
        {
            info["_types"] = new JsonObject
            {
                ["128k"] = new JsonObject { ["size"] = "" },
                ["320k"] = new JsonObject { ["size"] = "" },
                ["flac"] = new JsonObject { ["size"] = "" },
                ["flac24bit"] = new JsonObject { ["size"] = "" }
            };
        }

        return info;
    }

    private static string GetKugouHashForQuality(SongItem song, string quality, bool strict)
    {
        if (strict)
        {
            return quality switch
            {
                "flac24bit" => song.KugouResHash ?? "",
                "flac" => song.KugouSqHash ?? "",
                "320k" => song.Kugou320Hash ?? "",
                "128k" => song.KugouHash ?? "",
                _ => ""
            };
        }

        return quality switch
        {
            "flac24bit" => FirstNonEmpty(song.KugouResHash, song.KugouSqHash, song.Kugou320Hash, song.KugouHash),
            "flac" => FirstNonEmpty(song.KugouSqHash, song.KugouResHash, song.Kugou320Hash, song.KugouHash),
            "320k" => FirstNonEmpty(song.Kugou320Hash, song.KugouHash, song.KugouSqHash),
            _ => FirstNonEmpty(song.KugouHash, song.Kugou320Hash, song.KugouSqHash)
        };
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static bool HasArrayValue(JsonElement obj, string prop, string value)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() == value) return true;
        return false;
    }

    private static string? GetStr(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
