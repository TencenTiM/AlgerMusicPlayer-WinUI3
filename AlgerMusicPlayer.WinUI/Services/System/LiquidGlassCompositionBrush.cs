using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AlgerMusicPlayer.WinUI.Services.System;

/// <summary>
/// 原生 Windows DWM 合成器级 iOS 27「液态玻璃 (Liquid Glass)」实时 GPU 光学折射画刷。
/// 100% 采用同域纯 IGraphicsEffect 描述符管线（零外部 D3D/D2D 设备、零跨设备表面，彻底根绝 0x88990012 D2DERR_WRONG_RESOURCE_DOMAIN）：
/// 1. 随 ScrollViewer DirectManipulation 在合成线程 120Hz/144Hz 零延迟硬件同步滚动，杜绝差帧抖动；
/// 2. 100% 遵循父级 ScrollViewer 边界与卡片圆角硬件裁剪，绝不越界残留；
/// 3. 实时采样卡片背后动态壁纸/视频/极光/粒子（鲜明度增强 + 清透水滴透镜散射 + Screen 冰晶水光提亮）；
/// 4. 顶层「对比度雾化层 (Contrast Veil)」可在合成线程平滑动画调节浓度：字色固定不变，由玻璃浓度自适应背后壁纸明暗保障可读性。
/// </summary>
public sealed class LiquidGlassCompositionBrush : XamlCompositionBrushBase
{
    private const string VeilColorProperty = "Veil.Color";

    private readonly Color _accent;
    private readonly bool _isLight;
    private readonly float _intensity;
    private readonly float _blurAmount;
    private readonly float _saturation;

    private CompositionBackdropBrush? _backdropBrush;
    private CompositionEffectBrush? _effectBrush;
    private double _contrastVeil;

    public LiquidGlassCompositionBrush()
        : this(Color.FromArgb(255, 16, 185, 129), isLight: false)
    {
    }

    public LiquidGlassCompositionBrush(
        Color accent,
        bool isLight,
        double intensity = 1.0,
        float blurAmount = 14.0f,
        float saturation = 1.42f)
    {
        _accent = accent;
        _isLight = isLight;
        _intensity = (float)Math.Clamp(intensity, 0.55, 1.55);
        _blurAmount = Math.Clamp(blurAmount, 3.0f, 24.0f);
        _saturation = Math.Clamp(saturation, 1.08f, 1.80f);

        FallbackColor = isLight
            ? Color.FromArgb(68, 255, 255, 255)
            : Color.FromArgb(48, 226, 238, 255);
    }

    public Color Accent => _accent;
    public bool IsLight => _isLight;
    public double Intensity => _intensity;
    public float BlurAmount => _blurAmount;
    public double ContrastVeil => _contrastVeil;

    /// <summary>
    /// 雾化层颜色的 sRGB 灰度（浅色基调为冰白、深色基调为墨夜），供自适应引擎反推所需浓度
    /// </summary>
    public static double GetVeilGray(bool isLight) => isLight ? 0.975 : 0.045;

    /// <summary>
    /// 复制一份参数完全相同的独立画刷实例（用于共享 ThemeResource 的卡片按元素独立调节雾化浓度）
    /// </summary>
    public LiquidGlassCompositionBrush CloneIndependent() =>
        new(_accent, _isLight, _intensity, _blurAmount, _saturation);

    /// <summary>
    /// 估算背后壁纸经过本玻璃（Screen 水光 + 凝脂釉层，未含雾化层）后的 sRGB 灰度
    /// </summary>
    public double EstimateSurfaceGray(double wallpaperGray)
    {
        var (sheenTint, screenAlpha, glazeTint, glazeAlpha) = ResolveLayerParameters();
        double c = Math.Clamp(wallpaperGray, 0.0, 1.0);

        double s = screenAlpha / 255.0 * ColorToGray(sheenTint);
        c = 1.0 - (1.0 - c) * (1.0 - s);

        double g = glazeAlpha / 255.0;
        c = c * (1.0 - g) + ColorToGray(glazeTint) * g;
        return Math.Clamp(c, 0.0, 1.0);
    }

    /// <summary>
    /// 设置顶层对比度雾化层浓度（0 ~ 0.85），在合成线程上平滑过渡，不触发 XAML 重新布局
    /// </summary>
    public void SetContrastVeil(double veil, bool animate = true)
    {
        veil = Math.Clamp(veil, 0.0, 0.85);
        if (Math.Abs(veil - _contrastVeil) < 0.004)
            return;

        _contrastVeil = veil;
        var brush = _effectBrush;
        if (brush == null)
            return;

        try
        {
            var anim = brush.Compositor.CreateColorKeyFrameAnimation();
            anim.InsertKeyFrame(1.0f, GetVeilColor());
            anim.Duration = TimeSpan.FromMilliseconds(animate ? 320 : 1);
            brush.StartAnimation(VeilColorProperty, anim);
        }
        catch
        {
        }
    }

