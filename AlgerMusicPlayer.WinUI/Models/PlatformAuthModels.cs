using CommunityToolkit.Mvvm.ComponentModel;

namespace AlgerMusicPlayer.WinUI.Models;

/// <summary>
/// 多平台账号鉴权支持的 10 个扩展音乐平台枚举（网易云与 QQ 音乐沿用独立登录通道）
/// </summary>
public enum AuthMusicPlatform
{
    Qishui,
    KuGou,
    KuWo,
    Migu,
    Bilibili,
    Spotify,
    AppleMusic,
    Deezer,
    Tidal,
    YouTubeMusic
}

/// <summary>
/// 三通道纯本地鉴权流程类型
/// </summary>
public enum AuthFlowType
{
    WebViewCookie,
    OAuth2Pkce,
    AppleMusicKitJs
}

/// <summary>
/// 单个平台的加密账号会话凭证模型
/// </summary>
public sealed class AccountSession
{
    public AuthMusicPlatform Platform { get; set; }
    public AuthFlowType FlowType { get; set; } = AuthFlowType.WebViewCookie;
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string DeveloperToken { get; set; } = string.Empty;
    public string CookieHeader { get; set; } = string.Empty;
    public Dictionary<string, string> Cookies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Nickname { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }

    public bool IsTokenExpired =>
        ExpiresAt.HasValue && DateTimeOffset.UtcNow >= ExpiresAt.Value.AddMinutes(-2);
}

/// <summary>
/// 平台鉴权静态配置与双模式参数描述
/// </summary>
public sealed class PlatformAuthConfig
{
    public AuthMusicPlatform Platform { get; init; }
    public string SourceKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public AuthFlowType DefaultFlowType { get; init; }
    public string AuthBadgeText { get; init; } = string.Empty;

    // OAuth 2.0 PKCE 配置 (Spotify / Tidal)
    public bool SupportsOAuthPkce { get; init; }
    public string ClientId { get; set; } = string.Empty;
    public string AuthorizeEndpoint { get; init; } = string.Empty;
    public string TokenEndpoint { get; init; } = string.Empty;
    public string Scopes { get; init; } = string.Empty;
    public int LoopbackPort { get; init; } = 48920;
    public string RedirectUri => $"http://127.0.0.1:{LoopbackPort}/callback";

    // Apple Music 配置
    public bool SupportsAppleMusicKit { get; init; }
    public string AppleTeamId { get; set; } = string.Empty;
    public string AppleKeyId { get; set; } = string.Empty;
    public string ApplePrivateKeyPem { get; set; } = string.Empty;

    // WebView2 Cookie 提取配置（国内平台 / Deezer / YT Music / 免密钥海外网页版）
    public string LoginUrl { get; init; } = string.Empty;
    public string CookieTargetUrl { get; init; } = string.Empty;
    public string[] ExtraCookieUrls { get; init; } = [];
    public string[] RequiredCookieKeys { get; init; } = [];
    public string ManualPlaceholder { get; init; } = string.Empty;
}

