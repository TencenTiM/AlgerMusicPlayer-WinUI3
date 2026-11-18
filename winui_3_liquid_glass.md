# WinUI 3 Liquid Glass（液体玻璃）材质组件开发规范与完整实现

> **AI Agent / LLM 调用指令（System Context）**
> 本文档为 WinUI 3 平台实现原生级 **Liquid Glass（液体玻璃）** 实时光学折射材质的完整技术规范。当你在项目中集成或修改此组件时，请严格遵守以下工程约束：
> 1. **严禁省略代码**：生成或修改任何文件时必须输出完整源码，不得使用省略号或占位注释。
> 2. **保持渲染管线完整**：必须保留 `CompositionBackdropBrush` 双通道输入（Input 0 清晰背景用于边缘透镜折射，Input 1 高斯模糊用于中心衬底）与 ComputeSharp `D2D1PixelShader` 架构，不得退化为普通的 `AcrylicBrush`。
> 3. **隔离修改**：在将此组件接入现有业务页面时，不得破坏宿主项目原有的视觉树结构与其他业务逻辑。
> 4. **命名空间适配**：集成时请将示例代码中的根命名空间 `LiquidGlassWinUI` 替换为目标项目的实际命名空间（或在独立模块中保持一致）。

---

## 1. 视觉与光学物理特征定义

本方案还原真实 iOS Liquid Glass 的底层渲染逻辑，摒弃廉价的纯白描边与塑料内阴影，核心光学特征如下：

1. **双路背景输入管线（Dual-Input Backdrop）**：
   * **Input 0（清晰背景采样源）**：直接绑定 `Compositor.CreateBackdropBrush()`，保留组件下方 UI 的高频像素细节，供边缘倒角折射采样。
   * **Input 1（模糊背景采样源）**：通过 Win2D `GaussianBlurEffect` 对 `BackdropBrush` 进行实时预模糊，供组件中心区域混合以保证文字与图标可读性。
2. **圆角矩形有向距离场（Rounded Box SDF）**：在 HLSL 像素着色器中精确计算当前物理像素到圆角矩形边界的有向距离 $d$ 及有限差分梯度法线 $\vec{N}$。
3. **边缘倒角透镜折射（Bevel Lensing）**：在距离边界 `BevelWidth` 范围内，利用非线性幂函数曲线模拟凸透镜弧形边缘斜率，沿法线反方向执行 UV 采样偏移，使底层流光与色彩穿过边缘时产生自然的向内弯折、拉伸与折叠。
4. **中心-边缘分区模糊插值（Zonal Blur Mixing）**：中心区域按 `CenterBlurWeight` 采纳模糊背景，向边缘平滑过渡至低模糊度的 `EdgeBlurWeight`，实现中心磨砂保可读性、边缘清透吸取环境色的混合质感。
5. **切线菲涅尔微光（Fresnel Specular Rim）**：基于边缘 2D 法线与预设主副光源方向的点积，仅在最外圈约 $2.5\text{px}$ 的切线方向叠加极薄冷调反光，不产生全包围死板白框。

---

## 2. 项目依赖与工程配置 (`.csproj`)

目标 WinUI 3 项目必须开启 `AllowUnsafeBlocks`（供 ComputeSharp 源生成器在编译期生成原生 Direct2D HLSL 字节码），并引用 `Microsoft.Graphics.Win2D` 与 `ComputeSharp.D2D1`。

请在项目的 `.csproj` 文件中确保包含以下节点：

```xml
<PropertyGroup>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Microsoft.Graphics.Win2D" Version="1.2.0" />
  <PackageReference Include="ComputeSharp.D2D1" Version="3.1.0" />
</ItemGroup>
```

---

## 3. 标准文件结构

请在目标项目中按以下路径创建对应代码文件：

* `Shaders/LiquidGlassShader.cs` —— D2D1 HLSL 像素着色器结构体（ComputeSharp 编译期生成）
* `Brushes/LiquidGlassBrush.cs` —— 自定义 `XamlCompositionBrushBase` 合成器画刷封装
* `Controls/LiquidGlassPanel.cs` —— 自适应尺寸与 DPI 缩放的开箱即用容器控件

---

## 4. 完整源代码实现

