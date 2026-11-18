using LiquidGlass.WinUI.Materials;
using LiquidGlass.WinUI.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace LiquidGlass.WinUI.Controls;

/// <summary>
/// Liquid Glass 胶囊/玻璃交互按钮，自带五层光学表面与 GPU Composition 悬停（1.08x）/按压（0.93x）动效。
/// </summary>
[TemplatePart(Name = PartBackdrop, Type = typeof(Border))]
[TemplatePart(Name = PartTint, Type = typeof(Border))]
[TemplatePart(Name = PartReflection, Type = typeof(Border))]
[TemplatePart(Name = PartRefractionHost, Type = typeof(UIElement))]
[TemplatePart(Name = PartHighlight, Type = typeof(Border))]
public class LiquidGlassButton : Button
{
    private const string PartBackdrop = "PART_Backdrop";
    private const string PartTint = "PART_Tint";
    private const string PartReflection = "PART_Reflection";
    private const string PartRefractionHost = "PART_RefractionHost";
    private const string PartHighlight = "PART_Highlight";

    public static readonly DependencyProperty MaterialKindProperty = DependencyProperty.Register(
        nameof(MaterialKind),
        typeof(LiquidGlassMaterialKind),
        typeof(LiquidGlassButton),
        new PropertyMetadata(LiquidGlassMaterialKind.Button, OnOpticalChanged));

    public static readonly DependencyProperty CustomMaterialProperty = DependencyProperty.Register(
        nameof(CustomMaterial),
        typeof(LiquidGlassMaterial),
        typeof(LiquidGlassButton),
        new PropertyMetadata(null, OnOpticalChanged));

    public static readonly DependencyProperty EnableHoverEffectsProperty = DependencyProperty.Register(
        nameof(EnableHoverEffects),
        typeof(bool),
        typeof(LiquidGlassButton),
        new PropertyMetadata(true, OnInteractionChanged));

    private readonly LiquidGlassRenderer _renderer = new();
    private Border? _backdropBorder;
    private Border? _tintBorder;
    private Border? _reflectionBorder;
    private UIElement? _refractionHost;
    private Border? _highlightBorder;
    private LiquidGlassMaterial? _resolvedMaterial;
    private bool _fallbackTemplateApplied;
    private bool _isLoaded;

    public LiquidGlassButton()
    {
        DefaultStyleKey = typeof(LiquidGlassButton);
        CornerRadius = new CornerRadius(18);
        RegisterPropertyChangedCallback(CornerRadiusProperty, OnCornerRadiusChanged);
        Loaded += OnButtonLoaded;
        Unloaded += OnButtonUnloaded;
        SizeChanged += OnButtonSizeChanged;
        ActualThemeChanged += (_, _) => RefreshMaterial();
    }

    public LiquidGlassMaterialKind MaterialKind
    {
        get => (LiquidGlassMaterialKind)GetValue(MaterialKindProperty);
        set => SetValue(MaterialKindProperty, value);
    }

    public LiquidGlassMaterial? CustomMaterial
    {
        get => (LiquidGlassMaterial?)GetValue(CustomMaterialProperty);
        set => SetValue(CustomMaterialProperty, value);
    }

    public bool EnableHoverEffects
    {
        get => (bool)GetValue(EnableHoverEffectsProperty);
        set => SetValue(EnableHoverEffectsProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _backdropBorder = GetTemplateChild(PartBackdrop) as Border;
        if (_backdropBorder == null && !_fallbackTemplateApplied)
        {
            _fallbackTemplateApplied = true;
            Template = BuildFallbackButtonTemplate();
            return;
        }

        _tintBorder = GetTemplateChild(PartTint) as Border;
        _reflectionBorder = GetTemplateChild(PartReflection) as Border;
        _refractionHost = GetTemplateChild(PartRefractionHost) as UIElement;
        _highlightBorder = GetTemplateChild(PartHighlight) as Border;

        RefreshMaterial();
        SyncInteractions();
    }

    public void RefreshMaterial()
    {
        var hasCustomCorner = ReadLocalValue(CornerRadiusProperty) != DependencyProperty.UnsetValue;
        var material = LiquidGlassMaterialManager.Current.Resolve(
            MaterialKind,
            ActualTheme,
            CustomMaterial,
            hasCustomCorner ? CornerRadius : null);

        _resolvedMaterial = material;
        _renderer.ApplyToTemplateParts(
            this,
            material,
            _backdropBorder,
            _tintBorder,
            _reflectionBorder,
            _refractionHost,
            _highlightBorder);
    }

    private void SyncInteractions()
    {
        if (_isLoaded && EnableHoverEffects)
        {
            GlassAnimationController.AttachInteractions(this, LiquidGlassMaterialKind.Button, _highlightBorder);
        }
        else
        {
            GlassAnimationController.DetachInteractions(this);
        }
    }

    private void OnButtonLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        RefreshMaterial();
        SyncInteractions();
    }

    private void OnButtonUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _renderer.DetachRefraction(_refractionHost);
        GlassAnimationController.DetachInteractions(this);
    }

    private void OnButtonSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_resolvedMaterial != null && e.NewSize.Width > 4 && e.NewSize.Height > 4)
        {
            _renderer.UpdateRefractionSize(_refractionHost, _resolvedMaterial, e.NewSize.Width, e.NewSize.Height);
        }
    }

    private void OnCornerRadiusChanged(DependencyObject sender, DependencyProperty dp)
    {
        RefreshMaterial();
    }

    private static void OnOpticalChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LiquidGlassButton btn)
        {
            btn.RefreshMaterial();
        }
    }

    private static void OnInteractionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LiquidGlassButton btn)
        {
            btn.SyncInteractions();
        }
    }

    internal static ControlTemplate BuildFallbackButtonTemplate()
    {
        const string xaml = """
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="Button">
    <Grid CornerRadius="{TemplateBinding CornerRadius}">
        <Border x:Name="PART_Backdrop" IsHitTestVisible="False" CornerRadius="{TemplateBinding CornerRadius}" />
        <Border x:Name="PART_Tint" IsHitTestVisible="False" CornerRadius="{TemplateBinding CornerRadius}" />
        <Border x:Name="PART_Reflection" IsHitTestVisible="False" CornerRadius="{TemplateBinding CornerRadius}" />
        <Grid x:Name="PART_RefractionHost" IsHitTestVisible="False" />
        <Border x:Name="PART_Highlight" IsHitTestVisible="False" CornerRadius="{TemplateBinding CornerRadius}" />
        <ContentPresenter x:Name="ContentPresenter"
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