/// <summary>
/// 10 平台官方与免密钥双模式默认注册表
/// </summary>
public static class PlatformRegistry
{
    public static Dictionary<AuthMusicPlatform, PlatformAuthConfig> CreateDefaultConfigs()
    {
        return new Dictionary<AuthMusicPlatform, PlatformAuthConfig>
        {
            [AuthMusicPlatform.Qishui] = new PlatformAuthConfig
            {
                Platform = AuthMusicPlatform.Qishui,
                SourceKey = "qishui",
                DisplayName = "汽水音乐",
                DefaultFlowType = AuthFlowType.WebViewCookie,
                AuthBadgeText = "WebView2 Cookie",
                LoginUrl = "https://music.douyin.com/",
                CookieTargetUrl = "https://music.douyin.com/",
                ExtraCookieUrls = ["https://api.qishui.com/", "https://www.douyin.com/"],
                RequiredCookieKeys = ["sessionid"],
                ManualPlaceholder = "粘贴包含 sessionid=... 或 sessionid_ss=... 的抖音/汽水 Cookie（也可直接粘贴 sessionid 值）"
            },
            [AuthMusicPlatform.KuGou] = new PlatformAuthConfig
            {
                Platform = AuthMusicPlatform.KuGou,
                SourceKey = "kugou",
                DisplayName = "酷狗音乐",
                DefaultFlowType = AuthFlowType.WebViewCookie,
                AuthBadgeText = "WebView2 Cookie",
                LoginUrl = "https://www.kugou.com/",
                CookieTargetUrl = "https://www.kugou.com/",
                ExtraCookieUrls = ["https://m.kugou.com/", "https://login-user.kugou.com/"],
                RequiredCookieKeys = ["t", "KugooID"],
                ManualPlaceholder = "粘贴包含 t=...; KugooID=... 的酷狗网页端 Cookie 字符串"
            },
            [AuthMusicPlatform.KuWo] = new PlatformAuthConfig
            {
                Platform = AuthMusicPlatform.KuWo,
                SourceKey = "kuwo",
                DisplayName = "酷我音乐",
                DefaultFlowType = AuthFlowType.WebViewCookie,
                AuthBadgeText = "WebView2 Cookie",
                LoginUrl = "https://www.kuwo.cn/",
                CookieTargetUrl = "https://www.kuwo.cn/",
                ExtraCookieUrls = ["https://m.kuwo.cn/", "https://h5app.kuwo.cn/"],
                RequiredCookieKeys = ["userid"],
                ManualPlaceholder = "粘贴包含 userid=...（或 uid=...; kw_token=...）的酷我网页 Cookie"
            },
            [AuthMusicPlatform.Migu] = new PlatformAuthConfig
            {
                Platform = AuthMusicPlatform.Migu,
                SourceKey = "migu",
                DisplayName = "咪咕音乐",
                DefaultFlowType = AuthFlowType.WebViewCookie,
                AuthBadgeText = "WebView2 Cookie",
                LoginUrl = "https://music.migu.cn/v5/",
                CookieTargetUrl = "https://music.migu.cn/",
                ExtraCookieUrls = ["https://app.c.nf.migu.cn/", "https://c.musicapp.migu.cn/", "https://passport.migu.cn/", "https://m.music.migu.cn/"],
                RequiredCookieKeys = ["mg_auth_pacmtoken"],
                ManualPlaceholder = "粘贴包含 mg_auth_pacmtoken=...; mg_auth_uid=...（或 migu_music_uid=...）的咪咕音乐 Cookie"
            },
            [AuthMusicPlatform.Bilibili] = new PlatformAuthConfig
            {
                Platform = AuthMusicPlatform.Bilibili,
                SourceKey = "bilibili",
                DisplayName = "B站音乐",
                DefaultFlowType = AuthFlowType.WebViewCookie,
                AuthBadgeText = "WebView2 Cookie",
                LoginUrl = "https://passport.bilibili.com/login",
                CookieTargetUrl = "https://www.bilibili.com/",
                ExtraCookieUrls = ["https://api.bilibili.com/"],
                RequiredCookieKeys = ["SESSDATA", "bili_jct"],
                ManualPlaceholder = "粘贴包含 SESSDATA=...; bili_jct=... 的 B 站 Cookie（可解锁大会员无损/Hi-Res 音轨与个人收藏夹）"
            },
            [AuthMusicPlatform.Spotify] = new PlatformAuthConfig
            {
                Platform = AuthMusicPlatform.Spotify,
                SourceKey = "spotify",
                DisplayName = "Spotify",
                DefaultFlowType = AuthFlowType.WebViewCookie,
                SupportsOAuthPkce = true,
                AuthBadgeText = "网页免密 / PKCE",
                AuthorizeEndpoint = "https://accounts.spotify.com/authorize",
                TokenEndpoint = "https://accounts.spotify.com/api/token",
                Scopes = "user-read-private user-library-read user-library-modify playlist-read-private playlist-modify-private playlist-modify-public",
                LoopbackPort = 48920,
                LoginUrl = "https://accounts.spotify.com/login?continue=https%3A%2F%2Fopen.spotify.com%2F",
                CookieTargetUrl = "https://open.spotify.com/",
                ExtraCookieUrls = ["https://accounts.spotify.com/"],
                RequiredCookieKeys = ["sp_dc"],
                ManualPlaceholder = "粘贴包含 sp_dc=... 的 Spotify Cookie（或直接粘贴 sp_dc 值 / Bearer Token）"
            },
            [AuthMusicPlatform.AppleMusic] = new PlatformAuthConfig
            {
                Platform = AuthMusicPlatform.AppleMusic,
                SourceKey = "apple",
                DisplayName = "Apple Music",
                DefaultFlowType = AuthFlowType.WebViewCookie,
                SupportsAppleMusicKit = true,
                AuthBadgeText = "网页免密 / MusicKit",
                LoginUrl = "https://music.apple.com/",
                CookieTargetUrl = "https://music.apple.com/",
                ExtraCookieUrls = ["https://authorize.music.apple.com/"],
                RequiredCookieKeys = ["media-user-token"],
                ManualPlaceholder = "粘贴 Apple Music 的 media-user-token 值或完整 Cookie 字符串"
            },
            [AuthMusicPlatform.Deezer] = new PlatformAuthConfig
            {
                Platform = AuthMusicPlatform.Deezer,
                SourceKey = "deezer",
                DisplayName = "Deezer 全球馆",
                DefaultFlowType = AuthFlowType.WebViewCookie,
                AuthBadgeText = "WebView2 arl",
                LoginUrl = "https://www.deezer.com/login",
                CookieTargetUrl = "https://www.deezer.com/",
                RequiredCookieKeys = ["arl"],
                ManualPlaceholder = "粘贴包含 arl=... 的 Deezer Cookie（或直接粘贴 192 位 arl 字符串）"
            },
            [AuthMusicPlatform.Tidal] = new PlatformAuthConfig
            {
                Platform = AuthMusicPlatform.Tidal,
                SourceKey = "tidal",
                DisplayName = "Tidal",
                DefaultFlowType = AuthFlowType.WebViewCookie,
                SupportsOAuthPkce = true,
                AuthBadgeText = "网页免密 / PKCE",
                AuthorizeEndpoint = "https://login.tidal.com/authorize",
                TokenEndpoint = "https://auth.tidal.com/v1/oauth2/token",
                Scopes = "user.read collection.read collection.write playlists.read playlists.write",
                LoopbackPort = 48921,
                LoginUrl = "https://listen.tidal.com/login",
                CookieTargetUrl = "https://listen.tidal.com/",
                ExtraCookieUrls = ["https://login.tidal.com/"],
                RequiredCookieKeys = ["token"],
                ManualPlaceholder = "粘贴 Tidal Bearer AccessToken 或网页完整 Cookie"
            },
            [AuthMusicPlatform.YouTubeMusic] = new PlatformAuthConfig
            {
                Platform = AuthMusicPlatform.YouTubeMusic,
                SourceKey = "ytmusic",
                DisplayName = "YouTube Music",
                DefaultFlowType = AuthFlowType.WebViewCookie,
                AuthBadgeText = "SAPISIDHASH",
                LoginUrl = "https://accounts.google.com/ServiceLogin?ltmpl=music&service=youtube&continue=https%3A%2F%2Fmusic.youtube.com%2F",
                CookieTargetUrl = "https://music.youtube.com/",
                ExtraCookieUrls = ["https://www.youtube.com/", "https://accounts.google.com/"],
                RequiredCookieKeys = ["SAPISID"],
                ManualPlaceholder = "粘贴包含 SAPISID=...（或 __Secure-3PAPISID=...）的 YouTube Music 网页 Cookie"
            }
        };
    }
}

