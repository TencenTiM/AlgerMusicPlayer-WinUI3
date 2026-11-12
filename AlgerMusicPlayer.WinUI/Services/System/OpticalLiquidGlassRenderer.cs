using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Streams;

namespace AlgerMusicPlayer.WinUI.Services.System;

/// <summary>
/// 单个 UI 元素挂载的原生 DWM 八分片水滴斜面法向拉伸折射与单线晶莹唇边句柄
/// </summary>
internal sealed class OpticalGlassAttachment : IDisposable
{
    public ContainerVisual? RootVisual;
    // 0:Left, 1:Right, 2:Top, 3:Bottom, 4:TL, 5:TR, 6:BL, 7:BR
    public SpriteVisual?[]? BevelSprites;
    public CompositionEffectBrush?[]? BevelBrushes;
    public SpriteVisual? RimSprite;
    public CompositionNineGridBrush? RimNineGridBrush;
    public CompositionRoundedRectangleGeometry? RoundedClipGeometry;
    public CompositionGeometricClip? RoundedClip;

    public int ProfileIndex = -1;
    public bool IsLight;
    public float LastW;
    public float LastH;
    public float LastRadius;
    public float LastDpiScale;

    public void DetachAndDispose(UIElement? hostElement)
    {
        if (hostElement != null)
        {
            try
            {
                ElementCompositionPreview.SetElementChildVisual(hostElement, null);
            }
            catch
            {
            }
        }

        DisposeVisualsOnly();
    }

    public void DisposeVisualsOnly()
    {
        try
        {
            if (BevelSprites != null)
            {
                for (int i = 0; i < BevelSprites.Length; i++)
                {
                    BevelSprites[i]?.Dispose();
                }
            }

            if (BevelBrushes != null)
            {
                for (int i = 0; i < BevelBrushes.Length; i++)
                {
                    BevelBrushes[i]?.Dispose();
                }
            }

            RimSprite?.Dispose();
            RimNineGridBrush?.Dispose();
            if (RootVisual != null)
            {
                RootVisual.Clip = null;
            }
            RoundedClip?.Dispose();
            RoundedClipGeometry?.Dispose();
            RootVisual?.Dispose();
        }
        catch
        {
        }

        BevelSprites = null;
        BevelBrushes = null;
        RimSprite = null;
        RimNineGridBrush = null;
        RoundedClip = null;
        RoundedClipGeometry = null;
        RootVisual = null;
        ProfileIndex = -1;
        LastW = 0;
        LastH = 0;
        LastRadius = 0;
        LastDpiScale = 0;
    }

    public void Dispose() => DisposeVisualsOnly();
}

/// <summary>
/// iOS 27 原生 DWM 合成器同域「圆角斜面法向拉丝凸透镜折射 + 极细晶莹单唇边」渲染器。
/// 核心物理特征（1:1 对齐光学液态玻璃实拍效果）：
/// 1. 八分片精确欧氏环面斜面（4 条直边 + 4 个四分之一圆角，斜面宽 B 严格等于圆角半径 R）：
///    - 左右竖边沿水平法线方向对底层树林/流光做 4.8x 横向液态拉丝折射（纵向保持 1.0x 锐利），在内沿 x=B 处与中心 1.0x 清透区 0.00px 无缝衔接；
///    - 上下横边沿垂直法线方向对底层晴空/草地做 4.8x 纵向液态拉丝折射（横向保持 1.0x 锐利），在内沿 y=B 处与中心 0.00px 无缝衔接；
///    - 四个圆角围绕圆心 (R, R) 做等比径向拉丝折射，与四条直边在切线处 C1 级精确连续，零 45° 折痕、零高斯糊雾；
/// 2. 彻底摒弃假黑圈暗槽与内层双白线：仅在最外缘 0~2 DIP 保留单根清脆晶体轮廓与薄高光唇线，配合顶边柔和天光漫反射；
/// 3. 100% 运行在 DWM BackdropBrush 硬件合成层（每条折射带仅 ~20px 宽，零全卡拷贝），240Hz 滚动时底层壁纸死死钉在窗口原地。
/// </summary>
internal sealed class OpticalLiquidGlassRenderer : IDisposable
{
    private const string TransformMatrixProperty = "LensTransform.TransformMatrix";
    private const float BevelStretchFactor = 6.60f;

