# LiquidGlass.WinUI 独立控件库开发文档

**项目名称：** LiquidGlass.WinUI
**项目类型：** 独立 WinUI 3 C# 控件库
**开发语言：** C# + XAML
**渲染技术：** Windows Composition + Win2D + GPU Shader
**目标平台：** Windows 10 / Windows 11
**文档版本：** 1.0
**设计基准：** iOS Liquid Glass 实际界面效果

---

# 一、项目概述

## 1.1 项目目标

开发一套独立、可复用、可扩展的 WinUI 3 Liquid Glass（液体玻璃）UI 材质控件库。

本项目不开发完整应用程序，而是提供一套可以被其他 WinUI 3 项目引用的 UI 控件及材质渲染系统。

通过 XAML 定义控件结构，使用 C# 管理材质参数和 Composition 渲染，结合 GPU 图形效果，实现接近 iOS Liquid Glass 的透明、模糊、折射、反射和动态交互效果。

控件必须具备以下特点：

1. 实时采样控件后方的界面内容。
2. 根据背景内容动态呈现玻璃效果。
3. 支持背景模糊、透明度、颜色透射。
4. 支持边缘高光、内部反射和折射增强。
5. 支持圆角、胶囊形状及不同尺寸。
6. 支持动态材质变化和交互动画。
7. 支持深色、浅色及不同背景环境。
8. 所有玻璃材质参数可通过 XAML 或 C# 修改。
9. 控件可以独立使用，不依赖宿主应用的业务逻辑。
10. 提供统一的材质配置和控件样式管理。

## 1.2 核心设计原则

本项目以 iOS 的 Liquid Glass 为视觉目标，不采用传统 Glassmorphism 的设计方式。

禁止将以下效果作为最终实现：

- 大面积蓝紫色渐变玻璃。
- 厚重的发光描边。
- 夸张的 3D 果冻和水晶造型。
- 仅使用 AcrylicBrush 实现全部玻璃效果。
- 使用静态 PNG 图片模拟动态玻璃。
- 对整个控件及其内部内容统一模糊。

核心原则：

**玻璃材质负责处理背景，控件内容始终保持清晰。**

---

# 二、视觉效果规范

根据提供的两张 iOS 参考图片，首先实现两种基础材质。

## 2.1 Liquid Glass Dock

对应第一张参考图：底部悬浮导航栏。

视觉要求：

1. 整体为深色半透明胶囊形状。
2. 玻璃主体具有明显的环境透射能力。
3. 后方背景颜色和纹理能够透过玻璃。
4. 背景纹理产生具有方向性的折射和扭曲。
5. 边缘具有自然的白色、灰色和环境色反光。
6. 上下边缘存在细腻的镜面高光。
7. 玻璃主体不能出现明显的厚重描边。
8. 内部图标保持原始颜色和清晰度。
9. 悬浮状态具有自然的层次感。
10. 鼠标悬停、点击时产生细腻的材质变化。

Dock 的核心不是背景模糊，而是深色透明玻璃与环境反射、背景折射的结合。

## 2.2 Liquid Glass Folder

对应第二张参考图：iOS 桌面文件夹。

视觉要求：

1. 大面积圆角半透明面板。
2. 背景内容明显模糊。
3. 背景中的颜色能够自然透过玻璃。
4. 玻璃主体保持深色或环境自适应色调。
5. 面板边缘具有柔和的反光。
6. 内部图标保持清晰，不受背景模糊影响。
7. 标题文字具有良好的可读性。
8. 背景变化时，玻璃效果能够同步变化。
9. 面板弹出和关闭具有自然的缩放及透明度动画。
10. 不使用夸张的彩色光晕。

## 2.3 两种材质的区别

| 特征    | Dock  | Folder   |
| ----- | ----- | -------- |
| 主体颜色  | 深色半透明 | 环境自适应半透明 |
| 背景模糊  | 轻微    | 明显       |
| 背景折射  | 强     | 适中       |
| 边缘高光  | 明显但细腻 | 柔和       |
| 环境反射  | 明显    | 适中       |
| 内容清晰度 | 完全清晰  | 完全清晰     |
| 形状    | 胶囊形   | 大圆角矩形    |
| 悬浮感   | 强     | 中等       |

