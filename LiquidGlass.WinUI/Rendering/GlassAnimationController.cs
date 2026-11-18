using System.Numerics;
using System.Runtime.CompilerServices;
using LiquidGlass.WinUI.Materials;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

namespace LiquidGlass.WinUI.Rendering;

/// <summary>
/// 基于 Microsoft.UI.Composition 的高性能 GPU 交互动效控制器。
/// 负责 Dock 按钮灵动微弹（1.08x / 0.93x）、Folder 悬停边缘高光微提以及弹窗/文件夹缩放淡入动画。
/// </summary>
public static class GlassAnimationController
{
    private sealed class InteractionState
    {
        public UIElement? Target;
        public FrameworkElement? HighlightLayer;
        public LiquidGlassMaterialKind Kind;
        public bool IsPointerOver;
        public bool IsPressed;
        public bool IsHooked;
        public PointerEventHandler? EnteredHandler;
        public PointerEventHandler? ExitedHandler;
        public PointerEventHandler? PressedHandler;
        public PointerEventHandler? ReleasedHandler;
        public PointerEventHandler? CaptureLostHandler;
    }

    private static readonly ConditionalWeakTable<UIElement, InteractionState> States = new();

    /// <summary>
    /// 为控件挂载原生 Composition 悬停/按压微弹动效与边缘高光响应。重复调用可安全刷新参数。
    /// </summary>
    public static void AttachInteractions(
        UIElement target,
        LiquidGlassMaterialKind kind,
        FrameworkElement? highlightLayer = null)
    {
        if (target == null)
        {
            return;
        }

        var state = States.GetOrCreateValue(target);
        state.Target = target;
        state.Kind = kind;
        state.HighlightLayer = highlightLayer;

        if (state.IsHooked)
        {
            return;
        }

        state.IsHooked = true;
        state.EnteredHandler = (_, _) =>
        {
            state.IsPointerOver = true;
            UpdateVisualState(state);
        };
        state.ExitedHandler = (_, _) =>
        {
            state.IsPointerOver = false;
            state.IsPressed = false;
            UpdateVisualState(state);
        };
        state.PressedHandler = (_, _) =>
        {
            state.IsPressed = true;
            UpdateVisualState(state);
        };
        state.ReleasedHandler = (_, _) =>
        {
            state.IsPressed = false;
            UpdateVisualState(state);
        };
        state.CaptureLostHandler = (_, _) =>
        {
            state.IsPressed = false;
            state.IsPointerOver = false;
            UpdateVisualState(state);
        };

        target.AddHandler(UIElement.PointerEnteredEvent, state.EnteredHandler, true);
        target.AddHandler(UIElement.PointerExitedEvent, state.ExitedHandler, true);
        target.AddHandler(UIElement.PointerPressedEvent, state.PressedHandler, true);
        target.AddHandler(UIElement.PointerReleasedEvent, state.ReleasedHandler, true);
        target.AddHandler(UIElement.PointerCaptureLostEvent, state.CaptureLostHandler, true);

        if (target is FrameworkElement fe)
        {
            fe.SizeChanged += OnTargetSizeChanged;
        }
    }

    /// <summary>
    /// 卸载交互动效并归位视觉缩放。
    /// </summary>
    public static void DetachInteractions(UIElement target)
    {
        if (target == null || !States.TryGetValue(target, out var state))
        {
            return;
        }

        if (state.IsHooked)
        {
            if (state.EnteredHandler != null) target.RemoveHandler(UIElement.PointerEnteredEvent, state.EnteredHandler);
            if (state.ExitedHandler != null) target.RemoveHandler(UIElement.PointerExitedEvent, state.ExitedHandler);
            if (state.PressedHandler != null) target.RemoveHandler(UIElement.PointerPressedEvent, state.PressedHandler);
            if (state.ReleasedHandler != null) target.RemoveHandler(UIElement.PointerReleasedEvent, state.ReleasedHandler);
            if (state.CaptureLostHandler != null) target.RemoveHandler(UIElement.PointerCaptureLostEvent, state.CaptureLostHandler);
            if (target is FrameworkElement fe)
            {
                fe.SizeChanged -= OnTargetSizeChanged;
            }
            state.IsHooked = false;
        }

        state.IsPointerOver = false;
        state.IsPressed = false;

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(target);
            visual.Scale = Vector3.One;
        }
        catch
        {
        }

