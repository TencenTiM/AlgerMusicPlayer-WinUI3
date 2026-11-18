using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace AlgerMusicPlayer.WinUI.Services.System;

public enum GlassSourceKind
{
    Background = 0,
    Content = 1
}

/// <summary>
/// 全局零回读 GPU 双共享层镜像中心（对照 Alger 液态玻璃规范与正式 Overlay 架构）：
/// 1. BackgroundSurface：直连 GlobalWindowBaseBgLayer，提供背景渐变与洋红/青流光；
/// 2. ContentSurface：直连主体工作区 ContentRoot (bodyHostGrid)，与浮层 Overlay 严格隔离避免自采样；
/// 既支撑底栏 0ms 穿底光学弯曲，也支撑播放列表等浮层使用 Blur 36 + 深青玻璃 #D00B2B36 高遮蔽共享渲染。
/// </summary>
public sealed class GlassHub : IDisposable
{
    // 静态全局共享背景采样源与降级开关（供 XAML 中全部 GlassPanel 复用）
    private static bool _backdropDegraded;
    public static Compositor? Compositor { get; private set; }
    public static UIElement? Root { get; private set; }
    public static UIElement? Background { get; private set; }
    public static CompositionVisualSurface? Surface { get; private set; }
    public static bool IsGpuAccelerationEnabled { get; private set; } = true;
    public static bool IsDegradedMode => _backdropDegraded || !IsGpuAccelerationEnabled;
    public static bool IsLightMode { get; private set; }
    public static event Action? StateChanged;

