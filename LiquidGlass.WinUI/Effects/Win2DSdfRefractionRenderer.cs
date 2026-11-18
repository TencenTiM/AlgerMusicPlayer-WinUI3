using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using ComputeSharp.D2D1;
using ComputeSharp.D2D1.Interop;
using LiquidGlass.WinUI.Materials;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using Windows.UI;

namespace LiquidGlass.WinUI.Effects;

/// <summary>
/// 混合型零延迟 GPU 单 Pass HLSL 光学折射渲染器：
/// 1. 优先运行 ComputeSharp.D2D1 预编译 ps_4_0 单 Pass 着色器 (LiquidGlassRefractionShader via Win2D PixelShaderEffect)，
///    彻底免除 CPU 位图生成与多路 Direct2D 特效链拼接，并在外围 4 边带 ROI 内一次完成曲线 Snell 折射、RGB 色散、3D 菲涅尔高光与焦散；
/// 2. 中心平坦区保持 100% 透明，直透底层零延迟原生 DWM CompositionBackdropBrush；
/// 3. 若罕见显卡/环境不支持 D2D PixelShaderEffect 场景坐标模式，自动无缝回退至持久化 D2D 四边带缓存管线。
/// </summary>
public sealed class Win2DSdfRefractionRenderer : IDisposable
{
    public sealed class HostVisualState
    {
        public UIElement? HostElement;
        public ContainerVisual? MasterRoot;
        public ContainerVisual? CustomUnderlayLayer;
        public SpriteVisual? SurfaceVisual;
        public SpriteVisual? SpecularVisual;
        public SpriteVisual? PointerGlowVisual;
        public CompositionDrawingSurface? DrawingSurface;
        public CompositionSurfaceBrush? SurfaceBrush;
        public CompositionDrawingSurface? SpecularSurface;
        public CompositionSurfaceBrush? SpecularBrush;
        public SdfLensTexturePair? LastSpecularTextures;
        public int SurfaceWidth;
        public int SurfaceHeight;
        public float LastRadius;
        public float LastWinX = float.NaN;
        public float LastWinY = float.NaN;
        public double LastWidth;
        public double LastHeight;
        public bool LastIsLight;
        public LiquidGlassMaterialKind LastKind;

        // 通用前端对齐与边缘钳位节点
        public Transform2DEffect? SceneTransform;
        public BorderEffect? ClampedScene;

        // 单 Pass ComputeSharp HLSL PixelShaderEffect 节点
        public PixelShaderEffect? SinglePassShaderEffect;
        public double BoundShaderWidth = -1;
        public double BoundShaderHeight = -1;
        public float BoundShaderRadius = -1f;
        public int BoundShaderBevel = -1;
        public float BoundShaderMaxDisp = -1f;
        public Color BoundShaderTint;
        public bool BoundShaderIsLight;
        public LiquidGlassMaterialKind BoundShaderKind;

        // 兜底持久化 Direct2D 效果链（仅当 SinglePassShader 不受支持时启用）
        public DisplacementMapEffect? DispR;
        public DisplacementMapEffect? DispG;
        public DisplacementMapEffect? DispB;
        public ColorMatrixEffect? MaskR;
        public ColorMatrixEffect? MaskG;
        public ColorMatrixEffect? MaskB;
        public BlendEffect? BlendRG;
        public BlendEffect? BlendRGB;
        public SaturationEffect? SatEffect;
        public AlphaMaskEffect? MaskedBevelEffect;
        public SdfLensTexturePair? BoundTextures;
        public float BoundMaxDispPx = -1f;
        public Color BoundTintColor;
        public bool BoundIsLight;

        public void DisposeEffectChain()
        {
            SinglePassShaderEffect?.Dispose();
            MaskedBevelEffect?.Dispose();
            SatEffect?.Dispose();
            BlendRGB?.Dispose();
            BlendRG?.Dispose();
            MaskB?.Dispose();
            MaskG?.Dispose();
            MaskR?.Dispose();
            DispB?.Dispose();
            DispG?.Dispose();
            DispR?.Dispose();
            ClampedScene?.Dispose();
            SceneTransform?.Dispose();

            SinglePassShaderEffect = null;
            MaskedBevelEffect = null;
            SatEffect = null;
            BlendRGB = null;
            BlendRG = null;
            MaskB = null;
            MaskG = null;
            MaskR = null;
            DispB = null;
            DispG = null;
            DispR = null;
            ClampedScene = null;
            SceneTransform = null;
            BoundTextures = null;
            BoundMaxDispPx = -1f;
            BoundShaderWidth = -1;
            BoundShaderHeight = -1;
            BoundShaderRadius = -1f;
            BoundShaderBevel = -1;
            BoundShaderMaxDisp = -1f;
        }
    }

