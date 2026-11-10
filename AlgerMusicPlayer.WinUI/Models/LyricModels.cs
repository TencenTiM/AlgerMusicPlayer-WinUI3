using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;

namespace AlgerMusicPlayer.WinUI.Models;

/// <summary>
/// 逐字歌词单字模型
/// </summary>
public class LyricWord
{
    public string Text { get; set; } = string.Empty;
    public double StartTimeMs { get; set; }
    public double DurationMs { get; set; }
}

/// <summary>
/// 单行歌词模型（支持逐字、翻译、罗马音与 MusicFull.vue 动态排版/高亮属性绑定）
/// </summary>
public partial class LyricLine : ObservableObject
{
    public int Index { get; set; }
    public double StartTimeMs { get; set; }
    public double DurationMs { get; set; }
    public string Text { get; set; } = string.Empty;
    public string TranslationText { get; set; } = string.Empty;
    public string RomajiText { get; set; } = string.Empty;
    public List<LyricWord> Words { get; set; } = [];
    public bool HasWordByWord => Words.Count > 0;

    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (SetProperty(ref _isCurrent, value))
            {
                OnPropertyChanged(nameof(LineOpacity));
                OnPropertyChanged(nameof(DisplayFontSize));
                OnPropertyChanged(nameof(LineFontWeight));
                OnPropertyChanged(nameof(LineForeground));
            }
        }
    }

    private double _wordProgress;
    public double WordProgress
    {
        get => _wordProgress;
        set => SetProperty(ref _wordProgress, value);
    }

    // 动态排版属性（由 LyricSettings 实时驱动）
    private double _baseFontSize = 22;
    public double BaseFontSize
    {
        get => _baseFontSize;
        set
        {
            if (SetProperty(ref _baseFontSize, value))
            {
                OnPropertyChanged(nameof(DisplayFontSize));
                OnPropertyChanged(nameof(TranslationFontSize));
            }
        }
    }

    private int _fontWeightNumeric = 600;
    public int FontWeightNumeric
    {
        get => _fontWeightNumeric;
        set
        {
            if (SetProperty(ref _fontWeightNumeric, value))
            {
                OnPropertyChanged(nameof(LineFontWeight));
            }
        }
    }

    private double _letterSpacing;
    public double LetterSpacing
    {
        get => _letterSpacing;
        set
        {
            if (SetProperty(ref _letterSpacing, value))
            {
                OnPropertyChanged(nameof(CharacterSpacingValue));
            }
        }
    }

    private double _lineHeightRatio = 2.0;
    public double LineHeightRatio
    {
        get => _lineHeightRatio;
        set
        {
            if (SetProperty(ref _lineHeightRatio, value))
            {
                OnPropertyChanged(nameof(LinePadding));
            }
        }
    }

    private bool _centerLyrics;
    public bool CenterLyrics
    {
        get => _centerLyrics;
        set
        {
            if (SetProperty(ref _centerLyrics, value))
            {
                OnPropertyChanged(nameof(TextAlignmentValue));
                OnPropertyChanged(nameof(HorizontalAlignmentValue));
            }
        }
    }

    private bool _showTranslation = true;
    public bool ShowTranslation
    {
        get => _showTranslation;
        set
        {
            if (SetProperty(ref _showTranslation, value))
            {
                OnPropertyChanged(nameof(TranslationVisibility));
            }
        }
    }

    private Brush? _primaryBrush;
    public Brush? PrimaryBrush
    {
        get => _primaryBrush;
        set
        {
            if (SetProperty(ref _primaryBrush, value))
            {
                OnPropertyChanged(nameof(LineForeground));
                OnPropertyChanged(nameof(TranslationForeground));
            }
        }
    }

    private Brush? _activeBrush;
    public Brush? ActiveBrush
    {
        get => _activeBrush;
        set
        {
            if (SetProperty(ref _activeBrush, value))
            {
                OnPropertyChanged(nameof(LineForeground));
            }
        }
    }

    public double LineOpacity => IsCurrent ? 1.0 : 0.58;

    public double DisplayFontSize => IsCurrent ? Math.Round(BaseFontSize * 1.1, 1) : BaseFontSize;

    public double TranslationFontSize => Math.Max(12, Math.Round(BaseFontSize * 0.68, 1));

    public FontWeight LineFontWeight => IsCurrent
        ? FontWeights.Bold
        : FontWeightNumeric switch
        {
            <= 200 => FontWeights.Thin,
            <= 300 => FontWeights.Light,
            <= 400 => FontWeights.Normal,
            <= 500 => FontWeights.Medium,
            <= 600 => FontWeights.SemiBold,
            <= 700 => FontWeights.Bold,
            _ => FontWeights.ExtraBold
        };

    public int CharacterSpacingValue => (int)Math.Round(LetterSpacing * 45);

    public Thickness LinePadding
    {
        get
        {
            double vertical = Math.Max(4, (LineHeightRatio - 1.0) * 10);
            return new Thickness(16, vertical, 16, vertical);
        }
    }

    public TextAlignment TextAlignmentValue => CenterLyrics ? TextAlignment.Center : TextAlignment.Left;

    public HorizontalAlignment HorizontalAlignmentValue => CenterLyrics ? HorizontalAlignment.Center : HorizontalAlignment.Left;

    public Visibility TranslationVisibility =>
        ShowTranslation && !string.IsNullOrWhiteSpace(TranslationText)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Brush? LineForeground => IsCurrent ? ActiveBrush : PrimaryBrush;

    public Brush? TranslationForeground => PrimaryBrush;

    public void ApplyTypographyAndTheme(ImmersiveLyricConfig cfg, Brush primaryBrush, Brush activeBrush)
    {
        BaseFontSize = Math.Clamp(cfg.FontSize, 12, 32);
        FontWeightNumeric = Math.Clamp(cfg.FontWeight, 100, 900);
        LetterSpacing = Math.Clamp(cfg.LetterSpacing, -2, 10);
        LineHeightRatio = Math.Clamp(cfg.LineHeight, 1.0, 3.0);
        CenterLyrics = cfg.CenterLyrics;
        ShowTranslation = cfg.ShowTranslation;
        PrimaryBrush = primaryBrush;
        ActiveBrush = activeBrush;
    }

    public string TimeTagText
    {
        get
        {
            var ts = TimeSpan.FromMilliseconds(Math.Max(0, StartTimeMs));
            return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
    }
}