/// <summary>
/// 个人中心顶部「全平台账号矩阵」单个平台卡片视图模型（支持 12 平台）
/// </summary>
public partial class PlatformAccountCardItem : ObservableObject
{
    public string ProviderKey { get; init; } = string.Empty;
    public AuthMusicPlatform? PlatformEnum { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string AuthBadgeText { get; init; } = string.Empty;
    public string LogoUri => ImageUriHelper.Normalize(PlatformVisualHelper.GetLogoUri(ProviderKey));
    public Microsoft.UI.Xaml.Media.SolidColorBrush? PlatformBrush => PlatformVisualHelper.GetPlatformBrush(ProviderKey);

    private bool _isLoggedIn;
    public bool IsLoggedIn
    {
        get => _isLoggedIn;
        set
        {
            if (SetProperty(ref _isLoggedIn, value))
            {
                OnPropertyChanged(nameof(LoggedInDotVisibility));
                OnPropertyChanged(nameof(LoggedOutDotVisibility));
                OnPropertyChanged(nameof(ActionHintText));
            }
        }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(SelectedHighlightVisibility));
                OnPropertyChanged(nameof(UnselectedBorderVisibility));
            }
        }
    }

    private string _statusSummary = "未登录";
    public string StatusSummary
    {
        get => _statusSummary;
        set => SetProperty(ref _statusSummary, value);
    }

    public string ActionHintText => IsLoggedIn ? "已授权" : "点击登录";

    public Microsoft.UI.Xaml.Visibility LoggedInDotVisibility =>
        _isLoggedIn ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility LoggedOutDotVisibility =>
        _isLoggedIn ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public Microsoft.UI.Xaml.Visibility SelectedHighlightVisibility =>
        _isSelected ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility UnselectedBorderVisibility =>
        _isSelected ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
}
