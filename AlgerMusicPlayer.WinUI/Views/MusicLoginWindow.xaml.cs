using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.Auth;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using WinRT.Interop;

namespace AlgerMusicPlayer.WinUI.Views;

/// <summary>
/// 统一 WebView2 登录与凭证嗅探窗口（复用全局共享 WebView2 环境，兼容 Cookie 提取与 Apple MusicKit JS 桥接）
/// </summary>
public sealed partial class MusicLoginWindow : Window
{
    private readonly PlatformAuthConfig _config;
    private readonly AuthFlowType _flowType;
    private readonly TaskCompletionSource<AccountSession?> _tcs = new();
    private DispatcherQueueTimer? _cookiePollTimer;
    private CancellationTokenSource? _appleServerCts;
    private TcpListener? _appleLocalServer;
    private string _generatedDeveloperToken = string.Empty;
    private bool _isCompleted;
    private bool _isExtracting;

    public MusicLoginWindow(PlatformAuthConfig config, AuthFlowType? overrideFlow = null)
    {
        InitializeComponent();
        _config = config;
        _flowType = overrideFlow ?? config.DefaultFlowType;
        Title = $"{config.DisplayName} - 账号登录";

        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        const int targetW = 1040;
        const int targetH = 740;
        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        if (displayArea != null)
        {
            var work = displayArea.WorkArea;
            int x = work.X + Math.Max(0, (work.Width - targetW) / 2);
            int y = work.Y + Math.Max(0, (work.Height - targetH) / 2);
            appWindow.MoveAndResize(new RectInt32(x, y, Math.Min(targetW, work.Width), Math.Min(targetH, work.Height)));
        }
        else
        {
            appWindow.Resize(new SizeInt32(targetW, targetH));
        }

        Closed += MusicLoginWindow_Closed;
        _ = InitializeFlowAsync();
    }

    public Task<AccountSession?> WaitForSessionAsync() => _tcs.Task;

    private async Task InitializeFlowAsync()
    {
        try
        {
            await App.EnsureWebView2Async(AuthWebView);

            if (_flowType == AuthFlowType.AppleMusicKitJs)
            {
                ManualExtractButton.Visibility = Visibility.Collapsed;
                await StartAppleMusicFlowAsync();
            }
            else
            {
                StatusTextBlock.Text = $"请在下方完成 {_config.DisplayName} 登录，检测到有效登录态后将自动加密保存并关闭窗口。";
                AuthWebView.CoreWebView2.Navigate(_config.LoginUrl);
                StartCookiePolling();
            }
        }
        catch (Exception ex)
        {
            CompleteAndClose(null, ex);
        }
    }

    private void StartCookiePolling()
    {
        _cookiePollTimer = DispatcherQueue.CreateTimer();
        _cookiePollTimer.Interval = TimeSpan.FromMilliseconds(1500);
        _cookiePollTimer.Tick += async (_, _) => await TryExtractCookiesAsync(forceAccept: false);
        _cookiePollTimer.Start();
    }