---

# 三、技术架构

## 3.1 总体架构

采用四层架构。

### 第一层：XAML 控件层

负责：

- 控件布局。
- 圆角和裁剪。
- 内容显示。
- 控件模板。
- 用户交互。
- 控件状态管理。

使用 WinUI 3 Templated Control 实现。

### 第二层：材质管理层

负责：

- 玻璃材质预设。
- 透明度管理。
- 模糊参数。
- 颜色透射。
- 边缘高光。
- 折射强度。
- 深浅色主题适配。

提供统一的材质配置对象。

### 第三层：Composition 渲染层

负责：

- 实时背景采样。
- 背景模糊。
- 颜色饱和度处理。
- 玻璃透明层。
- 高光和反射。
- 圆角裁剪。
- GPU 动画。

主要使用 Windows Composition API 和 Win2D。

### 第四层：GPU 折射增强层

负责：

- 背景纹理位移。
- 光学折射。
- 边缘扭曲。
- 动态反射。
- 更高级的镜面效果。

使用 ComputeSharp / Direct2D Pixel Shader 作为可选扩展技术。

这一层必须经过独立技术验证，不能将普通模糊效果误认为真实折射。

## 3.2 技术选型

| 技术                       | 主要用途           |
| ------------------------ | -------------- |
| WinUI 3                  | UI 框架          |
| XAML                     | 控件模板和布局        |
| C#                       | 控件逻辑及材质管理      |
| Microsoft.UI.Composition | GPU 合成和动画      |
| CompositionBackdropBrush | 实时背景采样         |
| CompositionEffectBrush   | 效果链            |
| Win2D                    | 模糊及图像处理        |
| ComputeSharp.D2D1.WinUI  | 自定义 GPU Shader |
| DependencyProperty       | 控件参数及数据绑定      |

## 3.3 特别注意：背景采样能力

这是整个项目最重要的技术基础。

使用 CompositionBackdropBrush 获取玻璃控件后方的背景内容，再通过 CompositionEffectBrush 处理背景。

但必须明确 WinUI 3 的限制：

- 可以处理由当前应用 Composition 渲染的内容。
- 不能保证采样 WebView2、MediaPlayerElement、SwapChainPanel 等外部渲染内容。
- 不能直接读取其他进程窗口的像素。
- Mica 和 DesktopAcrylic 等系统背景不等于可供 Composition 再次采样的普通背景纹理。

因此，宿主应用需要将需要参与玻璃效果的背景内容放在 Composition 可处理的视觉层中。

不能简单依靠窗口的 Mica 背景，就认为玻璃控件可以折射 Mica。

另外，WinUI 3 桌面应用中不能依赖 `Window.Current.Compositor`。控件库在没有直接 UIElement 引用时，应使用：

`CompositionTarget.GetCompositorForCurrentThread()`

---

# 四、项目目录结构

建议采用以下结构：

