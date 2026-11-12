using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AlgerMusicPlayer.WinUI.Services.System;

/// <summary>
/// 原生 Windows DWM 合成器级 iOS 27「液态玻璃 (Liquid Glass)」实时 GPU 光学水晶画刷。
/// 100% 采用同域纯 IGraphicsEffect 描述符管线 + CompositionBackdropBrush 实时采样：
/// 1. 背后的壁纸/极光/视频 100% 锁死在窗口底层，DirectManipulation 240Hz 滚动时绝不随卡片移动；
/// 2. 中心采用超清透零雾光学管线（0.45px 微润晶体 + 1.26x 鲜明度提艳 + 1.045x 通透微对比），完整保留背后景物锐利细节，告别泛白毛玻璃雾感；
/// 3. 配合 OpticalLiquidGlassRenderer 挂载的原生 DWM 圆角边缘透镜折射环与 3D 弯月面高光暗槽，呈现饱满立体的凸水滴水晶质感。
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
        float blurAmount = 0.45f,
        float saturation = 1.26f)
    {
        _accent = accent;
        _isLight = isLight;
        _intensity = (float)Math.Clamp(intensity, 0.55, 1.55);
        _blurAmount = Math.Clamp(blurAmount, 0.0f, 16.0f);
        _saturation = Math.Clamp(saturation, 1.05f, 1.65f);

        FallbackColor = isLight
            ? Color.FromArgb(14, 255, 255, 255)
            : Color.FromArgb(18, 15, 23, 42);
    }

    public Color Accent => _accent;
    public bool IsLight => _isLight;
    public double Intensity => _intensity;
    public float BlurAmount => _blurAmount;
    public double ContrastVeil => _contrastVeil;

    /// <summary>
    /// 雾化层颜色的 sRGB 灰度（浅色基调为冰白、深色基调为墨夜）
    /// </summary>
    public static double GetVeilGray(bool isLight) => isLight ? 0.975 : 0.045;

    /// <summary>
    /// 复制一份参数完全相同的独立画刷实例
    /// </summary>
    public LiquidGlassCompositionBrush CloneIndependent() =>
        new(_accent, _isLight, _intensity, _blurAmount, _saturation);

    /// <summary>
    /// 估算背后壁纸经过本玻璃中心后的 sRGB 灰度
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
    /// 设置可选微衬底浓度（默认 0 完全清透零白雾）
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
            anim.Duration = TimeSpan.FromMilliseconds(animate ? 260 : 1);
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

            // 1. 水晶鲜明度光学提艳（模拟高折射率液体晶体对背光色彩的润泽增艳）
            using var vibrantBackdrop = new SaturationEffect
            {
                Source = new CompositionEffectSourceParameter("Backdrop"),
                Saturation = _saturation
            };

            // 2. 清透微对比增强（消除泛灰白雾，令透射景物通透清亮）
            using var crispContrast = new ContrastEffect
            {
                Source = vibrantBackdrop,
                Contrast = 0.045f
            };

            // 3. 超清透水润微散射（默认仅 0.45px，完整保留草木与流光锐利纹理）
            using var opticalCrystal = new GaussianBlurEffect
            {
                Source = crispContrast,
                BlurAmount = _blurAmount,
                BorderMode = EffectBorderMode.Hard
            };

            // 4. 极轻盈冰晶水光层（绝不发白发雾）
            using var screenSheenSource = new ColorSourceEffect
            {
                Color = Color.FromArgb(screenAlpha, sheenTint.R, sheenTint.G, sheenTint.B)
            };

            using var screenLifted = new BlendEffect
            {
                Mode = BlendEffectMode.Screen,
                Background = opticalCrystal,
                Foreground = screenSheenSource
            };

            // 5. 极薄水色折射镀膜层
            using var glazeSource = new ColorSourceEffect
            {
                Color = Color.FromArgb(glazeAlpha, glazeTint.R, glazeTint.G, glazeTint.B)
            };

            // 6. 可选局部微衬底层（默认 Alpha=0 完全透明）
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
        Color sheenTint = ThemeService.LerpColor(Color.FromArgb(255, 244, 252, 255), _accent, _isLight ? 0.14 : 0.22);
        byte screenAlpha = (byte)Math.Clamp(Math.Round((_isLight ? 7.0 : 8.0) * _intensity), 3, 20);
        Color glazeTint = _isLight
            ? ThemeService.LerpColor(Color.FromArgb(255, 248, 253, 255), _accent, 0.10)
            : ThemeService.LerpColor(Color.FromArgb(255, 14, 24, 38), _accent, 0.14);
        byte glazeAlpha = (byte)Math.Clamp(Math.Round((_isLight ? 5.0 : 14.0) * _intensity), 2, 26);
        return (sheenTint, screenAlpha, glazeTint, glazeAlpha);
    }

    private Color GetVeilColor()
    {
        byte a = (byte)Math.Clamp(Math.Round(_contrastVeil * 255.0), 0, 255);
        return _isLight
            ? Color.FromArgb(a, 248, 251, 255)
            : Color.FromArgb(a, 8, 13, 23);
    }

    private static double ColorToGray(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}
