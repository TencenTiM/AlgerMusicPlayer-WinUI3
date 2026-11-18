using LiquidGlass.WinUI.Materials;
using Microsoft.UI.Xaml;

namespace LiquidGlass.WinUI.Controls;

/// <summary>
/// Liquid Glass 灵动坞/悬浮胶囊控件（对应 iOS Dock）。
/// 低模糊度保留底层背景丰富色彩，叠加较强边缘光学折射与清晰上下镜面棱线。
/// </summary>
public class LiquidGlassDock : LiquidGlassPanel
{
    public LiquidGlassDock()
    {
        DefaultStyleKey = typeof(LiquidGlassDock);
        MaterialKind = LiquidGlassMaterialKind.Dock;
        BlurAmount = 6.0;
        TintOpacity = 0.44;
        EdgeHighlightOpacity = 0.24;
        RefractionStrength = 0.30;
        IsCapsule = true;
        CornerRadius = new CornerRadius(42);
    }

    protected override void OnBeforeRefreshMaterial(LiquidGlassMaterial material)
    {
        if (IsCapsule && ActualWidth > 12.0 && ActualHeight > 12.0)
        {
            double half = Math.Min(ActualWidth, ActualHeight) * 0.5;
            material.CornerRadius = new CornerRadius(half);
        }
    }
}
