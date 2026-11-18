using System.Numerics;
using System.Runtime.CompilerServices;
using LiquidGlass.WinUI.Materials;
using LiquidGlass.WinUI.Rendering;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace LiquidGlass.WinUI.Effects;

/// <summary>
/// iOS 26 曲面水滴边缘光学折射渲染器。
/// 在玻璃外沿 11~20px 倒角带内采样 0px 无模糊高饱和实时 Backdrop，叠加向内法向像素拉拽矩阵与外沿微色散，
/// 使背景封面、文字及动态光斑跨越玻璃水滴边缘时产生真实弯曲放大与微棱镜色散。
/// </summary>
public sealed class GlassRefractionRenderer : ILiquidGlassRefractionRenderer
{
    private sealed class AttachedRefractionState
    {
        public ContainerVisual? Root;
        public CompositionBackdropBrush? SharedBackdrop;
        public double LastWidth;
        public double LastHeight;
        public double LastCornerRadius;
        public double LastEdgeWidth;
        public double LastScale;
        public double LastStrength;
        public LiquidGlassMaterialKind LastKind;
    }

    private readonly ConditionalWeakTable<UIElement, AttachedRefractionState> _states = new();

    public void AttachOrUpdate(UIElement hostElement, LiquidGlassMaterial material, double width, double height)
    {
        if (hostElement == null)
        {
            return;
        }

        if (width < 24.0 || height < 20.0
            || material.RefractionStrength <= 0.01
            || LiquidGlassMaterialManager.Current.ShouldUseFallbackSolid)
        {
            Detach(hostElement);
            return;
        }

        var state = _states.GetOrCreateValue(hostElement);
        double cornerR = ResolveEffectiveRadius(material.CornerRadius, width, height);
        double edgeW = Math.Clamp(material.RefractionEdgeWidth, 6.0, Math.Min(width, height) * 0.40);

        if (state.Root != null
            && Math.Abs(state.LastWidth - width) < 0.6
            && Math.Abs(state.LastHeight - height) < 0.6
            && Math.Abs(state.LastCornerRadius - cornerR) < 0.5
            && Math.Abs(state.LastEdgeWidth - edgeW) < 0.25
            && Math.Abs(state.LastScale - material.RefractionScale) < 0.05
            && Math.Abs(state.LastStrength - material.RefractionStrength) < 0.02
            && state.LastKind == material.Kind)
        {
            return;
        }

        var hostVisual = ElementCompositionPreview.GetElementVisual(hostElement);
        var compositor = hostVisual.Compositor;

        if (state.Root == null)
        {
            state.Root = compositor.CreateContainerVisual();
            state.SharedBackdrop = compositor.CreateBackdropBrush();
            ElementCompositionPreview.SetElementChildVisual(hostElement, state.Root);
        }

        PopulateRefractionContainer(compositor, state.Root, state.SharedBackdrop!, material, width, height);

        state.LastWidth = width;
        state.LastHeight = height;
        state.LastCornerRadius = cornerR;
        state.LastEdgeWidth = edgeW;
        state.LastScale = material.RefractionScale;
        state.LastStrength = material.RefractionStrength;
        state.LastKind = material.Kind;
    }

