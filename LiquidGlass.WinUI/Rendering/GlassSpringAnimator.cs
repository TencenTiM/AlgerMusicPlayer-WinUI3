using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

namespace LiquidGlass.WinUI.Rendering;

/// <summary>
/// 原生 DWM 合成器物理弹簧微交互控制器（DampingRatio=0.55f, Period=55ms）：
/// 在 UIElement 上无侵入式挂载按压与悬停 Q 弹缩放（完全由 DWM GPU 合成线程驱动，不阻塞 Click 或 Toggle 事件），
/// 且受全局 IsEnabled 开关严格控制，切出液态玻璃模式后自动恢复 1:1 静态比例。
/// </summary>
public static class GlassSpringAnimator
{
    private sealed class SpringBinding
    {
        public float PressScale;
        public float HoverScale;
        public bool IsPointerOver;
        public bool IsPressed;
        public float LastTargetScale = 1f;
        public SpringVector3NaturalMotionAnimation? SpringAnim;
    }

    private static readonly ConditionalWeakTable<FrameworkElement, SpringBinding> s_bindings = new();

    /// <summary>
    /// 是否启用物理弹簧交互（由主窗口依据当前是否为液态玻璃模式控制）。
    /// </summary>
    public static bool IsEnabled { get; set; }

    public static void Attach(FrameworkElement? element, float pressScale = 0.94f, float hoverScale = 1.03f)
    {
        if (element == null)
        {
            return;
        }

        if (s_bindings.TryGetValue(element, out var existing))
        {
            existing.PressScale = pressScale;
            existing.HoverScale = hoverScale;
            return;
        }

        var binding = new SpringBinding
        {
            PressScale = pressScale,
            HoverScale = hoverScale
        };
        s_bindings.Add(element, binding);

        element.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler((s, _) =>
        {
            if (s is FrameworkElement fe)
            {
                binding.IsPointerOver = true;
                UpdateTargetScale(fe, binding);
            }
        }), true);

        element.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((s, _) =>
        {
            if (s is FrameworkElement fe)
            {
                binding.IsPointerOver = false;
                binding.IsPressed = false;
                UpdateTargetScale(fe, binding);
            }
        }), true);

        element.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((s, e) =>
        {
            if (s is FrameworkElement fe && e.GetCurrentPoint(fe).Properties.IsLeftButtonPressed)
            {
                binding.IsPressed = true;
                UpdateTargetScale(fe, binding);
            }
        }), true);

        element.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((s, _) =>
        {
            if (s is FrameworkElement fe)
            {
                binding.IsPressed = false;
                UpdateTargetScale(fe, binding);
            }
        }), true);

        element.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((s, _) =>
        {
            if (s is FrameworkElement fe)
            {
                binding.IsPressed = false;
                UpdateTargetScale(fe, binding);
            }
        }), true);
    }

    private static void UpdateTargetScale(FrameworkElement fe, SpringBinding binding)
    {
        if (!fe.IsLoaded || fe.ActualWidth < 8 || fe.ActualHeight < 8)
        {
            return;
        }

        if (!IsEnabled)
        {
            if (MathF.Abs(binding.LastTargetScale - 1f) > 0.001f)
            {
                var vReset = ElementCompositionPreview.GetElementVisual(fe);
                vReset.StopAnimation("Scale");
                vReset.Scale = Vector3.One;
                binding.LastTargetScale = 1f;
                binding.IsPointerOver = false;
                binding.IsPressed = false;
            }
            return;
        }

        float target = binding.IsPressed
            ? binding.PressScale
            : (binding.IsPointerOver ? binding.HoverScale : 1f);

        if (MathF.Abs(binding.LastTargetScale - target) < 0.001f)
        {
            return;
        }

        binding.LastTargetScale = target;
        var visual = ElementCompositionPreview.GetElementVisual(fe);
        visual.CenterPoint = new Vector3((float)fe.ActualWidth * 0.5f, (float)fe.ActualHeight * 0.5f, 0f);

        var anim = binding.SpringAnim ??= visual.Compositor.CreateSpringVector3Animation();
        anim.FinalValue = new Vector3(target, target, 1f);
        anim.DampingRatio = 0.55f;
        anim.Period = TimeSpan.FromMilliseconds(55);
        visual.StartAnimation("Scale", anim);
    }
}