```text
LiquidGlass.WinUI/
│
├── Controls/
│   ├── LiquidGlassPanel.cs
│   ├── LiquidGlassFolder.cs
│   ├── LiquidGlassDock.cs
│   ├── LiquidGlassButton.cs
│   └── LiquidGlassSurface.cs
│
├── Materials/
│   ├── LiquidGlassMaterial.cs
│   ├── LiquidGlassMaterialPreset.cs
│   ├── LiquidGlassMaterialKind.cs
│   └── LiquidGlassMaterialManager.cs
│
├── Brushes/
│   ├── LiquidGlassBackdropBrush.cs
│   └── LiquidGlassTintBrush.cs
│
├── Rendering/
│   ├── LiquidGlassRenderer.cs
│   ├── CompositionRenderer.cs
│   ├── GlassHighlightRenderer.cs
│   ├── GlassShadowRenderer.cs
│   ├── GlassAnimationController.cs
│   └── CompositionResourceCache.cs
│
├── Effects/
│   ├── BackdropBlurEffect.cs
│   ├── GlassRefractionEffect.cs
│   ├── GlassDistortionEffect.cs
│   └── Shaders/
│
├── Helpers/
│   ├── CompositionHelper.cs
│   ├── ThemeHelper.cs
│   ├── VisualTreeHelper.cs
│   └── ResourceHelper.cs
│
├── Themes/
│   ├── Generic.xaml
│   ├── LiquidGlass.Light.xaml
│   └── LiquidGlass.Dark.xaml
│
├── Properties/
│   └── AssemblyInfo.cs
│
└── LiquidGlass.WinUI.csproj


LiquidGlass.SampleApp/
│
├── Pages/
│   ├── DockDemoPage.xaml
│   ├── FolderDemoPage.xaml
│   ├── ButtonDemoPage.xaml
│   ├── MaterialSettingsPage.xaml
│   └── PerformanceTestPage.xaml
│
├── Models/
├── ViewModels/
└── Assets/


LiquidGlass.Tests/
│
├── MaterialTests/
├── ControlTests/
└── RenderingTests/
```

控件库和演示程序必须分离。

演示程序只负责展示和测试，不允许将核心渲染逻辑写在 SampleApp 中。

---

# 五、项目配置及依赖

## 5.1 项目类型

使用 C# WinUI 3 Class Library。

建议以宿主应用当前使用的 .NET Target Framework 和 Windows App SDK 版本为准，保持兼容。

项目文件示例：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <UseWinUI>true</UseWinUI>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference
      Include="Microsoft.WindowsAppSDK"
      Version="$(WindowsAppSDKVersion)" />

    <PackageReference
      Include="Microsoft.Graphics.Win2D"
      Version="$(Win2DVersion)" />
  </ItemGroup>

</Project>
```

以上 TargetFramework 仅为示例，实际应与宿主项目统一。

## 5.2 NuGet 依赖

基础依赖：

- Microsoft.WindowsAppSDK
- Microsoft.Graphics.Win2D

高级折射功能：

- ComputeSharp.D2D1.WinUI

ComputeSharp 不应成为基础控件的强制依赖。将其放在高级渲染模块中，避免不使用 GPU Shader 的项目也承担额外依赖。

---

# 六、公共控件 API 设计

控件需要具备统一的属性接口，避免每个控件重复实现材质逻辑。

## 6.1 LiquidGlassPanel

基础玻璃容器。

继承：

`ContentControl`

主要用途：

- 通用玻璃面板。
- 悬浮窗口。
- 弹出菜单。
- 工具栏背景。
- 设置面板。

核心属性：

| 属性                   | 类型           | 说明     |
| -------------------- | ------------ | ------ |
| Material             | MaterialKind | 材质类型   |
| BlurAmount           | double       | 背景模糊强度 |
| TintColor            | Color        | 玻璃着色   |
| TintOpacity          | double       | 着色透明度  |
| Saturation           | double       | 背景饱和度  |
| CornerRadius         | CornerRadius | 圆角     |
| EdgeHighlightOpacity | double       | 边缘高光强度 |
| RefractionStrength   | double       | 折射强度   |
| FallbackColor        | Color        | 降级颜色   |
| IsAnimationEnabled   | bool         | 是否启用动画 |

## 6.2 LiquidGlassFolder

继承：

`LiquidGlassPanel`

专门用于实现文件夹和大面积玻璃容器。

额外属性：

- Header
- HeaderTemplate
- ItemsSource
- ItemTemplate
- ItemSpacing
- ContentPadding
- OpenAnimationDuration
- CloseAnimationDuration

要求支持自定义内容，不能限制为固定图标布局。

## 6.3 LiquidGlassDock

继承：

`ItemsControl`

用于实现底部悬浮导航栏。

功能：

- 水平排列。
- 自定义图标。
- 自定义 ItemTemplate。
- 悬停动画。
- 点击动画。
- 选中状态。
- 动态添加和删除项目。
- 自适应宽度。
- 胶囊形玻璃背景。

Dock 的材质与 Folder 不同，必须使用独立的 Dock 材质预设。

## 6.4 LiquidGlassButton

继承：

`Button`

功能：

- 透明玻璃背景。
- 悬停高光。
- 按下状态。
- 弹性缩放。
- 图标和文字自定义。
- 支持不同大小。

不能通过更改按钮内部图标透明度来模拟玻璃效果。

## 6.5 LiquidGlassMaterialPreset

提供统一预设：

```csharp
public enum LiquidGlassMaterialKind
{
    Dock,
    Folder,
    Button,
    Panel,
    Popover,
    Custom
}
```

控件通过 Material 属性选择预设。

自定义材质可以覆盖所有默认参数。

---

# 七、核心渲染实现

## 7.1 LiquidGlassBackdropBrush

这是核心基础组件。

继承：

`XamlCompositionBrushBase`

主要职责：

1. 获取当前线程的 Compositor。
2. 创建 CompositionBackdropBrush。
3. 创建 CompositionEffectBrush。
4. 设置背景采样源。
5. 应用模糊效果。
6. 更新动态参数。
7. 控件卸载时释放 Composition 资源。

基础流程：

```text
XAML 控件
    │
    ▼