    private static readonly Lazy<byte[]?> s_shaderBytecode = new(LoadShaderBytecodeSafe);
    private static bool s_shaderProbeCompleted;
    private static bool s_shaderSupported;

    public static bool IsSinglePassShaderSupported => s_shaderProbeCompleted ? s_shaderSupported : s_shaderBytecode.Value != null;
    public static string LastShaderDiagnostic { get; private set; } = "未初始化";
    public static bool ForceSoftwareRendering { get; set; }

    private readonly SdfLiquidLensMapGenerator _mapGenerator = new();
    private readonly ConditionalWeakTable<Compositor, CompositionGraphicsDevice> _graphicsDevices = new();
    private CanvasDevice? _sharedDevice;
    private bool _sharedDeviceIsSoftware;
    private readonly object _deviceGate = new();

    private static byte[]? LoadShaderBytecodeSafe()
    {
        try
        {
            return D2D1PixelShader.LoadBytecode<LiquidGlassRefractionShader>(
                D2D1ShaderProfile.PixelShader40,
                D2D1CompileOptions.OptimizationLevel3).ToArray();
        }
        catch (Exception ex)
        {
            LastShaderDiagnostic = $"LoadBytecode failed: {ex.Message}";
            Debug.WriteLine($"[Win2DSdfRefractionRenderer] {LastShaderDiagnostic}");
            return null;
        }
    }

    public CanvasDevice GetOrCreateDevice()
    {
        lock (_deviceGate)
        {
            bool wantSoftware = ForceSoftwareRendering;
            if (_sharedDevice == null || _sharedDevice.IsDeviceLost() || _sharedDeviceIsSoftware != wantSoftware)
            {
                if (_sharedDevice != null)
                {
                    _sharedDevice.DeviceLost -= OnCanvasDeviceLost;
                    _sharedDevice.Dispose();
                }
                _sharedDevice = CanvasDevice.GetSharedDevice(wantSoftware);
                _sharedDeviceIsSoftware = wantSoftware;
                _sharedDevice.DeviceLost += OnCanvasDeviceLost;
                _mapGenerator.Clear();
            }
            return _sharedDevice;
        }
    }

    private void OnCanvasDeviceLost(CanvasDevice sender, object args)
    {
        lock (_deviceGate)
        {
            _mapGenerator.Clear();
            if (ReferenceEquals(_sharedDevice, sender))
            {
                _sharedDevice = null;
            }
        }
    }