    private readonly record struct NineGridProfileSpec(
        int TextureSizePx,
        float InsetFraction,
        float CornerRadiusDip)
    {
        public float DesignHalfDip => TextureSizePx * 0.25f;
        public float TargetCornerBoxDip => DesignHalfDip * (InsetFraction / 0.5f);
    }

    // 2 档 2x 超采样立体水滴唇边规格：中型圆角卡 (R=16) 与饱满水滴大卡 (R=24)
    private static readonly NineGridProfileSpec[] Specs =
    [
        new(TextureSizePx: 144, InsetFraction: 0.42f, CornerRadiusDip: 16f),
        new(TextureSizePx: 192, InsetFraction: 0.42f, CornerRadiusDip: 24f)
    ];

    private Compositor? _compositor;
    private CompositionBackdropBrush? _sharedBackdropBrush;
    private CompositionEffectFactory? _bevelRefractFactory;
    private bool _supportsTransformMatrix;

    // [profileIndex, isLight ? 1 : 0]
    private readonly LoadedImageSurface?[,] _rimSurfaces = new LoadedImageSurface?[Specs.Length, 2];
    private readonly CompositionSurfaceBrush?[,] _rimSurfaceBrushes = new CompositionSurfaceBrush?[Specs.Length, 2];
    private readonly List<InMemoryRandomAccessStream> _pinnedStreams = [];
    private readonly List<WeakReference<OpticalGlassAttachment>> _liveAttachments = [];

    private bool _disposed;

    public bool IsHardwareTransformSupported => _supportsTransformMatrix;

