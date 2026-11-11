using System.Text.Json.Serialization;

namespace AlgerMusicPlayer.WinUI.Models;

/// <summary>
/// 单套主题完整配置档案（涵盖动态极光/视频/封面流光、粒子氛围、双色渐变、悬浮岛布局、质感风格、字体与黑胶旋转等全维 DIY）
/// </summary>
public class CustomThemeProfile
{
    // ==================== 元数据 ====================
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "我的自定义主题";
    public string Author { get; set; } = "User";
    public string Description { get; set; } = "个性化自定义主题";
    public bool IsBuiltIn { get; set; } = false;
    public string MarketOriginId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // ==================== 1. 全局基础与动态/静态背景引擎 ====================
    /// <summary>
    /// 明暗基调：Dark | Light | System
    /// </summary>
    public string BaseMode { get; set; } = "Dark";

    /// <summary>
    /// 窗口背景材质/引擎：
    /// Mica | MicaAlt | Acrylic | Solid | Image | Aurora (动态流体极光) | Video (MP4/WebM动态壁纸) | CoverFlow (歌曲封面多色流光呼吸)
    /// </summary>
    public string BackdropType { get; set; } = "Mica";

    /// <summary>
    /// 纯色背景或动态/静态壁纸底层衬色 (#RRGGBB)
    /// </summary>
    public string WindowBgColorHex { get; set; } = "#0F172A";

    /// <summary>
    /// 自定义静态背景壁纸本地路径（当 BackdropType == "Image" 时生效）
    /// </summary>
    public string BackgroundImagePath { get; set; } = string.Empty;

    /// <summary>
    /// 自定义动态视频壁纸本地路径（当 BackdropType == "Video" 时生效，支持 .mp4 / .webm）
    /// </summary>
    public string BackgroundVideoPath { get; set; } = string.Empty;

