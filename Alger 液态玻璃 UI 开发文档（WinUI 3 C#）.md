# Alger 液态玻璃 UI 开发文档（WinUI 3 / C#）

Nov 15, 2026 · @v

目标：在 Alger Music Player 的 WinUI 3 界面中还原 Liquid Glass 玻璃质感，覆盖截图中的全部组件；本文档按"可直接执行"的粒度写给开发智能体。

## 1. 范围与技术约束

本期只做 UI 皮肤层：把现有页面的容器和控件换成玻璃风格，不改播放、搜索、平台接入等业务逻辑。

**要做**

- 一套可复用的玻璃控件：`GlassPanel`（大面板、胶囊）、`GlassChip`、`GlassIconButton`、玻璃 `Slider`。
- 一个全局背景共享层 `GlassHub`，所有玻璃面板共用同一张背景纹理。
- 把截图里的侧边栏、搜索框、轮播卡片、歌单卡片、筛选 chip、播放条、进度条、音量条全部换成上述控件。
- 阶段二：给侧边栏、播放条、轮播卡片加折射着色器。

**不做**

- 不改任何 ViewModel、服务层和数据绑定的接口。
- 不给专辑封面小图、平台角标做玻璃处理，只加 1px 内描边。
- 不追求逐像素复刻 iOS，目标是同一套视觉语言：模糊、边缘光、折射、弹性。

**技术约束**

| 项 | 要求 |
| --- | --- |
| 框架 | WinUI 3（Windows App SDK 1.5 及以上），C# 12，.NET 8 |
| 目标系统 | Windows 10 19041 及以上 |
| NuGet | `Microsoft.WindowsAppSDK`、`Microsoft.Graphics.Win2D`；阶段二加 `ComputeSharp.D2D1` |
| 渲染路线 | 阶段一用 Composition `CompositionVisualSurface` 加效果图；不要使用 `CreateBackdropBrush` |
| 同屏模糊面板 | 不超过 8 块，模糊半径不超过 28 |

## 2. 视觉规格

所有玻璃元素共用下表参数，写成 `GlassTokens` 静态类或 ResourceDictionary，不要在各控件里写死数字。

| 档位 | 用于 | 模糊半径 | 饱和度 | 着色（ARGB） | 圆角 |
| --- | --- | --- | --- | --- | --- |
| A-大面板 | 侧边栏、播放条 | 26–28 | 1.6 | `#2EFFFFFF` | 24 或胶囊 |
| A-卡片 | 歌单卡片 | 20 | 1.6 | `#1AFFFFFF` | 20 |
| A-搜索 | 顶部搜索胶囊 | 14 | 1.6 | `#18FFFFFF` | 胶囊 |
| B-小控件 | chip、按钮、标签 | 不模糊 | 无 | `#1AFFFFFF` 半透明画笔 | 胶囊 |

**边缘光（所有档位通用）**

- 描边 1.2px，线性渐变，起点左上、终点右下。
- 渐变停靠点：0 处 `#B8FFFFFF`，0.45 和 0.55 处 `#0CFFFFFF`，1 处 `#55FFFFFF`。
- 内高光：顶部 35% 高度内，从 `#22FFFFFF` 渐变到 `#00FFFFFF`。

**强调色**

- 选中态、播放按钮、进度条已播放段使用截图里的青色，主值 `#3DCBF5`，按下态降低 10% 亮度。
- 轨道凹槽：`#26000000` 加 1px `#22FFFFFF` 描边。

**阴影**

- 大面板：Composition `DropShadow`，模糊 40，不透明度 25%，Y 偏移 12。
- 不要使用 `ThemeShadow`，边缘太硬。

**动画**

- 按压：`Scale` 缩到 0.94，弹簧 `DampingRatio` 0.55，`Period` 55 毫秒。
- 悬停：`Scale` 到 1.03，同一套弹簧参数。

## 3. 分层架构

核心思路：背景（青到洋红的渐变加光带）只渲染一次，变成共享纹理；每块玻璃面板只负责"透出自己正后方那一小块背景"，再做模糊和着色。

窗口根 Grid 内，从下到上的叠放顺序：

