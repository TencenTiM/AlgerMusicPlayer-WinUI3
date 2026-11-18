using System;
using AlgerMusicPlayer.WinUI.Services.System;
using LiquidGlassWinUI;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AlgerMusicPlayer.WinUI.Controls;

/// <summary>
/// 通用液态玻璃面板：底层接入 LiquidGlassWinUI 原生 DWM HLSL 着色器画刷（超椭圆 SDF 折射、色散、菲涅耳与镜面高光），
/// 并在 Mica / 亚克力 / 视频壁纸模式下自动降级为高对比纯色材质以规避透明背景采样发黑问题。
/// </summary>
public sealed class GlassPanel : ContentControl
{
    public GlassPanel()
    {
        DefaultStyleKey = typeof(GlassPanel);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => UpdateEffect();
        RegisterPropertyChangedCallback(CornerRadiusProperty, (_, _) =>
        {
            if (!_syncingCapsuleCorner)
            {
                ApplyShape();
            }
        });
    }

    public static readonly DependencyProperty BlurProperty = DependencyProperty.Register(
        nameof(Blur),
        typeof(double),
        typeof(GlassPanel),
        new PropertyMetadata(GlassTokens.BlurCard, (d, _) => ((GlassPanel)d).UpdateEffect()));

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint),
        typeof(Color),
        typeof(GlassPanel),
        new PropertyMetadata(GlassTokens.TintCard, (d, _) => ((GlassPanel)d).UpdateEffect()));

    public static readonly DependencyProperty IsCapsuleProperty = DependencyProperty.Register(
        nameof(IsCapsule),
        typeof(bool),
        typeof(GlassPanel),
        new PropertyMetadata(false, (d, _) => ((GlassPanel)d).ApplyShape()));

    public static readonly DependencyProperty SaturationProperty = DependencyProperty.Register(
        nameof(Saturation),
        typeof(double),
        typeof(GlassPanel),
        new PropertyMetadata((double)GlassTokens.Saturation, (d, _) => ((GlassPanel)d).UpdateEffect()));

    public static readonly DependencyProperty RefThicknessProperty = DependencyProperty.Register(
        nameof(RefThickness),
        typeof(double),
        typeof(GlassPanel),
        new PropertyMetadata(0.0, (d, _) => ((GlassPanel)d).ApplyShape()));

    public static readonly DependencyProperty RefFactorProperty = DependencyProperty.Register(
        nameof(RefFactor),
        typeof(double),
        typeof(GlassPanel),
        new PropertyMetadata(GlassTokens.DefaultRefFactor, (d, _) => ((GlassPanel)d).UpdateEffect()));

    public static readonly DependencyProperty RefDispersionProperty = DependencyProperty.Register(
        nameof(RefDispersion),
        typeof(double),
        typeof(GlassPanel),
        new PropertyMetadata(GlassTokens.DefaultRefDispersion, (d, _) => ((GlassPanel)d).UpdateEffect()));

    public static readonly DependencyProperty GlareFactorProperty = DependencyProperty.Register(
        nameof(GlareFactor),
        typeof(double),
        typeof(GlassPanel),
        new PropertyMetadata(GlassTokens.DefaultGlareFactor, (d, _) => ((GlassPanel)d).UpdateEffect()));

    public static readonly DependencyProperty MagnificationProperty = DependencyProperty.Register(
        nameof(Magnification),
        typeof(double),
        typeof(GlassPanel),
        new PropertyMetadata(1.0, (d, _) => ((GlassPanel)d).UpdateEffect()));

    public static readonly DependencyProperty ExposureProperty = DependencyProperty.Register(
        nameof(Exposure),
        typeof(double),
        typeof(GlassPanel),
        new PropertyMetadata(0.0, (d, _) => ((GlassPanel)d).UpdateEffect()));

    public static readonly DependencyProperty BloomAmountProperty = DependencyProperty.Register(
        nameof(BloomAmount),
        typeof(double),
        typeof(GlassPanel),
        new PropertyMetadata(0.85, (d, _) => ((GlassPanel)d).UpdateEffect()));

    public double Blur
    {
        get => (double)GetValue(BlurProperty);
        set => SetValue(BlurProperty, value);
    }

    public Color Tint
    {
        get => (Color)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    public bool IsCapsule
    {
        get => (bool)GetValue(IsCapsuleProperty);
        set => SetValue(IsCapsuleProperty, value);
    }

    public double Saturation
    {
        get => (double)GetValue(SaturationProperty);
        set => SetValue(SaturationProperty, value);
    }

    public double RefThickness
    {
        get => (double)GetValue(RefThicknessProperty);
        set => SetValue(RefThicknessProperty, value);
    }

    public double RefFactor
    {
        get => (double)GetValue(RefFactorProperty);
        set => SetValue(RefFactorProperty, value);
    }

    public double RefDispersion
    {
        get => (double)GetValue(RefDispersionProperty);
        set => SetValue(RefDispersionProperty, value);
    }

    public double GlareFactor
    {
        get => (double)GetValue(GlareFactorProperty);
        set => SetValue(GlareFactorProperty, value);
    }

    public double Magnification
    {
        get => (double)GetValue(MagnificationProperty);
        set => SetValue(MagnificationProperty, value);
    }

    public double Exposure
    {
        get => (double)GetValue(ExposureProperty);
        set => SetValue(ExposureProperty, value);
    }

    public double BloomAmount
    {
        get => (double)GetValue(BloomAmountProperty);
        set => SetValue(BloomAmountProperty, value);
    }

    private Border? _glassLayer;
    private Border? _edgeBorder;
    private LiquidGlassBrush? _lgBrush;
    private SolidColorBrush? _degradedBrush;
    private bool _hooksAttached;
    private bool _hubSubscribed;
    private bool _syncingCapsuleCorner;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _glassLayer = GetTemplateChild("GlassLayer") as Border;
        _edgeBorder = GetTemplateChild("EdgeHighlightBorder") as Border;

        if (!_hooksAttached)
        {
            _hooksAttached = true;
            SizeChanged += (_, _) => ApplyShape();
        }

        EnsureBrushInitialized();
        ApplyShape();
        UpdateEffect();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_hubSubscribed)
        {
            _hubSubscribed = true;
            GlassHub.StateChanged += OnHubStateChanged;
        }

        EnsureBrushInitialized();
        ApplyShape();
        UpdateEffect();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hubSubscribed)
        {
            _hubSubscribed = false;
            GlassHub.StateChanged -= OnHubStateChanged;
        }
    }

    private void OnHubStateChanged()
    {
        EnsureBrushInitialized();
        ApplyShape();
        UpdateEffect();
    }

    private void EnsureBrushInitialized()
    {
        if (_lgBrush == null)
        {
            _lgBrush = new LiquidGlassBrush
            {
                DispersionRange = GlassTokens.DefaultDispersionRange,
                RefFresnelRange = GlassTokens.DefaultRefFresnelRange,
                RefFresnelHardness = GlassTokens.DefaultRefFresnelHardness,
                RefFresnelFactor = GlassTokens.DefaultRefFresnelFactor,
                GlareAngle = GlassTokens.DefaultGlareAngle,
                GlareConvergence = GlassTokens.DefaultGlareConvergence,
                GlareOppositeFactor = GlassTokens.DefaultGlareOppositeFactor,
                GlareHardness = GlassTokens.DefaultGlareHardness,
                GlareRange = GlassTokens.DefaultGlareRange,
                BloomAmount = 0.85,
                Brightness = 0.0,
                Temperature = 0.0
            };
        }
    }

    private void ApplyShape()
    {
        var s = ActualSize;
        if (IsCapsule && s.X > 0 && s.Y > 0)
        {
            double rCapsule = Math.Min(s.X, s.Y) / 2.0;
            var cur = CornerRadius;
            if (Math.Abs(cur.TopLeft - rCapsule) > 0.05 ||
                Math.Abs(cur.TopRight - rCapsule) > 0.05 ||
                Math.Abs(cur.BottomLeft - rCapsule) > 0.05 ||
                Math.Abs(cur.BottomRight - rCapsule) > 0.05)
            {
                _syncingCapsuleCorner = true;
                try
                {
                    CornerRadius = new CornerRadius(rCapsule);
                }
                finally
                {
                    _syncingCapsuleCorner = false;
                }
            }
        }

        if (_glassLayer != null)
        {
            bool useDegraded = GlassHub.IsDegradedMode || LiquidGlassBrush.LastError != null;
            _glassLayer.CornerRadius = useDegraded ? CornerRadius : new CornerRadius(0);
        }

        if (_lgBrush == null)
        {
            return;
        }

        double w = s.X > 0 ? s.X : (double.IsNaN(Width) || Width <= 0 ? 320.0 : Width);
        double h = s.Y > 0 ? s.Y : (double.IsNaN(Height) || Height <= 0 ? 48.0 : Height);
        double minSide = Math.Max(2.0, Math.Min(w, h));

        if (IsCapsule)
        {
            _lgBrush.ShapeRadius = 1.0;
            _lgBrush.ShapeRoundness = 2.0;
        }
        else
        {
            var cr = CornerRadius;
            double maxCr = Math.Max(Math.Max(cr.TopLeft, cr.TopRight), Math.Max(cr.BottomLeft, cr.BottomRight));
            _lgBrush.ShapeRadius = Math.Max(4.0, maxCr);
            _lgBrush.ShapeRoundness = 2.0;
        }

        // 非胶囊面板的折射斜边厚度不超过圆角半径 maxCr，杜绝圆角内侧产生 45° 对角折痕与拉伸模糊
        double maxSafe = Math.Max(10.0, minSide * 0.48);
        double targetRef = RefThickness > 0.0 ? RefThickness : (IsCapsule ? 44.0 : 22.0);
        if (!IsCapsule)
        {
            var cr = CornerRadius;
            double maxCr = Math.Max(Math.Max(cr.TopLeft, cr.TopRight), Math.Max(cr.BottomLeft, cr.BottomRight));
            if (maxCr > 0.0)
            {
                maxSafe = Math.Min(maxSafe, Math.Max(12.0, maxCr));
            }
        }
        _lgBrush.RefThickness = Math.Clamp(targetRef, 10.0, Math.Min(90.0, maxSafe));
    }

    private void UpdateEffect()
    {
        if (_glassLayer == null)
        {
            return;
        }

        EnsureBrushInitialized();

        bool isDegraded = GlassHub.IsDegradedMode || LiquidGlassBrush.LastError != null;
        if (isDegraded)
        {
            _glassLayer.CornerRadius = CornerRadius;
            _degradedBrush ??= new SolidColorBrush();
            _degradedBrush.Color = ResolveDegradedColor();
            if (!ReferenceEquals(_glassLayer.Background, _degradedBrush))
            {
                _glassLayer.Background = _degradedBrush;
            }
            if (_edgeBorder != null)
            {
                _edgeBorder.Visibility = Visibility.Visible;
                _edgeBorder.Opacity = 1.0;
            }
            return;
        }

        // LiquidGlass.hlsl 内部已包含 SHAPE_MARGIN 超椭圆抗锯齿、菲涅耳与双向镜面高光，置 0 圆角并隐藏外层防双框
        _glassLayer.CornerRadius = new CornerRadius(0);
        if (_edgeBorder != null)
        {
            _edgeBorder.Visibility = Visibility.Collapsed;
        }

        var brush = _lgBrush!;
        // 对齐主线收窄 BlurH/BlurV 卷积核后的清透模糊曲线：低模糊突出折射弯曲感，避免雾感吞噬光线
        double mappedBlur = Blur <= 0.05
            ? 0.0
            : (Blur <= 5.0 ? Blur : Math.Clamp(0.65 + Blur * 0.045, 0.90, 1.93));
        brush.RefFactor = Math.Clamp(RefFactor, 1.0, 10.0);
        brush.RefDispersion = Math.Clamp(RefDispersion, 0.0, 50.0);
        brush.GlareFactor = Math.Clamp(GlareFactor, 0.0, 120.0);
        brush.Magnification = Math.Clamp(Magnification, 1.0, 3.0);

        // 遵循宿主元素的明确主题（例如 RequestedTheme="Dark" 的沉浸歌词/封面大卡），避免在全局浅色模式下误漂白
        bool effectiveLight = ActualTheme == ElementTheme.Dark
            ? false
            : (ActualTheme == ElementTheme.Light ? true : GlassHub.IsLightMode);

        bool isHighOpaqueDrawer = Tint.A >= 0xB0;
        if (isHighOpaqueDrawer)
        {
            brush.BlurAmount = 1.65;
            brush.BloomAmount = 1.0;
            brush.TintR = Tint.R;
            brush.TintG = Tint.G;
            brush.TintB = Tint.B;
            brush.TintA = 0.88;
            // 浅色主题下高不透明亮色浮层适当提高曝光，保证深色字体清晰可读
            bool isLightSurface = effectiveLight && Tint.R > 180 && Tint.G > 180 && Tint.B > 180;
            brush.Exposure = Exposure > 0.0 ? Exposure : (isLightSurface ? 1.02 : 0.82);
            brush.Brightness = 0.0;
            brush.Contrast = 1.08;
            brush.Saturation = 1.16;
            brush.Vibrance = 0.12;
        }
        else if (effectiveLight)
        {
            brush.BlurAmount = mappedBlur;
            brush.BloomAmount = Math.Clamp(BloomAmount, 0.0, 1.0);
            brush.TintR = 255;
            brush.TintG = 255;
            brush.TintB = 255;
            brush.TintA = 0.22;
            brush.Exposure = Exposure > 0.0 ? Exposure : 1.08;
            brush.Brightness = 0.02;
            brush.Contrast = 1.06;
            brush.Saturation = 1.15;
            brush.Vibrance = 0.18;
        }
        else
        {
            brush.BlurAmount = mappedBlur;
            brush.BloomAmount = Math.Clamp(BloomAmount, 0.0, 1.0);
            // 深色模式下若显式指定深色底 Tint（如顶部粘性栏/封面信息条），保留可控压暗着色保证白字对比度
            bool hasExplicitDarkTint = Tint.A > 0 && (Tint.R < 250 || Tint.G < 250 || Tint.B < 250);
            if (hasExplicitDarkTint)
            {
                brush.TintR = Tint.R;
                brush.TintG = Tint.G;
                brush.TintB = Tint.B;
                brush.TintA = Math.Clamp(Tint.A / 255.0, 0.0, 0.85);
                brush.Exposure = Exposure > 0.0 ? Exposure : 1.08;
            }
            else
            {
                brush.TintR = 255;
                brush.TintG = 255;
                brush.TintB = 255;
                brush.TintA = 0.0;
                brush.Exposure = Exposure > 0.0 ? Exposure : 1.14;
            }
            brush.Brightness = 0.03;
            brush.Contrast = 1.08;
            brush.Saturation = 1.15;
            brush.Vibrance = 0.20;
        }

        if (!ReferenceEquals(_glassLayer.Background, brush))
        {
            _glassLayer.Background = brush;
        }
    }

    private Color ResolveDegradedColor()
    {
        if (Tint.A >= 0xB0 && !GlassHub.IsLightMode)
        {
            return Tint;
        }

        if (GlassHub.IsLightMode)
        {
            return Tint.A >= 0xB0 ? Tint : GlassTokens.DegradedTintLight;
        }

        return Color.FromArgb(
            Math.Max(GlassTokens.DegradedTintDark.A, Tint.A),
            GlassTokens.DegradedTintDark.R,
            GlassTokens.DegradedTintDark.G,
            GlassTokens.DegradedTintDark.B);
    }
}
