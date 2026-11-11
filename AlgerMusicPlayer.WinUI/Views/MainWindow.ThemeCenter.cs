using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.System;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace AlgerMusicPlayer.WinUI.Views;

/// <summary>
/// 主窗口分部类：顶栏主题中心独立弹窗 + 设置页常规内嵌「个性主题」面板 + 动态视觉引擎 + 创意主题市场 + 深度 DIY 定制
/// </summary>
public sealed partial class MainWindow
{
    private CustomThemeProfile _themeDraft = new();
    private bool _isLoadingThemeCenterUi;
    private bool _isUpdatingSharedColorPicker;
    private string _activeColorFieldKey = string.Empty;
    private string _activeThemeDiyTab = "global";
    private string _currentBackdropKindKey = "Mica";
    private int _wallpaperRenderVersion;
    private int _themeApplyVersion;

    // DIY 滑块与调色盘原生 VSync 高刷帧合并局部预览调度状态（自动匹配 60Hz~240Hz+ 显示器刷新率）
    private bool _isDiyVsyncPending;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _diyBlurPreviewTimer;
    private readonly HashSet<string> _pendingFastPreviewKeys = new(StringComparer.Ordinal);
    private string _cachedMiniPreviewWallPath = string.Empty;

    // 主题市场交互状态
    private string _selectedMarketCategory = string.Empty;
    private string _marketSearchQuery = string.Empty;
    private string _pageThemeSubMode = "local";
    private string _modalMainMode = "workshop";
    private List<MarketplaceThemeItem> _marketItems = [];

    /// <summary>
    /// 实时返回当前主界面实际应呈现的明暗基调（直接依据生效主题计算，规避 RootLayout.ActualTheme 异步滞后导致的反色漏刷）
    /// </summary>
    private bool IsCurrentUiLightMode => ThemeService.IsEffectiveLightMode(ViewModel.Theme.GetEffectiveTheme().BaseMode);

    /// <summary>
    /// 判断指定主题是否处于「iOS 27 液态玻璃 (Liquid Glass)」模式（显式选择 LiquidGlass，或自定义图片壁纸开启局部智能反色且未指定其他特定卡片风格时默认启用）
    /// </summary>
    private static bool IsLiquidGlassMode(CustomThemeProfile profile) =>
        string.Equals(profile.CardVisualStyle, "LiquidGlass", StringComparison.OrdinalIgnoreCase) ||
        (string.Equals(profile.BackdropType, "Image", StringComparison.OrdinalIgnoreCase) &&
         profile.EnableAdaptiveWallpaperText &&
         (string.IsNullOrWhiteSpace(profile.CardVisualStyle) ||
          string.Equals(profile.CardVisualStyle, "Default", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// 判断指定主题是否处于「全透明晶透无框底」模式（仅当用户显式选择 CrystalClear 档位时启用）
    /// </summary>
    private static bool IsCrystalClearMode(CustomThemeProfile profile) =>
        string.Equals(profile.CardVisualStyle, "CrystalClear", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] OverridableThemeResourceKeys =
    [
        "CardBackgroundFillColorDefaultBrush",
        "CardBackgroundFillColorSecondaryBrush",
        "CardStrokeColorDefaultBrush",
        "DividerStrokeColorDefaultBrush",
        "SecondaryPillFillBrush",
        "SecondaryPillBorderBrush",
        "TextFillColorPrimaryBrush",
        "TextFillColorSecondaryBrush",
        "TextFillColorTertiaryBrush"
    ];

    /// <summary>
    /// 启动时初始化主题中心与动态视觉特效引擎，并应用已持久化的用户主题
    /// </summary>
    private void InitializeThemeCenterEngine()
    {
        InitializeDynamicThemeVisualEngine();
        InitializeAdaptiveWallpaperTextEngine();

        _diyBlurPreviewTimer = DispatcherQueue.CreateTimer();
        _diyBlurPreviewTimer.Interval = TimeSpan.FromMilliseconds(45);
        _diyBlurPreviewTimer.IsRepeating = false;
        _diyBlurPreviewTimer.Tick += async (_, _) =>
        {
            bool isLight = ThemeService.IsEffectiveLightMode(_themeDraft.BaseMode);
            var adapted = ThemeService.ResolveModeAdaptedProfile(_themeDraft, isLight);
            await UpdateGlobalWallpaperLayerAsync(adapted);
            _activeAdaptiveRenderedProfile = adapted;
            ScheduleAdaptiveWallpaperTextPass(immediate: false);
        };

        ViewModel.Theme.ThemeApplied += OnThemeServiceApplied;
        RootLayout.ActualThemeChanged += (_, _) =>
        {
            var eff = ViewModel.Theme.GetEffectiveTheme();
            if (string.Equals(eff.BaseMode, "System", StringComparison.OrdinalIgnoreCase))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _ = ApplyThemeProfileToMainWindowAsync(ViewModel.Theme.GetEffectiveTheme());
                });
            }
        };

        var initialTheme = ViewModel.Theme.GetSavedActiveTheme();
        ViewModel.Theme.UpdateGlobalAccentBrushes(initialTheme);
        _ = ApplyThemeProfileToMainWindowAsync(initialTheme);
    }

