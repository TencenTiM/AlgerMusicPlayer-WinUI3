using System;
using LiquidGlass.WinUI.Effects;
using LiquidGlass.WinUI.Materials;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;

namespace AlgerMusicPlayer.WinUI.Services.System;

/// <summary>
/// 宿主光学透镜与真物理折射渲染器：
/// 优先通过 Win2DSdfRefractionRenderer 执行 GPU 逐像素圆角 SDF 非线性 DisplacementMap 折射 + R/G/B 三通道色散 + 3D Fresnel/高光/焦散；
/// 仅在场景纹理尚未准备就绪的极短首帧过渡期保持平滑降级。
/// </summary>
public sealed class OpticalLiquidGlassRenderer : IDisposable
{
    public sealed class LensAttachment
    {
        public UIElement? HostElement;
        public Win2DSdfRefractionRenderer.HostVisualState Win2DState = new();
        public double LastWidth;
        public double LastHeight;
        public double LastRadius;
        public bool LastIsLight;
        public LiquidGlassPreset LastPreset;
        internal Action<UIElement?>? OnDetach;

        public void DetachAndDispose(UIElement? hostElement)
        {
            OnDetach?.Invoke(hostElement);
        }
    }

    private readonly Win2DSdfRefractionRenderer _win2DRenderer = new();

    public CanvasDevice SharedCanvasDevice => _win2DRenderer.GetOrCreateDevice();

    public void AttachOrUpdateLensVisual(
        UIElement hostElement,
        LensAttachment attachment,
        double cornerRadius,
        bool isLight,
        double intensity,
        double width,
        double height,
        LiquidGlassPreset preset = LiquidGlassPreset.FrostedFolder,
        CanvasBitmap? sceneBitmap = null,
        float winX = 0f,
        float winY = 0f)
    {
        if (hostElement == null || attachment == null)
        {
            return;
        }

        if (width < 20.0 || height < 18.0 || LiquidGlassMaterialManager.Current.ShouldUseFallbackSolid)
        {
            Detach(hostElement, attachment);
            return;
        }

        if (sceneBitmap == null)
        {
            attachment.HostElement = hostElement;
            attachment.LastWidth = width;
            attachment.LastHeight = height;
            attachment.LastRadius = cornerRadius;
            attachment.LastIsLight = isLight;
            attachment.LastPreset = preset;
            attachment.OnDetach = h => Detach(h ?? hostElement, attachment);
            return;
        }

        var kind = LiquidGlassCompositionBrush.MapKind(preset);
        var material = LiquidGlassMaterialPreset.GetPreset(kind, isLight, new CornerRadius(cornerRadius));

        _win2DRenderer.RenderRefractedGlass(
            hostElement,
            attachment.Win2DState,
            sceneBitmap,
            winX,
            winY,
            width,
            height,
            cornerRadius,
            isLight,
            material,
            intensity);

        attachment.HostElement = hostElement;
        attachment.LastWidth = width;
        attachment.LastHeight = height;
        attachment.LastRadius = cornerRadius;
        attachment.LastIsLight = isLight;
        attachment.LastPreset = preset;
        attachment.OnDetach = h => Detach(h ?? hostElement, attachment);
    }

    public void UpdatePointerGlow(LensAttachment? attachment, float localX, float localY, bool isPointerActive)
    {
        _win2DRenderer.UpdatePointerGlow(attachment?.Win2DState, localX, localY, isPointerActive);
    }

    public void Detach(UIElement? hostElement, LensAttachment? attachment = null)
    {
        if (attachment != null)
        {
            _win2DRenderer.Detach(hostElement ?? attachment.HostElement, attachment.Win2DState);
            attachment.HostElement = null;
        }
    }

    public void Dispose()
    {
        _win2DRenderer.Dispose();
    }
}
