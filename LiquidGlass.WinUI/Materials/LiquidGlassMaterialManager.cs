using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace LiquidGlass.WinUI.Materials;

/// <summary>
/// Liquid Glass 全局材质管理器：负责结合 Light/Dark、系统高对比度及省电/透明效果开关解析最终渲染参数。
/// </summary>
public sealed class LiquidGlassMaterialManager
{
    private static readonly Lazy<LiquidGlassMaterialManager> LazyInstance =
        new(() => new LiquidGlassMaterialManager());

    public static LiquidGlassMaterialManager Current => LazyInstance.Value;

    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibilitySettings = new();

    private LiquidGlassMaterialManager()
    {
        try
        {
            _uiSettings.AdvancedEffectsEnabledChanged += (_, _) => MaterialEnvironmentChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 非交互式宿主环境可能不支持系统设置事件订阅
        }

        try
        {
            _accessibilitySettings.HighContrastChanged += (_, _) => MaterialEnvironmentChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 当系统透明特效开关或高对比度状态变化时触发。
    /// </summary>
    public event EventHandler? MaterialEnvironmentChanged;

    /// <summary>
    /// 系统是否开启高级透明与模糊效果。
    /// </summary>
    public bool AreAdvancedEffectsEnabled
    {
        get
        {
            try
            {
                return _uiSettings.AdvancedEffectsEnabled;
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>
    /// 系统是否启用高对比度模式。
    /// </summary>
    public bool IsHighContrast
    {
        get
        {
            try
            {
                return _accessibilitySettings.HighContrast;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 是否应回退到无背景模糊的纯色模式（省电/关闭透明/高对比度）。
    /// </summary>
    public bool ShouldUseFallbackSolid => !AreAdvancedEffectsEnabled || IsHighContrast;

    /// <summary>
    /// 根据材质类型、控件当前主题以及可选覆盖对象解析最终可渲染材质。
    /// </summary>
    public LiquidGlassMaterial Resolve(
        LiquidGlassMaterialKind kind,
        ElementTheme theme,
        LiquidGlassMaterial? customOverride = null,
        CornerRadius? cornerRadiusOverride = null)
    {
        bool isLight = theme == ElementTheme.Light;
        var resolved = customOverride != null
            ? customOverride.Clone()
            : LiquidGlassMaterialPreset.GetPreset(kind, isLight, cornerRadiusOverride);

        if (cornerRadiusOverride.HasValue)
        {
            resolved.CornerRadius = cornerRadiusOverride.Value;
        }

        if (customOverride != null && theme != ElementTheme.Default)
        {
            resolved.IsLight = isLight;
        }

        if (ShouldUseFallbackSolid)
        {
            resolved.BlurAmount = 0;
            resolved.RefractionStrength = 0;
            resolved.TintColor = resolved.FallbackColor;
            resolved.TintOpacity = Math.Max(0.86, resolved.TintOpacity);
        }

        return resolved;
    }
}