1. `BackgroundLayer`：铺满窗口的背景元素，包含渐变和光带动画。它必须是玻璃面板的兄弟元素，不能是祖先。
2. 页面内容：侧边栏、轮播、歌单、播放条，其中的大面板用 `GlassPanel`。
3. 每个 `GlassPanel` 内部：`GlassLayer`（空 Border，挂 `SpriteVisual`）、边缘光 Border、`ContentPresenter`，顺序固定。

数据流：

- `GlassHub.Init(RootGrid, BackgroundLayer)` 在页面 `Loaded` 时调用一次，创建 `CompositionVisualSurface`，`SourceVisual` 指向背景层，`SourceSize` 用表达式动画绑定背景层 `Size`。
- 每个 `GlassPanel` 持有一个 `CompositionSurfaceBrush`，指向这张共享纹理，`Offset` 设为面板左上角相对根 Grid 的负坐标。
- 效果图固定为：高斯模糊、饱和度、半透明着色，三者按顺序叠加。

**硬性规则**

- `GlassHub.Init` 必须先于任何 `GlassPanel` 应用模板。
- `SetElementChildVisual` 挂的 Visual 会盖在宿主内容之上，所以玻璃层必须挂在独立的空元素 `GlassLayer` 上，内容放在它后面。
- 小控件（chip、按钮、标签）不抓背景，只用画笔。

## 4. 组件清单（对照截图）

共 17 类组件，逐项落到具体控件和档位。档位含义见第 2 节。

| # | 截图中的组件 | 使用的控件 | 档位 | 实现要点 |
| --- | --- | --- | --- | --- |
| 1 | 左侧边栏整体 | `GlassPanel` | A-大面板 | 圆角只留右侧 `0,24,24,0`；模糊 26 |
| 2 | 侧边栏选中项（"首页"） | `GlassChip` 常驻选中态 | B | 青色半透明填充加亮描边，胶囊形 |
| 3 | 侧边栏普通导航项 | 透明按钮 | B | 悬停时出现 `#1AFFFFFF` 背景，按压弹簧缩放 |
| 4 | 顶部搜索框 | `GlassPanel IsCapsule` 内嵌 `TextBox` | A-搜索 | 聚焦时描边变为青色并加外发光 |
| 5 | 顶部右侧图标（心动、主题、下载、置顶、窗口控制） | `GlassIconButton` | B | 无边框，悬停才出现玻璃底 |
| 6 | 焦点画报轮播大卡片（3 张） | `GlassCard`（图上玻璃） | B | 不模糊；封面图之上加边缘光、顶部内高光、底部黑色渐变用于托住文字 |
| 7 | 轮播左右箭头、分页点、"1/5" | `GlassIconButton`、小圆点 | B | 分页点当前项拉长成胶囊，用弹簧动画过渡 |
| 8 | 卡片角标（网易云首发、QQ音乐臻峰首发） | `GlassChip` 只读 | B | 圆角 10，纯色或半透明平台色 |
| 9 | 圆形播放按钮（卡片内、歌单卡内） | `GlassIconButton` 青色实心 | B | 直径 40，按压缩放 0.94 |
| 10 | 平台筛选 chip 行（全部、网易云、QQ 音乐……） | `GlassChip`（`ToggleButton` 单选） | B | 选中态青色实心加亮描边；未选中半透明加描边 |
| 11 | "刷新"、"播放全部"、"换一批" | `GlassChip` 按钮形 | B | 带图标，胶囊形，悬停变亮 |
| 12 | 新歌首发封面卡片列表 | `GlassCard` | B | 封面圆角 14 加 1px 内描边，右上角 SQ 无损、HI-RES 标签用 `GlassChip` 只读 |
| 13 | 跨平台歌单卡片（3 张） | `GlassPanel` | A-卡片 | 内含封面、标题、前三首歌文字、播放按钮；模糊 20 |
| 14 | 底部播放条 | `GlassPanel IsCapsule` | A-大面板 | 模糊 28；内容区做三栏布局：歌曲信息、transport、右侧工具 |
| 15 | 播放条里的 1.0x、QQ音乐、无损 FLAC 标签 | `GlassChip` 下拉形 | B | 小号，圆角 8 |
| 16 | 进度条、音量条 | 玻璃 `Slider` | B | 轨道凹槽高 6，已播放段青色渐变，滑块为直径 18 的玻璃球加淡青外发光 |
| 17 | 播放条里的圆形图标按钮（上一首、下一首、循环、下载、停止等） | `GlassIconButton` | B | 播放/暂停按钮用青色实心，其余透明底，悬停出玻璃底 |

