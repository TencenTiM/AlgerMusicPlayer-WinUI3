using LiquidGlass.WinUI.Materials;
using LiquidGlass.WinUI.Rendering;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LiquidGlass.WinUI.Brushes;

/// <summary>
/// Liquid Glass 核心背景采集与高斯模糊画刷（对应五层渲染模型第 1 层 Backdrop，并支持单独作为单画刷使用）。
/// </summary>
public sealed class LiquidGlassBackdropBrush : XamlCompositionBrushBase
{
    public static readonly DependencyProperty BlurAmountProperty = DependencyProperty.Register(
        nameof(BlurAmount),
        typeof(double),
        typeof(LiquidGlassBackdropBrush),
        new PropertyMetadata(22.0, OnOpticalPropertyChanged));

    public static readonly DependencyProperty SaturationProperty = DependencyProperty.Register(
        nameof(Saturation),
        typeof(double),
        typeof(LiquidGlassBackdropBrush),
        new PropertyMetadata(1.05, OnOpticalPropertyChanged));

    public static readonly DependencyProperty TintColorProperty = DependencyProperty.Register(
        nameof(TintColor),
        typeof(Color),
        typeof(LiquidGlassBackdropBrush),
        new PropertyMetadata(Color.FromArgb(255, 22, 26, 32), OnOpticalPropertyChanged));

    public static readonly DependencyProperty TintOpacityProperty = DependencyProperty.Register(
        nameof(TintOpacity),
        typeof(double),
        typeof(LiquidGlassBackdropBrush),
        new PropertyMetadata(0.28, OnOpticalPropertyChanged));

    public static readonly DependencyProperty IncludeTintInBrushProperty = DependencyProperty.Register(
        nameof(IncludeTintInBrush),
        typeof(bool),
        typeof(LiquidGlassBackdropBrush),
        new PropertyMetadata(true, OnStructurePropertyChanged));

    private bool _isConnected;

    public double BlurAmount
    {
        get => (double)GetValue(BlurAmountProperty);
        set => SetValue(BlurAmountProperty, value);
    }

    public double Saturation
    {
        get => (double)GetValue(SaturationProperty);
        set => SetValue(SaturationProperty, value);
    }

    public Color TintColor
    {
        get => (Color)GetValue(TintColorProperty);
        set => SetValue(TintColorProperty, value);
    }

    public double TintOpacity
    {
        get => (double)GetValue(TintOpacityProperty);
        set => SetValue(TintOpacityProperty, value);
    }

    /// <summary>
    /// 为 true 时在 Composition 着色器内部一并叠染 TintColor（适用于单层 Border.Background）；
    /// 为 false 时仅输出纯 Backdrop + Saturation + GaussianBlur（供五层控件独立第 2 层 PART_Tint 使用）。
    /// </summary>
    public bool IncludeTintInBrush
    {
        get => (bool)GetValue(IncludeTintInBrushProperty);
        set => SetValue(IncludeTintInBrushProperty, value);
    }

    /// <summary>
    /// 从材质预设批量同步光学参数。
    /// </summary>
    public void ApplyMaterial(LiquidGlassMaterial material, bool includeTintInBrush = true)
    {
        BlurAmount = material.BlurAmount;
        Saturation = material.Saturation;
        TintColor = material.TintColor;
        TintOpacity = material.TintOpacity;
        FallbackColor = material.FallbackColor;
        IncludeTintInBrush = includeTintInBrush;
        SyncConnectedProperties();
    }

    protected override void OnConnected()
    {
        _isConnected = true;
        RebuildCompositionBrush();
        LiquidGlassMaterialManager.Current.MaterialEnvironmentChanged += OnMaterialEnvironmentChanged;
    }

    protected override void OnDisconnected()
    {
        _isConnected = false;
        LiquidGlassMaterialManager.Current.MaterialEnvironmentChanged -= OnMaterialEnvironmentChanged;

        if (CompositionBrush != null)
        {
            CompositionBrush.Dispose();
            CompositionBrush = null;
        }
    }

    private void OnMaterialEnvironmentChanged(object? sender, EventArgs e)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (_isConnected)
            {
                RebuildCompositionBrush();
            }
        });
    }

    private static void OnOpticalPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LiquidGlassBackdropBrush brush)
        {
            brush.SyncConnectedProperties();
        }
    }

    private static void OnStructurePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LiquidGlassBackdropBrush brush && brush._isConnected)
        {
            brush.RebuildCompositionBrush();
        }
    }

    private void RebuildCompositionBrush()
    {
        if (!_isConnected)
        {
            return;
        }

        if (CompositionBrush != null)
        {
            CompositionBrush.Dispose();
            CompositionBrush = null;
        }

        var compositor = CompositionTarget.GetCompositorForCurrentThread();
        if (LiquidGlassMaterialManager.Current.ShouldUseFallbackSolid)
        {
            CompositionBrush = compositor.CreateColorBrush(FallbackColor);
            return;
        }

        var cache = CompositionResourceCache.GetForCompositor(compositor);
        CompositionBrush = cache.CreateBackdropBrush(
            BlurAmount,
            Saturation,
            TintColor,
            TintOpacity,
            IncludeTintInBrush,
            FallbackColor);
    }

    private void SyncConnectedProperties()
    {
        if (CompositionBrush is CompositionEffectBrush effectBrush)
        {
            CompositionResourceCache.UpdateBackdropBrushProperties(
                effectBrush,
                BlurAmount,
                Saturation,
                TintColor,
                TintOpacity,
                IncludeTintInBrush);
        }
        else if (CompositionBrush is CompositionColorBrush colorBrush)
        {
            colorBrush.Color = FallbackColor;
        }
    }
}
