using System.Net.Http.Headers;
using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.System;

namespace AlgerMusicPlayer.WinUI.Services.Auth;

/// <summary>
/// 全平台账号统一鉴权协调服务（托管 10 平台凭证持久化、手动解析、PKCE 续期与请求头自动注入）
/// </summary>
public sealed class PlatformAuthService
{
    private readonly ConfigService _configService;
    private readonly SecureCredentialStore _store;
    private readonly OAuthPkceService _oauthPkce;
    private readonly Dictionary<AuthMusicPlatform, PlatformAuthConfig> _configs;

    public event Action<AuthMusicPlatform>? SessionChanged;

    public PlatformAuthService(
        ConfigService configService,
        SecureCredentialStore store,
        OAuthPkceService oauthPkce)
    {
        _configService = configService;
        _store = store;
        _oauthPkce = oauthPkce;
        _configs = PlatformRegistry.CreateDefaultConfigs();
        SyncConfigsFromSettings();
    }

    public IReadOnlyDictionary<AuthMusicPlatform, PlatformAuthConfig> Configs => _configs;

    public async Task InitializeAsync()
    {
        await _store.InitializeAsync();
        SyncConfigsFromSettings();
    }

    public void SyncConfigsFromSettings()
    {
        var s = _configService.Settings;
        _configs[AuthMusicPlatform.Spotify].ClientId = s.SpotifyClientId ?? string.Empty;
        _configs[AuthMusicPlatform.Tidal].ClientId = s.TidalClientId ?? string.Empty;
        _configs[AuthMusicPlatform.AppleMusic].AppleTeamId = s.AppleMusicTeamId ?? string.Empty;
        _configs[AuthMusicPlatform.AppleMusic].AppleKeyId = s.AppleMusicKeyId ?? string.Empty;
        _configs[AuthMusicPlatform.AppleMusic].ApplePrivateKeyPem = s.AppleMusicPrivateKeyPem ?? string.Empty;
    }

    public PlatformAuthConfig GetConfig(AuthMusicPlatform platform)
    {
        SyncConfigsFromSettings();
        return _configs[platform];
    }

    public AccountSession? TryGetSession(AuthMusicPlatform platform)
    {
        return _store.TryGetCachedSession(platform);
    }

    public async Task<Dictionary<AuthMusicPlatform, AccountSession>> GetAllSessionsAsync()
    {
        return await _store.GetAllSessionsAsync();
    }

    public bool HasValidSession(AuthMusicPlatform platform)
    {
        var session = _store.TryGetCachedSession(platform);
        return session != null &&
               (!string.IsNullOrWhiteSpace(session.CookieHeader) ||
                !string.IsNullOrWhiteSpace(session.AccessToken) ||
                session.Cookies.Count > 0);
    }

    public static bool TryMapSourceToPlatform(string? sourceKey, out AuthMusicPlatform platform)
    {
        switch (PlatformVisualHelper.NormalizeKey(sourceKey))
        {
            case "qishui":
                platform = AuthMusicPlatform.Qishui;
                return true;
            case "kugou":
                platform = AuthMusicPlatform.KuGou;
                return true;
            case "kuwo":
                platform = AuthMusicPlatform.KuWo;
                return true;
            case "migu":
                platform = AuthMusicPlatform.Migu;
                return true;
            case "bilibili":
                platform = AuthMusicPlatform.Bilibili;
                return true;
            case "spotify":
                platform = AuthMusicPlatform.Spotify;
                return true;
            case "apple":
                platform = AuthMusicPlatform.AppleMusic;
                return true;
            case "deezer":
                platform = AuthMusicPlatform.Deezer;
                return true;
            case "tidal":
                platform = AuthMusicPlatform.Tidal;
                return true;
            case "ytmusic":
                platform = AuthMusicPlatform.YouTubeMusic;
                return true;
            default:
                platform = default;
                return false;
        }
    }

    public async Task SaveSessionAsync(AccountSession session)
    {
        await _store.SaveSessionAsync(session);
        SessionChanged?.Invoke(session.Platform);
    }

    public async Task RemoveSessionAsync(AuthMusicPlatform platform)
    {
        await _store.RemoveSessionAsync(platform);
        SessionChanged?.Invoke(platform);
    }

    public async Task ClearAllSessionsAsync()
    {
        await _store.ClearAllAsync();
        foreach (var p in Enum.GetValues<AuthMusicPlatform>())
        {
            SessionChanged?.Invoke(p);
        }
    }

