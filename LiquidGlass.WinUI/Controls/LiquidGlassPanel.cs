using LiquidGlass.WinUI.Materials;
using LiquidGlass.WinUI.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Windows.UI;

namespace LiquidGlass.WinUI.Controls;

public enum GlassPanelSource
{
    Background = 0,
    Content = 1
}

/// <summary>
/// Liquid Glass（液态玻璃）五层渲染基类控件。
/// 依次管理 Backdrop -> Tint -> Reflection/Refraction -> Highlight -> Content 五层合成。
/// </summary>
[TemplatePart(Name = PartBackdrop, Type = typeof(Border))]
[TemplatePart(Name = PartTint, Type = typeof(Border))]
[TemplatePart(Name = PartReflection, Type = typeof(Border))]
[TemplatePart(Name = PartRefractionHost, Type = typeof(UIElement))]
[TemplatePart(Name = PartHighlight, Type = typeof(Border))]
public class LiquidGlassPanel : ContentControl
{
    protected const string PartBackdrop = "PART_Backdrop";
    protected const string PartTint = "PART_Tint";
    protected const string PartReflection = "PART_Reflection";
    protected const string PartRefractionHost = "PART_RefractionHost";
    protected const string PartHighlight = "PART_Highlight";

    public static readonly DependencyProperty MaterialKindProperty = DependencyProperty.Register(
        nameof(MaterialKind),
        typeof(LiquidGlassMaterialKind),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(LiquidGlassMaterialKind.Folder, OnOpticalPropertyChanged));

