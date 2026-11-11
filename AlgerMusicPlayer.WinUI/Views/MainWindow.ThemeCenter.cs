using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.System;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
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

    // 主题市场交互状态
    private string _selectedMarketCategory = string.Empty;
    private string _marketSearchQuery = string.Empty;
    private string _pageThemeSubMode = "local";
    private string _modalMainMode = "workshop";
    private List<MarketplaceThemeItem> _marketItems = [];

    private static readonly string[] OverridableThemeResourceKeys =
    [
        "CardBackgroundFillColorDefaultBrush",
        "CardBackgroundFillColorSecondaryBrush",
        "CardStrokeColorDefaultBrush",
        "DividerStrokeColorDefaultBrush",
        "TextFillColorPrimaryBrush",
        "TextFillColorSecondaryBrush"
    ];

    /// <summary>
    /// 启动时初始化主题中心与动态视觉特效引擎，并应用已持久化的用户主题
    /// </summary>
    private void InitializeThemeCenterEngine()
    {
        InitializeDynamicThemeVisualEngine();
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
        // 1. 计算目标明暗模式并交由双态自适应引擎生成协调的白昼/暗夜渲染配置（不修改原始保存的主题数据）
        ElementTheme targetElementTheme = rawProfile.BaseMode switch
        {
            "Light" => ElementTheme.Light,
            "System" => ElementTheme.Default,
            _ => ElementTheme.Dark
        };

        bool isEffectiveLight = targetElementTheme == ElementTheme.Light ||
                                (targetElementTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Light);

        var profile = ThemeService.ResolveModeAdaptedProfile(rawProfile, isEffectiveLight);
        ViewModel.Theme.UpdateGlobalAccentBrushes(profile);
        var (accent, hover, pressed, _) = ViewModel.Theme.DeriveAccentColors(profile);

        // 2. 窗口材质 (Mica / MicaAlt / Acrylic / Solid / Aurora / CoverFlow / Video / Image) 与底层壁纸渲染
        ApplyWindowBackdropAndBaseLayer(profile, isEffectiveLight);
        await UpdateGlobalWallpaperLayerAsync(profile);

        // 3. 动态流体极光、封面流光、动态视频壁纸与独立粒子氛围层
        await ApplyDynamicBackgroundAndParticlesAsync(profile, accent);

        // 4. 顶部标题栏独立 DIY 渲染（支持双色流光渐变）
        ApplyTitleBarSectionTheme(profile, isEffectiveLight);

        // 5. 左侧导航栏独立 DIY 渲染（支持双色流光渐变）
        ApplyNavigationPaneSectionTheme(profile, accent, isEffectiveLight);

        // 6. 主内容工作区与卡片全局 DIY 渲染
        ApplyContentAndCardsSectionTheme(profile, isEffectiveLight);

        // 7. 底部播放控制栏独立 DIY 渲染（支持双色流光渐变、独立主播放圆钮与进度条色）
        ApplyBottomPlayBarSectionTheme(profile, accent, hover, pressed);

        // 8. 界面形态（经典贴底 / 悬浮胶囊灵动岛 / 极简无界玻璃）、卡片视觉质感预设、旋转黑胶唱盘与全局字体排版
        ApplyMorphologyTypographyAndCoverDetails(profile, accent, isEffectiveLight);

        // 9. 触发视觉树 ThemeResource 刷新以使卡片/文字覆盖立即生效
        RefreshRootElementTheme(targetElementTheme);

        // 10. 同步弹窗与常规设置内嵌面板徽章、原生最小化/关闭按钮与歌词行配色
        var savedTheme = ViewModel.Theme.GetSavedActiveTheme();
        string badgeText = ViewModel.Theme.PreviewProfile != null
            ? $"实时预览中：{profile.Name}"
            : $"当前：{savedTheme.Name}";

        if (PageActiveThemeNameBadge != null) PageActiveThemeNameBadge.Text = badgeText;
        if (ThemeModalActiveNameBadge != null) ThemeModalActiveNameBadge.Text = badgeText;

        // 若当前正展示主题中心弹窗或设置页「个性主题」面板，立即随明暗基调同步重绘主题卡片，彻底杜绝深浅切换残影或泛白
        if (ThemeCenterModalOverlay.Visibility == Visibility.Visible || SettingsThemePanel.Visibility == Visibility.Visible)
        {
            RebuildThemeLibraryCardsUi();
        }

        ConfigureTitleBarButtonsTheme();
        SyncThemeSettingsUi();
        SyncLyricLinesTypographyAndEmptyState();
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
            GlobalWallpaperImage.Source = null;
            GlobalWallpaperImage.Visibility = Visibility.Collapsed;
            GlobalWallpaperMaskBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var maskBase = ThemeService.ParseColorOrDefault(profile.BackgroundMaskColorHex, Color.FromArgb(255, 9, 13, 22));
        GlobalWallpaperMaskBorder.Background = new SolidColorBrush(ThemeService.WithOpacity(maskBase, profile.BackgroundMaskOpacity));
        GlobalWallpaperMaskBorder.Visibility = Visibility.Visible;

        var imgSource = await ViewModel.Theme.CreateWallpaperImageSourceAsync(profile.BackgroundImagePath, profile.BackgroundBlur);
        if (myVersion != _wallpaperRenderVersion) return;

        if (imgSource != null)
        {
            GlobalWallpaperImage.Source = imgSource;
            GlobalWallpaperImage.Visibility = Visibility.Visible;
        }
        else
        {
            GlobalWallpaperImage.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyTitleBarSectionTheme(CustomThemeProfile profile, bool isLight)
    {
        if (profile.EnableCustomTitleBar)
        {
            var bgCol = ThemeService.ParseColorOrDefault(profile.TitleBarBgHex, isLight ? Color.FromArgb(255, 241, 245, 249) : Color.FromArgb(255, 17, 24, 39));
            AppTitleBar.Background = ThemeService.CreateSectionBrush(bgCol, profile.EnableTitleBarGradient, profile.TitleBarGradientEndHex, profile.TitleBarOpacity, profile.GradientAngle);

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
        else
        {
            AppTitleBar.Background = new SolidColorBrush(Colors.Transparent);
        }

        BrandTitleText.ClearValue(TextBlock.ForegroundProperty);
        foreach (var child in TitleBarRightToolbar.Children)
        {
            if (child is Button btn)
            {
                btn.ClearValue(Control.ForegroundProperty);
            }
        }
    }

    private void ApplyNavigationPaneSectionTheme(CustomThemeProfile profile, Color accent, bool isLight)
    {
        var navRes = MainNavView.Resources;
        navRes["NavigationViewSelectionIndicatorForeground"] = new SolidColorBrush(accent);

        bool isCustomLayer = profile.BackdropType is "Image" or "Solid" or "Aurora" or "CoverFlow" or "Video";
        Color iceWhite = ThemeService.CreateTintedIceWhite(accent, 0.06);

        // 同步让顶栏搜索框在动态玻璃背景下呈现轻盈半透明雾面，消除生硬纯白条
        if (TopSearchBox != null)
        {
            var sbRes = TopSearchBox.Resources;
            if (isCustomLayer)
            {
                var boxBg = new SolidColorBrush(isLight
                    ? Color.FromArgb(115, 255, 255, 255)
                    : Color.FromArgb(95, 15, 23, 42));
                var boxFocusBg = new SolidColorBrush(isLight
                    ? Color.FromArgb(175, 255, 255, 255)
                    : Color.FromArgb(145, 15, 23, 42));
                var boxBorder = new SolidColorBrush(isLight
                    ? Color.FromArgb(155, 255, 255, 255)
                    : Color.FromArgb(55, 255, 255, 255));
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
        }

        if (profile.EnableCustomNavPane)
        {
            var paneCol = ThemeService.ParseColorOrDefault(profile.NavPaneBgHex, isLight ? iceWhite : Color.FromArgb(255, 15, 23, 42));
            var paneBrush = ThemeService.CreateSectionBrush(paneCol, profile.EnableNavPaneGradient, profile.NavPaneGradientEndHex, profile.NavPaneOpacity, profile.GradientAngle, useAcrylicBlur: true);
            navRes["NavigationViewDefaultPaneBackground"] = paneBrush;
            navRes["NavigationViewExpandedPaneBackground"] = paneBrush;
            navRes["NavigationViewTopPaneBackground"] = paneBrush;

            var selCol = ThemeService.TryParseColor(profile.NavPaneSelectedBgHex, out var parsedSel)
                ? parsedSel
                : Color.FromArgb(0x28, accent.R, accent.G, accent.B);
            navRes["NavigationViewItemBackgroundSelected"] = new SolidColorBrush(selCol);
            navRes["NavigationViewItemBackgroundSelectedPointerOver"] = new SolidColorBrush(selCol);

            Brush? itemFg = ThemeService.TryParseColor(profile.NavPaneTextHex, out var txtCol)
                ? new SolidColorBrush(txtCol)
                : null;
            ApplyNavItemsForeground(itemFg);
        }
        else
        {
            if (isCustomLayer)
            {
                Brush glassPane = isLight
                    ? ThemeService.CreateFrostedGlassBrush(iceWhite, tintOpacity: 0.36, luminosityOpacity: 0.58, fallbackAlpha: 112)
                    : ThemeService.CreateFrostedGlassBrush(Color.FromArgb(255, 15, 23, 42), tintOpacity: 0.52, luminosityOpacity: 0.68, fallbackAlpha: 125);
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
            navRes.Remove("NavigationViewItemBackgroundSelected");
            navRes.Remove("NavigationViewItemBackgroundSelectedPointerOver");
            ApplyNavItemsForeground(null);
        }
    }

    private void ApplyNavItemsForeground(Brush? fgBrush)
    {
        foreach (var obj in MainNavView.MenuItems.Concat(MainNavView.FooterMenuItems))
        {
            if (obj is NavigationViewItem item)
            {
                if (fgBrush != null) item.Foreground = fgBrush;
                else item.ClearValue(Control.ForegroundProperty);
            }
        }
    }

    private void ApplyContentAndCardsSectionTheme(CustomThemeProfile profile, bool isLight)
    {
        var rootRes = RootLayout.Resources;
        var navRes = MainNavView.Resources;
        bool isCustomLayer = profile.BackdropType is "Image" or "Solid" or "Aurora" or "CoverFlow" or "Video";

        // 当启用自定义底层壁纸/极光或开启内容区独立定制时，让 NavigationView 默认实心内容底透明化，交由我们的图层控制
        if (profile.EnableCustomContent || isCustomLayer)
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
            double effContentOp = (isLight && isCustomLayer) ? Math.Min(profile.ContentOpacity, 10) : profile.ContentOpacity;
            MainContentWorkspaceGrid.Background = new SolidColorBrush(ThemeService.WithOpacity(contentBg, effContentOp));

            var cardBg = ThemeService.ParseColorOrDefault(profile.CardBgHex, isLight ? iceWhite : Color.FromArgb(255, 30, 41, 59));
            Brush cardPrimaryBrush = profile.CardOpacity < 96
                ? ThemeService.CreateSectionBrush(cardBg, false, null, profile.CardOpacity, 0, useAcrylicBlur: true)
                : new SolidColorBrush(ThemeService.WithOpacity(cardBg, profile.CardOpacity));

            var secondaryCardCol = ThemeService.AdjustLightness(cardBg, isLight ? -0.02 : 0.04);
            var cardSecondaryBrush = new SolidColorBrush(isLight
                ? Color.FromArgb(82, secondaryCardCol.R, secondaryCardCol.G, secondaryCardCol.B)
                : ThemeService.WithOpacity(secondaryCardCol, Math.Clamp(profile.CardOpacity - 8, 12, 100)));

            var borderCol = ThemeService.ParseColorOrDefault(profile.CardBorderHex, isLight ? Colors.White : Color.FromArgb(255, 51, 65, 85));
            var borderBrush = new SolidColorBrush(isLight
                ? Color.FromArgb(165, borderCol.R, borderCol.G, borderCol.B)
                : ThemeService.WithOpacity(borderCol, Math.Max(profile.CardOpacity, 55)));
            var dividerBrush = new SolidColorBrush(isLight
                ? Color.FromArgb(24, 15, 23, 42)
                : Color.FromArgb(90, borderCol.R, borderCol.G, borderCol.B));

            rootRes["CardBackgroundFillColorDefaultBrush"] = cardPrimaryBrush;
            rootRes["CardBackgroundFillColorSecondaryBrush"] = cardSecondaryBrush;
            rootRes["CardStrokeColorDefaultBrush"] = borderBrush;
            rootRes["DividerStrokeColorDefaultBrush"] = dividerBrush;

            if (ThemeService.TryParseColor(profile.PrimaryTextHex, out var pText))
                rootRes["TextFillColorPrimaryBrush"] = new SolidColorBrush(pText);
            else
                rootRes.Remove("TextFillColorPrimaryBrush");

            if (ThemeService.TryParseColor(profile.SecondaryTextHex, out var sText))
                rootRes["TextFillColorSecondaryBrush"] = new SolidColorBrush(sText);
            else
                rootRes.Remove("TextFillColorSecondaryBrush");

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

            // 开启全屏壁纸/极光/流光等自定义背景层时，默认赋予卡片真实高斯模糊亚克力毛玻璃质感，透出底层浅色/深色动态特效
            if (isCustomLayer)
            {
                var accent = ThemeService.ParseColorOrDefault(profile.AccentColorHex, Color.FromArgb(255, 16, 185, 129));
                var iceWhite = ThemeService.CreateTintedIceWhite(accent, 0.055);

                rootRes["CardBackgroundFillColorDefaultBrush"] = isLight
                    ? ThemeService.CreateFrostedGlassBrush(iceWhite, tintOpacity: 0.34, luminosityOpacity: 0.56, fallbackAlpha: 108)
                    : ThemeService.CreateFrostedGlassBrush(Color.FromArgb(255, 22, 30, 46), tintOpacity: 0.48, luminosityOpacity: 0.66, fallbackAlpha: 122);
                rootRes["CardBackgroundFillColorSecondaryBrush"] = new SolidColorBrush(isLight
                    ? Color.FromArgb(80, 255, 255, 255)
                    : Color.FromArgb(95, 17, 24, 39));
                rootRes["CardStrokeColorDefaultBrush"] = new SolidColorBrush(isLight
                    ? Color.FromArgb(170, 255, 255, 255)
                    : Color.FromArgb(52, 255, 255, 255));
                if (isLight)
                {
                    rootRes["DividerStrokeColorDefaultBrush"] = new SolidColorBrush(Color.FromArgb(22, 15, 23, 42));
                }
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

    private void ApplyBottomPlayBarSectionTheme(CustomThemeProfile profile, Color accent, Color hover, Color pressed)
    {
        Color sliderCol = (profile.EnableCustomPlayBar && ThemeService.TryParseColor(profile.ProgressSliderColorHex, out var customSlider))
            ? customSlider
            : accent;
        Color sliderHover = ThemeService.AdjustLightness(sliderCol, -0.06);
        Color sliderPressed = ThemeService.AdjustLightness(sliderCol, -0.12);

        ApplySliderAccentResources(SeekSlider, sliderCol, sliderHover, sliderPressed);
        ApplySliderAccentResources(VolumeSlider, sliderCol, sliderHover, sliderPressed);
        ApplySliderAccentResources(SimplePlayBarVolumeSlider, sliderCol, sliderHover, sliderPressed);

        if (profile.EnableCustomPlayBar)
        {
            var bgCol = ThemeService.ParseColorOrDefault(profile.PlayBarBgHex, Color.FromArgb(255, 17, 24, 39));
            var borderCol = ThemeService.ParseColorOrDefault(profile.PlayBarBorderHex, Color.FromArgb(255, 31, 41, 55));
            BottomPlayBarBorder.Background = ThemeService.CreateSectionBrush(bgCol, profile.EnablePlayBarGradient, profile.PlayBarGradientEndHex, profile.PlayBarOpacity, profile.GradientAngle, useAcrylicBlur: true);
            BottomPlayBarBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(170, borderCol.R, borderCol.G, borderCol.B));

            if (ThemeService.TryParseColor(profile.PlayBarTextHex, out var textCol))
            {
                var fg = new SolidColorBrush(textCol);
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
            ClearPlayBarCustomTextForeground();
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
        var fillBrush = new SolidColorBrush(fill);
        var hoverBrush = new SolidColorBrush(hover);
        var pressedBrush = new SolidColorBrush(pressed);

        r["SliderTrackValueFill"] = fillBrush;
        r["SliderTrackValueFillPointerOver"] = hoverBrush;
        r["SliderTrackValueFillPressed"] = pressedBrush;
        r["SliderThumbBackground"] = fillBrush;
        r["SliderThumbBackgroundPointerOver"] = hoverBrush;
        r["SliderThumbBackgroundPressed"] = pressedBrush;
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

    private UIElement CreateThemeLibraryItemCard(CustomThemeProfile item, bool isActive)
    {
        bool isUiLight = RootLayout.ActualTheme == ElementTheme.Light;
        var accentCol = ThemeService.ParseColorOrDefault(item.AccentColorHex, Color.FromArgb(255, 16, 185, 129));
        bool isItemLight = string.Equals(item.BaseMode, "Light", StringComparison.OrdinalIgnoreCase);
        var itemWinBg = ThemeService.ParseColorOrDefault(item.WindowBgColorHex,
            isItemLight ? Color.FromArgb(255, 248, 250, 252) : Color.FromArgb(255, 18, 22, 31));
        var itemNavBg = item.EnableCustomNavPane
            ? ThemeService.ParseColorOrDefault(item.NavPaneBgHex, itemWinBg)
            : (isItemLight ? Color.FromArgb(255, 236, 241, 247) : Color.FromArgb(255, 26, 33, 46));

        Color normalBg = isUiLight
            ? Color.FromArgb(92, 255, 255, 255)
            : Color.FromArgb(20, 255, 255, 255);
        Color hoverBg = isUiLight
            ? Color.FromArgb(34, accentCol.R, accentCol.G, accentCol.B)
            : Color.FromArgb(34, 255, 255, 255);
        Color activeBg = isUiLight
            ? Color.FromArgb(40, accentCol.R, accentCol.G, accentCol.B)
            : Color.FromArgb(38, accentCol.R, accentCol.G, accentCol.B);

        Color normalBorder = isUiLight
            ? Color.FromArgb(155, 255, 255, 255)
            : Color.FromArgb(36, 255, 255, 255);
        Color hoverBorder = Color.FromArgb(145, accentCol.R, accentCol.G, accentCol.B);

        Color titleColor = isUiLight
            ? Color.FromArgb(255, 15, 23, 42)
            : Color.FromArgb(255, 248, 250, 252);
        Color descColor = isUiLight
            ? Color.FromArgb(255, 100, 116, 139)
            : Color.FromArgb(255, 148, 163, 184);

        var outerBorder = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(11, 9, 11, 9),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(isActive ? activeBg : normalBg),
            BorderBrush = new SolidColorBrush(isActive ? accentCol : normalBorder),
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
                Background = new SolidColorBrush(Color.FromArgb(0x24, accentCol.R, accentCol.G, accentCol.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, accentCol.R, accentCol.G, accentCol.B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 1, 4, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = item.BackdropType is "Aurora" or "CoverFlow" ? "极光" : (item.EnableParticleOverlay ? "粒子" : "动态"),
                    FontSize = 9,
                    Foreground = new SolidColorBrush(accentCol),
                    FontWeight = FontWeights.SemiBold
                }
            });
        }

        if (isActive)
        {
            var activeTag = new Border
            {
                Background = new SolidColorBrush(accentCol),
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
                outerBorder.Background = new SolidColorBrush(hoverBg);
                outerBorder.BorderBrush = new SolidColorBrush(hoverBorder);
            }
        };
        outerBorder.PointerExited += (_, _) =>
        {
            outerBorder.Background = new SolidColorBrush(isActive ? activeBg : normalBg);
            outerBorder.BorderBrush = new SolidColorBrush(isActive ? accentCol : normalBorder);
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

    private UIElement CreateMarketplaceThemeCard(MarketplaceThemeItem item)
    {
        bool isUiLight = RootLayout.ActualTheme == ElementTheme.Light;
        var t = item.Theme;
        var accentCol = ThemeService.ParseColorOrDefault(t.AccentColorHex, Color.FromArgb(255, 16, 185, 129));
        bool isThemeLight = string.Equals(t.BaseMode, "Light", StringComparison.OrdinalIgnoreCase);
        bool isInstalled = ViewModel.Theme.IsMarketplaceThemeInstalled(item.Id);

        Color cardBg = isUiLight ? Color.FromArgb(96, 255, 255, 255) : Color.FromArgb(22, 255, 255, 255);
        Color borderCol = isUiLight ? Color.FromArgb(160, 255, 255, 255) : Color.FromArgb(36, 255, 255, 255);
        Color titleColor = isUiLight ? Color.FromArgb(255, 15, 23, 42) : Color.FromArgb(255, 248, 250, 252);
        Color subColor = isUiLight ? Color.FromArgb(255, 100, 116, 139) : Color.FromArgb(255, 148, 163, 184);

        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(cardBg),
            BorderBrush = new SolidColorBrush(borderCol),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12)
        };

        var mainStack = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };

        // 1. 顶部艺术色带头图（带双色渐变 / 极光预览色）
        var headerBanner = new Border
        {
            Height = 44,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))
        };

        var c1 = accentCol;
        var c2 = t.EnableAccentGradient && ThemeService.TryParseColor(t.AccentGradientEndHex, out var parsedEnd)
            ? parsedEnd
            : ThemeService.AdjustLightness(accentCol, isThemeLight ? 0.18 : -0.15);

        headerBanner.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = c1, Offset = 0 },
                new GradientStop { Color = c2, Offset = 1 }
            }
        };

        var bannerGrid = new Grid { Padding = new Thickness(8, 0, 8, 0) };
        var leftTags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        leftTags.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Child = new TextBlock { Text = item.Category, FontSize = 10, Foreground = new SolidColorBrush(Colors.White), FontWeight = FontWeights.SemiBold }
        });

        if (t.HasDynamicEffect)
        {
            leftTags.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 2, 5, 2),
                Child = new TextBlock { Text = t.BackdropType is "Aurora" or "CoverFlow" ? "动态极光" : "氛围粒子", FontSize = 10, Foreground = new SolidColorBrush(Colors.White) }
            });
        }
        bannerGrid.Children.Add(leftTags);

        var rightMeta = new TextBlock
        {
            Text = $"↓ {item.Downloads}",
            FontSize = 10,
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        bannerGrid.Children.Add(rightMeta);
        headerBanner.Child = bannerGrid;
        mainStack.Children.Add(headerBanner);

        // 2. 主题标题、作者与简介
        var infoStack = new StackPanel { Spacing = 3 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        titleRow.Children.Add(new TextBlock
        {
            Text = t.Name,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(titleColor),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        });
        if (isInstalled)
        {
            titleRow.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x30, accentCol.R, accentCol.G, accentCol.B)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "已装", FontSize = 9, Foreground = new SolidColorBrush(accentCol), FontWeight = FontWeights.Bold }
            });
        }
        infoStack.Children.Add(titleRow);

        infoStack.Children.Add(new TextBlock
        {
            Text = $"By {t.Author} · {item.SourceName}",
            FontSize = 11,
            Foreground = new SolidColorBrush(subColor)
        });

        infoStack.Children.Add(new TextBlock
        {
            Text = t.Description,
            FontSize = 12,
            Foreground = new SolidColorBrush(titleColor),
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Height = 32
        });
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
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        previewBtn.Click += async (_, _) =>
        {
            _themeDraft = item.Theme.Clone();
            ViewModel.Theme.NotifyPreviewUpdated(_themeDraft);
            LoadThemeDraftToEditorUi();
            ViewModel.StatusToast = $"正在预览试用市场主题：{t.Name}";
            await Task.CompletedTask;
        };
        Grid.SetColumn(previewBtn, 0);
        actionsRow.Children.Add(previewBtn);

        var installBtn = new Button
        {
            Content = isInstalled ? "更新并应用" : "安装并应用",
            Style = (Style)Application.Current.Resources["BrandPrimaryButtonStyle"],
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5, 8, 5),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        installBtn.Click += async (_, _) =>
        {
            var saved = await ViewModel.Theme.InstallMarketplaceThemeAsync(item);
            _themeDraft = saved.Clone();
            RebuildThemeLibraryCardsUi();
            LoadThemeDraftToEditorUi();
            RenderMarketplaceCardsUi();
            ViewModel.StatusToast = $"已成功安装并应用：{saved.Name}";
        };
        Grid.SetColumn(installBtn, 1);
        actionsRow.Children.Add(installBtn);

        mainStack.Children.Add(actionsRow);
        card.Child = mainStack;
        return card;
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
        swatchBtn.Background = new SolidColorBrush(col);
        if (hexBox != null)
        {
            hexBox.Text = allowEmptyText && string.IsNullOrWhiteSpace(rawHex)
                ? string.Empty
                : (string.IsNullOrWhiteSpace(rawHex) ? ThemeService.ToHex(col) : rawHex);
        }
    }

    /// <summary>
    /// 实时刷新弹窗右上角 1:1 四大分区微缩仿真窗口（含微缩极光与悬浮灵动岛预览）
    /// </summary>
    private void UpdateMiniPreviewWindow()
    {
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
            try
            {
                MiniPreviewWallpaperImg.Source = new BitmapImage(new Uri(previewDraft.BackgroundImagePath));
                MiniPreviewWallpaperImg.Visibility = Visibility.Visible;
            }
            catch
            {
                MiniPreviewWallpaperImg.Visibility = Visibility.Collapsed;
            }
            var maskCol = ThemeService.ParseColorOrDefault(previewDraft.BackgroundMaskColorHex, Color.FromArgb(255, 9, 13, 22));
            MiniPreviewWallpaperMask.Background = new SolidColorBrush(ThemeService.WithOpacity(maskCol, previewDraft.BackgroundMaskOpacity));
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
        if (_isLoadingThemeCenterUi) return;
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
        if (_isLoadingThemeCenterUi) return;
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
        _themeDraft.BackdropType = kind;
        SyncBaseModeAndBackdropButtons();
        PushDraftLivePreview();
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
        if (_isLoadingThemeCenterUi) return;

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
        if (_isLoadingThemeCenterUi || sender is not ToggleSwitch ts || ts.Tag is not string tag) return;

        bool isOn = ts.IsOn;
        switch (tag)
        {
            case "UseCustomAccentStates": _themeDraft.UseCustomAccentStates = isOn; break;
            case "EnableAccentGradient": _themeDraft.EnableAccentGradient = isOn; break;
            case "EnableParticleOverlay": _themeDraft.EnableParticleOverlay = isOn; break;
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
        if (_isLoadingThemeCenterUi) return;
        await UpdateFollowCoverAccentSettingAsync(FollowSongCoverAccentToggle.IsOn);
    }

    private async void OnPageFollowCoverAccentToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingThemeCenterUi) return;
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
        if (_isLoadingThemeCenterUi || !_isInitialized) return;

        _themeDraft.BackgroundMaskOpacity = Math.Round(ThemeWallpaperMaskOpacitySlider.Value);
        _themeDraft.TitleBarOpacity = Math.Round(TitleBarOpacitySlider.Value);
        _themeDraft.NavPaneOpacity = Math.Round(NavPaneOpacitySlider.Value);
        _themeDraft.ContentOpacity = Math.Round(ContentOpacitySlider.Value);
        _themeDraft.CardOpacity = Math.Round(CardOpacitySlider.Value);
        _themeDraft.CardCornerRadius = Math.Round(CardCornerRadiusSlider.Value);
        _themeDraft.PlayBarOpacity = Math.Round(PlayBarOpacitySlider.Value);
        _themeDraft.MiniPlayerOpacity = Math.Round(MiniPlayerOpacitySlider.Value);

        SyncSliderLabelsAndCrossControls();
        PushDraftLivePreview();
    }

    private void OnThemeWallpaperBlurChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isLoadingThemeCenterUi || !_isInitialized) return;
        _themeDraft.BackgroundBlur = Math.Round(ThemeWallpaperBlurSlider.Value);
        SyncSliderLabelsAndCrossControls();
        PushDraftLivePreview();
    }

    private void OnTaggedThemeSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isLoadingThemeCenterUi || !_isInitialized || sender is not Slider sl || sl.Tag is not string tag) return;

        double val = sl.Value;
        switch (tag)
        {
            case "BackgroundBlur": _themeDraft.BackgroundBlur = Math.Round(val); break;
            case "BackgroundMaskOpacity": _themeDraft.BackgroundMaskOpacity = Math.Round(val); break;
            case "AuroraSpeed": _themeDraft.AuroraSpeed = Math.Round(val / 100.0, 2); break;
            case "AuroraIntensity": _themeDraft.AuroraIntensity = Math.Round(val); break;
            case "ParticleDensity": _themeDraft.ParticleDensity = (int)Math.Round(val); break;
            case "ParticleSpeed": _themeDraft.ParticleSpeed = Math.Round(val / 100.0, 2); break;
            case "TitleBarOpacity": _themeDraft.TitleBarOpacity = Math.Round(val); break;
            case "NavPaneOpacity": _themeDraft.NavPaneOpacity = Math.Round(val); break;
            case "ContentOpacity": _themeDraft.ContentOpacity = Math.Round(val); break;
            case "CardOpacity": _themeDraft.CardOpacity = Math.Round(val); break;
            case "CardCornerRadius": _themeDraft.CardCornerRadius = Math.Round(val); break;
            case "PlayBarOpacity": _themeDraft.PlayBarOpacity = Math.Round(val); break;
            case "MiniPlayerOpacity": _themeDraft.MiniPlayerOpacity = Math.Round(val); break;
            case "ButtonCornerRadius": _themeDraft.ButtonCornerRadius = Math.Round(val); break;
        }

        SyncSliderLabelsAndCrossControls();
        PushDraftLivePreview();
    }

    private void SyncSliderLabelsAndCrossControls()
    {
        _isLoadingThemeCenterUi = true;

        ThemeWallpaperBlurSlider.Value = _themeDraft.BackgroundBlur;
        PageWallpaperBlurSlider.Value = _themeDraft.BackgroundBlur;
        ThemeWallpaperBlurText.Text = $"{_themeDraft.BackgroundBlur:0}px";
        PageWallpaperBlurText.Text = $"{_themeDraft.BackgroundBlur:0}px";

        ThemeWallpaperMaskOpacitySlider.Value = _themeDraft.BackgroundMaskOpacity;
        PageWallpaperMaskOpacitySlider.Value = _themeDraft.BackgroundMaskOpacity;
        PageVideoMaskOpacitySlider.Value = _themeDraft.BackgroundMaskOpacity;
        ThemeWallpaperMaskOpacityText.Text = $"{_themeDraft.BackgroundMaskOpacity:0}%";
        PageWallpaperMaskOpacityText.Text = $"{_themeDraft.BackgroundMaskOpacity:0}%";
        PageVideoMaskOpacityText.Text = $"{_themeDraft.BackgroundMaskOpacity:0}%";

        double aSpeed = Math.Clamp(_themeDraft.AuroraSpeed * 100, 20, 300);
        ThemeAuroraSpeedSlider.Value = aSpeed;
        PageAuroraSpeedSlider.Value = aSpeed;
        ThemeAuroraSpeedText.Text = $"{_themeDraft.AuroraSpeed:0.0}x";
        PageAuroraSpeedText.Text = $"{_themeDraft.AuroraSpeed:0.0}x";

        ThemeAuroraIntensitySlider.Value = _themeDraft.AuroraIntensity;
        PageAuroraIntensitySlider.Value = _themeDraft.AuroraIntensity;
        ThemeAuroraIntensityText.Text = $"{_themeDraft.AuroraIntensity:0}%";
        PageAuroraIntensityText.Text = $"{_themeDraft.AuroraIntensity:0}%";

        ThemeParticleDensitySlider.Value = _themeDraft.ParticleDensity;
        PageParticleDensitySlider.Value = _themeDraft.ParticleDensity;
        ThemeParticleDensityText.Text = $"{_themeDraft.ParticleDensity:0}";
        PageParticleDensityText.Text = $"{_themeDraft.ParticleDensity:0}";

        double pSpeed = Math.Clamp(_themeDraft.ParticleSpeed * 100, 30, 250);
        ThemeParticleSpeedSlider.Value = pSpeed;
        PageParticleSpeedSlider.Value = pSpeed;
        ThemeParticleSpeedText.Text = $"{_themeDraft.ParticleSpeed:0.0}x";
        PageParticleSpeedText.Text = $"{_themeDraft.ParticleSpeed:0.0}x";

        TitleBarOpacitySlider.Value = _themeDraft.TitleBarOpacity;
        PageTitleBarOpacitySlider.Value = _themeDraft.TitleBarOpacity;
        TitleBarOpacityText.Text = $"{_themeDraft.TitleBarOpacity:0}%";
        PageTitleBarOpacityText.Text = $"{_themeDraft.TitleBarOpacity:0}%";

        NavPaneOpacitySlider.Value = _themeDraft.NavPaneOpacity;
        PageNavPaneOpacitySlider.Value = _themeDraft.NavPaneOpacity;
        NavPaneOpacityText.Text = $"{_themeDraft.NavPaneOpacity:0}%";
        PageNavPaneOpacityText.Text = $"{_themeDraft.NavPaneOpacity:0}%";

        ContentOpacitySlider.Value = _themeDraft.ContentOpacity;
        PageContentOpacitySlider.Value = _themeDraft.ContentOpacity;
        ContentOpacityText.Text = $"{_themeDraft.ContentOpacity:0}%";
        PageContentOpacityText.Text = $"{_themeDraft.ContentOpacity:0}%";

        CardOpacitySlider.Value = _themeDraft.CardOpacity;
        PageCardOpacitySlider.Value = _themeDraft.CardOpacity;
        CardOpacityText.Text = $"{_themeDraft.CardOpacity:0}%";
        PageCardOpacityText.Text = $"{_themeDraft.CardOpacity:0}%";

        CardCornerRadiusSlider.Value = _themeDraft.CardCornerRadius;
        PageCardCornerRadiusSlider.Value = _themeDraft.CardCornerRadius;
        CardCornerRadiusText.Text = $"{_themeDraft.CardCornerRadius:0}px";
        PageCardCornerRadiusText.Text = $"{_themeDraft.CardCornerRadius:0}px";

        PlayBarOpacitySlider.Value = _themeDraft.PlayBarOpacity;
        PagePlayBarOpacitySlider.Value = _themeDraft.PlayBarOpacity;
        PlayBarOpacityText.Text = $"{_themeDraft.PlayBarOpacity:0}%";
        PagePlayBarOpacityText.Text = $"{_themeDraft.PlayBarOpacity:0}%";

        MiniPlayerOpacitySlider.Value = _themeDraft.MiniPlayerOpacity;
        PageMiniPlayerOpacitySlider.Value = _themeDraft.MiniPlayerOpacity;

        ThemeButtonRadiusSlider.Value = _themeDraft.ButtonCornerRadius;
        PageButtonRadiusSlider.Value = _themeDraft.ButtonCornerRadius;
        ThemeButtonRadiusText.Text = $"{_themeDraft.ButtonCornerRadius:0}px";
        PageButtonRadiusText.Text = $"{_themeDraft.ButtonCornerRadius:0}px";

        _isLoadingThemeCenterUi = false;
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
        SetDraftColorHexByKey(_activeColorFieldKey, hex);

        _isLoadingThemeCenterUi = true;
        SyncAllColorBoxesAndSwatchesFromDraft();
        _isLoadingThemeCenterUi = false;

        PushDraftLivePreview();
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

            LoadThemeDraftToEditorUi();
            PushDraftLivePreview();
            ViewModel.StatusToast = $"已加载背景图片：{file.Name}";
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
