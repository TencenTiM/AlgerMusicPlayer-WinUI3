using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlgerMusicPlayer.WinUI.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace AlgerMusicPlayer.WinUI.Services.Unblock;

/// <summary>
/// 落雪音源 (LX Music) JS 脚本宿主：借助隐藏 WebView2 运行脚本（每个脚本独立 Worker），
/// 脚本内 lx.request 发起的网络请求由 C# HttpClient 代理以绕过 CORS
/// </summary>
public sealed class LxScriptHostService
{
    private const string HostName = "lxhost.local";

    private sealed class RunnerState
    {
        public TaskCompletionSource<JsonElement>? InitTcs;
        public JsonElement Sources;
        public bool Initialized;
        public string? LastMusicUrl;
    }

    private readonly HttpClient _httpClient;
    private readonly TaskCompletionSource<bool> _hostReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, RunnerState> _runners = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingCalls = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private CoreWebView2? _core;
    private DispatcherQueue? _dispatcher;
    private long _callSeq;

    public LxScriptHostService()
    {
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = global::System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>
    /// 挂载隐藏 WebView2 并加载宿主页（需在 UI 线程调用）
    /// </summary>
    public async Task AttachAsync(WebView2 view)
    {
        if (_core != null) return;
        try
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            await view.EnsureCoreWebView2Async();
            _core = view.CoreWebView2;
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

    /// <summary>
    /// 使用指定脚本解析歌曲直链，返回 (url, 实际音质)；失败返回 (null, "")
    /// </summary>
    public async Task<(string? Url, string Quality)> GetMusicUrlAsync(
        LxScriptItem script, SongItem song, string preferQuality, CancellationToken ct)
    {
        if (script == null || string.IsNullOrWhiteSpace(script.ScriptContent)) return (null, "");

        var state = await EnsureInitializedAsync(script, ct);
        if (state == null || state.Sources.ValueKind != JsonValueKind.Object) return (null, "");

        // 选择支持 musicUrl 的音源，按 wy > kw > mg > kg > tx 优先
        string? source = null;
        JsonElement srcCfg = default;
        foreach (var key in new[] { "wy", "kw", "mg", "kg", "tx" })
        {
            if (state.Sources.TryGetProperty(key, out var cfg) && HasArrayValue(cfg, "actions", "musicUrl"))
            {
                source = key;
                srcCfg = cfg;
                break;
            }
        }
        if (source == null) return (null, "");

        // 音质回退：所选音质不支持时按 flac24bit > flac > 320k > 128k 选择
        var quality = preferQuality;
        if (!HasArrayValue(srcCfg, "qualitys", quality))
        {
            quality = new[] { "flac24bit", "flac", "320k", "128k" }
                .FirstOrDefault(q => HasArrayValue(srcCfg, "qualitys", q)) ?? preferQuality;
        }

        var payload = new JsonObject
        {
            ["source"] = source,
            ["action"] = "musicUrl",
            ["info"] = new JsonObject
            {
                ["type"] = quality,
                ["musicInfo"] = BuildMusicInfo(song)
            }
        };

        JsonElement result;
        try
        {
            result = await InvokeAsync(script.Id, payload, ct);
        }
        catch
        {
            return (null, "");
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
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return (null, "");

        url = await ResolveAudioUrlAsync(url, ct);
        return (url, url == null ? "" : quality);
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

    private static JsonObject BuildMusicInfo(SongItem song)
    {
        var dur = song.DurationMs;
        var interval = $"{dur / 60000:00}:{dur % 60000 / 1000:00}";
        return new JsonObject
        {
            ["songmid"] = song.NumericId > 0 ? JsonValue.Create(song.NumericId) : JsonValue.Create(song.Id),
            ["name"] = song.Name,
            ["singer"] = string.Join("、", song.Artists.Select(a => a.Name)),
            ["album"] = song.Album?.Name ?? "",
            ["albumId"] = song.Album?.Id.ToString() ?? "",
            ["source"] = "wy",
            ["interval"] = interval,
            ["img"] = song.PicUrl ?? ""
        };
    }

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
