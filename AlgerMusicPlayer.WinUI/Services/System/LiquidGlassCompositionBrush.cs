using System;
using System.Diagnostics;
using LiquidGlass.WinUI.Materials;
using LiquidGlass.WinUI.Rendering;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AlgerMusicPlayer.WinUI.Services.System;

/// <summary>
/// Liquid Glass 材质预设分类，映射到 LiquidGlass.WinUI 核心引擎预设体系。
/// </summary>
public enum LiquidGlassPreset
{
    /// <summary>
    /// 内容卡片 / 文件夹面板（22px 强高斯模糊 + 中性微透底色 + 柔和边缘反光）。
    /// </summary>
    FrostedFolder = 0,

    /// <summary>
    /// 灵动胶囊 Dock / 悬浮播放栏 / 顶部搜索胶囊（6px 轻量模糊保留底色 + 强边缘折射 + 清晰上下高光棱线）。
    /// </summary>
    ClearDock = 1,

    /// <summary>
    /// 次级胶囊按钮与图标按钮（10px 中等模糊 + 清晰外缘高光）。
    /// </summary>
    GlassButton = 2,

    /// <summary>
    /// 侧边导航栏与大面积玻璃面板（20px 稳定磨砂以保障左侧菜单可读性）。
    /// </summary>
    GlassPanel = 3
}

/// <summary>
/// 宿主应用与 LiquidGlass.WinUI 引擎的 Composition 光学画刷桥接类。
/// 彻底移除会触发 DWM CreateEffectFactory 异常的双路 DAG 与 EffectOptimization 属性，
/// 确保所有前端卡片、Dock 胶囊、导航栏均真实启用硬件级 Backdrop 高斯模糊与中性着色合成。
/// </summary>
public sealed class LiquidGlassCompositionBrush : XamlCompositionBrushBase
{
    private bool _isConnected;

    public Color AccentColor { get; private set; }
    public bool IsLight { get; private set; }
    public double Intensity { get; private set; }
    public LiquidGlassPreset Preset { get; private set; }
    public float? CustomBlurAmount { get; private set; }
    public float? CustomTintOpacity { get; private set; }
    public Color? CustomTintColor { get; private set; }

    /// <summary>
    /// 最近一次诊断信息（指示当前线程 Compositor 是否完整运行于 GPU 模糊管线）。
    /// </summary>
    public static string LastPipelineDiagnostic { get; private set; } = "未连接";

    public static bool IsHardwareBlurSupported { get; private set; }

    public LiquidGlassCompositionBrush(
        Color accentColor,
        bool isLight,
        double intensity = 1.0,
        LiquidGlassPreset preset = LiquidGlassPreset.FrostedFolder,
        float? customBlurAmount = null,
        float? customCenterBlurWeight = null,
        float? customTintOpacity = null,
        Color? customTintColor = null)
    {
        AccentColor = accentColor;
        IsLight = isLight;
        Intensity = Math.Clamp(intensity, 0.5, 1.5);
        Preset = preset;
        CustomBlurAmount = customBlurAmount;
        CustomTintOpacity = customTintOpacity;
        CustomTintColor = customTintColor;
        _ = customCenterBlurWeight;

        FallbackColor = ResolveMaterial().FallbackColor;
    }

    /// <summary>
    /// 将局部参数变更实时写入已连接的 GPU CompositionEffectBrush，避免重建 Visual 导致闪烁。
    /// </summary>
    public void UpdateOpticalParameters(Color accentColor, bool isLight, double intensity, LiquidGlassPreset preset, float? customBlurAmount = null, float? customTintOpacity = null, Color? customTintColor = null)
    {
        AccentColor = accentColor;
        IsLight = isLight;
        Intensity = Math.Clamp(intensity, 0.5, 1.5);
        Preset = preset;
        CustomBlurAmount = customBlurAmount;
        CustomTintOpacity = customTintOpacity;
        CustomTintColor = customTintColor;

        var material = ResolveMaterial();
        FallbackColor = material.FallbackColor;

        if (!_isConnected)
        {
            return;
        }

        if (LiquidGlassMaterialManager.Current.ShouldUseFallbackSolid)
        {
            RebuildBrush();
            return;
        }

        if (CompositionBrush is CompositionEffectBrush effectBrush)
        {
            CompositionResourceCache.UpdateBackdropBrushProperties(
                effectBrush,
                material.BlurAmount,
                material.Saturation,
                material.TintColor,
                material.TintOpacity,
                includeTint: true);
        }
        else if (CompositionBrush is CompositionColorBrush colorBrush)
        {
            colorBrush.Color = material.FallbackColor;
        }
    }

