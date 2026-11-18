using LiquidGlass.WinUI.Materials;
using Microsoft.UI.Xaml;

namespace LiquidGlass.WinUI.Controls;

/// <summary>
/// 通用 Liquid Glass 玻璃表面容器（默认 Panel 材质，可用于侧栏或 Popover 浮层）。
/// </summary>
public class LiquidGlassSurface : LiquidGlassPanel
{
    public LiquidGlassSurface()
    {
        DefaultStyleKey = typeof(LiquidGlassSurface);
        MaterialKind = LiquidGlassMaterialKind.Panel;
        EnableHoverEffects = false;
        CornerRadius = new CornerRadius(16);
    }
}