### 4.1 像素着色器：`Shaders/LiquidGlassShader.cs`

```csharp
using ComputeSharp;
using ComputeSharp.D2D1;

namespace LiquidGlassWinUI.Shaders
{
    /// <summary>
    /// Liquid Glass D2D1 像素着色器
    /// Input 0: 原始清晰 Backdrop 纹理（复杂坐标采样，用于边缘折射）
    /// Input 1: 高斯模糊 Backdrop 纹理（复杂坐标采样，用于中心内容衬底）
    /// </summary>
    [D2DInputCount(2)]
    [D2DInputComplex(0)]
    [D2DInputComplex(1)]
    [D2DRequiresScenePosition]
    [D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
    [D2DGeneratedPixelShaderDescriptor]
    public readonly partial struct LiquidGlassShader : ID2D1PixelShader
    {
        public readonly float2 ControlSize;
        public readonly float CornerRadius;
        public readonly float BevelWidth;
        public readonly float RefractionStrength;
        public readonly float CenterBlurWeight;
        public readonly float EdgeBlurWeight;
        public readonly float4 TintColor;
        public readonly float SpecularIntensity;

        public LiquidGlassShader(
            float2 controlSize,
            float cornerRadius,
            float bevelWidth,
            float refractionStrength,
            float centerBlurWeight,
            float edgeBlurWeight,
            float4 tintColor,
            float specularIntensity)
        {
            ControlSize = controlSize;
            CornerRadius = cornerRadius;
            BevelWidth = bevelWidth;
            RefractionStrength = refractionStrength;
            CenterBlurWeight = centerBlurWeight;
            EdgeBlurWeight = edgeBlurWeight;
            TintColor = tintColor;
            SpecularIntensity = specularIntensity;
        }

        private static float SdfRoundedBox(float2 p, float2 b, float r)
        {
            float2 q = Hlsl.Abs(p) - b + r;
            return Hlsl.Min(Hlsl.Max(q.X, q.Y), 0.0f) + Hlsl.Length(Hlsl.Max(q, 0.0f)) - r;
        }

        public float4 Execute()
        {
            // 获取当前像素在画刷局部空间中的物理像素坐标
            float2 pos = D2D.GetScenePosition().XY;
            float2 halfSize = ControlSize * 0.5f;
            float2 centerRelPos = pos - halfSize;

            float safeRadius = Hlsl.Clamp(CornerRadius, 0.0f, Hlsl.Min(halfSize.X, halfSize.Y));
            float dist = SdfRoundedBox(centerRelPos, halfSize, safeRadius);

            // 圆角外部抗锯齿透明裁剪（1.0px 边缘羽化）
            if (dist > 0.5f)
            {
                return new float4(0.0f, 0.0f, 0.0f, 0.0f);
            }
            float edgeAlpha = 1.0f - Hlsl.SmoothStep(-0.5f, 0.5f, dist);

            // 使用有限差分计算 SDF 梯度（指向边界外侧的 2D 单位法线）
            const float eps = 1.0f;
            float dx = SdfRoundedBox(centerRelPos + new float2(eps, 0.0f), halfSize, safeRadius)
                     - SdfRoundedBox(centerRelPos - new float2(eps, 0.0f), halfSize, safeRadius);
            float dy = SdfRoundedBox(centerRelPos + new float2(0.0f, eps), halfSize, safeRadius)
                     - SdfRoundedBox(centerRelPos - new float2(0.0f, eps), halfSize, safeRadius);
            float2 grad = new float2(dx, dy);
            float gradLen = Hlsl.Length(grad);
            float2 normal2D = gradLen > 0.0001f ? grad / gradLen : new float2(0.0f, -1.0f);

            // 计算当前像素进入玻璃内侧的深度（0 = 最外边缘，BevelWidth = 倒角结束进入平坦区）
            float inwardDist = -dist;
            float safeBevel = Hlsl.Max(BevelWidth, 1.0f);

            // bevelCurve: 最外边缘为 1.0，向内平滑衰减至 0.0
            float t = Hlsl.Saturate(inwardDist / safeBevel);
            float bevelCurve = 1.0f - Hlsl.SmoothStep(0.0f, 1.0f, t);

            // 模拟凸透镜弧形边缘斜率：越靠近最外圈曲率越大，向内折射偏移越显著
            float lensProfile = Hlsl.Pow(bevelCurve, 2.2f);
            float2 uvOffset = -normal2D * lensProfile * RefractionStrength;

            float2 sampleCoord = pos + uvOffset;

            float4 sharpSample = D2D.SampleInputAtPosition(0, sampleCoord);
            float4 blurredSample = D2D.SampleInputAtPosition(1, sampleCoord);

            // 分区模糊混合：中心采用 CenterBlurWeight，边缘倒角区过渡至 EdgeBlurWeight
            float blurMix = Hlsl.Lerp(CenterBlurWeight, EdgeBlurWeight, bevelCurve);
            float4 refractedColor = Hlsl.Lerp(sharpSample, blurredSample, blurMix);

            // 叠加环境底色
            float3 tintedRgb = Hlsl.Lerp(refractedColor.RGB, TintColor.RGB, TintColor.W);

            // 切线菲涅尔冷光：模拟左上与底部反光切线，不产生死板全包围白框
            float2 primaryLightDir = Hlsl.Normalize(new float2(-0.6f, -0.8f));
            float2 secondaryLightDir = Hlsl.Normalize(new float2(0.3f, 0.95f));

            float topLeftCatch = Hlsl.Saturate(Hlsl.Dot(normal2D, primaryLightDir));
            float bottomCatch = Hlsl.Saturate(Hlsl.Dot(normal2D, secondaryLightDir)) * 0.55f;

            // 仅在最外侧约 2.5px 极窄区域生成镜面高光
            float rimMask = Hlsl.Pow(1.0f - Hlsl.Saturate(inwardDist / 2.5f), 3.0f);
            float specular = rimMask * (topLeftCatch + bottomCatch) * SpecularIntensity;

            float3 finalRgb = Hlsl.Saturate(tintedRgb + new float3(specular, specular * 1.02f, specular * 1.05f));

            return new float4(finalRgb * edgeAlpha, edgeAlpha);
        }
    }
}
```