    /// <summary>
    /// 初始化全局共享背景采样纹理，必须在任何 GlassPanel 应用模板前调用。
    /// </summary>
    public static void Init(UIElement root, UIElement background)
    {
        if (root == null || background == null)
        {
            return;
        }

        Root = root;
        Background = background;
        var bgVisual = ElementCompositionPreview.GetElementVisual(background);
        Compositor = bgVisual.Compositor;

        Surface?.Dispose();
        Surface = Compositor.CreateVisualSurface();
        Surface.SourceVisual = bgVisual;

        if (background is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
        {
            Surface.SourceSize = new Vector2((float)fe.ActualWidth, (float)fe.ActualHeight);
        }

        var sizeAnim = Compositor.CreateExpressionAnimation("bg.Size");
        sizeAnim.SetReferenceParameter("bg", bgVisual);
        Surface.StartAnimation("SourceSize", sizeAnim);

        StateChanged?.Invoke();
    }

    /// <summary>
    /// 切换全局降级模式（Mica / 纯色 / WebView2 视频壁纸或关闭 GPU 加速时禁止硬采样背景层）。
    /// </summary>
    public static void SetDegradedMode(bool isDegraded, bool isLightMode = false)
    {
        bool prevEffective = IsDegradedMode;
        _backdropDegraded = isDegraded;
        bool nextEffective = IsDegradedMode;
        if (prevEffective == nextEffective && IsLightMode == isLightMode)
        {
            return;
        }

        IsLightMode = isLightMode;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 切换全局 GPU 硬件加速状态（同步联动 Win2D 软件回退与所有 GlassPanel 降级纯色呈现）。
    /// </summary>
    public static void SetGpuAccelerationEnabled(bool enabled)
    {
        LiquidGlass.WinUI.Effects.Win2DSdfRefractionRenderer.ForceSoftwareRendering = !enabled;
        if (IsGpuAccelerationEnabled == enabled)
        {
            return;
        }

        IsGpuAccelerationEnabled = enabled;
        StateChanged?.Invoke();
    }

    private sealed class PassUnderRingSlot
    {
        public ContainerVisual Root = null!;
        public CompositionRoundedRectangleGeometry ClipGeom = null!;
        public readonly SpriteVisual[] EdgeSprites = new SpriteVisual[4]; // 0:Top, 1:Bottom, 2:Left, 3:Right
        public readonly CompositionSurfaceBrush[] WarmBrushes = new CompositionSurfaceBrush[4];
        public readonly CompositionSurfaceBrush[] CoolBrushes = new CompositionSurfaceBrush[4];
        public readonly CompositionLinearGradientBrush[] MaskGradBrushes = new CompositionLinearGradientBrush[4];
        public readonly CompositionMaskBrush[] MaskBrushes = new CompositionMaskBrush[4];
        public readonly CompositionEffectBrush[] EffectBrushes = new CompositionEffectBrush[4];
        public double LastW;
        public double LastH;
        public double LastR;
        public bool LastLight;
    }

    private sealed class OverlaySurfaceSlot
    {
        public SpriteVisual Sprite = null!;
        public CompositionRoundedRectangleGeometry ClipGeom = null!;
        public CompositionSurfaceBrush? BgBrush;
        public CompositionSurfaceBrush? ContentBrush;
        public CompositionEffectBrush EffectBrush = null!;
        public bool UsesDualSource;
        public double LastW;
        public double LastH;
        public double LastR;
    }

    private Compositor? _compositor;
    private UIElement? _backgroundElement;
    private UIElement? _sourceElement;
    private CompositionVisualSurface? _backgroundVisualSurface;
    private CompositionVisualSurface? _bodyVisualSurface;
    private CompositionEffectFactory? _dispersionEffectFactory;
    private CompositionEffectFactory? _overlayDualGlassFactory;
    private CompositionEffectFactory? _overlaySingleGlassFactory;

    // 多浮层穿底折射环槽位 & 共享毛玻璃浮层槽位
    private readonly Dictionary<ContainerVisual, PassUnderRingSlot> _rings = new();
    private readonly Dictionary<Border, OverlaySurfaceSlot> _overlaySlots = new();
    private bool _singleSourceFallback;
    private bool _overlayDualRejected;
    private bool _isDisposed;

    public bool IsInitialized => _bodyVisualSurface != null && !_isDisposed;

    public CompositionVisualSurface? BackgroundSurface => _backgroundVisualSurface;
    public CompositionVisualSurface? ContentSurface => _bodyVisualSurface;

    public bool EnsureBackgroundInitialized(UIElement? backgroundLayer)
    {
        if (_isDisposed || backgroundLayer == null)
        {
            return false;
        }

        if (_backgroundVisualSurface != null && ReferenceEquals(_backgroundElement, backgroundLayer))
        {
            return true;
        }

        try
        {
            var bgVisual = ElementCompositionPreview.GetElementVisual(backgroundLayer);
            _compositor ??= bgVisual.Compositor;

            if (_backgroundVisualSurface != null)
            {
                DisposeOverlaySurfaceSlotsOnly();
                _backgroundVisualSurface.Dispose();
            }

            _backgroundElement = backgroundLayer;
            _backgroundVisualSurface = _compositor.CreateVisualSurface();
            _backgroundVisualSurface.SourceVisual = bgVisual;

            var sizeExpr = _compositor.CreateExpressionAnimation("bg.Size");
            sizeExpr.SetReferenceParameter("bg", bgVisual);
            _backgroundVisualSurface.StartAnimation("SourceSize", sizeExpr);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool EnsureInitialized(UIElement? bodyHostGrid)
    {
        if (_isDisposed || bodyHostGrid == null)
        {
            return false;
        }

        if (_bodyVisualSurface != null && ReferenceEquals(_sourceElement, bodyHostGrid))
        {
            return true;
        }

        try
        {
            DisposeDockRingVisualsOnly();
            DisposeOverlaySurfaceSlotsOnly();
            _bodyVisualSurface?.Dispose();
            _dispersionEffectFactory?.Dispose();

            var bodyVisual = ElementCompositionPreview.GetElementVisual(bodyHostGrid);
            _compositor = bodyVisual.Compositor;
            _sourceElement = bodyHostGrid;

            _bodyVisualSurface = _compositor.CreateVisualSurface();
            _bodyVisualSurface.SourceVisual = bodyVisual;

            var sizeExpr = _compositor.CreateExpressionAnimation("src.Size");
            sizeExpr.SetReferenceParameter("src", bodyVisual);
            _bodyVisualSurface.StartAnimation("SourceSize", sizeExpr);

            EnsureDispersionEffectFactory();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureDispersionEffectFactory()
    {
        if (_compositor == null || _dispersionEffectFactory != null)
        {
            return;
        }

        // 双通道红/青棱镜色散合成图：Warm (R=1,G=0,B=0) + Cool (R=0,G=1,B=1) Screen 无损重组 + 1.20x 宝石微饱与
        var warmMask = new Matrix5x4
        {
            M11 = 1f, M12 = 0f, M13 = 0f, M14 = 0f,
            M21 = 0f, M22 = 0f, M23 = 0f, M24 = 0f,
            M31 = 0f, M32 = 0f, M33 = 0f, M34 = 0f,
            M41 = 0f, M42 = 0f, M43 = 0f, M44 = 1f
        };
        var coolMask = new Matrix5x4
        {
            M11 = 0f, M12 = 0f, M13 = 0f, M14 = 0f,
            M21 = 0f, M22 = 1f, M23 = 0f, M24 = 0f,
            M31 = 0f, M32 = 0f, M33 = 1f, M34 = 0f,
            M41 = 0f, M42 = 0f, M43 = 0f, M44 = 1f
        };

        var effectGraph = new SaturationEffect
        {
            Saturation = 1.20f,
            Source = new BlendEffect
            {
                Mode = BlendEffectMode.Screen,
                Background = new ColorMatrixEffect
                {
                    ColorMatrix = warmMask,
                    Source = new CompositionEffectSourceParameter("WarmSrc")
                },
                Foreground = new ColorMatrixEffect
                {
                    ColorMatrix = coolMask,
                    Source = new CompositionEffectSourceParameter("CoolSrc")
                }
            }
        };

        try
        {
            _singleSourceFallback = false;
            _dispersionEffectFactory = _compositor.CreateEffectFactory(effectGraph);
        }
        catch
        {
            _singleSourceFallback = true;
            var fallbackGraph = new SaturationEffect
            {
                Saturation = 1.20f,
                Source = new CompositionEffectSourceParameter("WarmSrc")
            };
            _dispersionEffectFactory = _compositor.CreateEffectFactory(fallbackGraph);
        }
    }

    /// <summary>
    /// 在 BottomPlayBarBorder 的宿主 CustomUnderlayLayer 上挂载或同步零延迟纯 GPU 穿底折射四边带。
    /// 中心区域通过 CompositionMaskBrush 平滑衰减为 0，100% 透出原生 DWM LiquidGlassCompositionBrush 中心。
    /// </summary>
    public void AttachOrUpdateDockPassUnderRefraction(
        ContainerVisual? customUnderlayLayer,
        UIElement? bodyHostGrid,
        UIElement? rootLayout,
        double dockW,
        double dockH,
        double cornerRadius,
        float dockWinX,
        float dockWinY,
        bool isLight)
    {
        if (_isDisposed || customUnderlayLayer == null || bodyHostGrid == null || rootLayout == null)
        {
            return;
        }

        if (dockW < 60 || dockH < 36 || !EnsureInitialized(bodyHostGrid) ||
            _compositor == null || _bodyVisualSurface == null || _dispersionEffectFactory == null)
        {
            return;
        }

        try
        {
            var bodyPt = bodyHostGrid.TransformToVisual(rootLayout).TransformPoint(new Point(0, 0));
            float baseOffX = (float)bodyPt.X - dockWinX;
            float baseOffY = (float)bodyPt.Y - dockWinY;

            float fw = (float)dockW;
            float fh = (float)dockH;
            float r = (float)Math.Clamp(cornerRadius, 4.0, Math.Min(dockW, dockH) * 0.5);

            bool needAttach = !_rings.TryGetValue(customUnderlayLayer, out var slot);
            if (needAttach)
            {
                slot = new PassUnderRingSlot
                {
                    Root = _compositor.CreateContainerVisual(),
                    ClipGeom = _compositor.CreateRoundedRectangleGeometry()
                };
                slot.Root.Clip = _compositor.CreateGeometricClip(slot.ClipGeom);

                for (int i = 0; i < 4; i++)
                {
                    var warm = _compositor.CreateSurfaceBrush(_bodyVisualSurface);
                    warm.Stretch = CompositionStretch.None;
                    warm.HorizontalAlignmentRatio = 0f;
                    warm.VerticalAlignmentRatio = 0f;

                    var cool = _compositor.CreateSurfaceBrush(_bodyVisualSurface);
                    cool.Stretch = CompositionStretch.None;
                    cool.HorizontalAlignmentRatio = 0f;
                    cool.VerticalAlignmentRatio = 0f;

                    var fx = _dispersionEffectFactory.CreateBrush();
                    fx.SetSourceParameter("WarmSrc", warm);
                    if (!_singleSourceFallback)
                    {
                        fx.SetSourceParameter("CoolSrc", cool);
                    }

                    var grad = _compositor.CreateLinearGradientBrush();
                    grad.MappingMode = CompositionMappingMode.Relative;
                    ConfigureMaskGradient(grad, i, isLight);

                    var mask = _compositor.CreateMaskBrush();
                    mask.Source = fx;
                    mask.Mask = grad;

                    var sp = _compositor.CreateSpriteVisual();
                    sp.Brush = mask;

                    slot.WarmBrushes[i] = warm;
                    slot.CoolBrushes[i] = cool;
                    slot.EffectBrushes[i] = fx;
                    slot.MaskGradBrushes[i] = grad;
                    slot.MaskBrushes[i] = mask;
                    slot.EdgeSprites[i] = sp;
                    slot.Root.Children.InsertAtTop(sp);
                }

                slot.LastW = -1;
                _rings[customUnderlayLayer] = slot;
            }

            if (!customUnderlayLayer.Children.Contains(slot!.Root))
            {
                customUnderlayLayer.Children.RemoveAll();
                customUnderlayLayer.Children.InsertAtTop(slot.Root);
            }

            bool geomChanged = Math.Abs(slot.LastW - dockW) >= 0.4 ||
                               Math.Abs(slot.LastH - dockH) >= 0.4 ||
                               Math.Abs(slot.LastR - r) >= 0.3;
            bool themeChanged = slot.LastLight != isLight;

            bool isTallOverlay = dockH > 140.0;
            float topBand = isTallOverlay
                ? 16f
                : (float)Math.Clamp(Math.Round(dockH * 0.34), 16.0, 26.0);
            float sideBand = isTallOverlay
                ? 16f
                : (float)Math.Clamp(Math.Round(dockH * 0.38), 18.0, 30.0);

            if (geomChanged || needAttach)
            {
                slot.Root.Size = new Vector2(fw, fh);
                slot.ClipGeom.Size = new Vector2(fw, fh);
                slot.ClipGeom.CornerRadius = new Vector2(r, r);

                // 0: Top
                slot.EdgeSprites[0].Offset = Vector3.Zero;
                slot.EdgeSprites[0].Size = new Vector2(fw, topBand);

                // 1: Bottom
                slot.EdgeSprites[1].Offset = new Vector3(0f, fh - topBand, 0f);
                slot.EdgeSprites[1].Size = new Vector2(fw, topBand);

                // 2: Left
                float midH = MathF.Max(4f, fh - 2f * topBand);
                slot.EdgeSprites[2].Offset = new Vector3(0f, topBand, 0f);
                slot.EdgeSprites[2].Size = new Vector2(sideBand, midH);

                // 3: Right
                slot.EdgeSprites[3].Offset = new Vector3(fw - sideBand, topBand, 0f);
                slot.EdgeSprites[3].Size = new Vector2(sideBand, midH);

                slot.LastW = dockW;
                slot.LastH = dockH;
                slot.LastR = r;
            }

            if (themeChanged || needAttach)
            {
                for (int i = 0; i < 4; i++)
                {
                    ConfigureMaskGradient(slot.MaskGradBrushes[i], i, isLight);
                }
                slot.Root.Opacity = isTallOverlay
                    ? (isLight ? 0.56f : 0.68f)
                    : (isLight ? 0.78f : 0.86f);
                slot.LastLight = isLight;
            }

            // 沿内法线拉拽采样，高尺寸抽屉收敛拉拽幅度避免内侧双影；同时减去边带自身局部偏移 (sx, sy)
            float pullY = isTallOverlay ? 8.5f : 17f;
            float pullX = isTallOverlay ? 9.5f : 20f;

            // 0: Top (sx=0, sy=0)
            UpdateStripOffsets(slot, 0, baseOffX, baseOffY, 0f, 0f, 0f, +pullY);
            // 1: Bottom (sx=0, sy=fh - topBand)
            UpdateStripOffsets(slot, 1, baseOffX, baseOffY, 0f, fh - topBand, 0f, -pullY);
            // 2: Left (sx=0, sy=topBand)
            UpdateStripOffsets(slot, 2, baseOffX, baseOffY, 0f, topBand, +pullX, 0f);
            // 3: Right (sx=fw - sideBand, sy=topBand)
            UpdateStripOffsets(slot, 3, baseOffX, baseOffY, fw - sideBand, topBand, -pullX, 0f);
        }
        catch
        {
        }
    }

    private static void UpdateStripOffsets(
        PassUnderRingSlot slot,
        int idx,
        float baseOffX,
        float baseOffY,
        float sx,
        float sy,
        float pullX,
        float pullY)
    {
        var warm = slot.WarmBrushes[idx];
        var cool = slot.CoolBrushes[idx];
        if (warm == null || cool == null)
        {
            return;
        }

        Vector2 wOff = new(baseOffX - sx - pullX * 0.80f, baseOffY - sy - pullY * 0.80f);
        Vector2 cOff = new(baseOffX - sx - pullX * 1.24f, baseOffY - sy - pullY * 1.24f);

        if (warm.Offset != wOff)
        {
            warm.Offset = wOff;
        }
        if (cool.Offset != cOff)
        {
            cool.Offset = cOff;
        }
    }

    private void ConfigureMaskGradient(CompositionLinearGradientBrush grad, int edgeIndex, bool isLight)
    {
        grad.ColorStops.Clear();
        byte peakA = isLight ? (byte)215 : (byte)230;
        byte midA = isLight ? (byte)110 : (byte)128;

        switch (edgeIndex)
        {
            case 0: // Top: 外顶 0 -> 内 1 消隐
                grad.StartPoint = new Vector2(0.5f, 0f);
                grad.EndPoint = new Vector2(0.5f, 1f);
                break;
            case 1: // Bottom: 外底 1 -> 内 0 消隐
                grad.StartPoint = new Vector2(0.5f, 1f);
                grad.EndPoint = new Vector2(0.5f, 0f);
                break;
            case 2: // Left: 外左 0 -> 内 1 消隐
                grad.StartPoint = new Vector2(0f, 0.5f);
                grad.EndPoint = new Vector2(1f, 0.5f);
                break;
            default: // Right: 外右 1 -> 内 0 消隐
                grad.StartPoint = new Vector2(1f, 0.5f);
                grad.EndPoint = new Vector2(0f, 0.5f);
                break;
        }

        grad.ColorStops.Add(_compositor!.CreateColorGradientStop(0.00f, Color.FromArgb(peakA, 255, 255, 255)));
        grad.ColorStops.Add(_compositor.CreateColorGradientStop(0.42f, Color.FromArgb(midA, 255, 255, 255)));
        grad.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));
    }

    /// <summary>
    /// 在处于 OverlayLayer 的浮层内部空宿主 Border（GlassLayer）上挂载双共享层硬件玻璃：
    /// 从 ContentSurface（叠加 BackgroundSurface）实时采样并经 GaussianBlur(36) + Saturation + 深青高遮蔽着色(#D00B2B36) 合成，
    /// 浮层与 ContentRoot 严格分离，杜绝任何自采样空白、低糊色块与底层文字穿透。
    /// </summary>
    public bool AttachOrUpdateOverlaySurfaceGlass(
        Border? hostBorder,
        GlassSourceKind source,
        UIElement? backgroundLayer,
        UIElement? contentRoot,
        UIElement? rootLayout,
        double width,
        double height,
        double cornerRadius,
        float blurAmount,
        float saturation,
        Color tintWithAlpha)
    {
        if (_isDisposed || hostBorder == null || rootLayout == null || width < 16 || height < 16)
        {
            return false;
        }

        try
        {
            EnsureBackgroundInitialized(backgroundLayer);
            EnsureInitialized(contentRoot);

            if (_compositor == null)
            {
                hostBorder.Background = new SolidColorBrush(tintWithAlpha);
                return false;
            }

            CompositionVisualSurface? primarySurface = source == GlassSourceKind.Content
                ? (_bodyVisualSurface ?? _backgroundVisualSurface)
                : (_backgroundVisualSurface ?? _bodyVisualSurface);

            if (primarySurface == null)
            {
                hostBorder.Background = new SolidColorBrush(tintWithAlpha);
                return false;
            }

            bool canDual = !_overlayDualRejected &&
                           _backgroundVisualSurface != null &&
                           _bodyVisualSurface != null &&
                           source == GlassSourceKind.Content;

            EnsureOverlayGlassFactories(blurAmount, saturation, tintWithAlpha, canDual);

            bool useDual = canDual && _overlayDualGlassFactory != null;
            var targetFactory = useDual ? _overlayDualGlassFactory : _overlaySingleGlassFactory;
            if (targetFactory == null)
            {
                ElementCompositionPreview.SetElementChildVisual(hostBorder, null);
                hostBorder.Background = new SolidColorBrush(tintWithAlpha);
                return false;
            }

            if (!_overlaySlots.TryGetValue(hostBorder, out var slot) || slot.UsesDualSource != useDual)
            {
                if (slot != null)
                {
                    DisposeOverlaySlot(hostBorder, slot);
                    _overlaySlots.Remove(hostBorder);
                }

                var fxBrush = targetFactory.CreateBrush();
                CompositionSurfaceBrush? bgBrush = null;
                CompositionSurfaceBrush? contentBrush = null;

                if (useDual)
                {
                    bgBrush = _compositor.CreateSurfaceBrush(_backgroundVisualSurface!);
                    bgBrush.Stretch = CompositionStretch.None;
                    bgBrush.HorizontalAlignmentRatio = 0f;
                    bgBrush.VerticalAlignmentRatio = 0f;

                    contentBrush = _compositor.CreateSurfaceBrush(_bodyVisualSurface!);
                    contentBrush.Stretch = CompositionStretch.None;
                    contentBrush.HorizontalAlignmentRatio = 0f;
                    contentBrush.VerticalAlignmentRatio = 0f;

                    fxBrush.SetSourceParameter("BackgroundSrc", bgBrush);
                    fxBrush.SetSourceParameter("ContentSrc", contentBrush);
                }
                else
                {
                    var singleBrush = _compositor.CreateSurfaceBrush(primarySurface);
                    singleBrush.Stretch = CompositionStretch.None;
                    singleBrush.HorizontalAlignmentRatio = 0f;
                    singleBrush.VerticalAlignmentRatio = 0f;
                    if (ReferenceEquals(primarySurface, _bodyVisualSurface))
                    {
                        contentBrush = singleBrush;
                    }
                    else
                    {
                        bgBrush = singleBrush;
                    }

                    fxBrush.SetSourceParameter("Src", singleBrush);
                }

                var clipGeom = _compositor.CreateRoundedRectangleGeometry();
                var sprite = _compositor.CreateSpriteVisual();
                sprite.Brush = fxBrush;
                sprite.Clip = _compositor.CreateGeometricClip(clipGeom);

                slot = new OverlaySurfaceSlot
                {
                    Sprite = sprite,
                    ClipGeom = clipGeom,
                    BgBrush = bgBrush,
                    ContentBrush = contentBrush,
                    EffectBrush = fxBrush,
                    UsesDualSource = useDual,
                    LastW = -1
                };
                _overlaySlots[hostBorder] = slot;
                hostBorder.Background = null;
                ElementCompositionPreview.SetElementChildVisual(hostBorder, sprite);
            }

            float fw = (float)width;
            float fh = (float)height;
            float r = (float)Math.Clamp(cornerRadius, 0.0, Math.Min(width, height) * 0.5);

            if (Math.Abs(slot.LastW - width) >= 0.4 ||
                Math.Abs(slot.LastH - height) >= 0.4 ||
                Math.Abs(slot.LastR - r) >= 0.3)
            {
                slot.Sprite.Size = new Vector2(fw, fh);
                slot.ClipGeom.Size = new Vector2(fw, fh);
                slot.ClipGeom.CornerRadius = new Vector2(r, r);
                slot.LastW = width;
                slot.LastH = height;
                slot.LastR = r;
            }

            slot.EffectBrush.Properties.InsertScalar("OverlayBlur.BlurAmount", Math.Clamp(blurAmount, 0f, 48f));
            slot.EffectBrush.Properties.InsertScalar("OverlaySat.Saturation", Math.Clamp(saturation, 0.5f, 2.0f));
            slot.EffectBrush.Properties.InsertColor("OverlayTint.Color", tintWithAlpha);

            var hostPt = hostBorder.TransformToVisual(rootLayout).TransformPoint(new Point(0, 0));
            float hostWinX = (float)hostPt.X;
            float hostWinY = (float)hostPt.Y;

            if (slot.BgBrush != null && backgroundLayer != null)
            {
                var bgPt = backgroundLayer.TransformToVisual(rootLayout).TransformPoint(new Point(0, 0));
                var bgOff = new Vector2((float)bgPt.X - hostWinX, (float)bgPt.Y - hostWinY);
                if (slot.BgBrush.Offset != bgOff)
                {
                    slot.BgBrush.Offset = bgOff;
                }
            }

            if (slot.ContentBrush != null && contentRoot != null)
            {
                var contentPt = contentRoot.TransformToVisual(rootLayout).TransformPoint(new Point(0, 0));
                var contentOff = new Vector2((float)contentPt.X - hostWinX, (float)contentPt.Y - hostWinY);
                if (slot.ContentBrush.Offset != contentOff)
                {
                    slot.ContentBrush.Offset = contentOff;
                }
            }

            return true;
        }
        catch
        {
            ElementCompositionPreview.SetElementChildVisual(hostBorder, null);
            hostBorder.Background = new SolidColorBrush(tintWithAlpha);
            return false;
        }
    }

    private void EnsureOverlayGlassFactories(float blurAmount, float saturation, Color tintWithAlpha, bool tryDual)
    {
        if (_compositor == null)
        {
            return;
        }

        string[] animProps = ["OverlayBlur.BlurAmount", "OverlaySat.Saturation", "OverlayTint.Color"];

        if (tryDual && !_overlayDualRejected && _overlayDualGlassFactory == null)
        {
            try
            {
                var dualGraph = new CompositeEffect
                {
                    Mode = CanvasComposite.SourceOver,
                    Sources =
                    {
                        new SaturationEffect
                        {
                            Name = "OverlaySat",
                            Saturation = saturation,
                            Source = new GaussianBlurEffect
                            {
                                Name = "OverlayBlur",
                                BlurAmount = blurAmount,
                                BorderMode = EffectBorderMode.Hard,
                                Source = new CompositeEffect
                                {
                                    Mode = CanvasComposite.SourceOver,
                                    Sources =
                                    {
                                        new CompositionEffectSourceParameter("BackgroundSrc"),
                                        new CompositionEffectSourceParameter("ContentSrc")
                                    }
                                }
                            }
                        },
                        new ColorSourceEffect
                        {
                            Name = "OverlayTint",
                            Color = tintWithAlpha
                        }
                    }
                };
                _overlayDualGlassFactory = _compositor.CreateEffectFactory(dualGraph, animProps);
            }
            catch
            {
                _overlayDualRejected = true;
                _overlayDualGlassFactory?.Dispose();
                _overlayDualGlassFactory = null;
            }
        }

        if (_overlaySingleGlassFactory == null)
        {
            try
            {
                var singleGraph = new CompositeEffect
                {
                    Mode = CanvasComposite.SourceOver,
                    Sources =
                    {
                        new SaturationEffect
                        {
                            Name = "OverlaySat",
                            Saturation = saturation,
                            Source = new GaussianBlurEffect
                            {
                                Name = "OverlayBlur",
                                BlurAmount = blurAmount,
                                BorderMode = EffectBorderMode.Hard,
                                Source = new CompositionEffectSourceParameter("Src")
                            }
                        },
                        new ColorSourceEffect
                        {
                            Name = "OverlayTint",
                            Color = tintWithAlpha
                        }
                    }
                };
                _overlaySingleGlassFactory = _compositor.CreateEffectFactory(singleGraph, animProps);
            }
            catch
            {
                _overlaySingleGlassFactory?.Dispose();
                _overlaySingleGlassFactory = null;
            }
        }
    }

    public void DetachOverlaySurfaceGlass(Border? hostBorder = null)
    {
        if (hostBorder != null)
        {
            if (_overlaySlots.Remove(hostBorder, out var singleSlot))
            {
                DisposeOverlaySlot(hostBorder, singleSlot);
            }
            else
            {
                try
                {
                    ElementCompositionPreview.SetElementChildVisual(hostBorder, null);
                    hostBorder.Background = null;
                }
                catch
                {
                }
            }
            return;
        }

        DisposeOverlaySurfaceSlotsOnly();
    }

    private void DisposeOverlaySurfaceSlotsOnly()
    {
        foreach (var (host, slot) in _overlaySlots)
        {
            DisposeOverlaySlot(host, slot);
        }
        _overlaySlots.Clear();
    }

    private static void DisposeOverlaySlot(Border host, OverlaySurfaceSlot slot)
    {
        try
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
            host.Background = null;
        }
        catch
        {
        }
        slot.Sprite.Dispose();
        slot.ClipGeom.Dispose();
        slot.EffectBrush.Dispose();
        slot.BgBrush?.Dispose();
        slot.ContentBrush?.Dispose();
    }

    public void DetachDockPassUnder(ContainerVisual? customUnderlayLayer = null)
    {
        if (customUnderlayLayer != null)
        {
            customUnderlayLayer.Children.RemoveAll();
            if (_rings.Remove(customUnderlayLayer, out var singleSlot))
            {
                DisposeSlot(singleSlot);
            }
            return;
        }

        DisposeDockRingVisualsOnly();
    }

    private void DisposeDockRingVisualsOnly()
    {
        foreach (var (underlay, slot) in _rings)
        {
            underlay.Children.RemoveAll();
            DisposeSlot(slot);
        }
        _rings.Clear();
    }

    private static void DisposeSlot(PassUnderRingSlot slot)
    {
        if (slot.Root.Parent is ContainerVisual parent)
        {
            parent.Children.Remove(slot.Root);
        }
        slot.Root.Children.RemoveAll();
        slot.Root.Dispose();
        slot.ClipGeom.Dispose();

        for (int i = 0; i < 4; i++)
        {
            slot.EdgeSprites[i].Dispose();
            slot.MaskBrushes[i].Dispose();
            slot.MaskGradBrushes[i].Dispose();
            slot.EffectBrushes[i].Dispose();
            slot.WarmBrushes[i].Dispose();
            slot.CoolBrushes[i].Dispose();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }
        _isDisposed = true;
        DisposeDockRingVisualsOnly();
        DisposeOverlaySurfaceSlotsOnly();
        _backgroundVisualSurface?.Dispose();
        _bodyVisualSurface?.Dispose();
        _dispersionEffectFactory?.Dispose();
        _overlayDualGlassFactory?.Dispose();
        _overlaySingleGlassFactory?.Dispose();
        _backgroundVisualSurface = null;
        _bodyVisualSurface = null;
        _dispersionEffectFactory = null;
        _overlayDualGlassFactory = null;
        _overlaySingleGlassFactory = null;
        _backgroundElement = null;
        _sourceElement = null;
        _compositor = null;
    }
}
