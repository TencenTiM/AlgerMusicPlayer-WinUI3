using LiquidGlass.WinUI.Materials;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace LiquidGlass.WinUI.Rendering;

/// <summary>
/// Liquid Glass 第 3 层（顶部 35% 内高光）与第 4 层（4 停靠点斜向高光边缘光）画刷生成器。
/// 对齐 Alger 液态玻璃规范：左上受光高光 -> 侧腰消隐 -> 右下冰青焦散，兼顾浅色与深色高对比度。
/// </summary>
public static class GlassHighlightRenderer
{
    /// <summary>
    /// 创建第 3 层（Reflection）：玻璃表层顶部 35% 克制内高光渐变（深色 #14FFFFFF -> #00FFFFFF，浅色 #30FFFFFF -> #00FFFFFF）。
    /// </summary>
    public static LinearGradientBrush CreateReflectionBrush(LiquidGlassMaterial material)
    {
        byte topA = material.IsLight ? (byte)0x30 : (byte)0x14;
        var tone = Color.FromArgb(255, 255, 255, 255);

        return new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0.0),
            EndPoint = new Point(0.5, 1.0),
            GradientStops =
            {
                new GradientStop { Offset = 0.00, Color = Color.FromArgb(topA, tone.R, tone.G, tone.B) },
                new GradientStop { Offset = 0.35, Color = Color.FromArgb(0, tone.R, tone.G, tone.B) },
                new GradientStop { Offset = 1.00, Color = Color.FromArgb(0, tone.R, tone.G, tone.B) }
            }
        };
    }

    /// <summary>
    /// 创建第 4 层（Highlight）外沿 1px 克制纯白对角高光描边（无绿/无青染色）：
    /// 深色：0.00 #80FFFFFF -> 0.40/0.60 #14FFFFFF -> 1.00 #38FFFFFF；
    /// 浅色：0.00 #B0FFFFFF -> 0.40/0.60 #24FFFFFF -> 1.00 #50FFFFFF。
    /// </summary>
    public static LinearGradientBrush CreateSpecularBorderBrush(LiquidGlassMaterial material, double boost = 1.0)
    {
        _ = boost;

        if (material.IsLight)
        {
            return new LinearGradientBrush
            {
                StartPoint = new Point(0.0, 0.0),
                EndPoint = new Point(1.0, 1.0),
                GradientStops =
                {
                    new GradientStop { Offset = 0.00, Color = Color.FromArgb(0xB0, 255, 255, 255) },
                    new GradientStop { Offset = 0.40, Color = Color.FromArgb(0x24, 255, 255, 255) },
                    new GradientStop { Offset = 0.60, Color = Color.FromArgb(0x24, 255, 255, 255) },
                    new GradientStop { Offset = 1.00, Color = Color.FromArgb(0x50, 255, 255, 255) }
                }
            };
        }

        return new LinearGradientBrush
        {
            StartPoint = new Point(0.0, 0.0),
            EndPoint = new Point(1.0, 1.0),
            GradientStops =
            {
                new GradientStop { Offset = 0.00, Color = Color.FromArgb(0x80, 255, 255, 255) },
                new GradientStop { Offset = 0.40, Color = Color.FromArgb(0x14, 255, 255, 255) },
                new GradientStop { Offset = 0.60, Color = Color.FromArgb(0x14, 255, 255, 255) },
                new GradientStop { Offset = 1.00, Color = Color.FromArgb(0x38, 255, 255, 255) }
            }
        };
    }

    /// <summary>
    /// 创建内部微沉边缘内阴影画刷（极低不透明度，增强厚度感而不造成脏边）。
    /// </summary>
    public static LinearGradientBrush CreateInnerDepthBrush(LiquidGlassMaterial material)
    {
        double inner = Math.Clamp(material.InnerShadowOpacity, 0.0, 0.35);
        byte topRim = (byte)Math.Clamp((int)Math.Round(Math.Min(0.28, material.EdgeHighlightOpacity * 0.42) * 255.0), 0, 255);
        byte bottomDark = (byte)Math.Clamp((int)Math.Round(inner * 255.0), 0, 255);

        return new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0.0),
            EndPoint = new Point(0.5, 1.0),
            GradientStops =
            {
                new GradientStop { Offset = 0.00, Color = Color.FromArgb(topRim, 255, 255, 255) },
                new GradientStop { Offset = 0.14, Color = Color.FromArgb(0, 255, 255, 255) },
                new GradientStop { Offset = 0.86, Color = Color.FromArgb(0, 0, 0, 0) },
                new GradientStop { Offset = 1.00, Color = Color.FromArgb(bottomDark, 0, 0, 0) }
            }
        };
    }
}
