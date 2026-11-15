using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.System;

namespace AlgerMusicPlayer.WinUI.Services.Api;

/// <summary>
/// 纯 C# QQ 音乐双平台登录、独立歌单中心、单曲播放与歌词解析服务
/// （1:1 对应 .worktrees/local-music-folder-tree 新增 gateway/qqRouter.ts、qqServices.ts 与 shared/qqMusicAdapter.ts）
/// </summary>
public partial class QqMusicService
{
    private readonly ConfigService _configService;
    private readonly HttpClient _httpClient;
    private readonly HttpClient _noRedirectClient;
    private readonly HttpClient _localPtClient;

    [GeneratedRegex(@"ptuiCB\('([^']*)',\s*'([^']*)',\s*'([^']*)',\s*'([^']*)',\s*'([^']*)',\s*'([^']*)'\)")]
    private static partial Regex PtuiCbRegex();

    [GeneratedRegex(@"ptui_qlogin_CB\('([^']*)',\s*'([^']*)',\s*'([^']*)'\)")]
    private static partial Regex PtuiQloginCbRegex();

    public QqMusicService(ConfigService configService)
    {
        _configService = configService;

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = false
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        var noRedirectHandler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = false
        };
        _noRedirectClient = new HttpClient(noRedirectHandler)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        _noRedirectClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        var localPtHandler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = false,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        _localPtClient = new HttpClient(localPtHandler)
        {
            Timeout = TimeSpan.FromMilliseconds(2200)
        };
        _localPtClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
    }

    /// <summary>
    /// 计算 QQ PTLogin 二维码校验令牌 (hash33 算法)
    /// </summary>
    public static int ComputePtQrToken(string qrsig)
    {
        int hash = 0;
        foreach (char c in qrsig)
        {
            hash += (hash << 5) + c;
        }
        return hash & 0x7FFFFFFF;
    }

    /// <summary>
    /// 计算 QQ 音乐 g_tk (time33 算法)
    /// </summary>
    public static int ComputeGtk(string cookie)
    {
        string key = ExtractCookieField(cookie, "qqmusic_key");
        if (string.IsNullOrWhiteSpace(key)) key = ExtractCookieField(cookie, "qm_keyst");
        if (string.IsNullOrWhiteSpace(key)) key = ExtractCookieField(cookie, "p_skey");
        if (string.IsNullOrWhiteSpace(key)) key = ExtractCookieField(cookie, "skey");

        int hash = 5381;
        foreach (char c in key)
        {
            hash += (hash << 5) + c;
        }
        return hash & 0x7FFFFFFF;
    }

    public static string ExtractCookieField(string rawCookie, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(rawCookie)) return string.Empty;
        var match = Regex.Match(rawCookie, $@"(?:^|;\s*){Regex.Escape(fieldName)}=([^;]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    public static string ExtractUinFromCookieOrInput(string rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput)) return string.Empty;
        var trimmed = rawInput.Trim();
        if (Regex.IsMatch(trimmed, @"^\d{5,13}$"))
        {
            return trimmed;
        }

        string[] keys = ["uin", "o_cookie", "qqmusic_uin", "p_uin", "wxuin"];
        foreach (var key in keys)
        {
            var val = ExtractCookieField(trimmed, key);
            if (!string.IsNullOrWhiteSpace(val))
            {
                var digits = Regex.Replace(val, @"[^\d]", "");
                digits = digits.TrimStart('0');
                if (digits.Length >= 5) return digits;
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// 生成手机 QQ 扫码登录二维码图片与 qrsig
    /// </summary>
    public async Task<(byte[]? PngBytes, string Qrsig)> CreateQrCodeAsync()
    {
        try
        {
            var url = $"https://ssl.ptlogin2.qq.com/ptqrshow?appid=716027609&e=2&l=M&s=3&d=72&v=4&t={Random.Shared.NextDouble():F6}&daid=383&pt_3rd_aid=100497308";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://xui.ptlogin2.qq.com/");
            using var resp = await _httpClient.SendAsync(req);

            string qrsig = string.Empty;
            if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var sc in setCookies)
                {
                    var m = Regex.Match(sc, @"qrsig=([^;]+)");
                    if (m.Success)
                    {
                        qrsig = m.Groups[1].Value;
                        break;
                    }
                }
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync();
            return (bytes, qrsig);
        }
        catch
        {
            return (null, string.Empty);
        }
    }

    /// <summary>
    /// 轮询手机 QQ 扫码状态（801=待扫码，802=已扫码待确认，800=已过期，803=登录成功）
    /// </summary>
    public async Task<(int StatusCode, string Message, string Cookie, string Uin, string Nickname)> CheckQrStatusAsync(string qrsig)
    {
        if (string.IsNullOrWhiteSpace(qrsig))
        {
            return (800, "二维码参数无效", string.Empty, string.Empty, string.Empty);
        }

        try
        {
            int token = ComputePtQrToken(qrsig);
            var url = $"https://ssl.ptlogin2.qq.com/ptqrlogin?u1=https%3A%2F%2Fgraph.qq.com%2Foauth2.0%2Flogin_jump&ptqrtoken={token}&ptredirect=0&h=1&t=1&g=1&from_ui=1&ptlang=2052&action=0-0-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}&js_ver=24032716&js_type=1&login_sig=&pt_uistyle=40&aid=716027609&daid=383&pt_3rd_aid=100497308&has_onekey=1";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://xui.ptlogin2.qq.com/");
            req.Headers.Add("Cookie", $"qrsig={qrsig}");

            using var resp = await _httpClient.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            var match = PtuiCbRegex().Match(body);
            if (!match.Success)
            {
                return (801, "等待手机 QQ 扫码...", string.Empty, string.Empty, string.Empty);
            }

            var ptCode = match.Groups[1].Value;
            var redirectUrl = match.Groups[3].Value;
            var message = match.Groups[5].Value;
            var nickname = match.Groups[6].Value;

            if (ptCode == "66")
            {
                return (801, "请使用手机 QQ 扫描二维码登录", string.Empty, string.Empty, string.Empty);
            }
            if (ptCode == "67")
            {
                return (802, $"扫码成功 ({nickname})，请在手机 QQ 上点击确认", string.Empty, string.Empty, nickname);
            }
            if (ptCode == "65")
            {
                return (800, "二维码已过期，请点击刷新二维码", string.Empty, string.Empty, string.Empty);
            }
            if (ptCode == "0")
            {
                var cookieParts = new List<string>();
                if (resp.Headers.TryGetValues("Set-Cookie", out var loginCookies))
                {
                    foreach (var sc in loginCookies)
                    {
                        var kv = sc.Split(';')[0].Trim();
                        if (!string.IsNullOrWhiteSpace(kv) && !kv.EndsWith('='))
                        {
                            cookieParts.Add(kv);
                        }
                    }
                }

                string uinHint = string.Empty;
                if (!string.IsNullOrWhiteSpace(redirectUrl))
                {
                    var uinMatch = Regex.Match(redirectUrl, @"uin=(\d+)");
                    if (uinMatch.Success) uinHint = uinMatch.Groups[1].Value;
                }

                var oauthRes = await CompleteOAuthLoginFromCheckSigAsync(redirectUrl, cookieParts, uinHint, nickname);
                if (oauthRes.Success)
                {
                    return (803, string.IsNullOrWhiteSpace(message) ? "QQ 音乐登录成功" : message, oauthRes.Cookie, oauthRes.Uin, oauthRes.Nickname);
                }

                var combinedCookie = string.Join("; ", cookieParts.Distinct());
                var uin = ExtractUinFromCookieOrInput(combinedCookie);
                if (string.IsNullOrWhiteSpace(uin)) uin = uinHint;

                return (803, string.IsNullOrWhiteSpace(message) ? "QQ 音乐登录成功" : message, combinedCookie, uin, nickname);
            }

            return (801, message, string.Empty, string.Empty, nickname);
        }
        catch (Exception ex)
        {
            return (801, $"轮询状态: {ex.Message}", string.Empty, string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// 初始化本机 QQ PTLogin 会话，获取 pt_local_token 与 pt_login_sig
    /// </summary>
    private async Task<(string PtLocalToken, string PtLoginSig)> InitPtLocalSessionAsync()
    {
        string ptLocalToken = string.Empty;
        string ptLoginSig = string.Empty;

        try
        {
            const string xloginUrl = "https://xui.ptlogin2.qq.com/cgi-bin/xlogin?appid=716027609&daid=383&style=33&login_text=%E7%99%BB%E5%BD%95&hide_title_bar=1&hide_border=1&target=self&s_url=https%3A%2F%2Fgraph.qq.com%2Foauth2.0%2Flogin_jump&pt_3rd_aid=100497308&pt_feedback_link=https%3A%2F%2Fsupport.qq.com%2Fproducts%2F77942%3FcustomInfo%3D.appid100497308";
            using var req = new HttpRequestMessage(HttpMethod.Get, xloginUrl);
            req.Headers.Referrer = new Uri("https://graph.qq.com/");
            using var resp = await _httpClient.SendAsync(req);

            if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var sc in setCookies)
                {
                    var tkMatch = Regex.Match(sc, @"pt_local_token=([^;]+)");
                    if (tkMatch.Success && string.IsNullOrWhiteSpace(ptLocalToken))
                    {
                        ptLocalToken = tkMatch.Groups[1].Value.Trim();
                    }
                    var sigMatch = Regex.Match(sc, @"pt_login_sig=([^;]+)");
                    if (sigMatch.Success && string.IsNullOrWhiteSpace(ptLoginSig))
                    {
                        ptLoginSig = sigMatch.Groups[1].Value.Trim();
                    }
                }
            }
        }
        catch
        {
            // 忽略网络异常，使用本地随机令牌兜底
        }

        if (string.IsNullOrWhiteSpace(ptLocalToken))
        {
            ptLocalToken = Random.Shared.Next(100000000, 999999999).ToString();
        }

        return (ptLocalToken, ptLoginSig);
    }

    /// <summary>
    /// 检测当前电脑已登录的 PC QQ / TIM / NTQQ 客户端账号列表（客户端内直接调用，无需跳外部浏览器）
    /// </summary>
    public async Task<List<LocalQqAccountItem>> DetectLocalLoggedInQqAccountsAsync()
    {
        var results = new List<LocalQqAccountItem>();
        var seenUins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var (ptLocalToken, ptLoginSig) = await InitPtLocalSessionAsync();

        (string Scheme, int Port)[] endpoints =
        [
            ("https", 4301),
            ("http", 4300),
            ("https", 4303),
            ("http", 4302),
            ("https", 4305),
            ("http", 4304),
            ("https", 4307),
            ("http", 4306)
        ];

        string[] hosts = ["localhost.ptlogin2.qq.com", "127.0.0.1"];

        foreach (var (scheme, port) in endpoints)
        {
            foreach (var host in hosts)
            {
                try
                {
                    var url = $"{scheme}://{host}:{port}/pt_get_uins?callback=ptui_getuins_CB&r={Random.Shared.NextDouble():F16}&pt_local_tk={Uri.EscapeDataString(ptLocalToken)}";
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Host = $"localhost.ptlogin2.qq.com:{port}";
                    req.Headers.Referrer = new Uri("https://xui.ptlogin2.qq.com/");
                    req.Headers.Add("Cookie", $"pt_local_token={ptLocalToken}");

                    using var resp = await _localPtClient.SendAsync(req);
                    if (!resp.IsSuccessStatusCode) continue;

                    var body = await resp.Content.ReadAsStringAsync();
                    var jsonMatch = Regex.Match(body, @"var_sso_uin_list\s*=\s*(\[[\s\S]*?\])\s*;");
                    if (!jsonMatch.Success) continue;

                    using var doc = JsonDocument.Parse(jsonMatch.Groups[1].Value);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        var uin = el.GetStringProp("uin");
                        if (string.IsNullOrWhiteSpace(uin)) uin = el.GetLongProp("uin").ToString();
                        var account = el.GetStringProp("account");
                        if (string.IsNullOrWhiteSpace(uin) || uin == "0") uin = account;
                        if (string.IsNullOrWhiteSpace(uin) || uin == "0" || !seenUins.Add(uin)) continue;

                        var nick = el.GetStringProp("nickname");
                        var clientType = el.GetIntProp("client_type");

                        results.Add(new LocalQqAccountItem
                        {
                            Uin = uin,
                            Account = string.IsNullOrWhiteSpace(account) ? uin : account,
                            Nickname = string.IsNullOrWhiteSpace(nick) ? $"QQ用户 ({uin})" : nick,
                            ClientType = clientType,
                            Scheme = scheme,
                            Port = port,
                            PtLocalToken = ptLocalToken,
                            PtLoginSig = ptLoginSig
                        });
                    }

                    if (results.Count > 0)
                    {
                        return results;
                    }
                }
                catch
                {
                    // 当前端口未监听则继续尝试下一个端口
                }
            }
        }

        return results;
    }

    /// <summary>
    /// 直接调用本机已登录 PC QQ 账号完成客户端内静默快捷授权登录（无需打开网页）
    /// </summary>
    public async Task<(bool Success, string Message, string Cookie, string Uin, string Nickname)> LoginWithLocalQqAccountAsync(LocalQqAccountItem account)
    {
        if (account == null || string.IsNullOrWhiteSpace(account.Uin))
        {
            return (false, "未选择有效的本机 QQ 账号", string.Empty, string.Empty, string.Empty);
        }

        try
        {
            var ptLocalToken = account.PtLocalToken;
            var ptLoginSig = account.PtLoginSig;
            if (string.IsNullOrWhiteSpace(ptLocalToken))
            {
                (ptLocalToken, ptLoginSig) = await InitPtLocalSessionAsync();
            }

            string clientKey = string.Empty;
            string clientUin = account.Uin;
            string[] hosts = ["localhost.ptlogin2.qq.com", "127.0.0.1"];

            foreach (var host in hosts)
            {
                try
                {
                    var stUrl = $"{account.Scheme}://{host}:{account.Port}/pt_get_st?clientuin={Uri.EscapeDataString(account.Uin)}&r={Random.Shared.NextDouble():F16}&pt_local_tk={Uri.EscapeDataString(ptLocalToken)}&callback=__jp0";
                    using var stReq = new HttpRequestMessage(HttpMethod.Get, stUrl);
                    stReq.Headers.Host = $"localhost.ptlogin2.qq.com:{account.Port}";
                    stReq.Headers.Referrer = new Uri("https://xui.ptlogin2.qq.com/");
                    stReq.Headers.Add("Cookie", $"pt_local_token={ptLocalToken}");

                    using var stResp = await _localPtClient.SendAsync(stReq);
                    if (stResp.Headers.TryGetValues("Set-Cookie", out var stCookies))
                    {
                        foreach (var sc in stCookies)
                        {
                            var ckMatch = Regex.Match(sc, @"clientkey=([^;]+)");
                            if (ckMatch.Success && !string.IsNullOrWhiteSpace(ckMatch.Groups[1].Value))
                            {
                                clientKey = ckMatch.Groups[1].Value.Trim();
                            }
                            var cuMatch = Regex.Match(sc, @"clientuin=([^;]+)");
                            if (cuMatch.Success && !string.IsNullOrWhiteSpace(cuMatch.Groups[1].Value))
                            {
                                clientUin = cuMatch.Groups[1].Value.Trim();
                            }
                        }
                    }

                    var stBody = await stResp.Content.ReadAsStringAsync();
                    if (string.IsNullOrWhiteSpace(clientKey))
                    {
                        var bodyCk = Regex.Match(stBody, @"clientkey\s*[:=]\s*['""]([^'""]+)['""]");
                        if (bodyCk.Success) clientKey = bodyCk.Groups[1].Value.Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(clientKey))
                    {
                        break;
                    }
                }
                catch
                {
                    // 尝试下一个 host
                }
            }

            if (string.IsNullOrWhiteSpace(clientKey))
            {
                return (false, "未能从本机 QQ 客户端获取登录凭证 (clientkey)，请使用客户端内快捷授权窗口", string.Empty, account.Uin, account.Nickname);
            }

            string[] keyIndices = ["19", "9"];
            foreach (var keyIndex in keyIndices)
            {
                var jumpUrl = $"https://ssl.ptlogin2.qq.com/jump?clientuin={Uri.EscapeDataString(clientUin)}&keyindex={keyIndex}&pt_aid=716027609&daid=383&u1=https%3A%2F%2Fgraph.qq.com%2Foauth2.0%2Flogin_jump&pt_local_tk={Uri.EscapeDataString(ptLocalToken)}&pt_3rd_aid=100497308&ptopt=1&style=40";
                using var jumpReq = new HttpRequestMessage(HttpMethod.Get, jumpUrl);
                jumpReq.Headers.Referrer = new Uri("https://xui.ptlogin2.qq.com/");

                var jumpCookieBuilder = new StringBuilder();
                jumpCookieBuilder.Append($"clientuin={clientUin}; clientkey={clientKey}; pt_local_token={ptLocalToken}");
                if (!string.IsNullOrWhiteSpace(ptLoginSig))
                {
                    jumpCookieBuilder.Append($"; pt_login_sig={ptLoginSig}");
                }
                jumpReq.Headers.Add("Cookie", jumpCookieBuilder.ToString());

                using var jumpResp = await _httpClient.SendAsync(jumpReq);
                var jumpBody = await jumpResp.Content.ReadAsStringAsync();

                var jumpCookies = new List<string>();
                if (jumpResp.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    foreach (var sc in setCookies)
                    {
                        var kv = sc.Split(';')[0].Trim();
                        if (!string.IsNullOrWhiteSpace(kv) && !kv.EndsWith('='))
                        {
                            jumpCookies.Add(kv);
                        }
                    }
                }

                string checkSigUrl = string.Empty;
                var qloginMatch = PtuiQloginCbRegex().Match(jumpBody);
                if (qloginMatch.Success)
                {
                    var code = qloginMatch.Groups[1].Value;
                    var urlCandidate = qloginMatch.Groups[2].Value;
                    if (code == "0" && !string.IsNullOrWhiteSpace(urlCandidate))
                    {
                        checkSigUrl = urlCandidate;
                    }
                }
                else
                {
                    var cbMatch = PtuiCbRegex().Match(jumpBody);
                    if (cbMatch.Success && cbMatch.Groups[1].Value == "0")
                    {
                        checkSigUrl = cbMatch.Groups[3].Value;
                    }
                }

                if (!string.IsNullOrWhiteSpace(checkSigUrl) && checkSigUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    var oauthResult = await CompleteOAuthLoginFromCheckSigAsync(checkSigUrl, jumpCookies, account.Uin, account.Nickname);
                    if (oauthResult.Success)
                    {
                        return oauthResult;
                    }
                }
            }

            return (false, "本机 QQ 快速校验未返回有效跳转链接，正在为您打开客户端内快捷登录窗口", string.Empty, account.Uin, account.Nickname);
        }
        catch (Exception ex)
        {
            return (false, $"本机 QQ 快捷登录异常: {ex.Message}", string.Empty, account.Uin, account.Nickname);
        }
    }

    /// <summary>
    /// 从 PTLogin check_sig 链接完成 QQ 互联 OAuth 授权并换取完整 QQ 音乐 Cookie (含 qm_keyst / qqmusic_key)
    /// </summary>
    public async Task<(bool Success, string Message, string Cookie, string Uin, string Nickname)> CompleteOAuthLoginFromCheckSigAsync(
        string checkSigUrl,
        IEnumerable<string>? initialCookies = null,
        string? fallbackUin = null,
        string? fallbackNickname = null)
    {
        var cookieMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void MergeCookiePair(string kv)
        {
            if (string.IsNullOrWhiteSpace(kv)) return;
            var idx = kv.IndexOf('=');
            if (idx <= 0) return;
            var k = kv[..idx].Trim();
            var v = kv[(idx + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v))
            {
                cookieMap[k] = v;
            }
        }

        if (initialCookies != null)
        {
            foreach (var c in initialCookies)
            {
                MergeCookiePair(c.Split(';')[0].Trim());
            }
        }

        string BuildCookieHeader() => string.Join("; ", cookieMap.Select(kv => $"{kv.Key}={kv.Value}"));

        try
        {
            if (!string.IsNullOrWhiteSpace(checkSigUrl) && checkSigUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                using var checkReq = new HttpRequestMessage(HttpMethod.Get, checkSigUrl);
                checkReq.Headers.Referrer = new Uri("https://xui.ptlogin2.qq.com/");
                if (cookieMap.Count > 0)
                {
                    checkReq.Headers.Add("Cookie", BuildCookieHeader());
                }

                using var checkResp = await _noRedirectClient.SendAsync(checkReq);
                if (checkResp.Headers.TryGetValues("Set-Cookie", out var redirectCookies))
                {
                    foreach (var sc in redirectCookies)
                    {
                        MergeCookiePair(sc.Split(';')[0].Trim());
                    }
                }
            }

            // 通过 graph.qq.com/oauth2.0/authorize 换取 QQ 音乐 OAuth code
            if (cookieMap.TryGetValue("p_skey", out var pSkey) && !string.IsNullOrWhiteSpace(pSkey))
            {
                int gtk = ComputeGtk($"p_skey={pSkey}");
                var formFields = new Dictionary<string, string>
                {
                    ["response_type"] = "code",
                    ["client_id"] = "100497308",
                    ["redirect_uri"] = "https://y.qq.com/portal/wx_redirect.html?login_type=1&surl=https://y.qq.com/",
                    ["scope"] = "get_user_info,get_app_friends",
                    ["state"] = "state",
                    ["switch"] = "",
                    ["from_ptlogin"] = "1",
                    ["src"] = "1",
                    ["update_auth"] = "1",
                    ["openapi"] = "1010_1030",
                    ["g_tk"] = gtk.ToString(),
                    ["auth_time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                    ["ui"] = Guid.NewGuid().ToString().ToUpperInvariant()
                };

                using var authReq = new HttpRequestMessage(HttpMethod.Post, "https://graph.qq.com/oauth2.0/authorize")
                {
                    Content = new FormUrlEncodedContent(formFields)
                };
                authReq.Headers.Referrer = new Uri("https://graph.qq.com/oauth2.0/show?which=Login&display=pc&response_type=code&client_id=100497308&redirect_uri=https://y.qq.com/portal/wx_redirect.html?login_type=1&surl=https://y.qq.com/&state=state&display=pc");
                authReq.Headers.Add("Cookie", BuildCookieHeader());

                using var authResp = await _noRedirectClient.SendAsync(authReq);
                if (authResp.Headers.TryGetValues("Set-Cookie", out var authCookies))
                {
                    foreach (var sc in authCookies)
                    {
                        MergeCookiePair(sc.Split(';')[0].Trim());
                    }
                }

                string location = authResp.Headers.Location?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(location) && authResp.Headers.TryGetValues("Location", out var locValues))
                {
                    location = locValues.FirstOrDefault() ?? string.Empty;
                }

                var codeMatch = Regex.Match(location, @"[?&]code=([^&#]+)");
                if (codeMatch.Success)
                {
                    var code = Uri.UnescapeDataString(codeMatch.Groups[1].Value);
                    var exchangeResult = await ExchangeOAuthCodeForQqMusicCookieAsync(code, BuildCookieHeader(), fallbackUin, fallbackNickname);
                    if (exchangeResult.Success)
                    {
                        return exchangeResult;
                    }
                }
            }
        }
        catch
        {
            // 若 OAuth 阶段失败，回退使用已拿到的 Cookie
        }

        var finalCookie = BuildCookieHeader();
        var finalUin = ExtractUinFromCookieOrInput(finalCookie);
        if (string.IsNullOrWhiteSpace(finalUin)) finalUin = ExtractUinFromCookieOrInput(fallbackUin ?? string.Empty);

        if (!string.IsNullOrWhiteSpace(finalCookie) && !string.IsNullOrWhiteSpace(finalUin))
        {
            return (true, "QQ 登录成功", finalCookie, finalUin, fallbackNickname ?? $"QQ用户 ({finalUin})");
        }

        return (false, "未能完成 QQ OAuth 授权", finalCookie, finalUin, fallbackNickname ?? string.Empty);
    }

    /// <summary>
    /// 使用 QQ 互联 OAuth code 向 u.y.qq.com/cgi-bin/musicu.fcg (QQConnectLogin.LoginServer.QQLogin) 换取 qm_keyst 与 qqmusic_uin
    /// </summary>
    public async Task<(bool Success, string Message, string Cookie, string Uin, string Nickname)> ExchangeOAuthCodeForQqMusicCookieAsync(
        string code,
        string? existingCookie = null,
        string? fallbackUin = null,
        string? fallbackNickname = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return (false, "OAuth 授权码为空", string.Empty, fallbackUin ?? string.Empty, fallbackNickname ?? string.Empty);
        }

        var cookieMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void MergeCookiePair(string kv)
        {
            if (string.IsNullOrWhiteSpace(kv)) return;
            var idx = kv.IndexOf('=');
            if (idx <= 0) return;
            var k = kv[..idx].Trim();
            var v = kv[(idx + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v))
            {
                cookieMap[k] = v;
            }
        }

        if (!string.IsNullOrWhiteSpace(existingCookie))
        {
            foreach (var part in existingCookie.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                MergeCookiePair(part.Trim());
            }
        }

        try
        {
            int gtk = ComputeGtk(existingCookie ?? string.Empty);
            var payload = new
            {
                comm = new
                {
                    g_tk = gtk,
                    platform = "yqq",
                    ct = 24,
                    cv = 0
                },
                req = new
                {
                    module = "QQConnectLogin.LoginServer",
                    method = "QQLogin",
                    param = new
                    {
                        code
                    }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Referrer = new Uri("https://y.qq.com/");
            if (cookieMap.Count > 0)
            {
                req.Headers.Add("Cookie", string.Join("; ", cookieMap.Select(kv => $"{kv.Key}={kv.Value}")));
            }

            using var resp = await _noRedirectClient.SendAsync(req);
            if (resp.Headers.TryGetValues("Set-Cookie", out var musicCookies))
            {
                foreach (var sc in musicCookies)
                {
                    MergeCookiePair(sc.Split(';')[0].Trim());
                }
            }

            var json = await resp.Content.ReadAsStringAsync();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("req", out var reqEl) &&
                    reqEl.TryGetProperty("data", out var dataEl) &&
                    dataEl.ValueKind == JsonValueKind.Object)
                {
                    var musicKey = dataEl.GetStringProp("musickey");
                    var musicId = dataEl.GetStringProp("musicid");
                    if (string.IsNullOrWhiteSpace(musicId))
                    {
                        var midLong = dataEl.GetLongProp("musicid");
                        if (midLong > 0) musicId = midLong.ToString();
                    }

                    if (!string.IsNullOrWhiteSpace(musicKey))
                    {
                        cookieMap["qm_keyst"] = musicKey;
                        cookieMap["qqmusic_key"] = musicKey;
                    }
                    if (!string.IsNullOrWhiteSpace(musicId))
                    {
                        cookieMap["uin"] = musicId;
                        cookieMap["qqmusic_uin"] = musicId;
                    }
                }
            }
            catch
            {
                // 忽略 JSON 解析异常，以 Set-Cookie 为准
            }

            var combinedCookie = string.Join("; ", cookieMap.Select(kv => $"{kv.Key}={kv.Value}"));
            var uin = ExtractUinFromCookieOrInput(combinedCookie);
            if (string.IsNullOrWhiteSpace(uin)) uin = ExtractUinFromCookieOrInput(fallbackUin ?? string.Empty);

            if (!string.IsNullOrWhiteSpace(uin))
            {
                return (true, "QQ 音乐快捷登录成功", combinedCookie, uin, fallbackNickname ?? $"QQ用户 ({uin})");
            }
        }
        catch (Exception ex)
        {
            return (false, $"换取 QQ 音乐登录态失败: {ex.Message}", string.Empty, fallbackUin ?? string.Empty, fallbackNickname ?? string.Empty);
        }

        return (false, "未能获取有效的 QQ 音乐登录态", string.Empty, fallbackUin ?? string.Empty, fallbackNickname ?? string.Empty);
    }

    private static string ExtractMusicKey(string cookie)
    {
        var key = ExtractCookieField(cookie, "qm_keyst");
        if (string.IsNullOrWhiteSpace(key)) key = ExtractCookieField(cookie, "qqmusic_key");
        return key;
    }

    private static string ExtractLoginType(string cookie, string musicKey)
    {
        var loginType = ExtractCookieField(cookie, "tmeLoginType");
        if (string.IsNullOrWhiteSpace(loginType)) loginType = ExtractCookieField(cookie, "login_type");
        if (string.IsNullOrWhiteSpace(loginType))
        {
            loginType = musicKey.StartsWith("W_X_", StringComparison.OrdinalIgnoreCase) ? "1" : "2";
        }
        return loginType;
    }

    private static Dictionary<string, object> BuildAuthenticatedMusicuComm(string uin, string cookie, int gtk)
    {
        long uinNum = long.TryParse(uin, out var u) ? u : 0;
        var comm = new Dictionary<string, object>
        {
            ["cv"] = 4747474,
            ["ct"] = 24,
            ["format"] = "json",
            ["inCharset"] = "utf-8",
            ["outCharset"] = "utf-8",
            ["notice"] = 0,
            ["platform"] = "yqq.json",
            ["needNewCode"] = 1,
            ["uin"] = uinNum,
            ["g_tk_new_20200303"] = gtk,
            ["g_tk"] = gtk
        };

        var musicKey = ExtractMusicKey(cookie);
        if (!string.IsNullOrWhiteSpace(musicKey))
        {
            comm["authst"] = musicKey;
            comm["tmeLoginType"] = ExtractLoginType(cookie, musicKey);
            comm["tmeAppID"] = "qqmusic";
        }

        return comm;
    }

    private static PlaylistItem? TryParseCollectedPlaylistElement(JsonElement item, string fallbackCover)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        string dissId = item.GetStringProp("dissid");
        if (string.IsNullOrWhiteSpace(dissId) || dissId == "0") dissId = item.GetStringProp("tid");
        if (string.IsNullOrWhiteSpace(dissId) || dissId == "0") dissId = item.GetStringProp("dirid");
        if (string.IsNullOrWhiteSpace(dissId) || dissId == "0") dissId = item.GetStringProp("id");
        if (string.IsNullOrWhiteSpace(dissId) || dissId == "0") return null;

        string name = item.GetStringProp("dissname");
        if (string.IsNullOrWhiteSpace(name)) name = item.GetStringProp("diss_name");
        if (string.IsNullOrWhiteSpace(name)) name = item.GetStringProp("dirName");
        if (string.IsNullOrWhiteSpace(name)) name = item.GetStringProp("title");
        if (string.IsNullOrWhiteSpace(name)) name = item.GetStringProp("name");
        if (string.IsNullOrWhiteSpace(name)) return null;

        string cover = item.GetStringProp("logo");
        if (string.IsNullOrWhiteSpace(cover)) cover = item.GetStringProp("imgurl");
        if (string.IsNullOrWhiteSpace(cover)) cover = item.GetStringProp("picUrl");
        if (string.IsNullOrWhiteSpace(cover)) cover = item.GetStringProp("picurl");
        if (string.IsNullOrWhiteSpace(cover)) cover = item.GetStringProp("diss_cover");
        if (string.IsNullOrWhiteSpace(cover)) cover = item.GetStringProp("pic");

        int songNum = item.GetIntProp("songnum");
        if (songNum <= 0) songNum = item.GetIntProp("songNum");
        if (songNum <= 0) songNum = item.GetIntProp("song_cnt");
        if (songNum <= 0) songNum = item.GetIntProp("total_song_num");

        string creatorName = item.GetStringProp("nickname");
        if (string.IsNullOrWhiteSpace(creatorName)) creatorName = item.GetStringProp("nick");
        if (string.IsNullOrWhiteSpace(creatorName) &&
            item.TryGetProperty("creator", out var creatorObj) &&
            creatorObj.ValueKind == JsonValueKind.Object)
        {
            creatorName = creatorObj.GetStringProp("name");
            if (string.IsNullOrWhiteSpace(creatorName)) creatorName = creatorObj.GetStringProp("nick");
        }

        return new PlaylistItem
        {
            Id = long.TryParse(dissId, out var lid) ? lid : Math.Abs(dissId.GetHashCode()),
            Source = "qq",
            QqDissId = dissId,
            QqDirId = item.GetIntProp("dirId", item.GetIntProp("dirid")),
            Name = WebUtility.HtmlDecode(name),
            CoverImgUrl = NormalizeCoverUrl(cover, fallbackCover),
            TrackCount = songNum,
            CreatorName = WebUtility.HtmlDecode(creatorName),
            Subscribed = true
        };
    }

    private static string ParseCreatedPlaylistDissId(JsonElement item)
    {
        var dissId = item.GetStringProp("dissid");
        if (string.IsNullOrWhiteSpace(dissId) || dissId == "0") dissId = item.GetStringProp("tid");
        if (string.IsNullOrWhiteSpace(dissId) || dissId == "0") dissId = item.GetStringProp("id");
        return dissId;
    }

    /// <summary>
    /// 获取 QQ 音乐用户资料及创建的歌单与收藏的歌单（对应 gateway/qqServices.ts fetchQqProfileAndPlaylists）
    /// </summary>
    public async Task<(QqUserProfile? Profile, List<PlaylistItem> CreatedPlaylists, List<PlaylistItem> CollectedPlaylists)> GetUserProfileAndPlaylistsAsync(string uinInput, string? cookieInput = null)
    {
        var created = new List<PlaylistItem>();
        var collected = new List<PlaylistItem>();

        var cookie = cookieInput ?? _configService.Settings.QqMusicCookie;
        var uin = ExtractUinFromCookieOrInput(string.IsNullOrWhiteSpace(uinInput) ? cookie : uinInput);
        if (string.IsNullOrWhiteSpace(uin))
        {
            return (null, created, collected);
        }

        var profile = new QqUserProfile
        {
            Uin = uin,
            Nickname = !string.IsNullOrWhiteSpace(_configService.Settings.QqMusicNickname)
                ? _configService.Settings.QqMusicNickname
                : $"QQ音乐用户 ({uin})",
            AvatarUrl = $"https://qlogo4.store.qq.com/qzone/{uin}/{uin}/100"
        };

        int gtk = ComputeGtk(cookie);
        string encryptUin = ExtractCookieField(cookie, "euin");
        var seenCreatedIds = new HashSet<string>(StringComparer.Ordinal);

        // 1. 拉取个人主页（优先 c6.y.qq.com，回退 c.y.qq.com）
        foreach (var host in new[] { "c6.y.qq.com", "c.y.qq.com" })
        {
            try
            {
                var url = $"https://{host}/rsc/fcgi-bin/fcg_get_profile_homepage.fcg?format=json&inCharset=utf8&outCharset=utf-8&cid=205360838&ct=24&userid={Uri.EscapeDataString(uin)}&reqfrom=1&reqtype=0&hostUin=0&loginUin={Uri.EscapeDataString(uin)}&g_tk={gtk}&g_tk_new_20200303={gtk}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Referrer = new Uri("https://y.qq.com/");
                if (!string.IsNullOrWhiteSpace(cookie))
                {
                    req.Headers.Add("Cookie", cookie);
                }

                using var resp = await _httpClient.SendAsync(req);
                if (!resp.IsSuccessStatusCode) continue;

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.GetIntProp("code", -1) != 0 ||
                    !doc.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (data.TryGetProperty("creator", out var creator) && creator.ValueKind == JsonValueKind.Object)
                {
                    var nick = creator.GetStringProp("nick");
                    var headpic = creator.GetStringProp("headpic");
                    var enc = creator.GetStringProp("encrypt_uin");
                    if (!string.IsNullOrWhiteSpace(nick)) profile.Nickname = nick;
                    if (!string.IsNullOrWhiteSpace(headpic)) profile.AvatarUrl = headpic.Replace("http://", "https://");
                    if (!string.IsNullOrWhiteSpace(enc)) encryptUin = enc;
                }

                // 1.1 我喜欢歌单 (mymusic)
                if (data.TryGetProperty("mymusic", out var mymusic) && mymusic.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in mymusic.EnumerateArray())
                    {
                        var dissId = item.GetStringProp("id");
                        var title = item.GetStringProp("title", "我喜欢");
                        var pic = item.GetStringProp("laypic");
                        if (string.IsNullOrWhiteSpace(pic)) pic = item.GetStringProp("picurl");
                        int num = item.GetIntProp("num0");
                        if (!string.IsNullOrWhiteSpace(dissId) && dissId != "0" && seenCreatedIds.Add(dissId))
                        {
                            created.Add(new PlaylistItem
                            {
                                Id = long.TryParse(dissId, out var lid) ? lid : Math.Abs(dissId.GetHashCode()),
                                Source = "qq",
                                QqDissId = dissId,
                                QqDirId = item.GetIntProp("dirid", 201),
                                Name = title,
                                CoverImgUrl = NormalizeCoverUrl(pic, profile.DisplayAvatarUrl),
                                TrackCount = num,
                                CreatorName = profile.Nickname,
                                IsSystemDefaultPlaylist = true
                            });
                        }
                    }
                }

                // 1.2 自建歌单 (mydiss.list)
                if (data.TryGetProperty("mydiss", out var mydiss) &&
                    mydiss.ValueKind == JsonValueKind.Object &&
                    mydiss.TryGetProperty("list", out var dissList) &&
                    dissList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dissList.EnumerateArray())
                    {
                        var dissId = ParseCreatedPlaylistDissId(item);
                        int dirId = item.GetIntProp("dirid");
                        var title = item.GetStringProp("title");
                        var pic = item.GetStringProp("picurl");
                        var subtitle = item.GetStringProp("subtitle");
                        int trackCount = 0;
                        var m = Regex.Match(subtitle, @"(\d+)");
                        if (m.Success) int.TryParse(m.Groups[1].Value, out trackCount);

                        if (!string.IsNullOrWhiteSpace(dissId) && dissId != "0" && seenCreatedIds.Add(dissId))
                        {
                            created.Add(new PlaylistItem
                            {
                                Id = long.TryParse(dissId, out var lid) ? lid : Math.Abs(dissId.GetHashCode()),
                                Source = "qq",
                                QqDissId = dissId,
                                QqDirId = dirId,
                                Name = WebUtility.HtmlDecode(title),
                                CoverImgUrl = NormalizeCoverUrl(pic, profile.DisplayAvatarUrl),
                                TrackCount = trackCount,
                                CreatorName = profile.Nickname,
                                IsSystemDefaultPlaylist = dirId is 201 or 205 or 206
                            });
                        }
                    }
                }

                break;
            }
            catch
            {
                // 主页当前网关异常，尝试回退另一网关或新版接口
            }
        }

        // 1.3 若主页未返回自建歌单且已有登录 Cookie，回退至新版 PlaylistBaseRead.GetPlaylistByUin
        if (created.Count == 0 && !string.IsNullOrWhiteSpace(cookie))
        {
            try
            {
                var payload = new
                {
                    comm = BuildAuthenticatedMusicuComm(uin, cookie, gtk),
                    req_0 = new
                    {
                        module = "music.musicasset.PlaylistBaseRead",
                        method = "GetPlaylistByUin",
                        param = new
                        {
                            uin,
                            bWithoutStatus = false
                        }
                    }
                };
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                req.Headers.Referrer = new Uri("https://y.qq.com/");
                req.Headers.Add("Cookie", cookie);

                using var resp = await _httpClient.SendAsync(req);
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("req_0", out var req0) &&
                    req0.GetIntProp("code", -1) == 0 &&
                    req0.TryGetProperty("data", out var dataEl) &&
                    dataEl.TryGetProperty("v_playlist", out var vPlaylist) &&
                    vPlaylist.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in vPlaylist.EnumerateArray())
                    {
                        var dissId = ParseCreatedPlaylistDissId(item);
                        if (string.IsNullOrWhiteSpace(dissId) || dissId == "0" || !seenCreatedIds.Add(dissId)) continue;

                        var title = item.GetStringProp("dirName");
                        if (string.IsNullOrWhiteSpace(title)) title = item.GetStringProp("title", "未命名歌单");
                        var pic = item.GetStringProp("picUrl");
                        if (string.IsNullOrWhiteSpace(pic)) pic = item.GetStringProp("bigpicUrl");
                        int dirId = item.GetIntProp("dirId");
                        created.Add(new PlaylistItem
                        {
                            Id = long.TryParse(dissId, out var lid) ? lid : Math.Abs(dissId.GetHashCode()),
                            Source = "qq",
                            QqDissId = dissId,
                            QqDirId = dirId,
                            Name = WebUtility.HtmlDecode(title),
                            CoverImgUrl = NormalizeCoverUrl(pic, profile.DisplayAvatarUrl),
                            TrackCount = item.GetIntProp("songNum"),
                            CreatorName = profile.Nickname,
                            IsSystemDefaultPlaylist = dirId is 201 or 205 or 206
                        });
                    }
                }
            }
            catch
            {
                // 忽略新版自建歌单补全异常
            }
        }

        // 2. 多通道拉取 QQ 音乐收藏的歌单（按 QqDissId 去重合并）
        var seenCollectedIds = new HashSet<string>(StringComparer.Ordinal);
        void AddCollectedIfNew(PlaylistItem? pl)
        {
            if (pl == null || string.IsNullOrWhiteSpace(pl.QqDissId)) return;
            if (seenCollectedIds.Add(pl.QqDissId))
            {
                collected.Add(pl);
            }
        }

        // 通道 A：c6.y.qq.com / c.y.qq.com fcg_get_profile_order_asset.fcg（优先 encryptUin，其次 uin，支持分页）
        var candidateUserIds = new List<string>();
        if (!string.IsNullOrWhiteSpace(encryptUin)) candidateUserIds.Add(encryptUin);
        if (!candidateUserIds.Contains(uin, StringComparer.Ordinal)) candidateUserIds.Add(uin);

        foreach (var targetUserId in candidateUserIds)
        {
            int fetchedForId = 0;
            const int pageSize = 50;
            for (int sin = 0; sin < 500; sin += pageSize)
            {
                int ein = sin + pageSize;
                JsonDocument? pageDoc = null;

                foreach (var host in new[] { "c6.y.qq.com", "c.y.qq.com" })
                {
                    try
                    {
                        var favUrl = $"https://{host}/fav/fcgi-bin/fcg_get_profile_order_asset.fcg?format=json&inCharset=utf8&outCharset=utf-8&ct=20&cid=205360956&userid={Uri.EscapeDataString(targetUserId)}&reqtype=3&sin={sin}&ein={ein}&g_tk={gtk}&g_tk_new_20200303={gtk}&loginUin={Uri.EscapeDataString(uin)}";
                        using var favReq = new HttpRequestMessage(HttpMethod.Get, favUrl);
                        favReq.Headers.Referrer = new Uri("https://y.qq.com/n/ryqq/profile/like/playlist");
                        if (!string.IsNullOrWhiteSpace(cookie))
                        {
                            favReq.Headers.Add("Cookie", cookie);
                        }

                        using var favResp = await _httpClient.SendAsync(favReq);
                        if (!favResp.IsSuccessStatusCode) continue;

                        var favJson = await favResp.Content.ReadAsStringAsync();
                        var candidateDoc = JsonDocument.Parse(favJson);
                        if (candidateDoc.RootElement.GetIntProp("code", -1) == 0 &&
                            candidateDoc.RootElement.TryGetProperty("data", out var dObj) &&
                            dObj.ValueKind == JsonValueKind.Object)
                        {
                            pageDoc = candidateDoc;
                            break;
                        }
                        candidateDoc.Dispose();
                    }
                    catch
                    {
                        // 当前域名请求失败则回退下一个域名
                    }
                }

                if (pageDoc == null) break;

                using (pageDoc)
                {
                    var favData = pageDoc.RootElement.GetProperty("data");
                    int totalDiss = favData.GetIntProp("totaldiss");
                    int pageCount = 0;

                    if (favData.TryGetProperty("cdlist", out var cdList) && cdList.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in cdList.EnumerateArray())
                        {
                            pageCount++;
                            var parsed = TryParseCollectedPlaylistElement(item, profile.DisplayAvatarUrl);
                            if (parsed != null)
                            {
                                AddCollectedIfNew(parsed);
                                fetchedForId++;
                            }
                        }
                    }

                    if (pageCount == 0 || (totalDiss > 0 && sin + pageCount >= totalDiss) || pageCount < pageSize)
                    {
                        break;
                    }
                }
            }

            if (fetchedForId > 0) break;
        }

        // 通道 B：新版 u.y.qq.com musicu.fcg -> music.musicasset.PlaylistFavRead.GetPlaylistFavInfo
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            const int rpcPageSize = 50;
            for (int start = 0; start < 500; start += rpcPageSize)
            {
                try
                {
                    var payload = new
                    {
                        comm = BuildAuthenticatedMusicuComm(uin, cookie, gtk),
                        req_0 = new
                        {
                            module = "music.musicasset.PlaylistFavRead",
                            method = "GetPlaylistFavInfo",
                            param = new
                            {
                                uin,
                                start,
                                number = rpcPageSize,
                                num = rpcPageSize
                            }
                        }
                    };

                    using var rpcReq = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
                    {
                        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                    };
                    rpcReq.Headers.Referrer = new Uri("https://y.qq.com/n/ryqq/profile/like/playlist");
                    rpcReq.Headers.Add("Cookie", cookie);

                    using var rpcResp = await _httpClient.SendAsync(rpcReq);
                    if (!rpcResp.IsSuccessStatusCode) break;

                    var rpcJson = await rpcResp.Content.ReadAsStringAsync();
                    using var rpcDoc = JsonDocument.Parse(rpcJson);
                    if (!rpcDoc.RootElement.TryGetProperty("req_0", out var req0) ||
                        req0.GetIntProp("code", -1) != 0 ||
                        !req0.TryGetProperty("data", out var rpcData) ||
                        rpcData.ValueKind != JsonValueKind.Object)
                    {
                        break;
                    }

                    int batchCount = 0;
                    if (rpcData.TryGetProperty("v_list", out var vList) && vList.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in vList.EnumerateArray())
                        {
                            batchCount++;
                            AddCollectedIfNew(TryParseCollectedPlaylistElement(item, profile.DisplayAvatarUrl));
                        }
                    }

                    bool hasMore = rpcData.GetIntProp("hasmore") == 1;
                    if (!hasMore || batchCount == 0) break;
                }
                catch
                {
                    break;
                }
            }
        }

        // 通道 C：兜底调用 PlaylistFavRead.GetPlaylistFavList 获取收藏 tid 列表，再批量补全详情
        if (collected.Count == 0 && !string.IsNullOrWhiteSpace(cookie))
        {
            try
            {
                var listPayload = new
                {
                    comm = BuildAuthenticatedMusicuComm(uin, cookie, gtk),
                    req_0 = new
                    {
                        module = "music.musicasset.PlaylistFavRead",
                        method = "GetPlaylistFavList",
                        param = new
                        {
                            uin,
                            start = 0,
                            number = 100,
                            num = 100,
                            size = 100
                        }
                    }
                };

                using var listReq = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
                {
                    Content = new StringContent(JsonSerializer.Serialize(listPayload), Encoding.UTF8, "application/json")
                };
                listReq.Headers.Referrer = new Uri("https://y.qq.com/");
                listReq.Headers.Add("Cookie", cookie);

                using var listResp = await _httpClient.SendAsync(listReq);
                var listJson = await listResp.Content.ReadAsStringAsync();
                using var listDoc = JsonDocument.Parse(listJson);

                var tidList = new List<long>();
                if (listDoc.RootElement.TryGetProperty("req_0", out var req0) &&
                    req0.GetIntProp("code", -1) == 0 &&
                    req0.TryGetProperty("data", out var listData) &&
                    listData.TryGetProperty("v_tids", out var vTids) &&
                    vTids.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in vTids.EnumerateArray())
                    {
                        long tidVal = 0;
                        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n)) tidVal = n;
                        else if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var s)) tidVal = s;
                        else if (el.ValueKind == JsonValueKind.Object)
                        {
                            tidVal = el.GetLongProp("tid", el.GetLongProp("dissid"));
                        }

                        if (tidVal > 0 && !tidList.Contains(tidVal)) tidList.Add(tidVal);
                    }
                }

                // 批量通过 uniform_get_Dissinfo 补全标题、封面、创建者与曲目数（每批最多 20 个）
                for (int offset = 0; offset < tidList.Count; offset += 20)
                {
                    var chunk = tidList.Skip(offset).Take(20).ToList();
                    var batchBody = new Dictionary<string, object>
                    {
                        ["comm"] = BuildAuthenticatedMusicuComm(uin, cookie, gtk)
                    };
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        batchBody[$"req_{i}"] = new
                        {
                            module = "music.srfDissInfo.aiDissInfo",
                            method = "uniform_get_Dissinfo",
                            param = new
                            {
                                disstid = chunk[i],
                                enc_host_uin = "",
                                tag = 1,
                                userinfo = 1,
                                song_begin = 0,
                                song_num = 1,
                                onlysonglist = 0
                            }
                        };
                    }

                    using var batchReq = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
                    {
                        Content = new StringContent(JsonSerializer.Serialize(batchBody), Encoding.UTF8, "application/json")
                    };
                    batchReq.Headers.Referrer = new Uri("https://y.qq.com/");
                    batchReq.Headers.Add("Cookie", cookie);

                    using var batchResp = await _httpClient.SendAsync(batchReq);
                    if (!batchResp.IsSuccessStatusCode) continue;

                    var batchJson = await batchResp.Content.ReadAsStringAsync();
                    using var batchDoc = JsonDocument.Parse(batchJson);
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        if (!batchDoc.RootElement.TryGetProperty($"req_{i}", out var itemReq) ||
                            itemReq.GetIntProp("code", -1) != 0 ||
                            !itemReq.TryGetProperty("data", out var itemData) ||
                            !itemData.TryGetProperty("dirinfo", out var dirinfo))
                        {
                            continue;
                        }

                        var tidStr = chunk[i].ToString();
                        var title = dirinfo.GetStringProp("title");
                        if (string.IsNullOrWhiteSpace(title)) title = dirinfo.GetStringProp("dissname");
                        if (string.IsNullOrWhiteSpace(title)) continue;

                        var pic = dirinfo.GetStringProp("picurl");
                        if (string.IsNullOrWhiteSpace(pic)) pic = dirinfo.GetStringProp("picurl2");
                        var creatorNick = dirinfo.GetStringProp("host_nick");
                        if (string.IsNullOrWhiteSpace(creatorNick) &&
                            dirinfo.TryGetProperty("creator", out var cObj) &&
                            cObj.ValueKind == JsonValueKind.Object)
                        {
                            creatorNick = cObj.GetStringProp("nick");
                        }

                        AddCollectedIfNew(new PlaylistItem
                        {
                            Id = chunk[i],
                            Source = "qq",
                            QqDissId = tidStr,
                            QqDirId = dirinfo.GetIntProp("dirid"),
                            Name = WebUtility.HtmlDecode(title),
                            CoverImgUrl = NormalizeCoverUrl(pic, profile.DisplayAvatarUrl),
                            TrackCount = dirinfo.GetIntProp("songnum"),
                            CreatorName = WebUtility.HtmlDecode(creatorNick),
                            Subscribed = true
                        });
                    }
                }
            }
            catch
            {
                // 忽略兜底收藏列表异常
            }
        }

        return (profile, created, collected);
    }

    /// <summary>
    /// 查询指定 QQ 音乐歌单是否已被当前账号收藏（music.musicasset.PlaylistFavRead -> IsPlaylistFan）
    /// </summary>
    public async Task<bool> IsPlaylistCollectedAsync(string dissId, CancellationToken ct = default)
    {
        if (!long.TryParse(dissId?.Trim(), out var tid) || tid <= 0) return false;

        var cookie = _configService.Settings.QqMusicCookie;
        var uin = ExtractUinFromCookieOrInput(_configService.Settings.QqMusicUin);
        if (string.IsNullOrWhiteSpace(uin)) uin = ExtractUinFromCookieOrInput(cookie);
        if (string.IsNullOrWhiteSpace(uin) || string.IsNullOrWhiteSpace(cookie)) return false;

        int gtk = ComputeGtk(cookie);

        try
        {
            var payload = new
            {
                comm = BuildAuthenticatedMusicuComm(uin, cookie, gtk),
                req_0 = new
                {
                    module = "music.musicasset.PlaylistFavRead",
                    method = "IsPlaylistFan",
                    param = new
                    {
                        v_tid = new[] { tid }
                    }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Referrer = new Uri("https://y.qq.com/");
            req.Headers.Add("Cookie", cookie);

            using var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return false;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("req_0", out var req0) &&
                req0.GetIntProp("code", -1) == 0 &&
                req0.TryGetProperty("data", out var data) &&
                data.TryGetProperty("m_fan", out var mFan) &&
                mFan.ValueKind == JsonValueKind.Object &&
                mFan.TryGetProperty(tid.ToString(), out var fanVal))
            {
                return fanVal.ValueKind == JsonValueKind.True ||
                       (fanVal.ValueKind == JsonValueKind.Number && fanVal.TryGetInt32(out var num) && num > 0);
            }
        }
        catch
        {
            // 忽略收藏状态查询异常
        }

        return false;
    }

    /// <summary>
    /// 收藏或取消收藏 QQ 音乐歌单（music.musicasset.PlaylistFavWrite -> FavPlaylist / CancelFavPlaylist）
    /// </summary>
    public async Task<bool> SubscribePlaylistAsync(string dissId, bool collect, CancellationToken ct = default)
    {
        if (!long.TryParse(dissId?.Trim(), out var tid) || tid <= 0) return false;

        var cookie = _configService.Settings.QqMusicCookie;
        var uin = ExtractUinFromCookieOrInput(_configService.Settings.QqMusicUin);
        if (string.IsNullOrWhiteSpace(uin)) uin = ExtractUinFromCookieOrInput(cookie);
        if (string.IsNullOrWhiteSpace(uin) || string.IsNullOrWhiteSpace(cookie)) return false;

        int gtk = ComputeGtk(cookie);

        try
        {
            var payload = new
            {
                comm = BuildAuthenticatedMusicuComm(uin, cookie, gtk),
                req_0 = new
                {
                    module = "music.musicasset.PlaylistFavWrite",
                    method = collect ? "FavPlaylist" : "CancelFavPlaylist",
                    param = new
                    {
                        uin,
                        v_playlistId = new[] { tid }
                    }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Referrer = new Uri("https://y.qq.com/");
            req.Headers.Add("Cookie", cookie);

            using var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return false;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("req_0", out var req0) &&
                req0.GetIntProp("code", -1) == 0)
            {
                return true;
            }
        }
        catch
        {
            // 忽略收藏操作异常
        }

        return false;
    }

    /// <summary>
    /// 获取 QQ 音乐歌单详情与全部歌曲（对应 gateway/qqServices.ts getQqPlaylistDetail &amp; shared/qqMusicAdapter.ts）
    /// </summary>
    public async Task<PlaylistItem?> GetPlaylistDetailAsync(string dissId, string? cookieInput = null)
    {
        if (string.IsNullOrWhiteSpace(dissId)) return null;

        var cleanDissId = dissId.Trim();
        var cookie = cookieInput ?? _configService.Settings.QqMusicCookie;
        var uin = ExtractUinFromCookieOrInput(_configService.Settings.QqMusicUin);
        if (string.IsNullOrWhiteSpace(uin)) uin = "0";
        int gtk = ComputeGtk(cookie);

        // 优先走新版 musicu uniform_get_Dissinfo（匿名与登录均稳定返回歌单信息与完整曲目列表）
        if (long.TryParse(cleanDissId, out var disstidNum) && disstidNum > 0)
        {
            try
            {
                const int pageSize = 200;
                int songBegin = 0;
                int expectedTotal = 0;
                string title = string.Empty;
                string picUrl = string.Empty;
                string desc = string.Empty;
                string creatorNick = string.Empty;
                var allTracks = new List<SongItem>();
                var seenIds = new HashSet<string>(StringComparer.Ordinal);

                for (int page = 0; page < 4; page++)
                {
                    var payload = new
                    {
                        comm = new { ct = 24, cv = 0, uin = string.IsNullOrWhiteSpace(uin) ? "0" : uin },
                        req = new
                        {
                            module = "music.srfDissInfo.aiDissInfo",
                            method = "uniform_get_Dissinfo",
                            param = new
                            {
                                disstid = disstidNum,
                                enc_host_uin = "",
                                tag = 1,
                                userinfo = page == 0 ? 1 : 0,
                                song_begin = songBegin,
                                song_num = pageSize,
                                onlysonglist = page == 0 ? 0 : 1
                            }
                        }
                    };

                    using var musicuReq = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
                    {
                        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                    };
                    musicuReq.Headers.Referrer = new Uri("https://y.qq.com/");
                    if (!string.IsNullOrWhiteSpace(cookie)) musicuReq.Headers.Add("Cookie", cookie);

                    using var musicuResp = await _httpClient.SendAsync(musicuReq);
                    if (!musicuResp.IsSuccessStatusCode) break;

                    var musicuJson = await musicuResp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(musicuJson);
                    if (!doc.RootElement.TryGetProperty("req", out var reqEl) ||
                        !reqEl.TryGetProperty("data", out var dataEl))
                    {
                        break;
                    }

                    if (page == 0 && dataEl.TryGetProperty("dirinfo", out var dirinfo))
                    {
                        title = dirinfo.GetStringProp("title");
                        picUrl = dirinfo.GetStringProp("picurl");
                        if (string.IsNullOrWhiteSpace(picUrl)) picUrl = dirinfo.GetStringProp("picurl2");
                        desc = dirinfo.GetStringProp("desc");
                        creatorNick = dirinfo.GetStringProp("host_nick");
                        if (string.IsNullOrWhiteSpace(creatorNick) &&
                            dirinfo.TryGetProperty("creator", out var creatorEl) &&
                            creatorEl.ValueKind == JsonValueKind.Object)
                        {
                            creatorNick = creatorEl.GetStringProp("nick");
                        }
                        expectedTotal = dirinfo.GetIntProp("songnum");
                    }

                    int pageFetched = 0;
                    int newlyAdded = 0;
                    if (dataEl.TryGetProperty("songlist", out var songListEl) &&
                        songListEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in songListEl.EnumerateArray())
                        {
                            pageFetched++;
                            var song = NormalizeQqSong(s);
                            if (song != null && seenIds.Add(song.Id))
                            {
                                allTracks.Add(song);
                                newlyAdded++;
                            }
                        }
                    }

                    if (pageFetched == 0 || newlyAdded == 0 || (expectedTotal > 0 ? allTracks.Count >= expectedTotal : pageFetched < pageSize))
                        break;

                    songBegin += pageFetched;
                }

                if (allTracks.Count > 0 || !string.IsNullOrWhiteSpace(title))
                {
                    return new PlaylistItem
                    {
                        Id = disstidNum,
                        QqDissId = cleanDissId,
                        Name = string.IsNullOrWhiteSpace(title) ? "QQ 音乐歌单" : title,
                        CoverImgUrl = NormalizeCoverUrl(picUrl, ImageUriHelper.DefaultCoverUri),
                        Description = desc,
                        CreatorName = creatorNick,
                        TrackCount = expectedTotal > 0 ? expectedTotal : allTracks.Count,
                        Tracks = allTracks,
                        Source = "qq"
                    };
                }
            }
            catch
            {
                // 回退到旧版 c.y.qq.com 接口
            }
        }
        try
        {
            var url = $"https://c.y.qq.com/qzone/fcg-bin/fcg_ucc_getcdinfo_byids_cp.fcg?type=1&json=1&utf8=1&onlysong=0&new_format=1&disstid={Uri.EscapeDataString(cleanDissId)}&g_tk={gtk}&loginUin={uin}&hostUin=0&format=json&inCharset=utf8&outCharset=utf-8&notice=0&platform=yqq.json&needNewCode=0";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://y.qq.com/n/ryqq/playlist");
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                req.Headers.Add("Cookie", cookie);
            }

            using var resp = await _httpClient.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);

            if (!doc.RootElement.TryGetProperty("cdlist", out var cdlist) ||
                cdlist.ValueKind != JsonValueKind.Array ||
                cdlist.GetArrayLength() == 0)
            {
                return null;
            }

            var cd = cdlist[0];
            var result = new PlaylistItem
            {
                Id = long.TryParse(dissId, out var lid) ? lid : Math.Abs(dissId.GetHashCode()),
                Source = "qq",
                QqDissId = dissId,
                QqDirId = cd.GetIntProp("dirid"),
                Name = WebUtility.HtmlDecode(cd.GetStringProp("dissname", "QQ 音乐歌单")),
                CoverImgUrl = NormalizeCoverUrl(cd.GetStringProp("logo"), ImageUriHelper.DefaultCoverUri),
                Description = WebUtility.HtmlDecode(cd.GetStringProp("desc")),
                CreatorName = WebUtility.HtmlDecode(cd.GetStringProp("nickname", "QQ音乐")),
                PlayCount = cd.GetLongProp("visitnum"),
                TrackCount = cd.GetIntProp("songnum")
            };

            if (cd.TryGetProperty("songlist", out var songlist) && songlist.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in songlist.EnumerateArray())
                {
                    var song = NormalizeQqSong(s);
                    if (song != null)
                    {
                        result.Tracks.Add(song);
                    }
                }
                result.TrackCount = result.Tracks.Count;
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 将 QQ 音乐原始歌曲 JSON 转换为统一 SongItem 模型（对应 shared/qqMusicAdapter.ts normalizeQqSong）
    /// </summary>
    public static SongItem? NormalizeQqSong(JsonElement item)
    {
        var mid = item.GetStringProp("mid");
        if (string.IsNullOrWhiteSpace(mid)) mid = item.GetStringProp("songmid");
        var idNum = item.GetLongProp("id");
        if (idNum == 0) idNum = item.GetLongProp("songid");

        var title = item.GetStringProp("title");
        if (string.IsNullOrWhiteSpace(title)) title = item.GetStringProp("songname");
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(mid)) return null;

        var artists = new List<ArtistItem>();
        if (item.TryGetProperty("singer", out var singers) && singers.ValueKind == JsonValueKind.Array)
        {
            foreach (var sg in singers.EnumerateArray())
            {
                var sgName = sg.GetStringProp("name");
                if (string.IsNullOrWhiteSpace(sgName)) sgName = sg.GetStringProp("title");
                if (!string.IsNullOrWhiteSpace(sgName))
                {
                    artists.Add(new ArtistItem
                    {
                        Id = sg.GetLongProp("id"),
                        Name = WebUtility.HtmlDecode(sgName)
                    });
                }
            }
        }

        string albumName = string.Empty;
        string albumMid = string.Empty;
        long albumId = 0;
        if (item.TryGetProperty("album", out var albumObj) && albumObj.ValueKind == JsonValueKind.Object)
        {
            albumName = albumObj.GetStringProp("name");
            if (string.IsNullOrWhiteSpace(albumName)) albumName = albumObj.GetStringProp("title");
            albumMid = albumObj.GetStringProp("mid");
            albumId = albumObj.GetLongProp("id");
        }
        else
        {
            albumName = item.GetStringProp("albumname");
            albumMid = item.GetStringProp("albummid");
            albumId = item.GetLongProp("albumid");
        }

        string coverUrl = !string.IsNullOrWhiteSpace(albumMid)
            ? $"https://y.gtimg.cn/music/photo_new/T002R300x300M000{albumMid}.jpg"
            : ImageUriHelper.DefaultCoverUri;

        int intervalSeconds = item.GetIntProp("interval");
        string qualityHint = string.Empty;
        if (item.TryGetProperty("file", out var fileObj) && fileObj.ValueKind == JsonValueKind.Object)
        {
            if (fileObj.GetLongProp("size_hires") > 0 || fileObj.GetLongProp("size_new") > 0)
                qualityHint = "Hi-Res";
            else if (fileObj.GetLongProp("size_flac") > 0)
                qualityHint = "FLAC";
            else if (fileObj.GetLongProp("size_320mp3") > 0)
                qualityHint = "320k";
        }

        return new SongItem
        {
            Id = !string.IsNullOrWhiteSpace(mid) ? $"qq_{mid}" : $"qq_{idNum}",
            QqSongMid = mid,
            QqSongId = idNum,
            Name = WebUtility.HtmlDecode(title),
            PicUrl = coverUrl,
            Artists = artists,
            Album = new AlbumItem
            {
                Id = albumId,
                Name = WebUtility.HtmlDecode(albumName),
                PicUrl = coverUrl
            },
            DurationMs = intervalSeconds * 1000L,
            Source = "qq",
            ResolvedPlatform = "qq",
            NativeQualityHint = qualityHint
        };
    }

    /// <summary>
    /// 解析任意歌曲对应的 QQ 音乐数字 songId（用于添加到/移出 QQ 音乐歌单）
    /// </summary>
    public async Task<long> ResolveQqSongIdAsync(SongItem song, CancellationToken ct = default)
    {
        if (song == null) return 0;
        if (song.QqSongId > 0) return song.QqSongId;

        var cookie = _configService.Settings.QqMusicCookie;
        var uin = ExtractUinFromCookieOrInput(_configService.Settings.QqMusicUin);
        long uinNum = long.TryParse(uin, out var u) ? u : 0;
        int gtk = ComputeGtk(cookie);

        // 1. 如果已有 QqSongMid，通过 CgiGetTrackInfo 查询数字 songId
        var mid = song.QqSongMid;
        if (string.IsNullOrWhiteSpace(mid) && song.Id.StartsWith("qq_", StringComparison.OrdinalIgnoreCase))
        {
            mid = song.Id[3..];
        }

        if (!string.IsNullOrWhiteSpace(mid))
        {
            if (long.TryParse(mid, out var directId) && directId > 0)
            {
                song.QqSongId = directId;
                return directId;
            }

            try
            {
                var trackPayload = new
                {
                    comm = new
                    {
                        cv = 4747474,
                        ct = 24,
                        format = "json",
                        inCharset = "utf-8",
                        outCharset = "utf-8",
                        notice = 0,
                        platform = "yqq.json",
                        needNewCode = 1,
                        uin = uinNum,
                        g_tk_new_20200303 = gtk,
                        g_tk = gtk
                    },
                    req_0 = new
                    {
                        module = "music.trackInfo.UniformRuleCtrl",
                        method = "CgiGetTrackInfo",
                        param = new
                        {
                            mids = new[] { mid },
                            types = new[] { 0 }
                        }
                    }
                };

                using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
                {
                    Content = new StringContent(JsonSerializer.Serialize(trackPayload), Encoding.UTF8, "application/json")
                };
                req.Headers.Referrer = new Uri("https://y.qq.com/");
                if (!string.IsNullOrWhiteSpace(cookie)) req.Headers.Add("Cookie", cookie);

                using var resp = await _httpClient.SendAsync(req, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("req_0", out var req0) &&
                    req0.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("tracks", out var tracks) &&
                    tracks.ValueKind == JsonValueKind.Array &&
                    tracks.GetArrayLength() > 0)
                {
                    long resolvedId = tracks[0].GetLongProp("id");
                    if (resolvedId > 0)
                    {
                        song.QqSongId = resolvedId;
                        return resolvedId;
                    }
                }
            }
            catch
            {
                // 继续走关键词搜索回退
            }
        }

        // 2. 跨平台歌曲（网易云 / 本地音乐等）：按“歌名 歌手”在 QQ 音乐曲库搜索匹配数字 songId
        var keyword = $"{song.Name} {song.ArtistText}".Replace("未知歌手", "").Trim();
        if (string.IsNullOrWhiteSpace(keyword)) return 0;

        try
        {
            var searchPayload = new
            {
                comm = new
                {
                    cv = 4747474,
                    ct = 24,
                    format = "json",
                    inCharset = "utf-8",
                    outCharset = "utf-8",
                    notice = 0,
                    platform = "yqq.json",
                    needNewCode = 1,
                    uin = uinNum,
                    g_tk_new_20200303 = gtk,
                    g_tk = gtk
                },
                req_0 = new
                {
                    module = "music.search.SearchCgiService",
                    method = "DoSearchForQQMusicDesktop",
                    param = new
                    {
                        num_per_page = 5,
                        page_num = 1,
                        query = keyword,
                        search_type = 0
                    }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
            {
                Content = new StringContent(JsonSerializer.Serialize(searchPayload), Encoding.UTF8, "application/json")
            };
            req.Headers.Referrer = new Uri("https://y.qq.com/");
            if (!string.IsNullOrWhiteSpace(cookie)) req.Headers.Add("Cookie", cookie);

            using var resp = await _httpClient.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("req_0", out var req0) &&
                req0.TryGetProperty("data", out var data) &&
                data.TryGetProperty("body", out var body) &&
                body.TryGetProperty("song", out var songObj) &&
                songObj.TryGetProperty("list", out var list) &&
                list.ValueKind == JsonValueKind.Array &&
                list.GetArrayLength() > 0)
            {
                var first = list[0];
                long matchedId = first.GetLongProp("id");
                if (matchedId == 0) matchedId = first.GetLongProp("songid");
                var matchedMid = first.GetStringProp("mid");
                if (string.IsNullOrWhiteSpace(matchedMid)) matchedMid = first.GetStringProp("songmid");

                if (matchedId > 0)
                {
                    song.QqSongId = matchedId;
                    if (string.IsNullOrWhiteSpace(song.QqSongMid) && !string.IsNullOrWhiteSpace(matchedMid))
                    {
                        song.QqSongMid = matchedMid;
                    }
                    return matchedId;
                }
            }
        }
        catch
        {
            // 忽略搜索异常
        }

        return 0;
    }

    /// <summary>
    /// 根据歌单 dissId 查找对应的 QQ 音乐 dirId（201 为“我喜欢”）
    /// </summary>
    public async Task<int> ResolvePlaylistDirIdAsync(PlaylistItem playlist, CancellationToken ct = default)
    {
        if (playlist == null) return 0;
        if (playlist.QqDirId > 0) return playlist.QqDirId;
        if (playlist.Name == "我喜欢") return 201;

        var cookie = _configService.Settings.QqMusicCookie;
        var uin = ExtractUinFromCookieOrInput(_configService.Settings.QqMusicUin);
        long uinNum = long.TryParse(uin, out var u) ? u : 0;
        int gtk = ComputeGtk(cookie);
        if (uinNum <= 0 || string.IsNullOrWhiteSpace(cookie)) return 0;

        try
        {
            var payload = new
            {
                comm = new
                {
                    cv = 4747474,
                    ct = 24,
                    format = "json",
                    inCharset = "utf-8",
                    outCharset = "utf-8",
                    notice = 0,
                    platform = "yqq.json",
                    needNewCode = 1,
                    uin = uinNum,
                    g_tk_new_20200303 = gtk,
                    g_tk = gtk
                },
                req_0 = new
                {
                    module = "music.musicasset.PlaylistBaseRead",
                    method = "GetPlaylistByUin",
                    param = new
                    {
                        uin = uin,
                        bWithoutStatus = false
                    }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Referrer = new Uri("https://y.qq.com/");
            req.Headers.Add("Cookie", cookie);

            using var resp = await _httpClient.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("req_0", out var req0) &&
                req0.TryGetProperty("data", out var data) &&
                data.TryGetProperty("v_playlist", out var vList) &&
                vList.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in vList.EnumerateArray())
                {
                    var tid = item.GetLongProp("tid").ToString();
                    var dirName = item.GetStringProp("dirName");
                    int dirId = item.GetIntProp("dirId");
                    if ((!string.IsNullOrWhiteSpace(playlist.QqDissId) && tid == playlist.QqDissId) ||
                        (!string.IsNullOrWhiteSpace(playlist.Name) && string.Equals(dirName, playlist.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        playlist.QqDirId = dirId;
                        return dirId;
                    }
                }
            }
        }
        catch
        {
        }

        return 0;
    }

    /// <summary>
    /// 添加或从 QQ 音乐歌单移除单首歌曲（music.musicasset.PlaylistDetailWrite -> AddSonglist / DelSonglist）
    /// </summary>
    public async Task<(bool Success, string Message)> AddOrRemoveSongInPlaylistAsync(
        SongItem song,
        int dirId,
        bool isDelete = false,
        CancellationToken ct = default)
    {
        var (ok, _, msg) = await UpdateSongsInPlaylistAsync([song], dirId, isDelete, ct);
        return (ok, msg);
    }

    /// <summary>
    /// 批量添加或从 QQ 音乐歌单移除歌曲（支持跨平台按歌名+歌手自动匹配 QQ 曲库 ID）
    /// </summary>
    public async Task<(bool Success, int ProcessedCount, string Message)> UpdateSongsInPlaylistAsync(
        IReadOnlyList<SongItem> songs,
        int dirId,
        bool isDelete = false,
        CancellationToken ct = default)
    {
        var cookie = _configService.Settings.QqMusicCookie;
        var uin = ExtractUinFromCookieOrInput(_configService.Settings.QqMusicUin);
        long uinNum = long.TryParse(uin, out var u) ? u : 0;
        if (uinNum <= 0 || string.IsNullOrWhiteSpace(cookie))
        {
            return (false, 0, "请先登录 QQ 音乐账号");
        }

        if (dirId <= 0)
        {
            return (false, 0, "无效的 QQ 音乐歌单目录 ID");
        }

        if (songs.Count == 0)
        {
            return (false, 0, "未选择歌曲");
        }

        var matchedIds = new List<long>();
        var seenIds = new HashSet<long>();
        foreach (var song in songs)
        {
            long songId = await ResolveQqSongIdAsync(song, ct);
            if (songId > 0 && seenIds.Add(songId))
            {
                matchedIds.Add(songId);
            }
        }

        if (matchedIds.Count == 0)
        {
            var errMsg = songs.Count == 1
                ? $"未能在 QQ 音乐曲库匹配到《{songs[0].Name}》"
                : "未能在 QQ 音乐曲库匹配到所选歌曲";
            return (false, 0, errMsg);
        }

        int gtk = ComputeGtk(cookie);
        string method = isDelete ? "DelSonglist" : "AddSonglist";

        try
        {
            var payload = new
            {
                comm = new
                {
                    cv = 4747474,
                    ct = 24,
                    format = "json",
                    inCharset = "utf-8",
                    outCharset = "utf-8",
                    notice = 0,
                    platform = "yqq.json",
                    needNewCode = 1,
                    uin = uinNum,
                    g_tk_new_20200303 = gtk,
                    g_tk = gtk
                },
                req_0 = new
                {
                    module = "music.musicasset.PlaylistDetailWrite",
                    method,
                    param = new
                    {
                        dirId,
                        v_songInfo = matchedIds.Select(id => new { songType = 0, songId = id }).ToArray()
                    }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Referrer = new Uri("https://y.qq.com/");
            req.Headers.Add("Cookie", cookie);

            using var resp = await _httpClient.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("req_0", out var req0))
            {
                int code = req0.GetIntProp("code", -1);
                int retCode = req0.TryGetProperty("data", out var data) ? data.GetIntProp("retCode", 0) : 0;
                if (code == 0 && retCode == 0)
                {
                    var okMsg = isDelete
                        ? $"已从 QQ 音乐歌单移除 {matchedIds.Count} 首歌曲"
                        : $"已添加 {matchedIds.Count} 首歌曲到 QQ 音乐歌单";
                    return (true, matchedIds.Count, okMsg);
                }
                if (code == 1000)
                {
                    return (false, 0, "QQ 音乐登录态已过期，请重新登录");
                }
                return (false, 0, $"QQ 音乐歌单操作失败 (code={code})");
            }
        }
        catch (Exception ex)
        {
            return (false, 0, $"请求异常：{ex.Message}");
        }

        return (false, 0, "QQ 音乐歌单操作失败");
    }

    /// <summary>
    /// 新建 QQ 音乐自建歌单（music.musicasset.PlaylistBaseWrite -> AddPlaylist）
    /// </summary>
    public async Task<PlaylistItem?> CreatePlaylistAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var cookie = _configService.Settings.QqMusicCookie;
        var uin = ExtractUinFromCookieOrInput(_configService.Settings.QqMusicUin);
        long uinNum = long.TryParse(uin, out var u) ? u : 0;
        if (uinNum <= 0 || string.IsNullOrWhiteSpace(cookie)) return null;

        int gtk = ComputeGtk(cookie);

        try
        {
            var payload = new
            {
                comm = new
                {
                    cv = 4747474,
                    ct = 24,
                    format = "json",
                    inCharset = "utf-8",
                    outCharset = "utf-8",
                    notice = 0,
                    platform = "yqq.json",
                    needNewCode = 1,
                    uin = uinNum,
                    g_tk_new_20200303 = gtk,
                    g_tk = gtk
                },
                req_0 = new
                {
                    module = "music.musicasset.PlaylistBaseWrite",
                    method = "AddPlaylist",
                    param = new
                    {
                        dirName = name.Trim(),
                        dirDesc = "",
                        dirPicUrl = "",
                        taglist = Array.Empty<int>()
                    }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Referrer = new Uri("https://y.qq.com/");
            req.Headers.Add("Cookie", cookie);

            using var resp = await _httpClient.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("req_0", out var req0) &&
                req0.GetIntProp("code", -1) == 0 &&
                req0.TryGetProperty("data", out var data) &&
                data.TryGetProperty("result", out var result))
            {
                long tid = result.GetLongProp("tid");
                int dirId = result.GetIntProp("dirId");
                string dirName = result.GetStringProp("dirName", name.Trim());
                string dissId = tid > 0 ? tid.ToString() : dirId.ToString();
                return new PlaylistItem
                {
                    Id = tid > 0 ? tid : dirId,
                    Source = "qq",
                    QqDissId = dissId,
                    QqDirId = dirId,
                    Name = dirName,
                    CoverImgUrl = ImageUriHelper.DefaultCoverUri,
                    TrackCount = 0
                };
            }
        }
        catch
        {
        }

        return null;
    }

    /// <summary>
    /// 删除 QQ 音乐自建歌单（music.musicasset.PlaylistBaseWrite -> DelPlaylist，拦截系统默认歌单 dirId 201/205/206）
    /// </summary>
    public async Task<(bool Success, string Message)> DeletePlaylistAsync(PlaylistItem playlist, CancellationToken ct = default)
    {
        if (playlist == null) return (false, "未选择要删除的 QQ 音乐歌单");
        var cookie = _configService.Settings.QqMusicCookie;
        var uin = ExtractUinFromCookieOrInput(_configService.Settings.QqMusicUin);
        long uinNum = long.TryParse(uin, out var u) ? u : 0;
        if (uinNum <= 0 || string.IsNullOrWhiteSpace(cookie))
        {
            return (false, "请先登录 QQ 音乐后再删除歌单");
        }

        int dirId = playlist.QqDirId > 0 ? playlist.QqDirId : await ResolvePlaylistDirIdAsync(playlist, ct);
        if (playlist.IsSystemDefaultPlaylist || dirId is 201 or 205 or 206)
        {
            return (false, "QQ 音乐系统默认歌单（如“我喜欢”）不支持删除");
        }
        if (dirId <= 0)
        {
            return (false, "无法获取 QQ 音乐歌单目录编号 (dirId)");
        }

        int gtk = ComputeGtk(cookie);

        try
        {
            var payload = new
            {
                comm = new
                {
                    cv = 4747474,
                    ct = 24,
                    format = "json",
                    inCharset = "utf-8",
                    outCharset = "utf-8",
                    notice = 0,
                    platform = "yqq.json",
                    needNewCode = 1,
                    uin = uinNum,
                    g_tk_new_20200303 = gtk,
                    g_tk = gtk
                },
                req_0 = new
                {
                    module = "music.musicasset.PlaylistBaseWrite",
                    method = "DelPlaylist",
                    param = new
                    {
                        dirId
                    }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Referrer = new Uri("https://y.qq.com/");
            req.Headers.Add("Cookie", cookie);

            using var resp = await _httpClient.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("req_0", out var req0))
            {
                int code = req0.GetIntProp("code", -1);
                if (code == 0)
                {
                    return (true, $"已删除 QQ 音乐歌单《{playlist.Name}》");
                }
                return (false, $"删除 QQ 音乐歌单失败 (code={code})");
            }
        }
        catch (Exception ex)
        {
            return (false, $"删除 QQ 音乐歌单异常：{ex.Message}");
        }

        return (false, "删除 QQ 音乐歌单失败");
    }

    /// <summary>
    /// 解析 QQ 音乐单曲播放/下载流地址（优先请求 RS01 母带 / F000 无损 FLAC，支持按目标音质档位请求，非绿钻无损时支持先播快开并回退落月 API）
    /// </summary>
    public async Task<string?> GetSongPlayUrlAsync(
        string songMid, CancellationToken ct = default, bool preferLossless = true, string? targetQualityLevel = null, bool allowFastStart = false)
    {
        if (string.IsNullOrWhiteSpace(songMid)) return null;
        var cookie = _configService.Settings.QqMusicCookie;
        var uin = ExtractUinFromCookieOrInput(_configService.Settings.QqMusicUin);
        if (string.IsNullOrWhiteSpace(uin)) uin = "0";

        var target = (targetQualityLevel ?? string.Empty).Trim().ToLowerInvariant();
        string? officialFallbackUrl = null;

        try
        {
            string[] filenames = target switch
            {
                MusicQualityLevel.Standard or MusicQualityLevel.Higher => [$"C400{songMid}.m4a", $"M800{songMid}.mp3"],
                MusicQualityLevel.Exhigh => [$"M800{songMid}.mp3", $"C400{songMid}.m4a"],
                MusicQualityLevel.Lossless => [$"F000{songMid}.flac", $"M800{songMid}.mp3", $"C400{songMid}.m4a"],
                _ => preferLossless
                    ? [$"RS01{songMid}.flac", $"Q000{songMid}.flac", $"F000{songMid}.flac", $"M800{songMid}.mp3", $"C400{songMid}.m4a"]
                    : [$"M800{songMid}.mp3", $"C400{songMid}.m4a"]
            };
            string[] mids = Enumerable.Repeat(songMid, filenames.Length).ToArray();
            int[] types = new int[filenames.Length];

            var payload = new
            {
                req_0 = new
                {
                    module = "vkey.GetVkeyServer",
                    method = "CgiGetVkey",
                    param = new
                    {
                        guid = "10000",
                        songmid = mids,
                        filename = filenames,
                        songtype = types,
                        uin,
                        loginflag = 1,
                        platform = "20"
                    }
                },
                comm = new
                {
                    uin,
                    format = "json",
                    ct = 24,
                    cv = 0
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Referrer = new Uri("https://y.qq.com/");
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                req.Headers.Add("Cookie", cookie);
            }

            using var resp = await _httpClient.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);

            if (doc.RootElement.TryGetProperty("req_0", out var req0) &&
                req0.TryGetProperty("data", out var data) &&
                data.TryGetProperty("midurlinfo", out var midArr) &&
                midArr.ValueKind == JsonValueKind.Array &&
                midArr.GetArrayLength() > 0)
            {
                string sip = "https://dl.stream.qqmusic.qq.com/";
                if (data.TryGetProperty("sip", out var sipArr) && sipArr.ValueKind == JsonValueKind.Array && sipArr.GetArrayLength() > 0)
                {
                    var firstSip = sipArr[0].GetString();
                    if (!string.IsNullOrWhiteSpace(firstSip))
                    {
                        sip = firstSip.Replace("http://", "https://");
                    }
                }

                foreach (var info in midArr.EnumerateArray())
                {
                    var purl = info.GetStringProp("purl");
                    if (!string.IsNullOrWhiteSpace(purl))
                    {
                        var fullUrl = sip.TrimEnd('/') + "/" + purl.TrimStart('/');
                        if (fullUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase))
                        {
                            return fullUrl;
                        }
                        officialFallbackUrl ??= fullUrl;
                    }
                }
            }
        }
        catch
        {
            // 继续尝试 GD 音乐台 tencent 源解析
        }

        // 若官方已返回完整非 FLAC（如 M800 320k / C400 AAC），且当前不强求阻塞等待无损或允许先播后升级，立即返回秒开
        if (!string.IsNullOrWhiteSpace(officialFallbackUrl) && (!preferLossless || allowFastStart))
        {
            return officialFallbackUrl;
        }

        // 若优先无损且官方未返回 FLAC（如账号无绿钻），尝试落月 API (api.vkeys.cn) tencent 源解析
        int[] qualities = target switch
        {
            MusicQualityLevel.Standard => [6, 8],
            MusicQualityLevel.Higher => [6, 8],
            MusicQualityLevel.Exhigh => [8, 6],
            MusicQualityLevel.Lossless => !string.IsNullOrWhiteSpace(officialFallbackUrl) ? [10] : [10, 8],
            _ => preferLossless ? (!string.IsNullOrWhiteSpace(officialFallbackUrl) ? [10] : [10, 8]) : [8]
        };
        foreach (var q in qualities)
        {
            try
            {
                var vkeysUrl = $"https://api.vkeys.cn/v2/music/tencent?mid={Uri.EscapeDataString(songMid)}&quality={q}";
                var vkeysJson = await _httpClient.GetStringAsync(vkeysUrl, ct);
                using var vkeysDoc = JsonDocument.Parse(vkeysJson);
                if (vkeysDoc.RootElement.TryGetProperty("data", out var vData) && vData.ValueKind == JsonValueKind.Object)
                {
                    var resolved = vData.GetStringProp("url").Replace("\\/", "/");
                    var qualName = vData.GetStringProp("quality");
                    if (!string.IsNullOrWhiteSpace(resolved) &&
                        resolved.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                        !qualName.Contains("试听", StringComparison.OrdinalIgnoreCase) &&
                        !resolved.Contains("RS02", StringComparison.OrdinalIgnoreCase))
                    {
                        if (resolved.Contains(".flac", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(officialFallbackUrl))
                        {
                            return resolved;
                        }
                    }
                }
            }
            catch
            {
                // 忽略当前档位异常
            }
        }

        return officialFallbackUrl;
    }

    /// <summary>
    /// 获取 QQ 音乐单曲歌词与翻译歌词（Base64 自动解码，对应 fcg_query_lyric_new.fcg）
    /// </summary>
    public async Task<(string Lrc, string TranslatedLrc)> GetLyricAsync(string songMid)
    {
        if (string.IsNullOrWhiteSpace(songMid)) return (string.Empty, string.Empty);

        try
        {
            var url = $"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={Uri.EscapeDataString(songMid)}&format=json&nobase64=0&g_tk=5381";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://y.qq.com/");
            if (!string.IsNullOrWhiteSpace(_configService.Settings.QqMusicCookie))
            {
                req.Headers.Add("Cookie", _configService.Settings.QqMusicCookie);
            }

            using var resp = await _httpClient.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);

            string lrc = DecodeBase64Safe(doc.RootElement.GetStringProp("lyric"));
            string trans = DecodeBase64Safe(doc.RootElement.GetStringProp("trans"));
            return (WebUtility.HtmlDecode(lrc), WebUtility.HtmlDecode(trans));
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    private static string DecodeBase64Safe(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return string.Empty;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return base64;
        }
    }

    private static string NormalizeCoverUrl(string? url, string fallback)
    {
        if (string.IsNullOrWhiteSpace(url)) return fallback;
        if (url.StartsWith("//")) return "https:" + url;
        return url.Replace("http://", "https://");
    }
}
