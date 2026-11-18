using Microsoft.UI.Xaml;
using Windows.UI;

namespace LiquidGlass.WinUI.Materials;

/// <summary>
/// Liquid Glass 中性 iOS 风格预设矩阵（严格区分 Dock 胶囊与 Folder 大圆角高模糊卡片）。
/// 遵循中性微透白/炭黑基调，避免默认蓝紫染色与厚重发光边框。
/// </summary>
public static class LiquidGlassMaterialPreset
{
    public static LiquidGlassMaterial CreateDarkDock(CornerRadius? cornerRadius = null)
    {
        return new LiquidGlassMaterial
        {
            Kind = LiquidGlassMaterialKind.Dock,
            IsLight = false,
            BlurAmount = 24.0,
            Saturation = 1.6,
            TintColor = Color.FromArgb(255, 255, 255, 255),
            TintOpacity = 0.180,
            FallbackColor = Color.FromArgb(228, 20, 24, 34),
            ReflectionOpacity = 0.12,
            EdgeHighlightOpacity = 0.50,
            InnerShadowOpacity = 0.14,
            RefractionStrength = 0.94,
            RefractionScale = 2.55,
            RefractionEdgeWidth = 22.0,
            CornerRadius = cornerRadius ?? new CornerRadius(44),
            BorderThickness = 1.0,
            ElevationShadowOpacity = 0.25
        };
    }

    public static LiquidGlassMaterial CreateLightDock(CornerRadius? cornerRadius = null)
    {
        return new LiquidGlassMaterial
        {
            Kind = LiquidGlassMaterialKind.Dock,
            IsLight = true,
            BlurAmount = 24.0,
            Saturation = 1.6,
            TintColor = Color.FromArgb(255, 255, 255, 255),
            TintOpacity = 0.50,
            FallbackColor = Color.FromArgb(232, 244, 247, 250),
            ReflectionOpacity = 0.18,
            EdgeHighlightOpacity = 0.65,
            InnerShadowOpacity = 0.10,
            RefractionStrength = 0.92,
            RefractionScale = 2.50,
            RefractionEdgeWidth = 22.0,
            CornerRadius = cornerRadius ?? new CornerRadius(44),
            BorderThickness = 1.0,
            ElevationShadowOpacity = 0.18
        };
    }

    public static LiquidGlassMaterial CreateDarkFolder(CornerRadius? cornerRadius = null)
    {
        return new LiquidGlassMaterial
        {
            Kind = LiquidGlassMaterialKind.Folder,
            IsLight = false,
            BlurAmount = 20.0,
            Saturation = 1.6,
            TintColor = Color.FromArgb(255, 255, 255, 255),
            TintOpacity = 0.102,
            FallbackColor = Color.FromArgb(218, 24, 28, 36),
            ReflectionOpacity = 0.10,
            EdgeHighlightOpacity = 0.45,
            InnerShadowOpacity = 0.12,
            RefractionStrength = 0.72,
            RefractionScale = 1.95,
            RefractionEdgeWidth = 12.0,
            CornerRadius = cornerRadius ?? new CornerRadius(20),
            BorderThickness = 1.0,
            ElevationShadowOpacity = 0.20
        };
    }

    public static LiquidGlassMaterial CreateLightFolder(CornerRadius? cornerRadius = null)
    {
        return new LiquidGlassMaterial
        {
            Kind = LiquidGlassMaterialKind.Folder,
            IsLight = true,
            BlurAmount = 20.0,
            Saturation = 1.6,
            TintColor = Color.FromArgb(255, 255, 255, 255),
            TintOpacity = 0.46,
            FallbackColor = Color.FromArgb(224, 246, 249, 252),
            ReflectionOpacity = 0.16,
            EdgeHighlightOpacity = 0.60,
            InnerShadowOpacity = 0.08,
            RefractionStrength = 0.70,
            RefractionScale = 1.90,
            RefractionEdgeWidth = 12.0,
            CornerRadius = cornerRadius ?? new CornerRadius(20),
            BorderThickness = 1.0,
            ElevationShadowOpacity = 0.14
        };
    }