---

### 4.2 合成画刷：`Brushes/LiquidGlassBrush.cs`

```csharp
using System;
using System.Numerics;
using ComputeSharp;
using ComputeSharp.D2D1.Interop;
using LiquidGlassWinUI.Shaders;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LiquidGlassWinUI.Brushes
{
    public sealed class LiquidGlassBrush : XamlCompositionBrushBase
    {
        private CompositionEffectBrush? _effectBrush;
        private GaussianBlurEffect? _blurEffect;
        private PixelShaderEffect<LiquidGlassShader>? _liquidEffect;

        public Vector2 RenderSize
        {
            get => (Vector2)GetValue(RenderSizeProperty);
            set => SetValue(RenderSizeProperty, value);
        }

        public static readonly DependencyProperty RenderSizeProperty =
            DependencyProperty.Register(nameof(RenderSize), typeof(Vector2), typeof(LiquidGlassBrush),
                new PropertyMetadata(new Vector2(300f, 80f), OnShaderParameterChanged));

        public double CornerRadius
        {
            get => (double)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register(nameof(CornerRadius), typeof(double), typeof(LiquidGlassBrush),
                new PropertyMetadata(28.0, OnShaderParameterChanged));

        public double BevelWidth
        {
            get => (double)GetValue(BevelWidthProperty);
            set => SetValue(BevelWidthProperty, value);
        }

        public static readonly DependencyProperty BevelWidthProperty =
            DependencyProperty.Register(nameof(BevelWidth), typeof(double), typeof(LiquidGlassBrush),
                new PropertyMetadata(14.0, OnShaderParameterChanged));

        public double RefractionStrength
        {
            get => (double)GetValue(RefractionStrengthProperty);
            set => SetValue(RefractionStrengthProperty, value);
        }

        public static readonly DependencyProperty RefractionStrengthProperty =
            DependencyProperty.Register(nameof(RefractionStrength), typeof(double), typeof(LiquidGlassBrush),
                new PropertyMetadata(22.0, OnShaderParameterChanged));

        public double BlurAmount
        {
            get => (double)GetValue(BlurAmountProperty);
            set => SetValue(BlurAmountProperty, value);
        }

        public static readonly DependencyProperty BlurAmountProperty =
            DependencyProperty.Register(nameof(BlurAmount), typeof(double), typeof(LiquidGlassBrush),
                new PropertyMetadata(18.0, OnBlurAmountChanged));

        public double CenterBlurWeight
        {
            get => (double)GetValue(CenterBlurWeightProperty);
            set => SetValue(CenterBlurWeightProperty, value);
        }

        public static readonly DependencyProperty CenterBlurWeightProperty =
            DependencyProperty.Register(nameof(CenterBlurWeight), typeof(double), typeof(LiquidGlassBrush),
                new PropertyMetadata(0.25, OnShaderParameterChanged));

        public double EdgeBlurWeight
        {
            get => (double)GetValue(EdgeBlurWeightProperty);
            set => SetValue(EdgeBlurWeightProperty, value);
        }

        public static readonly DependencyProperty EdgeBlurWeightProperty =
            DependencyProperty.Register(nameof(EdgeBlurWeight), typeof(double), typeof(LiquidGlassBrush),
                new PropertyMetadata(0.05, OnShaderParameterChanged));

        public Color TintColor
        {
            get => (Color)GetValue(TintColorProperty);
            set => SetValue(TintColorProperty, value);
        }

        public static readonly DependencyProperty TintColorProperty =
            DependencyProperty.Register(nameof(TintColor), typeof(Color), typeof(LiquidGlassBrush),
                new PropertyMetadata(Color.FromArgb(45, 20, 20, 24), OnShaderParameterChanged));

        public double SpecularIntensity
        {
            get => (double)GetValue(SpecularIntensityProperty);
            set => SetValue(SpecularIntensityProperty, value);
        }

        public static readonly DependencyProperty SpecularIntensityProperty =
            DependencyProperty.Register(nameof(SpecularIntensity), typeof(double), typeof(LiquidGlassBrush),
                new PropertyMetadata(0.38, OnShaderParameterChanged));

        public double RasterizationScale { get; set; } = 1.0;

        private static void OnShaderParameterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LiquidGlassBrush brush)
            {
                brush.RebuildPipeline();
            }
        }

        private static void OnBlurAmountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LiquidGlassBrush brush)
            {
                brush.RebuildPipeline();
            }
        }

        protected override void OnConnected()
        {
            base.OnConnected();
            RebuildPipeline();
        }

        protected override void OnDisconnected()
        {
            base.OnDisconnected();
            DisposeResources();
        }

        public void RebuildPipeline()
        {
            Compositor? compositor = CompositionTarget.GetCompositorForCurrentThread();
            if (compositor == null)
            {
                return;
            }

            // 计算考虑 DPI 缩放后的物理像素尺寸
            float scale = (float)Math.Max(RasterizationScale, 1.0);
            float widthPx = Math.Max(1.0f, RenderSize.X * scale);
            float heightPx = Math.Max(1.0f, RenderSize.Y * scale);
            float radiusPx = (float)CornerRadius * scale;
            float bevelPx = (float)BevelWidth * scale;
            float refractionPx = (float)RefractionStrength * scale;

            Color c = TintColor;
            float4 tintVec = new float4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

            DisposeEffectsOnly();

            var backdropSource = new CompositionEffectSourceParameter("Backdrop");

            _blurEffect = new GaussianBlurEffect
            {
                Name = "BackdropBlur",
                BlurAmount = (float)(BlurAmount * scale),
                BorderMode = EffectBorderMode.Hard,
                Optimization = EffectOptimization.Balanced,
                Source = backdropSource
            };

            _liquidEffect = new PixelShaderEffect<LiquidGlassShader>
            {
                ConstantBuffer = new LiquidGlassShader(
                    controlSize: new float2(widthPx, heightPx),
                    cornerRadius: radiusPx,
                    bevelWidth: bevelPx,
                    refractionStrength: refractionPx,
                    centerBlurWeight: (float)CenterBlurWeight,
                    edgeBlurWeight: (float)EdgeBlurWeight,
                    tintColor: tintVec,
                    specularIntensity: (float)SpecularIntensity)
            };

            // Source 0: 清晰背景；Source 1: 模糊背景
            _liquidEffect.Sources[0] = backdropSource;
            _liquidEffect.Sources[1] = _blurEffect;

            CompositionEffectFactory factory = compositor.CreateEffectFactory(_liquidEffect);
            _effectBrush = factory.CreateBrush();
            _effectBrush.SetSourceParameter("Backdrop", compositor.CreateBackdropBrush());

            CompositionBrush = _effectBrush;
        }

        private void DisposeEffectsOnly()
        {
            _liquidEffect?.Dispose();
            _liquidEffect = null;

            _blurEffect?.Dispose();
            _blurEffect = null;
        }

        private void DisposeResources()
        {
            if (CompositionBrush != null)
            {
                CompositionBrush.Dispose();
                CompositionBrush = null;
            }
            _effectBrush = null;
            DisposeEffectsOnly();
        }
    }
}
```