    private void OnThemeServiceApplied(CustomThemeProfile profile)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _ = ApplyThemeProfileToMainWindowAsync(profile);
        });
    }

    // ==================== 主窗口全局与多区域实时渲染 ====================

    private async Task ApplyThemeProfileToMainWindowAsync(CustomThemeProfile rawProfile)
    {
        if (_isDiyVsyncPending)
        {
            CompositionTarget.Rendering -= OnDiyFastPreviewVsyncTick;
            _isDiyVsyncPending = false;
        }
        _diyBlurPreviewTimer?.Stop();
        _pendingFastPreviewKeys.Clear();
        int applyVersion = ++_themeApplyVersion;

        // 1. 先计算新主题的目标明暗模式（必须在还原自适应文字之前确定，确保切回浅色主题时直接恢复为深色基准字而非旧暗夜白字）
        ElementTheme targetElementTheme = rawProfile.BaseMode switch
        {
            "Light" => ElementTheme.Light,
            "System" => ElementTheme.Default,
            _ => ElementTheme.Dark
        };

        bool isEffectiveLight = targetElementTheme == ElementTheme.Light ||
                                (targetElementTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Light);

        // 0. 立即阻断壁纸反色激活态并干净还原所有曾被自定义壁纸接管的文字/控件前景色（动态反色严格仅限自定义壁纸）
        _activeAdaptiveRenderedProfile = null;
        if (_hasActiveAdaptiveOverrides)
        {
            RestoreAllAdaptiveWallpaperTexts(isEffectiveLight);
        }

        var profile = ThemeService.ResolveModeAdaptedProfile(rawProfile, isEffectiveLight);
        ViewModel.Theme.UpdateGlobalAccentBrushes(profile);
        var (accent, hover, pressed, _) = ViewModel.Theme.DeriveAccentColors(profile);

        // 2. 窗口材质 (Mica / MicaAlt / Acrylic / Solid / Aurora / CoverFlow / Video / Image) 与底层壁纸渲染
        ApplyWindowBackdropAndBaseLayer(profile, isEffectiveLight);
        await UpdateGlobalWallpaperLayerAsync(profile);
        if (applyVersion != _themeApplyVersion) return;

        // 3. 动态流体极光、封面流光、动态视频壁纸与独立粒子氛围层
        await ApplyDynamicBackgroundAndParticlesAsync(profile, accent, isEffectiveLight);
        if (applyVersion != _themeApplyVersion) return;

        // 4. 顶部标题栏独立 DIY 渲染（支持双色流光渐变与特色皮肤顶栏微光）
        ApplyTitleBarSectionTheme(profile, accent, isEffectiveLight);

        // 5. 左侧导航栏独立 DIY 渲染（支持双色流光渐变与选中胶囊联动）
        ApplyNavigationPaneSectionTheme(profile, accent, isEffectiveLight);

        // 6. 主内容工作区与卡片全局 DIY 渲染
        ApplyContentAndCardsSectionTheme(profile, isEffectiveLight);

        // 7. 底部播放控制栏独立 DIY 渲染（支持双色流光渐变、独立主播放圆钮与进度条色）
        ApplyBottomPlayBarSectionTheme(profile, accent, hover, pressed, isEffectiveLight);

        // 8. 界面形态（经典贴底 / 悬浮胶囊灵动岛 / 极简无界玻璃）、卡片视觉质感预设、旋转黑胶唱盘与全局字体排版
        ApplyMorphologyTypographyAndCoverDetails(profile, accent, isEffectiveLight);

        // 9. 全局系统原生控件强调色（开关/滑块/复选框/输入框聚焦线）、15 个二级弹窗/抽屉/右键菜单磨砂染色与首页英雄大卡联动
        ApplySystemControlAndHeroThemeResources(profile, accent, hover, pressed, isEffectiveLight);

        // 10. 触发视觉树 ThemeResource 刷新以使卡片/文字/圆角/弹窗与原生控件覆盖立即生效
        RefreshRootElementTheme(targetElementTheme);

        // 11. 同步弹窗与常规设置内嵌面板徽章、原生最小化/关闭按钮、动态标签与歌词行配色
        var savedTheme = ViewModel.Theme.GetSavedActiveTheme();
        string badgeText = ViewModel.Theme.PreviewProfile != null
            ? $"实时预览中：{profile.Name}"
            : $"当前：{savedTheme.Name}";

        if (PageActiveThemeNameBadge != null) PageActiveThemeNameBadge.Text = badgeText;
        if (ThemeModalActiveNameBadge != null) ThemeModalActiveNameBadge.Text = badgeText;

        // 若当前正展示主题中心弹窗或设置页「个性主题」面板，原地同步主题市场卡片配色并锁定滚动条偏移，杜绝焦点顺延跳动与白字残影
        if (ThemeCenterModalOverlay.Visibility == Visibility.Visible || SettingsThemePanel.Visibility == Visibility.Visible)
        {
            var settingsSv = FindAncestorScrollViewer(SettingsThemePanel);
            double? savedOffset = (SettingsThemePanel.Visibility == Visibility.Visible && settingsSv != null)
                ? settingsSv.VerticalOffset
                : null;

            RebuildThemeLibraryCardsUi();
            RefreshMarketplaceCardsInPlace();

            if (savedOffset.HasValue && settingsSv != null)
            {
                double targetOffset = savedOffset.Value;
                settingsSv.ChangeView(null, targetOffset, null, disableAnimation: true);
                DispatcherQueue.TryEnqueue(() =>
                {
                    settingsSv.ChangeView(null, targetOffset, null, disableAnimation: true);
                });
            }
        }

        ConfigureTitleBarButtonsTheme();
        SyncThemeSettingsUi();
        SyncMvAreaButtonsUi();
        if (MusicSourcesModalOverlay.Visibility == Visibility.Visible)
        {
            SyncMusicSourceCardsUi();
        }
        SyncLyricLinesTypographyAndEmptyState();

        // 12. 仅当当前主题为「自定义图片壁纸」且开启局部反色时启动动态文字采样；非壁纸主题保持 100% 原生深浅色字效
        _activeAdaptiveRenderedProfile = profile;
        ScheduleAdaptiveWallpaperTextPass(immediate: true);
    }

    private void ApplyWindowBackdropAndBaseLayer(CustomThemeProfile profile, bool isLight)
    {
        string kind = profile.BackdropType ?? "Mica";
        bool useNativeBackdrop = kind is "Mica" or "MicaAlt" or "Acrylic";

        if (useNativeBackdrop)
        {
            GlobalWindowBaseBgLayer.Visibility = Visibility.Collapsed;
            GlobalWallpaperImage.Visibility = Visibility.Collapsed;
            GlobalWallpaperMaskBorder.Visibility = Visibility.Collapsed;

            if (_currentBackdropKindKey != kind)
            {
                try
                {
                    SystemBackdrop = kind switch
                    {
                        "MicaAlt" => new MicaBackdrop { Kind = MicaKind.BaseAlt },
                        "Acrylic" => new DesktopAcrylicBackdrop(),
                        _ => new MicaBackdrop { Kind = MicaKind.Base }
                    };
                    _currentBackdropKindKey = kind;
                }
                catch
                {
                    // 老系统不支持云母时安全回退纯色
                    SystemBackdrop = null;
                    _currentBackdropKindKey = "Solid";
                }
            }
        }
        else
        {
            if (_currentBackdropKindKey != "None")
            {
                SystemBackdrop = null;
                _currentBackdropKindKey = "None";
            }

            var fallbackBg = isLight ? Color.FromArgb(255, 248, 250, 252) : Color.FromArgb(255, 15, 23, 42);
            var winBg = ThemeService.ParseColorOrDefault(profile.WindowBgColorHex, fallbackBg);
            GlobalWindowBaseBgLayer.Background = new SolidColorBrush(winBg);
            GlobalWindowBaseBgLayer.Visibility = Visibility.Visible;
        }
    }

    private async Task UpdateGlobalWallpaperLayerAsync(CustomThemeProfile profile)
    {
        int myVersion = ++_wallpaperRenderVersion;

        if (!string.Equals(profile.BackdropType, "Image", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(profile.BackgroundImagePath) ||
            !File.Exists(profile.BackgroundImagePath))
        {
            _activeWallpaperColorGrid = null;
            GlobalWallpaperBrush.ImageSource = null;
            GlobalWallpaperImage.Visibility = Visibility.Collapsed;
            GlobalWallpaperMaskBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var maskBase = ThemeService.ParseColorOrDefault(profile.BackgroundMaskColorHex, Color.FromArgb(255, 9, 13, 22));
        GlobalWallpaperMaskBorder.Background = new SolidColorBrush(ThemeService.WithOpacity(maskBase, profile.BackgroundMaskOpacity));
        GlobalWallpaperMaskBorder.Visibility = Visibility.Visible;

        var imgSource = await ViewModel.Theme.CreateWallpaperImageSourceAsync(profile.BackgroundImagePath, profile.BackgroundBlur);
        WallpaperColorGrid? colorGrid = profile.EnableAdaptiveWallpaperText
            ? await ViewModel.Theme.GetOrBuildWallpaperColorGridAsync(profile.BackgroundImagePath, profile.BackgroundBlur)
            : null;
        if (myVersion != _wallpaperRenderVersion) return;

        if (imgSource != null)
        {
            _activeWallpaperColorGrid = colorGrid;
            GlobalWallpaperBrush.ImageSource = imgSource;
            GlobalWallpaperImage.Visibility = Visibility.Visible;
        }
        else
        {
            _activeWallpaperColorGrid = null;
            GlobalWallpaperImage.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyTitleBarSectionTheme(CustomThemeProfile profile, Color accent, bool isLight)
    {
        bool isCustomLayer = profile.BackdropType is "Image" or "Solid" or "Aurora" or "CoverFlow" or "Video";
        bool isCrystalClear = IsCrystalClearMode(profile);

        if (profile.EnableCustomTitleBar)
        {
            var defaultTbCol = ThemeService.CreateThemeTintedSurfaceColor(profile, accent, isLight, isLight ? 0.14 : 0.22);
            var bgCol = ThemeService.ParseColorOrDefault(profile.TitleBarBgHex, defaultTbCol);
            AppTitleBar.Background = ThemeService.UpdateOrCreateSectionBrush(AppTitleBar.Background, bgCol, profile.EnableTitleBarGradient, profile.TitleBarGradientEndHex, profile.TitleBarOpacity, profile.GradientAngle, useAcrylicBlur: !isCrystalClear);

            if (ThemeService.TryParseColor(profile.TitleBarTextHex, out var textCol))
            {
                var fgBrush = new SolidColorBrush(textCol);
                BrandTitleText.Foreground = fgBrush;
                foreach (var child in TitleBarRightToolbar.Children)
                {
                    if (child is Button btn)
                    {
                        btn.Foreground = fgBrush;
                    }
                }
                return;
            }
        }
        else if (isCrystalClear)
        {
            AppTitleBar.Background = new SolidColorBrush(Colors.Transparent);
        }
        else if (isCustomLayer)
        {
            // 在特色动态/定制主题下，为顶栏铺设极轻盈的水平氛围渐变微光，使顶部区域自然融入皮肤性格
            var lgb = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0.5), EndPoint = new Windows.Foundation.Point(1, 0.5) };
            byte a0 = isLight ? (byte)22 : (byte)32;
            byte a1 = isLight ? (byte)6 : (byte)10;
            var endAccent = profile.EnableAccentGradient
                ? ThemeService.ParseColorOrDefault(profile.AccentGradientEndHex, accent)
                : accent;
            lgb.GradientStops.Add(new GradientStop { Color = Color.FromArgb(a0, accent.R, accent.G, accent.B), Offset = 0.0 });
            lgb.GradientStops.Add(new GradientStop { Color = Color.FromArgb(a1, endAccent.R, endAccent.G, endAccent.B), Offset = 1.0 });
            AppTitleBar.Background = lgb;
        }
        else
        {
            AppTitleBar.Background = new SolidColorBrush(Colors.Transparent);
        }

        if (isLight)
        {
            var crispDark = new SolidColorBrush(Color.FromArgb(255, 15, 23, 42));
            BrandTitleText.Foreground = crispDark;
            foreach (var child in TitleBarRightToolbar.Children)
            {
                if (child is Button btn)
                {
                    btn.Foreground = crispDark;
                }
            }
        }
        else
        {
            BrandTitleText.ClearValue(TextBlock.ForegroundProperty);
            foreach (var child in TitleBarRightToolbar.Children)
            {
                if (child is Button btn)
                {
                    btn.ClearValue(Control.ForegroundProperty);
                }
            }
        }
    }

    private static readonly string[] NavPaneForegroundResourceKeys =
    [
        "NavigationViewItemForeground",
        "NavigationViewItemForegroundPointerOver",
        "NavigationViewItemForegroundPressed",
        "NavigationViewItemForegroundSelected",
        "NavigationViewItemForegroundSelectedPointerOver",
        "NavigationViewItemForegroundSelectedPressed",
        "NavigationViewItemHeaderForeground",
        "NavigationViewButtonForeground"
    ];

    private void ApplyNavigationPaneSectionTheme(CustomThemeProfile profile, Color accent, bool isLight)
    {
        var navRes = MainNavView.Resources;
        var indicatorCol = ThemeService.EnsureReadableButtonFill(accent, isLight);
        navRes["NavigationViewSelectionIndicatorForeground"] = new SolidColorBrush(indicatorCol);

        bool isCustomLayer = profile.BackdropType is "Image" or "Solid" or "Aurora" or "CoverFlow" or "Video";
        bool isLiquidGlass = IsLiquidGlassMode(profile);
        bool isCrystalClear = IsCrystalClearMode(profile);
        Color tintedSurface = ThemeService.CreateThemeTintedSurfaceColor(profile, accent, isLight, isLight ? 0.13 : 0.22);

        // 同步让顶栏搜索框在液态玻璃/全透明壁纸/动态玻璃背景下呈现对应质感，并在浅色下确保输入字与占位符高对比清晰
        if (TopSearchBox != null)
        {
            var sbRes = TopSearchBox.Resources;
            sbRes["TextControlBorderBrushFocused"] = new SolidColorBrush(indicatorCol);
            if (isLiquidGlass)
            {
                var boxBg = ThemeService.CreateLiquidGlassSurfaceBrush(accent, isLight, intensity: 1.15);
                var boxFocusBg = new SolidColorBrush(isLight
                    ? Color.FromArgb(195, 255, 255, 255)
                    : Color.FromArgb(145, 15, 23, 42));
                var boxBorder = ThemeService.CreateLiquidGlassBorderBrush(accent, intensity: 1.0);
                sbRes["TextControlBackground"] = boxBg;
                sbRes["TextControlBackgroundPointerOver"] = boxFocusBg;
                sbRes["TextControlBackgroundFocused"] = boxFocusBg;
                sbRes["TextControlBorderBrush"] = boxBorder;
            }
            else if (isCrystalClear)
            {
                var boxBg = new SolidColorBrush(Color.FromArgb(20, 148, 163, 184));
                var boxFocusBg = new SolidColorBrush(isLight
                    ? Color.FromArgb(185, 255, 255, 255)
                    : Color.FromArgb(135, 15, 23, 42));
                var boxBorder = new SolidColorBrush(Color.FromArgb(76, 148, 163, 184));
                sbRes["TextControlBackground"] = boxBg;
                sbRes["TextControlBackgroundPointerOver"] = boxFocusBg;
                sbRes["TextControlBackgroundFocused"] = boxFocusBg;
                sbRes["TextControlBorderBrush"] = boxBorder;
            }
            else if (isCustomLayer)
            {
                var boxBg = new SolidColorBrush(isLight
                    ? Color.FromArgb(115, 255, 255, 255)
                    : Color.FromArgb(88, tintedSurface.R, tintedSurface.G, tintedSurface.B));
                var boxFocusBg = new SolidColorBrush(isLight
                    ? Color.FromArgb(205, 255, 255, 255)
                    : Color.FromArgb(145, tintedSurface.R, tintedSurface.G, tintedSurface.B));
                var borderMix = ThemeService.LerpColor(Colors.White, accent, isLight ? 0.30 : 0.45);
                var boxBorder = new SolidColorBrush(Color.FromArgb(isLight ? (byte)185 : (byte)72, borderMix.R, borderMix.G, borderMix.B));
                sbRes["TextControlBackground"] = boxBg;
                sbRes["TextControlBackgroundPointerOver"] = boxFocusBg;
                sbRes["TextControlBackgroundFocused"] = boxFocusBg;
                sbRes["TextControlBorderBrush"] = boxBorder;
            }
            else
            {
                sbRes.Remove("TextControlBackground");
                sbRes.Remove("TextControlBackgroundPointerOver");
                sbRes.Remove("TextControlBackgroundFocused");
                sbRes.Remove("TextControlBorderBrush");
            }

            if (isLight)
            {
                sbRes["TextControlForeground"] = new SolidColorBrush(Color.FromArgb(255, 15, 23, 42));
                sbRes["TextControlPlaceholderForeground"] = new SolidColorBrush(Color.FromArgb(255, 71, 85, 105));
            }
            else
            {
                sbRes.Remove("TextControlForeground");
                sbRes.Remove("TextControlPlaceholderForeground");
            }
        }

        // 所有主题下左侧导航栏选中胶囊均融合当前主题灵魂强调色
        var defaultSelCol = Color.FromArgb(isLight ? (byte)0x28 : (byte)0x34, accent.R, accent.G, accent.B);
        var defaultSelHoverCol = Color.FromArgb(isLight ? (byte)0x38 : (byte)0x46, accent.R, accent.G, accent.B);

        Brush? explicitNavFg = null;
        if (profile.EnableCustomNavPane)
        {
            var paneCol = ThemeService.ParseColorOrDefault(profile.NavPaneBgHex, tintedSurface);
            Brush? curPane = navRes.TryGetValue("NavigationViewDefaultPaneBackground", out var cp) ? cp as Brush : null;
            var paneBrush = ThemeService.UpdateOrCreateSectionBrush(curPane, paneCol, profile.EnableNavPaneGradient, profile.NavPaneGradientEndHex, profile.NavPaneOpacity, profile.GradientAngle, useAcrylicBlur: !(isCrystalClear || isLiquidGlass));
            navRes["NavigationViewDefaultPaneBackground"] = paneBrush;
            navRes["NavigationViewExpandedPaneBackground"] = paneBrush;
            navRes["NavigationViewTopPaneBackground"] = paneBrush;

            var selCol = ThemeService.TryParseColor(profile.NavPaneSelectedBgHex, out var parsedSel)
                ? parsedSel
                : defaultSelCol;
            ThemeService.SetOrUpdateSolidBrush(navRes, "NavigationViewItemBackgroundSelected", selCol);
            ThemeService.SetOrUpdateSolidBrush(navRes, "NavigationViewItemBackgroundSelectedPointerOver", selCol);

            if (ThemeService.TryParseColor(profile.NavPaneTextHex, out var txtCol))
            {
                explicitNavFg = new SolidColorBrush(txtCol);
            }
        }
        else
        {
            if (isLiquidGlass)
            {
                var liquidPane = ThemeService.CreateLiquidGlassSurfaceBrush(accent, isLight, intensity: 0.96, blurAmount: 15.5f);
                navRes["NavigationViewDefaultPaneBackground"] = liquidPane;
                navRes["NavigationViewExpandedPaneBackground"] = liquidPane;
                navRes["NavigationViewTopPaneBackground"] = liquidPane;
            }
            else if (isCrystalClear)
            {
                var clearBrush = new SolidColorBrush(Colors.Transparent);
                navRes["NavigationViewDefaultPaneBackground"] = clearBrush;
                navRes["NavigationViewExpandedPaneBackground"] = clearBrush;
                navRes["NavigationViewTopPaneBackground"] = clearBrush;
            }
            else if (isCustomLayer)
            {
                Brush glassPane = isLight
                    ? ThemeService.CreateFrostedGlassBrush(tintedSurface, tintOpacity: 0.14, luminosityOpacity: 0.22, fallbackAlpha: 50)
                    : ThemeService.CreateFrostedGlassBrush(tintedSurface, tintOpacity: 0.36, luminosityOpacity: 0.50, fallbackAlpha: 98);
                navRes["NavigationViewDefaultPaneBackground"] = glassPane;
                navRes["NavigationViewExpandedPaneBackground"] = glassPane;
                navRes["NavigationViewTopPaneBackground"] = glassPane;
            }
            else
            {
                navRes.Remove("NavigationViewDefaultPaneBackground");
                navRes.Remove("NavigationViewExpandedPaneBackground");
                navRes.Remove("NavigationViewTopPaneBackground");
            }
            navRes["NavigationViewItemBackgroundSelected"] = new SolidColorBrush(defaultSelCol);
            navRes["NavigationViewItemBackgroundSelectedPointerOver"] = new SolidColorBrush(defaultSelHoverCol);
        }

        // 统一设置主视图继承基准前景色与左侧导航菜单项各级状态前景色，彻底杜绝浅色模式下白字隐形
        var baseContentFg = (profile.EnableCustomContent && ThemeService.TryParseColor(profile.PrimaryTextHex, out var customPrimary))
            ? new SolidColorBrush(customPrimary)
            : new SolidColorBrush(isLight ? Color.FromArgb(255, 15, 23, 42) : Color.FromArgb(255, 248, 250, 252));
        MainNavView.Foreground = baseContentFg;

        Brush? targetNavFg = explicitNavFg ?? (isLight ? new SolidColorBrush(Color.FromArgb(255, 15, 23, 42)) : null);
        if (targetNavFg != null)
        {
            var headerFg = explicitNavFg ?? new SolidColorBrush(Color.FromArgb(255, 71, 85, 105));
            navRes["NavigationViewItemForeground"] = targetNavFg;
            navRes["NavigationViewItemForegroundPointerOver"] = targetNavFg;
            navRes["NavigationViewItemForegroundPressed"] = targetNavFg;
            navRes["NavigationViewItemForegroundSelected"] = targetNavFg;
            navRes["NavigationViewItemForegroundSelectedPointerOver"] = targetNavFg;
            navRes["NavigationViewItemForegroundSelectedPressed"] = targetNavFg;
            navRes["NavigationViewItemHeaderForeground"] = headerFg;
            navRes["NavigationViewButtonForeground"] = targetNavFg;
        }
        else
        {
            foreach (var key in NavPaneForegroundResourceKeys)
            {
                navRes.Remove(key);
            }
        }

        ApplyNavItemsForeground(targetNavFg);
    }

    private void ApplyNavItemsForeground(Brush? fgBrush)
    {
        var headerBrush = fgBrush ?? (IsCurrentUiLightMode ? new SolidColorBrush(Color.FromArgb(255, 71, 85, 105)) : null);
        foreach (var obj in MainNavView.MenuItems.Concat(MainNavView.FooterMenuItems))
        {
            if (obj is NavigationViewItem item)
            {
                if (fgBrush != null) item.Foreground = fgBrush;
                else item.ClearValue(Control.ForegroundProperty);
            }
            else if (obj is NavigationViewItemHeader header)
            {
                if (headerBrush != null) header.Foreground = headerBrush;
                else header.ClearValue(Control.ForegroundProperty);
            }
        }
    }

    private void ApplyContentAndCardsSectionTheme(CustomThemeProfile profile, bool isLight)
    {
        var rootRes = RootLayout.Resources;
        var navRes = MainNavView.Resources;
        bool isCustomLayer = profile.BackdropType is "Image" or "Solid" or "Aurora" or "CoverFlow" or "Video";
        bool isLiquidGlass = IsLiquidGlassMode(profile);
        bool isCrystalClear = IsCrystalClearMode(profile);

        // 当启用自定义底层壁纸/极光或开启内容区独立定制时，让 NavigationView 默认实心内容底透明化，交由我们的图层控制
        if (profile.EnableCustomContent || isCustomLayer || isLiquidGlass || isCrystalClear)
        {
            navRes["NavigationViewContentBackground"] = new SolidColorBrush(Colors.Transparent);
            navRes["NavigationViewContentGridBorderBrush"] = new SolidColorBrush(Colors.Transparent);
        }
        else
        {
            navRes.Remove("NavigationViewContentBackground");
            navRes.Remove("NavigationViewContentGridBorderBrush");
        }

        if (profile.EnableCustomContent)
        {
            var accent = ThemeService.ParseColorOrDefault(profile.AccentColorHex, Color.FromArgb(255, 16, 185, 129));
            var iceWhite = ThemeService.CreateTintedIceWhite(accent, 0.055);

            var contentBg = ThemeService.ParseColorOrDefault(profile.ContentBgHex, isLight ? Color.FromArgb(255, 248, 250, 252) : Color.FromArgb(255, 11, 17, 32));
            double effContentOp = (isCrystalClear || isLiquidGlass) ? 0 : ((isLight && isCustomLayer) ? Math.Min(profile.ContentOpacity, 10) : profile.ContentOpacity);
            MainContentWorkspaceGrid.Background = ThemeService.UpdateOrCreateSectionBrush(MainContentWorkspaceGrid.Background, contentBg, false, null, effContentOp, 0, useAcrylicBlur: false);

            if (isLiquidGlass)
            {
                rootRes["CardBackgroundFillColorDefaultBrush"] = ThemeService.CreateLiquidGlassSurfaceBrush(accent, isLight, intensity: 1.04, blurAmount: 14.0f);
                rootRes["CardBackgroundFillColorSecondaryBrush"] = ThemeService.CreateLiquidGlassSurfaceBrush(accent, isLight, intensity: 0.86, blurAmount: 10.5f);
                rootRes["SecondaryPillFillBrush"] = ThemeService.CreateLiquidGlassSurfaceBrush(accent, isLight, intensity: 0.95, blurAmount: 9.5f);
                rootRes["SecondaryPillBorderBrush"] = new SolidColorBrush(Colors.Transparent);
                rootRes["CardStrokeColorDefaultBrush"] = ThemeService.CreateLiquidGlassBorderBrush(accent, intensity: 1.0);
                ThemeService.SetOrUpdateSolidBrush(rootRes, "DividerStrokeColorDefaultBrush", isLight
                    ? Color.FromArgb(30, 15, 23, 42)
                    : Color.FromArgb(48, 255, 255, 255));
            }
            else if (isCrystalClear)
            {
                rootRes.Remove("SecondaryPillFillBrush");
                rootRes.Remove("SecondaryPillBorderBrush");
                ThemeService.SetOrUpdateSolidBrush(rootRes, "CardBackgroundFillColorDefaultBrush", Colors.Transparent);
                ThemeService.SetOrUpdateSolidBrush(rootRes, "CardBackgroundFillColorSecondaryBrush", Colors.Transparent);
                ThemeService.SetOrUpdateSolidBrush(rootRes, "CardStrokeColorDefaultBrush", Color.FromArgb(72, 148, 163, 184));
                ThemeService.SetOrUpdateSolidBrush(rootRes, "DividerStrokeColorDefaultBrush", Color.FromArgb(46, 148, 163, 184));
            }
            else
            {
                rootRes.Remove("SecondaryPillFillBrush");
                rootRes.Remove("SecondaryPillBorderBrush");
                var cardBg = ThemeService.ParseColorOrDefault(profile.CardBgHex, isLight ? iceWhite : Color.FromArgb(255, 30, 41, 59));
                Brush? curCardPrimary = rootRes.TryGetValue("CardBackgroundFillColorDefaultBrush", out var ccp) ? ccp as Brush : null;
                Brush cardPrimaryBrush = ThemeService.UpdateOrCreateSectionBrush(curCardPrimary, cardBg, false, null, profile.CardOpacity, 0, useAcrylicBlur: true);

                var secondaryCardCol = ThemeService.AdjustLightness(cardBg, isLight ? -0.02 : 0.04);
                var secondaryFinalCol = isLight
                    ? Color.FromArgb(32, secondaryCardCol.R, secondaryCardCol.G, secondaryCardCol.B)
                    : ThemeService.WithOpacity(secondaryCardCol, Math.Clamp(profile.CardOpacity - 8, 12, 100));

                var borderCol = ThemeService.ParseColorOrDefault(profile.CardBorderHex, isLight ? Colors.White : Color.FromArgb(255, 51, 65, 85));
                var borderFinalCol = isLight
                    ? Color.FromArgb(185, borderCol.R, borderCol.G, borderCol.B)
                    : ThemeService.WithOpacity(borderCol, Math.Max(profile.CardOpacity, 55));
                var dividerFinalCol = isLight
                    ? Color.FromArgb(24, 15, 23, 42)
                    : Color.FromArgb(90, borderCol.R, borderCol.G, borderCol.B);

                rootRes["CardBackgroundFillColorDefaultBrush"] = cardPrimaryBrush;
                ThemeService.SetOrUpdateSolidBrush(rootRes, "CardBackgroundFillColorSecondaryBrush", secondaryFinalCol);
                ThemeService.SetOrUpdateSolidBrush(rootRes, "CardStrokeColorDefaultBrush", borderFinalCol);
                ThemeService.SetOrUpdateSolidBrush(rootRes, "DividerStrokeColorDefaultBrush", dividerFinalCol);
            }

            if (ThemeService.TryParseColor(profile.PrimaryTextHex, out var pText))
                ThemeService.SetOrUpdateSolidBrush(rootRes, "TextFillColorPrimaryBrush", pText);
            else if (isLight)
                ThemeService.SetOrUpdateSolidBrush(rootRes, "TextFillColorPrimaryBrush", Color.FromArgb(255, 15, 23, 42));
            else
                rootRes.Remove("TextFillColorPrimaryBrush");

            if (ThemeService.TryParseColor(profile.SecondaryTextHex, out var sText))
                ThemeService.SetOrUpdateSolidBrush(rootRes, "TextFillColorSecondaryBrush", sText);
            else if (isLight)
                ThemeService.SetOrUpdateSolidBrush(rootRes, "TextFillColorSecondaryBrush", Color.FromArgb(255, 51, 65, 85));
            else
                rootRes.Remove("TextFillColorSecondaryBrush");

            if (isLight)
                ThemeService.SetOrUpdateSolidBrush(rootRes, "TextFillColorTertiaryBrush", Color.FromArgb(255, 71, 85, 105));
            else
                rootRes.Remove("TextFillColorTertiaryBrush");

            double r = Math.Clamp(profile.CardCornerRadius, 4, 24);
            rootRes["OverlayCornerRadius"] = new CornerRadius(r);
            rootRes["ControlCornerRadius"] = new CornerRadius(Math.Max(4, Math.Round(r * 0.65)));
            ApplyCustomCornerRadiusToVisibleCards(MainContentWorkspaceGrid, new CornerRadius(r));
        }
        else
        {
            MainContentWorkspaceGrid.ClearValue(Panel.BackgroundProperty);
            foreach (var key in OverridableThemeResourceKeys)
            {
                rootRes.Remove(key);
            }
            rootRes.Remove("OverlayCornerRadius");
            rootRes.Remove("ControlCornerRadius");

            if (isLiquidGlass)
            {
                var accent = ThemeService.ParseColorOrDefault(profile.AccentColorHex, Color.FromArgb(255, 16, 185, 129));
                MainContentWorkspaceGrid.Background = new SolidColorBrush(Colors.Transparent);
                rootRes["CardBackgroundFillColorDefaultBrush"] = ThemeService.CreateLiquidGlassSurfaceBrush(accent, isLight, intensity: 1.04, blurAmount: 14.0f);
                rootRes["CardBackgroundFillColorSecondaryBrush"] = ThemeService.CreateLiquidGlassSurfaceBrush(accent, isLight, intensity: 0.86, blurAmount: 10.5f);
                rootRes["SecondaryPillFillBrush"] = ThemeService.CreateLiquidGlassSurfaceBrush(accent, isLight, intensity: 0.95, blurAmount: 9.5f);
                rootRes["SecondaryPillBorderBrush"] = new SolidColorBrush(Colors.Transparent);
                rootRes["CardStrokeColorDefaultBrush"] = ThemeService.CreateLiquidGlassBorderBrush(accent, intensity: 1.0);
                rootRes["DividerStrokeColorDefaultBrush"] = new SolidColorBrush(isLight
                    ? Color.FromArgb(30, 15, 23, 42)
                    : Color.FromArgb(48, 255, 255, 255));
            }
            else if (isCrystalClear)
            {
                MainContentWorkspaceGrid.Background = new SolidColorBrush(Colors.Transparent);
                rootRes["CardBackgroundFillColorDefaultBrush"] = new SolidColorBrush(Colors.Transparent);
                rootRes["CardBackgroundFillColorSecondaryBrush"] = new SolidColorBrush(Colors.Transparent);
                rootRes["CardStrokeColorDefaultBrush"] = new SolidColorBrush(Color.FromArgb(72, 148, 163, 184));
                rootRes["DividerStrokeColorDefaultBrush"] = new SolidColorBrush(Color.FromArgb(46, 148, 163, 184));
            }
            else if (isCustomLayer)
            {
                var accent = ThemeService.ParseColorOrDefault(profile.AccentColorHex, Color.FromArgb(255, 16, 185, 129));
                var tintedCardSurface = ThemeService.CreateThemeTintedSurfaceColor(profile, accent, isLight, isLight ? 0.11 : 0.20);
                var borderTint = ThemeService.LerpColor(Colors.White, accent, isLight ? 0.20 : 0.38);

                rootRes["CardBackgroundFillColorDefaultBrush"] = isLight
                    ? ThemeService.CreateFrostedGlassBrush(tintedCardSurface, tintOpacity: 0.09, luminosityOpacity: 0.15, fallbackAlpha: 40)
                    : ThemeService.CreateFrostedGlassBrush(tintedCardSurface, tintOpacity: 0.26, luminosityOpacity: 0.38, fallbackAlpha: 78);
                rootRes["CardBackgroundFillColorSecondaryBrush"] = new SolidColorBrush(isLight
                    ? Color.FromArgb(42, 255, 255, 255)
                    : Color.FromArgb(55, tintedCardSurface.R, tintedCardSurface.G, tintedCardSurface.B));
                rootRes["CardStrokeColorDefaultBrush"] = new SolidColorBrush(isLight
                    ? Color.FromArgb(190, borderTint.R, borderTint.G, borderTint.B)
                    : Color.FromArgb(58, borderTint.R, borderTint.G, borderTint.B));
            }

            if (isLight)
            {
                if (!isCrystalClear && !isLiquidGlass)
                {
                    rootRes["DividerStrokeColorDefaultBrush"] = new SolidColorBrush(Color.FromArgb(30, 15, 23, 42));
                }
                rootRes["TextFillColorPrimaryBrush"] = new SolidColorBrush(Color.FromArgb(255, 15, 23, 42));
                rootRes["TextFillColorSecondaryBrush"] = new SolidColorBrush(Color.FromArgb(255, 51, 65, 85));
                rootRes["TextFillColorTertiaryBrush"] = new SolidColorBrush(Color.FromArgb(255, 71, 85, 105));
            }
        }
    }

    private static void ApplyCustomCornerRadiusToVisibleCards(DependencyObject root, CornerRadius targetRadius)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border border && border.BorderThickness.Left == 1 && border.CornerRadius.TopLeft is >= 8 and <= 24)
            {
                // 跳过胶囊与正圆头像
                if (Math.Abs(border.ActualHeight - border.CornerRadius.TopLeft * 2) > 2)
                {
                    border.CornerRadius = targetRadius;
                }
            }
            ApplyCustomCornerRadiusToVisibleCards(child, targetRadius);
        }
    }

    private void ApplyBottomPlayBarSectionTheme(CustomThemeProfile profile, Color accent, Color hover, Color pressed, bool isLight)
    {
        Color rawSlider = (profile.EnableCustomPlayBar && ThemeService.TryParseColor(profile.ProgressSliderColorHex, out var customSlider))
            ? customSlider
            : accent;
        Color sliderCol = ThemeService.EnsureReadableButtonFill(rawSlider, isLight);
        Color sliderHover = ThemeService.AdjustLightness(sliderCol, -0.06);
        Color sliderPressed = ThemeService.AdjustLightness(sliderCol, -0.12);

        ApplySliderAccentResources(SeekSlider, sliderCol, sliderHover, sliderPressed);
        ApplySliderAccentResources(VolumeSlider, sliderCol, sliderHover, sliderPressed);
        ApplySliderAccentResources(SimplePlayBarSeekSlider, sliderCol, sliderHover, sliderPressed);
        ApplySliderAccentResources(SimplePlayBarVolumeSlider, sliderCol, sliderHover, sliderPressed);

        bool isCrystalClear = IsCrystalClearMode(profile);
        bool isLiquidGlass = IsLiquidGlassMode(profile);

        if (profile.EnableCustomPlayBar)
        {
            var bgCol = ThemeService.ParseColorOrDefault(profile.PlayBarBgHex, Color.FromArgb(255, 17, 24, 39));
            var borderCol = ThemeService.ParseColorOrDefault(profile.PlayBarBorderHex, Color.FromArgb(255, 31, 41, 55));
            BottomPlayBarBorder.Background = isCrystalClear
                ? ThemeService.UpdateOrCreateSectionBrush(BottomPlayBarBorder.Background, Colors.Transparent, false, null, 0, 0)
                : isLiquidGlass
                    ? (BottomPlayBarBorder.Background is LiquidGlassCompositionBrush lg && lg.IsLight == isLight
                        ? lg
                        : ThemeService.CreateLiquidGlassSurfaceBrush(accent, isLight, intensity: 1.12))
                    : ThemeService.UpdateOrCreateSectionBrush(BottomPlayBarBorder.Background, bgCol, profile.EnablePlayBarGradient, profile.PlayBarGradientEndHex, profile.PlayBarOpacity, profile.GradientAngle, useAcrylicBlur: true);

            if (isLiquidGlass)
            {
                BottomPlayBarBorder.BorderBrush = ThemeService.CreateLiquidGlassBorderBrush(accent, intensity: 1.15);
            }
            else
            {
                var targetBdCol = isCrystalClear
                    ? Color.FromArgb(72, 148, 163, 184)
                    : Color.FromArgb(170, borderCol.R, borderCol.G, borderCol.B);
                BottomPlayBarBorder.BorderBrush = ThemeService.UpdateOrCreateSectionBrush(BottomPlayBarBorder.BorderBrush, targetBdCol, false, null, targetBdCol.A / 255.0 * 100.0, 0);
            }

            if (ThemeService.TryParseColor(profile.PlayBarTextHex, out var textCol))
            {
                var fg = new SolidColorBrush(textCol);
                BottomSongTitleText.Foreground = fg;
                PositionTimeText.Foreground = fg;
                DurationTimeText.Foreground = fg;
                PlayModeText.Foreground = fg;
            }
            else if (isLight)
            {
                var fg = new SolidColorBrush(Color.FromArgb(255, 15, 23, 42));
                BottomSongTitleText.Foreground = fg;
                PositionTimeText.Foreground = fg;
                DurationTimeText.Foreground = fg;
                PlayModeText.Foreground = fg;
            }
            else
            {
                ClearPlayBarCustomTextForeground();
            }
        }
        else
        {
            BottomPlayBarBorder.ClearValue(Border.BackgroundProperty);
            BottomPlayBarBorder.ClearValue(Border.BorderBrushProperty);
            if (isLight)
            {
                var fg = new SolidColorBrush(Color.FromArgb(255, 15, 23, 42));
                BottomSongTitleText.Foreground = fg;
                PositionTimeText.Foreground = fg;
                DurationTimeText.Foreground = fg;
                PlayModeText.Foreground = fg;
            }
            else
            {
                ClearPlayBarCustomTextForeground();
            }
        }
    }

    private void ClearPlayBarCustomTextForeground()
    {
        BottomSongTitleText.ClearValue(TextBlock.ForegroundProperty);
        PositionTimeText.ClearValue(TextBlock.ForegroundProperty);
        DurationTimeText.ClearValue(TextBlock.ForegroundProperty);
        PlayModeText.ClearValue(TextBlock.ForegroundProperty);
    }

    private static void ApplySliderAccentResources(Slider? slider, Color fill, Color hover, Color pressed)
    {
        if (slider == null) return;
        var r = slider.Resources;
        ThemeService.SetOrUpdateSolidBrush(r, "SliderTrackValueFill", fill);
        ThemeService.SetOrUpdateSolidBrush(r, "SliderTrackValueFillPointerOver", hover);
        ThemeService.SetOrUpdateSolidBrush(r, "SliderTrackValueFillPressed", pressed);
        ThemeService.SetOrUpdateSolidBrush(r, "SliderThumbBackground", fill);
        ThemeService.SetOrUpdateSolidBrush(r, "SliderThumbBackgroundPointerOver", hover);
        ThemeService.SetOrUpdateSolidBrush(r, "SliderThumbBackgroundPressed", pressed);
    }

    /// <summary>
    /// 全量接管 WinUI 3 原生系统控件强调色（开关/复选框/下拉框/滑块/输入框聚焦线）、全窗口 15 个弹窗/抽屉/右键菜单主题色磨砂背景，以及首页双英雄大卡与歌单详情页氛围渐变
    /// </summary>
    private void ApplySystemControlAndHeroThemeResources(CustomThemeProfile profile, Color accent, Color hover, Color pressed, bool isLight)
    {
        var rootRes = RootLayout.Resources;
        var fillCol = ThemeService.EnsureReadableButtonFill(accent, isLight);
        var fillHoverCol = ThemeService.AdjustLightness(fillCol, -0.07);
        var fillPressedCol = ThemeService.AdjustLightness(fillCol, -0.14);
        var readableTextCol = ThemeService.EnsureReadableTextAccent(accent, isLight);

        // 1. 原地更新 WinUI 3 原生控件系统强调色画刷（无需翻转 RequestedTheme 即可使全窗口滑块/开关/复选框实时变色）
        rootRes["SystemAccentColor"] = fillCol;
        ThemeService.SetOrUpdateSolidBrush(rootRes, "AccentFillColorDefaultBrush", fillCol);
        ThemeService.SetOrUpdateSolidBrush(rootRes, "AccentFillColorSecondaryBrush", Color.FromArgb(230, fillHoverCol.R, fillHoverCol.G, fillHoverCol.B));
        ThemeService.SetOrUpdateSolidBrush(rootRes, "AccentFillColorTertiaryBrush", Color.FromArgb(205, fillPressedCol.R, fillPressedCol.G, fillPressedCol.B));
        ThemeService.SetOrUpdateSolidBrush(rootRes, "AccentTextFillColorPrimaryBrush", readableTextCol);
        ThemeService.SetOrUpdateSolidBrush(rootRes, "AccentTextFillColorSecondaryBrush", readableTextCol);
        ThemeService.SetOrUpdateSolidBrush(rootRes, "AccentTextFillColorTertiaryBrush", ThemeService.AdjustLightness(readableTextCol, -0.06));
        ThemeService.SetOrUpdateSolidBrush(rootRes, "TextControlBorderBrushFocused", fillCol);

        ThemeService.SetOrUpdateSolidBrush(rootRes, "ToggleSwitchFillOn", fillCol);
        ThemeService.SetOrUpdateSolidBrush(rootRes, "ToggleSwitchFillOnPointerOver", fillHoverCol);
        ThemeService.SetOrUpdateSolidBrush(rootRes, "ToggleSwitchFillOnPressed", fillPressedCol);
        ThemeService.SetOrUpdateSolidBrush(rootRes, "ToggleSwitchStrokeOn", fillCol);
        ThemeService.SetOrUpdateSolidBrush(rootRes, "ToggleSwitchStrokeOnPointerOver", fillHoverCol);
        ThemeService.SetOrUpdateSolidBrush(rootRes, "ToggleSwitchStrokeOnPressed", fillPressedCol);

        ThemeService.SetOrUpdateSolidBrush(rootRes, "CheckBoxCheckBackgroundFillChecked", fillCol);
        ThemeService.SetOrUpdateSolidBrush(rootRes, "CheckBoxCheckBackgroundFillCheckedPointerOver", fillHoverCol);
        ThemeService.SetOrUpdateSolidBrush(rootRes, "CheckBoxCheckBackgroundFillCheckedPressed", fillPressedCol);
        ThemeService.SetOrUpdateSolidBrush(rootRes, "CheckBoxCheckBackgroundStrokeChecked", fillCol);

        ThemeService.SetOrUpdateSolidBrush(rootRes, "SliderTrackValueFill", fillCol);
        ThemeService.SetOrUpdateSolidBrush(rootRes, "SliderTrackValueFillPointerOver", fillHoverCol);
        ThemeService.SetOrUpdateSolidBrush(rootRes, "SliderTrackValueFillPressed", fillPressedCol);
        ThemeService.SetOrUpdateSolidBrush(rootRes, "SliderThumbBackground", fillCol);
        ThemeService.SetOrUpdateSolidBrush(rootRes, "SliderThumbBackgroundPointerOver", fillHoverCol);
        ThemeService.SetOrUpdateSolidBrush(rootRes, "SliderThumbBackgroundPressed", fillPressedCol);

        // 2. 为全窗口 15 个二级弹窗、右侧抽屉与右键菜单（FlyoutPresenter）注入融合当前主题色相的高级磨砂亚克力质感
        bool isCustomLayer = profile.BackdropType is "Image" or "Solid" or "Aurora" or "CoverFlow" or "Video";
        Color flyoutTint = ThemeService.CreateThemeTintedSurfaceColor(profile, accent, isLight, isLight ? 0.13 : 0.22);
        if (isCustomLayer || profile.EnableCustomContent)
        {
            Brush modalGlass = isLight
                ? ThemeService.CreateFrostedGlassBrush(flyoutTint, tintOpacity: 0.62, luminosityOpacity: 0.72, fallbackAlpha: 230)
                : ThemeService.CreateFrostedGlassBrush(flyoutTint, tintOpacity: 0.68, luminosityOpacity: 0.76, fallbackAlpha: 234);
            rootRes["SolidBackgroundFillColorBaseBrush"] = modalGlass;
            rootRes["SolidBackgroundFillColorBaseAltBrush"] = modalGlass;
            rootRes["AcrylicBackgroundFillColorDefaultBrush"] = modalGlass;
            rootRes["SurfaceStrokeColorFlyoutBrush"] = new SolidColorBrush(Color.FromArgb(isLight ? (byte)115 : (byte)90, accent.R, accent.G, accent.B));
        }
        else
        {
            rootRes.Remove("SolidBackgroundFillColorBaseBrush");
            rootRes.Remove("SolidBackgroundFillColorBaseAltBrush");
            rootRes.Remove("AcrylicBackgroundFillColorDefaultBrush");
            rootRes.Remove("SurfaceStrokeColorFlyoutBrush");
        }

        // 3. 联动首页双英雄大卡（左卡：每日推荐主色流光；右卡：私人 FM 主题副色/谐波色流光）与歌单详情页头部渐变层
        var (ah, aSat, _) = ThemeService.RgbToHsl(accent.R, accent.G, accent.B);
        var heroDarkBase = ThemeService.CreateThemeTintedSurfaceColor(profile, accent, isLight: false, tintWeight: 0.16);

        if (DailyHeroStop0 != null && DailyHeroStop1 != null)
        {
            var dailyStart = ThemeService.HslToColor(ah, Math.Clamp(aSat, 0.45, 0.82), 0.23);
            DailyHeroStop0.Color = Color.FromArgb(232, dailyStart.R, dailyStart.G, dailyStart.B);
            DailyHeroStop1.Color = Color.FromArgb(232, heroDarkBase.R, heroDarkBase.G, heroDarkBase.B);
        }
        if (DailyHeroSubtitleText != null)
        {
            DailyHeroSubtitleText.Foreground = new SolidColorBrush(ThemeService.HslToColor(ah, Math.Clamp(aSat, 0.45, 0.75), 0.82));
        }

        Color fmAccent;
        if (profile.EnableAccentGradient && ThemeService.TryParseColor(profile.AccentGradientEndHex, out var gradEnd))
        {
            fmAccent = gradEnd;
        }
        else if (profile.BackdropType == "Aurora" && ThemeService.TryParseColor(profile.AuroraColor2Hex, out var aur2))
        {
            var (h2, s2, _) = ThemeService.RgbToHsl(aur2.R, aur2.G, aur2.B);
            fmAccent = ThemeService.HslToColor(h2, Math.Clamp(s2, 0.55, 0.85), 0.56);
        }
        else
        {
            fmAccent = ThemeService.HslToColor((ah + 46.0) % 360.0, Math.Clamp(aSat, 0.55, 0.82), 0.58);
        }

        var (fh, fs, _) = ThemeService.RgbToHsl(fmAccent.R, fmAccent.G, fmAccent.B);
        if (FmHeroStop0 != null && FmHeroStop1 != null)
        {
            var fmStart = ThemeService.HslToColor(fh, Math.Clamp(fs, 0.45, 0.82), 0.22);
            FmHeroStop0.Color = Color.FromArgb(232, fmStart.R, fmStart.G, fmStart.B);
            FmHeroStop1.Color = Color.FromArgb(232, heroDarkBase.R, heroDarkBase.G, heroDarkBase.B);
        }
        if (FmHeroCardBorder != null)
        {
            FmHeroCardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(75, fmAccent.R, fmAccent.G, fmAccent.B));
        }
        var fmSoftLight = ThemeService.HslToColor(fh, Math.Clamp(fs, 0.55, 0.82), 0.82);
        var fmSoftBrush = new SolidColorBrush(fmSoftLight);
        if (FmHeroBadgeBorder != null) FmHeroBadgeBorder.Background = new SolidColorBrush(Color.FromArgb(52, fmAccent.R, fmAccent.G, fmAccent.B));
        if (FmHeroBadgeIcon != null) FmHeroBadgeIcon.Foreground = fmSoftBrush;
        if (FmHeroBadgeText != null) FmHeroBadgeText.Foreground = fmSoftBrush;
        if (FmHeroArtistName != null) FmHeroArtistName.Foreground = new SolidColorBrush(ThemeService.HslToColor(fh, Math.Clamp(fs, 0.45, 0.72), 0.86));
        if (FmPlayBtn != null)
        {
            var fmBtnCol = ThemeService.HslToColor(fh, Math.Clamp(fs, 0.58, 0.85), 0.52);
            FmPlayBtn.Background = new SolidColorBrush(fmBtnCol);
            FmPlayBtn.CornerRadius = new CornerRadius(Math.Clamp(profile.ButtonCornerRadius, 4, 22));
            FmPlayBtn.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(ThemeService.AdjustLightness(fmBtnCol, -0.07));
            FmPlayBtn.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(ThemeService.AdjustLightness(fmBtnCol, -0.14));
        }

        if (DetailHeroStop0 != null && DetailHeroStop1 != null)
        {
            DetailHeroStop0.Color = Color.FromArgb(42, accent.R, accent.G, accent.B);
            DetailHeroStop1.Color = Color.FromArgb(14, accent.R, accent.G, accent.B);
        }
    }

    private void RefreshRootElementTheme(ElementTheme targetTheme)
    {
        ElementTheme toggleTemp = targetTheme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
        RootLayout.RequestedTheme = toggleTemp;
        RootLayout.RequestedTheme = targetTheme;
    }

    // ==================== 入口一：设置页「个性主题」常规内嵌面板准备 ====================

    private void PrepareInlineSettingsThemeTab()
    {
        _themeDraft = ViewModel.Theme.GetEffectiveTheme().Clone();
        RebuildThemeLibraryCardsUi();
        LoadThemeDraftToEditorUi();
        if (_pageThemeSubMode == "market")
        {
            _ = LoadAndRenderMarketplaceCardsAsync();
        }
    }

    private void OnPageThemeSubModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string mode) return;
        _pageThemeSubMode = mode;
        bool isLocal = mode == "local";
        PageLocalLibraryCard.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
        PageMarketCard.Visibility = isLocal ? Visibility.Collapsed : Visibility.Visible;
        ApplyDynamicAccentButtonState(PageSubModeLocalBtn, isLocal);
        ApplyDynamicAccentButtonState(PageSubModeMarketBtn, !isLocal);
        if (!isLocal)
        {
            _ = LoadAndRenderMarketplaceCardsAsync();
        }
        ScheduleAdaptiveWallpaperTextPass(immediate: true);
    }

    // ==================== 入口二：标题栏独立主题中心全屏弹窗 ====================

    private void OnOpenThemeCenterModalClick(object sender, RoutedEventArgs e)
    {
        OpenThemeCenterModal();
    }

    private void OpenThemeCenterModal()
    {
        _themeDraft = ViewModel.Theme.GetEffectiveTheme().Clone();
        ThemeCenterModalOverlay.Visibility = Visibility.Visible;
        ThemeCenterModalCard.Opacity = 1.0;

        RebuildThemeLibraryCardsUi();
        LoadThemeDraftToEditorUi();
        SwitchThemeDiyTabUi(_activeThemeDiyTab);
        ScheduleUpdateTitleBarPassthroughRegions();
        PlayEntranceAnimation(ThemeCenterModalCard, 0, 18, 240);

        if (_modalMainMode == "market")
        {
            _ = LoadAndRenderMarketplaceCardsAsync();
        }
    }

    private void OnModalMainModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string mode) return;
        _modalMainMode = mode;
        bool isWorkshop = mode == "workshop";
        ThemeModalWorkshopBodyGrid.Visibility = isWorkshop ? Visibility.Visible : Visibility.Collapsed;
        ThemeModalMarketBodyGrid.Visibility = isWorkshop ? Visibility.Collapsed : Visibility.Visible;
        ApplyDynamicAccentButtonState(ModalModeWorkshopBtn, isWorkshop);
        ApplyDynamicAccentButtonState(ModalModeMarketBtn, !isWorkshop);
        if (!isWorkshop)
        {
            _ = LoadAndRenderMarketplaceCardsAsync();
        }
    }

    private void OnCloseThemeCenterModalClick(object sender, RoutedEventArgs e)
    {
        CloseThemeCenterModal();
    }

    private void CloseThemeCenterModal()
    {
        SharedColorPickerFlyout.Hide();
        // 关闭弹窗时，若有未保存的草稿预览，恢复为已正式激活的主题
        if (ViewModel.Theme.PreviewProfile != null)
        {
            ViewModel.Theme.RevertPreview();
            _themeDraft = ViewModel.Theme.GetSavedActiveTheme().Clone();
            if (SettingsThemePanel.Visibility == Visibility.Visible)
            {
                LoadThemeDraftToEditorUi();
            }
        }
        ThemeCenterModalOverlay.Visibility = Visibility.Collapsed;
        ScheduleUpdateTitleBarPassthroughRegions();
    }

    private void OnThemeCenterModalBackdropTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, ThemeCenterModalOverlay))
        {
            CloseThemeCenterModal();
        }
    }

    private void OnThemeCenterModalCardTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnThemePeekButtonPressed(object sender, PointerRoutedEventArgs e)
    {
        ThemeCenterModalCard.Opacity = 0.06;
        ThemeCenterModalOverlay.Background = new SolidColorBrush(Colors.Transparent);
    }

    private void OnThemePeekButtonReleased(object sender, PointerRoutedEventArgs e)
    {
        ThemeCenterModalCard.Opacity = 1.0;
        ThemeCenterModalOverlay.Background = new SolidColorBrush(Color.FromArgb(0x78, 0, 0, 0));
    }

    // ==================== 预设与自制主题库卡片构建（无泛白） ====================

    private void RebuildThemeLibraryCardsUi()
    {
        string activeId = ViewModel.Config.Settings.ThemeCenter.ActiveThemeId;
        var presets = CustomThemeProfile.GetBuiltInPresets();
        var customs = ViewModel.Config.Settings.ThemeCenter.CustomThemes;

        // 1. 填充标题栏主题中心弹窗左侧单列列表
        BuiltInThemesContainer.Children.Clear();
        CustomThemesContainer.Children.Clear();

        foreach (var preset in presets)
        {
            bool isActive = string.Equals(preset.Id, activeId, StringComparison.OrdinalIgnoreCase);
            BuiltInThemesContainer.Children.Add(CreateThemeLibraryItemCard(preset, isActive));
        }

        CustomThemesCountText.Text = $"{customs.Count} 套";
        CustomThemesEmptyHint.Visibility = customs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var custom in customs)
        {
            bool isActive = string.Equals(custom.Id, activeId, StringComparison.OrdinalIgnoreCase);
            CustomThemesContainer.Children.Add(CreateThemeLibraryItemCard(custom, isActive));
        }

        // 2. 填充设置页内嵌「个性主题」面板三列自适应网格
        PopulateThreeColumnThemeGrid(PageBuiltInThemesGrid, presets, activeId);
        PopulateThreeColumnThemeGrid(PageCustomThemesGrid, customs, activeId);
        PageCustomThemesCountText.Text = $"{customs.Count} 套";
        PageCustomThemesEmptyHint.Visibility = customs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageCustomThemesGrid.Visibility = customs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PopulateThreeColumnThemeGrid(Grid targetGrid, IReadOnlyList<CustomThemeProfile> items, string activeId)
    {
        targetGrid.Children.Clear();
        targetGrid.RowDefinitions.Clear();
        targetGrid.ColumnDefinitions.Clear();

        if (items.Count == 0) return;

        const int columns = 3;
        for (int c = 0; c < columns; c++)
        {
            targetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        int rowCount = (items.Count + columns - 1) / columns;
        for (int r = 0; r < rowCount; r++)
        {
            targetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            bool isActive = string.Equals(item.Id, activeId, StringComparison.OrdinalIgnoreCase);
            var card = CreateThemeLibraryItemCard(item, isActive);
            Grid.SetRow((FrameworkElement)card, i / columns);
            Grid.SetColumn((FrameworkElement)card, i % columns);
            targetGrid.Children.Add(card);
        }
    }

    private static LinearGradientBrush CreateInnerLiquidCrystalBrush(Color accent, bool isLight, double intensity = 1.0)
    {
        double k = Math.Clamp(intensity, 0.5, 1.6);
        Color accentSheen = ThemeService.LerpColor(Colors.White, accent, isLight ? 0.22 : 0.35);
        byte aTop = (byte)Math.Clamp(Math.Round((isLight ? 72 : 42) * k), 12, 130);
        byte aMid = (byte)Math.Clamp(Math.Round((isLight ? 28 : 14) * k), 4, 64);
        byte aBot = (byte)Math.Clamp(Math.Round((isLight ? 46 : 30) * k), 10, 96);

        var lgb = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1)
        };
        lgb.GradientStops.Add(new GradientStop { Color = Color.FromArgb(aTop, 255, 255, 255), Offset = 0.0 });
        lgb.GradientStops.Add(new GradientStop { Color = Color.FromArgb(aMid, 248, 252, 255), Offset = 0.52 });
        lgb.GradientStops.Add(new GradientStop { Color = Color.FromArgb(aBot, accentSheen.R, accentSheen.G, accentSheen.B), Offset = 1.0 });
        return lgb;
    }

    private UIElement CreateThemeLibraryItemCard(CustomThemeProfile item, bool isActive)
    {
        bool isUiLight = IsCurrentUiLightMode;
        var accentCol = ThemeService.ParseColorOrDefault(item.AccentColorHex, Color.FromArgb(255, 16, 185, 129));
        var readableTagTextCol = ThemeService.EnsureReadableTextAccent(accentCol, isUiLight);
        var readableBadgeFillCol = ThemeService.EnsureReadableButtonFill(accentCol, true);
        bool isItemLight = string.Equals(item.BaseMode, "Light", StringComparison.OrdinalIgnoreCase);
        var itemWinBg = ThemeService.ParseColorOrDefault(item.WindowBgColorHex,
            isItemLight ? Color.FromArgb(255, 248, 250, 252) : Color.FromArgb(255, 18, 22, 31));
        var itemNavBg = item.EnableCustomNavPane
            ? ThemeService.ParseColorOrDefault(item.NavPaneBgHex, itemWinBg)
            : (isItemLight ? Color.FromArgb(255, 236, 241, 247) : Color.FromArgb(255, 26, 33, 46));

        var effectiveProf = ViewModel.Theme.GetEffectiveTheme();
        bool isLiquid = IsLiquidGlassMode(effectiveProf);
        bool isCrystal = IsCrystalClearMode(effectiveProf);

        Brush restBgBrush;
        Brush hoverBgBrush;
        Brush restBorderBrush;
        if (isLiquid)
        {
            // 父容器已具备实时液态玻璃折射，子项采用清透对角冰晶高光渐变釉面，避免 60+ 嵌套 Backdrop 重复模糊叠加
            restBgBrush = CreateInnerLiquidCrystalBrush(accentCol, isUiLight, isActive ? 1.35 : 0.85);
            hoverBgBrush = CreateInnerLiquidCrystalBrush(accentCol, isUiLight, 1.45);
            restBorderBrush = isActive
                ? new SolidColorBrush(readableBadgeFillCol)
                : ThemeService.CreateLiquidGlassBorderBrush(accentCol, intensity: 0.95);
        }
        else
        {
            Color normalBg = isCrystal
                ? Colors.Transparent
                : (isUiLight ? Color.FromArgb(62, 255, 255, 255) : Color.FromArgb(18, 255, 255, 255));
            Color hoverBg = isCrystal
                ? Color.FromArgb(22, accentCol.R, accentCol.G, accentCol.B)
                : (isUiLight ? Color.FromArgb(44, accentCol.R, accentCol.G, accentCol.B) : Color.FromArgb(34, 255, 255, 255));
            Color activeBg = isCrystal
                ? Color.FromArgb(28, accentCol.R, accentCol.G, accentCol.B)
                : (isUiLight ? Color.FromArgb(52, accentCol.R, accentCol.G, accentCol.B) : Color.FromArgb(38, accentCol.R, accentCol.G, accentCol.B));
            Color normalBorder = isCrystal
                ? Color.FromArgb(72, 148, 163, 184)
                : (isUiLight ? Color.FromArgb(195, 255, 255, 255) : Color.FromArgb(38, 255, 255, 255));

            restBgBrush = new SolidColorBrush(isActive ? activeBg : normalBg);
            hoverBgBrush = new SolidColorBrush(hoverBg);
            restBorderBrush = new SolidColorBrush(isActive ? readableBadgeFillCol : normalBorder);
        }
        Color hoverBorder = Color.FromArgb(195, accentCol.R, accentCol.G, accentCol.B);

        // 注意：使用纯中性黑白灰，避免冷色 Slate 在低明度下产生伪色相，确保壁纸自适应引擎 100% 识别为纯黑白语义
        Color titleColor = isUiLight
            ? Color.FromArgb(255, 18, 22, 28)
            : Color.FromArgb(255, 250, 250, 252);
        Color descColor = isUiLight
            ? Color.FromArgb(255, 64, 70, 80)
            : Color.FromArgb(255, 175, 182, 192);

        var outerBorder = new Border
        {
            CornerRadius = new CornerRadius(isLiquid ? 14 : 10),
            Padding = new Thickness(12, 10, 12, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = restBgBrush,
            BorderBrush = restBorderBrush,
            BorderThickness = new Thickness(isActive ? 1.5 : 1)
        };

        var rootGrid = new Grid { ColumnSpacing = 10 };
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 左侧精致微缩色卡块
        var swatchBox = new Border
        {
            Width = 44,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(itemWinBg),
            BorderBrush = new SolidColorBrush(isItemLight
                ? Color.FromArgb(180, 203, 213, 225)
                : Color.FromArgb(75, 148, 163, 184)),
            BorderThickness = new Thickness(1)
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(swatchBox, SkipAdaptiveTextTag);

        var swatchInnerGrid = new Grid();
        swatchInnerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        swatchInnerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var swatchPaneStrip = new Border
        {
            Background = new SolidColorBrush(itemNavBg),
            CornerRadius = new CornerRadius(7, 0, 0, 7)
        };
        Grid.SetColumn(swatchPaneStrip, 0);
        swatchInnerGrid.Children.Add(swatchPaneStrip);

        var accentDot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 14,
            Height = 14,
            Fill = new SolidColorBrush(accentCol),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(accentDot, 1);
        swatchInnerGrid.Children.Add(accentDot);

        swatchBox.Child = swatchInnerGrid;
        Grid.SetColumn(swatchBox, 0);
        rootGrid.Children.Add(swatchBox);

        // 中部标题、简介与动态特性徽章
        var infoStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        titleRow.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(titleColor),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        if (item.HasDynamicEffect)
        {
            titleRow.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x28, accentCol.R, accentCol.G, accentCol.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, readableTagTextCol.R, readableTagTextCol.G, readableTagTextCol.B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 1, 4, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = item.BackdropType is "Aurora" or "CoverFlow" ? "极光" : (item.EnableParticleOverlay ? "粒子" : "动态"),
                    FontSize = 9,
                    Foreground = new SolidColorBrush(readableTagTextCol),
                    FontWeight = FontWeights.SemiBold
                }
            });
        }

        if (isActive)
        {
            var activeTag = new Border
            {
                Background = new SolidColorBrush(readableBadgeFillCol),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "使用中",
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Colors.White),
                    FontWeight = FontWeights.Bold
                }
            };
            titleRow.Children.Add(activeTag);
        }
        infoStack.Children.Add(titleRow);
        infoStack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.Description) ? $"{item.BaseMode} · {item.BackdropType}" : item.Description,
            FontSize = 11,
            Foreground = new SolidColorBrush(descColor),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(infoStack, 1);
        rootGrid.Children.Add(infoStack);

        // 右侧操作区（自制主题提供删除按钮）
        if (!item.IsBuiltIn)
        {
            var delBtn = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = item.Id,
                Content = new FontIcon
                {
                    Glyph = "\uE74D",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 239, 68, 68))
                }
            };
            ToolTipService.SetToolTip(delBtn, "删除此自制主题");
            delBtn.Tapped += (_, te) => te.Handled = true;
            delBtn.Click += OnDeleteCustomThemeCardClick;
            Grid.SetColumn(delBtn, 2);
            rootGrid.Children.Add(delBtn);
        }

        outerBorder.PointerEntered += (_, _) =>
        {
            if (!isActive)
            {
                outerBorder.Background = hoverBgBrush;
                outerBorder.BorderBrush = new SolidColorBrush(hoverBorder);
            }
        };
        outerBorder.PointerExited += (_, _) =>
        {
            outerBorder.Background = restBgBrush;
            outerBorder.BorderBrush = restBorderBrush;
        };
        outerBorder.Tapped += async (_, te) =>
        {
            te.Handled = true;
            await SelectAndApplyThemeCardAsync(item);
        };

        outerBorder.Child = rootGrid;
        return outerBorder;
    }

    private async Task SelectAndApplyThemeCardAsync(CustomThemeProfile selected)
    {
        await ViewModel.Theme.ApplyThemeAsync(selected, saveToDisk: true);
        _themeDraft = selected.Clone();
        RebuildThemeLibraryCardsUi();
        LoadThemeDraftToEditorUi();
        ViewModel.StatusToast = $"已应用主题：{selected.Name}";
    }

    private async void OnDeleteCustomThemeCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        bool ok = await ViewModel.Theme.DeleteCustomThemeAsync(id);
        if (ok)
        {
            _themeDraft = ViewModel.Theme.GetSavedActiveTheme().Clone();
            RebuildThemeLibraryCardsUi();
            LoadThemeDraftToEditorUi();
            ViewModel.StatusToast = "已删除自制主题";
        }
    }

    // ==================== 主题市场加载与卡片渲染 ====================

    private async Task LoadAndRenderMarketplaceCardsAsync(bool forceRefresh = false)
    {
        try
        {
            _marketItems = await ViewModel.Theme.GetMarketplaceItemsAsync(forceRefresh);
            RenderMarketplaceCardsUi();
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"加载主题市场失败：{ex.Message}";
        }
    }

    private void RenderMarketplaceCardsUi()
    {
        var filtered = _marketItems.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(_selectedMarketCategory))
        {
            filtered = filtered.Where(x => string.Equals(x.Category, _selectedMarketCategory, StringComparison.OrdinalIgnoreCase)
                                        || (x.Tags != null && x.Tags.Any(t => string.Equals(t, _selectedMarketCategory, StringComparison.OrdinalIgnoreCase))));
        }
        if (!string.IsNullOrWhiteSpace(_marketSearchQuery))
        {
            string q = _marketSearchQuery.Trim();
            filtered = filtered.Where(x => (x.Theme?.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                                        || (x.Theme?.Author?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                                        || (x.Theme?.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                                        || (x.Category?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                                        || (x.Tags != null && x.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase))));
        }

        var list = filtered.ToList();
        string countText = $"共 {list.Count} 款精选主题";
        if (PageMarketCountText != null) PageMarketCountText.Text = countText;
        if (ModalMarketCountText != null) ModalMarketCountText.Text = countText;

        if (PageMarketCardsGrid != null) PopulateMarketplaceGrid(PageMarketCardsGrid, list);
        if (ModalMarketCardsGrid != null) PopulateMarketplaceGrid(ModalMarketCardsGrid, list);

        UpdateMarketCategoryFilterButtonsUi();
        UpdateMarketFeedsStatusUi();
        ScheduleAdaptiveWallpaperTextPass(immediate: true);
    }

    private void PopulateMarketplaceGrid(Grid targetGrid, IReadOnlyList<MarketplaceThemeItem> items)
    {
        targetGrid.Children.Clear();
        targetGrid.RowDefinitions.Clear();
        targetGrid.ColumnDefinitions.Clear();

        if (items.Count == 0) return;

        const int columns = 3;
        for (int c = 0; c < columns; c++)
        {
            targetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        int rowCount = (items.Count + columns - 1) / columns;
        for (int r = 0; r < rowCount; r++)
        {
            targetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var card = CreateMarketplaceThemeCard(item);
            Grid.SetRow((FrameworkElement)card, i / columns);
            Grid.SetColumn((FrameworkElement)card, i % columns);
            targetGrid.Children.Add(card);
        }
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject? element)
    {
        var cur = element;
        while (cur != null)
        {
            if (cur is ScrollViewer sv) return sv;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return null;
    }

    /// <summary>
    /// 原地无损刷新已渲染的主题市场卡片明暗反色与已安装徽章状态（零 DOM 销毁重建，防止按钮焦点丢失引起 ScrollViewer 跳至底部）
    /// </summary>
    private void RefreshMarketplaceCardsInPlace()
    {
        static void SyncGrid(Grid? grid)
        {
            if (grid == null) return;
            foreach (var child in grid.Children)
            {
                if (child is Border { Tag: Action syncAction })
                {
                    syncAction();
                }
            }
        }

        SyncGrid(PageMarketCardsGrid);
        SyncGrid(ModalMarketCardsGrid);
        UpdateMarketCategoryFilterButtonsUi();
    }

    private UIElement CreateMarketplaceThemeCard(MarketplaceThemeItem item)
    {
        var t = item.Theme;
        var accentCol = ThemeService.ParseColorOrDefault(t.AccentColorHex, Color.FromArgb(255, 16, 185, 129));
        bool isThemeLight = string.Equals(t.BaseMode, "Light", StringComparison.OrdinalIgnoreCase);

        Brush currentBorderBrush = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255));
        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12)
        };

        var mainStack = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };

        void PreviewThisMarketItem()
        {
            var settingsSv = FindAncestorScrollViewer(SettingsThemePanel);
            double? savedOffset = (SettingsThemePanel.Visibility == Visibility.Visible && settingsSv != null)
                ? settingsSv.VerticalOffset
                : null;

            _themeDraft = item.Theme.Clone();
            ViewModel.Theme.NotifyPreviewUpdated(_themeDraft);
            LoadThemeDraftToEditorUi();
            ViewModel.StatusToast = $"正在预览试用市场主题：{t.Name}";

            if (savedOffset.HasValue && settingsSv != null)
            {
                settingsSv.ChangeView(null, savedOffset.Value, null, disableAnimation: true);
            }
        }

        // 1. 顶部 1:1 高拟真「微缩播放器实景预览图」（兼容远程 PreviewImageUrl 真实截图叠加，点击直接试穿）
        var previewThumbnail = BuildThemeLiveMockupPreview(item, accentCol, isThemeLight);
        previewThumbnail.Tapped += (_, _) => PreviewThisMarketItem();
        ToolTipService.SetToolTip(previewThumbnail, "点击预览图即可在主窗口实时试穿该主题效果");
        mainStack.Children.Add(previewThumbnail);

        // 2. 主题标题、作者与简介
        var infoStack = new StackPanel { Spacing = 3 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var titleBlock = new TextBlock
        {
            Text = t.Name,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        titleRow.Children.Add(titleBlock);

        var installedBadgeText = new TextBlock { Text = "已装", FontSize = 9, FontWeight = FontWeights.Bold };
        var installedBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x2E, accentCol.R, accentCol.G, accentCol.B)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = installedBadgeText
        };
        titleRow.Children.Add(installedBadge);
        infoStack.Children.Add(titleRow);

        var authorBlock = new TextBlock
        {
            Text = $"By {t.Author} · {item.SourceName}",
            FontSize = 11
        };
        infoStack.Children.Add(authorBlock);

        var descBlock = new TextBlock
        {
            Text = t.Description,
            FontSize = 12,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Height = 32
        };
        infoStack.Children.Add(descBlock);
        mainStack.Children.Add(infoStack);

        // 3. 底部操作按钮：预览试用 & 安装应用
        var actionsRow = new Grid { ColumnSpacing = 8 };
        actionsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });

        var previewBtn = new Button
        {
            Content = "预览试用",
            Style = (Style)Application.Current.Resources["SecondaryTabButtonStyle"],
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5, 8, 5),
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            IsTabStop = false,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        previewBtn.Click += (_, _) => PreviewThisMarketItem();
        Grid.SetColumn(previewBtn, 0);
        actionsRow.Children.Add(previewBtn);

        var installBtn = new Button
        {
            Style = (Style)Application.Current.Resources["BrandPrimaryButtonStyle"],
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5, 8, 5),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            IsTabStop = false,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        installBtn.Click += async (_, _) =>
        {
            var settingsSv = FindAncestorScrollViewer(SettingsThemePanel);
            double? savedOffset = (SettingsThemePanel.Visibility == Visibility.Visible && settingsSv != null)
                ? settingsSv.VerticalOffset
                : null;

            var saved = await ViewModel.Theme.InstallMarketplaceThemeAsync(item);
            _themeDraft = saved.Clone();
            RebuildThemeLibraryCardsUi();
            LoadThemeDraftToEditorUi();
            RefreshMarketplaceCardsInPlace();
            ViewModel.StatusToast = $"已成功安装并应用：{saved.Name}";

            if (savedOffset.HasValue && settingsSv != null)
            {
                settingsSv.ChangeView(null, savedOffset.Value, null, disableAnimation: true);
            }
        };
        Grid.SetColumn(installBtn, 1);
        actionsRow.Children.Add(installBtn);

        void SyncCardVisualState()
        {
            var effProf = ViewModel.Theme.GetEffectiveTheme();
            bool curUiLight = IsCurrentUiLightMode;
            bool isLiquid = IsLiquidGlassMode(effProf);
            bool isCrystal = IsCrystalClearMode(effProf);
            bool curInstalled = ViewModel.Theme.IsMarketplaceThemeInstalled(item.Id);
            var readableTagCol = ThemeService.EnsureReadableTextAccent(accentCol, curUiLight);

            if (isLiquid)
            {
                card.Background = ThemeService.CreateLiquidGlassSurfaceBrush(accentCol, curUiLight, intensity: 1.12);
                currentBorderBrush = ThemeService.CreateLiquidGlassBorderBrush(accentCol, intensity: 1.05);
                card.BorderBrush = currentBorderBrush;
            }
            else
            {
                Color cardBg = isCrystal
                    ? Colors.Transparent
                    : (curUiLight ? Color.FromArgb(72, 255, 255, 255) : Color.FromArgb(20, 255, 255, 255));
                Color borderCol = isCrystal
                    ? Color.FromArgb(76, 148, 163, 184)
                    : (curUiLight ? Color.FromArgb(200, 255, 255, 255) : Color.FromArgb(38, 255, 255, 255));
                currentBorderBrush = new SolidColorBrush(borderCol);
                card.Background = new SolidColorBrush(cardBg);
                card.BorderBrush = currentBorderBrush;
            }

            Color titleCol = curUiLight ? Color.FromArgb(255, 18, 22, 28) : Color.FromArgb(255, 250, 250, 252);
            Color subCol = curUiLight ? Color.FromArgb(255, 64, 70, 80) : Color.FromArgb(255, 175, 182, 192);
            Color descCol = curUiLight ? Color.FromArgb(255, 42, 48, 56) : Color.FromArgb(255, 212, 218, 226);

            titleBlock.Foreground = new SolidColorBrush(titleCol);
            authorBlock.Foreground = new SolidColorBrush(subCol);
            descBlock.Foreground = new SolidColorBrush(descCol);

            installedBadge.Visibility = curInstalled ? Visibility.Visible : Visibility.Collapsed;
            installedBadgeText.Foreground = new SolidColorBrush(readableTagCol);
            installBtn.Content = curInstalled ? "更新并应用" : "安装并应用";

            if (isLiquid)
            {
                previewBtn.ClearValue(Control.ForegroundProperty);
                previewBtn.Background = ThemeService.CreateLiquidGlassSurfaceBrush(accentCol, curUiLight, intensity: 0.95);
                previewBtn.BorderBrush = ThemeService.CreateLiquidGlassBorderBrush(accentCol, intensity: 0.9);
            }
            else if (isCrystal)
            {
                previewBtn.ClearValue(Control.ForegroundProperty);
                previewBtn.Background = new SolidColorBrush(Colors.Transparent);
                previewBtn.BorderBrush = new SolidColorBrush(Color.FromArgb(88, 148, 163, 184));
            }
            else if (curUiLight)
            {
                previewBtn.Foreground = new SolidColorBrush(Color.FromArgb(255, 15, 23, 42));
                previewBtn.Background = new SolidColorBrush(Color.FromArgb(145, 255, 255, 255));
                previewBtn.BorderBrush = new SolidColorBrush(Color.FromArgb(48, 15, 23, 42));
            }
            else
            {
                previewBtn.ClearValue(Control.ForegroundProperty);
                previewBtn.ClearValue(Control.BackgroundProperty);
                previewBtn.ClearValue(Control.BorderBrushProperty);
            }
        }

        SyncCardVisualState();
        card.Tag = (Action)SyncCardVisualState;

        mainStack.Children.Add(actionsRow);
        card.Child = mainStack;

        card.PointerEntered += (_, _) =>
        {
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(195, accentCol.R, accentCol.G, accentCol.B));
        };
        card.PointerExited += (_, _) =>
        {
            card.BorderBrush = currentBorderBrush;
        };

        return card;
    }

    /// <summary>
    /// 根据主题真实参数 1:1 自动渲染微缩桌面播放器实景预览图（含三色流体极光、散点粒子、毛玻璃侧栏、歌单卡片、灵动岛/贴底栏与黑胶唱盘，同时兼容网络 PreviewImageUrl）
    /// </summary>
    private static Border BuildThemeLiveMockupPreview(MarketplaceThemeItem item, Color accentCol, bool isThemeLight)
    {
        var t = item.Theme;
        var winBg = ThemeService.ParseColorOrDefault(t.WindowBgColorHex,
            isThemeLight ? Color.FromArgb(255, 246, 249, 253) : Color.FromArgb(255, 11, 15, 25));
        var accentEnd = t.EnableAccentGradient && ThemeService.TryParseColor(t.AccentGradientEndHex, out var parsedEnd)
            ? parsedEnd
            : ThemeService.AdjustLightness(accentCol, isThemeLight ? 0.16 : -0.14);

        var accentGradBrush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = accentCol, Offset = 0.0 },
                new GradientStop { Color = accentEnd, Offset = 1.0 }
            }
        };

        var outerFrame = new Border
        {
            Height = 136,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(winBg),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(isThemeLight
                ? Color.FromArgb(190, 226, 232, 240)
                : Color.FromArgb(65, 255, 255, 255))
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(outerFrame, SkipAdaptiveTextTag);

        var sceneRoot = new Grid { CornerRadius = new CornerRadius(8) };

        // ==================== 第 1 层：三色流体极光 / 封面流光径向晕染 ====================
        if (t.BackdropType is "Aurora" or "CoverFlow")
        {
            Color c1 = t.BackdropType == "Aurora"
                ? ThemeService.ParseColorOrDefault(t.AuroraColor1Hex, accentCol)
                : accentCol;
            Color c2 = t.BackdropType == "Aurora"
                ? ThemeService.ParseColorOrDefault(t.AuroraColor2Hex, accentEnd)
                : ThemeService.HslToColor((ThemeService.RgbToHsl(accentCol.R, accentCol.G, accentCol.B).H + 55) % 360, 0.78, isThemeLight ? 0.80 : 0.48);
            Color c3 = t.BackdropType == "Aurora"
                ? ThemeService.ParseColorOrDefault(t.AuroraColor3Hex, Color.FromArgb(255, 99, 102, 241))
                : ThemeService.HslToColor((ThemeService.RgbToHsl(accentCol.R, accentCol.G, accentCol.B).H + 135) % 360, 0.72, isThemeLight ? 0.84 : 0.44);

            byte centerA = isThemeLight ? (byte)165 : (byte)145;
            byte midA = isThemeLight ? (byte)75 : (byte)64;

            Microsoft.UI.Xaml.Shapes.Ellipse MakeOrb(Color col, double size, HorizontalAlignment ha, VerticalAlignment va, Thickness margin)
            {
                var rb = new RadialGradientBrush
                {
                    Center = new Windows.Foundation.Point(0.5, 0.5),
                    GradientOrigin = new Windows.Foundation.Point(0.5, 0.5),
                    RadiusX = 0.5,
                    RadiusY = 0.5
                };
                rb.GradientStops.Add(new GradientStop { Color = Color.FromArgb(centerA, col.R, col.G, col.B), Offset = 0.0 });
                rb.GradientStops.Add(new GradientStop { Color = Color.FromArgb(midA, col.R, col.G, col.B), Offset = 0.52 });
                rb.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, col.R, col.G, col.B), Offset = 1.0 });

                return new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = size,
                    Height = size,
                    Fill = rb,
                    HorizontalAlignment = ha,
                    VerticalAlignment = va,
                    Margin = margin,
                    IsHitTestVisible = false
                };
            }

            sceneRoot.Children.Add(MakeOrb(c1, 165, HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(-32, -36, 0, 0)));
            sceneRoot.Children.Add(MakeOrb(c2, 155, HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0, 0, -28, -34)));
            sceneRoot.Children.Add(MakeOrb(c3, 135, HorizontalAlignment.Center, VerticalAlignment.Center, new Thickness(24, 12, 0, 0)));
        }

        // ==================== 第 2 层：确定性氛围粒子装饰层（落樱/萤火/微雪/音泡/繁星） ====================
        if (t.EnableParticleOverlay)
        {
            var particleCanvas = new Canvas { IsHitTestVisible = false };
            string pType = string.IsNullOrWhiteSpace(t.ParticleType) ? "Starlight" : t.ParticleType;
            Color pCol = ThemeService.TryParseColor(t.ParticleColorHex, out var customP)
                ? customP
                : pType switch
                {
                    "Sakura" => isThemeLight ? Color.FromArgb(255, 244, 114, 182) : Color.FromArgb(255, 253, 164, 175),
                    "Firefly" => Color.FromArgb(255, 253, 224, 71),
                    "Snow" => isThemeLight ? Color.FromArgb(255, 148, 163, 184) : Color.FromArgb(255, 241, 245, 249),
                    _ => accentCol
                };

            int seed = Math.Abs((item.Id ?? t.Name).GetHashCode());
            var rng = new Random(seed);
            for (int p = 0; p < 12; p++)
            {
                double px = 14 + rng.NextDouble() * 235;
                double py = 8 + rng.NextDouble() * 112;
                FrameworkElement dot;

                if (pType == "Sakura")
                {
                    dot = new Border
                    {
                        Width = 6.5,
                        Height = 4.2,
                        CornerRadius = new CornerRadius(4, 1, 4, 1),
                        Background = new SolidColorBrush(Color.FromArgb(195, pCol.R, pCol.G, pCol.B)),
                        RenderTransform = new RotateTransform { Angle = rng.Next(0, 360) }
                    };
                }
                else if (pType == "Bubble")
                {
                    double bSize = 5 + rng.NextDouble() * 5.5;
                    dot = new Border
                    {
                        Width = bSize,
                        Height = bSize,
                        CornerRadius = new CornerRadius(bSize * 0.5),
                        Background = new SolidColorBrush(Color.FromArgb(45, pCol.R, pCol.G, pCol.B)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(175, pCol.R, pCol.G, pCol.B)),
                        BorderThickness = new Thickness(1)
                    };
                }
                else
                {
                    double sSize = pType == "Firefly" ? 4.2 : 3.0;
                    dot = new Microsoft.UI.Xaml.Shapes.Ellipse
                    {
                        Width = sSize,
                        Height = sSize,
                        Fill = new SolidColorBrush(Color.FromArgb(210, pCol.R, pCol.G, pCol.B))
                    };
                }

                Canvas.SetLeft(dot, px);
                Canvas.SetTop(dot, py);
                particleCanvas.Children.Add(dot);
            }
            sceneRoot.Children.Add(particleCanvas);
        }

        // ==================== 第 3 层：1:1 微缩播放器实景骨架（侧栏 + 顶栏 + 歌单卡片 + 灵动岛/贴底栏） ====================
        var mockupShell = new Grid { Padding = new Thickness(10, 24, 10, 6) };
        var appWindowBox = new Border
        {
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(isThemeLight
                ? Color.FromArgb(185, 255, 255, 255)
                : Color.FromArgb(52, 255, 255, 255)),
            Background = new SolidColorBrush(isThemeLight
                ? Color.FromArgb(32, 255, 255, 255)
                : Color.FromArgb(38, 15, 23, 42))
        };

        var winGrid = new Grid();
        winGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(13) });
        winGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        winGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(21) });

        Color fgLine = isThemeLight ? Color.FromArgb(165, 30, 41, 59) : Color.FromArgb(185, 241, 245, 249);
        Color subLine = isThemeLight ? Color.FromArgb(85, 71, 85, 105) : Color.FromArgb(90, 148, 163, 184);

        // 3.1 微缩顶栏（品牌点 + 居中胶囊搜索条）
        var miniTopBar = new Grid { Padding = new Thickness(6, 0, 6, 0) };
        miniTopBar.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = accentGradBrush },
                new Border { Width = 22, Height = 3, CornerRadius = new CornerRadius(1.5), Background = new SolidColorBrush(fgLine) }
            }
        });
        miniTopBar.Children.Add(new Border
        {
            Width = 64,
            Height = 7,
            CornerRadius = new CornerRadius(3.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(isThemeLight
                ? Color.FromArgb(115, 255, 255, 255)
                : Color.FromArgb(85, 30, 41, 59)),
            BorderThickness = new Thickness(0.6),
            BorderBrush = new SolidColorBrush(isThemeLight
                ? Color.FromArgb(170, 255, 255, 255)
                : Color.FromArgb(45, 255, 255, 255))
        });
        Grid.SetRow(miniTopBar, 0);
        winGrid.Children.Add(miniTopBar);

        // 3.2 微缩中部（左侧毛玻璃导航栏 + 右侧主内容歌单卡片）
        var bodyGrid = new Grid { Padding = new Thickness(4, 1, 5, 2), ColumnSpacing = 6 };
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Color paneBg = t.EnableCustomNavPane && ThemeService.TryParseColor(t.NavPaneBgHex, out var customNav)
            ? Color.FromArgb((byte)(isThemeLight ? 120 : 175), customNav.R, customNav.G, customNav.B)
            : (isThemeLight ? Color.FromArgb(105, 255, 255, 255) : Color.FromArgb(110, 15, 23, 42));

        var navPane = new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(paneBg),
            Padding = new Thickness(4, 4, 4, 4)
        };
        var navStack = new StackPanel { Spacing = 3.5 };
        navStack.Children.Add(new Border
        {
            Height = 7,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromArgb(65, accentCol.R, accentCol.G, accentCol.B)),
            Child = new Border
            {
                Width = 2,
                Height = 4,
                CornerRadius = new CornerRadius(1),
                Background = new SolidColorBrush(accentCol),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(1.5, 0, 0, 0)
            }
        });
        for (int n = 0; n < 3; n++)
        {
            navStack.Children.Add(new Border
            {
                Height = 3.5,
                Width = n == 2 ? 22 : 28,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(1.5),
                Margin = new Thickness(2, 0, 0, 0),
                Background = new SolidColorBrush(subLine)
            });
        }
        navPane.Child = navStack;
        Grid.SetColumn(navPane, 0);
        bodyGrid.Children.Add(navPane);

        // 右侧主工作区：顶部渐变横幅 + 3 张卡片（体现 FrostedGlass / NeonBorder / FlatMinimal 差异）
        var contentStack = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Stretch };
        contentStack.Children.Add(new Border
        {
            Height = 14,
            CornerRadius = new CornerRadius(4),
            Background = accentGradBrush,
            Opacity = 0.88,
            Padding = new Thickness(6, 0, 6, 0),
            Child = new Border
            {
                Width = 42,
                Height = 3.5,
                CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(225, 255, 255, 255))
            }
        });

        var cardsRow = new Grid { ColumnSpacing = 4 };
        for (int c = 0; c < 3; c++)
        {
            cardsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            bool isNeon = t.CardVisualStyle == "NeonBorder";
            bool isFlat = t.CardVisualStyle == "FlatMinimal";
            Color miniCardBg = isThemeLight
                ? Color.FromArgb(98, 255, 255, 255)
                : Color.FromArgb(95, 30, 41, 59);
            Color miniCardStroke = isNeon
                ? Color.FromArgb(185, accentCol.R, accentCol.G, accentCol.B)
                : (isFlat ? Colors.Transparent : (isThemeLight ? Color.FromArgb(195, 255, 255, 255) : Color.FromArgb(55, 255, 255, 255)));

            var miniCard = new Border
            {
                Height = 32,
                CornerRadius = new CornerRadius(Math.Clamp(t.ButtonCornerRadius * 0.3, 2.5, 5.5)),
                Background = new SolidColorBrush(miniCardBg),
                BorderBrush = new SolidColorBrush(miniCardStroke),
                BorderThickness = new Thickness(isFlat ? 0 : 1),
                Padding = new Thickness(4, 4, 4, 3)
            };

            var mcStack = new StackPanel { Spacing = 2.5 };
            mcStack.Children.Add(new Border
            {
                Height = 13,
                CornerRadius = new CornerRadius(2.5),
                Background = new SolidColorBrush(Color.FromArgb((byte)(55 + c * 22), accentCol.R, accentCol.G, accentCol.B))
            });
            mcStack.Children.Add(new Border
            {
                Height = 3,
                Width = 24,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(1.5),
                Background = new SolidColorBrush(fgLine)
            });
            miniCard.Child = mcStack;
            Grid.SetColumn(miniCard, c);
            cardsRow.Children.Add(miniCard);
        }
        contentStack.Children.Add(cardsRow);
        Grid.SetColumn(contentStack, 1);
        bodyGrid.Children.Add(contentStack);

        Grid.SetRow(bodyGrid, 1);
        winGrid.Children.Add(bodyGrid);

        // 3.3 微缩底部播放栏（1:1 体现 FloatingIsland 悬浮岛 / MinimalGlass / Docked + 黑胶唱盘 + 律动灯带）
        bool isIsland = t.PlayBarLayoutMode == "FloatingIsland";
        bool isMinimalBar = t.PlayBarLayoutMode == "MinimalGlass";
        Color pbBg = t.EnableCustomPlayBar && ThemeService.TryParseColor(t.PlayBarBgHex, out var customPb)
            ? Color.FromArgb((byte)(isThemeLight ? 145 : 205), customPb.R, customPb.G, customPb.B)
            : (isThemeLight
                ? Color.FromArgb((byte)(isMinimalBar ? 85 : 135), 255, 255, 255)
                : Color.FromArgb((byte)(isMinimalBar ? 95 : 175), 17, 24, 39));

        var miniPlayBar = new Border
        {
            Margin = isIsland ? new Thickness(7, 1, 7, 3) : new Thickness(0),
            CornerRadius = isIsland ? new CornerRadius(8) : new CornerRadius(0, 0, 6, 6),
            Background = new SolidColorBrush(pbBg),
            BorderThickness = isMinimalBar ? new Thickness(0) : (isIsland ? new Thickness(1) : new Thickness(0, 1, 0, 0)),
            BorderBrush = new SolidColorBrush(isThemeLight
                ? Color.FromArgb(185, 255, 255, 255)
                : Color.FromArgb(80, accentCol.R, accentCol.G, accentCol.B)),
            Padding = new Thickness(6, 0, 6, 0)
        };

        var pbGrid = new Grid();
        if (t.EnablePlayBarRhythmGlow)
        {
            pbGrid.Children.Add(new Border
            {
                Height = 1.5,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(8, 0, 8, 0),
                Background = accentGradBrush
            });
        }

        // 左下微缩黑胶唱盘 / 封面
        bool isVinyl = t.CoverArtShape == "VinylSpin";
        var miniCover = new Border
        {
            Width = 11,
            Height = 11,
            CornerRadius = new CornerRadius(isVinyl || t.CoverArtShape == "Circle" ? 5.5 : 2.5),
            Background = isVinyl ? new SolidColorBrush(Color.FromArgb(240, 24, 24, 27)) : accentGradBrush,
            BorderThickness = isVinyl ? new Thickness(1.5) : new Thickness(0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(200, 39, 39, 42)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = isVinyl
                ? new Border { Width = 4, Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(accentCol) }
                : null
        };
        pbGrid.Children.Add(miniCover);

        // 中央播放圆钮 + 微缩进度条
        var centerControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        centerControls.Children.Add(new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Background = accentGradBrush
        });
        var miniTrack = new Border
        {
            Width = 48,
            Height = 2.5,
            CornerRadius = new CornerRadius(1.2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(subLine),
            Child = new Border
            {
                Width = 26,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(1.2),
                Background = new SolidColorBrush(accentCol)
            }
        };
        centerControls.Children.Add(miniTrack);
        pbGrid.Children.Add(centerControls);

        miniPlayBar.Child = pbGrid;
        Grid.SetRow(miniPlayBar, 2);
        winGrid.Children.Add(miniPlayBar);

        appWindowBox.Child = winGrid;
        mockupShell.Children.Add(appWindowBox);
        sceneRoot.Children.Add(mockupShell);

        // ==================== 第 4 层：若远程 JSON 配置了真实预览图 URL 则自动无感覆盖渲染 ====================
        if (!string.IsNullOrWhiteSpace(item.PreviewImageUrl) &&
            (item.PreviewImageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             item.PreviewImageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                sceneRoot.Children.Add(new Image
                {
                    Source = new BitmapImage(new Uri(item.PreviewImageUrl)),
                    Stretch = Stretch.UniformToFill,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                });
            }
            catch
            {
                // URL 异常时静默保留底层 1:1 矢量实景预览图
            }
        }

        // ==================== 第 5 层：顶部悬浮磨砂分类/明暗/动态特效角标 + 右下热度 ====================
        var topOverlay = new Grid
        {
            VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(7, 5, 7, 0),
            IsHitTestVisible = false
        };

        var leftPills = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        leftPills.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(155, 9, 13, 22)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1.5, 5, 1.5),
            Child = new TextBlock { Text = item.Category, FontSize = 9.5, Foreground = new SolidColorBrush(Colors.White), FontWeight = FontWeights.SemiBold }
        });
        leftPills.Children.Add(new Border
        {
            Background = new SolidColorBrush(isThemeLight ? Color.FromArgb(190, 255, 255, 255) : Color.FromArgb(155, 15, 23, 42)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4.5, 1.5, 4.5, 1.5),
            Child = new TextBlock
            {
                Text = isThemeLight ? "☀ 浅色" : "🌙 深色",
                FontSize = 9,
                Foreground = new SolidColorBrush(isThemeLight ? Color.FromArgb(255, 15, 23, 42) : Color.FromArgb(255, 226, 232, 240)),
                FontWeight = FontWeights.Medium
            }
        });
        topOverlay.Children.Add(leftPills);

        var rightInfoStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        if (t.HasDynamicEffect)
        {
            string fxLabel = (t.BackdropType is "Aurora" or "CoverFlow", t.EnableParticleOverlay) switch
            {
                (true, true) => "极光+粒子",
                (true, false) => t.BackdropType == "CoverFlow" ? "随歌流光" : "流体极光",
                _ => "氛围粒子"
            };
            rightInfoStack.Children.Add(new Border
            {
                Background = accentGradBrush,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1.5, 5, 1.5),
                Child = new TextBlock { Text = fxLabel, FontSize = 9, Foreground = new SolidColorBrush(Colors.White), FontWeight = FontWeights.SemiBold }
            });
        }

        string dlText = item.Downloads >= 10000 ? $"🔥 {item.Downloads / 10000.0:F1}w" : $"↓ {item.Downloads}";
        rightInfoStack.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(145, 9, 13, 22)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4.5, 1.5, 4.5, 1.5),
            Child = new TextBlock { Text = dlText, FontSize = 9, Foreground = new SolidColorBrush(Colors.White) }
        });
        topOverlay.Children.Add(rightInfoStack);

        sceneRoot.Children.Add(topOverlay);
        outerFrame.Child = sceneRoot;
        return outerFrame;
    }

    private void UpdateMarketCategoryFilterButtonsUi()
    {
        void SyncStack(StackPanel? panel)
        {
            if (panel == null) return;
            foreach (var child in panel.Children)
            {
                if (child is Button btn)
                {
                    string cat = btn.Tag?.ToString() ?? string.Empty;
                    bool isAllBtn = string.IsNullOrEmpty(cat) ||
                                    string.Equals(cat, "all", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(cat, "全部", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(cat, "全部精选", StringComparison.OrdinalIgnoreCase);

                    bool isSelected = isAllBtn
                        ? string.IsNullOrWhiteSpace(_selectedMarketCategory)
                        : string.Equals(cat, _selectedMarketCategory, StringComparison.OrdinalIgnoreCase);

                    ApplyDynamicAccentButtonState(btn, isSelected);
                }
            }
        }
        SyncStack(PageMarketFilterStack);
        SyncStack(ModalMarketFilterStack);
    }

    private void UpdateMarketFeedsStatusUi()
    {
        int feedCount = ViewModel.Config.Settings.ThemeCenter.MarketFeedUrls.Count;
        string text = $"已订阅 {feedCount} 个外部源";
        if (PageFeedStatusText != null) PageFeedStatusText.Text = text;
    }

    private void OnMarketCategoryFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            string cat = btn.Tag?.ToString() ?? string.Empty;
            if (string.Equals(cat, "all", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cat, "全部", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cat, "全部精选", StringComparison.OrdinalIgnoreCase))
            {
                cat = string.Empty;
            }
            _selectedMarketCategory = cat;
            RenderMarketplaceCardsUi();
        }
    }

    private void OnMarketSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized) return;
        if (sender is TextBox tb)
        {
            _marketSearchQuery = tb.Text;
            RenderMarketplaceCardsUi();
        }
    }

    private async void OnRefreshMarketFeedsClick(object sender, RoutedEventArgs e)
    {
        await LoadAndRenderMarketplaceCardsAsync(forceRefresh: true);
        ViewModel.StatusToast = "已刷新主题市场在线订阅源！";
    }

    // ==================== 主题分享口令与在线订阅源交互 ====================

    private void OnCopyThemeShareCodeClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string code = ViewModel.Theme.GenerateShareCode(_themeDraft);
            var dp = new DataPackage();
            dp.SetText(code);
            Clipboard.SetContent(dp);
            ViewModel.StatusToast = $"主题「{_themeDraft.Name}」紧凑口令已成功复制到剪贴板！";
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"复制口令失败：{ex.Message}";
        }
    }

    private async void OnImportShareCodeFromPageClick(object sender, RoutedEventArgs e)
    {
        string raw = PageShareCodeInputBox?.Text ?? string.Empty;
        await ExecuteImportShareCodeAsync(raw);
    }

    private async void OnImportShareCodeFromModalClick(object sender, RoutedEventArgs e)
    {
        string raw = ModalShareCodeInputBox?.Text ?? string.Empty;
        await ExecuteImportShareCodeAsync(raw);
    }

    private async Task ExecuteImportShareCodeAsync(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            ViewModel.StatusToast = "请先粘贴 ALGER-THEME:// 分享口令或主题 JSON";
            return;
        }

        var imported = await ViewModel.Theme.ImportFromShareCodeAsync(raw);
        if (imported == null)
        {
            ViewModel.StatusToast = "解析口令失败，请检查分享码是否完整有效";
            return;
        }

        var saved = await ViewModel.Theme.SaveCustomThemeAsync(imported);
        _themeDraft = saved.Clone();
        RebuildThemeLibraryCardsUi();
        LoadThemeDraftToEditorUi();
        if (PageShareCodeInputBox != null) PageShareCodeInputBox.Text = string.Empty;
        if (ModalShareCodeInputBox != null) ModalShareCodeInputBox.Text = string.Empty;
        ViewModel.StatusToast = $"成功导入并应用主题：{saved.Name}！";
    }

    private async void OnAddMarketFeedFromPageClick(object sender, RoutedEventArgs e)
    {
        string url = PageFeedUrlInputBox?.Text?.Trim() ?? string.Empty;
        await AddMarketFeedUrlAsync(url);
        if (PageFeedUrlInputBox != null) PageFeedUrlInputBox.Text = string.Empty;
    }

    private async void OnAddMarketFeedFromModalClick(object sender, RoutedEventArgs e)
    {
        string url = ModalFeedUrlInputBox?.Text?.Trim() ?? string.Empty;
        await AddMarketFeedUrlAsync(url);
        if (ModalFeedUrlInputBox != null) ModalFeedUrlInputBox.Text = string.Empty;
    }

    private async Task AddMarketFeedUrlAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            ViewModel.StatusToast = "请输入有效的 http/https 订阅源 URL";
            return;
        }

        var list = ViewModel.Config.Settings.ThemeCenter.MarketFeedUrls;
        if (list.Contains(url))
        {
            ViewModel.StatusToast = "该订阅源已在列表中";
            return;
        }

        list.Add(url);
        await ViewModel.Config.SaveSettingsAsync();
        await LoadAndRenderMarketplaceCardsAsync(forceRefresh: true);
        ViewModel.StatusToast = "已添加新订阅源并更新市场！";
    }

    private async void OnClearMarketFeedsClick(object sender, RoutedEventArgs e)
    {
        ViewModel.Config.Settings.ThemeCenter.MarketFeedUrls.Clear();
        await ViewModel.Config.SaveSettingsAsync();
        await LoadAndRenderMarketplaceCardsAsync(forceRefresh: true);
        ViewModel.StatusToast = "已清空所有在线订阅源";
    }

    // ==================== 弹窗与常规设置内嵌面板双向状态同步 ====================

    private void SwitchThemeDiyTabUi(string tab)
    {
        _activeThemeDiyTab = tab;
        ThemeDiyGlobalPanel.Visibility = tab == "global" ? Visibility.Visible : Visibility.Collapsed;
        ThemeDiyAccentPanel.Visibility = tab == "accent" ? Visibility.Visible : Visibility.Collapsed;
        ThemeDiyBarsPanel.Visibility = tab == "bars" ? Visibility.Visible : Visibility.Collapsed;
        ThemeDiyContentPanel.Visibility = tab == "content" ? Visibility.Visible : Visibility.Collapsed;
        ThemeDiyPlayBarPanel.Visibility = tab == "playbar" ? Visibility.Visible : Visibility.Collapsed;
        ThemeDiyStylePanel.Visibility = tab == "style" ? Visibility.Visible : Visibility.Collapsed;

        ApplyDynamicAccentButtonState(ThemeDiyTabGlobalBtn, tab == "global");
        ApplyDynamicAccentButtonState(ThemeDiyTabAccentBtn, tab == "accent");
        ApplyDynamicAccentButtonState(ThemeDiyTabBarsBtn, tab == "bars");
        ApplyDynamicAccentButtonState(ThemeDiyTabContentBtn, tab == "content");
        ApplyDynamicAccentButtonState(ThemeDiyTabPlayBarBtn, tab == "playbar");
        ApplyDynamicAccentButtonState(ThemeDiyTabStyleBtn, tab == "style");
    }

    private void OnThemeDiyTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tab)
        {
            SwitchThemeDiyTabUi(tab);
        }
    }

    private void LoadThemeDraftToEditorUi()
    {
        _isLoadingThemeCenterUi = true;

        // 1. 元信息与保存按钮文案
        ThemeDraftNameBox.Text = _themeDraft.Name;
        ThemeDraftAuthorBox.Text = _themeDraft.Author;
        ThemeDraftDescBox.Text = _themeDraft.Description;

        PageThemeNameBox.Text = _themeDraft.Name;
        PageThemeAuthorBox.Text = _themeDraft.Author;
        PageThemeDescBox.Text = _themeDraft.Description;
        UpdateSaveThemeButtonLabel();

        // 2. 全局材质与动态壁纸/极光
        SyncBaseModeAndBackdropButtons();

        double blurVal = Math.Clamp(_themeDraft.BackgroundBlur, 0, 24);
        string blurLabel = $"{blurVal:0}px";
        ThemeWallpaperBlurSlider.Value = blurVal;
        ThemeWallpaperBlurText.Text = blurLabel;
        PageWallpaperBlurSlider.Value = blurVal;
        PageWallpaperBlurText.Text = blurLabel;

        double maskVal = Math.Clamp(_themeDraft.BackgroundMaskOpacity, 0, 95);
        string maskLabel = $"{maskVal:0}%";
        ThemeWallpaperMaskOpacitySlider.Value = maskVal;
        ThemeWallpaperMaskOpacityText.Text = maskLabel;
        PageWallpaperMaskOpacitySlider.Value = maskVal;
        PageWallpaperMaskOpacityText.Text = maskLabel;
        PageVideoMaskOpacitySlider.Value = maskVal;
        PageVideoMaskOpacityText.Text = maskLabel;

        ThemeAdaptiveWallpaperTextToggle.IsOn = _themeDraft.EnableAdaptiveWallpaperText;
        PageAdaptiveWallpaperTextToggle.IsOn = _themeDraft.EnableAdaptiveWallpaperText;

        string wallpaperName = string.IsNullOrWhiteSpace(_themeDraft.BackgroundImagePath)
            ? "尚未选择背景图片"
            : Path.GetFileName(_themeDraft.BackgroundImagePath);
        ThemeWallpaperPathText.Text = wallpaperName;
        PageWallpaperPathText.Text = wallpaperName;

        string videoName = string.IsNullOrWhiteSpace(_themeDraft.BackgroundVideoPath)
            ? "尚未选择动态视频"
            : Path.GetFileName(_themeDraft.BackgroundVideoPath);
        ThemeVideoPathText.Text = videoName;
        PageVideoPathText.Text = videoName;

        // 极光流体控制
        double auroraSpeed = Math.Clamp(_themeDraft.AuroraSpeed * 100, 20, 300);
        ThemeAuroraSpeedSlider.Value = auroraSpeed;
        PageAuroraSpeedSlider.Value = auroraSpeed;
        string speedLabel = $"{_themeDraft.AuroraSpeed:0.0}x";
        ThemeAuroraSpeedText.Text = speedLabel;
        PageAuroraSpeedText.Text = speedLabel;

        double auroraInt = Math.Clamp(_themeDraft.AuroraIntensity, 20, 100);
        ThemeAuroraIntensitySlider.Value = auroraInt;
        PageAuroraIntensitySlider.Value = auroraInt;
        string intLabel = $"{auroraInt:0}%";
        ThemeAuroraIntensityText.Text = intLabel;
        PageAuroraIntensityText.Text = intLabel;

        // 氛围粒子层控制
        ThemeEnableParticleToggle.IsOn = _themeDraft.EnableParticleOverlay;
        PageEnableParticleToggle.IsOn = _themeDraft.EnableParticleOverlay;
        double pDensity = Math.Clamp(_themeDraft.ParticleDensity, 10, 90);
        ThemeParticleDensitySlider.Value = pDensity;
        PageParticleDensitySlider.Value = pDensity;
        ThemeParticleDensityText.Text = $"{pDensity:0}";
        PageParticleDensityText.Text = $"{pDensity:0}";

        double pSpeed = Math.Clamp(_themeDraft.ParticleSpeed * 100, 30, 250);
        ThemeParticleSpeedSlider.Value = pSpeed;
        PageParticleSpeedSlider.Value = pSpeed;
        ThemeParticleSpeedText.Text = $"{_themeDraft.ParticleSpeed:0.0}x";
        PageParticleSpeedText.Text = $"{_themeDraft.ParticleSpeed:0.0}x";

        // 3. 强调色与流光双色渐变
        UseCustomAccentStatesToggle.IsOn = _themeDraft.UseCustomAccentStates;
        PageUseCustomAccentStatesToggle.IsOn = _themeDraft.UseCustomAccentStates;
        ThemeEnableAccentGradToggle.IsOn = _themeDraft.EnableAccentGradient;
        PageEnableAccentGradToggle.IsOn = _themeDraft.EnableAccentGradient;

        // 4. 顶部标题栏与左侧导航栏
        EnableCustomTitleBarToggle.IsOn = _themeDraft.EnableCustomTitleBar;
        PageEnableCustomTitleBarToggle.IsOn = _themeDraft.EnableCustomTitleBar;
        ThemeEnableTitleBarGradToggle.IsOn = _themeDraft.EnableTitleBarGradient;
        PageEnableTitleBarGradToggle.IsOn = _themeDraft.EnableTitleBarGradient;
        double tbOp = Math.Clamp(_themeDraft.TitleBarOpacity, 0, 100);
        TitleBarOpacitySlider.Value = tbOp;
        TitleBarOpacityText.Text = $"{tbOp:0}%";
        PageTitleBarOpacitySlider.Value = tbOp;
        PageTitleBarOpacityText.Text = $"{tbOp:0}%";

        EnableCustomNavPaneToggle.IsOn = _themeDraft.EnableCustomNavPane;
        PageEnableCustomNavPaneToggle.IsOn = _themeDraft.EnableCustomNavPane;
        ThemeEnableNavPaneGradToggle.IsOn = _themeDraft.EnableNavPaneGradient;
        PageEnableNavPaneGradToggle.IsOn = _themeDraft.EnableNavPaneGradient;
        double navOp = Math.Clamp(_themeDraft.NavPaneOpacity, 0, 100);
        NavPaneOpacitySlider.Value = navOp;
        NavPaneOpacityText.Text = $"{navOp:0}%";
        PageNavPaneOpacitySlider.Value = navOp;
        PageNavPaneOpacityText.Text = $"{navOp:0}%";

        // 5. 主内容工作区与卡片
        EnableCustomContentToggle.IsOn = _themeDraft.EnableCustomContent;
        PageEnableCustomContentToggle.IsOn = _themeDraft.EnableCustomContent;
        double contentOp = Math.Clamp(_themeDraft.ContentOpacity, 0, 100);
        ContentOpacitySlider.Value = contentOp;
        ContentOpacityText.Text = $"{contentOp:0}%";
        PageContentOpacitySlider.Value = contentOp;
        PageContentOpacityText.Text = $"{contentOp:0}%";

        double cardOp = Math.Clamp(_themeDraft.CardOpacity, 0, 100);
        CardOpacitySlider.Value = cardOp;
        CardOpacityText.Text = $"{cardOp:0}%";
        PageCardOpacitySlider.Value = cardOp;
        PageCardOpacityText.Text = $"{cardOp:0}%";

        double cardRad = Math.Clamp(_themeDraft.CardCornerRadius, 4, 24);
        CardCornerRadiusSlider.Value = cardRad;
        CardCornerRadiusText.Text = $"{cardRad:0}px";
        PageCardCornerRadiusSlider.Value = cardRad;
        PageCardCornerRadiusText.Text = $"{cardRad:0}px";

        // 6. 底部播放栏、双色渐变与联动
        EnableCustomPlayBarToggle.IsOn = _themeDraft.EnableCustomPlayBar;
        PageEnableCustomPlayBarToggle.IsOn = _themeDraft.EnableCustomPlayBar;
        ThemeEnablePlayBarGradToggle.IsOn = _themeDraft.EnablePlayBarGradient;
        PageEnablePlayBarGradToggle.IsOn = _themeDraft.EnablePlayBarGradient;
        double pbOp = Math.Clamp(_themeDraft.PlayBarOpacity, 0, 100);
        PlayBarOpacitySlider.Value = pbOp;
        PlayBarOpacityText.Text = $"{pbOp:0}%";
        PagePlayBarOpacitySlider.Value = pbOp;
        PagePlayBarOpacityText.Text = $"{pbOp:0}%";

        bool followCover = ViewModel.Config.Settings.ThemeCenter.FollowSongCoverAccent;
        FollowSongCoverAccentToggle.IsOn = followCover;
        PageFollowCoverAccentToggle.IsOn = followCover;

        SyncMiniPlayerToggle.IsOn = _themeDraft.SyncMiniPlayer;
        PageSyncMiniPlayerToggle.IsOn = _themeDraft.SyncMiniPlayer;
        EnableCustomMiniBgToggle.IsOn = _themeDraft.EnableCustomMiniPlayerBg;
        PageEnableCustomMiniBgToggle.IsOn = _themeDraft.EnableCustomMiniPlayerBg;
        double miniOp = Math.Clamp(_themeDraft.MiniPlayerOpacity, 30, 100);
        MiniPlayerOpacitySlider.Value = miniOp;
        PageMiniPlayerOpacitySlider.Value = miniOp;

        SyncImmersiveLyricToggle.IsOn = _themeDraft.SyncImmersiveLyricHighlight;
        PageSyncImmersiveLyricToggle.IsOn = _themeDraft.SyncImmersiveLyricHighlight;
        SyncDesktopLyricToggle.IsOn = _themeDraft.SyncDesktopLyric;
        PageSyncDesktopLyricToggle.IsOn = _themeDraft.SyncDesktopLyric;

        // 7. 形态、质感、黑胶唱盘与字体
        ThemeEnableRhythmGlowToggle.IsOn = _themeDraft.EnablePlayBarRhythmGlow;
        PageEnableRhythmGlowToggle.IsOn = _themeDraft.EnablePlayBarRhythmGlow;
        double btnRad = Math.Clamp(_themeDraft.ButtonCornerRadius, 4, 22);
        ThemeButtonRadiusSlider.Value = btnRad;
        PageButtonRadiusSlider.Value = btnRad;
        ThemeButtonRadiusText.Text = $"{btnRad:0}px";
        PageButtonRadiusText.Text = $"{btnRad:0}px";
        ThemeCustomFontBox.Text = _themeDraft.CustomFontFamily ?? string.Empty;
        PageCustomFontBox.Text = _themeDraft.CustomFontFamily ?? string.Empty;

        SyncOptionButtonsActiveState();
        SyncSectionCollapsibleVisibilityFromDraft();
        SyncAllColorBoxesAndSwatchesFromDraft();
        UpdateMiniPreviewWindow();

        _isLoadingThemeCenterUi = false;
    }

    private void SyncOptionButtonsActiveState()
    {
        // 粒子类型按钮
        void SyncParticles(StackPanel? p)
        {
            if (p == null) return;
            foreach (var child in p.Children)
            {
                if (child is Button btn && btn.Tag is string tag)
                {
                    ApplyDynamicAccentButtonState(btn, string.Equals(tag, _themeDraft.ParticleType, StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        SyncParticles(PageParticleTypeButtonsPanel);
        SyncParticles(ModalParticleTypeButtonsPanel);

        // 渐变角度按钮
        void SyncAngles(StackPanel? p)
        {
            if (p == null) return;
            string curAngleStr = ((int)Math.Round(_themeDraft.GradientAngle)).ToString();
            foreach (var child in p.Children)
            {
                if (child is Button btn && btn.Tag is string tag)
                {
                    ApplyDynamicAccentButtonState(btn, string.Equals(tag, curAngleStr, StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        SyncAngles(PageGradientAngleButtonsPanel);
        SyncAngles(ModalGradientAngleButtonsPanel);

        // 播放栏形态按钮
        void SyncPlayBarLayouts(StackPanel? p)
        {
            if (p == null) return;
            foreach (var child in p.Children)
            {
                if (child is Button btn && btn.Tag is string tag)
                {
                    ApplyDynamicAccentButtonState(btn, string.Equals(tag, _themeDraft.PlayBarLayoutMode, StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        SyncPlayBarLayouts(PagePlayBarLayoutButtonsPanel);
        SyncPlayBarLayouts(ModalPlayBarLayoutButtonsPanel);

        // 卡片质感按钮
        void SyncCardStyles(StackPanel? p)
        {
            if (p == null) return;
            foreach (var child in p.Children)
            {
                if (child is Button btn && btn.Tag is string tag)
                {
                    ApplyDynamicAccentButtonState(btn, string.Equals(tag, _themeDraft.CardVisualStyle, StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        SyncCardStyles(PageCardVisualStyleButtonsPanel);
        SyncCardStyles(ModalCardVisualStyleButtonsPanel);

        // 封面造型按钮
        void SyncCoverShapes(StackPanel? p)
        {
            if (p == null) return;
            foreach (var child in p.Children)
            {
                if (child is Button btn && btn.Tag is string tag)
                {
                    ApplyDynamicAccentButtonState(btn, string.Equals(tag, _themeDraft.CoverArtShape, StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        SyncCoverShapes(PageCoverArtShapeButtonsPanel);
        SyncCoverShapes(ModalCoverArtShapeButtonsPanel);

        // 字体预设按钮
        void SyncFonts(StackPanel? p)
        {
            if (p == null) return;
            string currentFont = _themeDraft.CustomFontFamily ?? string.Empty;
            foreach (var child in p.Children)
            {
                if (child is Button btn && btn.Tag is string tag)
                {
                    ApplyDynamicAccentButtonState(btn, string.Equals(tag, currentFont, StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        SyncFonts(PageFontPresetButtonsPanel);
        SyncFonts(ModalFontPresetButtonsPanel);
    }

    private void SyncSectionCollapsibleVisibilityFromDraft()
    {
        CustomAccentStatesBox.Visibility = _themeDraft.UseCustomAccentStates ? Visibility.Visible : Visibility.Collapsed;
        PageCustomAccentStatesBox.Visibility = _themeDraft.UseCustomAccentStates ? Visibility.Visible : Visibility.Collapsed;
        ThemeAccentGradBox.Visibility = _themeDraft.EnableAccentGradient ? Visibility.Visible : Visibility.Collapsed;
        PageAccentGradBox.Visibility = _themeDraft.EnableAccentGradient ? Visibility.Visible : Visibility.Collapsed;

        CustomTitleBarDetailBox.Visibility = _themeDraft.EnableCustomTitleBar ? Visibility.Visible : Visibility.Collapsed;
        PageCustomTitleBarBox.Visibility = _themeDraft.EnableCustomTitleBar ? Visibility.Visible : Visibility.Collapsed;

        CustomNavPaneDetailBox.Visibility = _themeDraft.EnableCustomNavPane ? Visibility.Visible : Visibility.Collapsed;
        PageCustomNavPaneBox.Visibility = _themeDraft.EnableCustomNavPane ? Visibility.Visible : Visibility.Collapsed;

        CustomContentDetailBox.Visibility = _themeDraft.EnableCustomContent ? Visibility.Visible : Visibility.Collapsed;
        PageCustomContentBox.Visibility = _themeDraft.EnableCustomContent ? Visibility.Visible : Visibility.Collapsed;

        CustomPlayBarDetailBox.Visibility = _themeDraft.EnableCustomPlayBar ? Visibility.Visible : Visibility.Collapsed;
        PageCustomPlayBarBox.Visibility = _themeDraft.EnableCustomPlayBar ? Visibility.Visible : Visibility.Collapsed;

        ThemeParticleDetailBox.Visibility = _themeDraft.EnableParticleOverlay ? Visibility.Visible : Visibility.Collapsed;
        PageParticleDetailBox.Visibility = _themeDraft.EnableParticleOverlay ? Visibility.Visible : Visibility.Collapsed;

        double miniRowOpacity = _themeDraft.SyncMiniPlayer ? 1.0 : 0.45;
        CustomMiniPlayerBgRow.Opacity = miniRowOpacity;
        PageCustomMiniBgBox.Opacity = miniRowOpacity;
    }

    private void UpdateSaveThemeButtonLabel()
    {
        string label = _themeDraft.IsBuiltIn
            ? "另存为我的自制主题并应用"
            : "保存当前自制主题并应用";
        SaveCustomThemeBtnText.Text = label;
        PageSaveThemeBtnText.Text = label;
    }

    private void SyncBaseModeAndBackdropButtons()
    {
        bool isDark = _themeDraft.BaseMode == "Dark";
        bool isLight = _themeDraft.BaseMode == "Light";
        bool isSystem = _themeDraft.BaseMode == "System";

        ApplyDynamicAccentButtonState(ThemeBaseDarkBtn, isDark);
        ApplyDynamicAccentButtonState(ThemeBaseLightBtn, isLight);
        ApplyDynamicAccentButtonState(ThemeBaseSystemBtn, isSystem);

        ApplyDynamicAccentButtonState(PageBaseDarkBtn, isDark);
        ApplyDynamicAccentButtonState(PageBaseLightBtn, isLight);
        ApplyDynamicAccentButtonState(PageBaseSystemBtn, isSystem);

        bool isMica = _themeDraft.BackdropType == "Mica";
        bool isMicaAlt = _themeDraft.BackdropType == "MicaAlt";
        bool isAcrylic = _themeDraft.BackdropType == "Acrylic";
        bool isSolid = _themeDraft.BackdropType == "Solid";
        bool isAurora = _themeDraft.BackdropType == "Aurora";
        bool isCoverFlow = _themeDraft.BackdropType == "CoverFlow";
        bool isImage = _themeDraft.BackdropType == "Image";
        bool isVideo = _themeDraft.BackdropType == "Video";

        ApplyDynamicAccentButtonState(BackdropMicaBtn, isMica);
        ApplyDynamicAccentButtonState(BackdropMicaAltBtn, isMicaAlt);
        ApplyDynamicAccentButtonState(BackdropAcrylicBtn, isAcrylic);
        ApplyDynamicAccentButtonState(BackdropSolidBtn, isSolid);
        ApplyDynamicAccentButtonState(BackdropAuroraBtn, isAurora);
        ApplyDynamicAccentButtonState(BackdropCoverFlowBtn, isCoverFlow);
        ApplyDynamicAccentButtonState(BackdropImageBtn, isImage);
        ApplyDynamicAccentButtonState(BackdropVideoBtn, isVideo);

        ApplyDynamicAccentButtonState(PageBackdropMicaBtn, isMica);
        ApplyDynamicAccentButtonState(PageBackdropMicaAltBtn, isMicaAlt);
        ApplyDynamicAccentButtonState(PageBackdropAcrylicBtn, isAcrylic);
        ApplyDynamicAccentButtonState(PageBackdropSolidBtn, isSolid);
        ApplyDynamicAccentButtonState(PageBackdropAuroraBtn, isAurora);
        ApplyDynamicAccentButtonState(PageBackdropCoverFlowBtn, isCoverFlow);
        ApplyDynamicAccentButtonState(PageBackdropImageBtn, isImage);
        ApplyDynamicAccentButtonState(PageBackdropVideoBtn, isVideo);

        // 展开与折叠对应专属面板
        ThemeAuroraSettingsBox.Visibility = (isAurora || isCoverFlow) ? Visibility.Visible : Visibility.Collapsed;
        PageAuroraDetailBox.Visibility = (isAurora || isCoverFlow) ? Visibility.Visible : Visibility.Collapsed;
        PageAuroraColorsRow.Visibility = isCoverFlow ? Visibility.Collapsed : Visibility.Visible;
        ThemeAuroraColorsRow.Visibility = isCoverFlow ? Visibility.Collapsed : Visibility.Visible;
        PageAuroraHintText.Text = isCoverFlow ? "光球色彩将随切歌自动吸色平滑渐变，在此可微调流动速度与弥散浓度" : "自由搭配 3 组弥散流体光球色彩，窗口最小化时自动休眠零耗电";
        ThemeAuroraHintText.Text = PageAuroraHintText.Text;

        ThemeWallpaperSettingsBox.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
        PageImageWallpaperRow.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;

        ThemeVideoSettingsBox.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        PageVideoDetailBox.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncAllColorBoxesAndSwatchesFromDraft()
    {
        var accentFallback = ThemeService.ParseColorOrDefault(_themeDraft.AccentColorHex, Color.FromArgb(255, 16, 185, 129));
        bool isLight = _themeDraft.BaseMode == "Light";
        var autoText = isLight ? Color.FromArgb(255, 30, 41, 59) : Color.FromArgb(255, 248, 250, 252);
        var autoSubText = isLight ? Color.FromArgb(255, 100, 116, 139) : Color.FromArgb(255, 148, 163, 184);
        var hoverFallback = ThemeService.AdjustLightness(accentFallback, -0.08);
        var pressedFallback = ThemeService.AdjustLightness(accentFallback, -0.15);

        // 弹窗控件绑定
        BindColorControlPair(SwatchWindowBgBtn, HexWindowBgBox, _themeDraft.WindowBgColorHex, Color.FromArgb(255, 15, 23, 42));
        BindColorControlPair(SwatchMaskColorBtn, HexMaskColorBox, _themeDraft.BackgroundMaskColorHex, Color.FromArgb(255, 9, 13, 22));

        BindColorControlPair(SwatchAurora1Btn, null, _themeDraft.AuroraColor1Hex, Color.FromArgb(255, 16, 185, 129));
        BindColorControlPair(SwatchAurora2Btn, null, _themeDraft.AuroraColor2Hex, Color.FromArgb(255, 59, 130, 246));
        BindColorControlPair(SwatchAurora3Btn, null, _themeDraft.AuroraColor3Hex, Color.FromArgb(255, 168, 85, 247));
        BindColorControlPair(SwatchParticleColorBtn, null, _themeDraft.ParticleColorHex, accentFallback, allowEmptyText: true);

        BindColorControlPair(SwatchAccentBtn, HexAccentBox, _themeDraft.AccentColorHex, accentFallback);
        BindColorControlPair(SwatchAccentGradEndBtn, HexAccentGradEndBox, _themeDraft.AccentGradientEndHex, accentFallback);
        BindColorControlPair(SwatchAccentHoverBtn, HexAccentHoverBox, _themeDraft.AccentHoverHex, hoverFallback);
        BindColorControlPair(SwatchAccentPressedBtn, HexAccentPressedBox, _themeDraft.AccentPressedHex, pressedFallback);

        BindColorControlPair(SwatchTitleBarBgBtn, HexTitleBarBgBox, _themeDraft.TitleBarBgHex, Color.FromArgb(255, 17, 24, 39));
        BindColorControlPair(SwatchTitleBarGradEndBtn, HexTitleBarGradEndBox, _themeDraft.TitleBarGradientEndHex, Color.FromArgb(255, 17, 24, 39));
        BindColorControlPair(SwatchTitleBarTextBtn, HexTitleBarTextBox, _themeDraft.TitleBarTextHex, autoText, allowEmptyText: true);

        BindColorControlPair(SwatchNavPaneBgBtn, HexNavPaneBgBox, _themeDraft.NavPaneBgHex, Color.FromArgb(255, 15, 23, 42));
        BindColorControlPair(SwatchNavPaneGradEndBtn, HexNavPaneGradEndBox, _themeDraft.NavPaneGradientEndHex, Color.FromArgb(255, 15, 23, 42));
        BindColorControlPair(SwatchNavPaneTextBtn, HexNavPaneTextBox, _themeDraft.NavPaneTextHex, autoText, allowEmptyText: true);
        BindColorControlPair(SwatchNavSelectedBgBtn, HexNavSelectedBgBox, _themeDraft.NavPaneSelectedBgHex, accentFallback, allowEmptyText: true);

        BindColorControlPair(SwatchContentBgBtn, HexContentBgBox, _themeDraft.ContentBgHex, Color.FromArgb(255, 11, 17, 32));
        BindColorControlPair(SwatchCardBgBtn, HexCardBgBox, _themeDraft.CardBgHex, Color.FromArgb(255, 30, 41, 59));
        BindColorControlPair(SwatchCardBorderBtn, HexCardBorderBox, _themeDraft.CardBorderHex, Color.FromArgb(255, 51, 65, 85));
        BindColorControlPair(SwatchPrimaryTextBtn, HexPrimaryTextBox, _themeDraft.PrimaryTextHex, autoText, allowEmptyText: true);
        BindColorControlPair(SwatchSecondaryTextBtn, HexSecondaryTextBox, _themeDraft.SecondaryTextHex, autoSubText, allowEmptyText: true);

        BindColorControlPair(SwatchPlayBarBgBtn, HexPlayBarBgBox, _themeDraft.PlayBarBgHex, Color.FromArgb(255, 17, 24, 39));
        BindColorControlPair(SwatchPlayBarGradEndBtn, HexPlayBarGradEndBox, _themeDraft.PlayBarGradientEndHex, Color.FromArgb(255, 17, 24, 39));
        BindColorControlPair(SwatchPlayBarBorderBtn, HexPlayBarBorderBox, _themeDraft.PlayBarBorderHex, Color.FromArgb(255, 31, 41, 55));
        BindColorControlPair(SwatchPlayBarTextBtn, HexPlayBarTextBox, _themeDraft.PlayBarTextHex, autoText, allowEmptyText: true);
        BindColorControlPair(SwatchPlayButtonBtn, HexPlayButtonBox, _themeDraft.PlayButtonColorHex, accentFallback, allowEmptyText: true);
        BindColorControlPair(SwatchProgressSliderBtn, HexProgressSliderBox, _themeDraft.ProgressSliderColorHex, accentFallback, allowEmptyText: true);

        BindColorControlPair(SwatchMiniBgBtn, HexMiniBgBox, _themeDraft.MiniPlayerBgHex, Color.FromArgb(255, 17, 24, 39));
        BindColorControlPair(SwatchLyricHighlightBtn, HexLyricHighlightBox, _themeDraft.LyricHighlightColorHex, accentFallback, allowEmptyText: true);

        // 常规设置页内嵌面板控件同步绑定
        BindColorControlPair(PageSwatchWindowBgBtn, PageHexWindowBgBox, _themeDraft.WindowBgColorHex, Color.FromArgb(255, 15, 23, 42));
        BindColorControlPair(PageSwatchMaskColorBtn, PageHexMaskColorBox, _themeDraft.BackgroundMaskColorHex, Color.FromArgb(255, 9, 13, 22));
        BindColorControlPair(PageSwatchVideoMaskBtn, null, _themeDraft.BackgroundMaskColorHex, Color.FromArgb(255, 9, 13, 22));

        BindColorControlPair(PageSwatchAurora1Btn, PageHexAurora1Box, _themeDraft.AuroraColor1Hex, Color.FromArgb(255, 16, 185, 129));
        BindColorControlPair(PageSwatchAurora2Btn, PageHexAurora2Box, _themeDraft.AuroraColor2Hex, Color.FromArgb(255, 59, 130, 246));
        BindColorControlPair(PageSwatchAurora3Btn, PageHexAurora3Box, _themeDraft.AuroraColor3Hex, Color.FromArgb(255, 168, 85, 247));
        BindColorControlPair(PageSwatchParticleColorBtn, PageHexParticleColorBox, _themeDraft.ParticleColorHex, accentFallback, allowEmptyText: true);

        BindColorControlPair(PageSwatchAccentBtn, PageHexAccentBox, _themeDraft.AccentColorHex, accentFallback);
        BindColorControlPair(PageSwatchAccentGradEndBtn, PageHexAccentGradEndBox, _themeDraft.AccentGradientEndHex, accentFallback);
        BindColorControlPair(PageSwatchAccentHoverBtn, PageHexAccentHoverBox, _themeDraft.AccentHoverHex, hoverFallback);
        BindColorControlPair(PageSwatchAccentPressedBtn, PageHexAccentPressedBox, _themeDraft.AccentPressedHex, pressedFallback);

        BindColorControlPair(PageSwatchTitleBarBgBtn, PageHexTitleBarBgBox, _themeDraft.TitleBarBgHex, Color.FromArgb(255, 17, 24, 39));
        BindColorControlPair(PageSwatchTitleBarGradEndBtn, PageHexTitleBarGradEndBox, _themeDraft.TitleBarGradientEndHex, Color.FromArgb(255, 17, 24, 39));
        BindColorControlPair(PageSwatchTitleBarTextBtn, PageHexTitleBarTextBox, _themeDraft.TitleBarTextHex, autoText, allowEmptyText: true);

        BindColorControlPair(PageSwatchNavPaneBgBtn, PageHexNavPaneBgBox, _themeDraft.NavPaneBgHex, Color.FromArgb(255, 15, 23, 42));
        BindColorControlPair(PageSwatchNavPaneGradEndBtn, PageHexNavPaneGradEndBox, _themeDraft.NavPaneGradientEndHex, Color.FromArgb(255, 15, 23, 42));
        BindColorControlPair(PageSwatchNavPaneTextBtn, PageHexNavPaneTextBox, _themeDraft.NavPaneTextHex, autoText, allowEmptyText: true);
        BindColorControlPair(PageSwatchNavSelectedBgBtn, PageHexNavSelectedBgBox, _themeDraft.NavPaneSelectedBgHex, accentFallback, allowEmptyText: true);

        BindColorControlPair(PageSwatchContentBgBtn, PageHexContentBgBox, _themeDraft.ContentBgHex, Color.FromArgb(255, 11, 17, 32));
        BindColorControlPair(PageSwatchCardBgBtn, PageHexCardBgBox, _themeDraft.CardBgHex, Color.FromArgb(255, 30, 41, 59));
        BindColorControlPair(PageSwatchCardBorderBtn, PageHexCardBorderBox, _themeDraft.CardBorderHex, Color.FromArgb(255, 51, 65, 85));
        BindColorControlPair(PageSwatchPrimaryTextBtn, PageHexPrimaryTextBox, _themeDraft.PrimaryTextHex, autoText, allowEmptyText: true);
        BindColorControlPair(PageSwatchSecondaryTextBtn, PageHexSecondaryTextBox, _themeDraft.SecondaryTextHex, autoSubText, allowEmptyText: true);

        BindColorControlPair(PageSwatchPlayBarBgBtn, PageHexPlayBarBgBox, _themeDraft.PlayBarBgHex, Color.FromArgb(255, 17, 24, 39));
        BindColorControlPair(PageSwatchPlayBarGradEndBtn, PageHexPlayBarGradEndBox, _themeDraft.PlayBarGradientEndHex, Color.FromArgb(255, 17, 24, 39));
        BindColorControlPair(PageSwatchPlayBarBorderBtn, PageHexPlayBarBorderBox, _themeDraft.PlayBarBorderHex, Color.FromArgb(255, 31, 41, 55));
        BindColorControlPair(PageSwatchPlayBarTextBtn, PageHexPlayBarTextBox, _themeDraft.PlayBarTextHex, autoText, allowEmptyText: true);
        BindColorControlPair(PageSwatchPlayButtonBtn, PageHexPlayButtonBox, _themeDraft.PlayButtonColorHex, accentFallback, allowEmptyText: true);
        BindColorControlPair(PageSwatchProgressSliderBtn, PageHexProgressSliderBox, _themeDraft.ProgressSliderColorHex, accentFallback, allowEmptyText: true);

        BindColorControlPair(PageSwatchMiniBgBtn, PageHexMiniBgBox, _themeDraft.MiniPlayerBgHex, Color.FromArgb(255, 17, 24, 39));
        BindColorControlPair(PageSwatchLyricHighlightBtn, PageHexLyricHighlightBox, _themeDraft.LyricHighlightColorHex, accentFallback, allowEmptyText: true);
    }

    private static void BindColorControlPair(Button swatchBtn, TextBox? hexBox, string rawHex, Color fallbackSwatchColor, bool allowEmptyText = false)
    {
        var col = ThemeService.ParseColorOrDefault(rawHex, fallbackSwatchColor);
        if (swatchBtn.Background is SolidColorBrush scb)
        {
            scb.Color = col;
        }
        else
        {
            swatchBtn.Background = new SolidColorBrush(col);
        }

        if (hexBox != null)
        {
            string targetText = allowEmptyText && string.IsNullOrWhiteSpace(rawHex)
                ? string.Empty
                : (string.IsNullOrWhiteSpace(rawHex) ? ThemeService.ToHex(col) : rawHex);
            if (!string.Equals(hexBox.Text, targetText, StringComparison.Ordinal))
            {
                hexBox.Text = targetText;
            }
        }
    }

    private void SyncSingleColorControlByKey(string key, Color resolvedColor, string rawHex)
    {
        bool allowEmpty = IsOptionalColorKey(key);
        switch (key)
        {
            case "WindowBgColorHex":
                BindColorControlPair(SwatchWindowBgBtn, HexWindowBgBox, rawHex, resolvedColor);
                BindColorControlPair(PageSwatchWindowBgBtn, PageHexWindowBgBox, rawHex, resolvedColor);
                break;
            case "BackgroundMaskColorHex":
                BindColorControlPair(SwatchMaskColorBtn, HexMaskColorBox, rawHex, resolvedColor);
                BindColorControlPair(PageSwatchMaskColorBtn, PageHexMaskColorBox, rawHex, resolvedColor);
                BindColorControlPair(PageSwatchVideoMaskBtn, null, rawHex, resolvedColor);
                break;
            case "AuroraColor1Hex":
                BindColorControlPair(SwatchAurora1Btn, null, rawHex, resolvedColor);
                BindColorControlPair(PageSwatchAurora1Btn, PageHexAurora1Box, rawHex, resolvedColor);
                break;
            case "AuroraColor2Hex":
                BindColorControlPair(SwatchAurora2Btn, null, rawHex, resolvedColor);
                BindColorControlPair(PageSwatchAurora2Btn, PageHexAurora2Box, rawHex, resolvedColor);
                break;
            case "AuroraColor3Hex":
                BindColorControlPair(SwatchAurora3Btn, null, rawHex, resolvedColor);
                BindColorControlPair(PageSwatchAurora3Btn, PageHexAurora3Box, rawHex, resolvedColor);
                break;
            case "ParticleColorHex":
                BindColorControlPair(SwatchParticleColorBtn, null, rawHex, resolvedColor, allowEmpty);
                BindColorControlPair(PageSwatchParticleColorBtn, PageHexParticleColorBox, rawHex, resolvedColor, allowEmpty);
                break;
            case "AccentColorHex":
                // 主强调色会影响自动衍生色块（Hover/Pressed/可选跟随主色的预览块），同步全组但原地改色零 GC
                SyncAllColorBoxesAndSwatchesFromDraft();
                break;
            case "AccentGradientEndHex":
                BindColorControlPair(SwatchAccentGradEndBtn, HexAccentGradEndBox, rawHex, resolvedColor);
                BindColorControlPair(PageSwatchAccentGradEndBtn, PageHexAccentGradEndBox, rawHex, resolvedColor);
                break;
            case "AccentHoverHex":
                BindColorControlPair(SwatchAccentHoverBtn, HexAccentHoverBox, rawHex, resolvedColor);
                BindColorControlPair(PageSwatchAccentHoverBtn, PageHexAccentHoverBox, rawHex, resolvedColor);
                break;
            case "AccentPressedHex":
                BindColorControlPair(SwatchAccentPressedBtn, HexAccentPressedBox, rawHex, resolvedColor);
                BindColorControlPair(PageSwatchAccentPressedBtn, PageHexAccentPressedBox, rawHex, resolvedColor);
                break;
            case "TitleBarBgHex":
                BindColorControlPair(SwatchTitleBarBgBtn, HexTitleBarBgBox, rawHex, resolvedColor);
                BindColorControlPair(PageSwatchTitleBarBgBtn, PageHexTitleBarBgBox, rawHex, resolvedColor);
                break;
            case "TitleBarGradientEndHex":
                BindColorControlPair(SwatchTitleBarGradEndBtn, HexTitleBarGradEndBox, rawHex, resolvedColor);
                BindColorControlPair(PageSwatchTitleBarGradEndBtn, PageHexTitleBarGradEndBox, rawHex, resolvedColor);
                break;
            case "TitleBarTextHex":
                BindColorControlPair(SwatchTitleBarTextBtn, HexTitleBarTextBox, rawHex, resolvedColor, allowEmpty);
                BindColorControlPair(PageSwatchTitleBarTextBtn, PageHexTitleBarTextBox, rawHex, resolvedColor, allowEmpty);
                break;
            case "NavPaneBgHex":
                BindColorControlPair(SwatchNavPaneBgBtn, HexNavPaneBgBox, rawHex, resolvedColor);
                BindColorControlPair(PageSwatchNavPaneBgBtn, PageHexNavPaneBgBox, rawHex, resolvedColor);
                break;
            case "NavPaneGradientEndHex":
                BindColorControlPair(SwatchNavPaneGradEndBtn, HexNavPaneGradEndBox, rawHex, resolvedColor);
                BindColorControlPair(PageSwatchNavPaneGradEndBtn, PageHexNavPaneGradEndBox, rawHex, resolvedColor);
                break;
            case "NavPaneTextHex":
                BindColorControlPair(SwatchNavPaneTextBtn, HexNavPaneTextBox, rawHex, resolvedColor, allowEmpty);
                BindColorControlPair(PageSwatchNavPaneTextBtn, PageHexNavPaneTextBox, rawHex, resolvedColor, allowEmpty);
                break;
            case "NavPaneSelectedBgHex":
                BindColorControlPair(SwatchNavSelectedBgBtn, HexNavSelectedBgBox, rawHex, resolvedColor, allowEmpty);
                BindColorControlPair(PageSwatchNavSelectedBgBtn, PageHexNavSelectedBgBox, rawHex, resolvedColor, allowEmpty);
                break;
            case "ContentBgHex":
                BindColorControlPair(SwatchContentBgBtn, HexContentBgBox, rawHex, resolvedColor);
                BindColorControlPair(PageSwatchContentBgBtn, PageHexContentBgBox, rawHex, resolvedColor);
                break;
            case "CardBgHex":
                BindColorControlPair(SwatchCardBgBtn, HexCardBgBox, rawHex, resolvedColor);
                BindColorControlPair(PageSwatchCardBgBtn, PageHexCardBgBox, rawHex, resolvedColor);
                break;
            case "CardBorderHex":
                BindColorControlPair(SwatchCardBorderBtn, HexCardBorderBox, rawHex, resolvedColor);
                BindColorControlPair(PageSwatchCardBorderBtn, PageHexCardBorderBox, rawHex, resolvedColor);
                break;
            case "PrimaryTextHex":
                BindColorControlPair(SwatchPrimaryTextBtn, HexPrimaryTextBox, rawHex, resolvedColor, allowEmpty);
                BindColorControlPair(PageSwatchPrimaryTextBtn, PageHexPrimaryTextBox, rawHex, resolvedColor, allowEmpty);
                break;
            case "SecondaryTextHex":
                BindColorControlPair(SwatchSecondaryTextBtn, HexSecondaryTextBox, rawHex, resolvedColor, allowEmpty);
                BindColorControlPair(PageSwatchSecondaryTextBtn, PageHexSecondaryTextBox, rawHex, resolvedColor, allowEmpty);
                break;
            case "PlayBarBgHex":
                BindColorControlPair(SwatchPlayBarBgBtn, HexPlayBarBgBox, rawHex, resolvedColor);
                BindColorControlPair(PageSwatchPlayBarBgBtn, PageHexPlayBarBgBox, rawHex, resolvedColor);
                break;
            case "PlayBarGradientEndHex":
                BindColorControlPair(SwatchPlayBarGradEndBtn, HexPlayBarGradEndBox, rawHex, resolvedColor);
                BindColorControlPair(PageSwatchPlayBarGradEndBtn, PageHexPlayBarGradEndBox, rawHex, resolvedColor);
                break;
            case "PlayBarBorderHex":
                BindColorControlPair(SwatchPlayBarBorderBtn, HexPlayBarBorderBox, rawHex, resolvedColor);
                BindColorControlPair(PageSwatchPlayBarBorderBtn, PageHexPlayBarBorderBox, rawHex, resolvedColor);
                break;
            case "PlayBarTextHex":
                BindColorControlPair(SwatchPlayBarTextBtn, HexPlayBarTextBox, rawHex, resolvedColor, allowEmpty);
                BindColorControlPair(PageSwatchPlayBarTextBtn, PageHexPlayBarTextBox, rawHex, resolvedColor, allowEmpty);
                break;
            case "PlayButtonColorHex":
                BindColorControlPair(SwatchPlayButtonBtn, HexPlayButtonBox, rawHex, resolvedColor, allowEmpty);
                BindColorControlPair(PageSwatchPlayButtonBtn, PageHexPlayButtonBox, rawHex, resolvedColor, allowEmpty);
                break;
            case "ProgressSliderColorHex":
                BindColorControlPair(SwatchProgressSliderBtn, HexProgressSliderBox, rawHex, resolvedColor, allowEmpty);
                BindColorControlPair(PageSwatchProgressSliderBtn, PageHexProgressSliderBox, rawHex, resolvedColor, allowEmpty);
                break;
            case "MiniPlayerBgHex":
                BindColorControlPair(SwatchMiniBgBtn, HexMiniBgBox, rawHex, resolvedColor);
                BindColorControlPair(PageSwatchMiniBgBtn, PageHexMiniBgBox, rawHex, resolvedColor);
                break;
            case "LyricHighlightColorHex":
                BindColorControlPair(SwatchLyricHighlightBtn, HexLyricHighlightBox, rawHex, resolvedColor, allowEmpty);
                BindColorControlPair(PageSwatchLyricHighlightBtn, PageHexLyricHighlightBox, rawHex, resolvedColor, allowEmpty);
                break;
            default:
                SyncAllColorBoxesAndSwatchesFromDraft();
                break;
        }
    }

    /// <summary>
    /// 实时刷新弹窗右上角 1:1 四大分区微缩仿真窗口（仅在独立主题弹窗可见时更新，并缓存壁纸位图避免重复解码）
    /// </summary>
    private void UpdateMiniPreviewWindow()
    {
        if (ThemeCenterModalOverlay.Visibility != Visibility.Visible)
            return;

        bool isLight = ThemeService.IsEffectiveLightMode(_themeDraft.BaseMode);
        var previewDraft = ThemeService.ResolveModeAdaptedProfile(_themeDraft, isLight);
        var (accent, _, _, subtle) = ViewModel.Theme.DeriveAccentColors(previewDraft);

        var defaultWinBg = isLight ? Color.FromArgb(255, 248, 250, 252) : Color.FromArgb(255, 16, 20, 29);
        var winBg = (previewDraft.BackdropType is "Solid" or "Image")
            ? ThemeService.ParseColorOrDefault(previewDraft.WindowBgColorHex, defaultWinBg)
            : defaultWinBg;

        if (previewDraft.BackdropType is "Aurora" or "CoverFlow")
        {
            var a1 = ThemeService.ParseColorOrDefault(previewDraft.AuroraColor1Hex, accent);
            var a2 = ThemeService.ParseColorOrDefault(previewDraft.AuroraColor2Hex, Color.FromArgb(255, 59, 130, 246));
            MiniPreviewWindowOuter.Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = a1, Offset = 0 },
                    new GradientStop { Color = a2, Offset = 1 }
                }
            };
        }
        else
        {
            MiniPreviewWindowOuter.Background = new SolidColorBrush(winBg);
        }

        if (previewDraft.BackdropType == "Image" && !string.IsNullOrWhiteSpace(previewDraft.BackgroundImagePath) && File.Exists(previewDraft.BackgroundImagePath))
        {
            if (!string.Equals(_cachedMiniPreviewWallPath, previewDraft.BackgroundImagePath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    MiniPreviewWallpaperImg.Source = new BitmapImage(new Uri(previewDraft.BackgroundImagePath));
                    _cachedMiniPreviewWallPath = previewDraft.BackgroundImagePath;
                }
                catch
                {
                    _cachedMiniPreviewWallPath = string.Empty;
                    MiniPreviewWallpaperImg.Visibility = Visibility.Collapsed;
                }
            }
            MiniPreviewWallpaperImg.Visibility = Visibility.Visible;
            var maskCol = ThemeService.ParseColorOrDefault(previewDraft.BackgroundMaskColorHex, Color.FromArgb(255, 9, 13, 22));
            MiniPreviewWallpaperMask.Background = ThemeService.UpdateOrCreateSectionBrush(MiniPreviewWallpaperMask.Background, maskCol, false, null, previewDraft.BackgroundMaskOpacity, 0);
            MiniPreviewWallpaperMask.Visibility = Visibility.Visible;
        }
        else
        {
            MiniPreviewWallpaperImg.Visibility = Visibility.Collapsed;
            MiniPreviewWallpaperMask.Visibility = Visibility.Collapsed;
        }

        var defaultPrimaryText = isLight ? Color.FromArgb(255, 17, 24, 39) : Color.FromArgb(255, 248, 250, 252);
        var defaultSecondaryText = isLight ? Color.FromArgb(255, 100, 116, 139) : Color.FromArgb(255, 148, 163, 184);

        // 1. 预览顶栏
        MiniPreviewLogoDot.Fill = new SolidColorBrush(accent);
        MiniPreviewThemeDot.Fill = new SolidColorBrush(accent);
        MiniPreviewTitleBar.Background = previewDraft.EnableCustomTitleBar
            ? ThemeService.CreateSectionBrush(ThemeService.ParseColorOrDefault(previewDraft.TitleBarBgHex, defaultWinBg), previewDraft.EnableTitleBarGradient, previewDraft.TitleBarGradientEndHex, previewDraft.TitleBarOpacity, previewDraft.GradientAngle)
            : new SolidColorBrush(Colors.Transparent);
        var titleTextCol = previewDraft.EnableCustomTitleBar && ThemeService.TryParseColor(previewDraft.TitleBarTextHex, out var tt) ? tt : defaultPrimaryText;
        MiniPreviewTitleText.Foreground = new SolidColorBrush(titleTextCol);

        // 2. 预览左侧导航栏
        MiniPreviewNavPane.Background = previewDraft.EnableCustomNavPane
            ? ThemeService.CreateSectionBrush(ThemeService.ParseColorOrDefault(previewDraft.NavPaneBgHex, defaultWinBg), previewDraft.EnableNavPaneGradient, previewDraft.NavPaneGradientEndHex, previewDraft.NavPaneOpacity, previewDraft.GradientAngle)
            : new SolidColorBrush(isLight ? Color.FromArgb(205, 255, 255, 255) : Color.FromArgb(110, 15, 23, 42));
        var navTextCol = previewDraft.EnableCustomNavPane && ThemeService.TryParseColor(previewDraft.NavPaneTextHex, out var nt) ? nt : defaultPrimaryText;
        var navTextBrush = new SolidColorBrush(navTextCol);
        MiniPreviewNavNormalText1.Foreground = navTextBrush;
        MiniPreviewNavNormalText2.Foreground = navTextBrush;
        MiniPreviewNavNormalText3.Foreground = navTextBrush;
        var navSelBg = previewDraft.EnableCustomNavPane && ThemeService.TryParseColor(previewDraft.NavPaneSelectedBgHex, out var nsb) ? nsb : subtle;
        MiniPreviewNavSelectedPill.Background = new SolidColorBrush(navSelBg);
        MiniPreviewNavSelectedText.Foreground = new SolidColorBrush(accent);

        // 3. 预览主内容与双卡片
        MiniPreviewContentArea.Background = previewDraft.EnableCustomContent
            ? new SolidColorBrush(ThemeService.WithOpacity(ThemeService.ParseColorOrDefault(previewDraft.ContentBgHex, defaultWinBg), previewDraft.ContentOpacity))
            : new SolidColorBrush(Colors.Transparent);

        var cardBg = previewDraft.EnableCustomContent
            ? ThemeService.WithOpacity(ThemeService.ParseColorOrDefault(previewDraft.CardBgHex, defaultWinBg), previewDraft.CardOpacity)
            : (isLight ? Color.FromArgb(228, 255, 255, 255) : Color.FromArgb(185, 30, 41, 59));
        var cardBorder = previewDraft.EnableCustomContent
            ? ThemeService.ParseColorOrDefault(previewDraft.CardBorderHex, Color.FromArgb(120, 100, 116, 139))
            : (isLight ? Color.FromArgb(255, 226, 232, 240) : Color.FromArgb(160, 51, 65, 85));
        double previewRadius = Math.Clamp(previewDraft.EnableCustomContent ? previewDraft.CardCornerRadius * 0.5 : 6, 2, 12);

        MiniPreviewCard1.Background = new SolidColorBrush(cardBg);
        MiniPreviewCard2.Background = new SolidColorBrush(cardBg);
        MiniPreviewCard1.BorderBrush = new SolidColorBrush(cardBorder);
        MiniPreviewCard2.BorderBrush = new SolidColorBrush(cardBorder);
        MiniPreviewCard1.CornerRadius = new CornerRadius(previewRadius);
        MiniPreviewCard2.CornerRadius = new CornerRadius(previewRadius);

        MiniPreviewCardBadge1.Background = new SolidColorBrush(subtle);
        MiniPreviewCardBadge2.Background = new SolidColorBrush(subtle);
        MiniPreviewCardBadgeText1.Foreground = new SolidColorBrush(accent);
        MiniPreviewCardBadgeText2.Foreground = new SolidColorBrush(accent);

        var pCol = previewDraft.EnableCustomContent && ThemeService.TryParseColor(previewDraft.PrimaryTextHex, out var ptc) ? ptc : defaultPrimaryText;
        var sCol = previewDraft.EnableCustomContent && ThemeService.TryParseColor(previewDraft.SecondaryTextHex, out var stc) ? stc : defaultSecondaryText;
        var pBrush = new SolidColorBrush(pCol);
        var sBrush = new SolidColorBrush(sCol);
        MiniPreviewPrimaryHeading.Foreground = pBrush;
        MiniPreviewCardTitle1.Foreground = pBrush;
        MiniPreviewCardTitle2.Foreground = pBrush;
        MiniPreviewCardSub1.Foreground = sBrush;
        MiniPreviewCardSub2.Foreground = sBrush;

        // 4. 预览底部播放栏（支持灵动岛边距与圆角）
        bool isIsland = string.Equals(previewDraft.PlayBarLayoutMode, "FloatingIsland", StringComparison.OrdinalIgnoreCase);
        MiniPreviewPlayBar.Margin = isIsland ? new Thickness(6, 0, 6, 3) : new Thickness(0);
        MiniPreviewPlayBar.CornerRadius = new CornerRadius(isIsland ? 6 : 0);

        MiniPreviewPlayBar.Background = previewDraft.EnableCustomPlayBar
            ? ThemeService.CreateSectionBrush(ThemeService.ParseColorOrDefault(previewDraft.PlayBarBgHex, defaultWinBg), previewDraft.EnablePlayBarGradient, previewDraft.PlayBarGradientEndHex, previewDraft.PlayBarOpacity, previewDraft.GradientAngle)
            : new SolidColorBrush(isLight ? Color.FromArgb(240, 255, 255, 255) : Color.FromArgb(225, 20, 26, 38));
        MiniPreviewPlayBar.BorderBrush = new SolidColorBrush(previewDraft.EnableCustomPlayBar
            ? ThemeService.ParseColorOrDefault(previewDraft.PlayBarBorderHex, cardBorder)
            : cardBorder);

        var playBtnCol = previewDraft.EnableCustomPlayBar && ThemeService.TryParseColor(previewDraft.PlayButtonColorHex, out var pbc) ? pbc : accent;
        var progCol = previewDraft.EnableCustomPlayBar && ThemeService.TryParseColor(previewDraft.ProgressSliderColorHex, out var prc) ? prc : accent;
        var pbTextCol = previewDraft.EnableCustomPlayBar && ThemeService.TryParseColor(previewDraft.PlayBarTextHex, out var pbt) ? pbt : defaultPrimaryText;

        MiniPreviewPlayCircle.Fill = new SolidColorBrush(playBtnCol);
        MiniPreviewProgressFill.Background = new SolidColorBrush(progCol);
        MiniPreviewPlayBarText.Foreground = new SolidColorBrush(pbTextCol);
    }

    private void PushDraftLivePreview()
    {
        UpdateMiniPreviewWindow();
        ViewModel.Theme.NotifyPreviewUpdated(_themeDraft);
    }

    // ==================== DIY 控件事件响应 ====================

    private void OnThemeDraftMetadataChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingThemeCenterUi || !_isInitialized) return;
        _themeDraft.Name = ThemeDraftNameBox.Text.Trim();
        _themeDraft.Author = ThemeDraftAuthorBox.Text.Trim();
        _themeDraft.Description = ThemeDraftDescBox.Text.Trim();

        _isLoadingThemeCenterUi = true;
        PageThemeNameBox.Text = ThemeDraftNameBox.Text;
        PageThemeAuthorBox.Text = ThemeDraftAuthorBox.Text;
        PageThemeDescBox.Text = ThemeDraftDescBox.Text;
        _isLoadingThemeCenterUi = false;
    }

    private void OnPageThemeMetadataChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingThemeCenterUi || !_isInitialized) return;
        _themeDraft.Name = PageThemeNameBox.Text.Trim();
        _themeDraft.Author = PageThemeAuthorBox.Text.Trim();
        _themeDraft.Description = PageThemeDescBox.Text.Trim();

        _isLoadingThemeCenterUi = true;
        ThemeDraftNameBox.Text = PageThemeNameBox.Text;
        ThemeDraftAuthorBox.Text = PageThemeAuthorBox.Text;
        ThemeDraftDescBox.Text = PageThemeDescBox.Text;
        _isLoadingThemeCenterUi = false;
    }

    private void OnThemeBaseModeBtnClick(object sender, RoutedEventArgs e)
    {
        if (_isLoadingThemeCenterUi || sender is not Button btn || btn.Tag is not string mode) return;
        _themeDraft.BaseMode = mode;
        SyncBaseModeAndBackdropButtons();
        SyncAllColorBoxesAndSwatchesFromDraft();
        PushDraftLivePreview();
    }

    private void OnThemeBackdropTypeBtnClick(object sender, RoutedEventArgs e)
    {
        if (_isLoadingThemeCenterUi || sender is not Button btn || btn.Tag is not string kind) return;
        bool switchingToImage = !string.Equals(_themeDraft.BackdropType, "Image", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(kind, "Image", StringComparison.OrdinalIgnoreCase);
        _themeDraft.BackdropType = kind;
        if (switchingToImage)
        {
            ApplyCrystalClearWallpaperDefaults(_themeDraft);
            LoadThemeDraftToEditorUi();
        }
        else
        {
            SyncBaseModeAndBackdropButtons();
        }
        PushDraftLivePreview();
    }

    private static void ApplyCrystalClearWallpaperDefaults(CustomThemeProfile draft)
    {
        draft.BackgroundBlur = 0.0;
        draft.BackgroundMaskOpacity = 0.0;
        draft.EnableAdaptiveWallpaperText = true;
        draft.CardVisualStyle = "LiquidGlass";
        draft.CardCornerRadius = 16.0;
        draft.ButtonCornerRadius = 12.0;
        draft.EnableCustomTitleBar = false;
        draft.EnableCustomNavPane = false;
        draft.EnableCustomContent = false;
        draft.EnableCustomPlayBar = false;
    }

    private void OnQuickAccentDotClick(object sender, RoutedEventArgs e)
    {
        if (_isLoadingThemeCenterUi || sender is not Button btn || btn.Tag is not string hex) return;
        _themeDraft.AccentColorHex = hex;
        _isLoadingThemeCenterUi = true;
        SyncAllColorBoxesAndSwatchesFromDraft();
        _isLoadingThemeCenterUi = false;
        PushDraftLivePreview();
    }

    private void OnThemeSectionSwitchToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingThemeCenterUi || !_isInitialized) return;

        _themeDraft.UseCustomAccentStates = UseCustomAccentStatesToggle.IsOn;
        _themeDraft.EnableCustomTitleBar = EnableCustomTitleBarToggle.IsOn;
        _themeDraft.EnableCustomNavPane = EnableCustomNavPaneToggle.IsOn;
        _themeDraft.EnableCustomContent = EnableCustomContentToggle.IsOn;
        _themeDraft.EnableCustomPlayBar = EnableCustomPlayBarToggle.IsOn;
        _themeDraft.SyncMiniPlayer = SyncMiniPlayerToggle.IsOn;
        _themeDraft.EnableCustomMiniPlayerBg = EnableCustomMiniBgToggle.IsOn;
        _themeDraft.SyncImmersiveLyricHighlight = SyncImmersiveLyricToggle.IsOn;
        _themeDraft.SyncDesktopLyric = SyncDesktopLyricToggle.IsOn;

        _isLoadingThemeCenterUi = true;
        PageUseCustomAccentStatesToggle.IsOn = _themeDraft.UseCustomAccentStates;
        PageEnableCustomTitleBarToggle.IsOn = _themeDraft.EnableCustomTitleBar;
        PageEnableCustomNavPaneToggle.IsOn = _themeDraft.EnableCustomNavPane;
        PageEnableCustomContentToggle.IsOn = _themeDraft.EnableCustomContent;
        PageEnableCustomPlayBarToggle.IsOn = _themeDraft.EnableCustomPlayBar;
        PageSyncMiniPlayerToggle.IsOn = _themeDraft.SyncMiniPlayer;
        PageEnableCustomMiniBgToggle.IsOn = _themeDraft.EnableCustomMiniPlayerBg;
        PageSyncImmersiveLyricToggle.IsOn = _themeDraft.SyncImmersiveLyricHighlight;
        PageSyncDesktopLyricToggle.IsOn = _themeDraft.SyncDesktopLyric;
        _isLoadingThemeCenterUi = false;

        SyncSectionCollapsibleVisibilityFromDraft();
        PushDraftLivePreview();
    }

    private void OnTaggedThemeSwitchToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingThemeCenterUi || !_isInitialized || sender is not ToggleSwitch ts || ts.Tag is not string tag) return;

        bool isOn = ts.IsOn;
        switch (tag)
        {
            case "UseCustomAccentStates": _themeDraft.UseCustomAccentStates = isOn; break;
            case "EnableAccentGradient": _themeDraft.EnableAccentGradient = isOn; break;
            case "EnableParticleOverlay": _themeDraft.EnableParticleOverlay = isOn; break;
            case "EnableAdaptiveWallpaperText":
                _themeDraft.EnableAdaptiveWallpaperText = isOn;
                if (isOn && string.Equals(_themeDraft.BackdropType, "Image", StringComparison.OrdinalIgnoreCase))
                {
                    if (_themeDraft.CardVisualStyle is null or "" or "Default" or "FrostedGlass")
                    {
                        _themeDraft.CardVisualStyle = "LiquidGlass";
                    }
                    _themeDraft.EnableCustomTitleBar = false;
                    _themeDraft.EnableCustomNavPane = false;
                    _themeDraft.EnableCustomContent = false;
                    _themeDraft.EnableCustomPlayBar = false;
                }
                break;
            case "EnableCustomTitleBar": _themeDraft.EnableCustomTitleBar = isOn; break;
            case "EnableTitleBarGradient": _themeDraft.EnableTitleBarGradient = isOn; break;
            case "EnableCustomNavPane": _themeDraft.EnableCustomNavPane = isOn; break;
            case "EnableNavPaneGradient": _themeDraft.EnableNavPaneGradient = isOn; break;
            case "EnableCustomContent": _themeDraft.EnableCustomContent = isOn; break;
            case "EnableCustomPlayBar": _themeDraft.EnableCustomPlayBar = isOn; break;
            case "EnablePlayBarGradient": _themeDraft.EnablePlayBarGradient = isOn; break;
            case "EnablePlayBarRhythmGlow": _themeDraft.EnablePlayBarRhythmGlow = isOn; break;
            case "SyncMiniPlayer": _themeDraft.SyncMiniPlayer = isOn; break;
            case "EnableCustomMiniPlayerBg": _themeDraft.EnableCustomMiniPlayerBg = isOn; break;
            case "SyncImmersiveLyricHighlight": _themeDraft.SyncImmersiveLyricHighlight = isOn; break;
            case "SyncDesktopLyric": _themeDraft.SyncDesktopLyric = isOn; break;
        }

        _isLoadingThemeCenterUi = true;
        UseCustomAccentStatesToggle.IsOn = _themeDraft.UseCustomAccentStates;
        PageUseCustomAccentStatesToggle.IsOn = _themeDraft.UseCustomAccentStates;
        ThemeEnableAccentGradToggle.IsOn = _themeDraft.EnableAccentGradient;
        PageEnableAccentGradToggle.IsOn = _themeDraft.EnableAccentGradient;
        ThemeEnableParticleToggle.IsOn = _themeDraft.EnableParticleOverlay;
        PageEnableParticleToggle.IsOn = _themeDraft.EnableParticleOverlay;
        ThemeAdaptiveWallpaperTextToggle.IsOn = _themeDraft.EnableAdaptiveWallpaperText;
        PageAdaptiveWallpaperTextToggle.IsOn = _themeDraft.EnableAdaptiveWallpaperText;

        EnableCustomTitleBarToggle.IsOn = _themeDraft.EnableCustomTitleBar;
        PageEnableCustomTitleBarToggle.IsOn = _themeDraft.EnableCustomTitleBar;
        ThemeEnableTitleBarGradToggle.IsOn = _themeDraft.EnableTitleBarGradient;
        PageEnableTitleBarGradToggle.IsOn = _themeDraft.EnableTitleBarGradient;

        EnableCustomNavPaneToggle.IsOn = _themeDraft.EnableCustomNavPane;
        PageEnableCustomNavPaneToggle.IsOn = _themeDraft.EnableCustomNavPane;
        ThemeEnableNavPaneGradToggle.IsOn = _themeDraft.EnableNavPaneGradient;
        PageEnableNavPaneGradToggle.IsOn = _themeDraft.EnableNavPaneGradient;

        EnableCustomContentToggle.IsOn = _themeDraft.EnableCustomContent;
        PageEnableCustomContentToggle.IsOn = _themeDraft.EnableCustomContent;

        EnableCustomPlayBarToggle.IsOn = _themeDraft.EnableCustomPlayBar;
        PageEnableCustomPlayBarToggle.IsOn = _themeDraft.EnableCustomPlayBar;
        ThemeEnablePlayBarGradToggle.IsOn = _themeDraft.EnablePlayBarGradient;
        PageEnablePlayBarGradToggle.IsOn = _themeDraft.EnablePlayBarGradient;
        ThemeEnableRhythmGlowToggle.IsOn = _themeDraft.EnablePlayBarRhythmGlow;
        PageEnableRhythmGlowToggle.IsOn = _themeDraft.EnablePlayBarRhythmGlow;

        SyncMiniPlayerToggle.IsOn = _themeDraft.SyncMiniPlayer;
        PageSyncMiniPlayerToggle.IsOn = _themeDraft.SyncMiniPlayer;
        EnableCustomMiniBgToggle.IsOn = _themeDraft.EnableCustomMiniPlayerBg;
        PageEnableCustomMiniBgToggle.IsOn = _themeDraft.EnableCustomMiniPlayerBg;
        SyncImmersiveLyricToggle.IsOn = _themeDraft.SyncImmersiveLyricHighlight;
        PageSyncImmersiveLyricToggle.IsOn = _themeDraft.SyncImmersiveLyricHighlight;
        SyncDesktopLyricToggle.IsOn = _themeDraft.SyncDesktopLyric;
        PageSyncDesktopLyricToggle.IsOn = _themeDraft.SyncDesktopLyric;
        _isLoadingThemeCenterUi = false;

        SyncSectionCollapsibleVisibilityFromDraft();
        PushDraftLivePreview();
    }

    private async void OnFollowCoverAccentToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingThemeCenterUi || !_isInitialized) return;
        await UpdateFollowCoverAccentSettingAsync(FollowSongCoverAccentToggle.IsOn);
    }

    private async void OnPageFollowCoverAccentToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingThemeCenterUi || !_isInitialized) return;
        await UpdateFollowCoverAccentSettingAsync(PageFollowCoverAccentToggle.IsOn);
    }

    private async Task UpdateFollowCoverAccentSettingAsync(bool enabled)
    {
        _isLoadingThemeCenterUi = true;
        FollowSongCoverAccentToggle.IsOn = enabled;
        PageFollowCoverAccentToggle.IsOn = enabled;
        _isLoadingThemeCenterUi = false;

        ViewModel.Config.Settings.ThemeCenter.FollowSongCoverAccent = enabled;
        await ViewModel.Config.SaveSettingsAsync();

        if (enabled && !string.IsNullOrWhiteSpace(ViewModel.CurrentCoverUrl))
        {
            await ApplyDynamicCoverAccentIfEnabledAsync(ViewModel.CurrentCoverUrl);
        }
    }

    private void OnThemeSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isLoadingThemeCenterUi || !_isInitialized || sender is not Slider sl) return;

        string? tag = null;
        if (ReferenceEquals(sl, ThemeWallpaperMaskOpacitySlider)) tag = "BackgroundMaskOpacity";
        else if (ReferenceEquals(sl, TitleBarOpacitySlider)) tag = "TitleBarOpacity";
        else if (ReferenceEquals(sl, NavPaneOpacitySlider)) tag = "NavPaneOpacity";
        else if (ReferenceEquals(sl, ContentOpacitySlider)) tag = "ContentOpacity";
        else if (ReferenceEquals(sl, CardOpacitySlider)) tag = "CardOpacity";
        else if (ReferenceEquals(sl, CardCornerRadiusSlider)) tag = "CardCornerRadius";
        else if (ReferenceEquals(sl, PlayBarOpacitySlider)) tag = "PlayBarOpacity";
        else if (ReferenceEquals(sl, MiniPlayerOpacitySlider)) tag = "MiniPlayerOpacity";

        if (tag != null)
        {
            ApplySingleSliderDragChange(sl, tag);
        }
    }

    private void OnThemeWallpaperBlurChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isLoadingThemeCenterUi || !_isInitialized || sender is not Slider sl) return;
        ApplySingleSliderDragChange(sl, "BackgroundBlur");
    }

    private void OnTaggedThemeSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isLoadingThemeCenterUi || !_isInitialized || sender is not Slider sl || sl.Tag is not string tag) return;
        ApplySingleSliderDragChange(sl, tag);
    }

    /// <summary>
    /// 统一处理所有 DIY 滑块拖动：先对目标精度做相等短路去重；保持当前拖动滑块 Thumb 连续顺滑不回写，
    /// 0 延迟更新配对百分比文字与镜像滑块，并将实际渲染合并到 16ms 局部快速通道
    /// </summary>
    private void ApplySingleSliderDragChange(Slider activeSlider, string tag)
    {
        double val = activeSlider.Value;
        bool changed = false;

        switch (tag)
        {
            case "BackgroundBlur":
            {
                double next = Math.Round(val);
                if (Math.Abs(_themeDraft.BackgroundBlur - next) >= 0.001) { _themeDraft.BackgroundBlur = next; changed = true; }
                break;
            }
            case "BackgroundMaskOpacity":
            {
                double next = Math.Round(val);
                if (Math.Abs(_themeDraft.BackgroundMaskOpacity - next) >= 0.001) { _themeDraft.BackgroundMaskOpacity = next; changed = true; }
                break;
            }
            case "AuroraSpeed":
            {
                double next = Math.Round(val / 100.0, 2);
                if (Math.Abs(_themeDraft.AuroraSpeed - next) >= 0.001) { _themeDraft.AuroraSpeed = next; changed = true; }
                break;
            }
            case "AuroraIntensity":
            {
                double next = Math.Round(val);
                if (Math.Abs(_themeDraft.AuroraIntensity - next) >= 0.001) { _themeDraft.AuroraIntensity = next; changed = true; }
                break;
            }
            case "ParticleDensity":
            {
                int next = (int)Math.Round(val);
                if (_themeDraft.ParticleDensity != next) { _themeDraft.ParticleDensity = next; changed = true; }
                break;
            }
            case "ParticleSpeed":
            {
                double next = Math.Round(val / 100.0, 2);
                if (Math.Abs(_themeDraft.ParticleSpeed - next) >= 0.001) { _themeDraft.ParticleSpeed = next; changed = true; }
                break;
            }
            case "TitleBarOpacity":
            {
                double next = Math.Round(val);
                if (Math.Abs(_themeDraft.TitleBarOpacity - next) >= 0.001) { _themeDraft.TitleBarOpacity = next; changed = true; }
                break;
            }
            case "NavPaneOpacity":
            {
                double next = Math.Round(val);
                if (Math.Abs(_themeDraft.NavPaneOpacity - next) >= 0.001) { _themeDraft.NavPaneOpacity = next; changed = true; }
                break;
            }
            case "ContentOpacity":
            {
                double next = Math.Round(val);
                if (Math.Abs(_themeDraft.ContentOpacity - next) >= 0.001) { _themeDraft.ContentOpacity = next; changed = true; }
                break;
            }
            case "CardOpacity":
            {
                double next = Math.Round(val);
                if (Math.Abs(_themeDraft.CardOpacity - next) >= 0.001) { _themeDraft.CardOpacity = next; changed = true; }
                break;
            }
            case "CardCornerRadius":
            {
                double next = Math.Round(val);
                if (Math.Abs(_themeDraft.CardCornerRadius - next) >= 0.001) { _themeDraft.CardCornerRadius = next; changed = true; }
                break;
            }
            case "PlayBarOpacity":
            {
                double next = Math.Round(val);
                if (Math.Abs(_themeDraft.PlayBarOpacity - next) >= 0.001) { _themeDraft.PlayBarOpacity = next; changed = true; }
                break;
            }
            case "MiniPlayerOpacity":
            {
                double next = Math.Round(val);
                if (Math.Abs(_themeDraft.MiniPlayerOpacity - next) >= 0.001) { _themeDraft.MiniPlayerOpacity = next; changed = true; }
                break;
            }
            case "ButtonCornerRadius":
            {
                double next = Math.Round(val);
                if (Math.Abs(_themeDraft.ButtonCornerRadius - next) >= 0.001) { _themeDraft.ButtonCornerRadius = next; changed = true; }
                break;
            }
        }

        if (!changed)
            return;

        SyncSingleSliderLabelAndMirror(tag, activeSlider);
        QueueFastDraftPreview(tag);
    }

    private void SyncSingleSliderLabelAndMirror(string tag, Slider activeSlider)
    {
        _isLoadingThemeCenterUi = true;
        void SetMirror(Slider target, double v)
        {
            if (!ReferenceEquals(target, activeSlider)) target.Value = v;
        }

        switch (tag)
        {
            case "BackgroundBlur":
                SetMirror(ThemeWallpaperBlurSlider, _themeDraft.BackgroundBlur);
                SetMirror(PageWallpaperBlurSlider, _themeDraft.BackgroundBlur);
                ThemeWallpaperBlurText.Text = PageWallpaperBlurText.Text = $"{_themeDraft.BackgroundBlur:0}px";
                break;
            case "BackgroundMaskOpacity":
                SetMirror(ThemeWallpaperMaskOpacitySlider, _themeDraft.BackgroundMaskOpacity);
                SetMirror(PageWallpaperMaskOpacitySlider, _themeDraft.BackgroundMaskOpacity);
                SetMirror(PageVideoMaskOpacitySlider, _themeDraft.BackgroundMaskOpacity);
                ThemeWallpaperMaskOpacityText.Text = PageWallpaperMaskOpacityText.Text = PageVideoMaskOpacityText.Text = $"{_themeDraft.BackgroundMaskOpacity:0}%";
                break;
            case "AuroraSpeed":
            {
                double s = Math.Clamp(_themeDraft.AuroraSpeed * 100, 20, 300);
                SetMirror(ThemeAuroraSpeedSlider, s);
                SetMirror(PageAuroraSpeedSlider, s);
                ThemeAuroraSpeedText.Text = PageAuroraSpeedText.Text = $"{_themeDraft.AuroraSpeed:0.0}x";
                break;
            }
            case "AuroraIntensity":
                SetMirror(ThemeAuroraIntensitySlider, _themeDraft.AuroraIntensity);
                SetMirror(PageAuroraIntensitySlider, _themeDraft.AuroraIntensity);
                ThemeAuroraIntensityText.Text = PageAuroraIntensityText.Text = $"{_themeDraft.AuroraIntensity:0}%";
                break;
            case "ParticleDensity":
                SetMirror(ThemeParticleDensitySlider, _themeDraft.ParticleDensity);
                SetMirror(PageParticleDensitySlider, _themeDraft.ParticleDensity);
                ThemeParticleDensityText.Text = PageParticleDensityText.Text = $"{_themeDraft.ParticleDensity:0}";
                break;
            case "ParticleSpeed":
            {
                double s = Math.Clamp(_themeDraft.ParticleSpeed * 100, 30, 250);
                SetMirror(ThemeParticleSpeedSlider, s);
                SetMirror(PageParticleSpeedSlider, s);
                ThemeParticleSpeedText.Text = PageParticleSpeedText.Text = $"{_themeDraft.ParticleSpeed:0.0}x";
                break;
            }
            case "TitleBarOpacity":
                SetMirror(TitleBarOpacitySlider, _themeDraft.TitleBarOpacity);
                SetMirror(PageTitleBarOpacitySlider, _themeDraft.TitleBarOpacity);
                TitleBarOpacityText.Text = PageTitleBarOpacityText.Text = $"{_themeDraft.TitleBarOpacity:0}%";
                break;
            case "NavPaneOpacity":
                SetMirror(NavPaneOpacitySlider, _themeDraft.NavPaneOpacity);
                SetMirror(PageNavPaneOpacitySlider, _themeDraft.NavPaneOpacity);
                NavPaneOpacityText.Text = PageNavPaneOpacityText.Text = $"{_themeDraft.NavPaneOpacity:0}%";
                break;
            case "ContentOpacity":
                SetMirror(ContentOpacitySlider, _themeDraft.ContentOpacity);
                SetMirror(PageContentOpacitySlider, _themeDraft.ContentOpacity);
                ContentOpacityText.Text = PageContentOpacityText.Text = $"{_themeDraft.ContentOpacity:0}%";
                break;
            case "CardOpacity":
                SetMirror(CardOpacitySlider, _themeDraft.CardOpacity);
                SetMirror(PageCardOpacitySlider, _themeDraft.CardOpacity);
                CardOpacityText.Text = PageCardOpacityText.Text = $"{_themeDraft.CardOpacity:0}%";
                break;
            case "CardCornerRadius":
                SetMirror(CardCornerRadiusSlider, _themeDraft.CardCornerRadius);
                SetMirror(PageCardCornerRadiusSlider, _themeDraft.CardCornerRadius);
                CardCornerRadiusText.Text = PageCardCornerRadiusText.Text = $"{_themeDraft.CardCornerRadius:0}px";
                break;
            case "PlayBarOpacity":
                SetMirror(PlayBarOpacitySlider, _themeDraft.PlayBarOpacity);
                SetMirror(PagePlayBarOpacitySlider, _themeDraft.PlayBarOpacity);
                PlayBarOpacityText.Text = PagePlayBarOpacityText.Text = $"{_themeDraft.PlayBarOpacity:0}%";
                break;
            case "MiniPlayerOpacity":
                SetMirror(MiniPlayerOpacitySlider, _themeDraft.MiniPlayerOpacity);
                SetMirror(PageMiniPlayerOpacitySlider, _themeDraft.MiniPlayerOpacity);
                break;
            case "ButtonCornerRadius":
                SetMirror(ThemeButtonRadiusSlider, _themeDraft.ButtonCornerRadius);
                SetMirror(PageButtonRadiusSlider, _themeDraft.ButtonCornerRadius);
                ThemeButtonRadiusText.Text = PageButtonRadiusText.Text = $"{_themeDraft.ButtonCornerRadius:0}px";
                break;
        }
        _isLoadingThemeCenterUi = false;
    }

    private void QueueFastDraftPreview(string changedKey)
    {
        ViewModel.Theme.SetPreviewDraft(_themeDraft, updateAccentBrushes: false);

        if (changedKey == "BackgroundBlur")
        {
            UpdateMiniPreviewWindow();
            if (_diyBlurPreviewTimer != null && !_diyBlurPreviewTimer.IsRunning)
            {
                _diyBlurPreviewTimer.Start();
            }
            return;
        }

        _pendingFastPreviewKeys.Add(changedKey);
        if (!_isDiyVsyncPending)
        {
            _isDiyVsyncPending = true;
            CompositionTarget.Rendering += OnDiyFastPreviewVsyncTick;
        }
    }

    private void OnDiyFastPreviewVsyncTick(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnDiyFastPreviewVsyncTick;
        _isDiyVsyncPending = false;
        FlushPendingFastPreviews();
    }

    /// <summary>
    /// 原生 VSync 高刷帧合并局部快速刷新（自动匹配 60Hz~240Hz+ 屏幕刷新率）：按实际变动的 DIY 字段原地更新对应分区画刷，
    /// 彻底绕开 RefreshRootElementTheme 全窗口主题双重翻转与 RebuildThemeLibraryCardsUi 主题卡片树销毁重建
    /// </summary>
    private void FlushPendingFastPreviews()
    {
        if (_pendingFastPreviewKeys.Count == 0)
            return;

        bool isLight = ThemeService.IsEffectiveLightMode(_themeDraft.BaseMode);
        var adapted = ThemeService.ResolveModeAdaptedProfile(_themeDraft, isLight);
        var (accent, hover, pressed, _) = ViewModel.Theme.DeriveAccentColors(adapted);

        string badgeText = $"实时预览中：{adapted.Name}";
        if (PageActiveThemeNameBadge != null && PageActiveThemeNameBadge.Text != badgeText) PageActiveThemeNameBadge.Text = badgeText;
        if (ThemeModalActiveNameBadge != null && ThemeModalActiveNameBadge.Text != badgeText) ThemeModalActiveNameBadge.Text = badgeText;

        UpdateMiniPreviewWindow();

        bool doAccent = false, doTitleBar = false, doNavPane = false, doContentCards = false;
        bool doPlayBar = false, doWindowBg = false, doMask = false, doAurora = false;
        bool doParticles = false, doCardRadius = false, doBtnRadius = false, doMini = false, doLyric = false;

        foreach (var k in _pendingFastPreviewKeys)
        {
            switch (k)
            {
                case "AccentColorHex" or "AccentGradientEndHex" or "AccentHoverHex" or "AccentPressedHex":
                    doAccent = true; break;
                case "TitleBarOpacity" or "TitleBarBgHex" or "TitleBarGradientEndHex" or "TitleBarTextHex":
                    doTitleBar = true; break;
                case "NavPaneOpacity" or "NavPaneBgHex" or "NavPaneGradientEndHex" or "NavPaneTextHex" or "NavPaneSelectedBgHex":
                    doNavPane = true; break;
                case "ContentOpacity" or "CardOpacity" or "ContentBgHex" or "CardBgHex" or "CardBorderHex" or "PrimaryTextHex" or "SecondaryTextHex":
                    doContentCards = true; break;
                case "PlayBarOpacity" or "PlayBarBgHex" or "PlayBarGradientEndHex" or "PlayBarBorderHex" or "PlayBarTextHex" or "PlayButtonColorHex" or "ProgressSliderColorHex":
                    doPlayBar = true; break;
                case "WindowBgColorHex":
                    doWindowBg = true; break;
                case "BackgroundMaskOpacity" or "BackgroundMaskColorHex":
                    doMask = true; break;
                case "AuroraSpeed" or "AuroraIntensity" or "AuroraColor1Hex" or "AuroraColor2Hex" or "AuroraColor3Hex":
                    doAurora = true; break;
                case "ParticleSpeed" or "ParticleDensity" or "ParticleColorHex":
                    doParticles = true; break;
                case "CardCornerRadius":
                    doCardRadius = true; break;
                case "ButtonCornerRadius":
                    doBtnRadius = true; break;
                case "MiniPlayerOpacity" or "MiniPlayerBgHex":
                    doMini = true; break;
                case "LyricHighlightColorHex":
                    doLyric = true; break;
            }
        }
        _pendingFastPreviewKeys.Clear();

        if (doAccent)
        {
            ViewModel.Theme.UpdateGlobalAccentBrushes(adapted);
            ApplySystemControlAndHeroThemeResources(adapted, accent, hover, pressed, isLight);
            ApplyCardVisualStyleOverlay(adapted, accent, isLight);
            ApplyNavigationPaneSectionTheme(adapted, accent, isLight);
            if (!adapted.EnableCustomTitleBar) ApplyTitleBarSectionTheme(adapted, accent, isLight);
            if (!adapted.EnableCustomPlayBar)
            {
                ApplySliderAccentResources(SeekSlider, ThemeService.EnsureReadableButtonFill(accent, isLight), hover, pressed);
                ApplySliderAccentResources(VolumeSlider, ThemeService.EnsureReadableButtonFill(accent, isLight), hover, pressed);
                ApplySliderAccentResources(SimplePlayBarSeekSlider, ThemeService.EnsureReadableButtonFill(accent, isLight), hover, pressed);
                ApplySliderAccentResources(SimplePlayBarVolumeSlider, ThemeService.EnsureReadableButtonFill(accent, isLight), hover, pressed);
            }
            if (adapted.EnableParticleOverlay && string.IsNullOrWhiteSpace(adapted.ParticleColorHex))
            {
                RebuildParticlePoolIfNeeded(adapted, accent, isLight);
            }
            SyncLyricLinesTypographyAndEmptyState();
            _miniPlayerWindow?.ApplyThemeProfile(_themeDraft);
            _desktopLyricWindow?.ApplyConfigToVisuals();
        }

        if (doWindowBg)
        {
            var fallbackBg = isLight ? Color.FromArgb(255, 248, 250, 252) : Color.FromArgb(255, 15, 23, 42);
            var winBg = ThemeService.ParseColorOrDefault(adapted.WindowBgColorHex, fallbackBg);
            GlobalWindowBaseBgLayer.Background = ThemeService.UpdateOrCreateSectionBrush(GlobalWindowBaseBgLayer.Background, winBg, false, null, 100, 0);
        }

        if (doMask)
        {
            var maskBase = ThemeService.ParseColorOrDefault(adapted.BackgroundMaskColorHex, Color.FromArgb(255, 9, 13, 22));
            GlobalWallpaperMaskBorder.Background = ThemeService.UpdateOrCreateSectionBrush(GlobalWallpaperMaskBorder.Background, maskBase, false, null, adapted.BackgroundMaskOpacity, 0);
            _activeAdaptiveRenderedProfile = adapted;
            ScheduleAdaptiveWallpaperTextPass(immediate: false);
        }

        if (doAurora)
        {
            if (adapted.BackdropType == "Aurora")
            {
                _curAuroraC1 = ThemeService.ParseColorOrDefault(adapted.AuroraColor1Hex, accent);
                _curAuroraC2 = ThemeService.ParseColorOrDefault(adapted.AuroraColor2Hex, Color.FromArgb(255, 99, 102, 241));
                _curAuroraC3 = ThemeService.ParseColorOrDefault(adapted.AuroraColor3Hex, Color.FromArgb(255, 236, 72, 153));
            }
            ApplyAuroraOrbColors(adapted.AuroraIntensity, isLight);
            UpdateDynamicEnginePowerState();
        }

        if (doParticles)
        {
            RebuildParticlePoolIfNeeded(adapted, accent, isLight);
            UpdateDynamicEnginePowerState();
        }

        if (doTitleBar)
        {
            ApplyTitleBarSectionTheme(adapted, accent, isLight);
            ConfigureTitleBarButtonsTheme();
        }

        if (doNavPane)
        {
            ApplyNavigationPaneSectionTheme(adapted, accent, isLight);
        }

        if (doContentCards)
        {
            RootLayout.Resources.TryGetValue("CardBackgroundFillColorDefaultBrush", out var prevCardBrush);
            ApplyContentAndCardsSectionTheme(adapted, isLight);
            RootLayout.Resources.TryGetValue("CardBackgroundFillColorDefaultBrush", out var nextCardBrush);
            // 仅当卡片主画刷跨越 96% 阈值发生类型切换（AcrylicBrush <-> SolidColorBrush）时才触发一次资源树重绑
            if (!ReferenceEquals(prevCardBrush, nextCardBrush))
            {
                ElementTheme targetTheme = adapted.BaseMode switch
                {
                    "Light" => ElementTheme.Light,
                    "System" => ElementTheme.Default,
                    _ => ElementTheme.Dark
                };
                RefreshRootElementTheme(targetTheme);
            }
        }

        if (doPlayBar)
        {
            ViewModel.Theme.UpdateGlobalAccentBrushes(adapted);
            ApplyBottomPlayBarSectionTheme(adapted, accent, hover, pressed, isLight);
        }

        if (doCardRadius)
        {
            double cardRadius = Math.Clamp(adapted.CardCornerRadius, 4, 24);
            RootLayout.Resources["OverlayCornerRadius"] = new CornerRadius(cardRadius);
            ApplyCustomCornerRadiusToVisibleCards(MainContentWorkspaceGrid, new CornerRadius(cardRadius));
            if (IsAdaptiveWallpaperTextActive)
            {
                ScheduleAdaptiveWallpaperTextPass(immediate: false);
            }
        }

        if (doBtnRadius)
        {
            double btnRadius = Math.Clamp(adapted.ButtonCornerRadius, 4, 22);
            var cr = new CornerRadius(btnRadius);
            RootLayout.Resources["ControlCornerRadius"] = cr;
            if (FmPlayBtn != null) FmPlayBtn.CornerRadius = cr;
            ApplyButtonCornerRadiusInSubtree(SettingsThemePanel.Visibility == Visibility.Visible ? SettingsThemePanel : ThemeCenterModalCard, cr);
            if (IsAdaptiveWallpaperTextActive)
            {
                ScheduleAdaptiveWallpaperTextPass(immediate: false);
            }
        }

        if (doMini)
        {
            _miniPlayerWindow?.ApplyThemeProfile(_themeDraft);
        }

        if (doLyric)
        {
            SyncLyricLinesTypographyAndEmptyState();
            _desktopLyricWindow?.ApplyConfigToVisuals();
        }
    }

    private static void ApplyButtonCornerRadiusInSubtree(DependencyObject? root, CornerRadius cr)
    {
        if (root == null) return;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button btn && btn.ActualHeight > 20 && Math.Abs(btn.ActualWidth - btn.ActualHeight) > 4)
            {
                // 跳过固定大胶囊筛选签（16/18）以外的标准圆角控制按钮
                if (btn.CornerRadius.TopLeft is >= 4 and <= 22 && btn.ReadLocalValue(Control.CornerRadiusProperty) == DependencyProperty.UnsetValue)
                {
                    // 仅对依赖样式 ControlCornerRadius 的按钮通过轻量重绑样式使其圆角即时更新
                    var s = btn.Style;
                    if (s != null)
                    {
                        btn.Style = null;
                        btn.Style = s;
                    }
                }
            }
            ApplyButtonCornerRadiusInSubtree(child, cr);
        }
    }

    // 形态/质感/粒子/角度按钮点击事件
    private void OnParticleTypeBtnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _themeDraft.ParticleType = tag;
            SyncOptionButtonsActiveState();
            PushDraftLivePreview();
        }
    }

    private void OnGradientAngleBtnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag && double.TryParse(tag, out double angle))
        {
            _themeDraft.GradientAngle = angle;
            SyncOptionButtonsActiveState();
            PushDraftLivePreview();
        }
    }

    private void OnPlayBarLayoutBtnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _themeDraft.PlayBarLayoutMode = tag;
            SyncOptionButtonsActiveState();
            PushDraftLivePreview();
        }
    }

    private void OnCardVisualStyleBtnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _themeDraft.CardVisualStyle = tag;
            SyncOptionButtonsActiveState();
            PushDraftLivePreview();
        }
    }

    private void OnCoverArtShapeBtnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _themeDraft.CoverArtShape = tag;
            SyncOptionButtonsActiveState();
            PushDraftLivePreview();
        }
    }

    private void OnQuickFontFamilyBtnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _themeDraft.CustomFontFamily = tag;
            _isLoadingThemeCenterUi = true;
            ThemeCustomFontBox.Text = tag;
            PageCustomFontBox.Text = tag;
            _isLoadingThemeCenterUi = false;
            SyncOptionButtonsActiveState();
            PushDraftLivePreview();
        }
    }

    private void OnCustomFontBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && !_isLoadingThemeCenterUi)
        {
            _themeDraft.CustomFontFamily = tb.Text.Trim();
            SyncOptionButtonsActiveState();
            PushDraftLivePreview();
        }
    }

    private void OnCustomFontBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && sender is TextBox tb)
        {
            _themeDraft.CustomFontFamily = tb.Text.Trim();
            SyncOptionButtonsActiveState();
            PushDraftLivePreview();
        }
    }

    // ==================== 共享光谱调色盘 & Hex 输入框交互 ====================

    private void OnThemeColorSwatchBtnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string fieldKey) return;
        _activeColorFieldKey = fieldKey;

        string currentHex = GetDraftColorHexByKey(fieldKey);
        var currentCol = ThemeService.ParseColorOrDefault(currentHex,
            (btn.Background as SolidColorBrush)?.Color ?? Color.FromArgb(255, 16, 185, 129));

        _isUpdatingSharedColorPicker = true;
        SharedThemeColorPicker.RequestedTheme = RootLayout.ActualTheme;
        SharedThemeColorPicker.Color = currentCol;
        _isUpdatingSharedColorPicker = false;

        SharedColorPickerTitleText.Text = $"正在调色：{fieldKey}";
        SharedColorPickerFlyout.ShowAt(btn);
    }

    private void OnSharedColorPickerChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_isUpdatingSharedColorPicker || _isLoadingThemeCenterUi || string.IsNullOrEmpty(_activeColorFieldKey))
            return;

        string hex = ThemeService.ToHex(args.NewColor);
        if (string.Equals(GetDraftColorHexByKey(_activeColorFieldKey), hex, StringComparison.OrdinalIgnoreCase))
            return;

        SetDraftColorHexByKey(_activeColorFieldKey, hex);

        _isLoadingThemeCenterUi = true;
        SyncSingleColorControlByKey(_activeColorFieldKey, args.NewColor, hex);
        _isLoadingThemeCenterUi = false;

        QueueFastDraftPreview(_activeColorFieldKey);
    }

    private void OnThemeHexBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && sender is TextBox tb)
        {
            ApplyHexTextBoxValue(tb);
        }
    }

    private void OnThemeHexBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            ApplyHexTextBoxValue(tb);
        }
    }

    private void ApplyHexTextBoxValue(TextBox tb)
    {
        if (_isLoadingThemeCenterUi || tb.Tag is not string key) return;
        string raw = tb.Text.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            if (IsOptionalColorKey(key))
            {
                SetDraftColorHexByKey(key, string.Empty);
                _isLoadingThemeCenterUi = true;
                SyncAllColorBoxesAndSwatchesFromDraft();
                _isLoadingThemeCenterUi = false;
                PushDraftLivePreview();
            }
            return;
        }

        if (!raw.StartsWith('#')) raw = "#" + raw;
        if (ThemeService.TryParseColor(raw, out var parsed))
        {
            string normalized = ThemeService.ToHex(parsed);
            SetDraftColorHexByKey(key, normalized);
            _isLoadingThemeCenterUi = true;
            SyncAllColorBoxesAndSwatchesFromDraft();
            _isLoadingThemeCenterUi = false;
            PushDraftLivePreview();
        }
    }

    private void OnClearOptionalColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string key) return;
        SetDraftColorHexByKey(key, string.Empty);
        _isLoadingThemeCenterUi = true;
        SyncAllColorBoxesAndSwatchesFromDraft();
        _isLoadingThemeCenterUi = false;
        PushDraftLivePreview();
    }

    private static bool IsOptionalColorKey(string key) => key is
        "TitleBarTextHex" or "NavPaneTextHex" or "NavPaneSelectedBgHex" or
        "PrimaryTextHex" or "SecondaryTextHex" or
        "PlayBarTextHex" or "PlayButtonColorHex" or "ProgressSliderColorHex" or
        "LyricHighlightColorHex" or "ParticleColorHex";

    private string GetDraftColorHexByKey(string key) => key switch
    {
        "WindowBgColorHex" => _themeDraft.WindowBgColorHex,
        "BackgroundMaskColorHex" => _themeDraft.BackgroundMaskColorHex,
        "AuroraColor1Hex" => _themeDraft.AuroraColor1Hex,
        "AuroraColor2Hex" => _themeDraft.AuroraColor2Hex,
        "AuroraColor3Hex" => _themeDraft.AuroraColor3Hex,
        "ParticleColorHex" => _themeDraft.ParticleColorHex,
        "AccentColorHex" => _themeDraft.AccentColorHex,
        "AccentGradientEndHex" => _themeDraft.AccentGradientEndHex,
        "AccentHoverHex" => _themeDraft.AccentHoverHex,
        "AccentPressedHex" => _themeDraft.AccentPressedHex,
        "TitleBarBgHex" => _themeDraft.TitleBarBgHex,
        "TitleBarGradientEndHex" => _themeDraft.TitleBarGradientEndHex,
        "TitleBarTextHex" => _themeDraft.TitleBarTextHex,
        "NavPaneBgHex" => _themeDraft.NavPaneBgHex,
        "NavPaneGradientEndHex" => _themeDraft.NavPaneGradientEndHex,
        "NavPaneTextHex" => _themeDraft.NavPaneTextHex,
        "NavPaneSelectedBgHex" => _themeDraft.NavPaneSelectedBgHex,
        "ContentBgHex" => _themeDraft.ContentBgHex,
        "CardBgHex" => _themeDraft.CardBgHex,
        "CardBorderHex" => _themeDraft.CardBorderHex,
        "PrimaryTextHex" => _themeDraft.PrimaryTextHex,
        "SecondaryTextHex" => _themeDraft.SecondaryTextHex,
        "PlayBarBgHex" => _themeDraft.PlayBarBgHex,
        "PlayBarGradientEndHex" => _themeDraft.PlayBarGradientEndHex,
        "PlayBarBorderHex" => _themeDraft.PlayBarBorderHex,
        "PlayBarTextHex" => _themeDraft.PlayBarTextHex,
        "PlayButtonColorHex" => _themeDraft.PlayButtonColorHex,
        "ProgressSliderColorHex" => _themeDraft.ProgressSliderColorHex,
        "MiniPlayerBgHex" => _themeDraft.MiniPlayerBgHex,
        "LyricHighlightColorHex" => _themeDraft.LyricHighlightColorHex,
        _ => string.Empty
    };

    private void SetDraftColorHexByKey(string key, string val)
    {
        switch (key)
        {
            case "WindowBgColorHex": _themeDraft.WindowBgColorHex = val; break;
            case "BackgroundMaskColorHex": _themeDraft.BackgroundMaskColorHex = val; break;
            case "AuroraColor1Hex": _themeDraft.AuroraColor1Hex = val; break;
            case "AuroraColor2Hex": _themeDraft.AuroraColor2Hex = val; break;
            case "AuroraColor3Hex": _themeDraft.AuroraColor3Hex = val; break;
            case "ParticleColorHex": _themeDraft.ParticleColorHex = val; break;
            case "AccentColorHex": _themeDraft.AccentColorHex = val; break;
            case "AccentGradientEndHex": _themeDraft.AccentGradientEndHex = val; break;
            case "AccentHoverHex": _themeDraft.AccentHoverHex = val; break;
            case "AccentPressedHex": _themeDraft.AccentPressedHex = val; break;
            case "TitleBarBgHex": _themeDraft.TitleBarBgHex = val; break;
            case "TitleBarGradientEndHex": _themeDraft.TitleBarGradientEndHex = val; break;
            case "TitleBarTextHex": _themeDraft.TitleBarTextHex = val; break;
            case "NavPaneBgHex": _themeDraft.NavPaneBgHex = val; break;
            case "NavPaneGradientEndHex": _themeDraft.NavPaneGradientEndHex = val; break;
            case "NavPaneTextHex": _themeDraft.NavPaneTextHex = val; break;
            case "NavPaneSelectedBgHex": _themeDraft.NavPaneSelectedBgHex = val; break;
            case "ContentBgHex": _themeDraft.ContentBgHex = val; break;
            case "CardBgHex": _themeDraft.CardBgHex = val; break;
            case "CardBorderHex": _themeDraft.CardBorderHex = val; break;
            case "PrimaryTextHex": _themeDraft.PrimaryTextHex = val; break;
            case "SecondaryTextHex": _themeDraft.SecondaryTextHex = val; break;
            case "PlayBarBgHex": _themeDraft.PlayBarBgHex = val; break;
            case "PlayBarGradientEndHex": _themeDraft.PlayBarGradientEndHex = val; break;
            case "PlayBarBorderHex": _themeDraft.PlayBarBorderHex = val; break;
            case "PlayBarTextHex": _themeDraft.PlayBarTextHex = val; break;
            case "PlayButtonColorHex": _themeDraft.PlayButtonColorHex = val; break;
            case "ProgressSliderColorHex": _themeDraft.ProgressSliderColorHex = val; break;
            case "MiniPlayerBgHex": _themeDraft.MiniPlayerBgHex = val; break;
            case "LyricHighlightColorHex": _themeDraft.LyricHighlightColorHex = val; break;
        }
    }

    // ==================== 新建 / 克隆 / 保存 / 重置 / 壁纸选择 / 动态视频 / 智能取色 / 导入导出 ====================

    private void OnCreateNewBlankThemeClick(object sender, RoutedEventArgs e)
    {
        _themeDraft = new CustomThemeProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"我的主题 {ViewModel.Config.Settings.ThemeCenter.CustomThemes.Count + 1}",
            Author = "我",
            Description = "全新个性化 DIY 主题",
            IsBuiltIn = false,
            BaseMode = "Dark",
            BackdropType = "Aurora",
            AccentColorHex = "#10B981",
            EnableAccentGradient = true,
            AccentGradientEndHex = "#06B6D4",
            EnableCustomTitleBar = true,
            EnableCustomNavPane = true,
            EnableCustomContent = true,
            EnableCustomPlayBar = true,
            PlayBarLayoutMode = "FloatingIsland",
            CardVisualStyle = "FrostedGlass",
            EnablePlayBarRhythmGlow = true
        };
        LoadThemeDraftToEditorUi();
        PushDraftLivePreview();
        ViewModel.StatusToast = "已创建新主题草稿，DIY 调整后点击保存即可";
    }

    private void OnCloneCurrentThemeClick(object sender, RoutedEventArgs e)
    {
        _themeDraft = _themeDraft.Clone(asNewCustom: true);
        LoadThemeDraftToEditorUi();
        PushDraftLivePreview();
        ViewModel.StatusToast = $"已克隆为新草稿：{_themeDraft.Name}";
    }

    private async void OnSaveDraftAsCustomThemeClick(object sender, RoutedEventArgs e)
    {
        if (_themeDraft.IsBuiltIn)
        {
            _themeDraft.Id = Guid.NewGuid().ToString("N");
            _themeDraft.IsBuiltIn = false;
            if (CustomThemeProfile.GetBuiltInPresets().Any(p => p.Name == _themeDraft.Name))
            {
                _themeDraft.Name = $"{_themeDraft.Name} (DIY)";
            }
            if (_themeDraft.Author == "Official")
            {
                _themeDraft.Author = "我";
            }
        }

        var saved = await ViewModel.Theme.SaveCustomThemeAsync(_themeDraft);
        _themeDraft = saved.Clone();
        RebuildThemeLibraryCardsUi();
        LoadThemeDraftToEditorUi();
        ViewModel.StatusToast = $"已保存并激活自制主题：{saved.Name}";
    }

    private void OnResetThemeDraftClick(object sender, RoutedEventArgs e)
    {
        ViewModel.Theme.RevertPreview();
        _themeDraft = ViewModel.Theme.GetSavedActiveTheme().Clone();
        LoadThemeDraftToEditorUi();
        ViewModel.StatusToast = "已恢复为当前已保存主题";
    }

    private async void OnPickThemeWallpaperClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, _hWnd);
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".webp");
            picker.FileTypeFilter.Add(".bmp");

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            string localPath = await ViewModel.Theme.CopyWallpaperToAppDataAsync(file.Path);
            _themeDraft.BackgroundImagePath = localPath;
            _themeDraft.BackdropType = "Image";
            ApplyCrystalClearWallpaperDefaults(_themeDraft);

            LoadThemeDraftToEditorUi();
            PushDraftLivePreview();
            ViewModel.StatusToast = $"已加载原画全透明壁纸：{file.Name}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"选择壁纸失败：{ex.Message}";
        }
    }

    private void OnClearThemeWallpaperClick(object sender, RoutedEventArgs e)
    {
        _themeDraft.BackgroundImagePath = string.Empty;
        if (_themeDraft.BackdropType == "Image")
        {
            _themeDraft.BackdropType = "Mica";
        }
        LoadThemeDraftToEditorUi();
        PushDraftLivePreview();
    }

    private async void OnPickThemeVideoWallpaperClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, _hWnd);
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".webm");
            picker.FileTypeFilter.Add(".mkv");

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            string localPath = await ViewModel.Theme.CopyWallpaperToAppDataAsync(file.Path);
            _themeDraft.BackgroundVideoPath = localPath;
            _themeDraft.BackdropType = "Video";

            LoadThemeDraftToEditorUi();
            PushDraftLivePreview();
            ViewModel.StatusToast = $"已加载动态视频壁纸：{file.Name}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"选择视频壁纸失败：{ex.Message}";
        }
    }

    private void OnClearThemeVideoWallpaperClick(object sender, RoutedEventArgs e)
    {
        _themeDraft.BackgroundVideoPath = string.Empty;
        if (_themeDraft.BackdropType == "Video")
        {
            _themeDraft.BackdropType = "Mica";
        }
        LoadThemeDraftToEditorUi();
        PushDraftLivePreview();
    }

    private async void OnExtractThemeFromCurrentSongClick(object sender, RoutedEventArgs e)
    {
        string coverUrl = ViewModel.Player.CurrentSong?.PicUrl ?? ViewModel.CurrentCoverUrl;
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            ViewModel.StatusToast = "当前没有正在播放或带封面的歌曲，请先播放一首歌曲";
            return;
        }

        string songTitle = ViewModel.Player.CurrentSong?.Name ?? "封面灵感";
        var generated = await ViewModel.Theme.ExtractThemeFromImageAsync(coverUrl, $"封面 · {songTitle}", useAsWallpaper: false);
        if (generated == null)
        {
            ViewModel.StatusToast = "无法读取当前歌曲封面图像，请稍后再试";
            return;
        }

        _themeDraft = generated;
        LoadThemeDraftToEditorUi();
        PushDraftLivePreview();
        ViewModel.StatusToast = $"已从《{songTitle}》封面智能生成主题草稿，点击保存即可珍藏！";
    }

    private async void OnExtractThemeFromWallpaperClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, _hWnd);
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".webp");
            picker.FileTypeFilter.Add(".bmp");

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            string baseName = Path.GetFileNameWithoutExtension(file.Name);
            var generated = await ViewModel.Theme.ExtractThemeFromImageAsync(file.Path, $"壁纸 · {baseName}", useAsWallpaper: true);
            if (generated == null)
            {
                ViewModel.StatusToast = "解析壁纸色彩失败";
                return;
            }

            _themeDraft = generated;
            LoadThemeDraftToEditorUi();
            PushDraftLivePreview();
            ViewModel.StatusToast = $"已根据壁纸《{baseName}》自动生成全区协调主题！";
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"壁纸生成主题失败：{ex.Message}";
        }
    }

    private async void OnImportThemeFileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, _hWnd);
            picker.FileTypeFilter.Add(".json");

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            var imported = await ViewModel.Theme.ImportThemeFromFileAsync(file.Path);
            if (imported == null)
            {
                ViewModel.StatusToast = "导入失败：主题文件格式无效";
                return;
            }

            _themeDraft = imported.Clone();
            RebuildThemeLibraryCardsUi();
            LoadThemeDraftToEditorUi();
            ViewModel.StatusToast = $"成功导入并激活主题：{imported.Name}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"导入主题失败：{ex.Message}";
        }
    }

    private async void OnExportCurrentThemeFileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var savePicker = new FileSavePicker();
            InitializeWithWindow.Initialize(savePicker, _hWnd);
            savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            savePicker.FileTypeChoices.Add("Alger 主题包 (.algertheme.json)", [".json"]);

            string safeName = string.Join("_", (_themeDraft.Name ?? "MyTheme").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "MyTheme";
            savePicker.SuggestedFileName = $"{safeName}.algertheme";

            var file = await savePicker.PickSaveFileAsync();
            if (file == null) return;

            await ViewModel.Theme.ExportThemeToFileAsync(_themeDraft, file.Path);
            ViewModel.StatusToast = $"主题已导出至：{file.Name}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusToast = $"导出主题失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 当切歌时，触发封面流光与强调色自适应
    /// </summary>
    private async Task ApplyDynamicCoverAccentIfEnabledAsync(string? coverUrl)
    {
        // 1. 若当前主题为封面动态流光，不论是否开启强调色吸色，都刷新极光光球配色
        var effective = ViewModel.Theme.GetEffectiveTheme();
        if (string.Equals(effective.BackdropType, "CoverFlow", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(coverUrl))
        {
            await RefreshCoverFlowColorsFromSongAsync(coverUrl);
        }

        if (!ViewModel.Config.Settings.ThemeCenter.FollowSongCoverAccent || string.IsNullOrWhiteSpace(coverUrl))
            return;

        // 若用户正处于未保存的主题草稿预览或打开弹窗编辑中，不打断用户的手动编辑
        if (ThemeCenterModalOverlay.Visibility == Visibility.Visible || ViewModel.Theme.PreviewProfile != null)
            return;

        var extracted = await ViewModel.Theme.ExtractThemeFromImageAsync(coverUrl, null, useAsWallpaper: false);
        if (extracted == null) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            var current = ViewModel.Theme.GetSavedActiveTheme().Clone();
            current.AccentColorHex = extracted.AccentColorHex;
            ViewModel.Theme.UpdateGlobalAccentBrushes(current);
            _ = ApplyThemeProfileToMainWindowAsync(current);
        });
    }
}
