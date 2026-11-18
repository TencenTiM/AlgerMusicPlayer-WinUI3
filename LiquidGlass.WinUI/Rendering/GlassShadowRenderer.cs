using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LiquidGlass.WinUI.Rendering;

/// <summary>
/// Liquid Glass 层级阴影渲染辅助类（基于 WinUI 3 ThemeShadow）。
/// </summary>
public static class GlassShadowRenderer
{
    /// <summary>
    /// 安全挂载 WinUI 3 ThemeShadow 并提升 Z 轴高度，若宿主尚无接收器则静默降级不抛异常。
    /// </summary>
    public static void ApplyElevation(UIElement target, float elevationZ, UIElement? shadowReceiver = null)
    {
        if (target == null)
        {
            return;
        }

        try
        {
            if (target.Shadow is not ThemeShadow themeShadow)
            {
                themeShadow = new ThemeShadow();
                target.Shadow = themeShadow;
            }

            if (shadowReceiver != null && !themeShadow.Receivers.Contains(shadowReceiver))
            {
                themeShadow.Receivers.Add(shadowReceiver);
            }

            target.Translation = new Vector3(0f, 0f, Math.Max(0f, elevationZ));
        }
        catch
        {
            // 部分嵌套虚拟化容器不支持 ThemeShadow Receiver 绑定时安全跳过
        }
    }

    public static void ClearElevation(UIElement target)
    {
        if (target == null)
        {
            return;
        }

        try
        {
            target.Shadow = null;
            target.Translation = Vector3.Zero;
        }
        catch
        {
        }
    }
}