    /// <summary>
    /// 兼容用户手动粘贴完整 Cookie 字符串、单核心字段裸值或 Bearer Token
    /// </summary>
    public async Task<AccountSession> SaveManualCredentialAsync(AuthMusicPlatform platform, string rawInput)
    {
        var trimmed = (rawInput ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("请输入有效的 Cookie 或 Token 凭证内容。");
        }

        var config = GetConfig(platform);
        var session = new AccountSession
        {
            Platform = platform,
            FlowType = AuthFlowType.WebViewCookie,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            session.AccessToken = trimmed["Bearer ".Length..].Trim();
            await SaveSessionAsync(session);
            return session;
        }

        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool looksLikeCookieHeader = trimmed.Contains(';') || LooksLikeSingleKeyValue(trimmed);
        if (looksLikeCookieHeader)
        {
            foreach (var segment in trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eqIdx = segment.IndexOf('=');
                if (eqIdx <= 0) continue;
                var key = segment[..eqIdx].Trim();
                var val = segment[(eqIdx + 1)..].Trim();
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(val))
                {
                    cookies[key] = val;
                }
            }
        }
        else
        {
            if (platform == AuthMusicPlatform.Tidal)
            {
                session.AccessToken = trimmed;
            }
            else
            {
                var primaryKey = config.RequiredCookieKeys.FirstOrDefault() ?? "token";
                cookies[primaryKey] = trimmed;
                if (platform == AuthMusicPlatform.AppleMusic)
                {
                    session.AccessToken = trimmed;
                }
            }
        }