    /// <summary>
    /// 导出主题包时内嵌的壁纸 Base64 数据（平时本地存储置空，仅导出单文件时写入）
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? EmbeddedImageBase64 { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? EmbeddedImageExt { get; set; }

    /// <summary>
    /// 背景壁纸模糊度 (0 ~ 24)
    /// </summary>
    public double BackgroundBlur { get; set; } = 8.0;

    /// <summary>
    /// 背景遮罩颜色 (#RRGGBB)
    /// </summary>
    public string BackgroundMaskColorHex { get; set; } = "#090D16";

    /// <summary>
    /// 背景遮罩浓度 (0 ~ 95%)
    /// </summary>
    public double BackgroundMaskOpacity { get; set; } = 42.0;

    // ---- 1.1 动态流体极光参数 (Aurora / CoverFlow) ----
    public string AuroraColor1Hex { get; set; } = "#10B981";
    public string AuroraColor2Hex { get; set; } = "#6366F1";
    public string AuroraColor3Hex { get; set; } = "#EC4899";
    /// <summary>
    /// 动态流光游走速率 (0.2 ~ 3.0)
    /// </summary>
    public double AuroraSpeed { get; set; } = 1.0;
    /// <summary>
    /// 极光光斑发光浓度 (20 ~ 100%)
    /// </summary>
    public double AuroraIntensity { get; set; } = 68.0;

    // ---- 1.2 全屏动态粒子氛围层（可独立叠加在任意背景之上） ----
    public bool EnableParticleOverlay { get; set; } = false;
    /// <summary>
    /// 粒子种类：Starlight (繁星闪烁) | Sakura (落樱缤纷) | Firefly (夏夜萤火) | Snow (静谧微雪) | Bubble (律动音泡)
    /// </summary>
    public string ParticleType { get; set; } = "Starlight";
    /// <summary>
    /// 粒子密度数量 (10 ~ 90)
    /// </summary>
    public int ParticleDensity { get; set; } = 36;
    /// <summary>
    /// 粒子飘动速率 (0.3 ~ 2.5)
    /// </summary>
    public double ParticleSpeed { get; set; } = 1.0;
    /// <summary>
    /// 自定义粒子颜色（留空则按粒子种类自然色或主题强调色渲染）
    /// </summary>
    public string ParticleColorHex { get; set; } = string.Empty;

    // ==================== 2. 全局品牌强调色与双色渐变 (Accent & Gradient) ====================
    public string AccentColorHex { get; set; } = "#10B981";
    public bool EnableAccentGradient { get; set; } = false;
    public string AccentGradientEndHex { get; set; } = "#06B6D4";
    /// <summary>
    /// 渐变角度：0 (水平从左到右) | 45 (右上斜向) | 90 (垂直从上到下) | 135 (右下斜向)
    /// </summary>
    public double GradientAngle { get; set; } = 135.0;

    public bool UseCustomAccentStates { get; set; } = false;
    public string AccentHoverHex { get; set; } = "#059669";
    public string AccentPressedHex { get; set; } = "#047857";

    // ==================== 3. 四大分区独立 DIY 配色、渐变与透明度 ====================

    // 3.1 顶部标题栏 (TitleBar)
    public bool EnableCustomTitleBar { get; set; } = false;
    public string TitleBarBgHex { get; set; } = "#111827";
    public bool EnableTitleBarGradient { get; set; } = false;
    public string TitleBarGradientEndHex { get; set; } = "#1E293B";
    public double TitleBarOpacity { get; set; } = 75.0;
    public string TitleBarTextHex { get; set; } = string.Empty;

    // 3.2 左侧导航栏 (Navigation Pane)
    public bool EnableCustomNavPane { get; set; } = false;
    public string NavPaneBgHex { get; set; } = "#0F172A";
    public bool EnableNavPaneGradient { get; set; } = false;
    public string NavPaneGradientEndHex { get; set; } = "#1E1B4B";
    public double NavPaneOpacity { get; set; } = 72.0;
    public string NavPaneTextHex { get; set; } = string.Empty;
    public string NavPaneSelectedBgHex { get; set; } = string.Empty;

    // 3.3 主内容与卡片区 (Content & Cards)
    public bool EnableCustomContent { get; set; } = false;
    public string ContentBgHex { get; set; } = "#0B1120";
    public double ContentOpacity { get; set; } = 45.0;
    public string CardBgHex { get; set; } = "#1E293B";
    public double CardOpacity { get; set; } = 68.0;
    public string CardBorderHex { get; set; } = "#334155";
    public double CardCornerRadius { get; set; } = 12.0;
    public string PrimaryTextHex { get; set; } = string.Empty;
    public string SecondaryTextHex { get; set; } = string.Empty;

    // 3.4 底部播放控制栏 (PlayBar)
    public bool EnableCustomPlayBar { get; set; } = false;
    public string PlayBarBgHex { get; set; } = "#111827";
    public bool EnablePlayBarGradient { get; set; } = false;
    public string PlayBarGradientEndHex { get; set; } = "#1E293B";
    public double PlayBarOpacity { get; set; } = 88.0;
    public string PlayBarBorderHex { get; set; } = "#1F2937";
    public string PlayButtonColorHex { get; set; } = string.Empty;
    public string ProgressSliderColorHex { get; set; } = string.Empty;
    public string PlayBarTextHex { get; set; } = string.Empty;

    // ==================== 4. 界面形态、质感风格、字体与黑胶律动细节 ====================
    /// <summary>
    /// 底部播放栏形态：Docked (经典贴底通栏) | FloatingIsland (悬浮圆角胶囊灵动岛) | MinimalGlass (极简无界毛玻璃)
    /// </summary>
    public string PlayBarLayoutMode { get; set; } = "Docked";

    /// <summary>
    /// 播放音乐时底栏边缘是否开启流光律动呼吸灯带
    /// </summary>
    public bool EnablePlayBarRhythmGlow { get; set; } = false;

    /// <summary>
    /// 卡片质感风格：Default (标准) | FrostedGlass (磨砂毛玻璃) | NeonBorder (霓虹微光描边) | FlatMinimal (无边扁平极简) | ElevatedCard (深框立体浮雕)
    /// </summary>
    public string CardVisualStyle { get; set; } = "Default";

    /// <summary>
    /// 全局按钮与胶囊圆角 (4 ~ 22px)
    /// </summary>
    public double ButtonCornerRadius { get; set; } = 8.0;

    /// <summary>
    /// 全局自定义字体族（留空则使用系统默认字体）
    /// </summary>
    public string CustomFontFamily { get; set; } = string.Empty;

    /// <summary>
    /// 左下角歌曲封面样式：RoundedSquare (经典圆角方形) | Circle (正圆封面) | VinylSpin (黑胶唱片圆盘旋转动画)
    /// </summary>
    public string CoverArtShape { get; set; } = "RoundedSquare";

    // ==================== 5. 歌词与子窗口联动 ====================
    public bool SyncMiniPlayer { get; set; } = true;
    public bool EnableCustomMiniPlayerBg { get; set; } = false;
    public string MiniPlayerBgHex { get; set; } = "#111827";
    public double MiniPlayerOpacity { get; set; } = 95.0;

    public bool SyncImmersiveLyricHighlight { get; set; } = false;
    public bool SyncDesktopLyric { get; set; } = false;
    public string LyricHighlightColorHex { get; set; } = string.Empty;

    [JsonIgnore]
    public bool HasDynamicEffect =>
        BackdropType is "Aurora" or "Video" or "CoverFlow" ||
        EnableParticleOverlay ||
        EnablePlayBarRhythmGlow ||
        CoverArtShape == "VinylSpin";

    public CustomThemeProfile Clone(bool asNewCustom = false)
    {
        return new CustomThemeProfile
        {
            Id = asNewCustom ? Guid.NewGuid().ToString("N") : Id,
            Name = asNewCustom ? $"{Name} (副本)" : Name,
            Author = asNewCustom ? "我" : Author,
            Description = Description,
            IsBuiltIn = !asNewCustom && IsBuiltIn,
            MarketOriginId = asNewCustom ? string.Empty : MarketOriginId,
            CreatedAt = asNewCustom ? DateTime.Now : CreatedAt,

            BaseMode = BaseMode,
            BackdropType = BackdropType,
            WindowBgColorHex = WindowBgColorHex,
            BackgroundImagePath = BackgroundImagePath,
            BackgroundVideoPath = BackgroundVideoPath,
            BackgroundBlur = BackgroundBlur,
            BackgroundMaskColorHex = BackgroundMaskColorHex,
            BackgroundMaskOpacity = BackgroundMaskOpacity,

            AuroraColor1Hex = AuroraColor1Hex,
            AuroraColor2Hex = AuroraColor2Hex,
            AuroraColor3Hex = AuroraColor3Hex,
            AuroraSpeed = AuroraSpeed,
            AuroraIntensity = AuroraIntensity,

            EnableParticleOverlay = EnableParticleOverlay,
            ParticleType = ParticleType,
            ParticleDensity = ParticleDensity,
            ParticleSpeed = ParticleSpeed,
            ParticleColorHex = ParticleColorHex,

            AccentColorHex = AccentColorHex,
            EnableAccentGradient = EnableAccentGradient,
            AccentGradientEndHex = AccentGradientEndHex,
            GradientAngle = GradientAngle,
            UseCustomAccentStates = UseCustomAccentStates,
            AccentHoverHex = AccentHoverHex,
            AccentPressedHex = AccentPressedHex,

            EnableCustomTitleBar = EnableCustomTitleBar,
            TitleBarBgHex = TitleBarBgHex,
            EnableTitleBarGradient = EnableTitleBarGradient,
            TitleBarGradientEndHex = TitleBarGradientEndHex,
            TitleBarOpacity = TitleBarOpacity,
            TitleBarTextHex = TitleBarTextHex,

            EnableCustomNavPane = EnableCustomNavPane,
            NavPaneBgHex = NavPaneBgHex,
            EnableNavPaneGradient = EnableNavPaneGradient,
            NavPaneGradientEndHex = NavPaneGradientEndHex,
            NavPaneOpacity = NavPaneOpacity,
            NavPaneTextHex = NavPaneTextHex,
            NavPaneSelectedBgHex = NavPaneSelectedBgHex,

            EnableCustomContent = EnableCustomContent,
            ContentBgHex = ContentBgHex,
            ContentOpacity = ContentOpacity,
            CardBgHex = CardBgHex,
            CardOpacity = CardOpacity,
            CardBorderHex = CardBorderHex,
            CardCornerRadius = CardCornerRadius,
            PrimaryTextHex = PrimaryTextHex,
            SecondaryTextHex = SecondaryTextHex,

            EnableCustomPlayBar = EnableCustomPlayBar,
            PlayBarBgHex = PlayBarBgHex,
            EnablePlayBarGradient = EnablePlayBarGradient,
            PlayBarGradientEndHex = PlayBarGradientEndHex,
            PlayBarOpacity = PlayBarOpacity,
            PlayBarBorderHex = PlayBarBorderHex,
            PlayButtonColorHex = PlayButtonColorHex,
            ProgressSliderColorHex = ProgressSliderColorHex,
            PlayBarTextHex = PlayBarTextHex,

            PlayBarLayoutMode = PlayBarLayoutMode,
            EnablePlayBarRhythmGlow = EnablePlayBarRhythmGlow,
            CardVisualStyle = CardVisualStyle,
            ButtonCornerRadius = ButtonCornerRadius,
            CustomFontFamily = CustomFontFamily,
            CoverArtShape = CoverArtShape,

            SyncMiniPlayer = SyncMiniPlayer,
            EnableCustomMiniPlayerBg = EnableCustomMiniPlayerBg,
            MiniPlayerBgHex = MiniPlayerBgHex,
            MiniPlayerOpacity = MiniPlayerOpacity,
            SyncImmersiveLyricHighlight = SyncImmersiveLyricHighlight,
            SyncDesktopLyric = SyncDesktopLyric,
            LyricHighlightColorHex = LyricHighlightColorHex
        };
    }

    /// <summary>
    /// 官方内置精选主题预设库（8 套经典材质预设 + 6 套高自由度动态/形态预设）
    /// </summary>
    public static List<CustomThemeProfile> GetBuiltInPresets() =>
    [
        new()
        {
            Id = "builtin-emerald-dark",
            Name = "翡翠森林",
            Author = "Official",
            Description = "默认经典翠绿与原生暗色云母材质",
            IsBuiltIn = true,
            BaseMode = "Dark",
            BackdropType = "Mica",
            WindowBgColorHex = "#12151C",
            AccentColorHex = "#10B981"
        },
        new()
        {
            Id = "builtin-aurora-dream",
            Name = "极光幻境 · 动态",
            Author = "Official",
            Description = "三色流体极光动态漫游 + 渐变主色 + 悬浮灵动岛播放栏",
            IsBuiltIn = true,
            BaseMode = "Dark",
            BackdropType = "Aurora",
            WindowBgColorHex = "#070B19",
            AuroraColor1Hex = "#10B981",
            AuroraColor2Hex = "#6366F1",
            AuroraColor3Hex = "#EC4899",
            AuroraSpeed = 1.1,
            AuroraIntensity = 72,
            AccentColorHex = "#10B981",
            EnableAccentGradient = true,
            AccentGradientEndHex = "#3B82F6",
            GradientAngle = 135,
            PlayBarLayoutMode = "FloatingIsland",
            EnablePlayBarRhythmGlow = true,
            CardVisualStyle = "FrostedGlass",
            ButtonCornerRadius = 14,
            SyncImmersiveLyricHighlight = true
        },
        new()
        {
            Id = "builtin-sakura-breeze",
            Name = "落樱吹雪 · 粒子",
            Author = "Official",
            Description = "柔粉昼光弥散 + 漫天樱花花瓣飘落 + 悬浮圆角胶囊岛",
            IsBuiltIn = true,
            BaseMode = "Light",
            BackdropType = "Aurora",
            WindowBgColorHex = "#FFF7FA",
            AuroraColor1Hex = "#FBCFE8",
            AuroraColor2Hex = "#E9D5FF",
            AuroraColor3Hex = "#FDE68A",
            AuroraSpeed = 0.8,
            AuroraIntensity = 62,
            EnableParticleOverlay = true,
            ParticleType = "Sakura",
            ParticleDensity = 34,
            ParticleSpeed = 0.9,
            AccentColorHex = "#F43F5E",
            EnableAccentGradient = true,
            AccentGradientEndHex = "#EC4899",
            PlayBarLayoutMode = "FloatingIsland",
            CardVisualStyle = "FrostedGlass",
            CoverArtShape = "Circle",
            ButtonCornerRadius = 16
        },
        new()
        {
            Id = "builtin-cyber-island",
            Name = "赛博霓虹 · 灵动岛",
            Author = "Official",
            Description = "电光紫青双渐变 + 繁星粒子 + 霓虹发光卡片与黑胶旋转",
            IsBuiltIn = true,
            BaseMode = "Dark",
            BackdropType = "Aurora",
            WindowBgColorHex = "#080614",
            AuroraColor1Hex = "#D946EF",
            AuroraColor2Hex = "#06B6D4",
            AuroraColor3Hex = "#4F46E5",
            AuroraSpeed = 1.4,
            AuroraIntensity = 75,
            EnableParticleOverlay = true,
            ParticleType = "Starlight",
            ParticleDensity = 42,
            AccentColorHex = "#D946EF",
            EnableAccentGradient = true,
            AccentGradientEndHex = "#06B6D4",
            GradientAngle = 45,
            PlayBarLayoutMode = "FloatingIsland",
            EnablePlayBarRhythmGlow = true,
            CardVisualStyle = "NeonBorder",
            CoverArtShape = "VinylSpin",
            ButtonCornerRadius = 18,
            SyncImmersiveLyricHighlight = true,
            SyncDesktopLyric = true
        },
        new()
        {
            Id = "builtin-cover-flow",
            Name = "随歌流光 · 呼吸",
            Author = "Official",
            Description = "切歌自动提取专辑封面多色流光呼吸 + 音泡粒子 + 黑胶唱片",
            IsBuiltIn = true,
            BaseMode = "Dark",
            BackdropType = "CoverFlow",
            WindowBgColorHex = "#0B101B",
            AuroraSpeed = 0.9,
            AuroraIntensity = 70,
            EnableParticleOverlay = true,
            ParticleType = "Bubble",
            ParticleDensity = 28,
            ParticleSpeed = 0.85,
            AccentColorHex = "#14B8A6",
            PlayBarLayoutMode = "FloatingIsland",
            EnablePlayBarRhythmGlow = true,
            CardVisualStyle = "FrostedGlass",
            CoverArtShape = "VinylSpin",
            SyncImmersiveLyricHighlight = true
        },
        new()
        {
            Id = "builtin-vinyl-retro",
            Name = "黑胶留声 · 复古金",
            Author = "Official",
            Description = "暖调胡桃木黑金渐变 + 夏夜萤火粒子 + 衬线字体与黑胶唱盘",
            IsBuiltIn = true,
            BaseMode = "Dark",
            BackdropType = "Solid",
            WindowBgColorHex = "#120E0A",
            EnableParticleOverlay = true,
            ParticleType = "Firefly",
            ParticleDensity = 26,
            ParticleSpeed = 0.7,
            AccentColorHex = "#D97706",
            EnableAccentGradient = true,
            AccentGradientEndHex = "#F59E0B",
            EnableCustomNavPane = true,
            NavPaneBgHex = "#19130D",
            EnableNavPaneGradient = true,
            NavPaneGradientEndHex = "#110D09",
            NavPaneOpacity = 94,
            EnableCustomContent = true,
            ContentBgHex = "#120E0A",
            ContentOpacity = 80,
            CardBgHex = "#1E1711",
            CardOpacity = 90,
            CardBorderHex = "#3E2F20",
            EnableCustomPlayBar = true,
            PlayBarBgHex = "#1B140E",
            EnablePlayBarGradient = true,
            PlayBarGradientEndHex = "#291E14",
            PlayBarOpacity = 96,
            PlayBarBorderHex = "#473524",
            CardVisualStyle = "ElevatedCard",
            CoverArtShape = "VinylSpin",
            CustomFontFamily = "SimSun, Georgia, Microsoft YaHei UI"
        },
        new()
        {
            Id = "builtin-winter-snow",
            Name = "冬日初雪 · 毛玻璃",
            Author = "Official",
            Description = "冰川蓝静谧亚克力 + 漫天微雪粒子 + 极简无界底栏",
            IsBuiltIn = true,
            BaseMode = "Dark",
            BackdropType = "Acrylic",
            WindowBgColorHex = "#0A1424",
            EnableParticleOverlay = true,
            ParticleType = "Snow",
            ParticleDensity = 45,
            ParticleSpeed = 0.85,
            AccentColorHex = "#38BDF8",
            EnableAccentGradient = true,
            AccentGradientEndHex = "#818CF8",
            PlayBarLayoutMode = "MinimalGlass",
            CardVisualStyle = "FrostedGlass",
            CoverArtShape = "Circle",
            ButtonCornerRadius = 14
        },
        new()
        {
            Id = "builtin-emerald-light",
            Name = "清风白昼",
            Author = "Official",
            Description = "通透浅色云母与清新翠绿点缀",
            IsBuiltIn = true,
            BaseMode = "Light",
            BackdropType = "Mica",
            WindowBgColorHex = "#F8FAFC",
            AccentColorHex = "#10B981"
        },
        new()
        {
            Id = "builtin-sakura-daylight",
            Name = "晴空花信 · 白昼樱",
            Author = "Official",
            Description = "樱雾粉白与晴空蓝晨曦流体极光，绯樱花瓣飘落配悬浮白玉灵动岛",
            IsBuiltIn = true,
            BaseMode = "Light",
            BackdropType = "Aurora",
            WindowBgColorHex = "#FDF5F8",
            AuroraColor1Hex = "#FECDD3",
            AuroraColor2Hex = "#BAE6FD",
            AuroraColor3Hex = "#DDD6FE",
            AuroraSpeed = 0.9,
            AuroraIntensity = 62,
            EnableParticleOverlay = true,
            ParticleType = "Sakura",
            ParticleDensity = 30,
            ParticleSpeed = 0.9,
            ParticleColorHex = "#F472B6",
            AccentColorHex = "#EC4899",
            EnableAccentGradient = true,
            AccentGradientEndHex = "#F43F5E",
            PlayBarLayoutMode = "FloatingIsland",
            EnablePlayBarRhythmGlow = true,
            CardVisualStyle = "FrostedGlass",
            ButtonCornerRadius = 16,
            CoverArtShape = "VinylSpin",
            SyncImmersiveLyricHighlight = true
        },
        new()
        {
            Id = "builtin-mint-morning",
            Name = "晨雾薄荷 · 浮光泡",
            Author = "Official",
            Description = "冰川青与鲜薄荷白昼极光漫游，晶莹音泡缓缓浮升，无界轻透底栏",
            IsBuiltIn = true,
            BaseMode = "Light",
            BackdropType = "Aurora",
            WindowBgColorHex = "#F2FBF8",
            AuroraColor1Hex = "#A7F3D0",
            AuroraColor2Hex = "#99F6E4",
            AuroraColor3Hex = "#BAE6FD",
            AuroraSpeed = 0.85,
            AuroraIntensity = 58,
            EnableParticleOverlay = true,
            ParticleType = "Bubble",
            ParticleDensity = 26,
            ParticleSpeed = 0.8,
            ParticleColorHex = "#10B981",
            AccentColorHex = "#059669",
            EnableAccentGradient = true,
            AccentGradientEndHex = "#0EA5E9",
            PlayBarLayoutMode = "MinimalGlass",
            EnablePlayBarRhythmGlow = true,
            CardVisualStyle = "FrostedGlass",
            ButtonCornerRadius = 14,
            CoverArtShape = "Circle"
        },
        new()
        {
            Id = "builtin-coverflow-day",
            Name = "流光白昼 · 随歌呼吸",
            Author = "Official",
            Description = "浅色护眼底色下随歌曲封面自动晕染三色水彩霞光，白昼听歌灵动清透",
            IsBuiltIn = true,
            BaseMode = "Light",
            BackdropType = "CoverFlow",
            WindowBgColorHex = "#F6F8FC",
            AuroraSpeed = 1.0,
            AuroraIntensity = 56,
            EnableParticleOverlay = true,
            ParticleType = "Starlight",
            ParticleDensity = 32,
            ParticleSpeed = 0.75,
            ParticleColorHex = "#38BDF8",
            AccentColorHex = "#0284C7",
            EnableAccentGradient = true,
            AccentGradientEndHex = "#6366F1",
            PlayBarLayoutMode = "FloatingIsland",
            EnablePlayBarRhythmGlow = true,
            CardVisualStyle = "FrostedGlass",
            ButtonCornerRadius = 16,
            CoverArtShape = "VinylSpin",
            SyncImmersiveLyricHighlight = true
        },
        new()
        {
            Id = "builtin-netease-red",
            Name = "云村雅红",
            Author = "Official",
            Description = "经典音乐红强调色与微透暖黑分栏布局",
            IsBuiltIn = true,
            BaseMode = "Dark",
            BackdropType = "Acrylic",
            WindowBgColorHex = "#13131A",
            AccentColorHex = "#EC4141",
            EnableCustomNavPane = true,
            NavPaneBgHex = "#191922",
            NavPaneOpacity = 82,
            EnableCustomPlayBar = true,
            PlayBarBgHex = "#1D1D26",
            PlayBarOpacity = 94,
            PlayBarBorderHex = "#2E2E3A",
            SyncImmersiveLyricHighlight = true
        },
        new()
        {
            Id = "builtin-qq-mint",
            Name = "绿钻律动",
            Author = "Official",
            Description = "灵动薄荷青绿搭配深邃青黑卡片层次",
            IsBuiltIn = true,
            BaseMode = "Dark",
            BackdropType = "MicaAlt",
            WindowBgColorHex = "#0D1717",
            AccentColorHex = "#1ECC94",
            EnableCustomContent = true,
            ContentBgHex = "#0B1414",
            ContentOpacity = 55,
            CardBgHex = "#142424",
            CardOpacity = 76,
            CardBorderHex = "#213B3B",
            EnableCustomPlayBar = true,
            PlayBarBgHex = "#101E1E",
            PlayBarOpacity = 92,
            PlayBarBorderHex = "#1E3636"
        },
        new()
        {
            Id = "builtin-sakura-pink",
            Name = "落樱微醺",
            Author = "Official",
            Description = "温柔樱花粉白亚克力，四角独立柔雾配色",
            IsBuiltIn = true,
            BaseMode = "Light",
            BackdropType = "Acrylic",
            WindowBgColorHex = "#FFF5F7",
            AccentColorHex = "#EC4899",
            EnableCustomTitleBar = true,
            TitleBarBgHex = "#FFE4EC",
            TitleBarOpacity = 70,
            TitleBarTextHex = "#4A1D34",
            EnableCustomNavPane = true,
            NavPaneBgHex = "#FFF0F5",
            NavPaneOpacity = 78,
            NavPaneTextHex = "#59223E",
            NavPaneSelectedBgHex = "#33EC4899",
            EnableCustomContent = true,
            ContentBgHex = "#FFF9FB",
            ContentOpacity = 68,
            CardBgHex = "#FFFFFF",
            CardOpacity = 86,
            CardBorderHex = "#FBCFE8",
            PrimaryTextHex = "#3B1328",
            SecondaryTextHex = "#834665",
            EnableCustomPlayBar = true,
            PlayBarBgHex = "#FFEAF1",
            PlayBarOpacity = 92,
            PlayBarBorderHex = "#F9A8D4",
            PlayBarTextHex = "#3B1328"
        },
        new()
        {
            Id = "builtin-ocean-aurora",
            Name = "深海极光",
            Author = "Official",
            Description = "冰川天蓝搭配深海蓝分层半透明舱体",
            IsBuiltIn = true,
            BaseMode = "Dark",
            BackdropType = "Acrylic",
            WindowBgColorHex = "#081226",
            AccentColorHex = "#0EA5E9",
            EnableCustomTitleBar = true,
            TitleBarBgHex = "#0B1933",
            TitleBarOpacity = 65,
            EnableCustomNavPane = true,
            NavPaneBgHex = "#09152B",
            NavPaneOpacity = 78,
            EnableCustomContent = true,
            ContentBgHex = "#0B1830",
            ContentOpacity = 50,
            CardBgHex = "#132647",
            CardOpacity = 70,
            CardBorderHex = "#1E3A6A",
            EnableCustomPlayBar = true,
            PlayBarBgHex = "#0A172E",
            PlayBarOpacity = 92,
            PlayBarBorderHex = "#1D355E"
        },
        new()
        {
            Id = "builtin-nebula-purple",
            Name = "暗夜星紫",
            Author = "Official",
            Description = "赛博霓虹紫罗兰，沉浸式暗夜氛围感",
            IsBuiltIn = true,
            BaseMode = "Dark",
            BackdropType = "Acrylic",
            WindowBgColorHex = "#120B24",
            AccentColorHex = "#A855F7",
            EnableCustomNavPane = true,
            NavPaneBgHex = "#170E2E",
            NavPaneOpacity = 80,
            EnableCustomContent = true,
            ContentBgHex = "#140C28",
            ContentOpacity = 55,
            CardBgHex = "#221542",
            CardOpacity = 72,
            CardBorderHex = "#3B256E",
            EnableCustomPlayBar = true,
            PlayBarBgHex = "#180F30",
            PlayBarOpacity = 92,
            PlayBarBorderHex = "#352063",
            SyncImmersiveLyricHighlight = true
        },
        new()
        {
            Id = "builtin-obsidian-gold",
            Name = "黑金流光",
            Author = "Official",
            Description = "纯粹曜石纯黑底色与香槟琥珀金高对比质感",
            IsBuiltIn = true,
            BaseMode = "Dark",
            BackdropType = "Solid",
            WindowBgColorHex = "#09090B",
            AccentColorHex = "#F59E0B",
            EnableCustomTitleBar = true,
            TitleBarBgHex = "#0C0C0F",
            TitleBarOpacity = 96,
            EnableCustomNavPane = true,
            NavPaneBgHex = "#0E0E12",
            NavPaneOpacity = 98,
            EnableCustomContent = true,
            ContentBgHex = "#09090B",
            ContentOpacity = 100,
            CardBgHex = "#141418",
            CardOpacity = 95,
            CardBorderHex = "#27272A",
            EnableCustomPlayBar = true,
            PlayBarBgHex = "#111115",
            PlayBarOpacity = 98,
            PlayBarBorderHex = "#27272A"
        }
    ];
}

/// <summary>
/// 主题市场单条上架主题卡片模型（兼容本地创意精选与远程 JSON 订阅源）
/// </summary>
public class MarketplaceThemeItem
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = "精选推荐";
    public List<string> Tags { get; set; } = [];
    public int Downloads { get; set; } = 1000;
    public string SourceName { get; set; } = "官方创意市场";
    public CustomThemeProfile Theme { get; set; } = new();
}

