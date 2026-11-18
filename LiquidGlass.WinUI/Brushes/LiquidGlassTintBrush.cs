using LiquidGlass.WinUI.Materials;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace LiquidGlass.WinUI.Brushes;

/// <summary>
/// Liquid Glass 第 2 层（Tint）玻璃着色与纵向微梯度透光画刷，用于在模糊后建立中性可读表面并避免死白/死黑平板感。
/// </summary>
public sealed class LiquidGlassTintBrush : XamlCompositionBrushBase
{
    public static readonly DependencyProperty TintColorProperty = DependencyProperty.Register(
        nameof(TintColor),
        typeof(Color),
        typeof(LiquidGlassTintBrush),
        new PropertyMetadata(Color.FromArgb(255, 22, 26, 32), OnTintChanged));

    public static readonly DependencyProperty TintOpacityProperty = DependencyProperty.Register(
        nameof(TintOpacity),
        typeof(double),
        typeof(LiquidGlassTintBrush),
        new PropertyMetadata(0.28, OnTintChanged));

    public static readonly DependencyProperty IsLightProperty = DependencyProperty.Register(
        nameof(IsLight),
        typeof(bool),
        typeof(LiquidGlassTintBrush),
        new PropertyMetadata(false, OnTintChanged));

    private bool _isConnected;

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

    public bool IsLight
    {
        get => (bool)GetValue(IsLightProperty);
        set => SetValue(IsLightProperty, value);
    }

    public void ApplyMaterial(LiquidGlassMaterial material)
    {
        TintColor = material.TintColor;
        TintOpacity = material.TintOpacity;
        IsLight = material.IsLight;
        FallbackColor = material.FallbackColor;
        UpdateStops();
    }

    protected override void OnConnected()
    {
        _isConnected = true;
        UpdateStops();
    }

    protected override void OnDisconnected()
    {
        _isConnected = false;
        if (CompositionBrush != null)
        {
            CompositionBrush.Dispose();
            CompositionBrush = null;
        }
    }

    private static void OnTintChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LiquidGlassTintBrush brush)
        {
            brush.UpdateStops();
        }
    }

    private void UpdateStops()
    {
        if (!_isConnected)
        {
            return;
        }

        var compositor = CompositionTarget.GetCompositorForCurrentThread();
        if (CompositionBrush is not CompositionLinearGradientBrush gradient)
        {
            gradient = compositor.CreateLinearGradientBrush();
            gradient.MappingMode = CompositionMappingMode.Relative;
            gradient.StartPoint = new System.Numerics.Vector2(0f, 0f);
            gradient.EndPoint = new System.Numerics.Vector2(0f, 1f);
            CompositionBrush = gradient;
        }

        double op = Math.Clamp(TintOpacity, 0.0, 1.0);
        // 顶部稍微轻透以配合边缘高光，中下部保持稳定透光度
        double topOp = IsLight ? Math.Min(1.0, op * 1.08) : Math.Max(0.05, op * 0.90);
        double bottomOp = IsLight ? Math.Max(0.05, op * 0.92) : Math.Min(1.0, op * 1.10);

        byte topA = (byte)Math.Clamp((int)Math.Round(topOp * 255.0), 0, 255);
        byte midA = (byte)Math.Clamp((int)Math.Round(op * 255.0), 0, 255);
        byte botA = (byte)Math.Clamp((int)Math.Round(bottomOp * 255.0), 0, 255);

        gradient.ColorStops.Clear();
        gradient.ColorStops.Add(compositor.CreateColorGradientStop(0.0f, Color.FromArgb(topA, TintColor.R, TintColor.G, TintColor.B)));
        gradient.ColorStops.Add(compositor.CreateColorGradientStop(0.45f, Color.FromArgb(midA, TintColor.R, TintColor.G, TintColor.B)));
        gradient.ColorStops.Add(compositor.CreateColorGradientStop(1.0f, Color.FromArgb(botA, TintColor.R, TintColor.G, TintColor.B)));
    }
}
