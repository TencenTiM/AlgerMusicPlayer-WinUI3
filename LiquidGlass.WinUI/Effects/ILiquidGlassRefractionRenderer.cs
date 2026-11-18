using LiquidGlass.WinUI.Materials;
using Microsoft.UI.Xaml;

namespace LiquidGlass.WinUI.Effects;

/// <summary>
/// Liquid Glass 边缘光学折射渲染器接口（对应增强效果组 1：边缘定向光学折射）。
/// 将边缘折射拆分为独立可替换模块，便于未来无缝升级到 Custom Shader / SwapChain 实现。
/// </summary>
public interface ILiquidGlassRefractionRenderer
{
    /// <summary>
    /// 在指定宿主元素上附加或更新 8 区域无缝边缘折射 Visual 树。
    /// </summary>
    void AttachOrUpdate(UIElement hostElement, LiquidGlassMaterial material, double width, double height);

    /// <summary>
    /// 从宿主元素上移除折射 Visual 树并释放相关 Composition 资源。
    /// </summary>
    void Detach(UIElement hostElement);
}