**不做玻璃处理的元素**：专辑封面小图、平台图标（网易云、QQ 音乐等的彩色图标）、页面分组标题文字。

## 5. 核心代码（阶段一）

以下代码是参考实现，智能体应在此基础上落地并编译通过，命名空间按项目现有的统一调整。

### 5.1 GlassHub：全局背景共享

```csharp
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

public static class GlassHub
{
    public static Compositor Compositor;
    public static UIElement Root;
    public static CompositionVisualSurface Surface;

    // root: 窗口根 Grid；background: 根 Grid 里最底层、铺满窗口的背景元素（必须是玻璃面板的兄弟，不能是祖先）
    public static void Init(UIElement root, UIElement background)
    {
        Root = root;
        var bgVisual = ElementCompositionPreview.GetElementVisual(background);
        Compositor = bgVisual.Compositor;

        Surface = Compositor.CreateVisualSurface();
        Surface.SourceVisual = bgVisual;

        var size = Compositor.CreateExpressionAnimation("bg.Size");
        size.SetReferenceParameter("bg", bgVisual);
        Surface.StartAnimation("SourceSize", size);   // 窗口缩放时自动跟随
    }
}
```

### 5.2 GlassTokens：统一参数

```csharp
public static class GlassTokens
{
    public const double BlurLarge = 28, BlurCard = 20, BlurSearch = 14;
    public const float Saturation = 1.6f;
    public static readonly Color TintLarge  = Color.FromArgb(0x2E, 255, 255, 255);
    public static readonly Color TintCard   = Color.FromArgb(0x1A, 255, 255, 255);
    public static readonly Color TintSearch = Color.FromArgb(0x18, 255, 255, 255);
    public static readonly Color Accent     = Color.FromArgb(0xFF, 0x3D, 0xCB, 0xF5);
}
```

### 5.3 GlassPanel：通用玻璃面板