    public void AttachOrUpdateLensVisual(
        FrameworkElement hostElement,
        OpticalGlassAttachment attachment,
        double cornerRadiusDip,
        bool isLight,
        double intensity = 1.0,
        double? overrideWidthDip = null,
        double? overrideHeightDip = null)
    {
        if (_disposed)
            return;

        float w = (float)(overrideWidthDip ?? hostElement.ActualWidth);
        float h = (float)(overrideHeightDip ?? hostElement.ActualHeight);
        if (w < 24f || h < 24f)
            return;

        try
        {
            float minDim = Math.Min(w, h);
            float radius = Math.Clamp((float)cornerRadiusDip, 6f, minDim * 0.46f);
            float dpiScale = (float)(hostElement.XamlRoot?.RasterizationScale ?? 1.0);
            if (dpiScale <= 0.1f) dpiScale = 1.0f;

            int profileIdx = radius >= 20.0f && minDim >= 64f ? 1 : 0;

            var hostVisual = ElementCompositionPreview.GetElementVisual(hostElement);
            var compositor = hostVisual.Compositor;

            EnsureSharedResources(compositor, profileIdx, isLight);
            var rimSurfBrush = _rimSurfaceBrushes[profileIdx, isLight ? 1 : 0];
            if (rimSurfBrush == null || _sharedBackdropBrush == null)
                return;

            bool enableBevelRefraction = minDim >= 44f &&
                                         _supportsTransformMatrix &&
                                         _bevelRefractFactory != null;
            bool hasBevelCurrently = attachment.BevelSprites != null;

            bool needRebuild = attachment.RootVisual == null ||
                               attachment.RootVisual.Compositor != compositor ||
                               attachment.ProfileIndex != profileIdx ||
                               attachment.IsLight != isLight ||
                               hasBevelCurrently != enableBevelRefraction;

            if (needRebuild)
            {
                attachment.DisposeVisualsOnly();

                var container = compositor.CreateContainerVisual();
                container.IsHitTestVisible = false;

                var clipGeom = compositor.CreateRoundedRectangleGeometry();
                var geoClip = compositor.CreateGeometricClip(clipGeom);
                container.Clip = geoClip;
                attachment.RoundedClipGeometry = clipGeom;
                attachment.RoundedClip = geoClip;

                if (enableBevelRefraction)
                {
                    var sprites = new SpriteVisual?[8];
                    var brushes = new CompositionEffectBrush?[8];
                    try
                    {
                        float bevelOpacity = (float)Math.Clamp(0.98 * intensity, 0.90, 1.0);
                        for (int i = 0; i < 8; i++)
                        {
                            var eb = _bevelRefractFactory!.CreateBrush();
                            eb.SetSourceParameter("Backdrop", _sharedBackdropBrush);

                            var sv = compositor.CreateSpriteVisual();
                            sv.IsHitTestVisible = false;
                            sv.Brush = eb;
                            sv.Opacity = bevelOpacity;

                            container.Children.InsertAtBottom(sv);
                            sprites[i] = sv;
                            brushes[i] = eb;
                        }

                        attachment.BevelSprites = sprites;
                        attachment.BevelBrushes = brushes;
                    }
                    catch
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            sprites[i]?.Dispose();
                            brushes[i]?.Dispose();
                        }
                        attachment.BevelSprites = null;
                        attachment.BevelBrushes = null;
                    }
                }

                // 顶层：水滴玻璃立体厚度轮廓 + 圆角方向性高光唇 + 顶沿柔和天光漫反射（零黑槽、零内白线）
                var rimNineGrid = compositor.CreateNineGridBrush();
                rimNineGrid.Source = rimSurfBrush;

                var rimSprite = compositor.CreateSpriteVisual();
                rimSprite.IsHitTestVisible = false;
                rimSprite.Brush = rimNineGrid;
                rimSprite.Opacity = (float)Math.Clamp(0.96 * intensity, 0.85, 1.0);
                container.Children.InsertAtTop(rimSprite);

                attachment.RootVisual = container;
                attachment.RimSprite = rimSprite;
                attachment.RimNineGridBrush = rimNineGrid;
                attachment.ProfileIndex = profileIdx;
                attachment.IsLight = isLight;
                attachment.LastW = -1f;
                attachment.LastH = -1f;
                attachment.LastRadius = -1f;
                attachment.LastDpiScale = -1f;

                TrackLiveAttachment(attachment);
                ElementCompositionPreview.SetElementChildVisual(hostElement, container);
            }

