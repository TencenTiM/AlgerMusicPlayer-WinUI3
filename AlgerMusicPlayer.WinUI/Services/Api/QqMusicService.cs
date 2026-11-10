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

        try
        {
            int gtk = ComputeGtk(cookie);
            var url = $"https://c.y.qq.com/rsc/fcgi-bin/fcg_get_profile_homepage.fcg?format=json&inCharset=utf8&outCharset=utf-8&cid=205360838&userid={uin}&reqfrom=1&g_tk={gtk}&loginUin={uin}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://y.qq.com/");
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                req.Headers.Add("Cookie", cookie);
            }

            using var resp = await _httpClient.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("creator", out var creator) && creator.ValueKind == JsonValueKind.Object)
                {
                    var nick = creator.GetStringProp("nick");
                    var headpic = creator.GetStringProp("headpic");
                    if (!string.IsNullOrWhiteSpace(nick)) profile.Nickname = nick;
                    if (!string.IsNullOrWhiteSpace(headpic)) profile.AvatarUrl = headpic.Replace("http://", "https://");
                }

                // 1. 我喜欢歌单 (mymusic)
                if (data.TryGetProperty("mymusic", out var mymusic) && mymusic.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in mymusic.EnumerateArray())
                    {
                        var dissId = item.GetStringProp("id");
                        var title = item.GetStringProp("title", "我喜欢");
                        var pic = item.GetStringProp("laypic");
                        if (string.IsNullOrWhiteSpace(pic)) pic = item.GetStringProp("picurl");
                        int num = item.GetIntProp("num0");
                        if (!string.IsNullOrWhiteSpace(dissId) && dissId != "0")
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
                                CreatorName = profile.Nickname
                            });
                        }
                    }
                }

                // 2. 自建歌单 (mydiss.list)
                if (data.TryGetProperty("mydiss", out var mydiss) &&
                    mydiss.ValueKind == JsonValueKind.Object &&
                    mydiss.TryGetProperty("list", out var dissList) &&
                    dissList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dissList.EnumerateArray())
                    {
                        var dissId = item.GetStringProp("dissid");
                        int dirId = item.GetIntProp("dirid");
                        var title = item.GetStringProp("title");
                        var pic = item.GetStringProp("picurl");
                        var subtitle = item.GetStringProp("subtitle");
                        int trackCount = 0;
                        var m = Regex.Match(subtitle, @"(\d+)");
                        if (m.Success) int.TryParse(m.Groups[1].Value, out trackCount);

                        if (!string.IsNullOrWhiteSpace(dissId))
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
                                CreatorName = profile.Nickname
                            });
                        }
                    }
                }
            }
        }
        catch
        {
            // 若主页接口受限，继续尝试收藏与备用歌单接口
        }

        // 3. 拉取 QQ 音乐收藏的歌单 (fcg_fav_list_get.fcg)
        try
        {
            int gtk = ComputeGtk(cookie);
            var favUrl = $"https://c.y.qq.com/fav/fcgi-bin/fcg_get_profile_order_asset.fcg?format=json&inCharset=utf8&outCharset=utf-8&ct=20&cid=205360956&userid={uin}&reqtype=3&sin=0&ein=50&g_tk={gtk}&loginUin={uin}";
            using var favReq = new HttpRequestMessage(HttpMethod.Get, favUrl);
            favReq.Headers.Referrer = new Uri("https://y.qq.com/");
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                favReq.Headers.Add("Cookie", cookie);
            }

            using var favResp = await _httpClient.SendAsync(favReq);
            var favJson = await favResp.Content.ReadAsStringAsync();
            using var favDoc = JsonDocument.Parse(favJson);
            if (favDoc.RootElement.TryGetProperty("data", out var favData) &&
                favData.ValueKind == JsonValueKind.Object &&
                favData.TryGetProperty("cdlist", out var cdList) &&
                cdList.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cdList.EnumerateArray())
                {
                    var dissId = item.GetStringProp("dissid");
                    if (string.IsNullOrWhiteSpace(dissId)) dissId = item.GetLongProp("dissid").ToString();
                    var name = item.GetStringProp("dissname");
                    var logo = item.GetStringProp("logo");
                    int songNum = item.GetIntProp("songnum");
                    var nick = item.GetStringProp("nickname");

                    if (!string.IsNullOrWhiteSpace(dissId) && dissId != "0" && !string.IsNullOrWhiteSpace(name))
                    {
                        collected.Add(new PlaylistItem
                        {
                            Id = long.TryParse(dissId, out var lid) ? lid : Math.Abs(dissId.GetHashCode()),
                            Source = "qq",
                            QqDissId = dissId,
                            Name = WebUtility.HtmlDecode(name),
                            CoverImgUrl = NormalizeCoverUrl(logo, profile.DisplayAvatarUrl),
                            TrackCount = songNum,
                            CreatorName = nick
                        });
                    }
                }
            }
        }
        catch
        {
            // 忽略收藏列表异常
        }

        return (profile, created, collected);
    }

    /// <summary>
    /// 获取 QQ 音乐歌单详情与全部歌曲（对应 gateway/qqServices.ts getQqPlaylistDetail &amp; shared/qqMusicAdapter.ts）
    /// </summary>
    public async Task<PlaylistItem?> GetPlaylistDetailAsync(string dissId, string? cookieInput = null)
    {
        if (string.IsNullOrWhiteSpace(dissId)) return null;
        var cookie = cookieInput ?? _configService.Settings.QqMusicCookie;
        var uin = ExtractUinFromCookieOrInput(_configService.Settings.QqMusicUin);
        if (string.IsNullOrWhiteSpace(uin)) uin = "0";
        int gtk = ComputeGtk(cookie);

        try
        {
            var url = $"https://c.y.qq.com/qzone/fcg-bin/fcg_ucc_getcdinfo_byids_cp.fcg?type=1&json=1&utf8=1&onlysong=0&new_format=1&disstid={Uri.EscapeDataString(dissId)}&g_tk={gtk}&loginUin={uin}&hostUin=0&format=json&inCharset=utf8&outCharset=utf-8&notice=0&platform=yqq.json&needNewCode=0";
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
                CoverImgUrl = NormalizeCoverUrl(cd.GetStringProp("logo"), "ms-appx:///Assets/DefaultCover.png"),
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
            : "ms-appx:///Assets/DefaultCover.png";

        int intervalSeconds = item.GetIntProp("interval");

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
            ResolvedPlatform = "qq"
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
    /// 添加或从 QQ 音乐歌单移除歌曲（music.musicasset.PlaylistDetailWrite -> AddSonglist / DelSonglist）
    /// </summary>
    public async Task<(bool Success, string Message)> AddOrRemoveSongInPlaylistAsync(
        SongItem song,
        int dirId,
        bool isDelete = false,
        CancellationToken ct = default)
    {
        var cookie = _configService.Settings.QqMusicCookie;
        var uin = ExtractUinFromCookieOrInput(_configService.Settings.QqMusicUin);
        long uinNum = long.TryParse(uin, out var u) ? u : 0;
        if (uinNum <= 0 || string.IsNullOrWhiteSpace(cookie))
        {
            return (false, "请先登录 QQ 音乐账号");
        }

        if (dirId <= 0)
        {
            return (false, "无效的 QQ 音乐歌单目录 ID");
        }

        long songId = await ResolveQqSongIdAsync(song, ct);
        if (songId <= 0)
        {
            return (false, $"未能在 QQ 音乐曲库匹配到《{song.Name}》");
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
                        v_songInfo = new[]
                        {
                            new { songType = 0, songId }
                        }
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
                    return (true, isDelete ? "已从 QQ 音乐歌单移除" : "已添加到 QQ 音乐歌单");
                }
                if (code == 1000)
                {
                    return (false, "QQ 音乐登录态已过期，请重新登录");
                }
                return (false, $"QQ 音乐歌单操作失败 (code={code})");
            }
        }
        catch (Exception ex)
        {
            return (false, $"请求异常：{ex.Message}");
        }

        return (false, "QQ 音乐歌单操作失败");
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
                    CoverImgUrl = "ms-appx:///Assets/DefaultCover.png",
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
    /// 解析 QQ 音乐单曲播放/下载流地址（优先请求 RS01 母带 / F000 无损 FLAC，非绿钻无损时自动结合 GD 音乐台 br=999 无损源）
    /// </summary>
    public async Task<string?> GetSongPlayUrlAsync(string songMid, CancellationToken ct = default, bool preferLossless = true)
    {
        if (string.IsNullOrWhiteSpace(songMid)) return null;
        var cookie = _configService.Settings.QqMusicCookie;
        var uin = ExtractUinFromCookieOrInput(_configService.Settings.QqMusicUin);
        if (string.IsNullOrWhiteSpace(uin)) uin = "0";

        string? officialFallbackUrl = null;

        try
        {
            string[] filenames = preferLossless
                ? [$"RS01{songMid}.flac", $"Q000{songMid}.flac", $"F000{songMid}.flac", $"M800{songMid}.mp3", $"C400{songMid}.m4a"]
                : [$"M800{songMid}.mp3", $"C400{songMid}.m4a"];
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

        // 若优先无损且官方未返回 FLAC（如账号无绿钻），尝试落月 API (api.vkeys.cn) tencent 源解析
        int[] qualities = preferLossless ? [10, 8] : [8];
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
