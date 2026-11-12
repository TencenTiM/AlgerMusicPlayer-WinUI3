using System.Numerics;
using System.Runtime.CompilerServices;
using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.System;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace AlgerMusicPlayer.WinUI.Views;

/// <summary>
/// MainWindow 分部类：iOS 27 原生 DWM「液态玻璃 (Liquid Glass)」边缘透镜折射挂载与壁纸自适应文字可读性引擎。
/// 核心设计：
/// 1. 所有卡片基底 100% 由 DWM 硬件实时采样底层壁纸（LiquidGlassCompositionBrush），滚动时壁纸死死钉在窗口原地，绝不随卡片滚动；
/// 2. 玻璃中心保持超清透零白雾（告别雾蒙蒙毛玻璃），圆角边缘挂载原生同域 DWM 实时位移折射环 + 3D「亮镜面唇-深色弯月暗槽-内焦散亮环」；
/// 3. 自定义壁纸开启智能可读性时，按每段文字所在精确屏幕位置自适应切换高对比度深/浅字色（绝不往整张大玻璃卡片泼牛奶白雾）；
/// 4. 切换回普通主题时基于明确布尔状态无条件卸载 ChildVisual 与本地属性覆盖，彻底绕开 CsWinRT RCW 比较陷阱，确保 100% 零残留。
/// </summary>
public sealed partial class MainWindow
{
    public const string SkipAdaptiveTextTag = "SkipAdaptiveWallpaperText";

    private const double MaxTitleBarVeil = 0.22;
    private const int TitleBarVeilSegments = 10;

    private readonly record struct AdaptivePassContext(
        double WinW,
        double WinH,
        double ImgLeft,
        double ImgTop,
        double ImgW,
        double ImgH,
        Color MaskCol,
        double MaskAlpha,
        bool IsLight,
        Color Accent,
        bool UseLiquidLens,
        bool UseWallpaperAdaptiveText);

    private sealed class VeilSurfaceState
    {
        public bool OriginalRecorded;
        public bool HadOriginalLocalBackground;
        public object? OriginalLocalBackground;
        public bool IsBackgroundOverriddenByUs;

        public bool HadOriginalLocalBorderBrush;
        public object? OriginalLocalBorderBrush;
        public bool IsBorderBrushOverridden;

        // 挂载于卡片内部 Grid 第 0 层的纯视觉底层宿主（使折射与水滴唇边完全垫在进度条、封面、文字正下方）
        public Border? UnderlayHost;
        public Panel? UnderlayParentPanel;
        public bool CardShadowApplied;

        public OpticalGlassAttachment? LensAttachment;
        public LiquidGlassCompositionBrush? OwnGlassBrush;
        public double CachedIntensity = 1.0;
        public bool ForceDarkSurface;

        // 原生 DWM 合成器圆角几何抗锯齿裁剪
        public CompositionRoundedRectangleGeometry? RoundedGeometry;
        public CompositionGeometricClip? GeometricClip;
        public float AppliedClipW;
        public float AppliedClipH;
        public float AppliedClipRadius;
        public bool SizingTouched;
        public BackgroundSizing OriginalSizing;
        public bool CornerRadiusTouched;
        public CornerRadius OriginalCornerRadius;
    }

    private sealed class BareTextOverrideState
    {
        public bool HadOriginalLocalForeground;
        public object? OriginalLocalForeground;
        public bool OriginalIsLightText;
        public SolidColorBrush? AdaptiveBrush;
        public bool IsOverridden;
        public bool LastWasLightText;
        public bool IsSecondaryTone;
    }

    private readonly OpticalLiquidGlassRenderer _opticalGlassRenderer = new();
    // 使用强引用字典（按 CsWinRT 原生 IInspectable 指针哈希匹配），彻底杜绝无 x:Name 控件的瞬态托管 RCW 被 Gen0 GC 回收导致切主题漏还原白字与子视觉层
    private readonly Dictionary<DependencyObject, VeilSurfaceState> _veilSurfaceStates = new();
    private readonly Dictionary<FrameworkElement, BareTextOverrideState> _bareTextStates = new();
    private readonly ConditionalWeakTable<ScrollViewer, object> _hookedAdaptiveScrollViewers = new();
    private readonly ConditionalWeakTable<FrameworkElement, object> _hookedGlassSizeElements = new();

    // 底部悬浮灵动岛空气感体积阴影
    private ThemeShadow? _floatingIslandShadow;
    private bool _playBarShadowApplied;

    private bool _titleBarVeilApplied;
    private bool _titleBarHadLocalBackground;
    private object? _titleBarOriginalBackground;
    private LinearGradientBrush? _titleBarVeilBrush;

    private WallpaperColorGrid? _activeWallpaperColorGrid;
    private CustomThemeProfile? _activeAdaptiveRenderedProfile;
    private DispatcherQueueTimer? _adaptiveTextThrottleTimer;
    private bool _adaptivePassPending;
    private bool _hasActiveAdaptiveOverrides;

