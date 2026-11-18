using Microsoft.UI.Xaml;
using Windows.UI;

namespace LiquidGlass.WinUI.Materials;

/// <summary>
/// Liquid Glass（液态玻璃）五层渲染光学参数模型。
/// </summary>
public sealed class LiquidGlassMaterial
{
    /// <summary>
    /// 材质类别。
    /// </summary>
    public LiquidGlassMaterialKind Kind { get; set; } = LiquidGlassMaterialKind.Folder;

    /// <summary>
    /// 第 1 层（Backdrop）高斯模糊半径（DIPs）。
    /// Dock 推荐 2~8，Folder 推荐 16~26。
    /// </summary>
    public double BlurAmount { get; set; } = 22.0;

    /// <summary>
    /// 背景色彩饱和度增强系数（1.0 为原色）。
    /// </summary>
    public double Saturation { get; set; } = 1.05;

    /// <summary>
    /// 第 2 层（Tint）玻璃着色基准色（保持中性白/炭黑倾向，不默认偏蓝紫）。
    /// </summary>
    public Color TintColor { get; set; } = Color.FromArgb(255, 22, 26, 32);

    /// <summary>
    /// 第 2 层（Tint）着色透明度（0.0 ~ 1.0）。
    /// </summary>
    public double TintOpacity { get; set; } = 0.28;

    /// <summary>
    /// 当系统关闭透明效果或开启高对比度时的纯色回退底色。
    /// </summary>
    public Color FallbackColor { get; set; } = Color.FromArgb(224, 28, 32, 38);

    /// <summary>
    /// 第 3 层（Reflection）斜向环境反光强度（0.0 ~ 1.0）。
    /// </summary>
    public double ReflectionOpacity { get; set; } = 0.12;

    /// <summary>
    /// 第 4 层（Highlight）边缘镜面高光强度（0.0 ~ 1.0）。
    /// </summary>
    public double EdgeHighlightOpacity { get; set; } = 0.18;

    /// <summary>
    /// 玻璃内阴影微沉层次透明度。
    /// </summary>
    public double InnerShadowOpacity { get; set; } = 0.08;

    /// <summary>
    /// 边缘定向折射可见强度（0.0 ~ 1.0）。
    /// </summary>
    public double RefractionStrength { get; set; } = 0.12;

    /// <summary>
    /// 边缘透镜光学放大率倍率。
    /// </summary>
    public double RefractionScale { get; set; } = 3.5;

    /// <summary>
    /// 边缘折射带有效宽度（DIPs）。
    /// </summary>
    public double RefractionEdgeWidth { get; set; } = 8.0;

    /// <summary>
    /// 玻璃圆角（对 Dock 通常为胶囊半高）。
    /// </summary>
    public CornerRadius CornerRadius { get; set; } = new(24);

    /// <summary>
    /// 边缘描边厚度（DIPs）。
    /// </summary>
    public double BorderThickness { get; set; } = 0.9;

    /// <summary>
    /// 底部投影强度（0.0 ~ 1.0）。
    /// </summary>
    public double ElevationShadowOpacity { get; set; } = 0.24;

    /// <summary>
    /// 浅色主题标记。
    /// </summary>
    public bool IsLight { get; set; }

    /// <summary>
    /// 创建当前材质实例的浅拷贝，方便局部微调而不污染全局预设。
    /// </summary>
    public LiquidGlassMaterial Clone()
    {
        return new LiquidGlassMaterial
        {
            Kind = Kind,
            BlurAmount = BlurAmount,
            Saturation = Saturation,
            TintColor = TintColor,
            TintOpacity = TintOpacity,
            FallbackColor = FallbackColor,
            ReflectionOpacity = ReflectionOpacity,
            EdgeHighlightOpacity = EdgeHighlightOpacity,
            InnerShadowOpacity = InnerShadowOpacity,
            RefractionStrength = RefractionStrength,
            RefractionScale = RefractionScale,
            RefractionEdgeWidth = RefractionEdgeWidth,
            CornerRadius = CornerRadius,
            BorderThickness = BorderThickness,
            ElevationShadowOpacity = ElevationShadowOpacity,
            IsLight = IsLight
        };
    }
}