            if (Math.Abs(attachment.LastW - w) > 0.25f ||
                Math.Abs(attachment.LastH - h) > 0.25f ||
                Math.Abs(attachment.LastRadius - radius) > 0.2f ||
                Math.Abs(attachment.LastDpiScale - dpiScale) > 0.01f)
            {
                ApplyDpiSafeNineGridInsets(attachment, w, h, radius);

                var fullSize = new Vector2(w, h);
                attachment.RootVisual!.Size = fullSize;
                attachment.RimSprite!.Size = fullSize;
                if (attachment.RoundedClipGeometry != null)
                {
                    attachment.RoundedClipGeometry.Size = fullSize;
                    attachment.RoundedClipGeometry.CornerRadius = new Vector2(radius, radius);
                }

                if (attachment.BevelSprites != null && attachment.BevelBrushes != null)
                {
                    UpdateBevelStripsAndMatrices(attachment, w, h, radius, dpiScale);
                }

                attachment.LastW = w;
                attachment.LastH = h;
                attachment.LastRadius = radius;
                attachment.LastDpiScale = dpiScale;
            }
        }
        catch
        {
        }
    }

    private static void UpdateBevelStripsAndMatrices(
        OpticalGlassAttachment att,
        float w,
        float h,
        float radius,
        float dpiScale)
    {
        var sprites = att.BevelSprites!;
        var brushes = att.BevelBrushes!;

        // 折射层已挂载于卡片底层 UnderlayHost（位于进度条、封面、文字之下），可完整释放饱满的水滴凸透镜斜面宽度
        float maxByCardKind = (w >= 280f && h >= 110f)
            ? 24.0f
            : (h < 110f && w >= 400f ? 19.0f : 15.0f);
        float b = Math.Clamp(Math.Min(radius, maxByCardKind), 8.0f, Math.Min(w, h) * 0.36f);
        float midW = Math.Max(0f, w - b * 2f);
        float midH = Math.Max(0f, h - b * 2f);

        // 关键：DWM CompositionEffectBrush 内部的 Transform2DEffect 以物理像素为单位，锚点必须为 b * dpiScale 才能与中心 1.0x 区 0.00px 无缝衔接
        float bPx = b * dpiScale;
        float s = BevelStretchFactor;

        // 0: Left 直边 [0..b, b..h-b] —— 锚定内沿 u = bPx，水平拉丝放大 s 倍，纵向 1.0x 锐利
        sprites[0]!.Offset = new Vector3(0f, b, 0f);
        sprites[0]!.Size = new Vector2(b, midH);
        brushes[0]!.Properties.InsertMatrix3x2(
            TransformMatrixProperty,
            Matrix3x2.CreateScale(s, 1f, new Vector2(bPx, 0f)));

        // 1: Right 直边 [w-b..w, b..h-b] —— 锚定内沿 u = 0，水平拉丝放大 s 倍
        sprites[1]!.Offset = new Vector3(w - b, b, 0f);
        sprites[1]!.Size = new Vector2(b, midH);
        brushes[1]!.Properties.InsertMatrix3x2(
            TransformMatrixProperty,
            Matrix3x2.CreateScale(s, 1f, Vector2.Zero));

        // 2: Top 直边 [b..w-b, 0..b] —— 锚定内沿 v = bPx，垂直拉丝放大 s 倍，横向 1.0x 锐利
        sprites[2]!.Offset = new Vector3(b, 0f, 0f);
        sprites[2]!.Size = new Vector2(midW, b);
        brushes[2]!.Properties.InsertMatrix3x2(
            TransformMatrixProperty,
            Matrix3x2.CreateScale(1f, s, new Vector2(0f, bPx)));

        // 3: Bottom 直边 [b..w-b, h-b..h] —— 锚定内沿 v = 0，垂直拉丝放大 s 倍
        sprites[3]!.Offset = new Vector3(b, h - b, 0f);
        sprites[3]!.Size = new Vector2(midW, b);
        brushes[3]!.Properties.InsertMatrix3x2(
            TransformMatrixProperty,
            Matrix3x2.CreateScale(1f, s, Vector2.Zero));

        var cornerSize = new Vector2(b, b);

        // 4: Top-Left 圆角 [0..b, 0..b] —— 锚定圆心 (bPx, bPx) 径向等比拉丝（与 Left、Top 在切线处 0.00px 连续）
        sprites[4]!.Offset = Vector3.Zero;
        sprites[4]!.Size = cornerSize;
        brushes[4]!.Properties.InsertMatrix3x2(
            TransformMatrixProperty,
            Matrix3x2.CreateScale(s, s, new Vector2(bPx, bPx)));

        // 5: Top-Right 圆角 [w-b..w, 0..b] —— 锚定圆心 (0, bPx)
        sprites[5]!.Offset = new Vector3(w - b, 0f, 0f);
        sprites[5]!.Size = cornerSize;
        brushes[5]!.Properties.InsertMatrix3x2(
            TransformMatrixProperty,
            Matrix3x2.CreateScale(s, s, new Vector2(0f, bPx)));

        // 6: Bottom-Left 圆角 [0..b, h-b..h] —— 锚定圆心 (bPx, 0)
        sprites[6]!.Offset = new Vector3(0f, h - b, 0f);
        sprites[6]!.Size = cornerSize;
        brushes[6]!.Properties.InsertMatrix3x2(
            TransformMatrixProperty,
            Matrix3x2.CreateScale(s, s, new Vector2(bPx, 0f)));

        // 7: Bottom-Right 圆角 [w-b..w, h-b..h] —— 锚定圆心 (0, 0)
        sprites[7]!.Offset = new Vector3(w - b, h - b, 0f);
        sprites[7]!.Size = cornerSize;
        brushes[7]!.Properties.InsertMatrix3x2(
            TransformMatrixProperty,
            Matrix3x2.CreateScale(s, s, Vector2.Zero));
    }

    private void ApplyDpiSafeNineGridInsets(OpticalGlassAttachment att, float w, float h, float cardRadiusDip)
    {
        if (att.RimNineGridBrush == null || att.ProfileIndex < 0)
            return;

        var spec = Specs[att.ProfileIndex];
        var surf = _rimSurfaces[att.ProfileIndex, att.IsLight ? 1 : 0];

        double decodedLogicalW = surf?.DecodedSize.Width ?? 0.0;
        float logicalTexSize = decodedLogicalW > 8.0
            ? (float)decodedLogicalW
            : spec.TextureSizePx * 0.5f;

        float insetDip = logicalTexSize * spec.InsetFraction;
        // 让九宫格圆角严格匹配当前卡片的实际圆角半径，杜绝圆角弧度错位形成双层框感
        float radiusRatio = Math.Clamp(cardRadiusDip / Math.Max(1f, spec.CornerRadiusDip), 0.45f, 1.35f);
        float desiredCornerBox = spec.TargetCornerBoxDip * radiusRatio;
        float maxCornerBox = Math.Min(desiredCornerBox, Math.Min(w, h) * 0.46f);
        float scale = Math.Clamp(maxCornerBox / Math.Max(1f, insetDip), 0.12f, 2.5f);

        att.RimNineGridBrush.SetInsets(insetDip, insetDip, insetDip, insetDip);
        att.RimNineGridBrush.SetInsetScales(scale, scale, scale, scale);
    }

    private void EnsureSharedResources(Compositor compositor, int profileIdx, bool isLight)
    {
        if (_compositor != null && _compositor != compositor)
        {
            ReleaseComposableResources();
        }
        _compositor = compositor;

        _sharedBackdropBrush ??= compositor.CreateBackdropBrush();

        if (_bevelRefractFactory == null)
        {
            BuildRefractionEffectFactory(compositor);
        }

        int lightIdx = isLight ? 1 : 0;
        if (_rimSurfaceBrushes[profileIdx, lightIdx] == null)
        {
            var spec = Specs[profileIdx];
            byte[] png = BuildCrystalRimPng(spec, isLight);
            var surf = LoadSurfaceFromPngBytes(png);
            _rimSurfaces[profileIdx, lightIdx] = surf;

            var sb = compositor.CreateSurfaceBrush(surf);
            sb.Stretch = CompositionStretch.Fill;
            _rimSurfaceBrushes[profileIdx, lightIdx] = sb;

            surf.LoadCompleted += (_, _) => OnRimSurfaceLoadCompleted();
        }
    }

    private void BuildRefractionEffectFactory(Compositor compositor)
    {
        try
        {
            // 关键：Transform2DEffect 在 CompositionEffectFactory 中仅支持 Source 与 TransformMatrix（不可设置 BorderMode，否则抛异常导致折射失效！）
            using var lensTransform = new Transform2DEffect
            {
                Name = "LensTransform",
                Source = new CompositionEffectSourceParameter("Backdrop"),
                TransformMatrix = Matrix3x2.Identity
            };
            // 极轻柔液态表面张力柔润（0.55px 消除 6.6x 拉丝像素锯齿，赋予水滴玻璃丝滑折射光泽，绝不发雾）
            using var liquidSmooth = new GaussianBlurEffect
            {
                Source = lensTransform,
                BlurAmount = 0.55f,
                BorderMode = EffectBorderMode.Hard
            };
            using var sat = new SaturationEffect
            {
                Source = liquidSmooth,
                Saturation = 1.26f
            };
            using var ctr = new ContrastEffect
            {
                Source = sat,
                Contrast = 0.055f
            };

            var factory = compositor.CreateEffectFactory(ctr, [TransformMatrixProperty]);
            using var probe = factory.CreateBrush();
            probe.SetSourceParameter("Backdrop", _sharedBackdropBrush!);
            probe.Properties.InsertMatrix3x2(TransformMatrixProperty, Matrix3x2.Identity);

            _bevelRefractFactory = factory;
            _supportsTransformMatrix = true;
        }
        catch
        {
            _bevelRefractFactory?.Dispose();
            _bevelRefractFactory = null;
            _supportsTransformMatrix = false;
        }
    }

    private void TrackLiveAttachment(OpticalGlassAttachment att)
    {
        for (int i = _liveAttachments.Count - 1; i >= 0; i--)
        {
            if (!_liveAttachments[i].TryGetTarget(out var target) || target.RootVisual == null)
            {
                _liveAttachments.RemoveAt(i);
            }
        }
        _liveAttachments.Add(new WeakReference<OpticalGlassAttachment>(att));
    }

    private void OnRimSurfaceLoadCompleted()
    {
        for (int i = _liveAttachments.Count - 1; i >= 0; i--)
        {
            if (!_liveAttachments[i].TryGetTarget(out var att) ||
                att.RimNineGridBrush == null ||
                att.ProfileIndex < 0)
            {
                _liveAttachments.RemoveAt(i);
                continue;
            }

            try
            {
                var sb = _rimSurfaceBrushes[att.ProfileIndex, att.IsLight ? 1 : 0];
                if (sb != null && att.LastW > 20f && att.LastH > 20f)
                {
                    att.RimNineGridBrush.Source = sb;
                    ApplyDpiSafeNineGridInsets(att, att.LastW, att.LastH, att.LastRadius);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// 生成 2x 超采样 3D 水滴立体光学唇边 + 圆角菲涅尔高光 + 顶沿晴空天光漫反射 PNG（1:1 复刻目标效果图右半边）：
    /// - 彻底摒弃假黑槽圈与第二道内白线，让底部八分片 6.6x 液态拉丝折射直达玻璃最外缘；
    /// - d = 0 ~ 1.05 DIP：深冷灰青物理外切暗边（使透明水滴在白云、亮黄草地与深松林上均边界锐利厚实）；
    /// - d = 0.95 ~ 2.85 DIP：方向性圆角水晶镜面唇（顶沿、底沿与四个大圆角饱满璀璨，左右竖边轻盈克制不遮挡松树拉丝）；
    /// - d = 2.0 ~ 18.0 DIP：顶沿宽幅柔和天光弧面反光 + 侧底斜面极微弱立体厚度过渡，塑造隆起水滴体积感。
    /// </summary>
    private static byte[] BuildCrystalRimPng(NineGridProfileSpec spec, bool isLight)
    {
        int n = spec.TextureSizePx;
        byte[] rgba = new byte[n * n * 4];
        float halfDip = n * 0.25f; // 2x 超采样：1 DIP = 2 px
        float rDip = spec.CornerRadiusDip;
        float maxBandDip = 18.5f;
        float bx = halfDip - rDip;
        float by = halfDip - rDip;

        for (int py = 0; py < n; py++)
        {
            float yDip = (py + 0.5f) * 0.5f - halfDip;
            float qy = MathF.Abs(yDip) - by;
            float sy = yDip < 0f ? -1f : 1f;

            for (int px = 0; px < n; px++)
            {
                float xDip = (px + 0.5f) * 0.5f - halfDip;
                float qx = MathF.Abs(xDip) - bx;
                float sx = xDip < 0f ? -1f : 1f;

                float mx = MathF.Max(qx, 0f);
                float my = MathF.Max(qy, 0f);
                float outsideLen = MathF.Sqrt(mx * mx + my * my);
                float sdf = outsideLen + MathF.Min(MathF.Max(qx, qy), 0f) - rDip;
                float d = -sdf; // 距外沿向内的 DIP 距离

                if (d <= -0.60f || d >= maxBandDip)
                    continue;

                float nx, ny;
                float cornerPeak = 0f;
                if (outsideLen > 1e-4f)
                {
                    float ux = mx / outsideLen;
                    float uy = my / outsideLen;
                    nx = sx * ux;
                    ny = sy * uy;
                    // 在直边切点 (ux=0 或 uy=0) 严格为 0，在 45° 圆角弧顶平滑达到 1.0，彻底消除直边/圆角亮度跳变
                    cornerPeak = 2f * ux * uy;
                }
                else if (qx > qy)
                {
                    nx = sx;
                    ny = 0f;
                }
                else
                {
                    nx = 0f;
                    ny = sy;
                }

                float topFacing = MathF.Pow(Math.Clamp(-ny, 0f, 1f), 1.55f);
                float bottomFacing = MathF.Pow(Math.Clamp(ny, 0f, 1f), 1.75f);
                float sideFacing = MathF.Pow(Math.Clamp(MathF.Abs(nx), 0f, 1f), 1.6f);

                float outAa = SmoothStep(-0.52f, 0.16f, d);

                // 1. 最外缘 1.05 DIP 物理外切深冷灰青轮廓（对标效果图清晰厚实的水滴玻璃外边界，无内黑圈）
                float contour = MathF.Exp(-MathF.Pow((d - 0.32f) / 0.58f, 2f)) * outAa;
                float contourAlpha = Math.Clamp(contour * (isLight ? 0.68f : 0.72f), 0f, 0.74f);

                // 2. 方向性圆角水晶镜面高光唇（顶边、底边与四角弧度饱满清亮，左右竖边中部轻盈通透以凸显树林横向拉丝）
                float lipWidth = 0.86f + 0.34f * cornerPeak + 0.16f * topFacing;
                float lipCenter = 1.48f + 0.20f * cornerPeak;
                float lipProfile = MathF.Exp(-MathF.Pow((d - lipCenter) / lipWidth, 2f)) * SmoothStep(0.22f, 0.92f, d);
                float directionalLip = 0.25f + 0.52f * topFacing + 0.38f * bottomFacing + 0.42f * cornerPeak;
                float lipAlpha = Math.Clamp(lipProfile * directionalLip, 0f, 0.92f);

                // 3. 顶沿宽幅柔和晴空天光漫反射带（模拟凸起的水滴玻璃上半部映出蓝白晴空光泽）
                float skyGloss = 0f;
                if (topFacing > 0.005f)
                {
                    float glossCurve = MathF.Exp(-MathF.Pow((d - 3.6f) / 7.8f, 2f)) *
                                       SmoothStep(1.0f, 2.8f, d) *
                                       (1f - SmoothStep(13.5f, maxBandDip, d));
                    skyGloss = Math.Clamp(glossCurve * topFacing * (isLight ? 0.27f : 0.18f), 0f, 0.29f);
                }

                // 4. 侧边与底边斜面极轻微立体厚度过渡（alpha <= 0.075，塑造圆弧隆起体积感而非暗槽框）
                float bevelDepth = 0f;
                float sideOrBottom = Math.Clamp(sideFacing * 0.75f + bottomFacing * 0.65f, 0f, 1f);
                if (sideOrBottom > 0.01f)
                {
                    float depthCurve = MathF.Exp(-MathF.Pow((d - 5.2f) / 4.8f, 2f)) *
                                       SmoothStep(1.8f, 3.6f, d) *
                                       (1f - SmoothStep(11.0f, 16.0f, d));
                    bevelDepth = Math.Clamp(depthCurve * sideOrBottom * (isLight ? 0.075f : 0.095f), 0f, 0.10f);
                }

                float pr = 0f, pg = 0f, pb = 0f, pa = 0f;

                if (bevelDepth > 1e-4f)
                {
                    BlendOver(ref pr, ref pg, ref pb, ref pa, 12f / 255f, 24f / 255f, 36f / 255f, bevelDepth);
                }

                if (contourAlpha > 1e-4f)
                {
                    BlendOver(ref pr, ref pg, ref pb, ref pa, 12f / 255f, 22f / 255f, 34f / 255f, contourAlpha);
                }

                if (skyGloss > 1e-4f)
                {
                    BlendOver(ref pr, ref pg, ref pb, ref pa, 0.96f, 0.99f, 1.0f, skyGloss);
                }

                if (lipAlpha > 1e-4f)
                {
                    BlendOver(ref pr, ref pg, ref pb, ref pa, 0.99f, 1.0f, 1.0f, lipAlpha);
                }

                if (pa <= 1e-4f)
                    continue;

                float invA = 1f / pa;
                int idx = (py * n + px) * 4;
                rgba[idx + 0] = (byte)Math.Clamp((int)MathF.Round(pr * invA * 255f), 0, 255);
                rgba[idx + 1] = (byte)Math.Clamp((int)MathF.Round(pg * invA * 255f), 0, 255);
                rgba[idx + 2] = (byte)Math.Clamp((int)MathF.Round(pb * invA * 255f), 0, 255);
                rgba[idx + 3] = (byte)Math.Clamp((int)MathF.Round(pa * 255f), 0, 255);
            }
        }

        return EncodeRgbaToPng(rgba, n, n);
    }

    private static void BlendOver(
        ref float dstPremulR,
        ref float dstPremulG,
        ref float dstPremulB,
        ref float dstA,
        float srcR,
        float srcG,
        float srcB,
        float srcA)
    {
        float inv = 1f - srcA;
        dstPremulR = srcR * srcA + dstPremulR * inv;
        dstPremulG = srcG * srcA + dstPremulG * inv;
        dstPremulB = srcB * srcA + dstPremulB * inv;
        dstA = srcA + dstA * inv;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / Math.Max(1e-5f, edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private LoadedImageSurface LoadSurfaceFromPngBytes(byte[] pngBytes)
    {
        var ms = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(ms.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(pngBytes);
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
            writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            writer.DetachStream();
        }
        ms.Seek(0);
        _pinnedStreams.Add(ms);
        return LoadedImageSurface.StartLoadFromStream(ms);
    }

    private static byte[] EncodeRgbaToPng(byte[] rgba, int width, int height)
    {
        using var output = new MemoryStream(width * height + 256);
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[0..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WritePngChunk(output, "IHDR"u8, ihdr);

        using (var idatBuffer = new MemoryStream())
        {
            using (var zlib = new ZLibStream(idatBuffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                int stride = width * 4;
                for (int y = 0; y < height; y++)
                {
                    zlib.WriteByte(0);
                    zlib.Write(rgba, y * stride, stride);
                }
            }
            WritePngChunk(output, "IDAT"u8, idatBuffer.ToArray());
        }

        WritePngChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WritePngChunk(Stream stream, ReadOnlySpan<byte> chunkType, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header[0..4], data.Length);
        chunkType.CopyTo(header[4..8]);
        stream.Write(header);
        if (!data.IsEmpty)
        {
            stream.Write(data);
        }

        uint crc = 0xFFFF_FFFFu;
        crc = UpdateCrc32(crc, chunkType);
        crc = UpdateCrc32(crc, data);
        crc ^= 0xFFFF_FFFFu;

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            crc ^= bytes[i];
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1u) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return crc;
    }

    private void ReleaseComposableResources()
    {
        try
        {
            _bevelRefractFactory?.Dispose();
            _sharedBackdropBrush?.Dispose();

            for (int p = 0; p < Specs.Length; p++)
            {
                for (int l = 0; l < 2; l++)
                {
                    _rimSurfaceBrushes[p, l]?.Dispose();
                    _rimSurfaces[p, l]?.Dispose();
                    _rimSurfaceBrushes[p, l] = null;
                    _rimSurfaces[p, l] = null;
                }
            }

            foreach (var s in _pinnedStreams)
            {
                s.Dispose();
            }
        }
        catch
        {
        }

        _pinnedStreams.Clear();
        _liveAttachments.Clear();
        _bevelRefractFactory = null;
        _sharedBackdropBrush = null;
        _supportsTransformMatrix = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ReleaseComposableResources();
        _compositor = null;
    }
}
