using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Windows.UI;

namespace LiquidGlass.WinUI.Rendering;

/// <summary>
///按 Compositor 实例缓存可复用的 Win2D/Composition EffectFactory 与诊断状态，避免每次创建控件都重新编译着色器图。
/// </summary>
public sealed class CompositionResourceCache
{
    private static readonly ConditionalWeakTable<Compositor, CompositionResourceCache> CacheByCompositor = new();

    private readonly Compositor _compositor;
    private readonly object _gate = new();

    private CompositionEffectFactory? _blurSatTintFactory;
    private CompositionEffectFactory? _blurSatFactory;
    private CompositionEffectFactory? _blurOnlyFactory;
    private CompositionEffectFactory? _refractionLensFactory;
    private CompositionEffectFactory? _prismCyanFactory;
    private CompositionEffectFactory? _prismRoseFactory;

    private bool _probedBlur;
    private bool _probedRefraction;

    private CompositionResourceCache(Compositor compositor)
    {
        _compositor = compositor;
    }

    public static CompositionResourceCache GetForCompositor(Compositor compositor)
    {
        return CacheByCompositor.GetValue(compositor, c => new CompositionResourceCache(c));
    }

    /// <summary>
    /// 硬件级 Backdrop 高斯模糊管线是否可用（用于日志验证及回退判定）。
    /// </summary>
    public bool IsHardwareBlurSupported { get; private set; }

    /// <summary>
    /// 硬件级内置 Tint 合成管线是否可用。
    /// </summary>
    public bool IsHardwareTintCompositeSupported { get; private set; }

    /// <summary>
    /// 硬件级边缘折射 Transform2DEffect 管线是否可用。
    /// </summary>
    public bool IsHardwareRefractionSupported { get; private set; }

    /// <summary>
    /// 最近一次渲染管线诊断信息。
    /// </summary>
    public string LastDiagnostic { get; private set; } = "未初始化";

    /// <summary>
    /// 创建带着色与饱和度的单链表/纯树结构玻璃背景模糊画刷（适用于直接挂载在单一 Border.Background 上）。
    /// 注意：严禁在 GaussianBlurEffect 上设置 Optimization 属性或共享 Backdrop 分叉 DAG，否则 WinUI 3 会抛 ArgumentException。
    /// </summary>
    public CompositionBrush CreateBackdropBrush(
        double blurAmount,
        double saturation,
        Color tintColor,
        double tintOpacity,
        bool includeTint,
        Color fallbackColor)
    {
        EnsureBlurFactories();

        try
        {
            if (includeTint && _blurSatTintFactory != null)
            {
                var brush = _blurSatTintFactory.CreateBrush();
                var backdrop = _compositor.CreateBackdropBrush();
                brush.SetSourceParameter("Backdrop", backdrop);
                UpdateBackdropBrushProperties(brush, blurAmount, saturation, tintColor, tintOpacity, includeTint: true);
                return brush;
            }

            if (_blurSatFactory != null)
            {
                var brush = _blurSatFactory.CreateBrush();
                var backdrop = _compositor.CreateBackdropBrush();
                brush.SetSourceParameter("Backdrop", backdrop);
                UpdateBackdropBrushProperties(brush, blurAmount, saturation, tintColor, tintOpacity, includeTint: false);
                return brush;
            }

            if (_blurOnlyFactory != null)
            {
                var brush = _blurOnlyFactory.CreateBrush();
                var backdrop = _compositor.CreateBackdropBrush();
                brush.SetSourceParameter("Backdrop", backdrop);
                brush.Properties.InsertScalar("GlassBlur.BlurAmount", (float)Math.Clamp(blurAmount, 0.0, 60.0));
                return brush;
            }
        }
        catch (Exception ex)
        {
            LastDiagnostic = $"CreateBackdropBrush 失败: {ex.GetType().Name}: {ex.Message}";
            Debug.WriteLine($"[LiquidGlass.WinUI] {LastDiagnostic}");
        }

        return _compositor.CreateColorBrush(fallbackColor);
    }

