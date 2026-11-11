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
/// MainWindow 分部类：自定义壁纸「字色固定 + 玻璃浓度自适应」可读性引擎。
/// 浅色基调全局深色字、深色基调全局白字，绝不按壁纸翻转字色；
/// 改为按每块液态玻璃表面背后的壁纸明暗，在合成线程平滑调节其顶层雾化层浓度，保证全局统一且清晰可读。
/// </summary>
public sealed partial class MainWindow
{
    public const string SkipAdaptiveTextTag = "SkipAdaptiveWallpaperText";

    // 浅色基调（固定深色字）要求玻璃表面最低明度；深色基调（固定白字）要求玻璃表面最高明度（CIE L*）
    private const double LightModeMinSurfaceLStar = 70.0;
    private const double DarkModeMaxSurfaceLStar = 42.0;

    // 雾化浓度上限与更新死区（避免滚动时微小抖动反复触发动画）
    private const double MaxContrastVeil = 0.80;
    private const double MaxTitleBarVeil = 0.72;
    private const double VeilUpdateDeadZone = 0.03;
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
        Color Accent);

    private sealed class VeilSurfaceState
    {
        // 为共享 ThemeResource 玻璃卡片单独创建并赋给本元素的独立画刷（还原时清除本地值即可回到主题资源）
        public LiquidGlassCompositionBrush? OwnBrush;

        // 原生 DWM 合成器圆角几何抗锯齿裁剪（无白边状态下消除玻璃圆角阶梯像素）
        public CompositionRoundedRectangleGeometry? RoundedGeometry;
        public CompositionGeometricClip? GeometricClip;
        public float AppliedClipW;
        public float AppliedClipH;
        public float AppliedClipRadius;
        public bool SizingTouched;
        public BackgroundSizing OriginalSizing;
    }

    private readonly ConditionalWeakTable<DependencyObject, VeilSurfaceState> _veilSurfaceStates = new();
    private readonly List<WeakReference<DependencyObject>> _trackedVeilSurfaces = new();
    private readonly ConditionalWeakTable<ScrollViewer, object> _hookedAdaptiveScrollViewers = new();
    private readonly ConditionalWeakTable<FrameworkElement, object> _hookedGlassSizeElements = new();

    private bool _titleBarVeilApplied;
    private bool _titleBarHadLocalBackground;
    private object? _titleBarOriginalBackground;
    private LinearGradientBrush? _titleBarVeilBrush;

    private WallpaperColorGrid? _activeWallpaperColorGrid;
    private CustomThemeProfile? _activeAdaptiveRenderedProfile;
    private DispatcherQueueTimer? _adaptiveTextThrottleTimer;
    private bool _adaptivePassPending;
    private bool _hasActiveAdaptiveOverrides;

    private bool IsAdaptiveWallpaperTextActive =>
        _activeAdaptiveRenderedProfile != null &&
        string.Equals(_activeAdaptiveRenderedProfile.BackdropType, "Image", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(_activeAdaptiveRenderedProfile.BackgroundImagePath) &&
        _activeAdaptiveRenderedProfile.EnableAdaptiveWallpaperText &&
        _activeWallpaperColorGrid != null &&
        GlobalWallpaperImage.Visibility == Visibility.Visible;

    /// <summary>
    /// 初始化壁纸玻璃浓度自适应节流定时器与窗口尺寸监听
    /// </summary>
    private void InitializeAdaptiveWallpaperTextEngine()
    {
        _adaptiveTextThrottleTimer = DispatcherQueue.CreateTimer();
        _adaptiveTextThrottleTimer.Interval = TimeSpan.FromMilliseconds(8);
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
    }

    /// <summary>
    /// 请求一次节流合并后的玻璃浓度自适应扫描（严格仅在自定义图片壁纸开启智能可读性时激活）
    /// </summary>
    public void ScheduleAdaptiveWallpaperTextPass(bool immediate = false)
    {
        if (!IsAdaptiveWallpaperTextActive)
        {
            if (_hasActiveAdaptiveOverrides)
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
    /// 关闭智能可读性或切离自定义壁纸时，100% 还原所有被接管的玻璃表面、标题胶囊与标题栏背景
    /// </summary>
    private void RestoreAllAdaptiveWallpaperTexts(bool? explicitTargetIsLight = null)
    {
        _adaptiveTextThrottleTimer?.Stop();
        _adaptivePassPending = false;

        for (int i = _trackedVeilSurfaces.Count - 1; i >= 0; i--)
        {
            if (!_trackedVeilSurfaces[i].TryGetTarget(out var target) ||
                !_veilSurfaceStates.TryGetValue(target, out var state))
            {
                continue;
            }

            var local = target.ReadLocalValue(GetBackgroundProperty(target)!);
            if (state.OwnBrush != null && Equals(local, state.OwnBrush))
            {
                target.ClearValue(GetBackgroundProperty(target)!);
            }
            else if (local is LiquidGlassCompositionBrush localGlass)
            {
                localGlass.SetContrastVeil(0, animate: false);
            }

            RestoreSmoothRoundedGlassClip(target, state);
            _veilSurfaceStates.Remove(target);
        }
        _trackedVeilSurfaces.Clear();

        ResetResourceGlassVeil(MainNavView.Resources, "NavigationViewDefaultPaneBackground", "NavigationViewExpandedPaneBackground", "NavigationViewTopPaneBackground");
        if (TopSearchBox != null)
        {
            ResetResourceGlassVeil(TopSearchBox.Resources, "TextControlBackground");
        }

        if (_titleBarVeilApplied)
        {
            if (ReferenceEquals(AppTitleBar.ReadLocalValue(Panel.BackgroundProperty), _titleBarVeilBrush))
            {
                if (_titleBarHadLocalBackground && _titleBarOriginalBackground is Brush originalBg)
                    AppTitleBar.Background = originalBg;
                else
                    AppTitleBar.ClearValue(Panel.BackgroundProperty);
            }
            _titleBarVeilApplied = false;
            _titleBarOriginalBackground = null;
            _titleBarVeilBrush = null;
        }

        _hasActiveAdaptiveOverrides = false;
        ConfigureTitleBarButtonsTheme();
    }

    private static void ResetResourceGlassVeil(ResourceDictionary res, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (res.TryGetValue(key, out var val) && val is LiquidGlassCompositionBrush glass)
            {
                glass.SetContrastVeil(0, animate: false);
            }
        }
    }

    private void ExecuteAdaptiveWallpaperTextPass()
    {
        if (!IsAdaptiveWallpaperTextActive || _activeWallpaperColorGrid == null || _activeAdaptiveRenderedProfile == null)
            return;

        double winW = RootLayout.ActualWidth;
        double winH = RootLayout.ActualHeight;
        if (winW <= 10 || winH <= 10)
            return;

        var grid = _activeWallpaperColorGrid;
        var profile = _activeAdaptiveRenderedProfile;
        var maskCol = ThemeService.ParseColorOrDefault(profile.BackgroundMaskColorHex, Color.FromArgb(255, 9, 13, 22));
        double maskAlpha = Math.Clamp(profile.BackgroundMaskOpacity / 100.0, 0.0, 1.0);

        double scale = Math.Max(winW / Math.Max(1.0, grid.ImageWidth), winH / Math.Max(1.0, grid.ImageHeight));
        double imgW = grid.ImageWidth * scale;
        double imgH = grid.ImageHeight * scale;

        var ctx = new AdaptivePassContext(
            winW, winH,
            (winW - imgW) * 0.5, (winH - imgH) * 0.5, imgW, imgH,
            maskCol, maskAlpha,
            IsLight: IsCurrentUiLightMode,
            Accent: ThemeService.ParseColorOrDefault(profile.AccentColorHex, Color.FromArgb(255, 16, 185, 129)));

        _hasActiveAdaptiveOverrides = true;

        // 1. 左侧导航栏（资源级独占玻璃画刷，整栏统一浓度）
        double navPaneW = MainNavView.IsPaneOpen ? Math.Max(180.0, MainNavView.OpenPaneLength) : 56.0;
        ApplyResourceGlassVeil(MainNavView.Resources, in ctx, 0, 40, navPaneW, Math.Max(120.0, winH - 96.0),
            "NavigationViewDefaultPaneBackground", "NavigationViewExpandedPaneBackground", "NavigationViewTopPaneBackground");

        // 2. 顶部搜索框
        if (TopSearchBox != null && TryGetVisibleViewportRect(TopSearchBox, in ctx, out double sx0, out double sy0, out double sx1, out double sy1))
        {
            ApplyResourceGlassVeil(TopSearchBox.Resources, in ctx, sx0, sy0, sx1, sy1, "TextControlBackground");
        }

        // 3. 顶部标题栏：水平平滑渐变雾化（左侧压在暗部时加深、右侧亮部保持通透，无任何硬边）
        if (!profile.EnableCustomTitleBar)
        {
            ApplyTitleBarVeilGradient(in ctx);
        }

        // 4. 主内容区所有液态玻璃卡片、胶囊按钮、悬浮标题玻璃牌与底部播放栏
        var plateStyle = Application.Current.Resources.TryGetValue("FloatingHeaderPlateStyle", out var ps) ? ps as Style : null;
        TraverseForGlassSurfaces(RootLayout, in ctx, plateStyle);
    }

    private void TraverseForGlassSurfaces(DependencyObject node, in AdaptivePassContext ctx, Style? plateStyle)
    {
        if (ReferenceEquals(node, GlobalWindowBaseBgLayer) ||
            ReferenceEquals(node, GlobalParticleOverlayCanvas) ||
            ReferenceEquals(node, MiniPreviewWindowOuter) ||
            ReferenceEquals(node, TopSearchBox))
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
                    ScheduleAdaptiveWallpaperTextPass();
                }
            };
        }

        // 命中顶层玻璃表面后不再深入：内部子元素的 Backdrop 采样已包含本层雾化后的画面
        if (node is FrameworkElement fe && TryApplyGlassSurfaceVeil(fe, in ctx, plateStyle))
            return;

        int childCount = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < childCount; i++)
        {
            TraverseForGlassSurfaces(VisualTreeHelper.GetChild(node, i), in ctx, plateStyle);
        }
    }

    private bool TryApplyGlassSurfaceVeil(FrameworkElement fe, in AdaptivePassContext ctx, Style? plateStyle)
    {
        DependencyProperty? bgProp = GetBackgroundProperty(fe);
        if (bgProp == null)
            return false;

        bool isPlate = plateStyle != null && fe is Border plateBorder && ReferenceEquals(plateBorder.Style, plateStyle);
        Brush? effective = fe.GetValue(bgProp) as Brush;
        if (!isPlate && effective is not LiquidGlassCompositionBrush)
            return false;

        if (fe.ActualWidth < 8 || fe.ActualHeight < 8)
            return true;

        // 解析本元素专属的玻璃画刷：本地独占画刷直接使用；共享主题资源或悬浮标题牌则创建独立实例
        var local = fe.ReadLocalValue(bgProp);
        VeilSurfaceState state;
        LiquidGlassCompositionBrush glass;
        if (local is LiquidGlassCompositionBrush localGlass)
        {
            glass = localGlass;
            state = TrackVeilSurface(fe);
        }
        else
        {
            state = TrackVeilSurface(fe);
            if (state.OwnBrush == null || state.OwnBrush.IsLight != ctx.IsLight)
            {
                state.OwnBrush = effective is LiquidGlassCompositionBrush shared
                    ? shared.CloneIndependent()
                    : new LiquidGlassCompositionBrush(ctx.Accent, ctx.IsLight, intensity: 0.52, blurAmount: 12.5f);
            }
            glass = state.OwnBrush;
            fe.SetValue(bgProp, glass);
        }

        ApplySmoothRoundedGlassClip(fe, state);

        if (!TryGetVisibleViewportRect(fe, in ctx, out double x0, out double y0, out double x1, out double y1))
            return true;

        double wallGray = SampleEffectiveWallpaperGray(in ctx, x0, y0, x1, y1);
        double veil = ComputeRequiredVeil(glass.EstimateSurfaceGray(wallGray), ctx.IsLight, MaxContrastVeil);
        if (Math.Abs(veil - glass.ContrastVeil) >= VeilUpdateDeadZone || (veil == 0 && glass.ContrastVeil > 0))
        {
            glass.SetContrastVeil(veil);
        }
        return true;
    }

    /// <summary>
    /// 在无白边状态下，使用原生 DWM 合成器圆角几何裁剪（CompositionGeometricClip）对液态玻璃表面边缘进行硬件矢量抗锯齿，
    /// 并将 Border 设为 OuterBorderEdge 消除 1px 透明边框造成的内缩接缝
    /// </summary>
    private void ApplySmoothRoundedGlassClip(FrameworkElement fe, VeilSurfaceState state)
    {
        if (fe is Border border && border.BackgroundSizing != BackgroundSizing.OuterBorderEdge)
        {
            if (!state.SizingTouched)
            {
                state.OriginalSizing = border.BackgroundSizing;
                state.SizingTouched = true;
            }
            border.BackgroundSizing = BackgroundSizing.OuterBorderEdge;
        }

        if (!_hookedGlassSizeElements.TryGetValue(fe, out _))
        {
            _hookedGlassSizeElements.Add(fe, new object());
            fe.SizeChanged += (sender, _) =>
            {
                if (IsAdaptiveWallpaperTextActive &&
                    sender is FrameworkElement el &&
                    _veilSurfaceStates.TryGetValue(el, out var st))
                {
                    UpdateRoundedClipGeometry(el, st);
                }
            };
        }

        UpdateRoundedClipGeometry(fe, state);
    }

    private static void UpdateRoundedClipGeometry(FrameworkElement fe, VeilSurfaceState state)
    {
        CornerRadius cr = fe switch
        {
            Border b => b.CornerRadius,
            Control c => c.CornerRadius,
            _ => default
        };

        bool uniformRounded = cr.TopLeft > 0.5 &&
                              Math.Abs(cr.TopLeft - cr.TopRight) < 0.2 &&
                              Math.Abs(cr.TopLeft - cr.BottomLeft) < 0.2 &&
                              Math.Abs(cr.TopLeft - cr.BottomRight) < 0.2;

        if (!uniformRounded)
        {
            if (state.GeometricClip != null)
            {
                ElementCompositionPreview.GetElementVisual(fe).Clip = null;
                state.GeometricClip.Dispose();
                state.RoundedGeometry?.Dispose();
                state.GeometricClip = null;
                state.RoundedGeometry = null;
                state.AppliedClipW = 0;
                state.AppliedClipH = 0;
                state.AppliedClipRadius = 0;
            }
            return;
        }

        float w = (float)fe.ActualWidth;
        float h = (float)fe.ActualHeight;
        if (w < 2f || h < 2f)
            return;

        float r = Math.Min((float)cr.TopLeft, Math.Min(w, h) * 0.5f);
        if (state.GeometricClip != null &&
            Math.Abs(state.AppliedClipW - w) < 0.25f &&
            Math.Abs(state.AppliedClipH - h) < 0.25f &&
            Math.Abs(state.AppliedClipRadius - r) < 0.1f)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(fe);
        state.RoundedGeometry ??= visual.Compositor.CreateRoundedRectangleGeometry();
        state.RoundedGeometry.Size = new Vector2(w, h);
        state.RoundedGeometry.CornerRadius = new Vector2(r, r);

        if (state.GeometricClip == null)
        {
            state.GeometricClip = visual.Compositor.CreateGeometricClip(state.RoundedGeometry);
            visual.Clip = state.GeometricClip;
        }

        state.AppliedClipW = w;
        state.AppliedClipH = h;
        state.AppliedClipRadius = r;
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
            state.AppliedClipH = 0;
            state.AppliedClipRadius = 0;
        }

        if (state.SizingTouched && target is Border border)
        {
            border.BackgroundSizing = state.OriginalSizing;
            state.SizingTouched = false;
        }
    }

    private VeilSurfaceState TrackVeilSurface(DependencyObject element)
    {
        if (_veilSurfaceStates.TryGetValue(element, out var existing))
            return existing;

        var created = new VeilSurfaceState();
        _veilSurfaceStates.Add(element, created);
        _trackedVeilSurfaces.Add(new WeakReference<DependencyObject>(element));
        return created;
    }

    private void ApplyResourceGlassVeil(ResourceDictionary res, in AdaptivePassContext ctx, double x0, double y0, double x1, double y1, params string[] keys)
    {
        LiquidGlassCompositionBrush? glass = null;
        foreach (var key in keys)
        {
            if (res.TryGetValue(key, out var val) && val is LiquidGlassCompositionBrush g)
            {
                glass = g;
                break;
            }
        }
        if (glass == null)
            return;

        double wallGray = SampleEffectiveWallpaperGray(in ctx, x0, y0, Math.Min(x1, ctx.WinW), Math.Min(y1, ctx.WinH));
        double veil = ComputeRequiredVeil(glass.EstimateSurfaceGray(wallGray), ctx.IsLight, MaxContrastVeil);
        if (Math.Abs(veil - glass.ContrastVeil) >= VeilUpdateDeadZone || (veil == 0 && glass.ContrastVeil > 0))
        {
            glass.SetContrastVeil(veil);
        }
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
            _titleBarVeilApplied = true;
        }

        if (_titleBarVeilBrush == null)
            return;

        Color veilBase = ctx.IsLight ? Color.FromArgb(255, 250, 251, 253) : Color.FromArgb(255, 10, 13, 22);
        double segW = ctx.WinW / TitleBarVeilSegments;
        for (int i = 0; i < TitleBarVeilSegments; i++)
        {
            double wallGray = SampleEffectiveWallpaperGray(in ctx, i * segW, 0, (i + 1) * segW, barH);
            double veil = ComputeRequiredVeil(wallGray, ctx.IsLight, MaxTitleBarVeil);
            byte a = (byte)Math.Round(veil * 255.0);
            var stop = _titleBarVeilBrush.GradientStops[i];
            if (Math.Abs(stop.Color.A - a) >= 6 || (a == 0 && stop.Color.A != 0))
            {
                stop.Color = Color.FromArgb(a, veilBase.R, veilBase.G, veilBase.B);
            }
        }

        if (!ReferenceEquals(AppTitleBar.ReadLocalValue(Panel.BackgroundProperty), _titleBarVeilBrush))
        {
            AppTitleBar.Background = _titleBarVeilBrush;
        }
    }

    /// <summary>
    /// 根据玻璃表面（未加雾化层）灰度反推所需雾化浓度：浅色基调把表面提亮到深色字清晰的明度，深色基调把表面压暗到白字清晰的明度
    /// </summary>
    private static double ComputeRequiredVeil(double surfaceGray, bool isLight, double maxVeil)
    {
        double veilGray = LiquidGlassCompositionBrush.GetVeilGray(isLight);
        if (isLight)
        {
            double target = LStarToSrgbGray(LightModeMinSurfaceLStar);
            if (surfaceGray >= target) return 0;
            return Math.Clamp((target - surfaceGray) / Math.Max(0.05, veilGray - surfaceGray), 0, maxVeil);
        }
        else
        {
            double target = LStarToSrgbGray(DarkModeMaxSurfaceLStar);
            if (surfaceGray <= target) return 0;
            return Math.Clamp((surfaceGray - target) / Math.Max(0.05, surfaceGray - veilGray), 0, maxVeil);
        }
    }

    /// <summary>
    /// 在可见区域内按 4×2 分块采样壁纸（含遮罩），以「最不利分块 65% + 平均 35%」作为有效灰度，确保卡片暗/亮一侧同样清晰
    /// </summary>
    private double SampleEffectiveWallpaperGray(in AdaptivePassContext ctx, double x0, double y0, double x1, double y1)
    {
        var grid = _activeWallpaperColorGrid;
        if (grid == null || x1 - x0 < 1 || y1 - y0 < 1)
            return ctx.IsLight ? 1.0 : 0.0;

        const int cols = 4, rows = 2;
        double tw = (x1 - x0) / cols, th = (y1 - y0) / rows;
        double sum = 0, worst = ctx.IsLight ? 1.0 : 0.0;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var (wR, wG, wB) = grid.SampleAreaAverageRgb(
                    x0 + c * tw, y0 + r * th, x0 + (c + 1) * tw, y0 + (r + 1) * th,
                    ctx.ImgLeft, ctx.ImgTop, ctx.ImgW, ctx.ImgH);

                double rr = wR * (1.0 - ctx.MaskAlpha) + ctx.MaskCol.R * ctx.MaskAlpha;
                double gg = wG * (1.0 - ctx.MaskAlpha) + ctx.MaskCol.G * ctx.MaskAlpha;
                double bb = wB * (1.0 - ctx.MaskAlpha) + ctx.MaskCol.B * ctx.MaskAlpha;
                double gray = Math.Clamp((0.2126 * rr + 0.7152 * gg + 0.0722 * bb) / 255.0, 0.0, 1.0);

                sum += gray;
                worst = ctx.IsLight ? Math.Min(worst, gray) : Math.Max(worst, gray);
            }
        }

        double mean = sum / (cols * rows);
        return worst * 0.65 + mean * 0.35;
    }

    private static double LStarToSrgbGray(double lStar)
    {
        double y = lStar > 8.0 ? Math.Pow((lStar + 16.0) / 116.0, 3) : lStar / 903.3;
        return y <= 0.0031308 ? 12.92 * y : 1.055 * Math.Pow(y, 1.0 / 2.4) - 0.055;
    }

    private bool TryGetVisibleViewportRect(
        FrameworkElement fe,
        in AdaptivePassContext ctx,
        out double x0,
        out double y0,
        out double x1,
        out double y1)
    {
        x0 = y0 = x1 = y1 = 0;
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

        // 裁剪至窗口可见视口，超高长卡片按当前屏幕实际可见区域采样
        x0 = Math.Clamp(topLeft.X, 0, ctx.WinW);
        y0 = Math.Clamp(topLeft.Y, 0, ctx.WinH);
        x1 = Math.Clamp(topLeft.X + w, 0, ctx.WinW);
        y1 = Math.Clamp(topLeft.Y + h, 0, ctx.WinH);
        return (x1 - x0) >= 2 && (y1 - y0) >= 2;
    }

    private static DependencyProperty? GetBackgroundProperty(DependencyObject element) => element switch
    {
        Border => Border.BackgroundProperty,
        Panel => Panel.BackgroundProperty,
        Control => Control.BackgroundProperty,
        _ => null
    };
}