```csharp
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

public sealed class GlassPanel : ContentControl
{
    public GlassPanel() => DefaultStyleKey = typeof(GlassPanel);

    public static readonly DependencyProperty BlurProperty = DependencyProperty.Register(
        nameof(Blur), typeof(double), typeof(GlassPanel),
        new PropertyMetadata(GlassTokens.BlurCard, (d, _) => ((GlassPanel)d).UpdateEffect()));
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Color), typeof(GlassPanel),
        new PropertyMetadata(GlassTokens.TintCard, (d, _) => ((GlassPanel)d).UpdateEffect()));
    public static readonly DependencyProperty IsCapsuleProperty = DependencyProperty.Register(
        nameof(IsCapsule), typeof(bool), typeof(GlassPanel), new PropertyMetadata(false));

    public double Blur { get => (double)GetValue(BlurProperty); set => SetValue(BlurProperty, value); }
    public Color Tint { get => (Color)GetValue(TintProperty); set => SetValue(TintProperty, value); }
    public bool IsCapsule { get => (bool)GetValue(IsCapsuleProperty); set => SetValue(IsCapsuleProperty, value); }

    Compositor _c;
    SpriteVisual _sprite;
    CompositionSurfaceBrush _bg;
    CompositionEffectBrush _fx;
    CompositionRoundedRectangleGeometry _geo;
    Vector2 _lastOffset = new(float.NaN);

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        var layer = (UIElement)GetTemplateChild("GlassLayer");   // 空元素，只用来挂玻璃
        _c = GlassHub.Compositor;

        _bg = _c.CreateSurfaceBrush(GlassHub.Surface);
        _bg.Stretch = CompositionStretch.None;
        _bg.HorizontalAlignmentRatio = 0;
        _bg.VerticalAlignmentRatio = 0;

        _sprite = _c.CreateSpriteVisual();
        _geo = _c.CreateRoundedRectangleGeometry();
        _sprite.Clip = _c.CreateGeometricClip(_geo);
        BuildEffect();
        ElementCompositionPreview.SetElementChildVisual(layer, _sprite);

        SizeChanged += (_, _) => { ApplySize(); SyncOffset(); };
        LayoutUpdated += (_, _) => SyncOffset();   // 滚动、布局变化时重新对齐背景
    }

    void BuildEffect()
    {
        var graph = new CompositeEffect
        {
            Mode = CanvasComposite.SourceOver,
            Sources =
            {
                new SaturationEffect
                {
                    Saturation = GlassTokens.Saturation,
                    Source = new GaussianBlurEffect
                    {
                        Name = "Blur", BlurAmount = (float)Blur,
                        BorderMode = EffectBorderMode.Hard,
                        Source = new CompositionEffectSourceParameter("bg")
                    }
                },
                new ColorSourceEffect { Name = "Tint", Color = Tint }
            }
        };
        _fx = _c.CreateEffectFactory(graph, new[] { "Blur.BlurAmount", "Tint.Color" }).CreateBrush();
        _fx.SetSourceParameter("bg", _bg);
        _sprite.Brush = _fx;
    }

    void UpdateEffect()
    {
        if (_fx == null) return;
        _fx.Properties.InsertScalar("Blur.BlurAmount", (float)Blur);
        _fx.Properties.InsertColor("Tint.Color", Tint);
    }

    void ApplySize()
    {
        var s = ActualSize;
        _sprite.Size = s;
        _geo.Size = s;
        var cr = CornerRadius;
        float r = IsCapsule ? Math.Min(s.X, s.Y) / 2
                            : (float)Math.Max(Math.Max(cr.TopLeft, cr.TopRight), Math.Max(cr.BottomLeft, cr.BottomRight));
        _geo.CornerRadius = new Vector2(r);
    }

    void SyncOffset()
    {
        if (_bg == null || !IsLoaded || GlassHub.Root == null) return;
        var p = TransformToVisual(GlassHub.Root).TransformPoint(new Windows.Foundation.Point(0, 0));
        var off = new Vector2(-(float)p.X, -(float)p.Y);
        if (off == _lastOffset) return;          // 位置没变就不写，避免滚动时白白重绘
        _lastOffset = off;
        _bg.Offset = off;                        // 让玻璃"透出"正后方那一块背景
    }
}
```

注意：`GlassPanel` 的 Clip 只支持统一圆角，取四个角的最大值。侧边栏需要"只有右侧圆角"时，把面板向左多伸出 24 像素（`Margin="-24,0,0,0"`，宽度加 24），让左侧圆角落在窗口外。

### 5.4 GlassPanel 默认模板（Themes/Generic.xaml）

层序固定为：玻璃层、边缘光、内容。

```xml
<Style TargetType="local:GlassPanel">
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="local:GlassPanel">
        <Grid>
          <Border x:Name="GlassLayer" CornerRadius="{TemplateBinding CornerRadius}"/>
          <Border CornerRadius="{TemplateBinding CornerRadius}" BorderThickness="1.2" IsHitTestVisible="False">
            <Border.BorderBrush>
              <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
                <GradientStop Color="#B8FFFFFF" Offset="0"/>
                <GradientStop Color="#0CFFFFFF" Offset="0.45"/>
                <GradientStop Color="#0CFFFFFF" Offset="0.55"/>
                <GradientStop Color="#55FFFFFF" Offset="1"/>
              </LinearGradientBrush>
            </Border.BorderBrush>
            <Border.Background>
              <LinearGradientBrush StartPoint="0.5,0" EndPoint="0.5,1">
                <GradientStop Color="#22FFFFFF" Offset="0"/>
                <GradientStop Color="#00FFFFFF" Offset="0.35"/>
              </LinearGradientBrush>
            </Border.Background>
          </Border>
          <ContentPresenter Padding="{TemplateBinding Padding}"
                            HorizontalContentAlignment="{TemplateBinding HorizontalContentAlignment}"
                            VerticalContentAlignment="{TemplateBinding VerticalContentAlignment}"/>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
```

页面根结构：

```xml
<Grid x:Name="RootGrid" Loaded="RootGrid_Loaded">
  <Grid x:Name="BackgroundLayer"><!-- 渐变和光带 --></Grid>
  <!-- 其余页面内容 -->
</Grid>
```