    private bool HasActiveCustomWallpaperImage =>
        _activeAdaptiveRenderedProfile != null &&
        string.Equals(_activeAdaptiveRenderedProfile.BackdropType, "Image", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(_activeAdaptiveRenderedProfile.BackgroundImagePath) &&
        GlobalWallpaperImage.Visibility == Visibility.Visible;

    private bool IsLiquidLensPassActive =>
        _activeAdaptiveRenderedProfile != null &&
        IsLiquidGlassMode(_activeAdaptiveRenderedProfile);

    private bool IsWallpaperAdaptiveTextPassActive =>
        HasActiveCustomWallpaperImage &&
        _activeAdaptiveRenderedProfile!.EnableAdaptiveWallpaperText &&
        _activeWallpaperColorGrid != null;

    private bool IsAdaptiveWallpaperTextActive =>
        IsLiquidLensPassActive || IsWallpaperAdaptiveTextPassActive;

    /// <summary>
    /// 初始化液态玻璃边缘透镜挂载与壁纸自适应文字监听
    /// </summary>
    private void InitializeAdaptiveWallpaperTextEngine()
    {
        _adaptiveTextThrottleTimer = DispatcherQueue.CreateTimer();
        _adaptiveTextThrottleTimer.Interval = TimeSpan.FromMilliseconds(16);
        _adaptiveTextThrottleTimer.IsRepeating = false;
        _adaptiveTextThrottleTimer.Tick += (_, _) =>
        {
            _adaptivePassPending = false;
            ExecuteAdaptiveWallpaperTextPass();
        };

        RootLayout.SizeChanged += (_, _) =>
        {
            if (IsAdaptiveWallpaperTextActive)
            {
                ScheduleAdaptiveWallpaperTextPass();
            }
        };

        MainNavView.PaneOpening += (_, _) =>
        {
            if (IsAdaptiveWallpaperTextActive)
            {
                ScheduleAdaptiveWallpaperTextPass();
            }
        };

        MainNavView.PaneClosing += (_, _) =>
        {
            if (IsAdaptiveWallpaperTextActive)
            {
                ScheduleAdaptiveWallpaperTextPass();
            }
        };
    }

    /// <summary>
    /// 请求一次液态玻璃边缘折射环同步与壁纸文字可读性扫描
    /// </summary>
    public void ScheduleAdaptiveWallpaperTextPass(bool immediate = false)
    {
        if (!IsAdaptiveWallpaperTextActive)
        {
            if (_hasActiveAdaptiveOverrides || _veilSurfaceStates.Count > 0 || _bareTextStates.Count > 0)
            {
                RestoreAllAdaptiveWallpaperTexts();
            }
            return;
        }

        if (immediate)
        {
            _adaptiveTextThrottleTimer?.Stop();
            _adaptivePassPending = false;
            ExecuteAdaptiveWallpaperTextPass();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (IsAdaptiveWallpaperTextActive)
                {
                    ExecuteAdaptiveWallpaperTextPass();
                }
            });
            return;
        }

        if (_adaptivePassPending || _adaptiveTextThrottleTimer == null)
            return;