LiquidGlassBackdropBrush
    │
    ▼
CompositionBackdropBrush
    │
    ▼
GaussianBlurEffect
    │
    ▼
CompositionEffectBrush
    │
    ▼
玻璃背景层
```

## 7.2 C# 核心实现参考

以下代码用于说明基础实现方式：

```csharp
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;

public sealed class LiquidGlassBackdropBrush
    : XamlCompositionBrushBase
{
    public double BlurAmount { get; set; } = 12;

    private CompositionBackdropBrush? _backdrop;

    protected override void OnConnected()
    {
        if (CompositionBrush != null)
            return;

        var compositor =
            CompositionTarget.GetCompositorForCurrentThread();

        _backdrop = compositor.CreateBackdropBrush();

        var effect = new GaussianBlurEffect
        {
            Name = "GlassBlur",
            BlurAmount = (float)BlurAmount,
            BorderMode = EffectBorderMode.Hard,
            Source = new CompositionEffectSourceParameter("Backdrop")
        };

        var factory = compositor.CreateEffectFactory(
            effect,
            new[] { "GlassBlur.BlurAmount" });

        var brush = factory.CreateBrush();

        brush.SetSourceParameter("Backdrop", _backdrop);

        CompositionBrush = brush;
    }

    protected override void OnDisconnected()
    {
        CompositionBrush?.Dispose();
        CompositionBrush = null;

        _backdrop?.Dispose();
        _backdrop = null;
    }
}
```

正式实现时，BlurAmount 等属性必须使用 DependencyProperty，并在属性变化时更新已经创建的 CompositionEffectBrush，而不是每次修改参数都重新创建整个效果链。

同时需要处理控件重新连接、资源释放和渲染设备变化。

## 7.3 玻璃材质渲染分层

玻璃不能只由一层模糊背景组成。

需要采用分层渲染：

```text
┌────────────────────────────┐
│       内容层 Content       │
│                            │
├────────────────────────────┤
│       边缘高光层           │
├────────────────────────────┤
│       环境反射层           │
├────────────────────────────┤
│       透明着色层           │
├────────────────────────────┤
│       背景模糊层           │
└────────────────────────────┘
```

各层职责：

**背景模糊层**

负责实时采样和背景处理。

**透明着色层**

控制玻璃主体明暗及环境色透射。

**环境反射层**

模拟背景环境在玻璃表面形成的反射。

**边缘高光层**

使用细腻的渐变和透明描边表现玻璃轮廓。

**内容层**

放置图标、文字和按钮，始终保持清晰。

---

# 八、真正的光学折射实现

这是本项目与普通 Acrylic 最大的区别。

## 8.1 实现目标

仅仅使用 GaussianBlurEffect 无法实现参考图片中的背景扭曲效果。

需要进一步实现：

- 背景纹理位移。
- 不同区域的折射强度。
- 玻璃边缘折射。
- 镜面反射。
- 局部高光变化。
- 动态折射。

## 8.2 Shader 技术方案

使用 ComputeSharp.D2D1.WinUI 实现 C# 自定义 GPU Pixel Shader。

通过位移映射改变背景纹理的采样位置。

基础原理：

```text
背景纹理
    │
    ▼