`RootGrid_Loaded` 里只写一行：`GlassHub.Init(RootGrid, BackgroundLayer);`。

### 5.5 GlassChip：筛选 chip 和小按钮（不抓背景）

```xml
<Style x:Key="GlassChip" TargetType="ToggleButton">
  <Setter Property="Padding" Value="14,5"/>
  <Setter Property="Foreground" Value="White"/>
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="ToggleButton">
        <Border x:Name="Root" CornerRadius="999" Padding="{TemplateBinding Padding}"
                Background="#1AFFFFFF" BorderBrush="#55FFFFFF" BorderThickness="1">
          <VisualStateManager.VisualStateGroups>
            <VisualStateGroup x:Name="CommonStates">
              <VisualState x:Name="Normal"/>
              <VisualState x:Name="PointerOver">
                <VisualState.Setters><Setter Target="Root.Background" Value="#33FFFFFF"/></VisualState.Setters>
              </VisualState>
              <VisualState x:Name="Checked">
                <VisualState.Setters>
                  <Setter Target="Root.Background" Value="#FF3DCBF5"/>
                  <Setter Target="Root.BorderBrush" Value="#AAFFFFFF"/>
                </VisualState.Setters>
              </VisualState>
            </VisualStateGroup>
          </VisualStateManager.VisualStateGroups>
          <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
        </Border>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
```

`GlassIconButton` 与此相同，改为 `Button`，圆形（宽高相等、`CornerRadius` 取一半），默认无背景，悬停出 `#26FFFFFF`；播放按钮变体直接用青色实心。

### 5.6 玻璃 Slider（进度条和音量条）

以 WinUI 默认 `Slider` 模板为底，只改下列部件，不要动命名部件的名字：

- `HorizontalTrackRect`：高度 6，`CornerRadius=3`，`Fill=#26000000`，加 1px `#22FFFFFF` 描边，做出凹槽感。
- `HorizontalDecreaseRect`：`Fill` 用青色线性渐变（`#3DCBF5` 到 `#7BE3FA`），圆角同上。
- `HorizontalThumb`（`Thumb`）：直径 18 的圆，`Fill=#E6FFFFFF`，描边用边缘光渐变，外加一圈淡青色外发光。

### 5.7 弹簧按压动画（所有按钮和卡片共用）

```csharp
public static class Spring
{
    public static void Attach(UIElement e)
    {
        e.PointerPressed  += (_, _) => Animate(e, 0.94f);
        e.PointerReleased += (_, _) => Animate(e, 1f);
        e.PointerExited   += (_, _) => Animate(e, 1f);
    }

    static void Animate(UIElement e, float scale)
    {
        var v = ElementCompositionPreview.GetElementVisual(e);
        v.CenterPoint = new System.Numerics.Vector3(e.ActualSize / 2, 0);
        var s = v.Compositor.CreateSpringVector3Animation();
        s.FinalValue = new System.Numerics.Vector3(scale, scale, 1);
        s.DampingRatio = 0.55f;
        s.Period = TimeSpan.FromMilliseconds(55);
        v.StartAnimation("Scale", s);
    }
}
```

### 5.8 各面板的用法

```xml
<!-- 左侧边栏：向左多伸出 24 像素，让左侧圆角落在窗口外 -->
<local:GlassPanel Margin="-24,0,0,0" CornerRadius="24" Blur="26" Tint="#2EFFFFFF"/>

<!-- 底部播放条 -->
<local:GlassPanel IsCapsule="True" Blur="28" Tint="#2EFFFFFF" Height="96"/>

<!-- 顶部搜索框 -->
<local:GlassPanel IsCapsule="True" Blur="14" Tint="#18FFFFFF" Width="420" Height="40"/>

<!-- 歌单卡片 -->
<local:GlassPanel CornerRadius="20" Blur="20" Tint="#1AFFFFFF"/>
```

## 6. 阶段二：折射着色器

只在三处启用：侧边栏、底部播放条、焦点画报轮播卡片。阶段一全部验收通过之后再开始，且必须保留"关闭折射、回退到阶段一效果"的开关。