    protected override void OnConnected()
    {
        if (CompositionBrush != null)
            return;

        try
        {
            Compositor compositor = CompositionTarget.GetCompositorForCurrentThread();
            var (sheenTint, screenAlpha, glazeTint, glazeAlpha) = ResolveLayerParameters();

            // 1. 鲜明度光学折射增强（模仿高折射率水滴晶体对背光色彩的通透提艳）
            using var vibrantBackdrop = new SaturationEffect
            {
                Source = new CompositionEffectSourceParameter("Backdrop"),
                Saturation = _saturation
            };

            // 2. 清透水滴凝胶透镜散射（柔化壁纸高频噪点以清晰衬托前景文字，同时完整保留背后色彩流动轮廓）
            using var opticalLensBlur = new GaussianBlurEffect
            {
                Source = vibrantBackdrop,
                BlurAmount = _blurAmount,
                BorderMode = EffectBorderMode.Hard
            };

            // 3. Screen 滤色冰晶镜面水光（提亮暗部通透水光感，同时在亮白天空/浅色区绝不泛灰发闷）
            using var screenSheenSource = new ColorSourceEffect
            {
                Color = Color.FromArgb(screenAlpha, sheenTint.R, sheenTint.G, sheenTint.B)
            };

            using var screenLifted = new BlendEffect
            {
                Mode = BlendEffectMode.Screen,
                Background = opticalLensBlur,
                Foreground = screenSheenSource
            };

            // 4. 柔和磨砂水滴凝脂釉层（赋予卡片实体玻璃厚度感）
            using var glazeSource = new ColorSourceEffect
            {
                Color = Color.FromArgb(glazeAlpha, glazeTint.R, glazeTint.G, glazeTint.B)
            };

            // 5. 对比度雾化层（浓度由自适应引擎按背后壁纸明暗实时动画调节，默认 0 完全通透）
            using var veilSource = new ColorSourceEffect
            {
                Name = "Veil",
                Color = GetVeilColor()
            };

            using var liquidComposite = new CompositeEffect
            {
                Mode = CanvasComposite.SourceOver
            };
            liquidComposite.Sources.Add(screenLifted);
            liquidComposite.Sources.Add(glazeSource);
            liquidComposite.Sources.Add(veilSource);

            using var effectFactory = compositor.CreateEffectFactory(liquidComposite, new[] { VeilColorProperty });
            CompositionEffectBrush effectBrush = effectFactory.CreateBrush();

            _backdropBrush = compositor.CreateBackdropBrush();
            effectBrush.SetSourceParameter("Backdrop", _backdropBrush);

            _effectBrush = effectBrush;
            CompositionBrush = effectBrush;
        }
        catch
        {
            try
            {
                Compositor compositor = CompositionTarget.GetCompositorForCurrentThread();
                CompositionBrush = compositor.CreateColorBrush(FallbackColor);
            }
            catch
            {
            }
        }
    }

    protected override void OnDisconnected()
    {
        // 先解除 XAML 包装器对底层 CompositionBrush 的引用，再安全释放，杜绝同帧切换主题时的悬空句柄
        _effectBrush = null;
        var oldBrush = CompositionBrush;
        CompositionBrush = null;
        oldBrush?.Dispose();

        var oldBackdrop = _backdropBrush;
        _backdropBrush = null;
        oldBackdrop?.Dispose();
    }

    private (Color SheenTint, byte ScreenAlpha, Color GlazeTint, byte GlazeAlpha) ResolveLayerParameters()
    {
        Color sheenTint = ThemeService.LerpColor(Color.FromArgb(255, 248, 253, 255), _accent, _isLight ? 0.14 : 0.22);
        byte screenAlpha = (byte)Math.Clamp(Math.Round((_isLight ? 44.0 : 28.0) * _intensity), 12, 92);
        Color glazeTint = _isLight
            ? Color.FromArgb(255, 255, 255, 255)
            : ThemeService.LerpColor(Color.FromArgb(255, 226, 240, 255), _accent, 0.20);
        byte glazeAlpha = (byte)Math.Clamp(Math.Round((_isLight ? 34.0 : 24.0) * _intensity), 10, 68);
        return (sheenTint, screenAlpha, glazeTint, glazeAlpha);
    }

    private Color GetVeilColor()
    {
        byte a = (byte)Math.Clamp(Math.Round(_contrastVeil * 255.0), 0, 255);
        return _isLight
            ? Color.FromArgb(a, 250, 251, 253)
            : Color.FromArgb(a, 10, 13, 22);
    }

    private static double ColorToGray(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}