生成位移映射
    │
    ▼
GPU Pixel Shader
    │
    ▼
计算新的采样坐标
    │
    ▼
生成折射后的背景
    │
    ▼
叠加玻璃反射及高光
```

折射效果需要根据玻璃形状、边缘位置和材质参数计算，不能简单对整个背景进行统一缩放。

## 8.3 重要技术验证

必须先进行一个独立技术验证：

验证 ComputeSharp 自定义效果与当前 Composition 背景采样源的衔接方式。

需要确认：

1. 是否能够将实时背景纹理提供给 Shader。
2. 是否能够在 GPU 上完成位移采样。
3. 是否能够保持实时更新。
4. 是否能够正确处理透明通道。
5. 是否能够与圆角裁剪配合。
6. 是否能够在控件移动和缩放时同步更新。

如果 CompositionBackdropBrush 无法直接作为自定义 Shader 的输入，就需要建立宿主提供的 GPU 背景 Surface 接口。

不能为了实现折射而每帧使用 CPU 截图、Bitmap 转换和重新上传 GPU。

## 8.4 折射模块接口

预留统一接口：

```csharp
public interface ILiquidGlassRefractionRenderer
{
    bool IsSupported { get; }

    void Initialize();

    void UpdateParameters(
        float strength,
        float edgeStrength);

    void Release();
}
```

基础版本可以使用 Composition 原生效果和高光层。

高级版本再接入 GPU Shader。

注意：在 Shader 完成之前，不得将模拟高光称为真实背景折射。

---

# 九、XAML 控件模板

所有控件采用 Templated Control。

统一模板放在：

`Themes/Generic.xaml`

基础玻璃面板模板结构：

```xml
<ControlTemplate
    TargetType="local:LiquidGlassPanel">

    <Grid>

        <!-- 背景采样及模糊层 -->
        <Border
            x:Name="PART_Backdrop"
            CornerRadius="{TemplateBinding CornerRadius}"
            Background="{TemplateBinding Background}" />

        <!-- 玻璃透明着色层 -->
        <Border
            x:Name="PART_Tint"
            CornerRadius="{TemplateBinding CornerRadius}"
            Background="{TemplateBinding TintBrush}" />

        <!-- 边缘反射层 -->
        <Border
            x:Name="PART_Highlight"
            CornerRadius="{TemplateBinding CornerRadius}"
            BorderBrush="{TemplateBinding EdgeBrush}"
            BorderThickness="1" />

        <!-- 清晰内容层 -->
        <ContentPresenter
            x:Name="PART_Content"
            Content="{TemplateBinding Content}"
            HorizontalAlignment="Stretch"
            VerticalAlignment="Stretch" />

    </Grid>