**为什么单独成阶段**：Composition 的效果图不支持自定义像素着色器，折射必须换成 Win2D 路线：`CanvasControl` 加 `PixelShaderEffect`，着色器用 `ComputeSharp.D2D1` 以 C# 编写。

**实现步骤**

1. 新建 `RefractiveGlassPanel`，内部用 `CanvasControl`（或 `CanvasSwapChainPanel`）作为玻璃层，替换 `GlassLayer`。
2. 背景输入：把 `BackgroundLayer` 渲染成 `CanvasRenderTarget`（窗口尺寸变化时重建），所有折射面板共用这一张。
3. 面板按自身偏移裁出对应区域，送入着色器；着色器输出后再叠模糊、饱和度和着色，参数沿用第 2 节。
4. 边缘光和内高光沿用阶段一的 XAML 边框，不进着色器。

**着色器逻辑（参考实现，API 名称以 ComputeSharp.D2D1 当前版本为准）**

```csharp
[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DRequiresScenePosition]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct RefractionShader(
    float2 size, float radius, float bezel, float strength, float dispersion) : ID2D1PixelShader
{
    float SdRoundRect(float2 p, float2 half, float r)
    {
        float2 q = Hlsl.Abs(p) - half + r;
        return Hlsl.Min(Hlsl.Max(q.X, q.Y), 0f) + Hlsl.Length(Hlsl.Max(q, 0f)) - r;   // 内部为负
    }

    public float4 Execute()
    {
        float2 pos = D2D.GetScenePosition().XY;
        float2 p = pos - size * 0.5f;
        float2 half = size * 0.5f;

        float d = SdRoundRect(p, half, radius);
        float edge = Hlsl.Saturate(1f + d / bezel);          // 越靠边越接近 1，中心为 0

        // 用有限差分求 SDF 梯度，即边缘法线
        float e = 1f;
        float2 n = Hlsl.Normalize(new float2(
            SdRoundRect(p + new float2(e, 0), half, radius) - SdRoundRect(p - new float2(e, 0), half, radius),
            SdRoundRect(p + new float2(0, e), half, radius) - SdRoundRect(p - new float2(0, e), half, radius)));

        float2 offset = n * Hlsl.Pow(edge, 2.5f) * strength;  // 只在边缘带内产生明显位移

        float r = D2D.SampleInputAtPosition(0, pos + offset * 1.00f).R;
        float g = D2D.SampleInputAtPosition(0, pos + offset * (1f + dispersion)).G;
        float b = D2D.SampleInputAtPosition(0, pos + offset * (1f + 2f * dispersion)).B;
        return new float4(r, g, b, 1f);
    }
}
```

**默认参数**

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `bezel` | 20（像素） | 折射作用的边缘带宽度 |
| `strength` | 14（像素） | 边缘最大位移量 |
| `dispersion` | 0.03 | RGB 三通道的位移差，做色散 |
| `radius` | 面板圆角 | 与面板实际圆角一致，胶囊取高度的一半 |

**回退**：`RefractiveGlassPanel` 提供 `UseRefraction` 属性，为 false 时直接换回阶段一的 `GlassPanel` 视觉，不影响布局。

## 7. 任务拆分与验收标准

按顺序执行，每个任务完成并通过自己的验收条目后再进入下一个。任务 1 到 8 属于阶段一，9 到 10 属于阶段二。