    /// <summary>
    /// 直接向指定 ContainerVisual 填充曲面凸透镜折射切片与微妙 RGB 色散环（避免抢占宿主 ElementChildVisual）。
    /// </summary>
    public void PopulateRefractionContainer(
        Compositor compositor,
        ContainerVisual targetRoot,
        CompositionBackdropBrush backdrop,
        LiquidGlassMaterial material,
        double width,
        double height)
    {
        if (targetRoot == null || width < 20.0 || height < 18.0)
        {
            return;
        }

        var cache = CompositionResourceCache.GetForCompositor(compositor);
        targetRoot.Size = new Vector2((float)width, (float)height);
        targetRoot.Children.RemoveAll();

        float w = (float)width;
        float h = (float)height;
        float r = (float)ResolveEffectiveRadius(material.CornerRadius, width, height);
        float ew = (float)Math.Clamp(material.RefractionEdgeWidth, 6.0, Math.Min(w, h) * 0.40);

        var clipGeom = compositor.CreateRoundedRectangleGeometry();
        clipGeom.Size = new Vector2(w, h);
        clipGeom.CornerRadius = new Vector2(r, r);
        targetRoot.Clip = compositor.CreateGeometricClip(clipGeom);

        // 曲面凸透镜法向拉拽与缩放参数：Dock 强烈拉拽（13.0px）、Folder 与 Button 饱满曲面（9.5px）
        float lensScale = (float)Math.Clamp(material.RefractionScale, 1.45, 2.8);
        float pull = material.Kind == LiquidGlassMaterialKind.Dock ? 13.0f : 9.5f;
        float baseAlpha = (float)Math.Clamp(material.RefractionStrength, 0.35, 0.96);
        bool enablePrism = material.Kind is LiquidGlassMaterialKind.Dock or LiquidGlassMaterialKind.Folder or LiquidGlassMaterialKind.Button;

        float edgeH = Math.Max(0f, w - 2f * r);
        float edgeV = Math.Max(0f, h - 2f * r);

        // 1. 顶边 (x: r..w-r, y: 0..ew)：向玻璃内部（+Y）弯折拉拽像素并纵向放大
        if (edgeH > 2f)
        {
            var topMatrix = Matrix3x2.CreateScale(1.04f, lensScale, new Vector2(edgeH * 0.5f, ew * 0.85f))
                          * Matrix3x2.CreateTranslation(0f, pull);
            AddStripVisual(
                compositor, cache, targetRoot, backdrop,
                pos: new Vector2(r, 0f),
                size: new Vector2(edgeH, ew),
                matrix: topMatrix,
                gradStart: new Vector2(0f, 0f),
                gradEnd: new Vector2(0f, 1f),
                peakAlpha: baseAlpha);

            if (enablePrism)
            {
                AddStripPrismRim(
                    compositor, cache, targetRoot, backdrop,
                    pos: new Vector2(r, 0f),
                    size: new Vector2(edgeH, ew * 0.45f),
                    anchor: new Vector2(edgeH * 0.5f, ew * 0.25f),
                    pullOffset: new Vector2(0f, pull),
                    lensScale: new Vector2(1.04f, lensScale),
                    gradStart: new Vector2(0f, 0f),
                    gradEnd: new Vector2(0f, 1f),
                    normalShiftY: 1f);
            }

            // 2. 底边 (x: r..w-r, y: h-ew..h)：向上向内（-Y）弯折拉拽
            var botMatrix = Matrix3x2.CreateScale(1.04f, lensScale, new Vector2(edgeH * 0.5f, ew * 0.15f))
                          * Matrix3x2.CreateTranslation(0f, -pull);
            AddStripVisual(
                compositor, cache, targetRoot, backdrop,
                pos: new Vector2(r, h - ew),
                size: new Vector2(edgeH, ew),
                matrix: botMatrix,
                gradStart: new Vector2(0f, 1f),
                gradEnd: new Vector2(0f, 0f),
                peakAlpha: baseAlpha * 0.96f);

            if (enablePrism)
            {
                AddStripPrismRim(
                    compositor, cache, targetRoot, backdrop,
                    pos: new Vector2(r, h - ew * 0.45f),
                    size: new Vector2(edgeH, ew * 0.45f),
                    anchor: new Vector2(edgeH * 0.5f, ew * 0.20f),
                    pullOffset: new Vector2(0f, -pull),
                    lensScale: new Vector2(1.04f, lensScale),
                    gradStart: new Vector2(0f, 1f),
                    gradEnd: new Vector2(0f, 0f),
                    normalShiftY: -1f);
            }
        }

        // 3. 左边 & 4. 右边
        if (edgeV > 2f)
        {
            var leftMatrix = Matrix3x2.CreateScale(lensScale, 1.04f, new Vector2(ew * 0.85f, edgeV * 0.5f))
                           * Matrix3x2.CreateTranslation(pull, 0f);
            AddStripVisual(
                compositor, cache, targetRoot, backdrop,
                pos: new Vector2(0f, r),
                size: new Vector2(ew, edgeV),
                matrix: leftMatrix,
                gradStart: new Vector2(0f, 0f),
                gradEnd: new Vector2(1f, 0f),
                peakAlpha: baseAlpha * 0.92f);

            var rightMatrix = Matrix3x2.CreateScale(lensScale, 1.04f, new Vector2(ew * 0.15f, edgeV * 0.5f))
                            * Matrix3x2.CreateTranslation(-pull, 0f);
            AddStripVisual(
                compositor, cache, targetRoot, backdrop,
                pos: new Vector2(w - ew, r),
                size: new Vector2(ew, edgeV),
                matrix: rightMatrix,
                gradStart: new Vector2(1f, 0f),
                gradEnd: new Vector2(0f, 0f),
                peakAlpha: baseAlpha * 0.92f);
        }

        // 5~8. 四个水滴大圆角：对角法向向内拉拽 + 弧带径向折射遮罩
        if (r >= 4f)
        {
            float cScale = 1.0f + (lensScale - 1.0f) * 0.92f;
            float diagPull = pull * 0.68f;

            // 左上圆角
            AddCornerVisual(compositor, cache, targetRoot, backdrop,
                pos: new Vector2(0f, 0f), radius: r, edgeWidth: ew,
                radialCenter: new Vector2(r, r),
                lensPivot: new Vector2(r * 0.35f, r * 0.35f),
                diagPull: new Vector2(+diagPull, +diagPull),
                scale: cScale, peakAlpha: baseAlpha, enablePrism: enablePrism);

            // 右上圆角
            AddCornerVisual(compositor, cache, targetRoot, backdrop,
                pos: new Vector2(w - r, 0f), radius: r, edgeWidth: ew,
                radialCenter: new Vector2(0f, r),
                lensPivot: new Vector2(r * 0.65f, r * 0.35f),
                diagPull: new Vector2(-diagPull, +diagPull),
                scale: cScale, peakAlpha: baseAlpha, enablePrism: enablePrism);

            // 左下圆角
            AddCornerVisual(compositor, cache, targetRoot, backdrop,
                pos: new Vector2(0f, h - r), radius: r, edgeWidth: ew,
                radialCenter: new Vector2(r, 0f),
                lensPivot: new Vector2(r * 0.35f, r * 0.65f),
                diagPull: new Vector2(+diagPull, -diagPull),
                scale: cScale, peakAlpha: baseAlpha * 0.95f, enablePrism: enablePrism);

            // 右下圆角
            AddCornerVisual(compositor, cache, targetRoot, backdrop,
                pos: new Vector2(w - r, h - r), radius: r, edgeWidth: ew,
                radialCenter: new Vector2(0f, 0f),
                lensPivot: new Vector2(r * 0.65f, r * 0.65f),
                diagPull: new Vector2(-diagPull, -diagPull),
                scale: cScale, peakAlpha: baseAlpha * 0.95f, enablePrism: enablePrism);
        }
    }