</ControlTemplate>
```

正式开发时，模板部分还需要处理圆角裁剪、背景画笔初始化和控件尺寸变化。

不允许将内容层放进模糊处理链中。

---

# 十、材质预设参数

以下参数作为第一轮视觉调试起点，不是最终固定值。

| 参数                   |      Dock |    Folder |
| -------------------- | --------: | --------: |
| BlurAmount           |       2–8 |     16–26 |
| TintOpacity          | 0.38–0.52 | 0.20–0.38 |
| Saturation           |  1.0–1.15 | 0.95–1.10 |
| EdgeHighlightOpacity | 0.15–0.25 | 0.08–0.18 |
| RefractionStrength   | 0.15–0.35 | 0.04–0.12 |
| CornerRadius         |       胶囊形 |     28–36 |

折射参数在高级 Shader 模块完成前仅作为预留配置，不代表基础模糊层已经具备折射能力。

必须支持深色、浅色和自动环境模式。

禁止将蓝紫色设置为默认玻璃颜色。

---

# 十一、动画和交互

采用 Composition Animation，避免使用高频 DispatcherTimer 驱动整个材质渲染。

需要实现：

### Dock

- 鼠标进入：轻微高光增强。
- 鼠标悬停：图标轻微放大。
- 鼠标按下：轻微缩放。
- 鼠标离开：自然恢复。
- Dock 出现：透明度与缩放组合动画。

### Folder

- 打开：透明度渐变与缩放。
- 关闭：反向动画。
- 悬停：边缘高光轻微变化。
- 内容变化：保持玻璃背景与内容同步。

动画必须轻量、自然，不能出现明显弹跳和过度发光。

---

# 十二、性能和资源管理

性能是独立控件库的必要指标。

开发要求：

1. 使用 GPU 进行效果处理。
2. 避免 CPU 逐像素处理。
3. 不使用持续的 RenderTargetBitmap 截图循环。
4. CompositionEffectFactory 尽可能复用。
5. 每个控件独立维护必要的材质参数。
6. 不使用不必要的重复 Composition 对象。
7. 控件卸载时释放相关资源。
8. 控件重新加载时能够重新初始化。
9. 参数变化时尽量更新已有效果对象。
10. 背景内容发生变化时保持实时效果。
11. 不允许因为增加玻璃效果而明显拖慢整个 UI。
12. 提供性能降级模式。

重点测试：

- 单个玻璃控件。
- 多个玻璃控件同时显示。
- 大面积 Folder。
- 多个 Dock。
- 窗口缩放。
- 多屏幕 DPI。
- 高频交互。

建议以 60 FPS 作为常规交互目标，并记录 GPU 占用、CPU 占用和帧时间。

---

# 十三、兼容性与降级机制

必须考虑以下情况：

| 场景             | 处理方式                  |
| -------------- | --------------------- |
| 深色主题           | 使用深色半透明材质             |
| 浅色主题           | 自动调整背景与文字对比度          |
| 高对比度           | 使用高对比度实色材质            |
| 关闭系统透明效果       | 使用不透明或半透明降级材质         |
| GPU Shader 不可用 | 使用 Composition 基础效果   |
| 外部渲染内容         | 使用宿主提供的背景 Surface 或降级 |
| 控件卸载           | 释放渲染资源                |
| GPU 设备变化       | 重新初始化渲染资源             |

降级后仍然必须保持圆角、布局、交互和可读性。

---

# 十四、开发阶段规划

按照以下顺序开发，不要一次性把所有效果混在一起实现。

## 阶段 0：技术验证

目标：确认基础渲染能力和高级折射技术路线。

任务：

- 创建 WinUI 3 Class Library。
- 验证 CompositionBackdropBrush。
- 验证 CompositionEffectBrush。
- 验证 Win2D GaussianBlurEffect。
- 验证实时背景变化。
- 验证圆角玻璃区域。
- 验证 ComputeSharp Shader 与背景 Surface 的衔接。

验收：确认基础玻璃效果可行，并明确真实折射的数据输入方案。

## 阶段 1：基础材质引擎

开发：

- LiquidGlassBackdropBrush。
- LiquidGlassRenderer。
- LiquidGlassMaterial。
- LiquidGlassMaterialManager。
- CompositionResourceCache。
- 深色、浅色及降级材质。

验收：基础玻璃能够实时显示背景、模糊和透明效果。

## 阶段 2：LiquidGlassFolder

开发：

- LiquidGlassPanel。
- LiquidGlassFolder。
- Folder XAML 模板。
- 背景模糊。
- 环境色透射。
- 边缘高光。
- 文件夹打开及关闭动画。

验收：视觉上接近第二张参考图片。

重点：背景模糊明显，图标和文字保持清晰，玻璃不能变成厚重的蓝紫色磨砂板。

## 阶段 3：LiquidGlassDock

开发：

- LiquidGlassDock。
- LiquidGlassButton。
- 胶囊形背景。
- 深色透明材质。
- 镜面反射。
- 边缘高光。
- 图标悬停动画。

验收：视觉上接近第一张参考图片。

## 阶段 4：GPU 折射增强

开发：

- GlassRefractionEffect。
- GlassDistortionEffect。
- Shader 参数管理。
- 背景位移映射。
- 动态折射。
- 边缘反射增强。

验收：背景不只是模糊，而是能够产生自然的光学扭曲和折射。

如果高级 Shader 路线需要宿主提供背景 Surface，必须在公开 API 和接入文档中明确说明。

## 阶段 5：控件库完善

开发：

- 统一主题资源。
- 控件属性文档。
- 性能测试。
- 兼容性测试。
- 示例项目。
- NuGet 打包配置。

最终交付独立控件库和完整演示程序。

---

# 十五、最终验收标准

必须以用户提供的两张 iOS 图片为对照，不以普通玻璃拟态效果作为验收依据。

## Dock 验收

- [ ] 深色半透明胶囊形状。
- [ ] 背景能够实时透射。
- [ ] 能够表现背景纹理的折射和扭曲。
- [ ] 具有细腻的镜面高光。
- [ ] 边缘反光自然。
- [ ] 图标颜色正常且清晰。
- [ ] 没有明显的蓝紫色发光边框。
- [ ] 鼠标交互自然。
- [ ] 缩放时材质正常。

## Folder 验收

- [ ] 大面积半透明玻璃面板。
- [ ] 背景模糊清晰可见。
- [ ] 背景颜色能够自然透射。
- [ ] 边缘反射自然。
- [ ] 图标保持清晰。
- [ ] 文字保持清晰。
- [ ] 圆角和裁剪正常。
- [ ] 打开关闭动画流畅。
- [ ] 背景变化时效果同步变化。

## 工程验收

- [ ] 控件库可以独立编译。
- [ ] SampleApp 可以单独运行。
- [ ] 宿主项目只需引用控件库即可使用。
- [ ] 所有主要参数可以通过 XAML 配置。
- [ ] 支持 C# 动态修改参数。
- [ ] Composition 资源可以正确释放。
- [ ] 不存在明显内存泄漏。
- [ ] 多控件同时显示时性能稳定。
- [ ] 关闭高级效果后仍能正常使用。

---

# 十六、宿主项目集成方式

控件库开发完成后，其他 WinUI 3 项目只需要添加项目引用：

```xml
<ItemGroup>
    <ProjectReference
        Include="..\LiquidGlass.WinUI\LiquidGlass.WinUI.csproj" />