        if (state.HighlightLayer != null)
        {
            try
            {
                var hVisual = ElementCompositionPreview.GetElementVisual(state.HighlightLayer);
                hVisual.Opacity = 1.0f;
            }
            catch
            {
            }
        }

        States.Remove(target);
    }

    /// <summary>
    /// 执行 Folder 打开/展开 Composition 缩放与渐显动画（从微缩态弹性过渡到 1.0）。
    /// </summary>
    public static void PlayFolderEnterAnimation(UIElement target)
    {
        if (target == null)
        {
            return;
        }

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(target);
            var compositor = visual.Compositor;
            SyncCenterPoint(target, visual);

            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1.0f), new Vector2(0.3f, 1.0f));

            var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.InsertKeyFrame(0.0f, new Vector3(0.95f, 0.95f, 1.0f));
            scaleAnim.InsertKeyFrame(1.0f, Vector3.One, ease);
            scaleAnim.Duration = TimeSpan.FromMilliseconds(260);

            var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(0.0f, 0.0f);
            opacityAnim.InsertKeyFrame(1.0f, 1.0f, ease);
            opacityAnim.Duration = TimeSpan.FromMilliseconds(220);

            visual.StartAnimation("Scale", scaleAnim);
            visual.StartAnimation("Opacity", opacityAnim);
        }
        catch
        {
        }
    }

    private static void OnTargetSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is UIElement target)
        {
            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(target);
                visual.CenterPoint = new Vector3((float)(e.NewSize.Width * 0.5), (float)(e.NewSize.Height * 0.5), 0f);
            }
            catch
            {
            }
        }
    }

    private static void SyncCenterPoint(UIElement target, Visual visual)
    {
        if (target is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
        {
            visual.CenterPoint = new Vector3((float)(fe.ActualWidth * 0.5), (float)(fe.ActualHeight * 0.5), 0f);
        }
    }

    private static void UpdateVisualState(InteractionState state)
    {
        if (state.Target == null)
        {
            return;
        }

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(state.Target);
            var compositor = visual.Compositor;
            SyncCenterPoint(state.Target, visual);

            float targetScale;
            int durationMs;
            if (state.Kind is LiquidGlassMaterialKind.Button or LiquidGlassMaterialKind.Dock)
            {
                // Dock / 胶囊按钮采用清晰的微弹缩放（1.08 / 0.93），大 Dock 条自身收敛幅度防止溢出
                bool isLargeDock = state.Target is FrameworkElement dfe && dfe.ActualWidth > 260;
                float hoverS = isLargeDock ? 1.006f : 1.08f;
                float pressS = isLargeDock ? 0.992f : 0.93f;
                targetScale = state.IsPressed ? pressS : (state.IsPointerOver ? hoverS : 1.0f);
                durationMs = state.IsPressed ? 110 : 190;
            }
            else
            {
                // Folder / Panel 悬停以轻微抬升和高光增强为主，避免大幅变形
                targetScale = state.IsPressed ? 0.988f : (state.IsPointerOver ? 1.012f : 1.0f);
                durationMs = state.IsPressed ? 120 : 210;
            }

            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.2f, 1.0f));
            var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.InsertKeyFrame(1.0f, new Vector3(targetScale, targetScale, 1.0f), ease);
            scaleAnim.Duration = TimeSpan.FromMilliseconds(durationMs);
            visual.StartAnimation("Scale", scaleAnim);

            if (state.HighlightLayer != null)
            {
                var hVisual = ElementCompositionPreview.GetElementVisual(state.HighlightLayer);
                float hOpacity = state.IsPressed ? 0.85f : (state.IsPointerOver ? 1.38f : 1.0f);
                var opAnim = compositor.CreateScalarKeyFrameAnimation();
                opAnim.InsertKeyFrame(1.0f, hOpacity, ease);
                opAnim.Duration = TimeSpan.FromMilliseconds(180);
                hVisual.StartAnimation("Opacity", opAnim);
            }
        }
        catch
        {
        }
    }
}