    public void Detach(UIElement hostElement)
    {
        if (hostElement == null)
        {
            return;
        }

        if (_states.TryGetValue(hostElement, out var state))
        {
            ElementCompositionPreview.SetElementChildVisual(hostElement, null);
            state.Root?.Children.RemoveAll();
            state.Root?.Dispose();
            state.SharedBackdrop?.Dispose();
            state.Root = null;
            state.SharedBackdrop = null;
            _states.Remove(hostElement);
        }
    }

    private static void AddStripVisual(
        Compositor compositor,
        CompositionResourceCache cache,
        ContainerVisual root,
        CompositionBackdropBrush backdrop,
        Vector2 pos,
        Vector2 size,
        Matrix3x2 matrix,
        Vector2 gradStart,
        Vector2 gradEnd,
        float peakAlpha)
    {
        var refractionBrush = cache.CreateRefractionBrush(backdrop, matrix);
        if (refractionBrush == null)
        {
            return;
        }

        var maskGrad = compositor.CreateLinearGradientBrush();
        maskGrad.MappingMode = CompositionMappingMode.Relative;
        maskGrad.StartPoint = gradStart;
        maskGrad.EndPoint = gradEnd;
        byte peak = (byte)Math.Clamp((int)Math.Round(peakAlpha * 255f), 0, 240);
        byte mid = (byte)Math.Clamp((int)Math.Round(peakAlpha * 0.68f * 255f), 0, 210);
        maskGrad.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(0, 255, 255, 255)));
        maskGrad.ColorStops.Add(compositor.CreateColorGradientStop(0.15f, Color.FromArgb(peak, 255, 255, 255)));
        maskGrad.ColorStops.Add(compositor.CreateColorGradientStop(0.58f, Color.FromArgb(mid, 255, 255, 255)));
        maskGrad.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));

        var maskBrush = compositor.CreateMaskBrush();
        maskBrush.Source = refractionBrush;
        maskBrush.Mask = maskGrad;

        var visual = compositor.CreateSpriteVisual();
        visual.Offset = new Vector3(pos.X, pos.Y, 0f);
        visual.Size = size;
        visual.Brush = maskBrush;
        root.Children.InsertAtTop(visual);
    }

    private static void AddStripPrismRim(
        Compositor compositor,
        CompositionResourceCache cache,
        ContainerVisual root,
        CompositionBackdropBrush backdrop,
        Vector2 pos,
        Vector2 size,
        Vector2 anchor,
        Vector2 pullOffset,
        Vector2 lensScale,
        Vector2 gradStart,
        Vector2 gradEnd,
        float normalShiftY)
    {
        var cyanMatrix = Matrix3x2.CreateScale(lensScale.X, lensScale.Y, anchor)
                       * Matrix3x2.CreateTranslation(pullOffset.X, pullOffset.Y + normalShiftY * 2.8f);
        var roseMatrix = Matrix3x2.CreateScale(lensScale.X, lensScale.Y, anchor)
                       * Matrix3x2.CreateTranslation(pullOffset.X, pullOffset.Y - normalShiftY * 2.4f);

        AddSinglePrismSlice(compositor, cache, root, backdrop, pos, size, cyanMatrix, gradStart, gradEnd, true, 48);
        AddSinglePrismSlice(compositor, cache, root, backdrop, pos, size, roseMatrix, gradStart, gradEnd, false, 44);
    }

    private static void AddSinglePrismSlice(
        Compositor compositor,
        CompositionResourceCache cache,
        ContainerVisual root,
        CompositionBackdropBrush backdrop,
        Vector2 pos,
        Vector2 size,
        Matrix3x2 matrix,
        Vector2 gradStart,
        Vector2 gradEnd,
        bool isCoolCyan,
        byte peakAlpha)
    {
        var prismBrush = cache.CreatePrismDispersionBrush(backdrop, matrix, isCoolCyan);
        if (prismBrush == null)
        {
            return;
        }

        var maskGrad = compositor.CreateLinearGradientBrush();
        maskGrad.MappingMode = CompositionMappingMode.Relative;
        maskGrad.StartPoint = gradStart;
        maskGrad.EndPoint = gradEnd;
        maskGrad.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(0, 255, 255, 255)));
        maskGrad.ColorStops.Add(compositor.CreateColorGradientStop(0.28f, Color.FromArgb(peakAlpha, 255, 255, 255)));
        maskGrad.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));

        var maskBrush = compositor.CreateMaskBrush();
        maskBrush.Source = prismBrush;
        maskBrush.Mask = maskGrad;

        var visual = compositor.CreateSpriteVisual();
        visual.Offset = new Vector3(pos.X, pos.Y, 0f);
        visual.Size = size;
        visual.Brush = maskBrush;
        root.Children.InsertAtTop(visual);
    }

    private static void AddCornerVisual(
        Compositor compositor,
        CompositionResourceCache cache,
        ContainerVisual root,
        CompositionBackdropBrush backdrop,
        Vector2 pos,
        float radius,
        float edgeWidth,
        Vector2 radialCenter,
        Vector2 lensPivot,
        Vector2 diagPull,
        float scale,
        float peakAlpha,
        bool enablePrism)
    {
        var transform = Matrix3x2.CreateScale(scale, scale, lensPivot)
                      * Matrix3x2.CreateTranslation(diagPull.X, diagPull.Y);
        var refractionBrush = cache.CreateRefractionBrush(backdrop, transform);
        if (refractionBrush == null)
        {
            return;
        }

        float innerRatio = Math.Clamp((radius - edgeWidth) / Math.Max(1f, radius), 0.04f, 0.90f);
        float midRatio = innerRatio + (1.0f - innerRatio) * 0.45f;
        float peakRatio = innerRatio + (1.0f - innerRatio) * 0.78f;
        byte peak = (byte)Math.Clamp((int)Math.Round(peakAlpha * 255f), 0, 236);
        byte mid = (byte)Math.Clamp((int)Math.Round(peakAlpha * 0.65f * 255f), 0, 195);

        var radialMask = compositor.CreateRadialGradientBrush();
        radialMask.MappingMode = CompositionMappingMode.Absolute;
        radialMask.EllipseCenter = radialCenter;
        radialMask.EllipseRadius = new Vector2(radius, radius);
        radialMask.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(0, 255, 255, 255)));
        radialMask.ColorStops.Add(compositor.CreateColorGradientStop(innerRatio, Color.FromArgb(0, 255, 255, 255)));
        radialMask.ColorStops.Add(compositor.CreateColorGradientStop(midRatio, Color.FromArgb(mid, 255, 255, 255)));
        radialMask.ColorStops.Add(compositor.CreateColorGradientStop(peakRatio, Color.FromArgb(peak, 255, 255, 255)));
        radialMask.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));

        var maskBrush = compositor.CreateMaskBrush();
        maskBrush.Source = refractionBrush;
        maskBrush.Mask = radialMask;

        var visual = compositor.CreateSpriteVisual();
        visual.Offset = new Vector3(pos.X, pos.Y, 0f);
        visual.Size = new Vector2(radius, radius);
        visual.Brush = maskBrush;
        root.Children.InsertAtTop(visual);

        if (enablePrism)
        {
            float sx = diagPull.X >= 0 ? 1f : -1f;
            float sy = diagPull.Y >= 0 ? 1f : -1f;
            var cyanMatrix = Matrix3x2.CreateScale(scale, scale, lensPivot)
                           * Matrix3x2.CreateTranslation(diagPull.X + sx * 2.4f, diagPull.Y + sy * 2.4f);
            var cyanBrush = cache.CreatePrismDispersionBrush(backdrop, cyanMatrix, true);
            if (cyanBrush != null)
            {
                var rimRatio = innerRatio + (1.0f - innerRatio) * 0.56f;
                var prismMask = compositor.CreateRadialGradientBrush();
                prismMask.MappingMode = CompositionMappingMode.Absolute;
                prismMask.EllipseCenter = radialCenter;
                prismMask.EllipseRadius = new Vector2(radius, radius);
                prismMask.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(0, 255, 255, 255)));
                prismMask.ColorStops.Add(compositor.CreateColorGradientStop(rimRatio, Color.FromArgb(0, 255, 255, 255)));
                prismMask.ColorStops.Add(compositor.CreateColorGradientStop(peakRatio, Color.FromArgb(46, 255, 255, 255)));
                prismMask.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));

                var pm = compositor.CreateMaskBrush();
                pm.Source = cyanBrush;
                pm.Mask = prismMask;

                var pv = compositor.CreateSpriteVisual();
                pv.Offset = new Vector3(pos.X, pos.Y, 0f);
                pv.Size = new Vector2(radius, radius);
                pv.Brush = pm;
                root.Children.InsertAtTop(pv);
            }
        }
    }

    private static double ResolveEffectiveRadius(CornerRadius cr, double width, double height)
    {
        double avg = (cr.TopLeft + cr.TopRight + cr.BottomLeft + cr.BottomRight) * 0.25;
        return Math.Clamp(avg, 0.0, Math.Min(width, height) * 0.5);
    }
}