    protected override void OnConnected()
    {
        _isConnected = true;
        RebuildBrush();
        LiquidGlassMaterialManager.Current.MaterialEnvironmentChanged += OnMaterialEnvironmentChanged;
    }

    protected override void OnDisconnected()
    {
        _isConnected = false;
        LiquidGlassMaterialManager.Current.MaterialEnvironmentChanged -= OnMaterialEnvironmentChanged;

        if (CompositionBrush != null)
        {
            CompositionBrush.Dispose();
            CompositionBrush = null;
        }
    }

    private void OnMaterialEnvironmentChanged(object? sender, EventArgs e)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (_isConnected)
            {
                RebuildBrush();
            }
        });
    }

    private void RebuildBrush()
    {
        if (!_isConnected)
        {
            return;
        }

        if (CompositionBrush != null)
        {
            CompositionBrush.Dispose();
            CompositionBrush = null;
        }

        var compositor = CompositionTarget.GetCompositorForCurrentThread();
        var material = ResolveMaterial();
        FallbackColor = material.FallbackColor;

        if (LiquidGlassMaterialManager.Current.ShouldUseFallbackSolid)
        {
            LastPipelineDiagnostic = "System Fallback Solid";
            CompositionBrush = compositor.CreateColorBrush(material.FallbackColor);
            return;
        }

        var cache = CompositionResourceCache.GetForCompositor(compositor);
        var created = cache.CreateBackdropBrush(
            material.BlurAmount,
            material.Saturation,
            material.TintColor,
            material.TintOpacity,
            includeTint: true,
            material.FallbackColor);

        IsHardwareBlurSupported = cache.IsHardwareBlurSupported;
        LastPipelineDiagnostic = cache.LastDiagnostic;
        Debug.WriteLine($"[LiquidGlassCompositionBrush] Preset={Preset}, Light={IsLight}, Blur={material.BlurAmount:F1}, TintOp={material.TintOpacity:F2}, Diag={LastPipelineDiagnostic}");
        CompositionBrush = created;
    }

    internal LiquidGlassMaterial ResolveMaterial()
    {
        var kind = MapKind(Preset);
        var material = LiquidGlassMaterialPreset.GetPreset(kind, IsLight);

        // 严禁把 AccentColor 强行混入玻璃基底，彻底杜绝绿框/绿底偏色；仅在显式传入 CustomTintColor 时替换（例如半实心浮层 #D00B2B36）
        if (CustomTintColor.HasValue)
        {
            material.TintColor = CustomTintColor.Value;
        }

        if (CustomBlurAmount.HasValue)
        {
            material.BlurAmount = Math.Clamp(CustomBlurAmount.Value, 2.0, 48.0);
        }

        // 若未显式覆盖 CustomTintOpacity，则直接保留预设自身的精确玻璃着色浓度（#30FFFFFF / #1AFFFFFF）
        if (CustomTintOpacity.HasValue)
        {
            material.TintOpacity = Math.Clamp(CustomTintOpacity.Value, 0.04, 0.92);
        }
        return material;
    }

    internal static LiquidGlassMaterialKind MapKind(LiquidGlassPreset preset)
    {
        return preset switch
        {
            LiquidGlassPreset.ClearDock => LiquidGlassMaterialKind.Dock,
            LiquidGlassPreset.GlassButton => LiquidGlassMaterialKind.Button,
            LiquidGlassPreset.GlassPanel => LiquidGlassMaterialKind.Panel,
            _ => LiquidGlassMaterialKind.Folder
        };
    }
}