- [ ] **1. 基础设施**：新增 `GlassHub`、`GlassTokens`，在主窗口根 Grid 里把背景拆成独立的 `BackgroundLayer`，`Loaded` 时调用 `GlassHub.Init`。验收：程序启动无异常，窗口缩放后背景纹理跟随变化。
- [ ] **2. GlassPanel 控件**：实现控件类和 `Generic.xaml` 默认模板。验收：放一个测试面板，能看到模糊后的背景、边缘光和内高光；拖动窗口大小时模糊区域始终对齐面板位置。
- [ ] **3. 播放条**：把底部播放条容器换成 `GlassPanel IsCapsule`，保持现有内部布局和绑定不变。验收：播放、暂停、切歌、进度拖动功能与改动前一致。
- [ ] **4. 侧边栏与搜索框**：侧边栏换 `GlassPanel`（左侧多伸出 24 像素），搜索框换 `GlassPanel IsCapsule`。验收：选中项"首页"为青色胶囊，聚焦搜索框时描边变青。
- [ ] **5. chip 与按钮**：实现 `GlassChip`、`GlassIconButton`，替换所有平台筛选 chip，以及"刷新"、"播放全部"、"换一批"和各圆形按钮。验收：选中、悬停、按下三态外观正确，单选 chip 的互斥逻辑保持原样。
- [ ] **6. 卡片**：歌单卡片换 `GlassPanel`；轮播卡片和新歌封面卡片加边缘光、内高光和底部渐变（不模糊）。验收：封面图不被遮挡，卡片上文字清晰可读。
- [ ] **7. Slider**：进度条和音量条套用玻璃 Slider 模板。验收：拖动、点击轨道跳转、键盘操作均正常，时间文字同步更新。
- [ ] **8. 动效与性能**：给所有按钮和卡片挂 `Spring.Attach`；确认同屏模糊面板数量不超过 8 块；窗口最小化或失焦时暂停背景光带动画。验收：连续滚动页面和拖动窗口时无明显掉帧，任务管理器里 GPU 占用无异常飙升。
- [ ] **9. 折射着色器**：实现 `RefractionShader` 和 `RefractiveGlassPanel`，接入共用的背景渲染目标。验收：面板边缘能看到背景被透镜扭曲，面板中心基本保持原样，开关 `UseRefraction` 可切换。
- [ ] **10. 折射上线**：在侧边栏、播放条、轮播卡片上启用折射。验收：三处同时开启时界面依旧流畅；关闭开关后完全回到阶段一效果。

**整体验收**

- 与截图逐项对照，第 4 节 17 类组件全部完成。
- 浅色、深色主题下文字对比度都清晰可读。
- 所有现有功能（播放、搜索、平台切换、歌单操作）行为与改动前一致。

## 8. 性能底线、已知坑与执行规则

**性能底线**

- 带模糊的面板同屏不超过 8 块，模糊半径不超过 28。
- 所有玻璃面板共用同一张背景纹理，禁止每块面板各自截图或各建一份。
- `SyncOffset` 只在位置真的变化时才写 `Offset`，列表滚动时否则会白白重绘。
- 窗口最小化或失焦时，暂停背景光带动画，必要时停掉共享纹理的刷新。

**已知坑**

| 现象 | 原因与处理 |
| --- | --- |
| `CreateBackdropBrush` 取不到背景或内容不对 | WinUI 3 里它的取样范围和 UWP 时代不同，本项目不使用；一律走 `GlassHub` 的 `CompositionVisualSurface` |
| 玻璃层盖住了面板内部文字 | 玻璃层挂在了含内容的元素上；必须挂在独立的空 `GlassLayer` 上，内容放在它后面 |
| 玻璃里的背景和面板位置错位 | `Offset` 没有随布局变化更新；检查 `LayoutUpdated` 里的 `SyncOffset` 是否被触发 |
| 背景层是玻璃面板的祖先时，面板里出现递归或空白 | `BackgroundLayer` 必须是兄弟元素，不能包住玻璃面板 |
| 不规则圆角（只有右侧圆角）裁剪不对 | `GlassPanel` 的 Clip 只支持统一圆角；用"向窗口外多伸出一截"的办法处理 |
| 窗口缩放后背景纹理不跟随 | 确认 `SourceSize` 的表达式动画绑定的是背景层 `Size`，而不是写死的数值 |

**给执行智能体的规则**

1. 严格按第 7 节的顺序做，每完成一个任务就编译、运行并对照验收条目自检，再进入下一个。
2. 不改动任何业务逻辑和数据绑定接口；需要动到时先停下来说明原因。
3. 所有颜色、模糊半径、圆角、动画参数引用 `GlassTokens` 或 ResourceDictionary，不得在控件内写死。
4. 参考代码里 API 名称与当前依赖版本不一致时，以实际版本为准，并在提交说明里写明改了什么。
5. 遇到性能问题优先减少面板数量或降低模糊半径，不要引入新的渲染路线。
6. 阶段二的折射效果出问题时，用 `UseRefraction=false` 回退，不要阻塞阶段一的交付。
7. 每个任务结束时给出：改动的文件清单、已通过的验收条目、未通过或有疑问的条目。