    public bool RenderRefractedGlass(
        UIElement hostElement,
        HostVisualState state,
        CanvasBitmap sceneBitmap,
        float winX,
        float winY,
        double width,
        double height,
        double cornerRadius,
        bool isLight,
        LiquidGlassMaterial material,
        double intensity = 1.0)
    {
        if (hostElement == null || state == null || sceneBitmap == null)
        {
            return false;
        }

        if (width < 20.0 || height < 18.0 || LiquidGlassMaterialManager.Current.ShouldUseFallbackSolid)
        {
            Detach(hostElement, state);
            return false;
        }

        int pixW = Math.Max(8, (int)Math.Ceiling(width));
        int pixH = Math.Max(8, (int)Math.Ceiling(height));
        float r = (float)Math.Clamp(cornerRadius, 2.0, Math.Min(width, height) * 0.5);

        try
        {
            var hostVisual = ElementCompositionPreview.GetElementVisual(hostElement);
            var compositor = hostVisual.Compositor;
            var device = GetOrCreateDevice();
            var graphicsDevice = _graphicsDevices.GetValue(
                compositor,
                c => CanvasComposition.CreateCompositionGraphicsDevice(c, device));

            EnsureVisualTree(hostElement, state, compositor, graphicsDevice, pixW, pixH, width, height, r);

            if (state.DrawingSurface == null)
            {
                return false;
            }

            int bevelW = material.Kind switch
            {
                LiquidGlassMaterialKind.Dock => (int)Math.Clamp(Math.Round(height * 0.44), 16, 26),
                LiquidGlassMaterialKind.Button => (int)Math.Clamp(Math.Round(height * 0.44), 10, 14),
                LiquidGlassMaterialKind.Panel => 12,
                _ => 12
            };

            float maxDispPx = material.Kind switch
            {
                LiquidGlassMaterialKind.Dock => 34f,
                LiquidGlassMaterialKind.Button => 18f,
                LiquidGlassMaterialKind.Panel => 14f,
                _ => 14f
            };
            maxDispPx *= (float)Math.Clamp(intensity, 0.75, 1.30);

            bool usedSinglePass = TryRenderSinglePassShader(
                state,
                sceneBitmap,
                winX,
                winY,
                pixW,
                pixH,
                width,
                height,
                r,
                bevelW,
                maxDispPx,
                material,
                isLight);

            if (!usedSinglePass)
            {
                RenderFallbackChain(
                    device,
                    state,
                    sceneBitmap,
                    winX,
                    winY,
                    pixW,
                    pixH,
                    width,
                    height,
                    r,
                    bevelW,
                    maxDispPx,
                    material,
                    isLight);
            }

            state.LastWinX = winX;
            state.LastWinY = winY;
            state.LastWidth = width;
            state.LastHeight = height;
            state.LastRadius = r;
            state.LastIsLight = isLight;
            state.LastKind = material.Kind;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRenderSinglePassShader(
        HostVisualState state,
        CanvasBitmap sceneBitmap,
        float winX,
        float winY,
        int pixW,
        int pixH,
        double width,
        double height,
        float r,
        int bevelW,
        float maxDispPx,
        LiquidGlassMaterial material,
        bool isLight)
    {
        if (s_shaderProbeCompleted && !s_shaderSupported)
        {
            return false;
        }

        if (state.SinglePassShaderEffect == null)
        {
            var bytecode = s_shaderBytecode.Value;
            if (bytecode == null || bytecode.Length == 0)
            {
                s_shaderProbeCompleted = true;
                s_shaderSupported = false;
                return false;
            }

            try
            {
                state.SceneTransform ??= new Transform2DEffect { BorderMode = EffectBorderMode.Hard };
                state.ClampedScene ??= new BorderEffect
                {
                    Source = state.SceneTransform,
                    ExtendX = CanvasEdgeBehavior.Clamp,
                    ExtendY = CanvasEdgeBehavior.Clamp
                };

                var pfx = new PixelShaderEffect(bytecode)
                {
                    Source1 = state.ClampedScene,
                    Source1Mapping = SamplerCoordinateMapping.Offset,
                    Source1BorderMode = EffectBorderMode.Hard,
                    MaxSamplerOffset = 48
                };

                if (!pfx.Properties.ContainsKey("controlSize"))
                {
                    pfx.Dispose();
                    s_shaderProbeCompleted = true;
                    s_shaderSupported = false;
                    LastShaderDiagnostic = "PixelShaderEffect missing expected controlSize constant";
                    return false;
                }

                state.SinglePassShaderEffect = pfx;
            }
            catch (Exception ex)
            {
                s_shaderProbeCompleted = true;
                s_shaderSupported = false;
                LastShaderDiagnostic = $"PixelShaderEffect init failed: {ex.Message}";
                Debug.WriteLine($"[Win2DSdfRefractionRenderer] {LastShaderDiagnostic}");
                return false;
            }
        }

        var shader = state.SinglePassShaderEffect;
        int effBevelBand = (int)MathF.Ceiling(MathF.Max(bevelW, 0.293f * r + 0.708f * bevelW));

        if (Math.Abs(state.BoundShaderWidth - width) > 0.25 ||
            Math.Abs(state.BoundShaderHeight - height) > 0.25 ||
            MathF.Abs(state.BoundShaderRadius - r) > 0.15f ||
            state.BoundShaderBevel != bevelW ||
            MathF.Abs(state.BoundShaderMaxDisp - maxDispPx) > 0.05f ||
            state.BoundShaderTint != material.TintColor ||
            state.BoundShaderIsLight != isLight ||
            state.BoundShaderKind != material.Kind)
        {
            float specGain = material.Kind == LiquidGlassMaterialKind.Dock
                ? 0.65f
                : (material.Kind == LiquidGlassMaterialKind.Button ? 0.55f : 0.45f);
            float tintAlpha = isLight ? 0.17f : 0.10f;

            shader.Properties["controlSize"] = new Vector2((float)width, (float)height);
            shader.Properties["radius"] = r;
            shader.Properties["bezel"] = (float)bevelW;
            shader.Properties["strength"] = maxDispPx;
            shader.Properties["dispersion"] = 0.22f;
            shader.Properties["tint"] = new Vector4(1f, 1f, 1f, tintAlpha);
            shader.Properties["specGain"] = specGain;
            shader.Properties["troughMax"] = isLight ? 0.03f : 0.05f;

            state.BoundShaderWidth = width;
            state.BoundShaderHeight = height;
            state.BoundShaderRadius = r;
            state.BoundShaderBevel = bevelW;
            state.BoundShaderMaxDisp = maxDispPx;
            state.BoundShaderTint = material.TintColor;
            state.BoundShaderIsLight = isLight;
            state.BoundShaderKind = material.Kind;
        }

        if (state.SpecularVisual != null && state.SpecularVisual.Opacity > 0.001f)
        {
            // 单 Pass HLSL 已在同一个 GPU 着色器里同时输出三维菲涅尔高光弧、焦散与弯曲折射，隐藏独立的 SpecularVisual 省去双层叠绘
            state.SpecularVisual.Opacity = 0f;
        }

        state.SceneTransform!.Source = sceneBitmap;
        state.SceneTransform.TransformMatrix = Matrix3x2.CreateTranslation(-winX, -winY);

        try
        {
            using var ds = CanvasComposition.CreateDrawingSession(
                state.DrawingSurface!,
                new Rect(0, 0, pixW, pixH),
                96f);
            ds.Clear(Colors.Transparent);

            int band = Math.Clamp(effBevelBand + 3, 8, Math.Min(pixW, pixH) / 2);
            if (pixW > band * 2 + 12 && pixH > band * 2 + 12)
            {
                var topRect = new Rect(0, 0, pixW, band);
                var botRect = new Rect(0, pixH - band, pixW, band);
                var leftRect = new Rect(0, band, band, pixH - 2 * band);
                var rightRect = new Rect(pixW - band, band, band, pixH - 2 * band);

                ds.DrawImage(shader, topRect, topRect);
                ds.DrawImage(shader, botRect, botRect);
                ds.DrawImage(shader, leftRect, leftRect);
                ds.DrawImage(shader, rightRect, rightRect);
            }
            else
            {
                ds.DrawImage(shader);
            }

            if (!s_shaderProbeCompleted)
            {
                s_shaderProbeCompleted = true;
                s_shaderSupported = true;
                LastShaderDiagnostic = "ComputeSharp.D2D1 Single-Pass PS_4_0 Active";
            }
            return true;
        }
        catch (Exception ex)
        {
            s_shaderProbeCompleted = true;
            s_shaderSupported = false;
            state.SinglePassShaderEffect?.Dispose();
            state.SinglePassShaderEffect = null;
            LastShaderDiagnostic = $"SinglePass draw failed, falling back: {ex.Message}";
            Debug.WriteLine($"[Win2DSdfRefractionRenderer] {LastShaderDiagnostic}");
            return false;
        }
        finally
        {
            state.SceneTransform.Source = null;
        }
    }

    private void RenderFallbackChain(
        CanvasDevice device,
        HostVisualState state,
        CanvasBitmap sceneBitmap,
        float winX,
        float winY,
        int pixW,
        int pixH,
        double width,
        double height,
        float r,
        int bevelW,
        float maxDispPx,
        LiquidGlassMaterial material,
        bool isLight)
    {
        var textures = _mapGenerator.GetOrCreate(device, width, height, r, bevelW, isLight, material.Kind);

        if (state.SpecularVisual != null && state.SpecularVisual.Opacity < 0.99f)
        {
            state.SpecularVisual.Opacity = 1f;
        }

        if (state.SpecularSurface != null && !ReferenceEquals(state.LastSpecularTextures, textures))
        {
            using (var specDs = CanvasComposition.CreateDrawingSession(
                state.SpecularSurface,
                new Rect(0, 0, pixW, pixH),
                96f))
            {
                specDs.Clear(Colors.Transparent);
                specDs.DrawImage(textures.SpecularOverlayMap, new Rect(0, 0, width, height));
            }
            state.LastSpecularTextures = textures;
        }

        EnsureFallbackEffectChain(state, textures, maxDispPx, material.TintColor, isLight);
        if (state.SceneTransform == null || state.MaskedBevelEffect == null)
        {
            return;
        }

        state.SceneTransform.Source = sceneBitmap;
        state.SceneTransform.TransformMatrix = Matrix3x2.CreateTranslation(-winX, -winY);

        try
        {
            using var ds = CanvasComposition.CreateDrawingSession(
                state.DrawingSurface!,
                new Rect(0, 0, pixW, pixH),
                96f);
            ds.Clear(Colors.Transparent);

            int band = Math.Clamp(textures.EffectiveBevelWidth + 3, 8, Math.Min(pixW, pixH) / 2);
            if (pixW > band * 2 + 12 && pixH > band * 2 + 12)
            {
                var topRect = new Rect(0, 0, pixW, band);
                var botRect = new Rect(0, pixH - band, pixW, band);
                var leftRect = new Rect(0, band, band, pixH - 2 * band);
                var rightRect = new Rect(pixW - band, band, band, pixH - 2 * band);

                ds.DrawImage(state.MaskedBevelEffect, topRect, topRect);
                ds.DrawImage(state.MaskedBevelEffect, botRect, botRect);
                ds.DrawImage(state.MaskedBevelEffect, leftRect, leftRect);
                ds.DrawImage(state.MaskedBevelEffect, rightRect, rightRect);
            }
            else
            {
                ds.DrawImage(state.MaskedBevelEffect);
            }
        }
        finally
        {
            state.SceneTransform.Source = null;
        }
    }

    private static void EnsureFallbackEffectChain(
        HostVisualState state,
        SdfLensTexturePair textures,
        float maxDispPx,
        Color tintColor,
        bool isLight)
    {
        if (state.MaskedBevelEffect == null)
        {
            state.SceneTransform ??= new Transform2DEffect { BorderMode = EffectBorderMode.Hard };
            state.ClampedScene ??= new BorderEffect
            {
                Source = state.SceneTransform,
                ExtendX = CanvasEdgeBehavior.Clamp,
                ExtendY = CanvasEdgeBehavior.Clamp
            };

            state.DispR = new DisplacementMapEffect
            {
                Source = state.ClampedScene,
                XChannelSelect = EffectChannelSelect.Red,
                YChannelSelect = EffectChannelSelect.Green
            };
            state.DispG = new DisplacementMapEffect
            {
                Source = state.ClampedScene,
                XChannelSelect = EffectChannelSelect.Red,
                YChannelSelect = EffectChannelSelect.Green
            };
            state.DispB = new DisplacementMapEffect
            {
                Source = state.ClampedScene,
                XChannelSelect = EffectChannelSelect.Red,
                YChannelSelect = EffectChannelSelect.Green
            };

            state.MaskR = new ColorMatrixEffect { Source = state.DispR };
            state.MaskG = new ColorMatrixEffect { Source = state.DispG };
            state.MaskB = new ColorMatrixEffect { Source = state.DispB };

            state.BlendRG = new BlendEffect
            {
                Mode = BlendEffectMode.Screen,
                Background = state.MaskR,
                Foreground = state.MaskG
            };
            state.BlendRGB = new BlendEffect
            {
                Mode = BlendEffectMode.Screen,
                Background = state.BlendRG,
                Foreground = state.MaskB
            };

            state.SatEffect = new SaturationEffect
            {
                Source = state.BlendRGB,
                Saturation = 1.15f
            };
            state.MaskedBevelEffect = new AlphaMaskEffect
            {
                Source = state.SatEffect
            };
        }

        if (!ReferenceEquals(state.BoundTextures, textures) ||
            MathF.Abs(state.BoundMaxDispPx - maxDispPx) > 0.05f ||
            state.BoundTintColor != tintColor ||
            state.BoundIsLight != isLight)
        {
            state.DispR!.Displacement = textures.DisplacementMap;
            state.DispG!.Displacement = textures.DisplacementMap;
            state.DispB!.Displacement = textures.DisplacementMap;
            state.DispR.Amount = 2.0f * maxDispPx * 0.80f;
            state.DispG.Amount = 2.0f * maxDispPx * 1.00f;
            state.DispB.Amount = 2.0f * maxDispPx * 1.24f;

            float tintFactor = isLight ? 0.17f : 0.09f;
            float keep = 1f - tintFactor;
            state.MaskR!.ColorMatrix = new Matrix5x4
            {
                M11 = keep, M12 = 0f, M13 = 0f, M14 = 0f,
                M21 = 0f, M22 = 0f, M23 = 0f, M24 = 0f,
                M31 = 0f, M32 = 0f, M33 = 0f, M34 = 0f,
                M41 = 0f, M42 = 0f, M43 = 0f, M44 = 1f,
                M51 = (tintColor.R / 255f) * tintFactor, M52 = 0f, M53 = 0f, M54 = 0f
            };
            state.MaskG!.ColorMatrix = new Matrix5x4
            {
                M11 = 0f, M12 = 0f, M13 = 0f, M14 = 0f,
                M21 = 0f, M22 = keep, M23 = 0f, M24 = 0f,
                M31 = 0f, M32 = 0f, M33 = 0f, M34 = 0f,
                M41 = 0f, M42 = 0f, M43 = 0f, M44 = 1f,
                M51 = 0f, M52 = (tintColor.G / 255f) * tintFactor, M53 = 0f, M54 = 0f
            };
            state.MaskB!.ColorMatrix = new Matrix5x4
            {
                M11 = 0f, M12 = 0f, M13 = 0f, M14 = 0f,
                M21 = 0f, M22 = 0f, M23 = 0f, M24 = 0f,
                M31 = 0f, M32 = 0f, M33 = keep, M34 = 0f,
                M41 = 0f, M42 = 0f, M43 = 0f, M44 = 1f,
                M51 = 0f, M52 = 0f, M53 = (tintColor.B / 255f) * tintFactor, M54 = 0f
            };

            state.MaskedBevelEffect!.AlphaMask = textures.BevelMaskMap;

            state.BoundTextures = textures;
            state.BoundMaxDispPx = maxDispPx;
            state.BoundTintColor = tintColor;
            state.BoundIsLight = isLight;
        }
    }

    public void UpdatePointerGlow(HostVisualState? state, float localX, float localY, bool isPointerActive)
    {
        var glow = state?.PointerGlowVisual;
        if (glow == null || state == null)
        {
            return;
        }

        if (!isPointerActive || state.LastWidth < 20 || state.LastHeight < 18)
        {
            if (glow.Opacity > 0.001f)
            {
                glow.Opacity = 0f;
            }
            return;
        }

        float w = (float)state.LastWidth;
        float h = (float)state.LastHeight;
        float cx = Math.Clamp(localX, 0f, w);
        float cy = Math.Clamp(localY, 0f, h);
        float dx = localX - cx;
        float dy = localY - cy;
        float dist = MathF.Sqrt(dx * dx + dy * dy);

        const float influenceRadius = 155f;
        if (dist >= influenceRadius)
        {
            if (glow.Opacity > 0.001f)
            {
                glow.Opacity = 0f;
            }
            return;
        }

        float t = 1f - dist / influenceRadius;
        glow.Offset = new Vector3(localX - 130f, localY - 130f, 0f);
        glow.Opacity = t * t * 0.90f;
    }

    public void Detach(UIElement? hostElement, HostVisualState? state)
    {
        if (hostElement != null)
        {
            ElementCompositionPreview.SetElementChildVisual(hostElement, null);
        }

        if (state != null)
        {
            if (state.HostElement != null && !ReferenceEquals(state.HostElement, hostElement))
            {
                ElementCompositionPreview.SetElementChildVisual(state.HostElement, null);
            }
            state.CustomUnderlayLayer?.Children.RemoveAll();
            state.MasterRoot?.Children.RemoveAll();
            state.MasterRoot?.Dispose();
            state.CustomUnderlayLayer?.Dispose();
            state.SurfaceVisual?.Dispose();
            state.SpecularVisual?.Dispose();
            state.PointerGlowVisual?.Dispose();
            state.SurfaceBrush?.Dispose();
            state.SpecularBrush?.Dispose();
            state.DrawingSurface?.Dispose();
            state.SpecularSurface?.Dispose();
            state.DisposeEffectChain();

            state.CustomUnderlayLayer = null;
            state.MasterRoot = null;
            state.SurfaceVisual = null;
            state.SpecularVisual = null;
            state.PointerGlowVisual = null;
            state.SurfaceBrush = null;
            state.SpecularBrush = null;
            state.DrawingSurface = null;
            state.SpecularSurface = null;
            state.LastSpecularTextures = null;
            state.HostElement = null;
            state.SurfaceWidth = 0;
            state.SurfaceHeight = 0;
        }
    }

    public static ContainerVisual? EnsureMasterVisualTree(
        UIElement hostElement,
        HostVisualState state,
        double width,
        double height,
        double cornerRadius)
    {
        if (hostElement == null || state == null || width < 20.0 || height < 18.0)
        {
            return null;
        }

        var hostVisual = ElementCompositionPreview.GetElementVisual(hostElement);
        var compositor = hostVisual.Compositor;
        int pixW = Math.Max(8, (int)Math.Ceiling(width));
        int pixH = Math.Max(8, (int)Math.Ceiling(height));
        float r = (float)Math.Clamp(cornerRadius, 2.0, Math.Min(width, height) * 0.5);

        bool rebuiltHost = state.MasterRoot == null || !ReferenceEquals(state.HostElement, hostElement);
        if (rebuiltHost)
        {
            if (state.HostElement != null && !ReferenceEquals(state.HostElement, hostElement))
            {
                ElementCompositionPreview.SetElementChildVisual(state.HostElement, null);
            }

            state.MasterRoot = compositor.CreateContainerVisual();
            state.CustomUnderlayLayer = compositor.CreateContainerVisual();
            state.MasterRoot.Children.InsertAtBottom(state.CustomUnderlayLayer);

            ElementCompositionPreview.SetElementChildVisual(hostElement, state.MasterRoot);
            state.HostElement = hostElement;
        }

        float fw = (float)width;
        float fh = (float)height;
        if (Math.Abs(state.LastWidth - width) >= 0.4 ||
            Math.Abs(state.LastHeight - height) >= 0.4 ||
            Math.Abs(state.LastRadius - r) >= 0.3 ||
            rebuiltHost)
        {
            state.MasterRoot!.Size = new Vector2(fw, fh);
            if (state.CustomUnderlayLayer != null)
            {
                state.CustomUnderlayLayer.Size = new Vector2(fw, fh);
            }

            var clipGeom = compositor.CreateRoundedRectangleGeometry();
            clipGeom.Size = new Vector2(fw, fh);
            clipGeom.CornerRadius = new Vector2(r, r);
            state.MasterRoot.Clip = compositor.CreateGeometricClip(clipGeom);
        }

        _ = pixW;
        _ = pixH;
        return state.MasterRoot;
    }

    private static void EnsureVisualTree(
        UIElement hostElement,
        HostVisualState state,
        Compositor compositor,
        CompositionGraphicsDevice graphicsDevice,
        int pixW,
        int pixH,
        double width,
        double height,
        float radius)
    {
        bool rebuiltHost = state.MasterRoot == null || !ReferenceEquals(state.HostElement, hostElement);
        if (rebuiltHost)
        {
            if (state.HostElement != null && !ReferenceEquals(state.HostElement, hostElement))
            {
                ElementCompositionPreview.SetElementChildVisual(state.HostElement, null);
            }

            state.MasterRoot = compositor.CreateContainerVisual();
            state.CustomUnderlayLayer ??= compositor.CreateContainerVisual();
        }

        bool needSurfaceInit = rebuiltHost || state.DrawingSurface == null;
        if (needSurfaceInit)
        {
            state.DrawingSurface = graphicsDevice.CreateDrawingSurface(
                new Size(pixW, pixH),
                Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
                Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
            state.SpecularSurface = graphicsDevice.CreateDrawingSurface(
                new Size(pixW, pixH),
                Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
                Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
            state.LastSpecularTextures = null;
            state.SurfaceWidth = pixW;
            state.SurfaceHeight = pixH;

            state.SurfaceBrush = compositor.CreateSurfaceBrush(state.DrawingSurface);
            state.SurfaceBrush.Stretch = CompositionStretch.Fill;

            state.SpecularBrush = compositor.CreateSurfaceBrush(state.SpecularSurface);
            state.SpecularBrush.Stretch = CompositionStretch.Fill;

            state.SurfaceVisual = compositor.CreateSpriteVisual();
            state.SurfaceVisual.Brush = state.SurfaceBrush;

            state.SpecularVisual = compositor.CreateSpriteVisual();
            state.SpecularVisual.Brush = state.SpecularBrush;

            var glowBrush = compositor.CreateRadialGradientBrush();
            glowBrush.MappingMode = CompositionMappingMode.Relative;
            glowBrush.EllipseCenter = new Vector2(0.5f, 0.5f);
            glowBrush.EllipseRadius = new Vector2(0.5f, 0.5f);
            glowBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(132, 255, 255, 255)));
            glowBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.38f, Color.FromArgb(52, 200, 242, 255)));
            glowBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.75f, Color.FromArgb(14, 175, 228, 255)));
            glowBrush.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));

            state.PointerGlowVisual = compositor.CreateSpriteVisual();
            state.PointerGlowVisual.Size = new Vector2(260f, 260f);
            state.PointerGlowVisual.Brush = glowBrush;
            state.PointerGlowVisual.Opacity = 0f;

            // 层序从下至上：底层 Win2D 背景丝带折射 -> GlassHub 0ms 穿底滚动封面折射环 -> 兜底 3D 高光层 -> 鼠标追光层
            state.MasterRoot!.Children.InsertAtTop(state.SurfaceVisual);
            state.MasterRoot.Children.InsertAtTop(state.CustomUnderlayLayer!);
            state.MasterRoot.Children.InsertAtTop(state.SpecularVisual);
            state.MasterRoot.Children.InsertAtTop(state.PointerGlowVisual);

            ElementCompositionPreview.SetElementChildVisual(hostElement, state.MasterRoot);
            state.HostElement = hostElement;
        }
        else if (state.DrawingSurface != null && (state.SurfaceWidth != pixW || state.SurfaceHeight != pixH))
        {
            CanvasComposition.Resize(state.DrawingSurface, new Size(pixW, pixH));
            if (state.SpecularSurface != null)
            {
                CanvasComposition.Resize(state.SpecularSurface, new Size(pixW, pixH));
            }
            state.LastSpecularTextures = null;
            state.SurfaceWidth = pixW;
            state.SurfaceHeight = pixH;
        }

        if (state.MasterRoot != null && state.SurfaceVisual != null)
        {
            float fw = (float)width;
            float fh = (float)height;
            if (Math.Abs(state.LastWidth - width) >= 0.4 ||
                Math.Abs(state.LastHeight - height) >= 0.4 ||
                Math.Abs(state.LastRadius - radius) >= 0.3 ||
                rebuiltHost || needSurfaceInit)
            {
                state.MasterRoot.Size = new Vector2(fw, fh);
                if (state.CustomUnderlayLayer != null)
                {
                    state.CustomUnderlayLayer.Size = new Vector2(fw, fh);
                }
                state.SurfaceVisual.Size = new Vector2(fw, fh);
                if (state.SpecularVisual != null)
                {
                    state.SpecularVisual.Size = new Vector2(fw, fh);
                }

                var clipGeom = compositor.CreateRoundedRectangleGeometry();
                clipGeom.Size = new Vector2(fw, fh);
                clipGeom.CornerRadius = new Vector2(radius, radius);
                state.MasterRoot.Clip = compositor.CreateGeometricClip(clipGeom);
            }
        }
    }

    public void Dispose()
    {
        _mapGenerator.Dispose();
        lock (_deviceGate)
        {
            if (_sharedDevice != null)
            {
                _sharedDevice.DeviceLost -= OnCanvasDeviceLost;
                _sharedDevice = null;
            }
        }
    }
}
