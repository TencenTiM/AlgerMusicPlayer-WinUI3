using Windows.UI;

namespace AlgerMusicPlayer.WinUI.Controls;

/// <summary>
/// 液态玻璃统一光学参数令牌（对照 Alger 液态玻璃 UI 开发文档与 6 步重构规范）。
/// </summary>
public static class GlassTokens
{
    public const double BlurLarge = 28.0;
    public const double BlurSidebar = 26.0;
    public const double BlurCard = 20.0;
    public const double BlurSearch = 14.0;
    public const double BlurDrawer = 36.0;

    public const float Saturation = 1.6f;

    public static readonly Color TintLarge = Color.FromArgb(0x2E, 255, 255, 255);
    public static readonly Color TintSidebar = Color.FromArgb(0x22, 255, 255, 255);
    public static readonly Color TintCard = Color.FromArgb(0x1A, 255, 255, 255);
    public static readonly Color TintSearch = Color.FromArgb(0x18, 255, 255, 255);
    public static readonly Color TintDrawer = Color.FromArgb(0xE0, 0x0B, 0x2A, 0x36);

    // 降级规则：Mica / 亚克力 / 视频壁纸模式下不低于 #B00B2A36
    public static readonly Color DegradedTintDark = Color.FromArgb(0xB0, 0x0B, 0x2A, 0x36);
    public static readonly Color DegradedTintLight = Color.FromArgb(0xD8, 0xEC, 0xF4, 0xF9);

    public static readonly Color Accent = Color.FromArgb(0xFF, 0x3D, 0xCB, 0xF5);

    // LiquidGlassWinUI 原生 DWM 着色器默认光学参数（对齐仓库 Preset_Default）
    public const double DefaultRefFactor = 3.37;
    public const double DefaultRefDispersion = 3.50;
    public const double DefaultDispersionRange = 0.39;
    public const double DefaultRefFresnelRange = 57.84;
    public const double DefaultRefFresnelHardness = 0.0;
    public const double DefaultRefFresnelFactor = 22.0;
    public const double DefaultGlareAngle = -135.0;
    public const double DefaultGlareConvergence = 100.0;
    public const double DefaultGlareFactor = 74.0;
    public const double DefaultGlareOppositeFactor = 80.0;
    public const double DefaultGlareHardness = 13.0;
    public const double DefaultGlareRange = 36.13;
}
