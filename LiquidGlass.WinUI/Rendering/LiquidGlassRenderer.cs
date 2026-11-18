using LiquidGlass.WinUI.Brushes;
using LiquidGlass.WinUI.Effects;
using LiquidGlass.WinUI.Materials;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LiquidGlass.WinUI.Rendering;

/// <summary>
/// Liquid Glass 五层渲染总调度器。
/// 依次统筹：1. Backdrop(高斯模糊背景) -> 2. Tint(中性着色透光) -> 3. Reflection(环境反射+边缘折射) -> 4. Highlight(镜面描边) -> 5. Content(前景内容)。
/// </summary>
public sealed class LiquidGlassRenderer
{
    private readonly ILiquidGlassRefractionRenderer _refractionRenderer;

    public LiquidGlassRenderer(ILiquidGlassRefractionRenderer? refractionRenderer = null)
    {
        _refractionRenderer = refractionRenderer ?? new GlassRefractionRenderer();
    }

    public void ApplyToTemplateParts(
        FrameworkElement owner,
        LiquidGlassMaterial material,
        Border? backdropBorder,
        Border? tintBorder,
        Border? reflectionBorder,
        UIElement? refractionHost,
        Border? highlightBorder,
        bool enableShadow = false)
    {
        var cornerRadius = material.CornerRadius;

        // 第 1 层：Backdrop（纯模糊与饱和度增强）
        if (backdropBorder != null)
        {
            backdropBorder.CornerRadius = cornerRadius;
            if (backdropBorder.Background is not LiquidGlassBackdropBrush backdropBrush)
            {
                backdropBrush = new LiquidGlassBackdropBrush();
                backdropBorder.Background = backdropBrush;
            }
            backdropBrush.ApplyMaterial(material, includeTintInBrush: tintBorder == null);
        }

        // 第 2 层：Tint（中性着色与纵向透光微梯度）
        if (tintBorder != null)
        {
            tintBorder.CornerRadius = cornerRadius;
            if (tintBorder.Background is not LiquidGlassTintBrush tintBrush)
            {
                tintBrush = new LiquidGlassTintBrush();
                tintBorder.Background = tintBrush;
            }
            tintBrush.ApplyMaterial(material);
        }

        // 第 3 层：Reflection（斜向玻璃反射 sheen + 边缘光学折射透镜）
        if (reflectionBorder != null)
        {
            reflectionBorder.CornerRadius = cornerRadius;
            reflectionBorder.Background = GlassHighlightRenderer.CreateReflectionBrush(material);
        }

        if (refractionHost != null)
        {
            double w = owner.ActualWidth;
            double h = owner.ActualHeight;
            if (w > 4 && h > 4)
            {
                _refractionRenderer.AttachOrUpdate(refractionHost, material, w, h);
            }
            else
            {
                _refractionRenderer.Detach(refractionHost);
            }
        }

        // 第 4 层：Highlight（外圈精确镜面高光描边 + 内部柔和顶部/底部深度渐变）
        if (highlightBorder != null)
        {
            highlightBorder.CornerRadius = cornerRadius;
            highlightBorder.BorderThickness = new Thickness(Math.Clamp(material.BorderThickness, 0.5, 1.5));
            highlightBorder.BorderBrush = GlassHighlightRenderer.CreateSpecularBorderBrush(material);
            highlightBorder.Background = GlassHighlightRenderer.CreateInnerDepthBrush(material);
        }

        if (enableShadow)
        {
            float z = material.Kind switch
            {
                LiquidGlassMaterialKind.Dock => 24f,
                LiquidGlassMaterialKind.Popover => 32f,
                LiquidGlassMaterialKind.Folder => 16f,
                _ => 8f
            };
            GlassShadowRenderer.ApplyElevation(owner, z);
        }
    }

    public void UpdateRefractionSize(UIElement? refractionHost, LiquidGlassMaterial material, double width, double height)
    {
        if (refractionHost != null)
        {
            _refractionRenderer.AttachOrUpdate(refractionHost, material, width, height);
        }
    }

    public void DetachRefraction(UIElement? refractionHost)
    {
        if (refractionHost != null)
        {
            _refractionRenderer.Detach(refractionHost);
        }
    }
}