        _adaptivePassPending = true;
        _adaptiveTextThrottleTimer.Start();
    }

    /// <summary>
    /// 切离液态玻璃或切换主题时，无条件 100% 干净卸载所有 DWM 子视觉层、本地背景画刷、前景色覆盖与圆角裁剪
    /// （绝不依赖 CsWinRT ReadLocalValue 引用相等判断，杜绝任何壁纸或样式残留）
    /// </summary>
    private void RestoreAllAdaptiveWallpaperTexts(bool? explicitTargetIsLight = null)
    {
        _adaptiveTextThrottleTimer?.Stop();
        _adaptivePassPending = false;

        foreach (var (target, state) in _veilSurfaceStates)
        {
            UIElement? lensHost = (UIElement?)state.UnderlayHost ?? (target as UIElement);
            state.LensAttachment?.DetachAndDispose(lensHost);
            state.LensAttachment = null;

            if (state.UnderlayHost != null && state.UnderlayParentPanel != null)
            {
                try
                {
                    state.UnderlayParentPanel.Children.Remove(state.UnderlayHost);
                }
                catch
                {
                }
                state.UnderlayHost = null;
                state.UnderlayParentPanel = null;
            }

            if (state.IsBorderBrushOverridden && target is Border bBorder)
            {
                if (state.HadOriginalLocalBorderBrush && state.OriginalLocalBorderBrush is Brush origBorderBrush)
                {
                    bBorder.BorderBrush = origBorderBrush;
                }
                else
                {
                    bBorder.ClearValue(Border.BorderBrushProperty);
                }
                state.IsBorderBrushOverridden = false;
            }

            if (state.CardShadowApplied && target is UIElement shadowEl)
            {
                try
                {
                    shadowEl.Shadow = null;
                    shadowEl.Translation = Vector3.Zero;
                }
                catch
                {
                }
                state.CardShadowApplied = false;
            }

            var bgProp = GetBackgroundProperty(target);
            if (bgProp != null && state.IsBackgroundOverriddenByUs)
            {
                if (state.HadOriginalLocalBackground && state.OriginalLocalBackground is Brush origBrush)
                {
                    target.SetValue(bgProp, origBrush);
                }
                else
                {
                    target.ClearValue(bgProp);
                }
                state.IsBackgroundOverriddenByUs = false;
            }
            state.OwnGlassBrush = null;

            RestoreSmoothRoundedGlassClip(target, state);
        }
        _veilSurfaceStates.Clear();

        RestorePlayBarFloatingShadow();
        RestoreAllBareTextOverrides(explicitTargetIsLight);

        if (_titleBarVeilApplied)
        {
            if (_titleBarHadLocalBackground && _titleBarOriginalBackground is Brush originalBg)
                AppTitleBar.Background = originalBg;
            else
                AppTitleBar.ClearValue(Panel.BackgroundProperty);

            _titleBarVeilApplied = false;
            _titleBarOriginalBackground = null;
            _titleBarVeilBrush = null;
        }

        _hasActiveAdaptiveOverrides = false;
        ConfigureTitleBarButtonsTheme();
    }

    private void RestoreSingleBareTextElement(FrameworkElement el, BareTextOverrideState st, DependencyProperty fgProp, bool targetIsLightUi)
    {
        Color defaultCol = targetIsLightUi
            ? (st.IsSecondaryTone ? Color.FromArgb(255, 51, 65, 85) : Color.FromArgb(255, 15, 23, 42))
            : (st.IsSecondaryTone ? Color.FromArgb(204, 203, 213, 225) : Color.FromArgb(255, 248, 250, 252));

        if (st.AdaptiveBrush != null)
        {
            st.AdaptiveBrush.Color = defaultCol;
        }

        ClearNavItemAdaptiveStateResources(el);

        // 对 TextBlock / IconElement 直接绑定回 RootLayout.Resources 中全局原地变色的标准主题前景画刷，彻底规避 CsWinRT RCW 引用比较及 RefreshRootElementTheme 翻转导致残留白字
        if (el is TextBlock or IconElement)
        {
            string resKey = st.IsSecondaryTone ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush";
            ThemeService.SetOrUpdateSolidBrush(RootLayout.Resources, resKey, defaultCol);
            if (RootLayout.Resources.TryGetValue(resKey, out var sharedObj) && sharedObj is Brush sharedBrush)
            {
                el.SetValue(fgProp, sharedBrush);
            }
            else
            {
                el.ClearValue(fgProp);
            }
        }
        else if (st.HadOriginalLocalForeground && st.OriginalLocalForeground is Brush origFg)
        {
            el.SetValue(fgProp, origFg);
        }
        else
        {
            el.ClearValue(fgProp);
        }
    }

    private void RestoreAllBareTextOverrides(bool? explicitTargetIsLight = null)
    {
        bool targetIsLightUi = explicitTargetIsLight ?? IsCurrentUiLightMode;
        foreach (var (el, st) in _bareTextStates)
        {
            var fgProp = GetForegroundProperty(el);
            if (fgProp != null && st.IsOverridden)
            {
                RestoreSingleBareTextElement(el, st, fgProp, targetIsLightUi);
            }
            st.IsOverridden = false;
        }
        _bareTextStates.Clear();
    }

    private static void SetNavItemAdaptiveStateResources(FrameworkElement el, Brush brush, bool targetLightText)
    {
        if (el is not NavigationViewItem navItem)
            return;

        var res = navItem.Resources;
        res["NavigationViewItemForeground"] = brush;
        res["NavigationViewItemForegroundPointerOver"] = brush;
        res["NavigationViewItemForegroundPressed"] = brush;
        res["NavigationViewItemForegroundSelected"] = brush;
        res["NavigationViewItemForegroundSelectedPointerOver"] = brush;
        res["NavigationViewItemForegroundSelectedPressed"] = brush;

        // 触发 NavigationViewItemPresenter 重新对子树 {ThemeResource} 求值（使启动即选中的「首页推荐」立即生效，且不触发 SelectionChanged）
        var wantTheme = targetLightText ? ElementTheme.Dark : ElementTheme.Light;
        if (navItem.RequestedTheme == wantTheme)
        {
            navItem.RequestedTheme = ElementTheme.Default;
        }
        navItem.RequestedTheme = wantTheme;
    }

    private static void ClearNavItemAdaptiveStateResources(FrameworkElement el)
    {
        if (el is not NavigationViewItem navItem)
            return;

        var res = navItem.Resources;
        res.Remove("NavigationViewItemForeground");
        res.Remove("NavigationViewItemForegroundPointerOver");
        res.Remove("NavigationViewItemForegroundPressed");
        res.Remove("NavigationViewItemForegroundSelected");
        res.Remove("NavigationViewItemForegroundSelectedPointerOver");
        res.Remove("NavigationViewItemForegroundSelectedPressed");

        navItem.RequestedTheme = ElementTheme.Default;
    }

    private static DependencyProperty? GetForegroundProperty(FrameworkElement el) => el switch
    {
        TextBlock => TextBlock.ForegroundProperty,
        IconElement => IconElement.ForegroundProperty,
        Control => Control.ForegroundProperty,
        _ => null
    };

    private void UpdatePlayBarFloatingShadow(bool enableShadow)
    {
        if (!enableShadow)
        {
            RestorePlayBarFloatingShadow();
            return;
        }

        if (_playBarShadowApplied)
            return;

        try
        {
            if (_floatingIslandShadow == null)
            {
                _floatingIslandShadow = new ThemeShadow();
                _floatingIslandShadow.Receivers.Add(GlobalWindowBaseBgLayer);
            }
            BottomPlayBarBorder.Shadow = _floatingIslandShadow;
            BottomPlayBarBorder.Translation = new Vector3(0f, 0f, 20f);
            _playBarShadowApplied = true;
        }
        catch
        {
        }
    }

    private void RestorePlayBarFloatingShadow()
    {
        if (!_playBarShadowApplied)
            return;

        try
        {
            BottomPlayBarBorder.Shadow = null;
            BottomPlayBarBorder.Translation = Vector3.Zero;
        }
        catch
        {
        }
        _playBarShadowApplied = false;
    }

    private bool TryBuildPassContext(out AdaptivePassContext ctx)
    {
        ctx = default;
        if (!IsAdaptiveWallpaperTextActive || _activeAdaptiveRenderedProfile == null)
            return false;

        double winW = RootLayout.ActualWidth;
        double winH = RootLayout.ActualHeight;
        if (winW <= 10 || winH <= 10)
            return false;

        var profile = _activeAdaptiveRenderedProfile;
        var maskCol = ThemeService.ParseColorOrDefault(profile.BackgroundMaskColorHex, Color.FromArgb(255, 9, 13, 22));
        double maskAlpha = Math.Clamp(profile.BackgroundMaskOpacity / 100.0, 0.0, 1.0);

        bool useLens = IsLiquidLensPassActive;
        bool useTextAdapt = IsWallpaperAdaptiveTextPassActive;
        if (!useLens && !useTextAdapt)
            return false;

        double imgLeft = 0, imgTop = 0, imgW = winW, imgH = winH;
        if (_activeWallpaperColorGrid is { } grid)
        {
            double scale = Math.Max(winW / Math.Max(1.0, grid.ImageWidth), winH / Math.Max(1.0, grid.ImageHeight));
            imgW = grid.ImageWidth * scale;
            imgH = grid.ImageHeight * scale;
            imgLeft = (winW - imgW) * 0.5;
            imgTop = (winH - imgH) * 0.5;
        }

        ctx = new AdaptivePassContext(
            winW,
            winH,
            imgLeft,
            imgTop,
            imgW,
            imgH,
            maskCol,
            maskAlpha,
            IsLight: IsCurrentUiLightMode,
            Accent: ThemeService.ParseColorOrDefault(profile.AccentColorHex, Color.FromArgb(255, 16, 185, 129)),
            UseLiquidLens: useLens,
            UseWallpaperAdaptiveText: useTextAdapt);
        return true;
    }

    private void ExecuteAdaptiveWallpaperTextPass()
    {
        if (!TryBuildPassContext(out var ctx))
            return;

        var profile = _activeAdaptiveRenderedProfile!;
        _hasActiveAdaptiveOverrides = true;

        // 1. 顶部标题栏微衬底（仅在开启壁纸智能可读性且未自定义顶栏时，为极端反差顶栏提供轻盈过渡）
        if (!profile.EnableCustomTitleBar && ctx.UseWallpaperAdaptiveText)
        {
            ApplyTitleBarVeilGradient(in ctx);
        }

        // 2. 底部悬浮灵动岛空气体积阴影
        bool wantIslandShadow = ctx.UseLiquidLens &&
                                !profile.EnableCustomPlayBar &&
                                string.Equals(profile.PlayBarLayoutMode, "FloatingIsland", StringComparison.OrdinalIgnoreCase);
        UpdatePlayBarFloatingShadow(wantIslandShadow);

        if (!ctx.UseWallpaperAdaptiveText && _bareTextStates.Count > 0)
        {
            RestoreAllBareTextOverrides();
        }

        // 3. 扫描可见树：为液态玻璃卡片挂载原生 DWM 水滴边缘折射环，并对壁纸上的文字做局部高对比度适配
        var plateStyle = (ctx.UseLiquidLens || ctx.UseWallpaperAdaptiveText) &&
                         Application.Current.Resources.TryGetValue("FloatingHeaderPlateStyle", out var ps)
            ? ps as Style
            : null;
        TraverseForGlassAndReadableTexts(RootLayout, in ctx, plateStyle, insideGlassSurface: false);
        PruneUnloadedAdaptiveEntries(ctx.IsLight);
    }

    private void PruneUnloadedAdaptiveEntries(bool isLightUi)
    {
        if (_bareTextStates.Count > 360)
        {
            List<FrameworkElement>? staleTexts = null;
            foreach (var (el, st) in _bareTextStates)
            {
                if (!el.IsLoaded)
                {
                    var fgProp = GetForegroundProperty(el);
                    if (fgProp != null && st.IsOverridden)
                    {
                        RestoreSingleBareTextElement(el, st, fgProp, isLightUi);
                    }
                    staleTexts ??= new List<FrameworkElement>();
                    staleTexts.Add(el);
                }
            }
            if (staleTexts != null)
            {
                foreach (var k in staleTexts)
                    _bareTextStates.Remove(k);
            }
        }

        if (_veilSurfaceStates.Count > 180)
        {
            List<DependencyObject>? staleSurfaces = null;
            foreach (var (target, state) in _veilSurfaceStates)
            {
                if (target is FrameworkElement { IsLoaded: false } fe)
                {
                    UIElement? lensHost = (UIElement?)state.UnderlayHost ?? fe;
                    state.LensAttachment?.DetachAndDispose(lensHost);
                    state.LensAttachment = null;
                    staleSurfaces ??= new List<DependencyObject>();
                    staleSurfaces.Add(target);
                }
            }
            if (staleSurfaces != null)
            {
                foreach (var k in staleSurfaces)
                    _veilSurfaceStates.Remove(k);
            }
        }
    }

    private void TraverseForGlassAndReadableTexts(
        DependencyObject node,
        in AdaptivePassContext ctx,
        Style? plateStyle,
        bool insideGlassSurface)
    {
        if (ReferenceEquals(node, GlobalWindowBaseBgLayer) ||
            ReferenceEquals(node, GlobalParticleOverlayCanvas) ||
            ReferenceEquals(node, MiniPreviewWindowOuter))
        {
            return;
        }

        if (node is UIElement uie)
        {
            if (uie.Visibility != Visibility.Visible || uie.Opacity <= 0.05)
                return;

            if (string.Equals(AutomationProperties.GetAutomationId(uie), SkipAdaptiveTextTag, StringComparison.Ordinal))
                return;
        }

        if (node is ScrollViewer sv && !_hookedAdaptiveScrollViewers.TryGetValue(sv, out _))
        {
            _hookedAdaptiveScrollViewers.Add(sv, new object());
            sv.ViewChanged += (_, _) =>
            {
                if (IsAdaptiveWallpaperTextActive)
                {
                    // 壁纸与边缘折射已由 DWM BackdropBrush 240Hz 硬件实时完成；此处仅轻量同步新滚入视口的虚拟化卡片与文字明暗
                    ScheduleAdaptiveWallpaperTextPass();
                }
            };
        }

        bool nowInsideGlass = insideGlassSurface;
        if (!insideGlassSurface && node is FrameworkElement fe && TryAttachLiquidGlassSurface(fe, in ctx, plateStyle))
        {
            nowInsideGlass = true;
            // 若当前不需要按壁纸位置自适应卡片内部字色，命中顶层玻璃表面后即可剪枝返回，避免父子卡片双重边缘环
            if (!ctx.UseWallpaperAdaptiveText)
            {
                return;
            }
        }

        // 跳过不透明实心按钮（如主色选中按钮）、输入框、下拉框内部文字，绝不破坏固定白字或白底输入字色
        if (node is TextBox or PasswordBox or AutoSuggestBox or ComboBox)
            return;

        if (node is Button btn)
        {
            if (btn.Background is SolidColorBrush scb && scb.Color.A > 130)
                return;
            if (btn.Background is LinearGradientBrush)
                return;
        }

        if (node is Border solidBorder && !ReferenceEquals(solidBorder, BottomPlayBarBorder) &&
            solidBorder.Background is SolidColorBrush bsc && bsc.Color.A > 140)
        {
            return;
        }

        // 在自定义图片壁纸开启智能可读性时，对落在反差不足背景上的文字/图标/字符串内容控件自适应切换高对比度字色
        // 注意：直接作用于外层 Button / NavigationViewItem 的 Control.ForegroundProperty，绝不覆写模板内部 ContentPresenter 的 TemplateBinding！
        if (ctx.UseWallpaperAdaptiveText && node is FrameworkElement feLeaf)
        {
            if (feLeaf is TextBlock or FontIcon)
            {
                AdaptTextOrIconOnClearWallpaper(feLeaf, in ctx);
                return;
            }

            if (feLeaf is NavigationViewItem or NavigationViewItemHeader ||
                (feLeaf is Button btnLeaf && btnLeaf.ActualHeight <= 56))
            {
                AdaptTextOrIconOnClearWallpaper(feLeaf, in ctx);
                // 绝不深入 Button / NavigationViewItem 控件模板内部，避免触碰模板内 ContentPresenter / TextBlock 破坏 VisualStateManager 状态机
                return;
            }
        }

        int childCount = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < childCount; i++)
        {
            TraverseForGlassAndReadableTexts(VisualTreeHelper.GetChild(node, i), in ctx, plateStyle, nowInsideGlass);
        }
    }

    private static readonly SolidColorBrush s_transparentBorderBrush = new(Color.FromArgb(0, 0, 0, 0));

    private static FrameworkElement EnsureOrUpdateLensHostElement(FrameworkElement fe, VeilSurfaceState state)
    {
        if (fe is Border cardBorder && cardBorder.Child is Grid innerGrid)
        {
            if (state.UnderlayHost == null || !ReferenceEquals(state.UnderlayParentPanel, innerGrid))
            {
                if (state.UnderlayHost != null && state.UnderlayParentPanel != null)
                {
                    try
                    {
                        state.UnderlayParentPanel.Children.Remove(state.UnderlayHost);
                    }
                    catch
                    {
                    }
                }

                var host = new Border
                {
                    IsHitTestVisible = false,
                    Background = null,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                };
                AutomationProperties.SetAutomationId(host, SkipAdaptiveTextTag);
                Grid.SetRow(host, 0);
                Grid.SetColumn(host, 0);
                Grid.SetRowSpan(host, 99);
                Grid.SetColumnSpan(host, 99);

                innerGrid.Children.Insert(0, host);
                state.UnderlayHost = host;
                state.UnderlayParentPanel = innerGrid;
            }

            var pad = cardBorder.Padding;
            var bt = cardBorder.BorderThickness;
            var wantMargin = new Thickness(
                -(pad.Left + bt.Left),
                -(pad.Top + bt.Top),
                -(pad.Right + bt.Right),
                -(pad.Bottom + bt.Bottom));
            if (state.UnderlayHost.Margin != wantMargin)
            {
                state.UnderlayHost.Margin = wantMargin;
            }

            return state.UnderlayHost;
        }

        return fe;
    }

    private bool TryAttachLiquidGlassSurface(FrameworkElement fe, in AdaptivePassContext ctx, Style? plateStyle)
    {
        if (!ctx.UseLiquidLens)
            return false;

        // 左侧导航栏、紧凑胶囊按钮与章节文字标题保持轻盈纯净（章节标题已由逐字对比度引擎自适应变白/变深，无需再套矩形贴纸框）
        if (fe is NavigationView or NavigationViewItem or Button)
            return false;

        if (plateStyle != null && fe is Border plateBorder && ReferenceEquals(plateBorder.Style, plateStyle))
            return false;

        DependencyProperty? bgProp = GetBackgroundProperty(fe);
        if (bgProp == null)
            return false;

        bool isHeroCard = ReferenceEquals(fe, DailyHeroCardBorder) || ReferenceEquals(fe, FmHeroCardBorder);
        Brush? effective = fe.GetValue(bgProp) as Brush;

        _veilSurfaceStates.TryGetValue(fe, out var existingState);
        bool alreadyManagedByUs = existingState is { IsBackgroundOverriddenByUs: true } ||
                                  existingState?.LensAttachment != null;

        if (!isHeroCard && !alreadyManagedByUs && effective is not LiquidGlassCompositionBrush)
            return false;

        if (fe.ActualWidth < 40 || fe.ActualHeight < 40)
            return true;

        VeilSurfaceState state = existingState ?? TrackVeilSurface(fe, bgProp);
        state.ForceDarkSurface = isHeroCard;
        state.CachedIntensity = ReferenceEquals(fe, BottomPlayBarBorder)
            ? 1.15
            : isHeroCard
                ? 1.16
                : 1.08;

        // 首页双英雄大卡在液态玻璃模式下赋予实时清澈水晶画刷
        if (isHeroCard)
        {
            bool surfLight = false;
            if (state.OwnGlassBrush == null || state.OwnGlassBrush.IsLight != surfLight)
            {
                state.OwnGlassBrush = new LiquidGlassCompositionBrush(
                    ctx.Accent,
                    surfLight,
                    intensity: state.CachedIntensity,
                    blurAmount: 0.45f);
            }

            if (!state.IsBackgroundOverriddenByUs)
            {
                fe.SetValue(bgProp, state.OwnGlassBrush);
                state.IsBackgroundOverriddenByUs = true;
            }
        }

        // 隐藏卡片自带的平面 1px XAML 灰白描边，完全交由原生 3D 水滴立体唇边呈现（切回普通主题时 100% 还原）
        if (fe is Border glassBorder && !state.IsBorderBrushOverridden)
        {
            var origBb = glassBorder.ReadLocalValue(Border.BorderBrushProperty);
            state.HadOriginalLocalBorderBrush = origBb != DependencyProperty.UnsetValue;
            state.OriginalLocalBorderBrush = origBb;
            glassBorder.BorderBrush = s_transparentBorderBrush;
            state.IsBorderBrushOverridden = true;
        }

        ApplySmoothRoundedGlassClip(fe, state);

        double radius = ResolveElementCornerRadius(fe);
        if (radius >= 4.0)
        {
            state.LensAttachment ??= new OpticalGlassAttachment();
            bool surfLight = !state.ForceDarkSurface && ctx.IsLight;
            FrameworkElement lensHost = EnsureOrUpdateLensHostElement(fe, state);
            _opticalGlassRenderer.AttachOrUpdateLensVisual(
                lensHost,
                state.LensAttachment,
                radius,
                surfLight,
                state.CachedIntensity,
                fe.ActualWidth,
                fe.ActualHeight);
        }
        else if (state.LensAttachment != null)
        {
            // 例如底部播放栏从悬浮灵动岛切为贴底直角（CornerRadius=0）时，自动移除圆角折射环
            UIElement? lensHost = (UIElement?)state.UnderlayHost ?? fe;
            state.LensAttachment.DetachAndDispose(lensHost);
            state.LensAttachment = null;
        }

        return true;
    }

    private void AdaptTextOrIconOnClearWallpaper(FrameworkElement el, in AdaptivePassContext ctx)
    {
        if (el is TextBlock tb && string.IsNullOrWhiteSpace(tb.Text))
            return;
        if (el.ActualWidth < 4 || el.ActualHeight < 4)
            return;

        var fgProp = GetForegroundProperty(el);
        if (fgProp == null)
            return;

        _bareTextStates.TryGetValue(el, out var st);
        if (st == null)
        {
            bool origIsLight = !ctx.IsLight;
            // 保护显式高色度品牌彩色文字与彩色图标（如粉红心动爱心、「使用中」青色徽章）；注意低明度墨黑 #0F172A 的 RGB 极差仅 27，绝不可误判跳过
            if (el.GetValue(fgProp) is SolidColorBrush effScb)
            {
                int maxC = Math.Max(effScb.Color.R, Math.Max(effScb.Color.G, effScb.Color.B));
                int minC = Math.Min(effScb.Color.R, Math.Min(effScb.Color.G, effScb.Color.B));
                if (maxC - minC > 68)
                    return;

                double origLum = (0.2126 * effScb.Color.R + 0.7152 * effScb.Color.G + 0.0722 * effScb.Color.B) / 255.0;
                origIsLight = origLum > 0.52;
            }

            var curLocal = el.ReadLocalValue(fgProp);
            bool isSystemDynamicBrush = false;
            if (curLocal is Brush localBr)
            {
                if ((RootLayout.Resources.TryGetValue("TextFillColorPrimaryBrush", out var pObj) && ReferenceEquals(localBr, pObj)) ||
                    (RootLayout.Resources.TryGetValue("TextFillColorSecondaryBrush", out var sObj) && ReferenceEquals(localBr, sObj)) ||
                    (RootLayout.Resources.TryGetValue("TextFillColorTertiaryBrush", out var tObj) && ReferenceEquals(localBr, tObj)))
                {
                    isSystemDynamicBrush = true;
                }
            }

            bool isSecondary = false;
            if (el is TextBlock textBlk && textBlk.FontSize <= 13.5 && textBlk.FontWeight.Weight < 600)
            {
                isSecondary = true;
            }

            st = new BareTextOverrideState
            {
                HadOriginalLocalForeground = curLocal is Brush &&
                                             !isSystemDynamicBrush &&
                                             el is not NavigationViewItem and not NavigationViewItemHeader,
                OriginalLocalForeground = curLocal,
                OriginalIsLightText = origIsLight,
                LastWasLightText = origIsLight,
                IsSecondaryTone = isSecondary
            };
            _bareTextStates[el] = st;
        }

        if (!TryGetElementViewportRect(el, in ctx, out double vx0, out double vy0, out double vx1, out double vy1))
            return;

        double wallGray = SampleEffectiveWallpaperGray(in ctx, vx0, vy0, vx1, vy1);
        double lStar = SrgbGrayToLStar(wallGray);

        // 带对比度滞回区间（L* 55 ~ 63），彻底防止临界区滚动微闪烁
        bool targetLightText;
        if (!st.OriginalIsLightText)
        {
            double flipToLightThreshold = st.IsOverridden && st.LastWasLightText ? 62.0 : 55.0;
            targetLightText = lStar < flipToLightThreshold;
        }
        else
        {
            double flipToDarkThreshold = st.IsOverridden && !st.LastWasLightText ? 66.0 : 73.0;
            targetLightText = lStar <= flipToDarkThreshold;
        }

        if (targetLightText == st.OriginalIsLightText)
        {
            if (st.IsOverridden)
            {
                RestoreSingleBareTextElement(el, st, fgProp, ctx.IsLight);
                st.IsOverridden = false;
            }
            st.LastWasLightText = targetLightText;
            return;
        }

        Color targetCol = targetLightText
            ? (st.IsSecondaryTone ? Color.FromArgb(232, 226, 232, 240) : Color.FromArgb(250, 248, 250, 252))
            : (st.IsSecondaryTone ? Color.FromArgb(232, 51, 65, 85) : Color.FromArgb(250, 15, 23, 42));

        if (st.AdaptiveBrush == null)
        {
            st.AdaptiveBrush = new SolidColorBrush(targetCol);
        }
        else if (st.AdaptiveBrush.Color != targetCol)
        {
            st.AdaptiveBrush.Color = targetCol;
            if (st.IsOverridden)
            {
                SetNavItemAdaptiveStateResources(el, st.AdaptiveBrush, targetLightText);
            }
        }

        if (!st.IsOverridden)
        {
            el.SetValue(fgProp, st.AdaptiveBrush);
            SetNavItemAdaptiveStateResources(el, st.AdaptiveBrush, targetLightText);
            st.IsOverridden = true;
        }
        st.LastWasLightText = targetLightText;
    }

    private static double ResolveElementCornerRadius(FrameworkElement fe)
    {
        CornerRadius cr = fe switch
        {
            Border b => b.CornerRadius,
            Control c => c.CornerRadius,
            _ => default
        };
        return Math.Max(0.0, cr.TopLeft);
    }

    private void ApplySmoothRoundedGlassClip(FrameworkElement fe, VeilSurfaceState state)
    {
        if (fe is Border border)
        {
            if (border.BackgroundSizing != BackgroundSizing.OuterBorderEdge)
            {
                if (!state.SizingTouched)
                {
                    state.OriginalSizing = border.BackgroundSizing;
                    state.SizingTouched = true;
                }
                border.BackgroundSizing = BackgroundSizing.OuterBorderEdge;
            }

            // 为大尺寸液态玻璃卡片赋予饱满的 24px 水滴圆角（对标效果图大斜面圆角，歌单小卡保持原有紧凑圆角）
            if (fe.ActualWidth >= 280 && fe.ActualHeight >= 110 && border.CornerRadius.TopLeft is >= 8 and < 24)
            {
                if (!state.CornerRadiusTouched)
                {
                    state.OriginalCornerRadius = border.CornerRadius;
                    state.CornerRadiusTouched = true;
                }
                border.CornerRadius = new CornerRadius(24);
            }
        }

        if (!_hookedGlassSizeElements.TryGetValue(fe, out _))
        {
            _hookedGlassSizeElements.Add(fe, new object());
            fe.SizeChanged += (sender, _) =>
            {
                if (sender is FrameworkElement el &&
                    _veilSurfaceStates.TryGetValue(el, out var st) &&
                    st.LensAttachment != null &&
                    IsLiquidLensPassActive)
                {
                    double rad = ResolveElementCornerRadius(el);
                    if (rad >= 4.0)
                    {
                        bool surfLight = !st.ForceDarkSurface && IsCurrentUiLightMode;
                        FrameworkElement lensHost = EnsureOrUpdateLensHostElement(el, st);
                        _opticalGlassRenderer.AttachOrUpdateLensVisual(
                            lensHost,
                            st.LensAttachment,
                            rad,
                            surfLight,
                            st.CachedIntensity,
                            el.ActualWidth,
                            el.ActualHeight);
                    }
                }
            };
        }
    }

    private static void RestoreSmoothRoundedGlassClip(DependencyObject target, VeilSurfaceState state)
    {
        if (state.GeometricClip != null && target is UIElement uie)
        {
            ElementCompositionPreview.GetElementVisual(uie).Clip = null;
            state.GeometricClip.Dispose();
            state.RoundedGeometry?.Dispose();
            state.GeometricClip = null;
            state.RoundedGeometry = null;
            state.AppliedClipW = 0;
            state.AppliedClipH = hZero();
            state.AppliedClipRadius = 0;
        }

        if (state.SizingTouched && target is Border border)
        {
            border.BackgroundSizing = state.OriginalSizing;
            state.SizingTouched = false;
        }

        if (state.CornerRadiusTouched && target is Border crBorder)
        {
            crBorder.CornerRadius = state.OriginalCornerRadius;
            state.CornerRadiusTouched = false;
        }

        static float hZero() => 0f;
    }

    private VeilSurfaceState TrackVeilSurface(DependencyObject element, DependencyProperty bgProp)
    {
        if (_veilSurfaceStates.TryGetValue(element, out var existing))
            return existing;

        var origLocal = element.ReadLocalValue(bgProp);
        var created = new VeilSurfaceState
        {
            OriginalRecorded = true,
            HadOriginalLocalBackground = origLocal != DependencyProperty.UnsetValue,
            OriginalLocalBackground = origLocal
        };
        _veilSurfaceStates[element] = created;
        return created;
    }

    private void ApplyTitleBarVeilGradient(in AdaptivePassContext ctx)
    {
        double barH = Math.Max(32.0, AppTitleBar.ActualHeight);
        if (!_titleBarVeilApplied)
        {
            var local = AppTitleBar.ReadLocalValue(Panel.BackgroundProperty);
            _titleBarHadLocalBackground = local != DependencyProperty.UnsetValue;
            _titleBarOriginalBackground = local;
            _titleBarVeilBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };
            for (int i = 0; i < TitleBarVeilSegments; i++)
            {
                _titleBarVeilBrush.GradientStops.Add(new GradientStop
                {
                    Offset = (i + 0.5) / TitleBarVeilSegments,
                    Color = Color.FromArgb(0, 0, 0, 0)
                });
            }
            AppTitleBar.Background = _titleBarVeilBrush;
            _titleBarVeilApplied = true;
        }

        if (_titleBarVeilBrush == null)
            return;

        Color veilBase = ctx.IsLight ? Color.FromArgb(255, 250, 251, 253) : Color.FromArgb(255, 10, 13, 22);
        double segW = ctx.WinW / TitleBarVeilSegments;
        for (int i = 0; i < TitleBarVeilSegments; i++)
        {
            double wallGray = SampleEffectiveWallpaperGray(in ctx, i * segW, 0, (i + 1) * segW, barH);
            double lStar = SrgbGrayToLStar(wallGray);
            double veil = 0.0;
            if (ctx.IsLight && lStar < 48.0)
            {
                veil = Math.Clamp((48.0 - lStar) / 48.0 * MaxTitleBarVeil, 0.0, MaxTitleBarVeil);
            }
            else if (!ctx.IsLight && lStar > 74.0)
            {
                veil = Math.Clamp((lStar - 74.0) / 26.0 * MaxTitleBarVeil, 0.0, MaxTitleBarVeil);
            }

            byte a = (byte)Math.Round(veil * 255.0);
            var stop = _titleBarVeilBrush.GradientStops[i];
            if (Math.Abs(stop.Color.A - a) >= 5 || (a == 0 && stop.Color.A != 0))
            {
                stop.Color = Color.FromArgb(a, veilBase.R, veilBase.G, veilBase.B);
            }
        }
    }

    private double SampleEffectiveWallpaperGray(in AdaptivePassContext ctx, double x0, double y0, double x1, double y1)
    {
        var grid = _activeWallpaperColorGrid;
        if (grid == null || x1 - x0 < 1 || y1 - y0 < 1)
            return ctx.IsLight ? 1.0 : 0.0;

        const int cols = 3;
        double tw = (x1 - x0) / cols;
        double sum = 0, worst = ctx.IsLight ? 1.0 : 0.0;
        for (int c = 0; c < cols; c++)
        {
            var (wR, wG, wB) = grid.SampleAreaAverageRgb(
                x0 + c * tw, y0, x0 + (c + 1) * tw, y1,
                ctx.ImgLeft, ctx.ImgTop, ctx.ImgW, ctx.ImgH);

            double rr = wR * (1.0 - ctx.MaskAlpha) + ctx.MaskCol.R * ctx.MaskAlpha;
            double gg = wG * (1.0 - ctx.MaskAlpha) + ctx.MaskCol.G * ctx.MaskAlpha;
            double bb = wB * (1.0 - ctx.MaskAlpha) + ctx.MaskCol.B * ctx.MaskAlpha;
            double gray = Math.Clamp((0.2126 * rr + 0.7152 * gg + 0.0722 * bb) / 255.0, 0.0, 1.0);

            sum += gray;
            worst = ctx.IsLight ? Math.Min(worst, gray) : Math.Max(worst, gray);
        }

        double mean = sum / cols;
        return worst * 0.55 + mean * 0.45;
    }

    private static double SrgbGrayToLStar(double srgbGray)
    {
        double c = Math.Clamp(srgbGray, 0.0, 1.0);
        double y = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        return y <= 0.008856 ? 903.3 * y : 116.0 * Math.Pow(y, 1.0 / 3.0) - 16.0;
    }

    private bool TryGetElementViewportRect(
        FrameworkElement fe,
        in AdaptivePassContext ctx,
        out double vx0,
        out double vy0,
        out double vx1,
        out double vy1)
    {
        vx0 = vy0 = vx1 = vy1 = 0;
        double w = fe.ActualWidth;
        double h = fe.ActualHeight;
        if (w <= 1 || h <= 1) return false;

        Point topLeft;
        try
        {
            topLeft = fe.TransformToVisual(RootLayout).TransformPoint(new Point(0, 0));
        }
        catch
        {
            return false;
        }

        if (topLeft.Y + h < 0 || topLeft.Y > ctx.WinH || topLeft.X + w < 0 || topLeft.X > ctx.WinW)
            return false;

        vx0 = Math.Clamp(topLeft.X, 0, ctx.WinW);
        vy0 = Math.Clamp(topLeft.Y, 0, ctx.WinH);
        vx1 = Math.Clamp(topLeft.X + w, 0, ctx.WinW);
        vy1 = Math.Clamp(topLeft.Y + h, 0, ctx.WinH);
        if (vx1 - vx0 < 2) vx1 = Math.Min(ctx.WinW, vx0 + Math.Min(w, 8));
        if (vy1 - vy0 < 2) vy1 = Math.Min(ctx.WinH, vy0 + Math.Min(h, 8));
        return true;
    }

    private static DependencyProperty? GetBackgroundProperty(DependencyObject element) => element switch
    {
        Border => Border.BackgroundProperty,
        Panel => Panel.BackgroundProperty,
        Control => Control.BackgroundProperty,
        _ => null
    };
}