---

### 4.3 容器控件：`Controls/LiquidGlassPanel.cs`

该控件不依赖外部 `Generic.xaml` 样式表，重写了内部视觉承载逻辑，放入任何 XAML 页面即可直接使用。

```csharp
using System;
using System.Numerics;
using LiquidGlassWinUI.Brushes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace LiquidGlassWinUI.Controls
{
    public enum LiquidGlassPreset
    {
        Custom,
        /// <summary>
        /// 高透强折射态（对应 iOS Dock / 搜索胶囊：中心清透、边缘强透镜折叠）
        /// </summary>
        ClearDock,
        /// <summary>
        /// 磨砂折射混合态（对应 iOS 展开文件夹：中心重模糊保可读性、边缘清透吸色）
        /// </summary>
        FrostedFolder
    }

    public sealed class LiquidGlassPanel : ContentControl
    {
        private readonly Border _backdropHost;
        private readonly ContentPresenter _contentHost;
        private readonly Grid _rootGrid;
        private readonly LiquidGlassBrush _glassBrush;
        private bool _isVisualTreeInitialized;

        public LiquidGlassPreset Preset
        {
            get => (LiquidGlassPreset)GetValue(PresetProperty);
            set => SetValue(PresetProperty, value);
        }

        public static readonly DependencyProperty PresetProperty =
            DependencyProperty.Register(nameof(Preset), typeof(LiquidGlassPreset), typeof(LiquidGlassPanel),
                new PropertyMetadata(LiquidGlassPreset.ClearDock, OnPresetChanged));

        public double GlassCornerRadius
        {
            get => (double)GetValue(GlassCornerRadiusProperty);
            set => SetValue(GlassCornerRadiusProperty, value);
        }

        public static readonly DependencyProperty GlassCornerRadiusProperty =
            DependencyProperty.Register(nameof(GlassCornerRadius), typeof(double), typeof(LiquidGlassPanel),
                new PropertyMetadata(32.0, OnVisualPropertyChanged));

        public double BevelWidth
        {
            get => (double)GetValue(BevelWidthProperty);
            set => SetValue(BevelWidthProperty, value);
        }

        public static readonly DependencyProperty BevelWidthProperty =
            DependencyProperty.Register(nameof(BevelWidth), typeof(double), typeof(LiquidGlassPanel),
                new PropertyMetadata(14.0, OnVisualPropertyChanged));

        public double RefractionStrength
        {
            get => (double)GetValue(RefractionStrengthProperty);
            set => SetValue(RefractionStrengthProperty, value);
        }

        public static readonly DependencyProperty RefractionStrengthProperty =
            DependencyProperty.Register(nameof(RefractionStrength), typeof(double), typeof(LiquidGlassPanel),
                new PropertyMetadata(24.0, OnVisualPropertyChanged));

        public double BlurAmount
        {
            get => (double)GetValue(BlurAmountProperty);
            set => SetValue(BlurAmountProperty, value);
        }

        public static readonly DependencyProperty BlurAmountProperty =
            DependencyProperty.Register(nameof(BlurAmount), typeof(double), typeof(LiquidGlassPanel),
                new PropertyMetadata(20.0, OnVisualPropertyChanged));

        public double CenterBlurWeight
        {
            get => (double)GetValue(CenterBlurWeightProperty);
            set => SetValue(CenterBlurWeightProperty, value);
        }

        public static readonly DependencyProperty CenterBlurWeightProperty =
            DependencyProperty.Register(nameof(CenterBlurWeight), typeof(double), typeof(LiquidGlassPanel),
                new PropertyMetadata(0.18, OnVisualPropertyChanged));

        public double EdgeBlurWeight
        {
            get => (double)GetValue(EdgeBlurWeightProperty);
            set => SetValue(EdgeBlurWeightProperty, value);
        }

        public static readonly DependencyProperty EdgeBlurWeightProperty =
            DependencyProperty.Register(nameof(EdgeBlurWeight), typeof(double), typeof(LiquidGlassPanel),
                new PropertyMetadata(0.02, OnVisualPropertyChanged));

        public Color GlassTintColor
        {
            get => (Color)GetValue(GlassTintColorProperty);
            set => SetValue(GlassTintColorProperty, value);
        }

        public static readonly DependencyProperty GlassTintColorProperty =
            DependencyProperty.Register(nameof(GlassTintColor), typeof(Color), typeof(LiquidGlassPanel),
                new PropertyMetadata(Color.FromArgb(42, 18, 18, 22), OnVisualPropertyChanged));

        public double SpecularIntensity
        {
            get => (double)GetValue(SpecularIntensityProperty);
            set => SetValue(SpecularIntensityProperty, value);
        }

        public static readonly DependencyProperty SpecularIntensityProperty =
            DependencyProperty.Register(nameof(SpecularIntensity), typeof(double), typeof(LiquidGlassPanel),
                new PropertyMetadata(0.36, OnVisualPropertyChanged));

        public LiquidGlassPanel()
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;

            _glassBrush = new LiquidGlassBrush();

            _backdropHost = new Border
            {
                Background = _glassBrush,
                IsHitTestVisible = false
            };

            _contentHost = new ContentPresenter
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            _rootGrid = new Grid();
            _rootGrid.Children.Add(_backdropHost);
            _rootGrid.Children.Add(_contentHost);

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += OnSizeChanged;

            RegisterPropertyChangedCallback(ContentProperty, OnExternalContentChanged);
            RegisterPropertyChangedCallback(PaddingProperty, (_, _) => _contentHost.Padding = Padding);
        }

        private void OnExternalContentChanged(DependencyObject sender, DependencyProperty dp)
        {
            if (Content != _rootGrid)
            {
                object? externalContent = Content;
                _contentHost.Content = externalContent;
                if (_isVisualTreeInitialized)
                {
                    Content = _rootGrid;
                }
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_isVisualTreeInitialized)
            {
                _isVisualTreeInitialized = true;
                if (Content != _rootGrid)
                {
                    object? userContent = Content;
                    _contentHost.Content = userContent;
                    _contentHost.Padding = Padding;
                    Content = _rootGrid;
                }
            }

            if (XamlRoot != null)
            {
                _glassBrush.RasterizationScale = XamlRoot.RasterizationScale;
                XamlRoot.Changed += OnXamlRootChanged;
            }

            ApplyPresetValues(Preset);
            SyncBrushProperties();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (XamlRoot != null)
            {
                XamlRoot.Changed -= OnXamlRootChanged;
            }
        }

        private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            if (Math.Abs(_glassBrush.RasterizationScale - sender.RasterizationScale) > 0.001)
            {
                _glassBrush.RasterizationScale = sender.RasterizationScale;
                _glassBrush.RebuildPipeline();
            }
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _glassBrush.RenderSize = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
        }

        private static void OnPresetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LiquidGlassPanel panel && e.NewValue is LiquidGlassPreset preset)
            {
                panel.ApplyPresetValues(preset);
            }
        }

        private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LiquidGlassPanel panel)
            {
                panel.SyncBrushProperties();
            }
        }

        private void ApplyPresetValues(LiquidGlassPreset preset)
        {
            if (preset == LiquidGlassPreset.ClearDock)
            {
                BevelWidth = 14.0;
                RefractionStrength = 24.0;
                BlurAmount = 16.0;
                CenterBlurWeight = 0.16;
                EdgeBlurWeight = 0.02;
                GlassTintColor = Color.FromArgb(45, 16, 16, 20);
                SpecularIntensity = 0.40;
            }
            else if (preset == LiquidGlassPreset.FrostedFolder)
            {
                BevelWidth = 18.0;
                RefractionStrength = 20.0;
                BlurAmount = 28.0;
                CenterBlurWeight = 0.88;
                EdgeBlurWeight = 0.15;
                GlassTintColor = Color.FromArgb(65, 24, 24, 30);
                SpecularIntensity = 0.32;
            }
        }

        private void SyncBrushProperties()
        {
            _backdropHost.CornerRadius = new CornerRadius(GlassCornerRadius);
            _glassBrush.CornerRadius = GlassCornerRadius;
            _glassBrush.BevelWidth = BevelWidth;
            _glassBrush.RefractionStrength = RefractionStrength;
            _glassBrush.BlurAmount = BlurAmount;
            _glassBrush.CenterBlurWeight = CenterBlurWeight;
            _glassBrush.EdgeBlurWeight = EdgeBlurWeight;
            _glassBrush.TintColor = GlassTintColor;
            _glassBrush.SpecularIntensity = SpecularIntensity;
        }
    }
}
```