    public static LiquidGlassMaterial CreatePanel(bool isLight, CornerRadius? cornerRadius = null)
    {
        return new LiquidGlassMaterial
        {
            Kind = LiquidGlassMaterialKind.Panel,
            IsLight = isLight,
            BlurAmount = 26.0,
            Saturation = 1.6,
            TintColor = Color.FromArgb(255, 255, 255, 255),
            TintOpacity = isLight ? 0.52 : 0.133,
            FallbackColor = isLight
                ? Color.FromArgb(232, 245, 248, 252)
                : Color.FromArgb(228, 20, 24, 32),
            ReflectionOpacity = 0.12,
            EdgeHighlightOpacity = isLight ? 0.50 : 0.40,
            InnerShadowOpacity = 0.08,
            RefractionStrength = 0.68,
            RefractionScale = 1.85,
            RefractionEdgeWidth = 12.0,
            CornerRadius = cornerRadius ?? new CornerRadius(0),
            BorderThickness = 1.0,
            ElevationShadowOpacity = 0.12
        };
    }

    public static LiquidGlassMaterial CreateButton(bool isLight, CornerRadius? cornerRadius = null)
    {
        return new LiquidGlassMaterial
        {
            Kind = LiquidGlassMaterialKind.Button,
            IsLight = isLight,
            BlurAmount = 14.0,
            Saturation = 1.6,
            TintColor = Color.FromArgb(255, 255, 255, 255),
            TintOpacity = isLight ? 0.45 : 0.102,
            FallbackColor = isLight
                ? Color.FromArgb(224, 246, 249, 253)
                : Color.FromArgb(216, 30, 36, 46),
            ReflectionOpacity = 0.14,
            EdgeHighlightOpacity = isLight ? 0.60 : 0.50,
            InnerShadowOpacity = 0.10,
            RefractionStrength = 0.72,
            RefractionScale = 2.0,
            RefractionEdgeWidth = 10.0,
            CornerRadius = cornerRadius ?? new CornerRadius(18),
            BorderThickness = 1.0,
            ElevationShadowOpacity = 0.14
        };
    }

    public static LiquidGlassMaterial CreatePopover(bool isLight, CornerRadius? cornerRadius = null)
    {
        return new LiquidGlassMaterial
        {
            Kind = LiquidGlassMaterialKind.Popover,
            IsLight = isLight,
            BlurAmount = 24.0,
            Saturation = 1.04,
            TintColor = isLight
                ? Color.FromArgb(255, 248, 250, 253)
                : Color.FromArgb(255, 20, 24, 30),
            TintOpacity = isLight ? 0.56 : 0.48,
            FallbackColor = isLight
                ? Color.FromArgb(242, 248, 250, 252)
                : Color.FromArgb(238, 24, 28, 34),
            ReflectionOpacity = 0.10,
            EdgeHighlightOpacity = isLight ? 0.24 : 0.18,
            InnerShadowOpacity = 0.06,
            RefractionStrength = 0.08,
            RefractionScale = 3.0,
            RefractionEdgeWidth = 7.0,
            CornerRadius = cornerRadius ?? new CornerRadius(16),
            BorderThickness = 0.9,
            ElevationShadowOpacity = 0.28
        };
    }

    public static LiquidGlassMaterial GetPreset(
        LiquidGlassMaterialKind kind,
        bool isLight,
        CornerRadius? cornerRadius = null)
    {
        return kind switch
        {
            LiquidGlassMaterialKind.Dock => isLight
                ? CreateLightDock(cornerRadius)
                : CreateDarkDock(cornerRadius),
            LiquidGlassMaterialKind.Folder => isLight
                ? CreateLightFolder(cornerRadius)
                : CreateDarkFolder(cornerRadius),
            LiquidGlassMaterialKind.Button => CreateButton(isLight, cornerRadius),
            LiquidGlassMaterialKind.Panel => CreatePanel(isLight, cornerRadius),
            LiquidGlassMaterialKind.Popover => CreatePopover(isLight, cornerRadius),
            _ => isLight
                ? CreateLightFolder(cornerRadius)
                : CreateDarkFolder(cornerRadius)
        };
    }
}