    /// <summary>
    /// 动态更新已连接的玻璃模糊画刷参数，避免重建 Visual 树引起闪烁。
    /// </summary>
    public static void UpdateBackdropBrushProperties(
        CompositionEffectBrush brush,
        double blurAmount,
        double saturation,
        Color tintColor,
        double tintOpacity,
        bool includeTint)
    {
        float clampedBlur = (float)Math.Clamp(blurAmount, 0.0, 60.0);
        float clampedSat = (float)Math.Clamp(saturation, 0.5, 2.0);

        try
        {
            brush.Properties.InsertScalar("GlassBlur.BlurAmount", clampedBlur);
        }
        catch
        {
        }

        try
        {
            brush.Properties.InsertScalar("GlassSat.Saturation", clampedSat);
        }
        catch
        {
        }

        if (includeTint)
        {
            byte tintAlpha = (byte)Math.Clamp((int)Math.Round(Math.Clamp(tintOpacity, 0.0, 1.0) * 255.0), 0, 255);
            var effectiveTint = Color.FromArgb(tintAlpha, tintColor.R, tintColor.G, tintColor.B);
            try
            {
                brush.Properties.InsertColor("GlassTint.Color", effectiveTint);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// 创建边缘光学折射画刷（Backdrop -> Saturation -> Transform2DEffect("LensTransform")）。
    /// </summary>
    public CompositionEffectBrush? CreateRefractionBrush(CompositionBackdropBrush sharedBackdrop, Matrix3x2 initialTransform)
    {
        EnsureRefractionFactory();
        if (_refractionLensFactory == null)
        {
            return null;
        }

        try
        {
            var brush = _refractionLensFactory.CreateBrush();
            brush.SetSourceParameter("Backdrop", sharedBackdrop);
            brush.Properties.InsertMatrix3x2("LensTransform.TransformMatrix", initialTransform);
            return brush;
        }
        catch (Exception ex)
        {
            LastDiagnostic = $"CreateRefractionBrush 失败: {ex.GetType().Name}: {ex.Message}";
            Debug.WriteLine($"[LiquidGlass.WinUI] {LastDiagnostic}");
            return null;
        }
    }

    /// <summary>
    /// 创建边缘微妙 RGB 棱镜色散画刷（冷青或暖珊红分量分离 + Transform2DEffect 偏移折射）。
    /// </summary>
    public CompositionEffectBrush? CreatePrismDispersionBrush(CompositionBackdropBrush sharedBackdrop, Matrix3x2 initialTransform, bool isCoolCyan)
    {
        EnsureRefractionFactory();
        var factory = isCoolCyan ? _prismCyanFactory : _prismRoseFactory;
        if (factory == null)
        {
            return null;
        }

        try
        {
            var brush = factory.CreateBrush();
            brush.SetSourceParameter("Backdrop", sharedBackdrop);
            brush.Properties.InsertMatrix3x2("PrismTransform.TransformMatrix", initialTransform);
            return brush;
        }
        catch
        {
            return null;
        }
    }

    private void EnsureBlurFactories()
    {
        if (_probedBlur)
        {
            return;
        }

        lock (_gate)
        {
            if (_probedBlur)
            {
                return;
            }

            // 1. 尝试完整管线：(Backdrop -> Saturation -> GaussianBlur) + ColorSourceEffect via CompositeEffect
            try
            {
                var sat = new SaturationEffect
                {
                    Name = "GlassSat",
                    Saturation = 1.05f,
                    Source = new CompositionEffectSourceParameter("Backdrop")
                };
                var blur = new GaussianBlurEffect
                {
                    Name = "GlassBlur",
                    BlurAmount = 22.0f,
                    BorderMode = EffectBorderMode.Hard,
                    Source = sat
                };
                var tint = new ColorSourceEffect
                {
                    Name = "GlassTint",
                    Color = Color.FromArgb(72, 22, 26, 32)
                };
                var composite = new CompositeEffect
                {
                    Mode = CanvasComposite.SourceOver
                };
                composite.Sources.Add(blur);
                composite.Sources.Add(tint);

                _blurSatTintFactory = _compositor.CreateEffectFactory(
                    composite,
                    new[] { "GlassSat.Saturation", "GlassBlur.BlurAmount", "GlassTint.Color" });
                IsHardwareTintCompositeSupported = true;
                IsHardwareBlurSupported = true;
            }
            catch (Exception ex)
            {
                IsHardwareTintCompositeSupported = false;
                Debug.WriteLine($"[LiquidGlass.WinUI] Composite tint factory 回退: {ex.Message}");
            }

            // 2. 独立构建纯 Backdrop -> Saturation -> GaussianBlur 管线（供 5 层模板第 1 层使用）
            try
            {
                var sat = new SaturationEffect
                {
                    Name = "GlassSat",
                    Saturation = 1.05f,
                    Source = new CompositionEffectSourceParameter("Backdrop")
                };
                var blur = new GaussianBlurEffect
                {
                    Name = "GlassBlur",
                    BlurAmount = 22.0f,
                    BorderMode = EffectBorderMode.Hard,
                    Source = sat
                };
                _blurSatFactory = _compositor.CreateEffectFactory(
                    blur,
                    new[] { "GlassSat.Saturation", "GlassBlur.BlurAmount" });
                IsHardwareBlurSupported = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LiquidGlass.WinUI] BlurSat factory 回退: {ex.Message}");
            }

            // 3. 兜底极简纯 Blur 管线
            if (_blurSatFactory == null)
            {
                try
                {
                    var blurOnly = new GaussianBlurEffect
                    {
                        Name = "GlassBlur",
                        BlurAmount = 20.0f,
                        BorderMode = EffectBorderMode.Hard,
                        Source = new CompositionEffectSourceParameter("Backdrop")
                    };
                    _blurOnlyFactory = _compositor.CreateEffectFactory(
                        blurOnly,
                        new[] { "GlassBlur.BlurAmount" });
                    IsHardwareBlurSupported = true;
                }
                catch (Exception ex)
                {
                    IsHardwareBlurSupported = false;
                    LastDiagnostic = $"硬件 GaussianBlur 不可用: {ex.Message}";
                    Debug.WriteLine($"[LiquidGlass.WinUI] {LastDiagnostic}");
                }
            }

            if (IsHardwareBlurSupported)
            {
                LastDiagnostic = IsHardwareTintCompositeSupported
                    ? "Hardware Blur+Sat+Tint Ready"
                    : "Hardware Blur+Sat Ready";
            }

            _probedBlur = true;
        }
    }

    private void EnsureRefractionFactory()
    {
        if (_probedRefraction)
        {
            return;
        }

        lock (_gate)
        {
            if (_probedRefraction)
            {
                return;
            }

            try
            {
                // 优先尝试高清高饱和边缘透镜：Backdrop -> SaturationEffect(1.32) -> Transform2DEffect("LensTransform")
                var edgeSat = new SaturationEffect
                {
                    Name = "LensSat",
                    Saturation = 1.32f,
                    Source = new CompositionEffectSourceParameter("Backdrop")
                };
                var lens = new Transform2DEffect
                {
                    Name = "LensTransform",
                    TransformMatrix = Matrix3x2.Identity,
                    Source = edgeSat
                };
                _refractionLensFactory = _compositor.CreateEffectFactory(
                    lens,
                    new[] { "LensTransform.TransformMatrix" });
                IsHardwareRefractionSupported = true;
            }
            catch
            {
                try
                {
                    // Transform2DEffect 兜底极简管线：仅设 Source 与 TransformMatrix
                    var lens = new Transform2DEffect
                    {
                        Name = "LensTransform",
                        TransformMatrix = Matrix3x2.Identity,
                        Source = new CompositionEffectSourceParameter("Backdrop")
                    };
                    _refractionLensFactory = _compositor.CreateEffectFactory(
                        lens,
                        new[] { "LensTransform.TransformMatrix" });
                    IsHardwareRefractionSupported = true;
                }
                catch (Exception ex)
                {
                    IsHardwareRefractionSupported = false;
                    Debug.WriteLine($"[LiquidGlass.WinUI] Transform2DEffect 不可用: {ex.Message}");
                }
            }

            // 微妙棱镜色散通道（独立 try/catch，不影响主折射管线）
            try
            {
                var cyanMatrix = new ColorMatrixEffect
                {
                    ColorMatrix = new Matrix5x4
                    {
                        M11 = 0.08f, M12 = 0f, M13 = 0f, M14 = 0f,
                        M21 = 0f, M22 = 0.72f, M23 = 0f, M24 = 0f,
                        M31 = 0f, M32 = 0f, M33 = 1.25f, M34 = 0f,
                        M41 = 0f, M42 = 0f, M43 = 0f, M44 = 1.0f,
                        M51 = 0f, M52 = 0f, M53 = 0f, M54 = 0f
                    },
                    Source = new CompositionEffectSourceParameter("Backdrop")
                };
                var cyanPrism = new Transform2DEffect
                {
                    Name = "PrismTransform",
                    TransformMatrix = Matrix3x2.Identity,
                    Source = cyanMatrix
                };
                _prismCyanFactory = _compositor.CreateEffectFactory(
                    cyanPrism,
                    new[] { "PrismTransform.TransformMatrix" });
            }
            catch
            {
                _prismCyanFactory = null;
            }

            try
            {
                var roseMatrix = new ColorMatrixEffect
                {
                    ColorMatrix = new Matrix5x4
                    {
                        M11 = 1.22f, M12 = 0f, M13 = 0f, M14 = 0f,
                        M21 = 0f, M22 = 0.22f, M23 = 0f, M24 = 0f,
                        M31 = 0f, M32 = 0f, M33 = 0.45f, M34 = 0f,
                        M41 = 0f, M42 = 0f, M43 = 0f, M44 = 1.0f,
                        M51 = 0f, M52 = 0f, M53 = 0f, M54 = 0f
                    },
                    Source = new CompositionEffectSourceParameter("Backdrop")
                };
                var rosePrism = new Transform2DEffect
                {
                    Name = "PrismTransform",
                    TransformMatrix = Matrix3x2.Identity,
                    Source = roseMatrix
                };
                _prismRoseFactory = _compositor.CreateEffectFactory(
                    rosePrism,
                    new[] { "PrismTransform.TransformMatrix" });
            }
            catch
            {
                _prismRoseFactory = null;
            }

            _probedRefraction = true;
        }
    }
}