    private async Task TryExtractCookiesAsync(bool forceAccept)
    {
        if (_isCompleted || _isExtracting || AuthWebView.CoreWebView2 == null)
        {
            return;
        }

        _isExtracting = true;
        try
        {
            Dictionary<string, string> cookieDict = new(StringComparer.OrdinalIgnoreCase);

            IEnumerable<string> targetUrls = new[] { _config.CookieTargetUrl }
                .Concat(_config.ExtraCookieUrls ?? [])
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (string url in targetUrls)
            {
                IReadOnlyList<CoreWebView2Cookie> cookieList =
                    await AuthWebView.CoreWebView2.CookieManager.GetCookiesAsync(url);

                foreach (CoreWebView2Cookie c in cookieList)
                {
                    if (!string.IsNullOrEmpty(c.Value))
                    {
                        cookieDict[c.Name] = c.Value;
                    }
                }
            }

            string extractedToken = string.Empty;
            string extractedDevToken = string.Empty;
            string extractedUserId = string.Empty;
            DateTimeOffset? extractedExpiresAt = null;

            // 多平台网页 JS 上下文令牌嗅探（Apple Music / Tidal / Spotify / Migu / KuWo）
            if (_config.Platform == AuthMusicPlatform.AppleMusic)
            {
                (extractedToken, extractedDevToken) = await ProbeAppleWebTokensAsync();
            }
            else if (_config.Platform == AuthMusicPlatform.Tidal)
            {
                extractedToken = await ProbeTidalWebAccessTokenAsync();
            }
            else if (_config.Platform == AuthMusicPlatform.Spotify)
            {
                (extractedToken, extractedExpiresAt) = await ProbeSpotifyWebTokenAsync();
            }
            else if (_config.Platform == AuthMusicPlatform.Migu)
            {
                var (miguToken, miguUid) = await ProbeMiguWebTokensAsync();
                if (!string.IsNullOrWhiteSpace(miguToken) && !cookieDict.ContainsKey("mg_auth_pacmtoken"))
                {
                    cookieDict["mg_auth_pacmtoken"] = miguToken;
                }
                if (!string.IsNullOrWhiteSpace(miguUid) && !cookieDict.ContainsKey("mg_auth_uid"))
                {
                    cookieDict["mg_auth_uid"] = miguUid;
                }
                if (!string.IsNullOrWhiteSpace(miguToken)) extractedToken = miguToken;
                extractedUserId = miguUid;
            }
            else if (_config.Platform == AuthMusicPlatform.KuWo)
            {
                var kwUid = await ProbeKuWoUserIdAsync();
                if (!string.IsNullOrWhiteSpace(kwUid) && !cookieDict.ContainsKey("userid"))
                {
                    cookieDict["userid"] = kwUid;
                }
            }

            // 兼容复合 Cookie 与多域名别名字段提取
            if (_config.Platform == AuthMusicPlatform.YouTubeMusic &&
                !cookieDict.ContainsKey("SAPISID"))
            {
                if (cookieDict.TryGetValue("__Secure-3PAPISID", out string? sec3) && !string.IsNullOrWhiteSpace(sec3))
                {
                    cookieDict["SAPISID"] = sec3;
                }
                else if (cookieDict.TryGetValue("__Secure-1PAPISID", out string? sec1) && !string.IsNullOrWhiteSpace(sec1))
                {
                    cookieDict["SAPISID"] = sec1;
                }
            }
            else if (_config.Platform == AuthMusicPlatform.Qishui &&
                     !cookieDict.ContainsKey("sessionid") &&
                     cookieDict.TryGetValue("sessionid_ss", out string? sidSs) &&
                     !string.IsNullOrWhiteSpace(sidSs))
            {
                cookieDict["sessionid"] = sidSs;
            }
            else if (_config.Platform == AuthMusicPlatform.KuGou &&
                     cookieDict.TryGetValue("KuGoo", out string? kgComposite) &&
                     !string.IsNullOrWhiteSpace(kgComposite))
            {
                foreach (var part in kgComposite.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    var pk = part[..eq].Trim();
                    var pv = part[(eq + 1)..].Trim();
                    if (!string.IsNullOrWhiteSpace(pk) &&
                        !string.IsNullOrWhiteSpace(pv) &&
                        !(pk.Equals("KugooID", StringComparison.OrdinalIgnoreCase) && pv == "0") &&
                        !cookieDict.ContainsKey(pk))
                    {
                        cookieDict[pk] = pv;
                    }
                }
            }
            else if (_config.Platform == AuthMusicPlatform.KuWo &&
                     !cookieDict.ContainsKey("userid"))
            {
                if (cookieDict.TryGetValue("uid", out string? kwU) && !string.IsNullOrWhiteSpace(kwU))
                {
                    cookieDict["userid"] = kwU;
                }
                else if (cookieDict.TryGetValue("loginUid", out string? kwLu) && !string.IsNullOrWhiteSpace(kwLu))
                {
                    cookieDict["userid"] = kwLu;
                }
            }
            else if (_config.Platform == AuthMusicPlatform.AppleMusic &&
                     string.IsNullOrWhiteSpace(extractedToken) &&
                     cookieDict.TryGetValue("media-user-token", out string? mutCookie) &&
                     !string.IsNullOrWhiteSpace(mutCookie))
            {
                extractedToken = mutCookie;
            }

            bool allRequiredPresent = _config.RequiredCookieKeys.All(
                reqKey => cookieDict.TryGetValue(reqKey, out string? val) && !string.IsNullOrWhiteSpace(val));

            if (!allRequiredPresent && _config.Platform == AuthMusicPlatform.AppleMusic && !string.IsNullOrWhiteSpace(extractedToken))
            {
                cookieDict["media-user-token"] = extractedToken;
                allRequiredPresent = true;
            }
            else if (!allRequiredPresent && _config.Platform == AuthMusicPlatform.Tidal && !string.IsNullOrWhiteSpace(extractedToken))
            {
                allRequiredPresent = true;
            }
            else if (!allRequiredPresent && _config.Platform == AuthMusicPlatform.Migu)
            {
                allRequiredPresent = (cookieDict.TryGetValue("mg_auth_pacmtoken", out var mt1) && !string.IsNullOrWhiteSpace(mt1)) ||
                                     (cookieDict.TryGetValue("pacmtoken", out var mt2) && !string.IsNullOrWhiteSpace(mt2)) ||
                                     (cookieDict.TryGetValue("migu_music_uid", out var mu1) && !string.IsNullOrWhiteSpace(mu1)) ||
                                     (cookieDict.TryGetValue("mg_auth_uid", out var mu2) && !string.IsNullOrWhiteSpace(mu2));
            }

            if (!allRequiredPresent && !forceAccept)
            {
                return;
            }

            if (cookieDict.Count == 0 && string.IsNullOrWhiteSpace(extractedToken))
            {
                if (forceAccept)
                {
                    StatusTextBlock.Text = "当前站点下尚未检测到有效 Cookie 或登录 Token，请先完成登录后再提取。";
                }
                return;
            }

            string rawCookieHeader = string.Join("; ", cookieDict.Select(kv => $"{kv.Key}={kv.Value}"));

            AccountSession session = new()
            {
                Platform = _config.Platform,
                FlowType = AuthFlowType.WebViewCookie,
                AccessToken = extractedToken,
                DeveloperToken = extractedDevToken,
                UserId = extractedUserId,
                CookieHeader = rawCookieHeader,
                Cookies = new Dictionary<string, string>(cookieDict, StringComparer.OrdinalIgnoreCase),
                ExpiresAt = extractedExpiresAt,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            CompleteAndClose(session, null);
        }
        catch
        {
            if (forceAccept)
            {
                StatusTextBlock.Text = "提取凭证时发生异常，请确认页面已加载完毕后重试。";
            }
        }
        finally
        {
            _isExtracting = false;
        }
    }

    private async Task<(string UserToken, string DevToken)> ProbeAppleWebTokensAsync()
    {
        try
        {
            var script = """
                (() => {
                    try {
                        let ut = '';
                        let dt = '';
                        if (window.MusicKit && typeof MusicKit.getInstance === 'function') {
                            const inst = MusicKit.getInstance();
                            if (inst) {
                                ut = inst.musicUserToken || '';
                                dt = inst.developerToken || '';
                            }
                        }
                        if (!ut && window.localStorage) {
                            for (let i = 0; i < localStorage.length; i++) {
                                const k = localStorage.key(i) || '';
                                if (k.includes('media-user-token') || k.endsWith('.u')) {
                                    const v = localStorage.getItem(k);
                                    if (v && v.length > 20 && !v.startsWith('{')) { ut = v; break; }
                                }
                            }
                        }
                        return JSON.stringify({ ut, dt });
                    } catch {
                        return '{}';
                    }
                })()
                """;
            var rawResult = await AuthWebView.CoreWebView2.ExecuteScriptAsync(script);
            using var outer = JsonDocument.Parse(rawResult);
            var jsonStr = outer.RootElement.ValueKind == JsonValueKind.String ? outer.RootElement.GetString() : "{}";
            if (!string.IsNullOrWhiteSpace(jsonStr))
            {
                using var doc = JsonDocument.Parse(jsonStr);
                var ut = doc.RootElement.TryGetProperty("ut", out var utProp) ? utProp.GetString() ?? string.Empty : string.Empty;
                var dt = doc.RootElement.TryGetProperty("dt", out var dtProp) ? dtProp.GetString() ?? string.Empty : string.Empty;
                return (ut, dt);
            }
        }
        catch
        {
        }
        return (string.Empty, string.Empty);
    }

    private async Task<string> ProbeTidalWebAccessTokenAsync()
    {
        try
        {
            var script = """
                (() => {
                    try {
                        const storages = [window.localStorage, window.sessionStorage];
                        for (const st of storages) {
                            if (!st) continue;
                            const direct = st.getItem('oAuthAccessToken') || st.getItem('access_token');
                            if (direct && direct.length > 20 && !direct.startsWith('{')) return direct;
                            for (let i = 0; i < st.length; i++) {
                                const k = st.key(i) || '';
                                const v = st.getItem(k) || '';
                                if (!v || v[0] !== '{') continue;
                                try {
                                    const parsed = JSON.parse(v);
                                    if (parsed) {
                                        if (typeof parsed.access_token === 'string' && parsed.access_token.length > 20) return parsed.access_token;
                                        if (typeof parsed.accessToken === 'string' && parsed.accessToken.length > 20) return parsed.accessToken;
                                        if (parsed.oAuthAccessToken && typeof parsed.oAuthAccessToken === 'string') return parsed.oAuthAccessToken;
                                    }
                                } catch {}
                            }
                        }
                        return '';
                    } catch {
                        return '';
                    }
                })()
                """;
            var rawResult = await AuthWebView.CoreWebView2.ExecuteScriptAsync(script);
            using var doc = JsonDocument.Parse(rawResult);
            return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<(string AccessToken, DateTimeOffset? ExpiresAt)> ProbeSpotifyWebTokenAsync()
    {
        try
        {
            var script = """
                (() => {
                    try {
                        let token = '';
                        let expMs = 0;
                        const sesEl = document.getElementById('session');
                        if (sesEl && sesEl.textContent) {
                            try {
                                const ses = JSON.parse(sesEl.textContent);
                                if (ses && !ses.isAnonymous && ses.accessToken) {
                                    token = ses.accessToken;
                                    expMs = Number(ses.accessTokenExpirationTimestampMs || 0);
                                }
                            } catch {}
                        }
                        if (!token && window.__NEXT_DATA__) {
                            const s = window.__NEXT_DATA__?.props?.pageProps?.state?.settings?.session;
                            if (s && !s.isAnonymous && s.accessToken) {
                                token = s.accessToken;
                                expMs = Number(s.accessTokenExpirationTimestampMs || 0);
                            }
                        }
                        if (!token && window.localStorage) {
                            for (let i = 0; i < localStorage.length; i++) {
                                const v = localStorage.getItem(localStorage.key(i) || '') || '';
                                if (!v.includes('accessToken')) continue;
                                try {
                                    const parsed = JSON.parse(v);
                                    if (parsed && typeof parsed.accessToken === 'string' && !parsed.isAnonymous) {
                                        token = parsed.accessToken;
                                        expMs = Number(parsed.accessTokenExpirationTimestampMs || 0);
                                        break;
                                    }
                                } catch {}
                            }
                        }
                        return JSON.stringify({ token, expMs });
                    } catch {
                        return '{}';
                    }
                })()
                """;
            var rawResult = await AuthWebView.CoreWebView2.ExecuteScriptAsync(script);
            using var outer = JsonDocument.Parse(rawResult);
            var jsonStr = outer.RootElement.ValueKind == JsonValueKind.String ? outer.RootElement.GetString() : "{}";
            if (!string.IsNullOrWhiteSpace(jsonStr))
            {
                using var doc = JsonDocument.Parse(jsonStr);
                var tok = doc.RootElement.TryGetProperty("token", out var tp) ? tp.GetString() ?? string.Empty : string.Empty;
                var expMs = doc.RootElement.TryGetProperty("expMs", out var ep) && ep.TryGetInt64(out var l) ? l : 0L;
                DateTimeOffset? exp = expMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(expMs) : null;
                return (tok, exp);
            }
        }
        catch
        {
        }
        return (string.Empty, null);
    }

    private async Task<(string PacmToken, string Uid)> ProbeMiguWebTokensAsync()
    {
        try
        {
            var script = """
                (() => {
                    try {
                        let pacmtoken = '';
                        let uid = '';
                        const storages = [window.localStorage, window.sessionStorage];
                        for (const st of storages) {
                            if (!st) continue;
                            pacmtoken = pacmtoken || st.getItem('mg_auth_pacmtoken') || st.getItem('pacmtoken') || '';
                            uid = uid || st.getItem('mg_auth_uid') || st.getItem('migu_music_uid') || '';
                            for (let i = 0; i < st.length; i++) {
                                const v = st.getItem(st.key(i) || '') || '';
                                if (!v || v[0] !== '{') continue;
                                try {
                                    const obj = JSON.parse(v);
                                    if (obj) {
                                        pacmtoken = pacmtoken || obj.pacmtoken || obj.mg_auth_pacmtoken || obj.userInfo?.pacmtoken || obj.state?.pacmtoken || '';
                                        uid = uid || obj.uid || obj.userId || obj.mg_auth_uid || obj.userInfo?.uid || obj.state?.uid || '';
                                    }
                                } catch {}
                            }
                        }
                        return JSON.stringify({ pacmtoken: String(pacmtoken || ''), uid: String(uid || '') });
                    } catch {
                        return '{}';
                    }
                })()
                """;
            var rawResult = await AuthWebView.CoreWebView2.ExecuteScriptAsync(script);
            using var outer = JsonDocument.Parse(rawResult);
            var jsonStr = outer.RootElement.ValueKind == JsonValueKind.String ? outer.RootElement.GetString() : "{}";
            if (!string.IsNullOrWhiteSpace(jsonStr))
            {
                using var doc = JsonDocument.Parse(jsonStr);
                var tok = doc.RootElement.TryGetProperty("pacmtoken", out var tp) ? tp.GetString() ?? string.Empty : string.Empty;
                var uid = doc.RootElement.TryGetProperty("uid", out var up) ? up.GetString() ?? string.Empty : string.Empty;
                return (tok, uid);
            }
        }
        catch
        {
        }
        return (string.Empty, string.Empty);
    }

    private async Task<string> ProbeKuWoUserIdAsync()
    {
        try
        {
            var script = """
                (() => {
                    try {
                        const storages = [window.localStorage, window.sessionStorage];
                        for (const st of storages) {
                            if (!st) continue;
                            const d = st.getItem('userid') || st.getItem('uid') || st.getItem('loginUid');
                            if (d) return String(d);
                            for (let i = 0; i < st.length; i++) {
                                const v = st.getItem(st.key(i) || '') || '';
                                if (!v || v[0] !== '{') continue;
                                try {
                                    const obj = JSON.parse(v);
                                    const cand = obj?.userid || obj?.uid || obj?.userInfo?.uid || '';
                                    if (cand) return String(cand);
                                } catch {}
                            }
                        }
                        return '';
                    } catch {
                        return '';
                    }
                })()
                """;
            var rawResult = await AuthWebView.CoreWebView2.ExecuteScriptAsync(script);
            using var doc = JsonDocument.Parse(rawResult);
            return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task StartAppleMusicFlowAsync()
    {
        _generatedDeveloperToken = PlatformCryptoHelpers.CreateAppleMusicDeveloperToken(
            _config.AppleTeamId,
            _config.AppleKeyId,
            _config.ApplePrivateKeyPem,
            TimeSpan.FromDays(90));

        AuthWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

        _appleServerCts = new CancellationTokenSource();
        _appleLocalServer = new TcpListener(IPAddress.Loopback, 0);
        _appleLocalServer.Start();
        int port = ((IPEndPoint)_appleLocalServer.LocalEndpoint).Port;

        string html = BuildAppleMusicBridgeHtml(_generatedDeveloperToken);
        _ = ServeAppleBridgeHtmlAsync(_appleLocalServer, html, _appleServerCts.Token);

        StatusTextBlock.Text = "请点击页面中按钮唤起 Apple ID 登录并授权 Apple Music。";
        AuthWebView.CoreWebView2.Navigate($"http://localhost:{port}/");
        await Task.CompletedTask;
    }

    private void CoreWebView2_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(args.WebMessageAsJson);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("musicUserToken", out JsonElement mutProp))
            {
                string userToken = mutProp.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(userToken))
                {
                    AccountSession session = new()
                    {
                        Platform = AuthMusicPlatform.AppleMusic,
                        FlowType = AuthFlowType.AppleMusicKitJs,
                        AccessToken = userToken,
                        DeveloperToken = _generatedDeveloperToken,
                        ExpiresAt = DateTimeOffset.UtcNow.AddDays(90),
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    CompleteAndClose(session, null);
                }
            }
            else if (root.TryGetProperty("error", out JsonElement errProp))
            {
                StatusTextBlock.Text = $"Apple Music 授权错误: {errProp.GetString()}";
            }
        }
        catch
        {
        }
    }

    private static async Task ServeAppleBridgeHtmlAsync(TcpListener server, string html, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(html);
        string header = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        byte[] headerBytes = Encoding.UTF8.GetBytes(header);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                using TcpClient client = await server.AcceptTcpClientAsync(ct);
                using NetworkStream stream = client.GetStream();
                byte[] reqBuf = new byte[2048];
                _ = await stream.ReadAsync(reqBuf, ct);
                await stream.WriteAsync(headerBytes, ct);
                await stream.WriteAsync(body, ct);
                await stream.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string BuildAppleMusicBridgeHtml(string developerToken)
    {
        return $$"""
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="utf-8" />
            <script src="https://js-cdn.music.apple.com/musickit/v3/musickit.js" data-web-components async></script>
            <style>
                body { background:#18181b; color:#f4f4f5; font-family:system-ui,-apple-system,sans-serif; display:flex; flex-direction:column; align-items:center; justify-content:center; height:85vh; margin:0; gap:16px; }
                button { padding:12px 24px; font-size:15px; font-weight:600; border-radius:8px; border:none; background:#fa243c; color:white; cursor:pointer; }
                button:hover { background:#e11d48; }
                p { color:#a1a1aa; font-size:13px; margin:0; }
            </style>
        </head>
        <body>
            <h2>Apple Music 账号授权</h2>
            <p>已加载本地签发的 Developer JWT，请点击下方按钮登录 Apple ID</p>
            <button id="authBtn">使用 Apple ID 登录并授权</button>
            <script>
                document.addEventListener('musickitloaded', async function () {
                    try {
                        await MusicKit.configure({
                            developerToken: '{{developerToken}}',
                            app: { name: 'AlgerMusicPlayer', build: '5.1.5' }
                        });
                    } catch (err) {
                        window.chrome.webview.postMessage({ error: String(err) });
                    }
                });

                document.getElementById('authBtn').addEventListener('click', async function () {
                    try {
                        const music = MusicKit.getInstance();
                        const musicUserToken = await music.authorize();
                        window.chrome.webview.postMessage({ musicUserToken: musicUserToken });
                    } catch (err) {
                        window.chrome.webview.postMessage({ error: String(err) });
                    }
                });
            </script>
        </body>
        </html>
        """;
    }

    private async void ClearCookieButton_Click(object sender, RoutedEventArgs e)
    {
        if (AuthWebView.CoreWebView2 == null) return;

        IEnumerable<string> targetUrls = new[]
            {
                string.IsNullOrEmpty(_config.CookieTargetUrl) ? "https://music.apple.com/" : _config.CookieTargetUrl,
                _config.LoginUrl
            }
            .Concat(_config.ExtraCookieUrls ?? [])
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (string url in targetUrls)
        {
            IReadOnlyList<CoreWebView2Cookie> cookies = await AuthWebView.CoreWebView2.CookieManager.GetCookiesAsync(url);
            foreach (CoreWebView2Cookie cookie in cookies)
            {
                AuthWebView.CoreWebView2.CookieManager.DeleteCookie(cookie);
            }
        }

        AuthWebView.CoreWebView2.Reload();
        StatusTextBlock.Text = "已清除当前站点 Cookie 并刷新页面。";
    }

    private async void ManualExtractButton_Click(object sender, RoutedEventArgs e)
    {
        await TryExtractCookiesAsync(forceAccept: true);
    }

    private void CompleteAndClose(AccountSession? session, Exception? error)
    {
        if (_isCompleted) return;
        _isCompleted = true;

        _cookiePollTimer?.Stop();
        _appleServerCts?.Cancel();
        _appleLocalServer?.Stop();

        if (error != null)
        {
            _tcs.TrySetException(error);
        }
        else
        {
            _tcs.TrySetResult(session);
        }

        Close();
    }

    private void MusicLoginWindow_Closed(object sender, WindowEventArgs args)
    {
        _cookiePollTimer?.Stop();
        _appleServerCts?.Cancel();
        _appleLocalServer?.Stop();

        if (!_isCompleted)
        {
            _isCompleted = true;
            _tcs.TrySetResult(null);
        }
    }
}