/// <summary>
/// 解析后的完整歌词结构
/// </summary>
public class ParsedLyric
{
    public string SongId { get; set; } = string.Empty;
    public List<LyricLine> Lines { get; set; } = [];
    public bool HasTranslation { get; set; }
    public bool HasRomaji { get; set; }
    public bool HasWordByWord { get; set; }
    public string RawLrc { get; set; } = string.Empty;
    public string RawTranslatedLrc { get; set; } = string.Empty;
}

/// <summary>
/// 独立桌面歌词窗口配置（1:1 对齐 src/renderer/views/lyric/index.vue 与 ThemeColorPanel.vue）
/// </summary>
public class DesktopLyricConfig
{
    public bool IsEnabled { get; set; } = false;
    public bool IsTop { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public string Theme { get; set; } = "dark"; // dark | light
    public string DisplayMode { get; set; } = "scroll"; // scroll | single | double
    public bool ShowTranslation { get; set; } = true;
    public bool IsDoubleLine { get; set; } = true;
    public double FontSize { get; set; } = 24; // 12 ~ 48
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public string HighlightColorHex { get; set; } = string.Empty; // 空字符串表示使用主题默认色 (#1ed760 / #1db954)
    public string PlayedColorHex { get; set; } = "#1ED760";
    public string UnplayedColorHex { get; set; } = "#E6E6E6";
    public string StrokeColorHex { get; set; } = "#000000";
    public double Opacity { get; set; } = 1.0;
    public bool HasCustomPosition { get; set; } = false;
    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;
    public int WindowWidth { get; set; } = 820;
    public int WindowHeight { get; set; } = 210;
}

/// <summary>
/// 播放详情页（MusicFull.vue + LyricSettings.vue）完整配置，1:1 对齐 DEFAULT_LYRIC_CONFIG (types/lyric.ts)
/// </summary>
public class ImmersiveLyricConfig
{
    // 显示设置 (display)
    public bool PureModeEnabled { get; set; } = false;
    public bool HideCover { get; set; } = false;
    public bool CenterLyrics { get; set; } = false;
    public bool ShowTranslation { get; set; } = true;
    public bool HideLyrics { get; set; } = false;
    public bool ShowRomaji { get; set; } = false;

    // 界面设置 (interface)
    public bool HideMiniPlayBar { get; set; } = false;
    public bool HidePlayBar { get; set; } = true;
    public int ContentWidth { get; set; } = 75; // 50% ~ 100%

    // 文字设置 (typography)
    public double FontSize { get; set; } = 22; // 12 ~ 32
    public double LetterSpacing { get; set; } = 0; // -2 ~ 10
    public int FontWeight { get; set; } = 500; // 100 ~ 900
    public double LineHeight { get; set; } = 2.0; // 1.0 ~ 3.0

    // 背景设置 (background)
    public string Theme { get; set; } = "default"; // default | light | dark
    public bool UseCustomBackground { get; set; } = false;
    public string BackgroundMode { get; set; } = "solid"; // solid | gradient | image | css
    public string SolidColorHex { get; set; } = "#1A1A1A";
    public List<string> GradientColors { get; set; } = ["#1A1A1A", "#000000"];
    public string GradientDirection { get; set; } = "to bottom"; // to bottom | to top | to right | to left | to bottom right | 45deg
    public string BackgroundImage { get; set; } = string.Empty;
    public double ImageBlur { get; set; } = 0; // 0 ~ 20px
    public double ImageBrightness { get; set; } = 100; // 0 ~ 200%
    public string CustomCss { get; set; } = string.Empty;
    public string CoverStyle { get; set; } = "square";
}