</ItemGroup>
```

XAML 引用：

```xml
xmlns:glass="using:LiquidGlass.WinUI.Controls"
```

使用 Folder：

```xml
<glass:LiquidGlassFolder
    Width="320"
    Height="420"
    Material="Folder">

    <Grid>
        <!-- 自定义文件夹内容 -->
    </Grid>

</glass:LiquidGlassFolder>
```

使用 Dock：

```xml
<glass:LiquidGlassDock
    HorizontalAlignment="Center"
    VerticalAlignment="Bottom"
    Material="Dock"
    ItemsSource="{x:Bind ViewModel.DockItems}">

</glass:LiquidGlassDock>
```

具体属性和模板以最终控件库 API 为准。

---

# 十七、最终交付要求

最终需要交付：

1. `LiquidGlass.WinUI` 独立 C# 控件库。
2. 完整 XAML 控件模板。
3. Composition 材质渲染引擎。
4. Dock 材质。
5. Folder 材质。
6. Button 材质。
7. GPU 折射扩展模块。
8. 深色及浅色主题。
9. 完整 SampleApp。
10. 控件 API 使用说明。
11. 性能及兼容性测试报告。
12. NuGet 打包配置。

## 最终开发原则

本项目的目标不是制作几个看起来像玻璃的控件，而是建立一套真正可复用的 UI 材质渲染系统。

**材质引擎与控件分离，控件与业务分离，XAML 与 GPU 渲染分离。**

所有控件共享统一的材质管理和渲染基础设施，但允许每种控件使用不同的材质参数。

第一优先级是实现参考图片中的 Folder 和 Dock 两种真实视觉效果。

在这两种效果达到验收标准之后，再扩展其他玻璃控件。