---

## 5. XAML 页面集成示例 (`MainWindow.xaml`)

```xml
<Window
    x:Class="LiquidGlassWinUI.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:controls="using:LiquidGlassWinUI.Controls">

    <Grid Background="#0B0C10">
        <!-- 底层高对比度滚动内容（测试实时透镜折射变形） -->
        <ScrollViewer VerticalScrollBarVisibility="Hidden">
            <StackPanel Spacing="24" Padding="40,60,40,220">
                <Border Height="220" CornerRadius="24">
                    <Border.Background>
                        <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
                            <GradientStop Color="#FF3B30" Offset="0.0"/>
                            <GradientStop Color="#FF9500" Offset="0.5"/>
                            <GradientStop Color="#AF52DE" Offset="1.0"/>
                        </LinearGradientBrush>
                    </Border.Background>
                </Border>
                <Border Height="220" CornerRadius="24">
                    <Border.Background>
                        <LinearGradientBrush StartPoint="0,1" EndPoint="1,0">
                            <GradientStop Color="#34C759" Offset="0.0"/>
                            <GradientStop Color="#00C7BE" Offset="0.5"/>
                            <GradientStop Color="#007AFF" Offset="1.0"/>
                        </LinearGradientBrush>
                    </Border.Background>
                </Border>
                <Border Height="320" CornerRadius="24" Background="#1C1C1E"/>
            </StackPanel>
        </ScrollViewer>

        <!-- 预设 1：展开文件夹态（中心磨砂保文字可读性，四边大圆角清透折射吸色） -->
        <controls:LiquidGlassPanel
            Width="320"
            Height="320"
            HorizontalAlignment="Center"
            VerticalAlignment="Center"
            Preset="FrostedFolder"
            GlassCornerRadius="44"
            Padding="28">
            <Grid RowDefinitions="*,*" ColumnDefinitions="*,*,*" RowSpacing="20" ColumnSpacing="20">
                <StackPanel Grid.Row="0" Grid.Column="0" HorizontalAlignment="Center" Spacing="6">
                    <Border Width="60" Height="60" CornerRadius="14" Background="#141416"/>
                    <TextBlock Text="语音备忘录" Foreground="White" FontSize="12" HorizontalAlignment="Center"/>
                </StackPanel>
                <StackPanel Grid.Row="0" Grid.Column="1" HorizontalAlignment="Center" Spacing="6">
                    <Border Width="60" Height="60" CornerRadius="14" Background="#141416"/>
                    <TextBlock Text="指南针" Foreground="White" FontSize="12" HorizontalAlignment="Center"/>
                </StackPanel>
                <StackPanel Grid.Row="0" Grid.Column="2" HorizontalAlignment="Center" Spacing="6">
                    <Border Width="60" Height="60" CornerRadius="14" Background="#141416"/>
                    <TextBlock Text="测距仪" Foreground="White" FontSize="12" HorizontalAlignment="Center"/>
                </StackPanel>
                <StackPanel Grid.Row="1" Grid.Column="0" HorizontalAlignment="Center" Spacing="6">
                    <Border Width="60" Height="60" CornerRadius="14" Background="#141416"/>
                    <TextBlock Text="放大器" Foreground="White" FontSize="12" HorizontalAlignment="Center"/>
                </StackPanel>
                <StackPanel Grid.Row="1" Grid.Column="1" HorizontalAlignment="Center" Spacing="6">
                    <Border Width="60" Height="60" CornerRadius="14" Background="#8E8E93"/>
                    <TextBlock Text="计算器" Foreground="White" FontSize="12" HorizontalAlignment="Center"/>
                </StackPanel>
                <StackPanel Grid.Row="1" Grid.Column="2" HorizontalAlignment="Center" Spacing="6">
                    <Border Width="60" Height="60" CornerRadius="14" Background="#141416"/>
                    <TextBlock Text="密码" Foreground="White" FontSize="12" HorizontalAlignment="Center"/>
                </StackPanel>
            </Grid>
        </controls:LiquidGlassPanel>

        <!-- 预设 2：底部悬浮 Dock 胶囊栏态（中心清透，边缘强透镜向内弯折） -->
        <controls:LiquidGlassPanel
            Width="360"
            Height="92"
            Margin="0,0,0,28"
            HorizontalAlignment="Center"
            VerticalAlignment="Bottom"
            Preset="ClearDock"
            GlassCornerRadius="46"
            Padding="18,14">
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Center" Spacing="22">
                <Border Width="60" Height="60" CornerRadius="15" Background="#34C759"/>
                <Border Width="60" Height="60" CornerRadius="15" Background="#E5E5EA"/>
                <Border Width="60" Height="60" CornerRadius="15" Background="#30D158"/>
                <Border Width="60" Height="60" CornerRadius="15" Background="#007AFF"/>
            </StackPanel>
        </controls:LiquidGlassPanel>
    </Grid>
</Window>
```

