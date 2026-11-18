using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlgerMusicPlayer.WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace AlgerMusicPlayer.WinUI.Services.System;

/// <summary>
/// 主题中心与创意市场核心服务（负责主题管理、双色渐变画刷染色、封面多色流光提取、壁纸模糊、在线主题仓库拉取与口令分享码互传）
/// </summary>
public class ThemeService
{
    public const string ShareCodePrefix = "ALGER-THEME://";
    public const string OfficialMarketFeedUrl = "https://raw.githubusercontent.com/TencenTiM/AlgerMusicPlayer-Themes/main/index.json";

    private static readonly string[] GitHubMirrorPrefixes =
    [
        "",
        "https://ghfast.top/",
        "https://gh-proxy.com/",
        "https://ghproxy.net/"
    ];

    private readonly ConfigService _config;
    private readonly string _wallpaperDir;
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly HttpClient MarketHttp = CreateMarketHttpClient();

    private static HttpClient CreateMarketHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AlgerMusicPlayer-WinUI3-ThemeMarket");
        client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
        return client;
    }

    private static readonly ConcurrentDictionary<string, byte[]> PreviewBytesCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 最近一次从在线主题仓库/外部订阅源拉取成功的主题数量
    /// </summary>
    public int LastRemoteFeedCount { get; private set; }

    /// <summary>
    /// 最近一次强刷主题仓库时的错误说明（成功则为空）
    /// </summary>
    public string? LastRemoteRefreshError { get; private set; }

    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions CompactShareJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    // 最近一次生成的模糊壁纸缓存，避免同参数重复解码
    private string _cachedBlurKey = string.Empty;
    private byte[]? _cachedBlurBgraPixels;
    private int _cachedBlurWidth;
    private int _cachedBlurHeight;

    // 最近一次生成的壁纸高精度局部色彩/亮度采样网格缓存（96x54 双线性插值）
    private string _cachedWallGridKey = string.Empty;
    private WallpaperColorGrid? _cachedWallGrid;

    // 远程订阅源主题内存缓存
    private List<MarketplaceThemeItem>? _cachedRemoteMarketItems;

    /// <summary>
    /// 正在实时预览中的临时主题（为空则使用已激活的持久化主题）
    /// </summary>
    public CustomThemeProfile? PreviewProfile { get; private set; }

    /// <summary>
    /// 当激活主题切换或 DIY 参数实时变更时触发
    /// </summary>
    public event Action<CustomThemeProfile>? ThemeApplied;

    public ThemeService(ConfigService config)
    {
        _config = config;
        _wallpaperDir = Path.Combine(_config.AppDataDirectory, "Themes", "Wallpapers");
        Directory.CreateDirectory(_wallpaperDir);

        // 一次性将历史自定义主题默认关闭动态反色字体，统一为固定字色（用户仍可随时在设置中手动重新打开）
        var tc = _config.Settings.ThemeCenter;
        if (!tc.AdaptiveTextDefaultOffMigrated)
        {
            tc.AdaptiveTextDefaultOffMigrated = true;
            foreach (var theme in tc.CustomThemes)
            {
                theme.EnableAdaptiveWallpaperText = false;
            }
            _config.SaveSettingsSync();
        }
    }

    public string WallpaperDirectory => _wallpaperDir;

    /// <summary>
    /// 获取所有可用主题（官方内置预设 + 用户自制/市场已安装主题）
    /// </summary>
    public List<CustomThemeProfile> GetAllThemes()
    {
        var list = CustomThemeProfile.GetBuiltInPresets();
        if (_config.Settings.ThemeCenter.CustomThemes.Count > 0)
        {
            list.AddRange(_config.Settings.ThemeCenter.CustomThemes);
        }
        return list;
    }

    /// <summary>
    /// 获取当前正在生效的主题（若有未退出的实时预览则优先返回预览）
    /// </summary>
    public CustomThemeProfile GetEffectiveTheme()
    {
        if (PreviewProfile != null) return PreviewProfile;
        return GetSavedActiveTheme();
    }

    /// <summary>
    /// 获取已持久化选中的主题（若用户在基础设置切换了全局日夜模式覆盖，则在克隆副本上叠加 BaseMode，不污染原主题数据）
    /// </summary>
    public CustomThemeProfile GetSavedActiveTheme()
    {
        var tc = _config.Settings.ThemeCenter;
        var all = GetAllThemes();
        var match = all.FirstOrDefault(x => string.Equals(x.Id, tc.ActiveThemeId, StringComparison.OrdinalIgnoreCase));
        CustomThemeProfile selected;
        if (match != null)
        {
            selected = match;
        }
        else if (_config.Settings.ManualTheme == "Light" && !_config.Settings.AutoTheme)
        {
            selected = all.First(x => x.Id == "builtin-emerald-light");
        }
        else
        {
            selected = all[0];
        }

        if (!string.IsNullOrWhiteSpace(tc.OverrideBaseMode))
        {
            var copy = selected.Clone();
            copy.BaseMode = tc.OverrideBaseMode;
            return copy;
        }

        return selected;
    }

    /// <summary>
    /// 应用主题（可选择仅实时预览或写入持久化配置）
    /// </summary>
    public async Task ApplyThemeAsync(CustomThemeProfile profile, bool saveToDisk = true)
    {
        if (saveToDisk)
        {
            PreviewProfile = null;
            _config.Settings.ThemeCenter.ActiveThemeId = profile.Id;
            _config.Settings.ThemeCenter.OverrideBaseMode = null;

            _config.Settings.AutoTheme = string.Equals(profile.BaseMode, "System", StringComparison.OrdinalIgnoreCase);
            if (!_config.Settings.AutoTheme)
            {
                _config.Settings.ManualTheme = string.Equals(profile.BaseMode, "Light", StringComparison.OrdinalIgnoreCase)
                    ? "Light"
                    : "Dark";
            }
        }
        else
        {
            PreviewProfile = profile;
        }

        var adapted = ResolveModeAdaptedProfile(profile, IsEffectiveLightMode(profile.BaseMode));
        UpdateGlobalAccentBrushes(adapted);
        ThemeApplied?.Invoke(profile);

        if (saveToDisk)
        {
            await _config.SaveSettingsAsync();
        }
    }

    /// <summary>
    /// 全局切换日间/夜间/跟随系统模式（不篡改任何主题原始保存的色值，由双态自适应引擎无损渲染）
    /// </summary>
    public async Task SetGlobalModeOverrideAsync(string mode)
    {
        PreviewProfile = null;
        _config.Settings.ThemeCenter.OverrideBaseMode = mode;
        _config.Settings.AutoTheme = string.Equals(mode, "System", StringComparison.OrdinalIgnoreCase);
        if (!_config.Settings.AutoTheme)
        {
            _config.Settings.ManualTheme = string.Equals(mode, "Light", StringComparison.OrdinalIgnoreCase)
                ? "Light"
                : "Dark";
        }

        var curId = _config.Settings.ThemeCenter.ActiveThemeId;
        if (curId is "builtin-emerald-dark" or "builtin-emerald-light" && mode is "Light" or "Dark")
        {
            _config.Settings.ThemeCenter.ActiveThemeId = mode == "Light" ? "builtin-emerald-light" : "builtin-emerald-dark";
            _config.Settings.ThemeCenter.OverrideBaseMode = null;
        }

        var effective = GetSavedActiveTheme();
        var adapted = ResolveModeAdaptedProfile(effective, IsEffectiveLightMode(effective.BaseMode));
        UpdateGlobalAccentBrushes(adapted);
        ThemeApplied?.Invoke(effective);
        await _config.SaveSettingsAsync();
    }

    public static bool IsEffectiveLightMode(string? baseMode)
    {
        if (string.Equals(baseMode, "Light", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(baseMode, "System", StringComparison.OrdinalIgnoreCase))
            return Application.Current.RequestedTheme == ApplicationTheme.Light;
        return false;
    }

    /// <summary>
    /// 将极光或封面流光色彩适配到当前深浅模式（浅色模式转清透水彩粉彩霞光，深色模式转饱满暗夜霓虹）
    /// </summary>
    public static Color AdaptAuroraColorForMode(Color c, bool isLight)
    {
        var (h, s, l) = RgbToHsl(c.R, c.G, c.B);
        if (isLight)
        {
            return l < 0.72
                ? HslToColor(h, Math.Clamp(s, 0.65, 0.90), Math.Clamp(0.77 + l * 0.08, 0.76, 0.84))
                : c;
        }

        return l > 0.68
            ? HslToColor(h, Math.Clamp(s, 0.65, 0.90), 0.52)
            : c;
    }

    /// <summary>
    /// 深/浅双态智能自适应色彩映射引擎：
    /// 当深色主题切至浅色（日间）或浅色主题切至深色（夜间）时，保留主题灵魂色相、极光、粒子与形态，
    /// 自动将画布底色、极光光斑、粒子明度、分栏背景与文字对比度无损转换为协调通透的白昼/暗夜色板。
    /// </summary>
    public static CustomThemeProfile ResolveModeAdaptedProfile(CustomThemeProfile source, bool targetIsLight)
    {
        var accent = ParseColorOrDefault(source.AccentColorHex, Color.FromArgb(255, 16, 185, 129));
        var (accH, accS, accL) = RgbToHsl(accent.R, accent.G, accent.B);

        var winBg = ParseColorOrDefault(source.WindowBgColorHex, Color.FromArgb(255, 15, 23, 42));
        var (winH, winS, winL) = RgbToHsl(winBg.R, winBg.G, winBg.B);
        double themeHue = winS >= 0.10 ? winH : accH;

        bool designedForLight = winL >= 0.50;
        if (designedForLight == targetIsLight)
        {
            return SanitizeTextContrastIfNeeded(source, targetIsLight, themeHue);
        }

        var adapted = source.Clone();

        if (targetIsLight)
        {
            // ==================== 深色主题 -> 清透白昼浅色自适应 ====================
            adapted.BaseMode = "Light";
            double tintSat = Math.Clamp(Math.Max(winS * 0.60, accS * 0.35), 0.14, 0.36);

            // 1. 守护白底与白字按钮上的强调色对比度（过亮荧光色适度压实）
            if (accL > 0.52)
            {
                adapted.AccentColorHex = ToHex(HslToColor(accH, Math.Clamp(accS, 0.62, 0.90), 0.44));
            }
            if (adapted.EnableAccentGradient && TryParseColor(adapted.AccentGradientEndHex, out var gradEnd))
            {
                var (eh, es, el) = RgbToHsl(gradEnd.R, gradEnd.G, gradEnd.B);
                if (el > 0.55)
                {
                    adapted.AccentGradientEndHex = ToHex(HslToColor(eh, Math.Clamp(es, 0.62, 0.90), 0.46));
                }
            }

            // 2. 窗口底层画布与遮罩转为柔和微染白昼底色
            adapted.WindowBgColorHex = ToHex(HslToColor(themeHue, tintSat, 0.966));
            if (TryParseColor(adapted.BackgroundMaskColorHex, out var maskCol) && RgbToHsl(maskCol.R, maskCol.G, maskCol.B).L < 0.50)
            {
                bool isClearGlass = string.Equals(adapted.CardVisualStyle, "LiquidGlass", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(adapted.CardVisualStyle, "CrystalClear", StringComparison.OrdinalIgnoreCase);
                adapted.BackgroundMaskColorHex = ToHex(HslToColor(themeHue, tintSat * 0.75, 0.96));
                adapted.BackgroundMaskOpacity = (adapted.EnableAdaptiveWallpaperText || isClearGlass)
                    ? Math.Min(adapted.BackgroundMaskOpacity, 54)
                    : Math.Clamp(adapted.BackgroundMaskOpacity, 24, 54);
            }

            // 3. 极光光斑转为水彩晨曦粉彩，粒子提升在白底上的辨识度
            adapted.AuroraColor1Hex = ToHex(AdaptAuroraColorForMode(ParseColorOrDefault(adapted.AuroraColor1Hex, accent), true));
            adapted.AuroraColor2Hex = ToHex(AdaptAuroraColorForMode(ParseColorOrDefault(adapted.AuroraColor2Hex, Color.FromArgb(255, 99, 102, 241)), true));
            adapted.AuroraColor3Hex = ToHex(AdaptAuroraColorForMode(ParseColorOrDefault(adapted.AuroraColor3Hex, Color.FromArgb(255, 236, 72, 153)), true));
            adapted.AuroraIntensity = Math.Clamp(adapted.AuroraIntensity, 38, 56);

            if (TryParseColor(adapted.ParticleColorHex, out var pCol))
            {
                var (ph, ps, pl) = RgbToHsl(pCol.R, pCol.G, pCol.B);
                if (pl > 0.72)
                {
                    adapted.ParticleColorHex = ToHex(HslToColor(ph, Math.Clamp(Math.Max(ps, 0.68), 0.55, 0.92), 0.60));
                }
            }

            string MapSurfaceToLight(string hex, double targetL, double satScale = 1.0)
            {
                if (!TryParseColor(hex, out var c)) return hex;
                var (h, s, l) = RgbToHsl(c.R, c.G, c.B);
                if (l >= 0.52) return hex;
                double useH = s >= 0.08 ? h : themeHue;
                return ToHex(HslToColor(useH, Math.Clamp(tintSat * satScale, 0.08, 0.36), targetL));
            }

            string MapTextToLight(string hex, double targetL, double maxAllowedL = 0.42)
            {
                if (string.IsNullOrWhiteSpace(hex) || !TryParseColor(hex, out var c)) return hex;
                var (th, ts, l) = RgbToHsl(c.R, c.G, c.B);
                double useH = ts >= 0.10 ? th : themeHue;
                return l > maxAllowedL ? ToHex(HslToColor(useH, 0.22, targetL)) : hex;
            }

            // 4. 各自定义区域与文字对比度白昼化（确保透过毛玻璃与极光依然深邃锐利）
            adapted.TitleBarBgHex = MapSurfaceToLight(adapted.TitleBarBgHex, 0.945);
            adapted.TitleBarGradientEndHex = MapSurfaceToLight(adapted.TitleBarGradientEndHex, 0.925);
            adapted.TitleBarTextHex = MapTextToLight(adapted.TitleBarTextHex, 0.12, 0.36);

            adapted.NavPaneBgHex = MapSurfaceToLight(adapted.NavPaneBgHex, 0.965);
            adapted.NavPaneGradientEndHex = MapSurfaceToLight(adapted.NavPaneGradientEndHex, 0.945);
            adapted.NavPaneOpacity = Math.Min(adapted.NavPaneOpacity, 26);
            adapted.NavPaneTextHex = MapTextToLight(adapted.NavPaneTextHex, 0.12, 0.36);

            adapted.ContentBgHex = MapSurfaceToLight(adapted.ContentBgHex, 0.975);
            adapted.ContentOpacity = 0;
            adapted.CardBgHex = MapSurfaceToLight(adapted.CardBgHex, 0.992, 0.25);
            adapted.CardOpacity = Math.Min(adapted.CardOpacity, 20);
            adapted.CardBorderHex = "#FFFFFF";
            adapted.PrimaryTextHex = MapTextToLight(adapted.PrimaryTextHex, 0.11, 0.34);
            adapted.SecondaryTextHex = MapTextToLight(adapted.SecondaryTextHex, 0.26, 0.40);

            adapted.PlayBarBgHex = MapSurfaceToLight(adapted.PlayBarBgHex, 0.985, 0.40);
            adapted.PlayBarGradientEndHex = MapSurfaceToLight(adapted.PlayBarGradientEndHex, 0.962, 0.60);
            adapted.PlayBarOpacity = Math.Min(adapted.PlayBarOpacity, 28);
            adapted.PlayBarBorderHex = "#FFFFFF";
            adapted.PlayBarTextHex = MapTextToLight(adapted.PlayBarTextHex, 0.12, 0.36);

            adapted.MiniPlayerBgHex = MapSurfaceToLight(adapted.MiniPlayerBgHex, 0.975, 0.55);
        }
        else
        {
            // ==================== 浅色主题 -> 深邃暗夜深色自适应 ====================
            adapted.BaseMode = "Dark";
            double tintSat = Math.Clamp(Math.Max(winS * 0.75, accS * 0.35), 0.16, 0.42);

            if (accL < 0.42)
            {
                adapted.AccentColorHex = ToHex(HslToColor(accH, Math.Clamp(accS, 0.58, 0.90), 0.56));
            }

            adapted.WindowBgColorHex = ToHex(HslToColor(themeHue, tintSat, 0.08));
            if (TryParseColor(adapted.BackgroundMaskColorHex, out var maskCol) && RgbToHsl(maskCol.R, maskCol.G, maskCol.B).L >= 0.50)
            {
                adapted.BackgroundMaskColorHex = ToHex(HslToColor(themeHue, tintSat, 0.07));
            }

            adapted.AuroraColor1Hex = ToHex(AdaptAuroraColorForMode(ParseColorOrDefault(adapted.AuroraColor1Hex, accent), false));
            adapted.AuroraColor2Hex = ToHex(AdaptAuroraColorForMode(ParseColorOrDefault(adapted.AuroraColor2Hex, Color.FromArgb(255, 99, 102, 241)), false));
            adapted.AuroraColor3Hex = ToHex(AdaptAuroraColorForMode(ParseColorOrDefault(adapted.AuroraColor3Hex, Color.FromArgb(255, 236, 72, 153)), false));
            adapted.AuroraIntensity = Math.Clamp(Math.Max(adapted.AuroraIntensity, 64), 50, 82);

            string MapSurfaceToDark(string hex, double targetL)
            {
                if (!TryParseColor(hex, out var c)) return hex;
                var (h, s, l) = RgbToHsl(c.R, c.G, c.B);
                if (l < 0.50) return hex;
                double useH = s >= 0.08 ? h : themeHue;
                return ToHex(HslToColor(useH, tintSat, targetL));
            }

            string MapTextToDark(string hex, double targetL)
            {
                if (string.IsNullOrWhiteSpace(hex) || !TryParseColor(hex, out var c)) return hex;
                var (_, _, l) = RgbToHsl(c.R, c.G, c.B);
                return l < 0.50 ? ToHex(HslToColor(themeHue, 0.15, targetL)) : hex;
            }

            adapted.TitleBarBgHex = MapSurfaceToDark(adapted.TitleBarBgHex, 0.10);
            adapted.TitleBarGradientEndHex = MapSurfaceToDark(adapted.TitleBarGradientEndHex, 0.12);
            adapted.TitleBarTextHex = MapTextToDark(adapted.TitleBarTextHex, 0.94);

            adapted.NavPaneBgHex = MapSurfaceToDark(adapted.NavPaneBgHex, 0.105);
            adapted.NavPaneGradientEndHex = MapSurfaceToDark(adapted.NavPaneGradientEndHex, 0.125);
            adapted.NavPaneTextHex = MapTextToDark(adapted.NavPaneTextHex, 0.92);

            adapted.ContentBgHex = MapSurfaceToDark(adapted.ContentBgHex, 0.085);
            adapted.CardBgHex = MapSurfaceToDark(adapted.CardBgHex, 0.145);
            adapted.CardBorderHex = MapSurfaceToDark(adapted.CardBorderHex, 0.24);
            adapted.PrimaryTextHex = MapTextToDark(adapted.PrimaryTextHex, 0.94);
            adapted.SecondaryTextHex = MapTextToDark(adapted.SecondaryTextHex, 0.68);

            adapted.PlayBarBgHex = MapSurfaceToDark(adapted.PlayBarBgHex, 0.115);
            adapted.PlayBarGradientEndHex = MapSurfaceToDark(adapted.PlayBarGradientEndHex, 0.135);
            adapted.PlayBarBorderHex = MapSurfaceToDark(adapted.PlayBarBorderHex, 0.22);
            adapted.PlayBarTextHex = MapTextToDark(adapted.PlayBarTextHex, 0.94);

            adapted.MiniPlayerBgHex = MapSurfaceToDark(adapted.MiniPlayerBgHex, 0.11);
        }

        return adapted;
    }

    /// <summary>
    /// 对同明暗基调主题中的自定义分栏文字颜色进行可读性对比度兜底（防止浅色底误配浅灰白字，或深色底误配深暗黑字）
    /// </summary>
    private static CustomThemeProfile SanitizeTextContrastIfNeeded(CustomThemeProfile source, bool isLight, double fallbackHue)
    {
        string CheckField(string hex, bool isSecondary)
        {
            if (string.IsNullOrWhiteSpace(hex) || !TryParseColor(hex, out var c)) return hex;
            var (h, s, l) = RgbToHsl(c.R, c.G, c.B);
            double useH = s >= 0.08 ? h : fallbackHue;
            double useS = Math.Clamp(s, 0.14, 0.36);

            if (isLight)
            {
                double maxL = isSecondary ? 0.40 : 0.34;
                if (l > maxL)
                {
                    return ToHex(HslToColor(useH, useS, isSecondary ? 0.26 : 0.12));
                }
            }
            else
            {
                double minL = isSecondary ? 0.50 : 0.58;
                if (l < minL)
                {
                    return ToHex(HslToColor(useH, Math.Min(useS, 0.22), isSecondary ? 0.72 : 0.94));
                }
            }
            return hex;
        }

        string tb = CheckField(source.TitleBarTextHex, false);
        string np = CheckField(source.NavPaneTextHex, false);
        string pt = CheckField(source.PrimaryTextHex, false);
        string st = CheckField(source.SecondaryTextHex, true);
        string pb = CheckField(source.PlayBarTextHex, false);

        if (tb == source.TitleBarTextHex && np == source.NavPaneTextHex &&
            pt == source.PrimaryTextHex && st == source.SecondaryTextHex && pb == source.PlayBarTextHex)
        {
            return source;
        }

        var copy = source.Clone();
        copy.TitleBarTextHex = tb;
        copy.NavPaneTextHex = np;
        copy.PrimaryTextHex = pt;
        copy.SecondaryTextHex = st;
        copy.PlayBarTextHex = pb;
        return copy;
    }

    /// <summary>
    /// 将任意强调色转换为当前明暗背景（特别是浅色毛玻璃与极光底）下高可读的彩色文字/图标前景色
    /// </summary>
    public static Color EnsureReadableTextAccent(Color accent, bool isLight)
    {
        var (h, s, l) = RgbToHsl(accent.R, accent.G, accent.B);
        if (isLight)
        {
            return l > 0.37
                ? HslToColor(h, Math.Clamp(Math.Max(s, 0.65), 0.55, 0.92), 0.35)
                : accent;
        }

        return l < 0.54
            ? HslToColor(h, Math.Clamp(Math.Max(s, 0.62), 0.50, 0.92), 0.62)
            : accent;
    }

    /// <summary>
    /// 保障承载纯白文字（Foreground="White"）的主操作按钮填充色在浅色模式下具备充足对比度，不刺眼发白
    /// </summary>
    public static Color EnsureReadableButtonFill(Color fill, bool isLight)
    {
        if (!isLight) return fill;
        var (h, s, l) = RgbToHsl(fill.R, fill.G, fill.B);
        return l > 0.48
            ? HslToColor(h, Math.Clamp(Math.Max(s, 0.65), 0.55, 0.92), 0.44)
            : fill;
    }

    /// <summary>
    /// 通知外部监听器当前预览主题的属性已微调
    /// </summary>
    public void NotifyPreviewUpdated(CustomThemeProfile draft)
    {
        PreviewProfile = draft;
        UpdateGlobalAccentBrushes(draft);
        ThemeApplied?.Invoke(draft);
    }

    /// <summary>
    /// 轻量更新预览草稿引用（供 DIY 滑块与调色盘高频拖动局部刷新通道使用，避免触发全窗口重绘）
    /// </summary>
    public void SetPreviewDraft(CustomThemeProfile draft, bool updateAccentBrushes = false)
    {
        PreviewProfile = draft;
        if (updateAccentBrushes)
        {
            UpdateGlobalAccentBrushes(draft);
        }
    }

    /// <summary>
    /// 放弃预览并恢复到已保存的正式主题
    /// </summary>
    public void RevertPreview()
    {
        PreviewProfile = null;
        var saved = GetSavedActiveTheme();
        UpdateGlobalAccentBrushes(saved);
        ThemeApplied?.Invoke(saved);
    }

    /// <summary>
    /// 保存或更新一套用户自制主题并立即设为当前激活主题
    /// </summary>
    public async Task<CustomThemeProfile> SaveCustomThemeAsync(CustomThemeProfile draft)
    {
        draft.IsBuiltIn = false;
        if (string.IsNullOrWhiteSpace(draft.Id) || draft.Id.StartsWith("builtin-", StringComparison.OrdinalIgnoreCase))
        {
            draft.Id = Guid.NewGuid().ToString("N");
            draft.CreatedAt = DateTime.Now;
        }

        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            draft.Name = "我的自定义主题";
        }

        var list = _config.Settings.ThemeCenter.CustomThemes;
        int idx = list.FindIndex(x => string.Equals(x.Id, draft.Id, StringComparison.OrdinalIgnoreCase));
        var savedCopy = draft.Clone();
        savedCopy.IsBuiltIn = false;

        if (idx >= 0)
        {
            list[idx] = savedCopy;
        }
        else
        {
            list.Add(savedCopy);
        }

        await ApplyThemeAsync(savedCopy, saveToDisk: true);
        return savedCopy;
    }

    /// <summary>
    /// 删除指定的用户自制主题
    /// </summary>
    public async Task<bool> DeleteCustomThemeAsync(string themeId)
    {
        var list = _config.Settings.ThemeCenter.CustomThemes;
        var target = list.FirstOrDefault(x => string.Equals(x.Id, themeId, StringComparison.OrdinalIgnoreCase));
        if (target == null || target.IsBuiltIn) return false;

        list.Remove(target);
        if (string.Equals(_config.Settings.ThemeCenter.ActiveThemeId, themeId, StringComparison.OrdinalIgnoreCase))
        {
            var fallback = CustomThemeProfile.GetBuiltInPresets()[0];
            await ApplyThemeAsync(fallback, saveToDisk: true);
        }
        else
        {
            await _config.SaveSettingsAsync();
        }
        return true;
    }

    // ==================== 全局画刷与双色渐变实时更新 ====================

    public (Color Accent, Color Hover, Color Pressed, Color Subtle) DeriveAccentColors(CustomThemeProfile profile)
    {
        var accent = ParseColorOrDefault(profile.AccentColorHex, Color.FromArgb(255, 16, 185, 129));
        Color hover;
        Color pressed;

        if (profile.UseCustomAccentStates)
        {
            hover = ParseColorOrDefault(profile.AccentHoverHex, AdjustLightness(accent, -0.08));
            pressed = ParseColorOrDefault(profile.AccentPressedHex, AdjustLightness(accent, -0.15));
        }
        else
        {
            hover = AdjustLightness(accent, -0.08);
            pressed = AdjustLightness(accent, -0.15);
        }

        var subtle = Color.FromArgb(0x22, accent.R, accent.G, accent.B);
        return (accent, hover, pressed, subtle);
    }

    /// <summary>
    /// 动态原地更新 Application.Current.Resources 中的品牌纯色与双色线性渐变画刷（保持引用类型严格一致，确保全窗口 StaticResource 100% 实时变色）
    /// </summary>
    public void UpdateGlobalAccentBrushes(CustomThemeProfile profile)
    {
        bool isLight = IsEffectiveLightMode(profile.BaseMode);
        profile = ResolveModeAdaptedProfile(profile, isLight);
        var (accent, hover, pressed, subtle) = DeriveAccentColors(profile);
        var accentEnd = profile.EnableAccentGradient
            ? ParseColorOrDefault(profile.AccentGradientEndHex, accent)
            : accent;

        // 彩色文字前景色在浅色毛玻璃下自动压深至高可读区间；黑底角标专用 LightText 始终保持清亮高明度
        var textAccent = EnsureReadableTextAccent(accent, isLight);
        var textHover = AdjustLightness(textAccent, -0.07);
        var textPressed = AdjustLightness(textAccent, -0.13);
        var (ah, aSat, al) = RgbToHsl(accent.R, accent.G, accent.B);
        var darkSurfaceLightText = HslToColor(ah, Math.Clamp(Math.Max(aSat, 0.60), 0.50, 0.90), Math.Clamp(al + 0.16, 0.68, 0.84));

        // 承载纯白文字的主操作按钮填充色在浅色模式下做护眼对比度钳制
        var fillStart = EnsureReadableButtonFill(accent, isLight);
        var fillEnd = EnsureReadableButtonFill(accentEnd, isLight);
        var fillHoverStart = profile.UseCustomAccentStates ? EnsureReadableButtonFill(hover, isLight) : AdjustLightness(fillStart, -0.07);
        var fillHoverEnd = profile.EnableAccentGradient ? AdjustLightness(fillEnd, -0.07) : fillHoverStart;
        var fillPressedStart = profile.UseCustomAccentStates ? EnsureReadableButtonFill(pressed, isLight) : AdjustLightness(fillStart, -0.14);
        var fillPressedEnd = profile.EnableAccentGradient ? AdjustLightness(fillEnd, -0.14) : fillPressedStart;

        var borderSubtle = Color.FromArgb(isLight ? (byte)0x68 : (byte)0x56, textAccent.R, textAccent.G, textAccent.B);
        var pressedSubtle = Color.FromArgb(0x3A, accent.R, accent.G, accent.B);

        var res = Application.Current.Resources;

        // 1. 原地更新纯色画刷（覆盖全窗口文字、FontIcon、徽章、进度环、迷你播放器等）
        SetOrUpdateSolidBrush(res, "BrandAccentBrush", textAccent);
        SetOrUpdateSolidBrush(res, "BrandAccentHoverBrush", textHover);
        SetOrUpdateSolidBrush(res, "BrandAccentPressedBrush", textPressed);
        SetOrUpdateSolidBrush(res, "BrandAccentLightTextBrush", darkSurfaceLightText);
        SetOrUpdateSolidBrush(res, "BrandAccentSubtleBrush", subtle);
        SetOrUpdateSolidBrush(res, "BrandAccentBorderSubtleBrush", borderSubtle);
        SetOrUpdateSolidBrush(res, "BrandAccentPressedSubtleBrush", pressedSubtle);

        // 2. 原地更新双色流光渐变填充画刷或液态水滴画刷（非降级模式升级为透明折射水滴胶囊，降级模式保持实心渐变）
        string bdKind = profile.BackdropType ?? "Mica";
        bool isDegradedBackdrop = bdKind is "Mica" or "MicaAlt" or "Acrylic" or "Video";
        bool useDropletButtons = !isDegradedBackdrop && GlassHub.IsGpuAccelerationEnabled && LiquidGlassWinUI.LiquidGlassBrush.LastError == null;

        if (useDropletButtons)
        {
            SetOrUpdateDropletBrush(res, "BrandAccentFillBrush", fillStart, isLight ? 0.58 : 0.48, isLight, refThickness: 15.0, refFactor: 2.35);
        }
        else
        {
            SetOrUpdateGradientBrush(res, "BrandAccentFillBrush", fillStart, fillEnd, profile.GradientAngle);
        }
        SetOrUpdateGradientBrush(res, "BrandAccentHoverFillBrush", fillHoverStart, fillHoverEnd, profile.GradientAngle);
        SetOrUpdateGradientBrush(res, "BrandAccentPressedFillBrush", fillPressedStart, fillPressedEnd, profile.GradientAngle);

        var sliderRaw = (profile.EnableCustomPlayBar && !string.IsNullOrWhiteSpace(profile.ProgressSliderColorHex))
            ? ParseColorOrDefault(profile.ProgressSliderColorHex, accent)
            : accent;
        var sliderColor = EnsureReadableButtonFill(sliderRaw, isLight);
        SetOrUpdateSolidBrush(res, "ThemePlayBarSliderBrush", sliderColor);

        // 3. 原地更新底部圆形主播放按钮画刷（非降级模式使用 DialCallKey 同款青色水滴画刷；降级模式回退青色/主题渐变）
        bool isLiquidGlassProfile = string.Equals(profile.CardVisualStyle, "LiquidGlass", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(profile.BackdropType, "Image", StringComparison.OrdinalIgnoreCase) &&
             (string.IsNullOrWhiteSpace(profile.CardVisualStyle) ||
              string.Equals(profile.CardVisualStyle, "Default", StringComparison.OrdinalIgnoreCase)));
        bool customPlayBtn = !string.IsNullOrWhiteSpace(profile.PlayButtonColorHex) &&
                             (profile.EnableCustomPlayBar || isLiquidGlassProfile);
        var cyanPlayColor = Color.FromArgb(0xFF, 0x3D, 0xCB, 0xF5);
        var playBtnStart = customPlayBtn
            ? EnsureReadableButtonFill(ParseColorOrDefault(profile.PlayButtonColorHex, accent), isLight)
            : (isLiquidGlassProfile ? cyanPlayColor : fillStart);
        var playBtnEnd = customPlayBtn
            ? playBtnStart
            : (isLiquidGlassProfile ? cyanPlayColor : fillEnd);
        if (useDropletButtons)
        {
            SetOrUpdateDropletBrush(res, "ThemePlayButtonBrush", playBtnStart, isLight ? 0.60 : 0.50, isLight, refThickness: 18.0, refFactor: 2.40, magnification: 1.04);
        }
        else
        {
            SetOrUpdateGradientBrush(res, "ThemePlayButtonBrush", playBtnStart, playBtnEnd, profile.GradientAngle);
        }
        SetOrUpdateGradientBrush(res, "ThemePlayButtonHoverBrush", AdjustLightness(playBtnStart, -0.08), AdjustLightness(playBtnEnd, -0.08), profile.GradientAngle);
        SetOrUpdateGradientBrush(res, "ThemePlayButtonPressedBrush", AdjustLightness(playBtnStart, -0.15), AdjustLightness(playBtnEnd, -0.15), profile.GradientAngle);
    }

    public static (Point Start, Point End) GetGradientEndpoints(double angleDegrees)
    {
        int norm = ((int)Math.Round(angleDegrees) % 360 + 360) % 360;
        return norm switch
        {
            < 25 or >= 335 => (new Point(0, 0.5), new Point(1, 0.5)), // 0° 水平
            >= 25 and < 70 => (new Point(0, 1), new Point(1, 0)),     // 45° 右上斜向
            >= 70 and < 115 => (new Point(0.5, 0), new Point(0.5, 1)),// 90° 垂直
            _ => (new Point(0, 0), new Point(1, 1))                   // 135° 右下斜向
        };
    }

    /// <summary>
    /// 生成混入极淡主题色相的冰透微光白，避免浅色毛玻璃发死白或冷灰
    /// </summary>
    public static Color CreateTintedIceWhite(Color accent, double accentWeight = 0.055)
    {
        double w = Math.Clamp(accentWeight, 0.0, 0.30);
        byte r = (byte)Math.Round(255 * (1 - w) + accent.R * w);
        byte g = (byte)Math.Round(255 * (1 - w) + accent.G * w);
        byte b = (byte)Math.Round(255 * (1 - w) + accent.B * w);
        return Color.FromArgb(255, r, g, b);
    }

    /// <summary>
    /// 结合主题窗口底色、极光主色与灵魂强调色，调配带有鲜明主题性格的深/浅表面色调（供导航栏、毛玻璃卡片、底栏、弹窗与抽屉统一染色）
    /// </summary>
    public static Color CreateThemeTintedSurfaceColor(CustomThemeProfile profile, Color accent, bool isLight, double tintWeight = 0.14)
    {
        Color soul = accent;
        if (string.Equals(profile.BackdropType, "Aurora", StringComparison.OrdinalIgnoreCase) &&
            TryParseColor(profile.AuroraColor1Hex, out var aur1))
        {
            soul = LerpColor(accent, aur1, 0.45);
        }

        if (isLight)
        {
            return CreateTintedIceWhite(soul, Math.Clamp(tintWeight, 0.05, 0.24));
        }

        var winBg = ParseColorOrDefault(profile.WindowBgColorHex, Color.FromArgb(255, 15, 23, 42));
        var liftedBase = AdjustLightness(winBg, 0.045);
        return LerpColor(liftedBase, soul, Math.Clamp(tintWeight, 0.08, 0.28));
    }

    /// <summary>
    /// 构建应用内实时高斯模糊亚克力毛玻璃画刷（透出底层浅色/深色动态极光与粒子虚化光晕，失焦回退仍保持高通透不泛死白）
    /// </summary>
    public static AcrylicBrush CreateFrostedGlassBrush(Color tintColor, double tintOpacity, double luminosityOpacity, byte fallbackAlpha = 48)
    {
        return new AcrylicBrush
        {
            TintColor = Color.FromArgb(255, tintColor.R, tintColor.G, tintColor.B),
            TintOpacity = Math.Clamp(tintOpacity, 0.02, 0.92),
            TintLuminosityOpacity = Math.Clamp(luminosityOpacity, 0.04, 0.92),
            FallbackColor = Color.FromArgb(fallbackAlpha, tintColor.R, tintColor.G, tintColor.B)
        };
    }

    /// <summary>
    /// 构建原生 Windows DWM 合成器级 Liquid Glass（液体玻璃）双通道光学画刷
    /// </summary>
    public static Brush CreateLiquidGlassSurfaceBrush(
        Color accent,
        bool isLight,
        double intensity = 1.0,
        LiquidGlassPreset preset = LiquidGlassPreset.FrostedFolder,
        float? customBlurAmount = null,
        float? customCenterBlurWeight = null,
        float? customTintOpacity = null,
        Color? customTintColor = null,
        double? customCornerRadiusPx = null)
    {
        if (!GlassHub.IsGpuAccelerationEnabled)
        {
            Color fallbackCol = customTintColor ?? (isLight
                ? Color.FromArgb(0xD8, 255, 255, 255)
                : Color.FromArgb(0xCC, 0x0C, 0x15, 0x24));
            return new SolidColorBrush(fallbackCol);
        }

        if (GlassHub.IsDegradedMode || LiquidGlassWinUI.LiquidGlassBrush.LastError != null)
        {
            return new LiquidGlassCompositionBrush(accent, isLight, intensity, preset, customBlurAmount, customCenterBlurWeight, customTintOpacity, customTintColor);
        }

        double safeIntensity = Math.Clamp(intensity, 0.75, 1.25);

        // 高不透明浮层 / 抽屉 / 弹出菜单：强高斯模糊 + 控制着色不透明度防止透字
        if (customTintOpacity is >= 0.70f)
        {
            Color tint = customTintColor ?? (isLight
                ? Color.FromArgb(255, 0xEC, 0xF4, 0xF9)
                : Color.FromArgb(255, 0x0A, 0x20, 0x2C));
            double blur = customBlurAmount.HasValue
                ? Math.Clamp(customBlurAmount.Value / 14.0, 0.35, 1.85)
                : 1.45;
            double modalCr = Math.Clamp(customCornerRadiusPx ?? 16.0, 6.0, 26.0);

            return new LiquidGlassWinUI.LiquidGlassBrush
            {
                BlurAmount = blur,
                BloomAmount = 0.85,
                RefFactor = 2.20,
                RefThickness = modalCr,
                RefDispersion = 1.20,
                DispersionRange = 0.30,
                Magnification = 1.0,
                RefFresnelRange = 52.0,
                RefFresnelHardness = 0.0,
                RefFresnelFactor = 26.0,
                GlareAngle = -135.0,
                GlareConvergence = 100.0,
                GlareFactor = 68.0,
                GlareOppositeFactor = 65.0,
                GlareHardness = 14.0,
                GlareRange = 32.0,
                ShapeRadius = modalCr,
                ShapeRoundness = 2.0,
                TintR = tint.R,
                TintG = tint.G,
                TintB = tint.B,
                TintA = Math.Clamp(customTintOpacity.Value, 0.72, 0.90),
                Exposure = isLight ? 1.02 : 0.88,
                Brightness = 0.0,
                Contrast = 1.05,
                Saturation = 1.10,
                Vibrance = 0.12
            };
        }

        // 次级胶囊与交互按钮：轻量模糊保留背景质感，强化水滴边缘折射
        if (preset == LiquidGlassPreset.GlassButton)
        {
            Color tint = customTintColor ?? (isLight
                ? Color.FromArgb(255, 255, 255, 255)
                : Color.FromArgb(255, 0x0A, 0x16, 0x24));
            double tintAlpha = customTintOpacity.HasValue
                ? Math.Clamp(customTintOpacity.Value, 0.0, 0.65)
                : (isLight ? 0.28 : 0.20);
            double blur = customBlurAmount.HasValue
                ? Math.Clamp(customBlurAmount.Value / 18.0, 0.0, 1.20)
                : 0.25;

            return new LiquidGlassWinUI.LiquidGlassBrush
            {
                BlurAmount = blur,
                BloomAmount = 0.15,
                RefFactor = 2.45,
                RefThickness = 18.0,
                RefDispersion = 3.50,
                DispersionRange = 0.39,
                Magnification = 1.02,
                RefFresnelRange = 57.84,
                RefFresnelHardness = 0.0,
                RefFresnelFactor = 24.0,
                GlareAngle = -135.0,
                GlareConvergence = 100.0,
                GlareFactor = 84.0,
                GlareOppositeFactor = 80.0,
                GlareHardness = 13.0,
                GlareRange = 36.13,
                ShapeRadius = 0.85,
                ShapeRoundness = 2.0,
                TintR = tint.R,
                TintG = tint.G,
                TintB = tint.B,
                TintA = tintAlpha,
                Exposure = isLight ? 1.04 : 1.12,
                Brightness = 0.03,
                Contrast = 1.08,
                Saturation = 1.15,
                Vibrance = 0.18
            };
        }

        // 核心大卡片 / 灵动 Dock（FrostedFolder / ClearDock / GlassPanel）：深色用微暗海军蓝保障白字，浅色用亮霜白
        var created = new LiquidGlassWinUI.LiquidGlassBrush();
        ApplySurfaceBrushProperties(created, isLight, intensity, preset, customBlurAmount, customTintOpacity, customTintColor, customCornerRadiusPx);
        return created;
    }

    /// <summary>
    /// 原地更新（或按需创建）Liquid Glass 表面画刷，避免拖动 DIY 滑块时重复销毁重建 DWM 着色器管线与引发视觉树重置
    /// </summary>
    public static void SetOrUpdateLiquidGlassSurfaceBrush(
        ResourceDictionary dict,
        string key,
        Color accentColor,
        bool isLight,
        double intensity = 1.0,
        LiquidGlassPreset preset = LiquidGlassPreset.FrostedFolder,
        double customCornerRadiusPx = 0,
        double? customOpacityPercent = null)
    {
        float? customTintOpacity = customOpacityPercent.HasValue
            ? (float)Math.Clamp(customOpacityPercent.Value / 100.0 * 0.70, 0.0, 0.70)
            : null;
        double? cornerPx = customCornerRadiusPx > 0 ? customCornerRadiusPx : null;

        if (!GlassHub.IsGpuAccelerationEnabled)
        {
            Color fallbackCol = isLight
                ? Color.FromArgb(0xD8, 255, 255, 255)
                : Color.FromArgb(0xCC, 0x0C, 0x15, 0x24);
            if (customOpacityPercent.HasValue)
            {
                byte alpha = (byte)Math.Round(Math.Clamp(customOpacityPercent.Value, 4.0, 95.0) / 100.0 * 255.0);
                fallbackCol = Color.FromArgb(alpha, fallbackCol.R, fallbackCol.G, fallbackCol.B);
            }
            SetOrUpdateSolidBrush(dict, key, fallbackCol);
            return;
        }

        if (GlassHub.IsDegradedMode || LiquidGlassWinUI.LiquidGlassBrush.LastError != null)
        {
            if (dict.TryGetValue(key, out var existingComp) && existingComp is LiquidGlassCompositionBrush lgComp)
            {
                lgComp.UpdateOpticalParameters(accentColor, isLight, intensity, preset, customTintOpacity: customTintOpacity);
                return;
            }
            dict[key] = new LiquidGlassCompositionBrush(accentColor, isLight, intensity, preset, customTintOpacity: customTintOpacity);
            return;
        }

        if (dict.TryGetValue(key, out var existing) && existing is LiquidGlassWinUI.LiquidGlassBrush lgb)
        {
            ApplySurfaceBrushProperties(lgb, isLight, intensity, preset, customBlurAmount: null, customTintOpacity, customTintColor: null, cornerPx);
            return;
        }

        var created = new LiquidGlassWinUI.LiquidGlassBrush();
        ApplySurfaceBrushProperties(created, isLight, intensity, preset, customBlurAmount: null, customTintOpacity, customTintColor: null, cornerPx);
        dict[key] = created;
    }

    private static void ApplySurfaceBrushProperties(
        LiquidGlassWinUI.LiquidGlassBrush lgb,
        bool isLight,
        double intensity,
        LiquidGlassPreset preset,
        float? customBlurAmount,
        float? customTintOpacity,
        Color? customTintColor,
        double? customCornerRadiusPx)
    {
        double safeIntensity = Math.Clamp(intensity, 0.75, 1.25);
        bool isDockPreset = preset == LiquidGlassPreset.ClearDock;
        Color cardTint = customTintColor ?? (isLight
            ? Color.FromArgb(255, 255, 255, 255)
            : Color.FromArgb(255, 0x08, 0x10, 0x1C));
        double baseCardAlpha = isLight ? 0.34 : (isDockPreset ? 0.22 : 0.38);
        double cardAlpha = customTintOpacity.HasValue
            ? Math.Clamp(customTintOpacity.Value, 0.0, 0.70)
            : Math.Clamp(baseCardAlpha * safeIntensity, 0.12, 0.55);
        double cardBlur = customBlurAmount.HasValue
            ? Math.Clamp(customBlurAmount.Value / 18.0, 0.15, 1.40)
            : (isDockPreset ? 0.25 : 0.88);
        double cardCr = Math.Clamp(customCornerRadiusPx ?? (isDockPreset ? 18.0 : 14.0), 6.0, 26.0);

        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.BlurAmountProperty, cardBlur);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.BloomAmountProperty, isDockPreset ? 0.15 : 0.35);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.RefFactorProperty, isDockPreset ? 2.85 : 3.15);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.RefThicknessProperty, cardCr);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.RefDispersionProperty, 3.10);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.DispersionRangeProperty, 0.39);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.MagnificationProperty, 1.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.RefFresnelRangeProperty, 56.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.RefFresnelHardnessProperty, 0.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.RefFresnelFactorProperty, 28.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.GlareAngleProperty, -135.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.GlareConvergenceProperty, 100.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.GlareFactorProperty, isDockPreset ? 82.0 : 76.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.GlareOppositeFactorProperty, isDockPreset ? 78.0 : 72.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.GlareHardnessProperty, 13.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.GlareRangeProperty, 35.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.ShapeRadiusProperty, cardCr);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.ShapeRoundnessProperty, 2.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.TintRProperty, cardTint.R);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.TintGProperty, cardTint.G);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.TintBProperty, cardTint.B);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.TintAProperty, cardAlpha);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.ExposureProperty, isLight ? 1.04 : 1.02);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.BrightnessProperty, 0.02);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.ContrastProperty, 1.06);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.SaturationProperty, 1.15);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.VibranceProperty, 0.18);
    }

    /// <summary>
    /// 仅当依赖属性发生实际数值变化时才写入 LiquidGlassBrush，避免高频拖动滑块时重复向 DWM 推送无意义标量
    /// </summary>
    private static void SetLiquidGlassBrushPropertyIfChanged(
        LiquidGlassWinUI.LiquidGlassBrush brush,
        DependencyProperty dp,
        double nextValue)
    {
        if (brush.GetValue(dp) is not double current || Math.Abs(current - nextValue) > 0.0005)
        {
            brush.SetValue(dp, nextValue);
        }
    }

    /// <summary>
    /// 构建「液态玻璃 (Liquid Glass)」中性镜面高光边界画刷（第 4 层精细上/下反光边缘，摒弃廉价高饱和霓虹粗框）
    /// </summary>
    public static Brush CreateLiquidGlassBorderBrush(
        Color accent,
        double intensity = 1.0,
        bool isLight = false,
        LiquidGlassPreset preset = LiquidGlassPreset.FrostedFolder)
    {
        _ = accent;
        var kind = LiquidGlassCompositionBrush.MapKind(preset);
        var material = LiquidGlass.WinUI.Materials.LiquidGlassMaterialPreset.GetPreset(kind, isLight);
        double boost = Math.Clamp(intensity, 0.75, 1.35);
        return LiquidGlass.WinUI.Rendering.GlassHighlightRenderer.CreateSpecularBorderBrush(material, boost);
    }

    /// <summary>
    /// 原地复用并更新现有分区画刷（AcrylicBrush / SolidColorBrush / LinearGradientBrush），避免拖动透明度或调色时频繁重建 DWM 合成画刷
    /// </summary>
    public static Brush UpdateOrCreateSectionBrush(
        Brush? currentBrush,
        Color primaryColor,
        bool enableGradient,
        string? gradientEndHex,
        double opacityPercent,
        double angleDegrees,
        bool useAcrylicBlur = false)
    {
        if (!enableGradient || !TryParseColor(gradientEndHex, out var rawEnd))
        {
            if (useAcrylicBlur && opacityPercent < 96)
            {
                double normOp = Math.Clamp(opacityPercent / 100.0, 0.06, 0.88);
                double tintOp = Math.Clamp(normOp * 0.55, 0.06, 0.68);
                double lumOp = Math.Clamp(normOp * 0.72 + 0.04, 0.10, 0.78);
                byte fbAlpha = (byte)Math.Round(Math.Clamp(normOp * 0.65, 0.12, 0.70) * 255);
                if (currentBrush is AcrylicBrush existingAcrylic)
                {
                    existingAcrylic.TintColor = Color.FromArgb(255, primaryColor.R, primaryColor.G, primaryColor.B);
                    existingAcrylic.TintOpacity = tintOp;
                    existingAcrylic.TintLuminosityOpacity = lumOp;
                    existingAcrylic.FallbackColor = Color.FromArgb(fbAlpha, primaryColor.R, primaryColor.G, primaryColor.B);
                    return existingAcrylic;
                }
                return CreateFrostedGlassBrush(primaryColor, tintOp, lumOp, fbAlpha);
            }

            var solidCol = WithOpacity(primaryColor, opacityPercent);
            if (currentBrush is SolidColorBrush existingSolid)
            {
                try
                {
                    existingSolid.Color = solidCol;
                    return existingSolid;
                }
                catch
                {
                }
            }
            return new SolidColorBrush(solidCol);
        }

        var c1 = WithOpacity(primaryColor, opacityPercent);
        var c2 = WithOpacity(rawEnd, opacityPercent);
        var (start, end) = GetGradientEndpoints(angleDegrees);
        if (currentBrush is LinearGradientBrush existingLgb && existingLgb.GradientStops.Count == 2)
        {
            try
            {
                existingLgb.StartPoint = start;
                existingLgb.EndPoint = end;
                existingLgb.GradientStops[0].Color = c1;
                existingLgb.GradientStops[1].Color = c2;
                return existingLgb;
            }
            catch
            {
            }
        }

        return CreateSectionBrush(primaryColor, enableGradient, gradientEndHex, opacityPercent, angleDegrees, useAcrylicBlur);
    }

    public static Brush CreateSectionBrush(Color primaryColor, bool enableGradient, string? gradientEndHex, double opacityPercent, double angleDegrees, bool useAcrylicBlur = false)
    {
        if (!enableGradient || !TryParseColor(gradientEndHex, out var rawEnd))
        {
            if (useAcrylicBlur && opacityPercent < 96)
            {
                double normOp = Math.Clamp(opacityPercent / 100.0, 0.06, 0.88);
                double tintOp = Math.Clamp(normOp * 0.55, 0.06, 0.68);
                double lumOp = Math.Clamp(normOp * 0.72 + 0.04, 0.10, 0.78);
                byte fbAlpha = (byte)Math.Round(Math.Clamp(normOp * 0.65, 0.12, 0.70) * 255);
                return CreateFrostedGlassBrush(primaryColor, tintOp, lumOp, fbAlpha);
            }

            return new SolidColorBrush(WithOpacity(primaryColor, opacityPercent));
        }

        var c1 = WithOpacity(primaryColor, opacityPercent);
        var c2 = WithOpacity(rawEnd, opacityPercent);
        var (start, end) = GetGradientEndpoints(angleDegrees);
        var lgb = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        lgb.GradientStops.Add(new GradientStop { Color = c1, Offset = 0.0 });
        lgb.GradientStops.Add(new GradientStop { Color = c2, Offset = 1.0 });
        return lgb;
    }

    public static void SetOrUpdateDropletBrush(
        ResourceDictionary dict,
        string key,
        Color tint,
        double tintA,
        bool isLight,
        double refThickness = 22.0,
        double refFactor = 2.45,
        double magnification = 1.0)
    {
        if (dict.TryGetValue(key, out var existing) && existing is LiquidGlassWinUI.LiquidGlassBrush lgb)
        {
            ApplyDropletBrushProperties(lgb, tint, tintA, isLight, refThickness, refFactor, magnification);
            return;
        }

        var created = new LiquidGlassWinUI.LiquidGlassBrush();
        ApplyDropletBrushProperties(created, tint, tintA, isLight, refThickness, refFactor, magnification);
        dict[key] = created;
    }

    private static void ApplyDropletBrushProperties(
        LiquidGlassWinUI.LiquidGlassBrush lgb,
        Color tint,
        double tintA,
        bool isLight,
        double refThickness,
        double refFactor,
        double magnification)
    {
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.BlurAmountProperty, 0.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.BloomAmountProperty, 0.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.RefFactorProperty, refFactor);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.RefThicknessProperty, refThickness);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.RefDispersionProperty, 3.50);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.DispersionRangeProperty, 0.39);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.MagnificationProperty, magnification);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.RefFresnelRangeProperty, 57.84);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.RefFresnelHardnessProperty, 0.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.RefFresnelFactorProperty, 24.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.GlareAngleProperty, -135.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.GlareConvergenceProperty, 100.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.GlareFactorProperty, 84.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.GlareOppositeFactorProperty, 80.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.GlareHardnessProperty, 13.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.GlareRangeProperty, 36.13);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.ShapeRadiusProperty, 1.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.ShapeRoundnessProperty, 2.0);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.TintRProperty, tint.R);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.TintGProperty, tint.G);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.TintBProperty, tint.B);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.TintAProperty, tintA);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.ExposureProperty, isLight ? 1.06 : 1.16);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.BrightnessProperty, 0.03);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.ContrastProperty, 1.08);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.SaturationProperty, 1.15);
        SetLiquidGlassBrushPropertyIfChanged(lgb, LiquidGlassWinUI.LiquidGlassBrush.VibranceProperty, 0.18);
    }

    private static void SetOrUpdateGradientBrush(ResourceDictionary dict, string key, Color c1, Color c2, double angle)
    {
        var (start, end) = GetGradientEndpoints(angle);
        if (dict.TryGetValue(key, out var existing) && existing is LinearGradientBrush lgb && lgb.GradientStops.Count >= 2)
        {
            try
            {
                lgb.StartPoint = start;
                lgb.EndPoint = end;
                lgb.GradientStops[0].Color = c1;
                lgb.GradientStops[1].Color = c2;
                return;
            }
            catch
            {
            }
        }

        var created = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        created.GradientStops.Add(new GradientStop { Color = c1, Offset = 0.0 });
        created.GradientStops.Add(new GradientStop { Color = c2, Offset = 1.0 });
        dict[key] = created;
    }

    public static void SetOrUpdateSolidBrush(ResourceDictionary dict, string key, Color color)
    {
        if (dict.TryGetValue(key, out var existing) && existing is SolidColorBrush brush)
        {
            try
            {
                if (brush.Color != color)
                {
                    brush.Color = color;
                }
                return;
            }
            catch
            {
            }
        }
        dict[key] = new SolidColorBrush(color);
    }

    // ==================== 智能图片取色 & 封面多色流光提取 ====================

    /// <summary>
    /// 从当前歌曲封面提取 3 个层次分明的流光色彩（供 CoverFlow 动态呼吸背景平滑过渡）
    /// </summary>
    public async Task<(Color C1, Color C2, Color C3)?> ExtractCoverFlowTriadAsync(string? imageSource)
    {
        byte[]? bytes = await LoadImageBytesAsync(imageSource ?? string.Empty);
        if (bytes == null || bytes.Length == 0) return null;

        try
        {
            using var ms = new MemoryStream(bytes);
            var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
            var transform = new BitmapTransform
            {
                ScaledWidth = 36,
                ScaledHeight = 36,
                InterpolationMode = BitmapInterpolationMode.Linear
            };
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Rgba8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            var raw = pixelData.DetachPixelData();
            double[] bucketWeight = new double[12];
            double[] bucketR = new double[12];
            double[] bucketG = new double[12];
            double[] bucketB = new double[12];

            for (int i = 0; i + 3 < raw.Length; i += 4)
            {
                byte r = raw[i], g = raw[i + 1], b = raw[i + 2];
                var (h, s, l) = RgbToHsl(r, g, b);
                if (s >= 0.14 && l >= 0.12 && l <= 0.88)
                {
                    int idx = Math.Clamp((int)(h / 30.0), 0, 11);
                    double w = (s + 0.15) * (1.0 - Math.Abs(l - 0.5) * 0.8);
                    bucketWeight[idx] += w;
                    bucketR[idx] += r * w;
                    bucketG[idx] += g * w;
                    bucketB[idx] += b * w;
                }
            }

            var ranked = Enumerable.Range(0, 12)
                .Where(b => bucketWeight[b] > 0.01)
                .OrderByDescending(b => bucketWeight[b])
                .ToList();

            if (ranked.Count == 0)
            {
                return (Color.FromArgb(255, 16, 185, 129), Color.FromArgb(255, 59, 130, 246), Color.FromArgb(255, 168, 85, 247));
            }

            Color GetBucketVivid(int b)
            {
                byte r = (byte)Math.Clamp(bucketR[b] / bucketWeight[b], 0, 255);
                byte g = (byte)Math.Clamp(bucketG[b] / bucketWeight[b], 0, 255);
                byte bl = (byte)Math.Clamp(bucketB[b] / bucketWeight[b], 0, 255);
                var (h, s, l) = RgbToHsl(r, g, bl);
                return HslToColor(h, Math.Clamp(s, 0.58, 0.92), Math.Clamp(l, 0.44, 0.62));
            }

            var c1 = GetBucketVivid(ranked[0]);
            var (h1, s1, l1) = RgbToHsl(c1.R, c1.G, c1.B);

            Color c2 = ranked.Count >= 2
                ? GetBucketVivid(ranked[1])
                : HslToColor((h1 + 48) % 360, s1, Math.Clamp(l1 - 0.04, 0.36, 0.60));

            Color c3 = ranked.Count >= 3
                ? GetBucketVivid(ranked[2])
                : HslToColor((h1 + 310) % 360, Math.Clamp(s1 * 0.9, 0.45, 0.88), Math.Clamp(l1 + 0.05, 0.40, 0.65));

            return (c1, c2, c3);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从本地图片路径或远程歌曲封面 URL 智能提取配色，生成协调的完整主题方案
    /// </summary>
    public async Task<CustomThemeProfile?> ExtractThemeFromImageAsync(string imageSource, string? themeName = null, bool useAsWallpaper = false)
    {
        byte[]? bytes = await LoadImageBytesAsync(imageSource);
        if (bytes == null || bytes.Length == 0) return null;

        using var ms = new MemoryStream(bytes);
        var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        var transform = new BitmapTransform
        {
            ScaledWidth = 48,
            ScaledHeight = 48,
            InterpolationMode = BitmapInterpolationMode.Linear
        };
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        var raw = pixelData.DetachPixelData();

        double[] bucketWeight = new double[12];
        double[] bucketSumR = new double[12];
        double[] bucketSumG = new double[12];
        double[] bucketSumB = new double[12];
        long totalR = 0, totalG = 0, totalB = 0, totalCount = 0;

        for (int i = 0; i + 3 < raw.Length; i += 4)
        {
            byte r = raw[i], g = raw[i + 1], b = raw[i + 2];
            totalR += r;
            totalG += g;
            totalB += b;
            totalCount++;

            var (h, s, l) = RgbToHsl(r, g, b);
            if (s >= 0.18 && l >= 0.15 && l <= 0.85)
            {
                int bucket = Math.Clamp((int)(h / 30.0), 0, 11);
                double weight = s * (1.0 - Math.Abs(l - 0.52));
                bucketWeight[bucket] += weight;
                bucketSumR[bucket] += r * weight;
                bucketSumG[bucket] += g * weight;
                bucketSumB[bucket] += b * weight;
            }
        }

        if (totalCount == 0) return null;

        Color avgColor = Color.FromArgb(255,
            (byte)(totalR / totalCount),
            (byte)(totalG / totalCount),
            (byte)(totalB / totalCount));

        var ranked = Enumerable.Range(0, 12).OrderByDescending(b => bucketWeight[b]).ToList();
        int bestBucket = ranked[0];
        double maxWeight = bucketWeight[bestBucket];

        Color accent;
        Color secondAccent;
        if (maxWeight > 0.01)
        {
            byte ar = (byte)Math.Clamp(bucketSumR[bestBucket] / maxWeight, 0, 255);
            byte ag = (byte)Math.Clamp(bucketSumG[bestBucket] / maxWeight, 0, 255);
            byte ab = (byte)Math.Clamp(bucketSumB[bestBucket] / maxWeight, 0, 255);
            var (ah, asat, al) = RgbToHsl(ar, ag, ab);
            accent = HslToColor(ah, Math.Clamp(asat, 0.55, 0.92), Math.Clamp(al, 0.46, 0.62));

            int secBucket = ranked[1];
            if (bucketWeight[secBucket] > 0.01)
            {
                byte sr = (byte)Math.Clamp(bucketSumR[secBucket] / bucketWeight[secBucket], 0, 255);
                byte sg = (byte)Math.Clamp(bucketSumG[secBucket] / bucketWeight[secBucket], 0, 255);
                byte sb = (byte)Math.Clamp(bucketSumB[secBucket] / bucketWeight[secBucket], 0, 255);
                var (sh, ssat, sl) = RgbToHsl(sr, sg, sb);
                secondAccent = HslToColor(sh, Math.Clamp(ssat, 0.55, 0.90), Math.Clamp(sl, 0.46, 0.64));
            }
            else
            {
                secondAccent = HslToColor((ah + 40) % 360, Math.Clamp(asat, 0.55, 0.90), Math.Clamp(al, 0.48, 0.64));
            }
        }
        else
        {
            accent = Color.FromArgb(255, 16, 185, 129);
            secondAccent = Color.FromArgb(255, 6, 182, 212);
        }

        var (bgH, bgS, _) = RgbToHsl(accent.R, accent.G, accent.B);
        double avgLum = (avgColor.R * 299 + avgColor.G * 587 + avgColor.B * 114) / 1000.0;
        bool preferLight = !useAsWallpaper && avgLum > 195;

        string baseMode = preferLight ? "Light" : "Dark";
        Color winBg = preferLight
            ? HslToColor(bgH, Math.Min(bgS * 0.25, 0.20), 0.96)
            : HslToColor(bgH, Math.Min(bgS * 0.35, 0.28), 0.08);
        Color navBg = preferLight
            ? HslToColor(bgH, Math.Min(bgS * 0.28, 0.22), 0.93)
            : HslToColor(bgH, Math.Min(bgS * 0.35, 0.30), 0.11);
        Color cardBg = preferLight
            ? Color.FromArgb(255, 255, 255, 255)
            : HslToColor(bgH, Math.Min(bgS * 0.32, 0.28), 0.15);
        Color cardBorder = preferLight
            ? HslToColor(bgH, Math.Min(bgS * 0.35, 0.30), 0.86)
            : HslToColor(bgH, Math.Min(bgS * 0.32, 0.28), 0.24);
        Color playBarBg = preferLight
            ? HslToColor(bgH, Math.Min(bgS * 0.30, 0.24), 0.94)
            : HslToColor(bgH, Math.Min(bgS * 0.38, 0.30), 0.10);

        string localWallPath = string.Empty;
        if (useAsWallpaper && File.Exists(imageSource))
        {
            localWallPath = await CopyWallpaperToAppDataAsync(imageSource);
        }

        return new CustomThemeProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(themeName) ? "灵感取色主题" : themeName,
            Author = "智能取色",
            Description = useAsWallpaper ? "由自定义壁纸智能取色生成" : "由歌曲封面智能提取生成动态极光与双渐变主题",
            IsBuiltIn = false,
            BaseMode = baseMode,
            BackdropType = !string.IsNullOrEmpty(localWallPath) ? "Image" : "Aurora",
            WindowBgColorHex = ToHex(winBg),
            BackgroundImagePath = localWallPath,
            BackgroundBlur = useAsWallpaper ? 0.0 : 10.0,
            BackgroundMaskColorHex = ToHex(winBg),
            BackgroundMaskOpacity = useAsWallpaper ? 0.0 : (preferLight ? 42.0 : 52.0),
            EnableAdaptiveWallpaperText = false,
            AuroraColor1Hex = ToHex(accent),
            AuroraColor2Hex = ToHex(secondAccent),
            AuroraColor3Hex = ToHex(HslToColor((bgH + 290) % 360, 0.68, 0.52)),
            AuroraSpeed = 0.95,
            AuroraIntensity = 68,
            AccentColorHex = ToHex(accent),
            EnableAccentGradient = true,
            AccentGradientEndHex = ToHex(secondAccent),

            EnableCustomTitleBar = !useAsWallpaper && preferLight,
            TitleBarBgHex = ToHex(navBg),
            TitleBarOpacity = useAsWallpaper ? 0.0 : 72.0,

            EnableCustomNavPane = false,
            NavPaneBgHex = ToHex(navBg),
            NavPaneOpacity = useAsWallpaper ? 0.0 : 78.0,

            EnableCustomContent = false,
            ContentBgHex = ToHex(winBg),
            ContentOpacity = 0.0,
            CardBgHex = ToHex(cardBg),
            CardOpacity = useAsWallpaper ? 0.0 : 76.0,
            CardBorderHex = ToHex(cardBorder),
            CardCornerRadius = useAsWallpaper ? 16.0 : 12.0,
            ButtonCornerRadius = useAsWallpaper ? 12.0 : 8.0,

            EnableCustomPlayBar = false,
            PlayBarBgHex = ToHex(playBarBg),
            PlayBarOpacity = useAsWallpaper ? 0.0 : 92.0,
            PlayBarBorderHex = ToHex(cardBorder),

            PlayBarLayoutMode = "FloatingIsland",
            CardVisualStyle = useAsWallpaper ? "LiquidGlass" : "FrostedGlass",
            EnablePlayBarRhythmGlow = true,
            SyncMiniPlayer = true,
            SyncImmersiveLyricHighlight = true
        };
    }

    /// <summary>
    /// 复制用户选择的静态图片或动态视频壁纸到应用数据目录（完全兼容中文路径）
    /// </summary>
    public async Task<string> CopyWallpaperToAppDataAsync(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return string.Empty;

        var fullSource = Path.GetFullPath(sourcePath);
        var fullWallDir = Path.GetFullPath(_wallpaperDir);
        if (fullSource.StartsWith(fullWallDir, StringComparison.OrdinalIgnoreCase))
            return fullSource;

        string ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".png";
        string prefix = ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)
            ? "video"
            : "wall";
        string destPath = Path.Combine(_wallpaperDir, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}{ext}");
        await Task.Run(() => File.Copy(sourcePath, destPath, overwrite: true));
        return destPath;
    }

    // ==================== 主题市场服务（官方 GitHub 仓库 + 本地创意精选 + 外部订阅源合并） ====================

    /// <summary>
    /// 同步瞬时返回当前已缓存远程条目与内置精选的合并列表（用于主题市场零等待首屏展示）
    /// </summary>
    public List<MarketplaceThemeItem> GetInstantMarketplaceItems()
    {
        return MergeMarketplaceItems(_cachedRemoteMarketItems);
    }

    private static List<MarketplaceThemeItem> MergeMarketplaceItems(List<MarketplaceThemeItem>? cachedRemote)
    {
        var result = new List<MarketplaceThemeItem>();
        if (cachedRemote != null)
        {
            foreach (var remoteItem in cachedRemote)
            {
                if (!result.Any(x => string.Equals(x.Id, remoteItem.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(remoteItem);
                }
            }
        }

        foreach (var curated in ThemeMarketplaceCatalog.GetCuratedMarketItems())
        {
            if (!result.Any(x => string.Equals(x.Id, curated.Id, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(curated);
            }
        }

        return result;
    }

    /// <summary>
    /// 获取主题市场列表（优先展示内置官方主题仓库与外部订阅源拉取的主题，并合并内置创意精选）
    /// </summary>
    public async Task<List<MarketplaceThemeItem>> GetMarketplaceItemsAsync(bool forceRefreshRemote = false)
    {
        var feedUrls = new List<string>();
        if (_config.Settings.ThemeCenter.UseOfficialMarketRepo)
        {
            feedUrls.Add(OfficialMarketFeedUrl);
        }
        foreach (var raw in _config.Settings.ThemeCenter.MarketFeedUrls)
        {
            string u = raw?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(u) &&
                u.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                !feedUrls.Any(x => string.Equals(x, u, StringComparison.OrdinalIgnoreCase)))
            {
                feedUrls.Add(u);
            }
        }

        if (feedUrls.Count > 0 && (forceRefreshRemote || _cachedRemoteMarketItems == null))
        {
            var (remoteItems, succeededFeeds, firstErr) = await FetchRemoteFeedsAsync(feedUrls, bustCache: forceRefreshRemote);
            if (succeededFeeds > 0)
            {
                _cachedRemoteMarketItems = remoteItems;
                LastRemoteFeedCount = remoteItems.Count;
                LastRemoteRefreshError = null;
            }
            else
            {
                _cachedRemoteMarketItems ??= [];
                LastRemoteFeedCount = _cachedRemoteMarketItems.Count;
                LastRemoteRefreshError = firstErr ?? "无法连接到 GitHub 主题仓库及镜像线路";
            }
        }
        else if (feedUrls.Count == 0)
        {
            _cachedRemoteMarketItems = [];
            LastRemoteFeedCount = 0;
            LastRemoteRefreshError = null;
        }

        return MergeMarketplaceItems(_cachedRemoteMarketItems);
    }

    private static bool ShouldUseGitHubMirrors(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        string host = uri.Host;
        return host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string AppendCacheBuster(string url)
    {
        string sep = url.Contains('?') ? "&" : "?";
        return $"{url}{sep}t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    private static string ResolveFeedRelativeUrl(string feedUrlWithoutQuery, string? candidateUrl)
    {
        if (string.IsNullOrWhiteSpace(candidateUrl)) return string.Empty;
        string raw = candidateUrl.Trim();
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return raw;
        }

        if (Uri.TryCreate(feedUrlWithoutQuery, UriKind.Absolute, out var baseUri) &&
            Uri.TryCreate(baseUri, raw.TrimStart('/').Replace('\\', '/'), out var resolved))
        {
            return resolved.AbsoluteUri;
        }
        return raw;
    }

    private static string StripQueryFromUrl(string url)
    {
        int qIdx = url.IndexOf('?');
        return qIdx >= 0 ? url[..qIdx] : url;
    }

    private static string MakeSafeMarketFileId(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(id.Length);
        foreach (char c in id)
        {
            sb.Append(invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c);
        }
        string clean = sb.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(clean) ? Guid.NewGuid().ToString("N")[..8] : clean;
    }

    private static string ExtractExtensionFromUrl(string url, string fallbackExt)
    {
        string clean = StripQueryFromUrl(url);
        if (Uri.TryCreate(clean, UriKind.Absolute, out var uri))
        {
            string ext = Path.GetExtension(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(ext) && ext.Length <= 8) return ext.ToLowerInvariant();
        }
        else
        {
            string ext = Path.GetExtension(clean);
            if (!string.IsNullOrWhiteSpace(ext) && ext.Length <= 8) return ext.ToLowerInvariant();
        }
        return fallbackExt;
    }

    private static async Task<(string? Body, string? LastError)> FetchStringWithMirrorsAsync(
        string url, bool bustCache, CancellationToken token)
    {
        string targetUrl = bustCache ? AppendCacheBuster(url) : url;
        string[] prefixes = ShouldUseGitHubMirrors(targetUrl) ? GitHubMirrorPrefixes : [""];
        string? lastErr = null;

        foreach (string prefix in prefixes)
        {
            token.ThrowIfCancellationRequested();
            string reqUrl = prefix + targetUrl;
            int timeoutSec = string.IsNullOrEmpty(prefix) ? 6 : 12;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                using var resp = await MarketHttp.GetAsync(reqUrl, HttpCompletionOption.ResponseContentRead, cts.Token);
                resp.EnsureSuccessStatusCode();
                string text = await resp.Content.ReadAsStringAsync(cts.Token);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return (text, null);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastErr = ex.Message;
            }
        }
        return (null, lastErr);
    }

    /// <summary>
    /// 通过多线路 GitHub 镜像加速拉取主题预览缩略图字节并做内存缓存
    /// </summary>
    public async Task<byte[]?> DownloadPreviewBytesAsync(string url, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return null;

        if (PreviewBytesCache.TryGetValue(url, out var cached))
            return cached;

        string[] prefixes = ShouldUseGitHubMirrors(url) ? GitHubMirrorPrefixes : [""];
        foreach (string prefix in prefixes)
        {
            token.ThrowIfCancellationRequested();
            string reqUrl = prefix + url;
            int timeoutSec = string.IsNullOrEmpty(prefix) ? 6 : 12;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                using var resp = await MarketHttp.GetAsync(reqUrl, HttpCompletionOption.ResponseContentRead, cts.Token);
                resp.EnsureSuccessStatusCode();
                byte[] bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
                if (bytes.Length >= 64)
                {
                    if (PreviewBytesCache.Count >= 64)
                    {
                        PreviewBytesCache.Clear();
                    }
                    PreviewBytesCache[url] = bytes;
                    return bytes;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }
        return null;
    }

    private static async Task DownloadFileWithMirrorsAsync(
        string url,
        string destPath,
        Action<long, long, string>? progress,
        CancellationToken token)
    {
        string? dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string[] prefixes = ShouldUseGitHubMirrors(url) ? GitHubMirrorPrefixes : [""];
        Exception? lastErr = null;

        foreach (string prefix in prefixes)
        {
            token.ThrowIfCancellationRequested();
            string reqUrl = prefix + url;
            string lineName = string.IsNullOrEmpty(prefix) ? "GitHub 直连" : new Uri(prefix).Host;
            string tempPath = $"{destPath}.{Guid.NewGuid().ToString("N")[..6]}.downloading";

            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                using var resp = await MarketHttp.GetAsync(reqUrl, HttpCompletionOption.ResponseHeadersRead, connectCts.Token);
                resp.EnsureSuccessStatusCode();

                string? mediaType = resp.Content.Headers.ContentType?.MediaType;
                if (!string.IsNullOrWhiteSpace(mediaType) &&
                    mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"{lineName} 返回了 HTML 错误页面");
                }

                long total = resp.Content.Headers.ContentLength ?? -1;
                await using (var src = await resp.Content.ReadAsStreamAsync(token))
                await using (var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var buffer = new byte[65536];
                    long done = 0;
                    var sw = Stopwatch.StartNew();
                    while (true)
                    {
                        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        stallCts.CancelAfter(TimeSpan.FromSeconds(20));
                        int read;
                        try
                        {
                            read = await src.ReadAsync(buffer, stallCts.Token);
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            throw new TimeoutException($"{lineName} 下载停滞");
                        }
                        if (read == 0) break;

                        await dst.WriteAsync(buffer.AsMemory(0, read), token);
                        done += read;
                        if (sw.ElapsedMilliseconds >= 120)
                        {
                            progress?.Invoke(done, total, lineName);
                            sw.Restart();
                        }
                    }

                    if (done <= 0 || (total > 0 && done != total))
                    {
                        throw new InvalidOperationException($"{lineName} 下载不完整 ({done}/{total})");
                    }
                    progress?.Invoke(done, total, lineName);
                }

                File.Move(tempPath, destPath, overwrite: true);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                lastErr = ex;
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        throw new InvalidOperationException($"壁纸资源下载失败（{lastErr?.Message ?? "所有线路均不可用"}）");
    }

    private static async Task<(List<MarketplaceThemeItem> Items, int SucceededFeeds, string? FirstError)> FetchRemoteFeedsAsync(
        IReadOnlyList<string> urls, bool bustCache)
    {
        var collected = new List<MarketplaceThemeItem>();
        int succeededFeeds = 0;
        string? firstErr = null;

        foreach (var rawUrl in urls)
        {
            string url = rawUrl.Trim();
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var (json, fetchErr) = await FetchStringWithMirrorsAsync(url, bustCache, CancellationToken.None);
                if (string.IsNullOrWhiteSpace(json))
                {
                    firstErr ??= fetchErr;
                    continue;
                }

                string feedBaseUrl = StripQueryFromUrl(url);
                void NormalizeItem(MarketplaceThemeItem item)
                {
                    if (string.IsNullOrWhiteSpace(item.Id))
                    {
                        item.Id = string.IsNullOrWhiteSpace(item.Theme.Id)
                            ? Guid.NewGuid().ToString("N")[..12]
                            : item.Theme.Id;
                    }
                    item.Theme.Id = string.IsNullOrWhiteSpace(item.Theme.Id) ? item.Id : item.Theme.Id;
                    item.Theme.MarketOriginId = item.Id;

                    // 将相对仓库路径解析为完整 URL
                    item.WallpaperUrl = ResolveFeedRelativeUrl(feedBaseUrl, item.WallpaperUrl);
                    item.VideoUrl = ResolveFeedRelativeUrl(feedBaseUrl, item.VideoUrl);
                    item.PreviewImageUrl = ResolveFeedRelativeUrl(feedBaseUrl, item.PreviewImageUrl);

                    // 兼容直接写在 BackgroundImagePath / BackgroundVideoPath 里的远程/相对地址
                    if (string.IsNullOrWhiteSpace(item.WallpaperUrl) &&
                        !string.IsNullOrWhiteSpace(item.Theme.BackgroundImagePath) &&
                        !Path.IsPathRooted(item.Theme.BackgroundImagePath))
                    {
                        item.WallpaperUrl = ResolveFeedRelativeUrl(feedBaseUrl, item.Theme.BackgroundImagePath);
                        item.Theme.BackgroundImagePath = string.Empty;
                    }
                    if (string.IsNullOrWhiteSpace(item.VideoUrl) &&
                        !string.IsNullOrWhiteSpace(item.Theme.BackgroundVideoPath) &&
                        !Path.IsPathRooted(item.Theme.BackgroundVideoPath))
                    {
                        item.VideoUrl = ResolveFeedRelativeUrl(feedBaseUrl, item.Theme.BackgroundVideoPath);
                        item.Theme.BackgroundVideoPath = string.Empty;
                    }
                }

                string trimmed = json.TrimStart();
                if (trimmed.StartsWith('['))
                {
                    var arr = JsonSerializer.Deserialize<List<MarketplaceThemeItem>>(json, ExportJsonOptions);
                    if (arr != null)
                    {
                        succeededFeeds++;
                        foreach (var item in arr)
                        {
                            if (item?.Theme == null) continue;
                            NormalizeItem(item);
                            collected.Add(item);
                        }
                    }
                }
                else
                {
                    // 优先尝试解析为 MarketplaceFeedManifest，其次支持直接指向单个 .algertheme.json 主题链接
                    var manifest = JsonSerializer.Deserialize<MarketplaceFeedManifest>(json, ExportJsonOptions);
                    if (manifest?.Items != null && (manifest.Items.Count > 0 || !string.IsNullOrWhiteSpace(manifest.FeedName)))
                    {
                        succeededFeeds++;
                        foreach (var item in manifest.Items)
                        {
                            if (item?.Theme == null) continue;
                            if (string.IsNullOrWhiteSpace(item.SourceName))
                                item.SourceName = manifest.FeedName;
                            NormalizeItem(item);
                            collected.Add(item);
                        }
                    }
                    else
                    {
                        var singleTheme = JsonSerializer.Deserialize<CustomThemeProfile>(json, ExportJsonOptions);
                        if (singleTheme != null && !string.IsNullOrWhiteSpace(singleTheme.Name))
                        {
                            succeededFeeds++;
                            string id = string.IsNullOrWhiteSpace(singleTheme.Id) ? Guid.NewGuid().ToString("N") : singleTheme.Id;
                            singleTheme.MarketOriginId = id;
                            var item = new MarketplaceThemeItem
                            {
                                Id = id,
                                Category = singleTheme.HasDynamicEffect ? "动态极光" : "社区订阅",
                                SourceName = "远程订阅",
                                Tags = ["在线订阅", singleTheme.BackdropType],
                                Downloads = 100,
                                Theme = singleTheme
                            };
                            NormalizeItem(item);
                            collected.Add(item);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                firstErr ??= ex.Message;
            }
        }
        return (collected, succeededFeeds, firstErr);
    }

    /// <summary>
    /// 确保在线市场主题关联的远程壁纸/视频/内嵌图片已下载落盘到本地壁纸目录
    /// </summary>
    public async Task EnsureMarketThemeAssetsAsync(
        MarketplaceThemeItem marketItem,
        CustomThemeProfile targetProfile,
        bool forceRedownload = false,
        Action<long, long, string>? progress = null,
        CancellationToken token = default)
    {
        string safeId = MakeSafeMarketFileId(string.IsNullOrWhiteSpace(marketItem.Id) ? targetProfile.Id : marketItem.Id);

        if (!string.IsNullOrWhiteSpace(marketItem.WallpaperUrl))
        {
            string ext = ExtractExtensionFromUrl(marketItem.WallpaperUrl, ".jpg");
            string destPath = Path.Combine(_wallpaperDir, $"market_{safeId}{ext}");
            if (forceRedownload || !File.Exists(destPath) || new FileInfo(destPath).Length == 0)
            {
                await DownloadFileWithMirrorsAsync(marketItem.WallpaperUrl, destPath, progress, token);
            }
            targetProfile.BackgroundImagePath = destPath;
            targetProfile.BackdropType = "Image";
        }
        else if (!string.IsNullOrWhiteSpace(targetProfile.EmbeddedImageBase64))
        {
            byte[] imgBytes = Convert.FromBase64String(targetProfile.EmbeddedImageBase64);
            string ext = string.IsNullOrWhiteSpace(targetProfile.EmbeddedImageExt) ? ".png" : targetProfile.EmbeddedImageExt!;
            if (!ext.StartsWith('.')) ext = "." + ext;
            string destPath = Path.Combine(_wallpaperDir, $"market_{safeId}{ext.ToLowerInvariant()}");
            await File.WriteAllBytesAsync(destPath, imgBytes, token);
            targetProfile.BackgroundImagePath = destPath;
            targetProfile.BackdropType = "Image";
        }

        if (!string.IsNullOrWhiteSpace(marketItem.VideoUrl))
        {
            string ext = ExtractExtensionFromUrl(marketItem.VideoUrl, ".mp4");
            string destPath = Path.Combine(_wallpaperDir, $"market_video_{safeId}{ext}");
            if (forceRedownload || !File.Exists(destPath) || new FileInfo(destPath).Length == 0)
            {
                await DownloadFileWithMirrorsAsync(marketItem.VideoUrl, destPath, progress, token);
            }
            targetProfile.BackgroundVideoPath = destPath;
            targetProfile.BackdropType = "Video";
        }

        targetProfile.EmbeddedImageBase64 = null;
        targetProfile.EmbeddedImageExt = null;
    }

    /// <summary>
    /// 从主题市场一键下载资源、安装并激活指定主题（若已安装同源主题则重新下载更新）
    /// </summary>
    public async Task<CustomThemeProfile> InstallMarketplaceThemeAsync(
        MarketplaceThemeItem marketItem,
        Action<long, long, string>? progress = null,
        CancellationToken token = default)
    {
        var list = _config.Settings.ThemeCenter.CustomThemes;
        var existing = list.FirstOrDefault(x =>
            (!string.IsNullOrWhiteSpace(x.MarketOriginId) && string.Equals(x.MarketOriginId, marketItem.Id, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(x.Id, marketItem.Id, StringComparison.OrdinalIgnoreCase));

        var installCopy = marketItem.Theme.Clone();
        installCopy.IsBuiltIn = false;
        installCopy.MarketOriginId = marketItem.Id;
        installCopy.Id = existing?.Id ?? (string.IsNullOrWhiteSpace(marketItem.Id) ? Guid.NewGuid().ToString("N") : marketItem.Id);
        installCopy.CreatedAt = DateTime.Now;

        await EnsureMarketThemeAssetsAsync(marketItem, installCopy, forceRedownload: existing != null, progress, token);
        return await SaveCustomThemeAsync(installCopy);
    }

    public bool IsMarketplaceThemeInstalled(string marketId)
    {
        return _config.Settings.ThemeCenter.CustomThemes.Any(x =>
            string.Equals(x.MarketOriginId, marketId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.Id, marketId, StringComparison.OrdinalIgnoreCase));
    }

    // ==================== 一键主题分享口令 (ALGER-THEME://...) ====================

    /// <summary>
    /// 将任意主题配置压缩编码为紧凑的一键分享码口令
    /// </summary>
    public string GenerateShareCode(CustomThemeProfile profile)
    {
        var clean = profile.Clone();
        clean.IsBuiltIn = false;
        clean.BackgroundImagePath = string.Empty;
        clean.BackgroundVideoPath = string.Empty;
        clean.EmbeddedImageBase64 = null;
        clean.EmbeddedImageExt = null;
        if (clean.BackdropType is "Image" or "Video")
        {
            clean.BackdropType = "Aurora";
        }

        string json = JsonSerializer.Serialize(clean, CompactShareJsonOptions);
        byte[] rawBytes = Encoding.UTF8.GetBytes(json);
        using var outMs = new MemoryStream();
        using (var gz = new GZipStream(outMs, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gz.Write(rawBytes, 0, rawBytes.Length);
        }
        return ShareCodePrefix + Convert.ToBase64String(outMs.ToArray());
    }

    /// <summary>
    /// 解析主题分享码口令（或原始 JSON 文本）并保存激活
    /// </summary>
    public async Task<CustomThemeProfile?> ImportFromShareCodeAsync(string rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput)) return null;
        string text = rawInput.Trim();

        try
        {
            string json;
            int prefixIdx = text.IndexOf(ShareCodePrefix, StringComparison.OrdinalIgnoreCase);
            if (prefixIdx >= 0)
            {
                string b64 = text[(prefixIdx + ShareCodePrefix.Length)..].Trim();
                // 去除可能夹杂的换行或引号
                b64 = b64.Split([' ', '\r', '\n', '\t', '"', '\''], StringSplitOptions.RemoveEmptyEntries)[0];
                byte[] compressed = Convert.FromBase64String(b64);
                using var inMs = new MemoryStream(compressed);
                using var gz = new GZipStream(inMs, CompressionMode.Decompress);
                using var sr = new StreamReader(gz, Encoding.UTF8);
                json = await sr.ReadToEndAsync();
            }
            else if (text.StartsWith('{'))
            {
                json = text;
            }
            else
            {
                return null;
            }

            var parsed = JsonSerializer.Deserialize<CustomThemeProfile>(json, ExportJsonOptions);
            if (parsed == null) return null;

            parsed.Id = Guid.NewGuid().ToString("N");
            parsed.IsBuiltIn = false;
            parsed.CreatedAt = DateTime.Now;
            if (string.IsNullOrWhiteSpace(parsed.Name))
            {
                parsed.Name = "口令导入主题";
            }

            return await SaveCustomThemeAsync(parsed);
        }
        catch
        {
            return null;
        }
    }

    // ==================== 背景壁纸软件高斯模糊渲染 ====================

    public async Task<ImageSource?> CreateWallpaperImageSourceAsync(string filePath, double blurRadius)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        int radius = (int)Math.Round(Math.Clamp(blurRadius, 0, 28));
        if (radius <= 0)
        {
            var bmp = new BitmapImage();
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await bmp.SetSourceAsync(fs.AsRandomAccessStream());
            return bmp;
        }

        string cacheKey = $"{filePath}|{File.GetLastWriteTimeUtc(filePath).Ticks}|{radius}";
        if (_cachedBlurKey != cacheKey || _cachedBlurBgraPixels == null)
        {
            var decoded = await DecodeAndBlurBackgroundAsync(filePath, radius);
            if (decoded == null) return null;
            _cachedBlurKey = cacheKey;
            _cachedBlurBgraPixels = decoded.Value.Pixels;
            _cachedBlurWidth = decoded.Value.Width;
            _cachedBlurHeight = decoded.Value.Height;
        }

        var wb = new WriteableBitmap(_cachedBlurWidth, _cachedBlurHeight);
        using (var stream = wb.PixelBuffer.AsStream())
        {
            await stream.WriteAsync(_cachedBlurBgraPixels, 0, _cachedBlurBgraPixels.Length);
        }
        wb.Invalidate();
        return wb;
    }

    /// <summary>
    /// 提取或从缓存获取自定义壁纸的高精度局部色彩/亮度二维采样网格（96x54，直接复用模糊后像素缓冲区）
    /// </summary>
    public async Task<WallpaperColorGrid?> GetOrBuildWallpaperColorGridAsync(string filePath, double blurRadius)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        int radius = (int)Math.Round(Math.Clamp(blurRadius, 0, 28));
        string cacheKey = $"{filePath}|{File.GetLastWriteTimeUtc(filePath).Ticks}|{radius}";
        if (_cachedWallGridKey == cacheKey && _cachedWallGrid != null)
            return _cachedWallGrid;

        try
        {
            byte[]? bgraPixels = null;
            int srcW = 0;
            int srcH = 0;

            if (radius > 0 && _cachedBlurKey == cacheKey && _cachedBlurBgraPixels != null)
            {
                bgraPixels = _cachedBlurBgraPixels;
                srcW = _cachedBlurWidth;
                srcH = _cachedBlurHeight;
            }
            else if (radius > 0)
            {
                var blurred = await DecodeAndBlurBackgroundAsync(filePath, radius);
                if (blurred != null)
                {
                    _cachedBlurKey = cacheKey;
                    _cachedBlurBgraPixels = blurred.Value.Pixels;
                    _cachedBlurWidth = blurred.Value.Width;
                    _cachedBlurHeight = blurred.Value.Height;
                    bgraPixels = blurred.Value.Pixels;
                    srcW = blurred.Value.Width;
                    srcH = blurred.Value.Height;
                }
            }
            else
            {
                byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
                using var ms = new MemoryStream(fileBytes);
                var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
                uint rawW = decoder.PixelWidth;
                uint rawH = decoder.PixelHeight;
                if (rawW == 0 || rawH == 0) return null;

                double scale = Math.Min(1.0, 768.0 / Math.Max(rawW, rawH));
                uint scaledRawW = Math.Max(16, (uint)Math.Round(rawW * scale));
                uint scaledRawH = Math.Max(16, (uint)Math.Round(rawH * scale));
                bool swapped = rawW != decoder.OrientedPixelWidth && rawW == decoder.OrientedPixelHeight;

                var transform = new BitmapTransform
                {
                    ScaledWidth = scaledRawW,
                    ScaledHeight = scaledRawH,
                    InterpolationMode = BitmapInterpolationMode.Linear
                };
                var provider = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                bgraPixels = provider.DetachPixelData();
                srcW = swapped ? (int)scaledRawH : (int)scaledRawW;
                srcH = swapped ? (int)scaledRawW : (int)scaledRawH;
            }

            if (bgraPixels == null || srcW <= 0 || srcH <= 0)
                return null;

            var grid = await Task.Run(() => WallpaperColorGrid.FromBgraBuffer(bgraPixels, srcW, srcH));
            _cachedWallGridKey = cacheKey;
            _cachedWallGrid = grid;
            return grid;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(byte[] Pixels, int Width, int Height)?> DecodeAndBlurBackgroundAsync(string filePath, int blurRadius)
    {
        try
        {
            byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
            using var ms = new MemoryStream(fileBytes);
            var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());

            uint origW = decoder.PixelWidth;
            uint origH = decoder.PixelHeight;
            if (origW == 0 || origH == 0) return null;

            double scale = Math.Min(1.0, 960.0 / Math.Max(origW, origH));
            uint targetW = Math.Max(16, (uint)Math.Round(origW * scale));
            uint targetH = Math.Max(16, (uint)Math.Round(origH * scale));
            bool swapped = origW != decoder.OrientedPixelWidth && origW == decoder.OrientedPixelHeight;

            var transform = new BitmapTransform
            {
                ScaledWidth = targetW,
                ScaledHeight = targetH,
                InterpolationMode = BitmapInterpolationMode.Linear
            };

            var pixelProvider = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);

            byte[] pixels = pixelProvider.DetachPixelData();
            int w = swapped ? (int)targetH : (int)targetW;
            int h = swapped ? (int)targetW : (int)targetH;
            int scaledRadius = Math.Max(1, (int)Math.Round(blurRadius * 1.4));

            await Task.Run(() =>
            {
                byte[] temp = new byte[pixels.Length];
                for (int pass = 0; pass < 3; pass++)
                {
                    BoxBlurHorizontal(pixels, temp, w, h, scaledRadius);
                    BoxBlurVertical(temp, pixels, w, h, scaledRadius);
                }
            });

            return (pixels, w, h);
        }
        catch
        {
            return null;
        }
    }

    private static void BoxBlurHorizontal(byte[] src, byte[] dst, int w, int h, int r)
    {
        double inv = 1.0 / (r + r + 1);
        for (int y = 0; y < h; y++)
        {
            int rowOffset = y * w * 4;
            int sumB = 0, sumG = 0, sumR = 0, sumA = 0;

            for (int ix = -r; ix <= r; ix++)
            {
                int cx = Math.Clamp(ix, 0, w - 1) * 4 + rowOffset;
                sumB += src[cx];
                sumG += src[cx + 1];
                sumR += src[cx + 2];
                sumA += src[cx + 3];
            }

            for (int x = 0; x < w; x++)
            {
                int outIdx = rowOffset + x * 4;
                dst[outIdx] = (byte)(sumB * inv);
                dst[outIdx + 1] = (byte)(sumG * inv);
                dst[outIdx + 2] = (byte)(sumR * inv);
                dst[outIdx + 3] = (byte)(sumA * inv);

                int removeIdx = rowOffset + Math.Clamp(x - r, 0, w - 1) * 4;
                int addIdx = rowOffset + Math.Clamp(x + r + 1, 0, w - 1) * 4;

                sumB += src[addIdx] - src[removeIdx];
                sumG += src[addIdx + 1] - src[removeIdx + 1];
                sumR += src[addIdx + 2] - src[removeIdx + 2];
                sumA += src[addIdx + 3] - src[removeIdx + 3];
            }
        }
    }

    private static void BoxBlurVertical(byte[] src, byte[] dst, int w, int h, int r)
    {
        double inv = 1.0 / (r + r + 1);
        int stride = w * 4;
        for (int x = 0; x < w; x++)
        {
            int colOffset = x * 4;
            int sumB = 0, sumG = 0, sumR = 0, sumA = 0;

            for (int iy = -r; iy <= r; iy++)
            {
                int cy = Math.Clamp(iy, 0, h - 1) * stride + colOffset;
                sumB += src[cy];
                sumG += src[cy + 1];
                sumR += src[cy + 2];
                sumA += src[cy + 3];
            }

            for (int y = 0; y < h; y++)
            {
                int outIdx = y * stride + colOffset;
                dst[outIdx] = (byte)(sumB * inv);
                dst[outIdx + 1] = (byte)(sumG * inv);
                dst[outIdx + 2] = (byte)(sumR * inv);
                dst[outIdx + 3] = (byte)(sumA * inv);

                int removeIdx = Math.Clamp(y - r, 0, h - 1) * stride + colOffset;
                int addIdx = Math.Clamp(y + r + 1, 0, h - 1) * stride + colOffset;

                sumB += src[addIdx] - src[removeIdx];
                sumG += src[addIdx + 1] - src[removeIdx + 1];
                sumR += src[addIdx + 2] - src[removeIdx + 2];
                sumA += src[addIdx + 3] - src[removeIdx + 3];
            }
        }
    }

    // ==================== 主题单文件导入与导出 ====================

    public async Task ExportThemeToFileAsync(CustomThemeProfile profile, string targetFilePath)
    {
        var exportCopy = profile.Clone();
        exportCopy.IsBuiltIn = false;

        if (string.Equals(exportCopy.BackdropType, "Image", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(exportCopy.BackgroundImagePath) &&
            File.Exists(exportCopy.BackgroundImagePath))
        {
            var info = new FileInfo(exportCopy.BackgroundImagePath);
            if (info.Length <= 12 * 1024 * 1024)
            {
                var bytes = await File.ReadAllBytesAsync(exportCopy.BackgroundImagePath);
                exportCopy.EmbeddedImageBase64 = Convert.ToBase64String(bytes);
                exportCopy.EmbeddedImageExt = string.IsNullOrWhiteSpace(info.Extension) ? ".png" : info.Extension;
            }
        }

        string json = JsonSerializer.Serialize(exportCopy, ExportJsonOptions);
        await File.WriteAllTextAsync(targetFilePath, json);
    }

    public async Task<CustomThemeProfile?> ImportThemeFromFileAsync(string sourceFilePath)
    {
        if (!File.Exists(sourceFilePath)) return null;

        string json = await File.ReadAllTextAsync(sourceFilePath);
        var imported = JsonSerializer.Deserialize<CustomThemeProfile>(json, ExportJsonOptions);
        if (imported == null) return null;

        imported.Id = Guid.NewGuid().ToString("N");
        imported.IsBuiltIn = false;
        imported.CreatedAt = DateTime.Now;
        if (string.IsNullOrWhiteSpace(imported.Name))
        {
            imported.Name = Path.GetFileNameWithoutExtension(sourceFilePath);
        }

        if (!string.IsNullOrWhiteSpace(imported.EmbeddedImageBase64))
        {
            try
            {
                byte[] imgBytes = Convert.FromBase64String(imported.EmbeddedImageBase64);
                string ext = string.IsNullOrWhiteSpace(imported.EmbeddedImageExt) ? ".png" : imported.EmbeddedImageExt!;
                if (!ext.StartsWith('.')) ext = "." + ext;
                string wallPath = Path.Combine(_wallpaperDir, $"imported_{imported.Id[..8]}{ext}");
                await File.WriteAllBytesAsync(wallPath, imgBytes);
                imported.BackgroundImagePath = wallPath;
            }
            catch
            {
                if (!File.Exists(imported.BackgroundImagePath))
                {
                    imported.BackdropType = "Aurora";
                }
            }
            imported.EmbeddedImageBase64 = null;
            imported.EmbeddedImageExt = null;
        }
        else if (imported.BackdropType == "Image" && !File.Exists(imported.BackgroundImagePath))
        {
            imported.BackdropType = "Aurora";
        }
        else if (imported.BackdropType == "Video" && !File.Exists(imported.BackgroundVideoPath))
        {
            imported.BackdropType = "Aurora";
        }

        return await SaveCustomThemeAsync(imported);
    }

    // ==================== 静态颜色工具方法 ====================

    private static async Task<byte[]?> LoadImageBytesAsync(string imageSource)
    {
        if (string.IsNullOrWhiteSpace(imageSource)) return null;
        try
        {
            if (imageSource.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return await SharedHttp.GetByteArrayAsync(imageSource);
            }
            if (File.Exists(imageSource))
            {
                return await File.ReadAllBytesAsync(imageSource);
            }
        }
        catch
        {
        }
        return null;
    }

    public static bool TryParseColor(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;

        var s = hex.Trim().TrimStart('#');
        if (s.Length == 6 &&
            byte.TryParse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
            byte.TryParse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
            byte.TryParse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            color = Color.FromArgb(255, r, g, b);
            return true;
        }

        if (s.Length == 8 &&
            byte.TryParse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte a) &&
            byte.TryParse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte ar) &&
            byte.TryParse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte ag) &&
            byte.TryParse(s[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte ab))
        {
            color = Color.FromArgb(a, ar, ag, ab);
            return true;
        }

        return false;
    }

    public static Color ParseColorOrDefault(string? hex, Color fallback)
        => TryParseColor(hex, out var c) ? c : fallback;

    public static Color WithOpacity(Color baseColor, double opacityPercent)
    {
        byte alpha = (byte)Math.Round(Math.Clamp(opacityPercent, 0, 100) * 2.55);
        return Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
    }

    public static string ToHex(Color c, bool includeAlpha = false)
        => includeAlpha
            ? $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}"
            : $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color AdjustLightness(Color c, double deltaL)
    {
        var (h, s, l) = RgbToHsl(c.R, c.G, c.B);
        return HslToColor(h, s, Math.Clamp(l + deltaL, 0.06, 0.94));
    }

    public static Color LerpColor(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return Color.FromArgb(
            (byte)Math.Round(a.A + (b.A - a.A) * t),
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    public static (double H, double S, double L) RgbToHsl(byte rByte, byte gByte, byte bByte)
    {
        double r = rByte / 255.0;
        double g = gByte / 255.0;
        double b = bByte / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double h = 0, s = 0, l = (max + min) / 2.0;

        if (Math.Abs(max - min) > 1e-6)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (Math.Abs(max - r) < 1e-6)
                h = (g - b) / d + (g < b ? 6 : 0);
            else if (Math.Abs(max - g) < 1e-6)
                h = (b - r) / d + 2;
            else
                h = (r - g) / d + 4;
            h *= 60.0;
        }
        return (h, s, l);
    }

    public static Color HslToColor(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = l - c / 2.0;

        double r1, g1, b1;
        if (h < 60) (r1, g1, b1) = (c, x, 0);
        else if (h < 120) (r1, g1, b1) = (x, c, 0);
        else if (h < 180) (r1, g1, b1) = (0, c, x);
        else if (h < 240) (r1, g1, b1) = (0, x, c);
        else if (h < 300) (r1, g1, b1) = (x, 0, c);
        else (r1, g1, b1) = (c, 0, x);

        return Color.FromArgb(
            255,
            (byte)Math.Clamp(Math.Round((r1 + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((g1 + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((b1 + m) * 255), 0, 255));
    }
}

/// <summary>
/// 壁纸超高精度二维色彩/亮度采样网格（192x108 像素积分图 Summed-Area Table，支持 O(1) 任意字符邻域感受野面积积分采样）
/// </summary>
public sealed class WallpaperColorGrid
{
    public const int GridCols = 192;
    public const int GridRows = 108;

    public int ImageWidth { get; }
    public int ImageHeight { get; }
    private readonly byte[] _rgb; // GridCols * GridRows * 3
    private readonly int[] _satR; // (GridCols + 1) * (GridRows + 1) 二维前缀和积分图
    private readonly int[] _satG;
    private readonly int[] _satB;

    private WallpaperColorGrid(int imageWidth, int imageHeight, byte[] rgb, int[] satR, int[] satG, int[] satB)
    {
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        _rgb = rgb;
        _satR = satR;
        _satG = satG;
        _satB = satB;
    }

    public static WallpaperColorGrid FromBgraBuffer(byte[] bgra, int width, int height)
    {
        byte[] rgb = new byte[GridCols * GridRows * 3];
        int satStride = GridCols + 1;
        int satLen = satStride * (GridRows + 1);
        int[] satR = new int[satLen];
        int[] satG = new int[satLen];
        int[] satB = new int[satLen];
        int stride = width * 4;

        for (int gy = 0; gy < GridRows; gy++)
        {
            int yStart = gy * height / GridRows;
            int yEnd = Math.Max(yStart + 1, (gy + 1) * height / GridRows);
            yEnd = Math.Min(yEnd, height);

            int rowSumR = 0, rowSumG = 0, rowSumB = 0;

            for (int gx = 0; gx < GridCols; gx++)
            {
                int xStart = gx * width / GridCols;
                int xEnd = Math.Max(xStart + 1, (gx + 1) * width / GridCols);
                xEnd = Math.Min(xEnd, width);

                long sumR = 0, sumG = 0, sumB = 0;
                int count = 0;

                for (int y = yStart; y < yEnd; y++)
                {
                    int rowBase = y * stride;
                    for (int x = xStart; x < xEnd; x++)
                    {
                        int p = rowBase + x * 4;
                        if (p + 2 < bgra.Length)
                        {
                            sumB += bgra[p];
                            sumG += bgra[p + 1];
                            sumR += bgra[p + 2];
                            count++;
                        }
                    }
                }

                byte cellR = count > 0 ? (byte)(sumR / count) : (byte)18;
                byte cellG = count > 0 ? (byte)(sumG / count) : (byte)24;
                byte cellB = count > 0 ? (byte)(sumB / count) : (byte)38;

                int outIdx = (gy * GridCols + gx) * 3;
                rgb[outIdx] = cellR;
                rgb[outIdx + 1] = cellG;
                rgb[outIdx + 2] = cellB;

                rowSumR += cellR;
                rowSumG += cellG;
                rowSumB += cellB;

                int curSatIdx = (gy + 1) * satStride + (gx + 1);
                int aboveSatIdx = gy * satStride + (gx + 1);
                satR[curSatIdx] = satR[aboveSatIdx] + rowSumR;
                satG[curSatIdx] = satG[aboveSatIdx] + rowSumG;
                satB[curSatIdx] = satB[aboveSatIdx] + rowSumB;
            }
        }

        return new WallpaperColorGrid(width, height, rgb, satR, satG, satB);
    }

    /// <summary>
    /// 基于壁纸 Image 控件实测渲染边界，O(1) 计算任意窗口矩形感受野 [rx0, ry0, rx1, ry1] 内的面积平均 RGB（彻底滤除树叶缝隙/碎花单像素噪点）
    /// </summary>
    public (double R, double G, double B) SampleAreaAverageRgb(
        double rx0,
        double ry0,
        double rx1,
        double ry1,
        double imgRenderLeft,
        double imgRenderTop,
        double imgRenderWidth,
        double imgRenderHeight)
    {
        if (imgRenderWidth <= 1 || imgRenderHeight <= 1 || ImageWidth <= 0 || ImageHeight <= 0)
            return (18, 24, 38);

        double u0 = Math.Clamp((Math.Min(rx0, rx1) - imgRenderLeft) / imgRenderWidth, 0.0, 1.0);
        double u1 = Math.Clamp((Math.Max(rx0, rx1) - imgRenderLeft) / imgRenderWidth, 0.0, 1.0);
        double v0 = Math.Clamp((Math.Min(ry0, ry1) - imgRenderTop) / imgRenderHeight, 0.0, 1.0);
        double v1 = Math.Clamp((Math.Max(ry0, ry1) - imgRenderTop) / imgRenderHeight, 0.0, 1.0);

        int gx0 = Math.Clamp((int)Math.Floor(u0 * GridCols), 0, GridCols - 1);
        int gx1 = Math.Clamp((int)Math.Ceiling(u1 * GridCols), gx0 + 1, GridCols);
        int gy0 = Math.Clamp((int)Math.Floor(v0 * GridRows), 0, GridRows - 1);
        int gy1 = Math.Clamp((int)Math.Ceiling(v1 * GridRows), gy0 + 1, GridRows);

        int area = (gx1 - gx0) * (gy1 - gy0);
        if (area <= 0)
            return SampleNormalizedUv((u0 + u1) * 0.5, (v0 + v1) * 0.5);

        int satStride = GridCols + 1;
        int i11 = gy1 * satStride + gx1;
        int i01 = gy1 * satStride + gx0;
        int i10 = gy0 * satStride + gx1;
        int i00 = gy0 * satStride + gx0;

        double avgR = (_satR[i11] - _satR[i01] - _satR[i10] + _satR[i00]) / (double)area;
        double avgG = (_satG[i11] - _satG[i01] - _satG[i10] + _satG[i00]) / (double)area;
        double avgB = (_satB[i11] - _satB[i01] - _satB[i10] + _satB[i00]) / (double)area;
        return (avgR, avgG, avgB);
    }

    public (double R, double G, double B) SampleAtWindowPoint(double winX, double winY, double winW, double winH)
    {
        if (winW <= 1 || winH <= 1 || ImageWidth <= 0 || ImageHeight <= 0)
            return (18, 24, 38);

        double scale = Math.Max(winW / ImageWidth, winH / ImageHeight);
        double dispW = ImageWidth * scale;
        double dispH = ImageHeight * scale;
        double offsetX = (dispW - winW) * 0.5;
        double offsetY = (dispH - winH) * 0.5;

        double u = Math.Clamp((winX + offsetX) / dispW, 0.0, 1.0);
        double v = Math.Clamp((winY + offsetY) / dispH, 0.0, 1.0);
        return SampleNormalizedUv(u, v);
    }

    private (double R, double G, double B) SampleNormalizedUv(double u, double v)
    {
        double fx = Math.Clamp(u, 0.0, 1.0) * (GridCols - 1);
        double fy = Math.Clamp(v, 0.0, 1.0) * (GridRows - 1);
        int x0 = (int)fx;
        int y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, GridCols - 1);
        int y1 = Math.Min(y0 + 1, GridRows - 1);
        double tx = fx - x0;
        double ty = fy - y0;

        int i00 = (y0 * GridCols + x0) * 3;
        int i10 = (y0 * GridCols + x1) * 3;
        int i01 = (y1 * GridCols + x0) * 3;
        int i11 = (y1 * GridCols + x1) * 3;

        double w00 = (1 - tx) * (1 - ty);
        double w10 = tx * (1 - ty);
        double w01 = (1 - tx) * ty;
        double w11 = tx * ty;

        double r = _rgb[i00] * w00 + _rgb[i10] * w10 + _rgb[i01] * w01 + _rgb[i11] * w11;
        double g = _rgb[i00 + 1] * w00 + _rgb[i10 + 1] * w10 + _rgb[i01 + 1] * w01 + _rgb[i11 + 1] * w11;
        double b = _rgb[i00 + 2] * w00 + _rgb[i10 + 2] * w10 + _rgb[i01 + 2] * w01 + _rgb[i11 + 2] * w11;
        return (r, g, b);
    }
}