/// <summary>
/// 远程在线主题订阅源 JSON 清单结构
/// </summary>
public class MarketplaceFeedManifest
{
    public string FeedName { get; set; } = "社区主题源";
    public List<MarketplaceThemeItem> Items { get; set; } = [];
}

/// <summary>
/// 主题中心与主题市场持久化配置
/// </summary>
public class ThemeCenterConfig
{
    public string ActiveThemeId { get; set; } = "builtin-emerald-dark";
    public string? OverrideBaseMode { get; set; } = null;
    public bool FollowSongCoverAccent { get; set; } = false;
    public List<CustomThemeProfile> CustomThemes { get; set; } = [];
    public List<string> MarketFeedUrls { get; set; } = [];
}

/// <summary>
/// 内置官方创意主题市场精选目录（覆盖动态极光、粒子氛围、赛博渐变、极简浅色、复古黑胶等丰富流派）
/// </summary>
public static class ThemeMarketplaceCatalog
{
    public static List<MarketplaceThemeItem> GetCuratedMarketItems() =>
    [
        new()
        {
            Id = "market-amethyst-firefly",
            Category = "动态极光",
            Tags = ["动态极光", "萤火粒子", "悬浮岛", "双渐变"],
            Downloads = 18420,
            SourceName = "Alger 创意工坊",
            Theme = new CustomThemeProfile
            {
                Id = "market-amethyst-firefly",
                MarketOriginId = "market-amethyst-firefly",
                Name = "紫晶秘境 · 萤火",
                Author = "LunaStudio",
                Description = "紫罗兰与翡翠双极光漫游，搭配森林萤火虫微光与悬浮灵动岛",
                BaseMode = "Dark",
                BackdropType = "Aurora",
                WindowBgColorHex = "#0B0719",
                AuroraColor1Hex = "#8B5CF6",
                AuroraColor2Hex = "#10B981",
                AuroraColor3Hex = "#3B82F6",
                AuroraSpeed = 0.95,
                AuroraIntensity = 70,
                EnableParticleOverlay = true,
                ParticleType = "Firefly",
                ParticleDensity = 32,
                ParticleColorHex = "#A7F3D0",
                AccentColorHex = "#A855F7",
                EnableAccentGradient = true,
                AccentGradientEndHex = "#10B981",
                PlayBarLayoutMode = "FloatingIsland",
                EnablePlayBarRhythmGlow = true,
                CardVisualStyle = "FrostedGlass",
                CoverArtShape = "VinylSpin",
                ButtonCornerRadius = 16,
                SyncImmersiveLyricHighlight = true
            }
        },
        new()
        {
            Id = "market-california-sunset",
            Category = "动态极光",
            Tags = ["落日渐变", "黑胶旋转", "悬浮岛"],
            Downloads = 14950,
            SourceName = "Alger 创意工坊",
            Theme = new CustomThemeProfile
            {
                Id = "market-california-sunset",
                MarketOriginId = "market-california-sunset",
                Name = "加州落日 · 1986",
                Author = "SynthWave",
                Description = "晚霞橙粉与暮光紫斜向流体渐变，温暖公路电台氛围",
                BaseMode = "Dark",
                BackdropType = "Aurora",
                WindowBgColorHex = "#160B18",
                AuroraColor1Hex = "#F97316",
                AuroraColor2Hex = "#EC4899",
                AuroraColor3Hex = "#7C3AED",
                AuroraSpeed = 1.2,
                AuroraIntensity = 72,
                AccentColorHex = "#F97316",
                EnableAccentGradient = true,
                AccentGradientEndHex = "#EC4899",
                GradientAngle = 45,
                EnableCustomPlayBar = true,
                PlayBarBgHex = "#241122",
                EnablePlayBarGradient = true,
                PlayBarGradientEndHex = "#1B0E26",
                PlayBarOpacity = 90,
                PlayBarBorderHex = "#58274C",
                PlayBarLayoutMode = "FloatingIsland",
                EnablePlayBarRhythmGlow = true,
                CardVisualStyle = "FrostedGlass",
                CoverArtShape = "VinylSpin",
                ButtonCornerRadius = 18
            }
        },
        new()
        {
            Id = "market-kyoto-matcha",
            Category = "极简浅色",
            Tags = ["浅色护眼", "楷体诗意", "落樱粒子"],
            Downloads = 12680,
            SourceName = "Alger 创意工坊",
            Theme = new CustomThemeProfile
            {
                Id = "market-kyoto-matcha",
                MarketOriginId = "market-kyoto-matcha",
                Name = "京都庭院 · 抹茶诗笺",
                Author = "MoriDesign",
                Description = "温润和纸米白衬底 + 宇治抹茶绿渐变 + 楷体诗词韵味",
                BaseMode = "Light",
                BackdropType = "Solid",
                WindowBgColorHex = "#F6F7F2",
                EnableParticleOverlay = true,
                ParticleType = "Sakura",
                ParticleDensity = 20,
                ParticleSpeed = 0.65,
                ParticleColorHex = "#86EFAC",
                AccentColorHex = "#15803D",
                EnableAccentGradient = true,
                AccentGradientEndHex = "#0D9488",
                EnableCustomNavPane = true,
                NavPaneBgHex = "#ECEFE6",
                NavPaneOpacity = 92,
                NavPaneTextHex = "#1E2923",
                EnableCustomContent = true,
                ContentBgHex = "#F6F7F2",
                ContentOpacity = 95,
                CardBgHex = "#FFFFFF",
                CardOpacity = 92,
                CardBorderHex = "#D9E2D5",
                CardCornerRadius = 14,
                PrimaryTextHex = "#1A261E",
                SecondaryTextHex = "#566B5C",
                CardVisualStyle = "FlatMinimal",
                CustomFontFamily = "KaiTi, STKaiti, Microsoft YaHei UI",
                CoverArtShape = "Circle",
                ButtonCornerRadius = 12
            }
        },
        new()
        {
            Id = "market-blue-archive-sky",
            Category = "极简浅色",
            Tags = ["二次元清爽", "几何胶囊", "悬浮岛"],
            Downloads = 21340,
            SourceName = "ACG 视觉社",
            Theme = new CustomThemeProfile
            {
                Id = "market-blue-archive-sky",
                MarketOriginId = "market-blue-archive-sky",
                Name = "什亭晴空 · 几何学园",
                Author = "SchaleLab",
                Description = "通透晴空天蓝与纯白斜切毛玻璃，明快清新的二次元桌面风",
                BaseMode = "Light",
                BackdropType = "Aurora",
                WindowBgColorHex = "#F0F7FF",
                AuroraColor1Hex = "#BAE6FD",
                AuroraColor2Hex = "#C7D2FE",
                AuroraColor3Hex = "#CCFBF1",
                AuroraSpeed = 0.85,
                AuroraIntensity = 60,
                EnableParticleOverlay = true,
                ParticleType = "Bubble",
                ParticleDensity = 24,
                ParticleColorHex = "#38BDF8",
                AccentColorHex = "#0284C7",
                EnableAccentGradient = true,
                AccentGradientEndHex = "#38BDF8",
                PlayBarLayoutMode = "FloatingIsland",
                CardVisualStyle = "FrostedGlass",
                ButtonCornerRadius = 20,
                CoverArtShape = "RoundedSquare"
            }
        },
        new()
        {
            Id = "market-deep-ocean-jelly",
            Category = "粒子氛围",
            Tags = ["深海音泡", "极简底栏", "霓虹微光"],
            Downloads = 11290,
            SourceName = "Alger 创意工坊",
            Theme = new CustomThemeProfile
            {
                Id = "market-deep-ocean-jelly",
                MarketOriginId = "market-deep-ocean-jelly",
                Name = "深海荧光 · 水母音泡",
                Author = "Abyssal",
                Description = "马里亚纳幽蓝深海 + 缓缓上升的发光水母音泡与冰青霓虹描边",
                BaseMode = "Dark",
                BackdropType = "Aurora",
                WindowBgColorHex = "#040D1A",
                AuroraColor1Hex = "#06B6D4",
                AuroraColor2Hex = "#1D4ED8",
                AuroraColor3Hex = "#0D9488",
                AuroraSpeed = 0.75,
                AuroraIntensity = 66,
                EnableParticleOverlay = true,
                ParticleType = "Bubble",
                ParticleDensity = 38,
                ParticleSpeed = 0.75,
                ParticleColorHex = "#67E8F9",
                AccentColorHex = "#06B6D4",
                EnableAccentGradient = true,
                AccentGradientEndHex = "#3B82F6",
                PlayBarLayoutMode = "MinimalGlass",
                EnablePlayBarRhythmGlow = true,
                CardVisualStyle = "NeonBorder",
                CoverArtShape = "Circle",
                ButtonCornerRadius = 14
            }
        },
        new()
        {
            Id = "market-hacker-matrix",
            Category = "赛博高定",
            Tags = ["极客等宽", "终端荧光绿", "硬朗方角"],
            Downloads = 9860,
            SourceName = "GeekHub",
            Theme = new CustomThemeProfile
            {
                Id = "market-hacker-matrix",
                MarketOriginId = "market-hacker-matrix",
                Name = "矩阵终端 · Terminal",
                Author = "ZeroCool",
                Description = "极客等宽代码字体 + 4px 硬朗切角 + 黑客矩阵荧光绿霓虹框",
                BaseMode = "Dark",
                BackdropType = "Solid",
                WindowBgColorHex = "#050806",
                EnableParticleOverlay = true,
                ParticleType = "Starlight",
                ParticleDensity = 30,
                ParticleColorHex = "#22C55E",
                AccentColorHex = "#22C55E",
                EnableCustomNavPane = true,
                NavPaneBgHex = "#070D09",
                NavPaneOpacity = 96,
                EnableCustomContent = true,
                ContentBgHex = "#050806",
                ContentOpacity = 98,
                CardBgHex = "#0A140D",
                CardOpacity = 92,
                CardBorderHex = "#166534",
                CardCornerRadius = 4,
                PrimaryTextHex = "#DCFCE7",
                SecondaryTextHex = "#4ADE80",
                EnableCustomPlayBar = true,
                PlayBarBgHex = "#070E09",
                PlayBarOpacity = 98,
                PlayBarBorderHex = "#15803D",
                CardVisualStyle = "NeonBorder",
                ButtonCornerRadius = 4,
                CustomFontFamily = "Cascadia Code, Consolas, Microsoft YaHei UI"
            }
        },
        new()
        {
            Id = "market-crimson-sakura-night",
            Category = "粒子氛围",
            Tags = ["暗夜落樱", "黑胶唱盘", "绯红渐变"],
            Downloads = 16700,
            SourceName = "ACG 视觉社",
            Theme = new CustomThemeProfile
            {
                Id = "market-crimson-sakura-night",
                MarketOriginId = "market-crimson-sakura-night",
                Name = "夜樱绯月 · 千本樱",
                Author = "Kurohana",
                Description = "玄黑绯红渐变暗夜下飘落的绯色樱瓣，搭配黑胶旋转唱盘",
                BaseMode = "Dark",
                BackdropType = "Aurora",
                WindowBgColorHex = "#12070C",
                AuroraColor1Hex = "#E11D48",
                AuroraColor2Hex = "#9F1239",
                AuroraColor3Hex = "#4C1D95",
                AuroraSpeed = 0.9,
                AuroraIntensity = 60,
                EnableParticleOverlay = true,
                ParticleType = "Sakura",
                ParticleDensity = 36,
                ParticleSpeed = 1.05,
                ParticleColorHex = "#FDA4AF",
                AccentColorHex = "#F43F5E",
                EnableAccentGradient = true,
                AccentGradientEndHex = "#FB7185",
                PlayBarLayoutMode = "FloatingIsland",
                EnablePlayBarRhythmGlow = true,
                CardVisualStyle = "FrostedGlass",
                CoverArtShape = "VinylSpin",
                ButtonCornerRadius = 14,
                SyncImmersiveLyricHighlight = true
            }
        },
        new()
        {
            Id = "market-mocha-latte",
            Category = "复古质感",
            Tags = ["咖啡暖调", "护眼柔和", "无边极简"],
            Downloads = 10430,
            SourceName = "Alger 创意工坊",
            Theme = new CustomThemeProfile
            {
                Id = "market-mocha-latte",
                MarketOriginId = "market-mocha-latte",
                Name = "焦糖拿铁 · 午后书房",
                Author = "CafeRoast",
                Description = "低饱和可可暖棕与燕麦奶白文字，长时间听歌温和不刺眼",
                BaseMode = "Dark",
                BackdropType = "Solid",
                WindowBgColorHex = "#1C1613",
                AccentColorHex = "#E09F67",
                EnableAccentGradient = true,
                AccentGradientEndHex = "#C67D43",
                EnableCustomNavPane = true,
                NavPaneBgHex = "#231C18",
                NavPaneOpacity = 96,
                PrimaryTextHex = "#F5EBE6",
                SecondaryTextHex = "#B8A398",
                EnableCustomContent = true,
                ContentBgHex = "#1C1613",
                ContentOpacity = 96,
                CardBgHex = "#28201C",
                CardOpacity = 92,
                CardBorderHex = "#3B2F29",
                CardCornerRadius = 14,
                EnableCustomPlayBar = true,
                PlayBarBgHex = "#241D19",
                PlayBarOpacity = 96,
                PlayBarBorderHex = "#3B2F29",
                CardVisualStyle = "FlatMinimal",
                CoverArtShape = "VinylSpin",
                ButtonCornerRadius = 12
            }
        },
        new()
        {
            Id = "market-nordic-frost",
            Category = "粒子氛围",
            Tags = ["北欧极简", "静谧微雪", "莫兰迪蓝"],
            Downloads = 13510,
            SourceName = "NordicUI",
            Theme = new CustomThemeProfile
            {
                Id = "market-nordic-frost",
                MarketOriginId = "market-nordic-frost",
                Name = "北欧峡湾 · 凛冬雪",
                Author = "NordicUI",
                Description = "经典 Nord 莫兰迪冰灰蓝配色，伴随轻柔飘雪与无界毛玻璃",
                BaseMode = "Dark",
                BackdropType = "Solid",
                WindowBgColorHex = "#2E3440",
                EnableParticleOverlay = true,
                ParticleType = "Snow",
                ParticleDensity = 32,
                ParticleSpeed = 0.7,
                AccentColorHex = "#88C0D0",
                EnableAccentGradient = true,
                AccentGradientEndHex = "#81A1C1",
                EnableCustomNavPane = true,
                NavPaneBgHex = "#292E39",
                NavPaneOpacity = 95,
                EnableCustomContent = true,
                ContentBgHex = "#2E3440",
                ContentOpacity = 92,
                CardBgHex = "#3B4252",
                CardOpacity = 88,
                CardBorderHex = "#4C566A",
                PrimaryTextHex = "#ECEFF4",
                SecondaryTextHex = "#D8DEE9",
                EnableCustomPlayBar = true,
                PlayBarBgHex = "#292E39",
                PlayBarOpacity = 94,
                PlayBarBorderHex = "#434C5E",
                PlayBarLayoutMode = "FloatingIsland",
                CardVisualStyle = "ElevatedCard",
                ButtonCornerRadius = 10
            }
        },
        new()
        {
            Id = "market-vaporwave-1984",
            Category = "赛博高定",
            Tags = ["蒸汽波", "粉青撞色", "律动灯带"],
            Downloads = 15200,
            SourceName = "RetroClub",
            Theme = new CustomThemeProfile
            {
                Id = "market-vaporwave-1984",
                MarketOriginId = "market-vaporwave-1984",
                Name = "蒸汽波 · 东京迷梦",
                Author = "RetroClub",
                Description = "经典芭比霓虹粉与蒂芙尼电青高饱和碰撞，全区斜向渐变",
                BaseMode = "Dark",
                BackdropType = "Aurora",
                WindowBgColorHex = "#140926",
                AuroraColor1Hex = "#FF2A85",
                AuroraColor2Hex = "#00F0FF",
                AuroraColor3Hex = "#7B2CBF",
                AuroraSpeed = 1.35,
                AuroraIntensity = 76,
                EnableParticleOverlay = true,
                ParticleType = "Starlight",
                ParticleDensity = 40,
                ParticleColorHex = "#00F0FF",
                AccentColorHex = "#FF2A85",
                EnableAccentGradient = true,
                AccentGradientEndHex = "#00F0FF",
                GradientAngle = 0,
                EnableCustomNavPane = true,
                NavPaneBgHex = "#1B0C33",
                EnableNavPaneGradient = true,
                NavPaneGradientEndHex = "#0E1D3A",
                NavPaneOpacity = 78,
                PlayBarLayoutMode = "FloatingIsland",
                EnablePlayBarRhythmGlow = true,
                CardVisualStyle = "NeonBorder",
                CoverArtShape = "VinylSpin",
                ButtonCornerRadius = 20
            }
        },
        new()
        {
            Id = "market-vanilla-cream",
            Category = "极简浅色",
            Tags = ["奶油暖白", "超圆润胶囊", "轻盈灵动"],
            Downloads = 11880,
            SourceName = "Alger 创意工坊",
            Theme = new CustomThemeProfile
            {
                Id = "market-vanilla-cream",
                MarketOriginId = "market-vanilla-cream",
                Name = "香草奶油 · 假日来信",
                Author = "CreamyLab",
                Description = "暖杏奶油底色配琥珀蜜糖渐变胶囊，圆润治愈的浅色体验",
                BaseMode = "Light",
                BackdropType = "Solid",
                WindowBgColorHex = "#FDFBF7",
                AccentColorHex = "#EA580C",
                EnableAccentGradient = true,
                AccentGradientEndHex = "#F59E0B",
                EnableCustomNavPane = true,
                NavPaneBgHex = "#F7F2E8",
                NavPaneOpacity = 95,
                PrimaryTextHex = "#292118",
                SecondaryTextHex = "#786B5D",
                EnableCustomContent = true,
                ContentBgHex = "#FDFBF7",
                ContentOpacity = 98,
                CardBgHex = "#FFFFFF",
                CardOpacity = 96,
                CardBorderHex = "#EFE6D5",
                CardCornerRadius = 18,
                PlayBarLayoutMode = "FloatingIsland",
                EnableCustomPlayBar = true,
                PlayBarBgHex = "#FFFDF9",
                PlayBarOpacity = 96,
                PlayBarBorderHex = "#E7DAC3",
                CardVisualStyle = "ElevatedCard",
                ButtonCornerRadius = 18,
                CoverArtShape = "Circle"
            }
        },
        new()
        {
            Id = "market-cover-symphony",
            Category = "动态极光",
            Tags = ["随歌流光", "繁星闪烁", "沉浸全透"],
            Downloads = 19650,
            SourceName = "Alger 创意工坊",
            Theme = new CustomThemeProfile
            {
                Id = "market-cover-symphony",
                MarketOriginId = "market-cover-symphony",
                Name = "流光交响 · 随歌万象",
                Author = "Official",
                Description = "每换一首歌即刻提取封面三色化成全屏极光，繁星相伴",
                BaseMode = "Dark",
                BackdropType = "CoverFlow",
                WindowBgColorHex = "#090D16",
                AuroraSpeed = 1.0,
                AuroraIntensity = 78,
                EnableParticleOverlay = true,
                ParticleType = "Starlight",
                ParticleDensity = 48,
                ParticleSpeed = 0.8,
                AccentColorHex = "#38BDF8",
                PlayBarLayoutMode = "MinimalGlass",
                EnablePlayBarRhythmGlow = true,
                CardVisualStyle = "FrostedGlass",
                CoverArtShape = "VinylSpin",
                ButtonCornerRadius = 16,
                SyncImmersiveLyricHighlight = true,
                SyncDesktopLyric = true
            }
        },
        new()
        {
            Id = "market-peach-ink-spring",
            Category = "极简浅色",
            Tags = ["国风浅色", "桃瓣飘落", "水彩极光"],
            Downloads = 17260,
            SourceName = "东方雅韵坊",
            Theme = new CustomThemeProfile
            {
                Id = "market-peach-ink-spring",
                MarketOriginId = "market-peach-ink-spring",
                Name = "桃夭春晓 · 水墨国风",
                Author = "InkLotus",
                Description = "宣纸暖白晕染桃花胭脂与春水碧青极光，飞花如雨配古典宋体韵味",
                BaseMode = "Light",
                BackdropType = "Aurora",
                WindowBgColorHex = "#FCF9F5",
                AuroraColor1Hex = "#FBCFE8",
                AuroraColor2Hex = "#A7F3D0",
                AuroraColor3Hex = "#FDE68A",
                AuroraSpeed = 0.75,
                AuroraIntensity = 56,
                EnableParticleOverlay = true,
                ParticleType = "Sakura",
                ParticleDensity = 28,
                ParticleSpeed = 0.8,
                ParticleColorHex = "#FB7185",
                AccentColorHex = "#E11D48",
                EnableAccentGradient = true,
                AccentGradientEndHex = "#F43F5E",
                PlayBarLayoutMode = "FloatingIsland",
                EnablePlayBarRhythmGlow = true,
                CardVisualStyle = "FrostedGlass",
                CoverArtShape = "VinylSpin",
                ButtonCornerRadius = 14,
                CustomFontFamily = "KaiTi, STKaiti, Microsoft YaHei UI",
                SyncImmersiveLyricHighlight = true
            }
        },
        new()
        {
            Id = "market-iceland-polar-day",
            Category = "粒子氛围",
            Tags = ["极昼浅色", "冰晶微雪", "白瓷浮雕"],
            Downloads = 15840,
            SourceName = "NordicUI",
            Theme = new CustomThemeProfile
            {
                Id = "market-iceland-polar-day",
                MarketOriginId = "market-iceland-polar-day",
                Name = "冰岛极昼 · 水晶微雪",
                Author = "NordicUI",
                Description = "纯净极昼冰川白与淡极光蓝紫轻柔交织，晶莹微雪飘落于白瓷浮雕卡片之上",
                BaseMode = "Light",
                BackdropType = "Aurora",
                WindowBgColorHex = "#F4F8FC",
                AuroraColor1Hex = "#BAE6FD",
                AuroraColor2Hex = "#C7D2FE",
                AuroraColor3Hex = "#A5F3FC",
                AuroraSpeed = 0.8,
                AuroraIntensity = 58,
                EnableParticleOverlay = true,
                ParticleType = "Snow",
                ParticleDensity = 34,
                ParticleSpeed = 0.75,
                ParticleColorHex = "#38BDF8",
                AccentColorHex = "#0284C7",
                EnableAccentGradient = true,
                AccentGradientEndHex = "#6366F1",
                PlayBarLayoutMode = "FloatingIsland",
                CardVisualStyle = "ElevatedCard",
                ButtonCornerRadius = 16,
                CoverArtShape = "Circle"
            }
        }
    ];
}