---

## 6. 光学参数调优矩阵

当 `Preset="Custom"` 时，可通过以下依赖属性微调玻璃光学质感：

| 属性名 | 类型 | 默认值 | ClearDock 预设 | FrostedFolder 预设 | 物理作用说明 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `GlassCornerRadius` | `double` | `32.0` | 胶囊取 `Height/2` | `44.0` | 圆角半径（逻辑像素 dp），决定 SDF 曲率拐角区域 |
| `BevelWidth` | `double` | `14.0` | `14.0` | `18.0` | 边缘弧形倒角宽度（dp），控制距离边界多宽的区域产生折射扭曲 |
| `RefractionStrength` | `double` | `24.0` | `24.0` | `20.0` | 法线方向 UV 折射偏移强度（dp），正值代表凸透镜向内压缩折叠采样 |
| `BlurAmount` | `double` | `20.0` | `16.0` | `28.0` | Input 1 通道的高斯模糊半径基准值 |
| `CenterBlurWeight` | `double` | `0.18` | `0.16` | `0.88` | 中心平坦区模糊混合权重（`0.0` 完全清晰，`1.0` 完全模糊） |
| `EdgeBlurWeight` | `double` | `0.02` | `0.02` | `0.15` | 边缘倒角区模糊混合权重，保持 `< 0.2` 以呈现清透边缘吸色质感 |
| `GlassTintColor` | `Color` | `#2A121216` | `#2D101014` | `#4118181E` | 玻璃基底环境色（含 Alpha 通道控制浓度） |
| `SpecularIntensity` | `double` | `0.36` | `0.40` | `0.32` | 左上与底部切线方向的菲涅尔极细冷光反光强度 |

---

## 7. 边界条件与平台限制说明

1. **窗口内视觉树穿透范围**：`CompositionBackdropBrush` 实时采样当前应用窗口视觉树中位于该控件下方的所有 XAML 元素（背景图、列表、视频帧、渐变色块等），不支持跨进程读取 Windows 桌面壁纸或其他应用窗口像素（DWM 安全隔离限制）。
2. **动态修改 Content 兼容性**：`LiquidGlassPanel` 已监听 `ContentProperty` 变更回调，支持在运行时通过代码或 MVVM 数据绑定动态替换内部子元素而不会覆盖底层的 `_backdropHost` 渲染层。
3. **高分屏多显示器自适应**：控件自动监听 `XamlRoot.Changed` 事件，在窗口跨不同缩放比例（如 100% 移至 150%/200%）显示器移动时，自动重算物理像素尺寸并重建着色器常量缓冲区，确保圆角与倒角边缘不会模糊或错位。