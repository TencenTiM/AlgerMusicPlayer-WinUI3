using LiquidGlass.WinUI.Materials;
using LiquidGlass.WinUI.Rendering;
using Microsoft.UI.Xaml;

namespace LiquidGlass.WinUI.Controls;

/// <summary>
/// Liquid Glass 大圆角磨砂内容容器控件（对应 iOS Folder / 内容卡片）。
/// 具备明显背景高斯模糊（默认 22px）、自然中性透色、柔和平滑边角折射与悬停微亮反馈。
/// </summary>
public class LiquidGlassFolder : LiquidGlassPanel
{
    public LiquidGlassFolder()
    {
        DefaultStyleKey = typeof(LiquidGlassFolder);
        MaterialKind = LiquidGlassMaterialKind.Folder;
        BlurAmount = 22.0;
        TintOpacity = 0.28;
        EdgeHighlightOpacity = 0.16;
        RefractionStrength = 0.10;
        CornerRadius = new CornerRadius(24);
    }

    /// <summary>
    /// 触发 Folder 弹出微弹入场动效（GPU Composition 缩放 + 渐显）。
    /// </summary>
    public void AnimateOpen()
    {
        GlassAnimationController.PlayFolderEnterAnimation(this);
    }
}