    public static readonly DependencyProperty IsCapsuleProperty = DependencyProperty.Register(
        nameof(IsCapsule),
        typeof(bool),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(false, OnCapsulePropertyChanged));

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(GlassPanelSource),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(GlassPanelSource.Background));

    public static readonly DependencyProperty CustomMaterialProperty = DependencyProperty.Register(
        nameof(CustomMaterial),
        typeof(LiquidGlassMaterial),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(null, OnOpticalPropertyChanged));

    public static readonly DependencyProperty BlurAmountProperty = DependencyProperty.Register(
        nameof(BlurAmount),
        typeof(double),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(22.0, OnOpticalPropertyChanged));

    public static readonly DependencyProperty TintColorProperty = DependencyProperty.Register(
        nameof(TintColor),
        typeof(Color),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(Color.FromArgb(255, 22, 26, 32), OnOpticalPropertyChanged));

    public static readonly DependencyProperty TintOpacityProperty = DependencyProperty.Register(
        nameof(TintOpacity),
        typeof(double),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(0.28, OnOpticalPropertyChanged));

    public static readonly DependencyProperty EdgeHighlightOpacityProperty = DependencyProperty.Register(
        nameof(EdgeHighlightOpacity),
        typeof(double),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(0.18, OnOpticalPropertyChanged));

    public static readonly DependencyProperty RefractionStrengthProperty = DependencyProperty.Register(
        nameof(RefractionStrength),
        typeof(double),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(0.12, OnOpticalPropertyChanged));

    public static readonly DependencyProperty EnableHoverEffectsProperty = DependencyProperty.Register(
        nameof(EnableHoverEffects),
        typeof(bool),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(true, OnInteractionSettingChanged));

    public static readonly DependencyProperty EnableElevationShadowProperty = DependencyProperty.Register(
        nameof(EnableElevationShadow),
        typeof(bool),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(false, OnOpticalPropertyChanged));

    public static readonly DependencyProperty UseCustomMaterialProperty = DependencyProperty.Register(
        nameof(UseCustomMaterial),
        typeof(bool),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(false, OnOpticalPropertyChanged));

    private readonly LiquidGlassRenderer _renderer = new();
    private Border? _backdropBorder;
    private Border? _tintBorder;
    private Border? _reflectionBorder;
    private UIElement? _refractionHost;
    private Border? _highlightBorder;
    private LiquidGlassMaterial? _resolvedMaterial;
    private bool _fallbackTemplateApplied;
    private bool _isLoaded;

    public LiquidGlassPanel()
    {
        DefaultStyleKey = typeof(LiquidGlassPanel);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        RegisterPropertyChangedCallback(CornerRadiusProperty, OnCornerRadiusPropertyChanged);
        Loaded += OnPanelLoaded;
        Unloaded += OnPanelUnloaded;
        SizeChanged += OnPanelSizeChanged;
        ActualThemeChanged += OnPanelActualThemeChanged;
    }

    public LiquidGlassMaterialKind MaterialKind
    {
        get => (LiquidGlassMaterialKind)GetValue(MaterialKindProperty);
        set => SetValue(MaterialKindProperty, value);
    }

    public bool IsCapsule
    {
        get => (bool)GetValue(IsCapsuleProperty);
        set => SetValue(IsCapsuleProperty, value);
    }

    public GlassPanelSource Source
    {
        get => (GlassPanelSource)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public LiquidGlassMaterial? CustomMaterial
    {
        get => (LiquidGlassMaterial?)GetValue(CustomMaterialProperty);
        set => SetValue(CustomMaterialProperty, value);
    }

    public double BlurAmount
    {
        get => (double)GetValue(BlurAmountProperty);
        set => SetValue(BlurAmountProperty, value);
    }

    public Color TintColor
    {
        get => (Color)GetValue(TintColorProperty);
        set => SetValue(TintColorProperty, value);
    }

    public double TintOpacity
    {
        get => (double)GetValue(TintOpacityProperty);
        set => SetValue(TintOpacityProperty, value);
    }

    public double EdgeHighlightOpacity
    {
        get => (double)GetValue(EdgeHighlightOpacityProperty);
        set => SetValue(EdgeHighlightOpacityProperty, value);
    }

    public double RefractionStrength
    {
        get => (double)GetValue(RefractionStrengthProperty);
        set => SetValue(RefractionStrengthProperty, value);
    }

    public bool EnableHoverEffects
    {
        get => (bool)GetValue(EnableHoverEffectsProperty);
        set => SetValue(EnableHoverEffectsProperty, value);
    }

    public bool EnableElevationShadow
    {
        get => (bool)GetValue(EnableElevationShadowProperty);
        set => SetValue(EnableElevationShadowProperty, value);
    }

    public bool UseCustomMaterial
    {
        get => (bool)GetValue(UseCustomMaterialProperty);
        set => SetValue(UseCustomMaterialProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _backdropBorder = GetTemplateChild(PartBackdrop) as Border;
        if (_backdropBorder == null && !_fallbackTemplateApplied)
        {
            _fallbackTemplateApplied = true;
            Template = BuildFallbackControlTemplate();
            return;
        }

        _tintBorder = GetTemplateChild(PartTint) as Border;
        _reflectionBorder = GetTemplateChild(PartReflection) as Border;
        _refractionHost = GetTemplateChild(PartRefractionHost) as UIElement;
        _highlightBorder = GetTemplateChild(PartHighlight) as Border;

        RefreshMaterial();
        SyncInteractions();
    }

    protected virtual void OnBeforeRefreshMaterial(LiquidGlassMaterial material)
    {
    }

    public void RefreshMaterial()
    {
        var baseOverride = UseCustomMaterial ? CustomMaterial : null;
        var hasCustomCorner = ReadLocalValue(CornerRadiusProperty) != DependencyProperty.UnsetValue;
        var material = LiquidGlassMaterialManager.Current.Resolve(
            MaterialKind,
            ActualTheme,
            baseOverride,
            hasCustomCorner ? CornerRadius : null);

        if (ReadLocalValue(BlurAmountProperty) != DependencyProperty.UnsetValue)
        {
            material.BlurAmount = BlurAmount;
        }
        if (ReadLocalValue(TintColorProperty) != DependencyProperty.UnsetValue)
        {
            material.TintColor = TintColor;
        }
        if (ReadLocalValue(TintOpacityProperty) != DependencyProperty.UnsetValue)
        {
            material.TintOpacity = TintOpacity;
        }
        if (ReadLocalValue(EdgeHighlightOpacityProperty) != DependencyProperty.UnsetValue)
        {
            material.EdgeHighlightOpacity = EdgeHighlightOpacity;
        }
        if (ReadLocalValue(RefractionStrengthProperty) != DependencyProperty.UnsetValue)
        {
            material.RefractionStrength = RefractionStrength;
        }

        OnBeforeRefreshMaterial(material);
        _resolvedMaterial = material;

        _renderer.ApplyToTemplateParts(
            this,
            material,
            _backdropBorder,
            _tintBorder,
            _reflectionBorder,
            _refractionHost,
            _highlightBorder,
            EnableElevationShadow);
    }

    private void SyncInteractions()
    {
        if (_isLoaded && EnableHoverEffects)
        {
            GlassAnimationController.AttachInteractions(this, MaterialKind, _highlightBorder);
        }
        else
        {
            GlassAnimationController.DetachInteractions(this);
        }
    }

    private void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        RefreshMaterial();
        SyncInteractions();
    }

    private void OnPanelUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _renderer.DetachRefraction(_refractionHost);
        GlassAnimationController.DetachInteractions(this);
    }

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        SyncCapsuleCornerRadius(e.NewSize.Width, e.NewSize.Height);
        OnSizeUpdated(e.NewSize.Width, e.NewSize.Height);
        if (_resolvedMaterial != null && e.NewSize.Width > 4 && e.NewSize.Height > 4)
        {
            _renderer.UpdateRefractionSize(_refractionHost, _resolvedMaterial, e.NewSize.Width, e.NewSize.Height);
        }
    }

    private void SyncCapsuleCornerRadius(double width, double height)
    {
        if (!IsCapsule || width <= 0 || height <= 0)
        {
            return;
        }

        double r = Math.Min(width, height) * 0.5;
        if (Math.Abs(CornerRadius.TopLeft - r) > 0.2 ||
            Math.Abs(CornerRadius.BottomRight - r) > 0.2)
        {
            CornerRadius = new CornerRadius(r);
        }
    }

    protected virtual void OnSizeUpdated(double width, double height)
    {
    }

    private void OnPanelActualThemeChanged(FrameworkElement sender, object args)
    {
        RefreshMaterial();
    }

    private void OnCornerRadiusPropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        RefreshMaterial();
    }

    private static void OnCapsulePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LiquidGlassPanel panel && panel.ActualWidth > 0 && panel.ActualHeight > 0)
        {
            panel.SyncCapsuleCornerRadius(panel.ActualWidth, panel.ActualHeight);
        }
    }

    private static void OnOpticalPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LiquidGlassPanel panel)
        {
            panel.RefreshMaterial();
        }
    }

    private static void OnInteractionSettingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LiquidGlassPanel panel)
        {
            panel.SyncInteractions();
        }
    }

    internal static ControlTemplate BuildFallbackControlTemplate()
    {
        const string xaml = """
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="ContentControl">
    <Grid CornerRadius="{TemplateBinding CornerRadius}">
        <Border x:Name="PART_Backdrop" IsHitTestVisible="False" CornerRadius="{TemplateBinding CornerRadius}" />
        <Border x:Name="PART_Tint" IsHitTestVisible="False" CornerRadius="{TemplateBinding CornerRadius}" />
        <Border x:Name="PART_Reflection" IsHitTestVisible="False" CornerRadius="{TemplateBinding CornerRadius}" />
        <Grid x:Name="PART_RefractionHost" IsHitTestVisible="False" />
        <Border x:Name="PART_Highlight" IsHitTestVisible="False" CornerRadius="{TemplateBinding CornerRadius}" />
        <ContentPresenter x:Name="PART_Content"
                          Content="{TemplateBinding Content}"
                          ContentTemplate="{TemplateBinding ContentTemplate}"
                          ContentTransitions="{TemplateBinding ContentTransitions}"
                          Padding="{TemplateBinding Padding}"
                          HorizontalContentAlignment="{TemplateBinding HorizontalContentAlignment}"
                          VerticalContentAlignment="{TemplateBinding VerticalContentAlignment}" />
    </Grid>
</ControlTemplate>
""";
        return (ControlTemplate)XamlReader.Load(xaml);
    }
}