        if (platform == AuthMusicPlatform.YouTubeMusic &&
            !cookies.ContainsKey("SAPISID"))
        {
            if (cookies.TryGetValue("__Secure-3PAPISID", out var sec3) && !string.IsNullOrWhiteSpace(sec3))
            {
                cookies["SAPISID"] = sec3;
            }
            else if (cookies.TryGetValue("__Secure-1PAPISID", out var sec1) && !string.IsNullOrWhiteSpace(sec1))
            {
                cookies["SAPISID"] = sec1;
            }
        }
        else if (platform == AuthMusicPlatform.Qishui &&
                 !cookies.ContainsKey("sessionid") &&
                 cookies.TryGetValue("sessionid_ss", out var sidSs))
        {
            cookies["sessionid"] = sidSs;
        }
        else if (platform == AuthMusicPlatform.Migu)
        {
            if (cookies.TryGetValue("mg_auth_pacmtoken", out var p1) && !string.IsNullOrWhiteSpace(p1))
            {
                session.AccessToken = p1;
            }
            else if (cookies.TryGetValue("pacmtoken", out var p2) && !string.IsNullOrWhiteSpace(p2))
            {
                cookies["mg_auth_pacmtoken"] = p2;
                session.AccessToken = p2;
            }
            if (cookies.TryGetValue("mg_auth_uid", out var mu) && !string.IsNullOrWhiteSpace(mu))
            {
                session.UserId = mu;
            }
            else if (cookies.TryGetValue("migu_music_uid", out var muOld) && !string.IsNullOrWhiteSpace(muOld))
            {
                cookies["mg_auth_uid"] = muOld;
                session.UserId = muOld;
            }
        }
        else if (platform == AuthMusicPlatform.KuWo &&
                 !cookies.ContainsKey("userid"))
        {
            if (cookies.TryGetValue("uid", out var kwUid) && !string.IsNullOrWhiteSpace(kwUid))
            {
                cookies["userid"] = kwUid;
            }
            else if (cookies.TryGetValue("loginUid", out var kwLoginUid) && !string.IsNullOrWhiteSpace(kwLoginUid))
            {
                cookies["userid"] = kwLoginUid;
            }
        }
        else if (platform == AuthMusicPlatform.KuGou &&
                 cookies.TryGetValue("KuGoo", out var kgComposite) &&
                 !string.IsNullOrWhiteSpace(kgComposite))
        {
            foreach (var part in kgComposite.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var pk = part[..eq].Trim();
                var pv = part[(eq + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(pk) && !string.IsNullOrWhiteSpace(pv) && !cookies.ContainsKey(pk))
                {
                    cookies[pk] = pv;
                }
            }
        }
        else if (platform == AuthMusicPlatform.AppleMusic &&
                 string.IsNullOrWhiteSpace(session.AccessToken) &&
                 cookies.TryGetValue("media-user-token", out var mediaToken))
        {
            session.AccessToken = mediaToken;
        }
        else if (platform == AuthMusicPlatform.Tidal &&
                 string.IsNullOrWhiteSpace(session.AccessToken) &&
                 cookies.TryGetValue("token", out var webToken))
        {
            session.AccessToken = webToken;
        }

        if (cookies.Count > 0)
        {
            session.Cookies = cookies;
            session.CookieHeader = string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        if (string.IsNullOrWhiteSpace(session.CookieHeader) && string.IsNullOrWhiteSpace(session.AccessToken))
        {
            throw new InvalidOperationException("未能从输入中解析出有效键值对或 Token，请检查粘贴内容。");
        }

        await SaveSessionAsync(session);
        return session;
    }

    public async Task<AccountSession> AuthorizePkceAsync(AuthMusicPlatform platform, bool forceInteractive = false, CancellationToken ct = default)
    {
        var config = GetConfig(platform);
        if (!config.SupportsOAuthPkce)
        {
            throw new NotSupportedException($"{config.DisplayName} 不支持 OAuth 2.0 PKCE 流程。");
        }

        AccountSession? existing = await _store.GetSessionAsync(platform);
        AccountSession session;

        if (!forceInteractive && existing != null && !string.IsNullOrWhiteSpace(existing.RefreshToken))
        {
            try
            {
                session = await _oauthPkce.RefreshTokenAsync(existing, config, ct);
            }
            catch
            {
                session = await _oauthPkce.AuthorizeAsync(config, ct);
            }
        }
        else
        {
            session = await _oauthPkce.AuthorizeAsync(config, ct);
        }

        await SaveSessionAsync(session);
        return session;
    }

    /// <summary>
    /// 自动向业务 HttpRequestMessage 注入对应平台的加密鉴权头、SAPISIDHASH 签名或按需刷新 Token
    /// </summary>
    public async Task<bool> ApplyAuthHeadersAsync(AuthMusicPlatform platform, HttpRequestMessage request, CancellationToken ct = default)
    {
        AccountSession? session = await _store.GetSessionAsync(platform);
        if (session == null)
        {
            return false;
        }

        PlatformAuthConfig config = GetConfig(platform);
        bool applied = false;

        switch (session.FlowType)
        {
            case AuthFlowType.OAuth2Pkce:
                if (session.IsTokenExpired && !string.IsNullOrWhiteSpace(session.RefreshToken))
                {
                    try
                    {
                        session = await _oauthPkce.RefreshTokenAsync(session, config, ct);
                        await SaveSessionAsync(session);
                    }
                    catch
                    {
                    }
                }
                if (!string.IsNullOrWhiteSpace(session.AccessToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
                    applied = true;
                }
                break;

            case AuthFlowType.AppleMusicKitJs:
                if (session.IsTokenExpired &&
                    !string.IsNullOrWhiteSpace(config.AppleTeamId) &&
                    !string.IsNullOrWhiteSpace(config.AppleKeyId) &&
                    !string.IsNullOrWhiteSpace(config.ApplePrivateKeyPem))
                {
                    try
                    {
                        session.DeveloperToken = PlatformCryptoHelpers.CreateAppleMusicDeveloperToken(
                            config.AppleTeamId,
                            config.AppleKeyId,
                            config.ApplePrivateKeyPem,
                            TimeSpan.FromDays(90));
                        session.ExpiresAt = DateTimeOffset.UtcNow.AddDays(90);
                        await SaveSessionAsync(session);
                    }
                    catch
                    {
                    }
                }
                if (!string.IsNullOrWhiteSpace(session.DeveloperToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.DeveloperToken);
                    applied = true;
                }
                if (!string.IsNullOrWhiteSpace(session.AccessToken))
                {
                    request.Headers.TryAddWithoutValidation("Music-User-Token", session.AccessToken);
                    applied = true;
                }
                if (!string.IsNullOrWhiteSpace(session.CookieHeader))
                {
                    request.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
                    applied = true;
                }
                break;

            case AuthFlowType.WebViewCookie:
                string effectiveCookieHeader = session.CookieHeader;
                if (platform == AuthMusicPlatform.Qishui &&
                    !string.IsNullOrWhiteSpace(effectiveCookieHeader) &&
                    !effectiveCookieHeader.Contains("sessionid=", StringComparison.OrdinalIgnoreCase) &&
                    session.Cookies.TryGetValue("sessionid_ss", out var qSidSs) &&
                    !string.IsNullOrWhiteSpace(qSidSs))
                {
                    effectiveCookieHeader = $"sessionid={qSidSs}; {effectiveCookieHeader}";
                }

                if (!string.IsNullOrWhiteSpace(effectiveCookieHeader))
                {
                    request.Headers.TryAddWithoutValidation("Cookie", effectiveCookieHeader);
                    applied = true;
                }
                if (!string.IsNullOrWhiteSpace(session.DeveloperToken))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {session.DeveloperToken}");
                    applied = true;
                }
                if (platform == AuthMusicPlatform.AppleMusic && !string.IsNullOrWhiteSpace(session.AccessToken))
                {
                    request.Headers.TryAddWithoutValidation("Music-User-Token", session.AccessToken);
                    request.Headers.TryAddWithoutValidation("Media-User-Token", session.AccessToken);
                    applied = true;
                }
                else if (platform == AuthMusicPlatform.Migu)
                {
                    // 咪咕 V5 H5 网关要求 channel/subchannel/platform/version 以及登录态 uid/pacmtoken 请求头
                    request.Headers.TryAddWithoutValidation("channel", "014X031");
                    request.Headers.TryAddWithoutValidation("subchannel", "014X031");
                    request.Headers.TryAddWithoutValidation("platform", "H5");
                    request.Headers.TryAddWithoutValidation("version", "6.8.8");
                    request.Headers.TryAddWithoutValidation("IMEI", "h5page");
                    request.Headers.TryAddWithoutValidation("IMSI", "h5page");

                    string miguUid = string.Empty;
                    foreach (var uk in new[] { "mg_auth_uid", "migu_music_uid", "uid" })
                    {
                        if (session.Cookies.TryGetValue(uk, out var uv) && !string.IsNullOrWhiteSpace(uv))
                        {
                            miguUid = uv;
                            break;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(miguUid)) miguUid = session.UserId;

                    string pacmToken = string.Empty;
                    foreach (var pk in new[] { "mg_auth_pacmtoken", "pacmtoken" })
                    {
                        if (session.Cookies.TryGetValue(pk, out var pv) && !string.IsNullOrWhiteSpace(pv))
                        {
                            pacmToken = pv;
                            break;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(pacmToken)) pacmToken = session.AccessToken;

                    if (!string.IsNullOrWhiteSpace(miguUid))
                    {
                        request.Headers.TryAddWithoutValidation("uid", miguUid);
                        applied = true;
                    }
                    if (!string.IsNullOrWhiteSpace(pacmToken))
                    {
                        request.Headers.TryAddWithoutValidation("pacmtoken", pacmToken);
                        applied = true;
                    }
                }
                else if (platform != AuthMusicPlatform.KuGou &&
                         platform != AuthMusicPlatform.YouTubeMusic &&
                         !string.IsNullOrWhiteSpace(session.AccessToken) &&
                         request.Headers.Authorization == null)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
                    applied = true;
                }

                if (platform == AuthMusicPlatform.YouTubeMusic)
                {
                    string sapisid = string.Empty;
                    foreach (var sk in new[] { "SAPISID", "__Secure-3PAPISID", "__Secure-1PAPISID" })
                    {
                        if (session.Cookies.TryGetValue(sk, out var sv) && !string.IsNullOrWhiteSpace(sv))
                        {
                            sapisid = sv;
                            break;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(sapisid))
                    {
                        string primary = PlatformCryptoHelpers.CreateYouTubeMusicSapisidHash(sapisid);
                        string hashValue = primary.StartsWith("SAPISIDHASH ", StringComparison.Ordinal)
                            ? primary["SAPISIDHASH ".Length..]
                            : primary;
                        string combined = $"SAPISIDHASH {hashValue} SAPISID1PHASH {hashValue} SAPISID3PHASH {hashValue}";
                        request.Headers.TryAddWithoutValidation("Authorization", combined);
                        request.Headers.TryAddWithoutValidation("X-Origin", "https://music.youtube.com");
                        request.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");
                        applied = true;
                    }
                }
                break;
        }

        return applied;
    }

    private static bool LooksLikeSingleKeyValue(string text)
    {
        int eqIdx = text.IndexOf('=');
        if (eqIdx <= 0 || eqIdx == text.Length - 1) return false;
        string candidateKey = text[..eqIdx].Trim();
        return candidateKey.Length <= 40 && candidateKey.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.');
    }
}
