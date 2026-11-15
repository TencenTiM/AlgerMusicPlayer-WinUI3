using System.ComponentModel;
using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.Auth;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AlgerMusicPlayer.WinUI.Views;

/// <summary>
/// 个人中心 12 平台账号矩阵、WebView2/PKCE/MusicKit 登录交互与歌单联动
/// </summary>
public sealed partial class MainWindow
{
    private string _loadedDevConfigProviderKey = string.Empty;

    private void BindPlatformAuthCenter()
    {
        PlatformAccountMatrixRepeater.ItemsSource = ViewModel.PlatformAccountCards;
        ViewModel.PropertyChanged += OnPlatformAuthViewModelPropertyChanged;
        ViewModel.AccountCenterStateChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            SyncLoginPanelsVisibilityUi();
            UpdateQqCollectedEmptyHintUi();
        });
        ViewModel.UserCreatedPlaylists.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateUserLibraryEmptyHintUi);
        ViewModel.UserFavoritePlaylists.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateUserLibraryEmptyHintUi);
        ViewModel.UserSubscribedAlbums.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateUserLibraryEmptyHintUi);
        ViewModel.QqCollectedPlaylists.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateQqCollectedEmptyHintUi);
        ViewModel.SyncUserAccountCenterCards();
        UpdateQqCollectedEmptyHintUi();
    }

    private void OnPlatformAuthViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModels.MainViewModel.MultiPlatformStatusSummary)
            or nameof(ViewModels.MainViewModel.SelectedUserPlatformKey))
        {
            DispatcherQueue.TryEnqueue(SyncLoginPanelsVisibilityUi);
        }
    }

    private void OnPlatformMatrixCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string provider) return;
        SwitchLoginProviderUi(provider);
    }

    private void SyncMultiPlatformPanelsAndLibraryUi()
    {
        bool isNetease = string.Equals(_activeLoginProvider, "netease", StringComparison.OrdinalIgnoreCase);
        bool isQq = string.Equals(_activeLoginProvider, "qq", StringComparison.OrdinalIgnoreCase);

        MultiPlatformSummaryBadgeText.Text = ViewModel.MultiPlatformStatusSummary;
        OpenQqDedicatedCenterBtn.Visibility = isQq ? Visibility.Visible : Visibility.Collapsed;

        // 判断 12 平台中是否有任意已登录账号以控制“退出全部平台账号”按钮
        bool anyExtendedLoggedIn = Enum.GetValues<AuthMusicPlatform>().Any(p => ViewModel.PlatformAuth.HasValidSession(p));
        bool anyLoggedIn = ViewModel.CurrentUser != null || ViewModel.CurrentQqUser != null || anyExtendedLoggedIn;
        LogoutAllPlatformsBtn.Visibility = anyLoggedIn ? Visibility.Visible : Visibility.Collapsed;

        // 仅网易云视图展示右栏听歌排行与“收藏的专辑”Tab
        UserPlayRecordCard.Visibility = isNetease ? Visibility.Visible : Visibility.Collapsed;
        UserPlayRecordColumn.Width = isNetease ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        UserLibraryAlbumNavItem.Visibility = isNetease ? Visibility.Visible : Visibility.Collapsed;
        if (!isNetease && UserSubscribedAlbumsGrid.Visibility == Visibility.Visible)
        {
            UserSubscribedAlbumsGrid.Visibility = Visibility.Collapsed;
            UserFavoritePlaylistsGrid.Visibility = Visibility.Collapsed;
            UserCreatedPlaylistsGrid.Visibility = Visibility.Visible;
            if (UserLibraryNavView.MenuItems.Count > 0)
            {
                UserLibraryNavView.SelectedItem = UserLibraryNavView.MenuItems[0];
            }
        }

        if (!isNetease && !isQq && PlatformAuthService.TryMapSourceToPlatform(_activeLoginProvider, out var platform))
        {
            MultiPlatformAuthProviderPanel.Visibility = Visibility.Visible;
            var config = ViewModel.PlatformAuth.GetConfig(platform);
            var session = ViewModel.PlatformAuth.TryGetSession(platform);
            bool hasSession = ViewModel.PlatformAuth.HasValidSession(platform);

            SetImageUrlSafe(ActivePlatformAuthLogoImg, PlatformVisualHelper.GetLogoUri(_activeLoginProvider));
            ActivePlatformAuthTitleText.Text = config.DisplayName;
            ActivePlatformAuthBadgeText.Text = config.AuthBadgeText;
            ActivePlatformAuthHintText.Text = GetPlatformAuthUsageHint(platform);
            ActivePlatformManualCookieBox.PlaceholderText = config.ManualPlaceholder;

            if (hasSession && session != null)
            {
                ActivePlatformLoggedInBanner.Visibility = Visibility.Visible;
                string modeLabel = session.FlowType switch
                {
                    AuthFlowType.OAuth2Pkce => "OAuth 2.0 PKCE",
                    AuthFlowType.AppleMusicKitJs => "Apple MusicKit JS",
                    _ => "WebView2 Cookie / Token"
                };
                string userText = !string.IsNullOrWhiteSpace(session.Nickname) ? $"{session.Nickname} · " : string.Empty;
                string countText = ViewModel.TryGetCachedPlatformLibraryCounts(platform, out int c1, out int c2)
                    ? $" · 创建 {c1} · 收藏 {c2}"
                    : string.Empty;
                string detailText = $"{config.DisplayName} 已授权（{userText}{modeLabel} · 更新于 {session.UpdatedAt.ToLocalTime():MM-dd HH:mm}）{countText}";
                ActivePlatformSessionDetailText.Text = detailText;
                UserLoginStatusText.Text = detailText;
            }
            else
            {
                ActivePlatformLoggedInBanner.Visibility = Visibility.Collapsed;
                UserLoginStatusText.Text = $"未登录 {config.DisplayName}（推荐点击“内置浏览器登录”自动加密提取凭证）";
            }

            ActivePlatformPkceLoginBtn.Visibility = config.SupportsOAuthPkce ? Visibility.Visible : Visibility.Collapsed;
            ActivePlatformAppleMusicKitBtn.Visibility = config.SupportsAppleMusicKit ? Visibility.Visible : Visibility.Collapsed;
            ActivePlatformDevConfigExpander.Visibility = (config.SupportsOAuthPkce || config.SupportsAppleMusicKit)
                ? Visibility.Visible
                : Visibility.Collapsed;
            PkceDevConfigSubPanel.Visibility = config.SupportsOAuthPkce ? Visibility.Visible : Visibility.Collapsed;
            AppleDevConfigSubPanel.Visibility = config.SupportsAppleMusicKit ? Visibility.Visible : Visibility.Collapsed;

            if (!string.Equals(_loadedDevConfigProviderKey, _activeLoginProvider, StringComparison.OrdinalIgnoreCase))
            {
                _loadedDevConfigProviderKey = _activeLoginProvider;
                ActivePlatformManualCookieBox.Text = string.Empty;

                if (config.SupportsOAuthPkce)
                {
                    PkceRedirectUriHintText.Text = $"开发者后台 Redirect URI 白名单请填写：{config.RedirectUri}（不填 Client ID 也可直接使用上方内置浏览器登录）";
                    PkceClientIdInputBox.Text = config.ClientId;
                }

                if (config.SupportsAppleMusicKit)
                {
                    AppleTeamIdInputBox.Text = config.AppleTeamId;
                    AppleKeyIdInputBox.Text = config.AppleKeyId;
                    ApplePrivateKeyInputBox.Text = config.ApplePrivateKeyPem;
                }
            }
        }
        else
        {
            MultiPlatformAuthProviderPanel.Visibility = Visibility.Collapsed;
            _loadedDevConfigProviderKey = _activeLoginProvider;
        }

        UpdateUserLibraryEmptyHintUi();
    }

    private void UpdateUserLibraryEmptyHintUi()
    {
        if (UserLocalCustomPlaylistsGrid.Visibility == Visibility.Visible)
        {
            UserLibraryEmptyHintText.Text = "暂无本地混合歌单，点击右上角「+ 新建歌单」即可创建并跨平台混编任意歌曲。";
            UserLibraryEmptyHintText.Visibility = ViewModel.LocalCustomPlaylists.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            return;
        }

        bool isNetease = string.Equals(_activeLoginProvider, "netease", StringComparison.OrdinalIgnoreCase);
        bool isQq = string.Equals(_activeLoginProvider, "qq", StringComparison.OrdinalIgnoreCase);
        bool isFavTab = UserFavoritePlaylistsGrid.Visibility == Visibility.Visible;
        bool isAlbumTab = UserSubscribedAlbumsGrid.Visibility == Visibility.Visible;

        bool isEmpty = (UserCreatedPlaylistsGrid.Visibility == Visibility.Visible && ViewModel.UserCreatedPlaylists.Count == 0)
                       || (isFavTab && ViewModel.UserFavoritePlaylists.Count == 0)
                       || (isAlbumTab && ViewModel.UserSubscribedAlbums.Count == 0);

        if (!isEmpty)
        {
            UserLibraryEmptyHintText.Visibility = Visibility.Collapsed;
            return;
        }

        if (isQq)
        {
            if (ViewModel.CurrentQqUser == null)
            {
                UserLibraryEmptyHintText.Text = "尚未登录 QQ 音乐，请先通过上方扫码、本机 QQ 快捷登录或 Cookie 登录后同步歌单。";
            }
            else if (isFavTab)
            {
                UserLibraryEmptyHintText.Text = "当前 QQ 账号云端收藏歌单数为 0（打开任意 QQ 音乐歌单详情点击「收藏」即可同步，或点击右上角「刷新歌单」重新拉取；另请注意 QQ 号与微信登录在 QQ 音乐中属于两个独立账号曲库）。";
            }
            else
            {
                UserLibraryEmptyHintText.Text = "当前 QQ 账号暂无自建歌单，可点击右上角「刷新歌单」重新同步。";
            }
        }
        else if (isNetease)
        {
            UserLibraryEmptyHintText.Text = ViewModel.CurrentUser == null
                ? "尚未登录网易云音乐，请在上方完成扫码、UID 或 Cookie 登录后同步歌单。"
                : "当前分类下暂无同步到的网易云歌单或专辑，可点击右上角「刷新歌单」重试。";
        }
        else
        {
            bool hasExtSession = PlatformAuthService.TryMapSourceToPlatform(_activeLoginProvider, out var extPlatform) &&
                                 ViewModel.PlatformAuth.HasValidSession(extPlatform);
            if (hasExtSession)
            {
                UserLibraryEmptyHintText.Text = isFavTab
                    ? "当前账号在当前平台暂无收藏歌单，可点击右上角「刷新歌单」重新同步。"
                    : "当前分类下暂无同步到的歌单，可点击右上角「刷新歌单」重试（若该平台 Cookie 已失效，请点击上方「退出登录」后重新登录）。";
            }
            else
            {
                UserLibraryEmptyHintText.Text = "当前平台尚未登录账号，请先在上方完成登录后自动同步歌单（或用于高音质播放鉴权）。";
            }
        }

        UserLibraryEmptyHintText.Visibility = Visibility.Visible;
    }

    private void UpdateQqCollectedEmptyHintUi()
    {
        bool show = QqCollectedPlaylistsGrid.Visibility == Visibility.Visible && ViewModel.QqCollectedPlaylists.Count == 0;
        if (show)
        {
            QqCollectedEmptyHintText.Text = ViewModel.CurrentQqUser == null
                ? "尚未登录 QQ 音乐，请先点击上方「快速登录 QQ 音乐」或在个人中心登录。"
                : "当前 QQ 账号云端收藏歌单数为 0；打开任意 QQ 音乐歌单详情点击「收藏」即可同步，或点击上方「刷新歌单」重试（QQ 号与微信登录在 QQ 音乐中为两个独立账号曲库）。";
        }
        QqCollectedEmptyHintText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnRefreshActiveUserLibraryClick(object sender, RoutedEventArgs e)
    {
        if (string.Equals(_activeLoginProvider, "qq", StringComparison.OrdinalIgnoreCase))
        {
            if (ViewModel.CurrentQqUser == null && string.IsNullOrWhiteSpace(ViewModel.Config.Settings.QqMusicCookie))
            {
                ViewModel.StatusToast = "请先登录 QQ 音乐账号后再刷新歌单";
                return;
            }
            await ViewModel.RefreshQqUserProfileAsync();
            SyncLoginPanelsVisibilityUi();
            UpdateUserLibraryEmptyHintUi();
            UpdateQqCollectedEmptyHintUi();
            ViewModel.StatusToast = "已刷新 QQ 音乐个人歌单";
            return;
        }

        if (string.Equals(_activeLoginProvider, "netease", StringComparison.OrdinalIgnoreCase))
        {
            if (ViewModel.CurrentUser == null &&
                string.IsNullOrWhiteSpace(ViewModel.Config.Settings.NeteaseCookie) &&
                ViewModel.Config.Settings.SavedUid <= 0)
            {
                ViewModel.StatusToast = "请先登录网易云音乐账号后再刷新歌单";
                return;
            }
            await ViewModel.RefreshUserProfileAsync();
            SyncLoginPanelsVisibilityUi();
            UpdateUserLibraryEmptyHintUi();
            ViewModel.StatusToast = "已刷新网易云音乐个人歌单";
            return;
        }

        if (TryGetActiveExtendedPlatform(out var platform, out var config))
        {
            await ViewModel.SyncPlatformAccountLibraryAsync(platform);
            SyncLoginPanelsVisibilityUi();
            UpdateUserLibraryEmptyHintUi();
            ViewModel.StatusToast = $"已同步 {config.DisplayName} 个人歌单";
        }
    }

    private static string GetPlatformAuthUsageHint(AuthMusicPlatform platform) => platform switch
    {
        AuthMusicPlatform.Bilibili => "登录后自动注入 SESSDATA / bili_jct，可解锁大会员无损/Hi-Res DASH 音频流并同步个人收藏夹。",
        AuthMusicPlatform.YouTubeMusic => "登录后自动签名 Authorization: SAPISIDHASH 与 Cookie，解锁完整音频流及个人喜欢/歌单。",
        AuthMusicPlatform.Spotify => "支持内置网页登录自动提取 sp_dc（免开发者 Key），也支持展开下方填写 Client ID 走 OAuth 2.0 PKCE 授权。",
        AuthMusicPlatform.AppleMusic => "支持内置网页登录提取 media-user-token（免证书），也支持配置 .p8 私钥走 MusicKit JS 官方授权同步曲库。",
        AuthMusicPlatform.Tidal => "支持内置网页登录（免 Key）及 OAuth 2.0 PKCE 授权，登录后自动解锁 LOSSLESS 无损音轨与个人收藏。",
        AuthMusicPlatform.Deezer => "登录后自动加密存储 192 位 arl Cookie 并同步 Deezer 个人喜爱歌单。",
        AuthMusicPlatform.Qishui => "登录后自动提取抖音/汽水音乐 sessionid 并注入播放/检索接口。",
        AuthMusicPlatform.KuGou => "登录后自动提取 t / KugooID Cookie 用于酷狗鉴权检索。",
        AuthMusicPlatform.KuWo => "登录后自动提取 userid / kw_token 用于酷我网页用户资料及鉴权。",
        AuthMusicPlatform.Migu => "登录后自动提取 migu_music_uid / SESSION 等 Cookie 用于咪咕高音质鉴权。",
        _ => "支持内置浏览器自动嗅探登录或手动粘贴 Cookie / Token 凭证。"
    };

    private bool TryGetActiveExtendedPlatform(out AuthMusicPlatform platform, out PlatformAuthConfig config)
    {
        if (PlatformAuthService.TryMapSourceToPlatform(_activeLoginProvider, out platform))
        {
            config = ViewModel.PlatformAuth.GetConfig(platform);
            return true;
        }

        config = default!;
        return false;
    }

    private async void OnWebViewLoginActivePlatformClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetActiveExtendedPlatform(out var platform, out var config)) return;

        try
        {
            var loginWin = new MusicLoginWindow(config, AuthFlowType.WebViewCookie);
            loginWin.Activate();
            var session = await loginWin.WaitForSessionAsync();
            if (session == null)
            {
                ViewModel.StatusToast = $"已取消 {config.DisplayName} 登录";
                return;
            }

            await ViewModel.PlatformAuth.SaveSessionAsync(session);
            ViewModel.StatusToast = $"已加密保存 {config.DisplayName} 登录凭证";
            await ViewModel.SyncPlatformAccountLibraryAsync(platform);
            SyncLoginPanelsVisibilityUi();
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"{config.DisplayName} 登录失败：{ex.Message}";
        }
    }

    private async void OnSaveManualPlatformCredentialClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetActiveExtendedPlatform(out var platform, out var config)) return;

        try
        {
            await ViewModel.PlatformAuth.SaveManualCredentialAsync(platform, ActivePlatformManualCookieBox.Text);
            ActivePlatformManualCookieBox.Text = string.Empty;
            ViewModel.StatusToast = $"已加密保存 {config.DisplayName} 手动凭证";
            await ViewModel.SyncPlatformAccountLibraryAsync(platform);
            SyncLoginPanelsVisibilityUi();
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"保存失败：{ex.Message}";
        }
    }

    private async void OnPkceOAuthLoginActivePlatformClick(object sender, RoutedEventArgs e)
    {
        await RunPkceLoginAsync(saveInputsFirst: false);
    }

    private async void OnSaveAndStartPkceClick(object sender, RoutedEventArgs e)
    {
        await RunPkceLoginAsync(saveInputsFirst: true);
    }

    private async Task RunPkceLoginAsync(bool saveInputsFirst)
    {
        if (!TryGetActiveExtendedPlatform(out var platform, out var config) || !config.SupportsOAuthPkce) return;

        if (saveInputsFirst)
        {
            var cid = PkceClientIdInputBox.Text.Trim();
            if (platform == AuthMusicPlatform.Spotify) ViewModel.Config.Settings.SpotifyClientId = cid;
            else if (platform == AuthMusicPlatform.Tidal) ViewModel.Config.Settings.TidalClientId = cid;
            await ViewModel.Config.SaveSettingsAsync();
            ViewModel.PlatformAuth.SyncConfigsFromSettings();
            config = ViewModel.PlatformAuth.GetConfig(platform);
        }

        if (string.IsNullOrWhiteSpace(config.ClientId))
        {
            ActivePlatformDevConfigExpander.IsExpanded = true;
            ViewModel.StatusToast = $"请先填写 {config.DisplayName} 开发者 Client ID，或点击“内置浏览器登录”免密使用";
            return;
        }

        try
        {
            ViewModel.StatusToast = $"正在打开系统浏览器进行 {config.DisplayName} PKCE 授权（等待 {config.RedirectUri} 回调）...";
            await ViewModel.PlatformAuth.AuthorizePkceAsync(platform, forceInteractive: true);
            ViewModel.StatusToast = $"{config.DisplayName} PKCE 授权成功，已启用自动无感续期";
            await ViewModel.SyncPlatformAccountLibraryAsync(platform);
            SyncLoginPanelsVisibilityUi();
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"{config.DisplayName} PKCE 授权失败：{ex.Message}";
        }
    }

    private async void OnImportAppleP8FileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".p8");
            picker.FileTypeFilter.Add(".pem");
            picker.FileTypeFilter.Add(".txt");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            ApplePrivateKeyInputBox.Text = await File.ReadAllTextAsync(file.Path);
            ViewModel.StatusToast = $"已导入私钥文件：{file.Name}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"读取 .p8 文件失败：{ex.Message}";
        }
    }

    private async void OnAppleMusicKitLoginClick(object sender, RoutedEventArgs e)
    {
        await RunAppleMusicKitLoginAsync(saveInputsFirst: false);
    }

    private async void OnSaveAndStartAppleMusicKitClick(object sender, RoutedEventArgs e)
    {
        await RunAppleMusicKitLoginAsync(saveInputsFirst: true);
    }

    private async Task RunAppleMusicKitLoginAsync(bool saveInputsFirst)
    {
        if (!TryGetActiveExtendedPlatform(out var platform, out var config) || platform != AuthMusicPlatform.AppleMusic) return;

        if (saveInputsFirst)
        {
            ViewModel.Config.Settings.AppleMusicTeamId = AppleTeamIdInputBox.Text.Trim();
            ViewModel.Config.Settings.AppleMusicKeyId = AppleKeyIdInputBox.Text.Trim();
            ViewModel.Config.Settings.AppleMusicPrivateKeyPem = ApplePrivateKeyInputBox.Text.Trim();
            await ViewModel.Config.SaveSettingsAsync();
            ViewModel.PlatformAuth.SyncConfigsFromSettings();
            config = ViewModel.PlatformAuth.GetConfig(AuthMusicPlatform.AppleMusic);
        }

        if (string.IsNullOrWhiteSpace(config.AppleTeamId)
            || string.IsNullOrWhiteSpace(config.AppleKeyId)
            || string.IsNullOrWhiteSpace(config.ApplePrivateKeyPem))
        {
            ActivePlatformDevConfigExpander.IsExpanded = true;
            ViewModel.StatusToast = "请先填写 Team ID、Key ID 与 .p8 私钥，或直接使用上方“内置浏览器登录”免证书体验";
            return;
        }

        try
        {
            var loginWin = new MusicLoginWindow(config, AuthFlowType.AppleMusicKitJs);
            loginWin.Activate();
            var session = await loginWin.WaitForSessionAsync();
            if (session == null)
            {
                ViewModel.StatusToast = "已取消 Apple MusicKit JS 授权";
                return;
            }

            await ViewModel.PlatformAuth.SaveSessionAsync(session);
            ViewModel.StatusToast = "Apple MusicKit JS 授权成功";
            await ViewModel.SyncPlatformAccountLibraryAsync(AuthMusicPlatform.AppleMusic);
            SyncLoginPanelsVisibilityUi();
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"Apple MusicKit 授权失败：{ex.Message}";
        }
    }

    private async void OnSyncActivePlatformLibraryClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetActiveExtendedPlatform(out var platform, out var config)) return;
        await ViewModel.SyncPlatformAccountLibraryAsync(platform);
        SyncLoginPanelsVisibilityUi();
        ViewModel.StatusToast = $"已同步 {config.DisplayName} 账号资料与个人歌单";
    }

    private async void OnLogoutActivePlatformClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetActiveExtendedPlatform(out var platform, out var config)) return;
        await ViewModel.PlatformAuth.RemoveSessionAsync(platform);
        await ViewModel.SyncPlatformAccountLibraryAsync(platform);
        SyncLoginPanelsVisibilityUi();
        ViewModel.StatusToast = $"已退出 {config.DisplayName} 登录";
    }

    private async void OnLogoutAllPlatformsClick(object sender, RoutedEventArgs e)
    {
        _qrPollCts?.Cancel();
        CookieInputBox.Text = string.Empty;
        UidLoginInputBox.Text = string.Empty;
        LoginQrImage.Source = null;
        QqLoginQrImage.Source = null;
        QqPageAvatarImg.Source = null;
        QqManualPasswordInputBox.Password = string.Empty;
        ActivePlatformManualCookieBox.Text = string.Empty;

        await ViewModel.LogoutAllMusicAccountsAsync();

        BasicTokenStatusText.Text = ViewModel.LoginStatusText;
        QqPageNicknameText.Text = "QQ 音乐歌单中心";
        QqPageStatusText.Text = ViewModel.QqLoginStatusText;
        SyncLoginPanelsVisibilityUi();
        SyncCookieSettingsRowUi();
    }
}
