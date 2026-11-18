using System;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AlgerMusicPlayer.WinUI.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace AlgerMusicPlayer.WinUI.Services.System;

/// <summary>
/// Liquid Glass（液态玻璃）全局唯一共享背景场景纹理提供器（对照 Alger 文档第 3 / 8 节）：
/// 所有折射玻璃面板共用唯一一份 BgSceneTarget，彻底移除 RenderTargetBitmap 与 GetPixelsAsync() CPU 截屏回读，
/// 滚动歌单的穿底折射完全交由 GlassHub 的 CompositionVisualSurface 零延迟硬件直通。
/// </summary>
public sealed class LiquidGlassSceneProvider : IDisposable
{
    private static readonly CanvasStrokeStyle s_ribbonStrokeStyle = new()
    {
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round
    };

    private CanvasDevice? _device;
    private CanvasRenderTarget? _bgSceneTarget;
    private CanvasImageSource? _visibleAuroraImageSource;
    private Image? _visibleAuroraHostImage;
    private int _bgW;
    private int _bgH;
    private string _lastStaticBgSignature = string.Empty;

    // 静态壁纸缓存
    private string _cachedWallpaperPath = string.Empty;
    private CanvasBitmap? _cachedWallpaperBitmap;
    private readonly SemaphoreSlim _wallpaperLoadLock = new(1, 1);

    private bool _isDisposed;

    public CanvasBitmap? BackgroundSceneBitmap => _bgSceneTarget;

    public void SetScreenAuroraVisibility(bool visible)
    {
        if (_visibleAuroraHostImage != null)
        {
            _visibleAuroraHostImage.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public void BindDevice(CanvasDevice device)
    {
        if (!ReferenceEquals(_device, device))
        {
            DisposeTextures();
            _device = device;
        }
    }

    public bool EnsureBgSceneReady(
        CanvasDevice device,
        Canvas auroraCanvas,
        double winW,
        double winH,
        CustomThemeProfile profile,
        bool isLight,
        Color accent,
        Color auroraC1,
        Color auroraC2,
        Color auroraC3,
        double phase,
        double pointerShiftX,
        double pointerShiftY)
    {
        if (_isDisposed || winW < 100 || winH < 100)
        {
            return false;
        }

        BindDevice(device);

        int tw = Math.Max(64, (int)Math.Ceiling(winW));
        int th = Math.Max(64, (int)Math.Ceiling(winH));

        bool resized = _bgSceneTarget == null || _bgW != tw || _bgH != th;
        if (resized)
        {
            _bgSceneTarget?.Dispose();
            _bgSceneTarget = new CanvasRenderTarget(device, tw, th, 96f);
            _visibleAuroraImageSource = new CanvasImageSource(device, tw, th, 96f);
            _bgW = tw;
            _bgH = th;
            _lastStaticBgSignature = string.Empty;

            if (_visibleAuroraHostImage != null)
            {
                _visibleAuroraHostImage.Source = _visibleAuroraImageSource;
                _visibleAuroraHostImage.Width = tw;
                _visibleAuroraHostImage.Height = th;
            }
        }

        string backdropKind = profile.BackdropType ?? "Mica";
        bool renderDynamicFluid = backdropKind is "Aurora" or "CoverFlow";
        bool needRedrawBg = true;

        if (!renderDynamicFluid)
        {
            string hasWp = string.Equals(backdropKind, "Image", StringComparison.OrdinalIgnoreCase) && _cachedWallpaperBitmap != null
                ? "1"
                : "0";
            string staticSig = $"{tw}|{th}|{backdropKind}|{_cachedWallpaperPath}|{hasWp}|{isLight}|{profile.WindowBgColorHex}|{profile.BackgroundMaskColorHex}|{profile.BackgroundMaskOpacity}|{accent}";
            if (!resized && _bgSceneTarget != null && string.Equals(_lastStaticBgSignature, staticSig, StringComparison.Ordinal))
            {
                needRedrawBg = false;
            }
            else
            {
                _lastStaticBgSignature = staticSig;
            }
        }
        else
        {
            _lastStaticBgSignature = string.Empty;
        }

        if (needRedrawBg)
        {
            using var ds = _bgSceneTarget!.CreateDrawingSession();
            Color baseBg = isLight
                ? ThemeService.ParseColorOrDefault(profile.WindowBgColorHex, Color.FromArgb(255, 236, 245, 252))
                : ThemeService.ParseColorOrDefault(profile.WindowBgColorHex, Color.FromArgb(255, 11, 17, 30));
            if (isLight && (baseBg.R * 0.299 + baseBg.G * 0.587 + baseBg.B * 0.114) < 140.0)
            {
                baseBg = Color.FromArgb(255, 236, 245, 252);
            }
            ds.Clear(baseBg);

            if (string.Equals(backdropKind, "Image", StringComparison.OrdinalIgnoreCase) &&
                _cachedWallpaperBitmap != null)
            {
                DrawWallpaperUniformToFill(ds, _cachedWallpaperBitmap, tw, th);
                var maskCol = ThemeService.ParseColorOrDefault(profile.BackgroundMaskColorHex, Color.FromArgb(255, 9, 13, 22));
                byte maskA = (byte)Math.Clamp((int)Math.Round(Math.Clamp(profile.BackgroundMaskOpacity, 0, 100) / 100.0 * 255.0), 0, 220);
                if (maskA > 0)
                {
                    ds.FillRectangle(0, 0, tw, th, Color.FromArgb(maskA, maskCol.R, maskCol.G, maskCol.B));
                }
            }
            else if (renderDynamicFluid)
            {
                DrawAuroraOrbsAndOpticalRibbons(
                    ds, device, tw, th, isLight,
                    auroraC1, auroraC2, auroraC3,
                    profile.AuroraIntensity,
                    phase, pointerShiftX, pointerShiftY);
            }
            else
            {
                Color g1 = Color.FromArgb((byte)(isLight ? 42 : 48), accent.R, accent.G, accent.B);
                using var softGrad = new CanvasRadialGradientBrush(device, g1, Colors.Transparent)
                {
                    Center = new Vector2(tw * 0.25f, th * 0.25f),
                    RadiusX = tw * 0.65f,
                    RadiusY = th * 0.65f
                };
                ds.FillRectangle(0, 0, tw, th, softGrad);
            }
        }

        // 将与 BgSceneTarget 像素完全一致的流体高反差丝带输出到屏幕上 GlobalAuroraCanvas 宿主 Image
        if (renderDynamicFluid && auroraCanvas != null && _visibleAuroraImageSource != null)
        {
            if (_visibleAuroraHostImage == null)
            {
                _visibleAuroraHostImage = new Image
                {
                    Stretch = Stretch.Fill,
                    Width = tw,
                    Height = th,
                    Source = _visibleAuroraImageSource,
                    IsHitTestVisible = false
                };
            }

            if (!auroraCanvas.Children.Contains(_visibleAuroraHostImage))
            {
                auroraCanvas.Children.Add(_visibleAuroraHostImage);
            }
            if (_visibleAuroraHostImage.Visibility != Visibility.Visible)
            {
                _visibleAuroraHostImage.Visibility = Visibility.Visible;
            }
            Canvas.SetZIndex(_visibleAuroraHostImage, 5);

            using var screenDs = _visibleAuroraImageSource.CreateDrawingSession(Colors.Transparent);
            screenDs.DrawImage(_bgSceneTarget);
        }
        else if (_visibleAuroraHostImage != null && _visibleAuroraHostImage.Visibility != Visibility.Collapsed)
        {
            _visibleAuroraHostImage.Visibility = Visibility.Collapsed;
        }

        return true;
    }

    /// <summary>
    /// 异步预加载并缓存壁纸位图到 GPU。
    /// </summary>
    public async Task EnsureWallpaperLoadedAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _cachedWallpaperPath = string.Empty;
            _lastStaticBgSignature = string.Empty;
            _cachedWallpaperBitmap?.Dispose();
            _cachedWallpaperBitmap = null;
            return;
        }

        if (string.Equals(_cachedWallpaperPath, path, StringComparison.OrdinalIgnoreCase) && _cachedWallpaperBitmap != null)
        {
            return;
        }

        await _wallpaperLoadLock.WaitAsync();
        try
        {
            if (string.Equals(_cachedWallpaperPath, path, StringComparison.OrdinalIgnoreCase) && _cachedWallpaperBitmap != null)
            {
                return;
            }

            var dev = _device ?? CanvasDevice.GetSharedDevice();
            var bmp = await CanvasBitmap.LoadAsync(dev, path);
            _cachedWallpaperBitmap?.Dispose();
            _cachedWallpaperBitmap = bmp;
            _cachedWallpaperPath = path;
            _lastStaticBgSignature = string.Empty;
        }
        catch
        {
        }
        finally
        {
            _wallpaperLoadLock.Release();
        }
    }

    private static void DrawWallpaperUniformToFill(CanvasDrawingSession ds, CanvasBitmap bmp, float tw, float th)
    {
        float bw = bmp.SizeInPixels.Width;
        float bh = bmp.SizeInPixels.Height;
        if (bw <= 1 || bh <= 1)
        {
            return;
        }

        float scale = MathF.Max(tw / bw, th / bh);
        float dw = bw * scale;
        float dh = bh * scale;
        float dx = (tw - dw) * 0.5f;
        float dy = (th - dh) * 0.5f;
        ds.DrawImage(bmp, new Rect(dx, dy, dw, dh));
    }

    private static void DrawAuroraOrbsAndOpticalRibbons(
        CanvasDrawingSession ds,
        CanvasDevice device,
        float w,
        float h,
        bool isLight,
        Color c1,
        Color c2,
        Color c3,
        double intensityPercent,
        double phase,
        double pointerShiftX,
        double pointerShiftY)
    {
        float factor = (float)(Math.Clamp(intensityPercent, 25, 100) / 100.0);

        // 若主题色板中含翠绿 (#34D399) 易使暗夜玻璃卡片泛绿偏暗：在深色流体背景下自动修正融合为明快洋红 (#EC4899) / 紫罗兰 (#A855F7)
        static Color EnsureVibrantMagentaIfGreen(Color c, Color replacement)
        {
            if (c.G > c.R + 30 && c.G > c.B)
            {
                return Color.FromArgb(
                    255,
                    (byte)Math.Round(c.R * 0.20 + replacement.R * 0.80),
                    (byte)Math.Round(c.G * 0.20 + replacement.G * 0.80),
                    (byte)Math.Round(c.B * 0.20 + replacement.B * 0.80));
            }
            return c;
        }

        Color orbLeft = c1;
        Color orbRightTop = isLight
            ? c2
            : Color.FromArgb(
                255,
                (byte)Math.Round(c2.R * 0.25 + 0xF4 * 0.75),
                (byte)Math.Round(c2.G * 0.25 + 0x3F * 0.75),
                (byte)Math.Round(c2.B * 0.25 + 0x8E * 0.75));
        Color orbRightBot = isLight
            ? c3
            : Color.FromArgb(255, 0xD9, 0x46, 0xEF);
        _ = EnsureVibrantMagentaIfGreen(c3, orbRightBot);

        // 1. 左青右洋红四枚饱满大半径流动光斑，保证所有背景光晕均同源存在于共享背景纹理内
        byte orbCenterA = (byte)Math.Clamp((int)MathF.Round((isLight ? 150f : 220f) * factor), 90, 240);
        DrawSoftOrb(ds, device,
            w * (0.20f + 0.08f * MathF.Sin((float)phase * 0.7f)) + (float)pointerShiftX * 0.7f,
            h * (0.28f + 0.09f * MathF.Cos((float)phase * 0.6f)) + (float)pointerShiftY * 0.6f,
            w * 0.52f, Color.FromArgb(orbCenterA, orbLeft.R, orbLeft.G, orbLeft.B));

        DrawSoftOrb(ds, device,
            w * (0.22f + 0.06f * MathF.Cos((float)phase * 0.55f + 1.0f)) + (float)pointerShiftX * 0.4f,
            h * (0.80f + 0.06f * MathF.Sin((float)phase * 0.65f + 0.9f)) - (float)pointerShiftY * 0.4f,
            w * 0.44f, Color.FromArgb((byte)Math.Min(235, orbCenterA * 0.78f), orbLeft.R, orbLeft.G, orbLeft.B));

        DrawSoftOrb(ds, device,
            w * (0.80f + 0.06f * MathF.Cos((float)phase * 0.65f + 1.2f)) - (float)pointerShiftX * 0.5f,
            h * (0.32f + 0.09f * MathF.Sin((float)phase * 0.85f + 0.6f)) + (float)pointerShiftY * 0.5f,
            w * 0.52f, Color.FromArgb(orbCenterA, orbRightTop.R, orbRightTop.G, orbRightTop.B));

        DrawSoftOrb(ds, device,
            w * (0.76f + 0.07f * MathF.Sin((float)phase * 0.5f + 2.1f)),
            h * (0.76f + 0.08f * MathF.Cos((float)phase * 0.75f + 1.4f)),
            w * 0.50f, Color.FromArgb((byte)Math.Min(235, orbCenterA * 0.94f), orbRightBot.R, orbRightBot.G, orbRightBot.B));

        // 2. 两条平滑高反差流光丝带 + 晶透光环 + 内置柔光漂浮微光气泡（避免脱离 BackgroundLayer 漏采）
        DrawSmoothOpticalRibbon(ds, device, w, h, isLight, orbLeft, orbRightTop, phase, 0.22f, 0.78f, strokeCore: 4.5f, glowWidth: 34f, factor);
        DrawSmoothOpticalRibbon(ds, device, w, h, isLight, orbRightTop, orbRightBot, phase + 1.85, 0.80f, 0.30f, strokeCore: 3.5f, glowWidth: 26f, factor);

        float ringX = w * (0.64f + 0.08f * MathF.Sin((float)phase * 0.55f)) - (float)pointerShiftX * 0.35f;
        float ringY = h * (0.46f + 0.07f * MathF.Cos((float)phase * 0.45f)) - (float)pointerShiftY * 0.35f;
        float ringR = MathF.Min(w, h) * 0.27f;
        byte ringA = (byte)Math.Clamp((int)MathF.Round((isLight ? 115f : 135f) * factor), 40, 175);
        ds.DrawCircle(ringX, ringY, ringR, Color.FromArgb((byte)(ringA / 3), orbLeft.R, orbLeft.G, orbLeft.B), 16f);
        ds.DrawCircle(ringX, ringY, ringR, Color.FromArgb(ringA, orbRightTop.R, orbRightTop.G, orbRightTop.B), 2.5f);

        for (int b = 0; b < 6; b++)
        {
            float tSeed = b * 1.17f;
            float bx = w * (0.14f + 0.14f * b + 0.03f * MathF.Sin((float)phase * 0.4f + tSeed));
            float by = h * (0.20f + ((b * 0.29f + (float)phase * 0.02f) % 0.65f));
            float br = 12f + (b % 3) * 7f;
            byte ba = (byte)(isLight ? 38 : 54);
            Color bc = (b % 2 == 0) ? orbLeft : orbRightTop;
            ds.FillCircle(bx, by, br, Color.FromArgb((byte)(ba / 3), bc.R, bc.G, bc.B));
            ds.DrawCircle(bx, by, br, Color.FromArgb(ba, 255, 255, 255), 1.0f);
        }
    }

    private static void DrawSoftOrb(CanvasDrawingSession ds, CanvasDevice device, float cx, float cy, float radius, Color centerColor)
    {
        using var brush = new CanvasRadialGradientBrush(device, centerColor, Colors.Transparent)
        {
            Center = new Vector2(cx, cy),
            RadiusX = radius,
            RadiusY = radius
        };
        ds.FillCircle(cx, cy, radius, brush);
    }

    private static void DrawSmoothOpticalRibbon(
        CanvasDrawingSession ds,
        CanvasDevice device,
        float w,
        float h,
        bool isLight,
        Color startColor,
        Color endColor,
        double phase,
        float startYFrac,
        float endYFrac,
        float strokeCore,
        float glowWidth,
        float factor)
    {
        float p = (float)phase;
        float y0 = h * startYFrac + MathF.Sin(p * 0.8f) * 38f;
        float y3 = h * endYFrac + MathF.Cos(p * 0.65f) * 42f;
        float c1y = h * (startYFrac * 0.45f + 0.25f) + MathF.Cos(p * 0.9f) * 88f;
        float c2y = h * (endYFrac * 0.65f + 0.22f) + MathF.Sin(p * 0.75f) * 86f;

        using var pb = new CanvasPathBuilder(device);
        pb.BeginFigure(-40f, y0);
        pb.AddCubicBezier(
            new Vector2(w * 0.34f, c1y),
            new Vector2(w * 0.68f, c2y),
            new Vector2(w + 40f, y3));
        pb.EndFigure(CanvasFigureLoop.Open);
        using var geom = CanvasGeometry.CreatePath(pb);

        byte glowA = (byte)Math.Clamp((int)MathF.Round((isLight ? 90f : 112f) * factor), 30, 145);
        byte coreA = (byte)Math.Clamp((int)MathF.Round((isLight ? 168f : 196f) * factor), 60, 235);

        using var glowBrush = new CanvasLinearGradientBrush(
            device,
            Color.FromArgb(glowA, startColor.R, startColor.G, startColor.B),
            Color.FromArgb(glowA, endColor.R, endColor.G, endColor.B))
        {
            StartPoint = new Vector2(0, y0),
            EndPoint = new Vector2(w, y3)
        };

        Color coreHighlightStart = Color.FromArgb(
            coreA,
            (byte)Math.Min(255, startColor.R + 60),
            (byte)Math.Min(255, startColor.G + 60),
            (byte)Math.Min(255, startColor.B + 60));
        Color coreHighlightEnd = Color.FromArgb(
            coreA,
            (byte)Math.Min(255, endColor.R + 60),
            (byte)Math.Min(255, endColor.G + 60),
            (byte)Math.Min(255, endColor.B + 60));

        using var coreBrush = new CanvasLinearGradientBrush(device, coreHighlightStart, coreHighlightEnd)
        {
            StartPoint = new Vector2(0, y0),
            EndPoint = new Vector2(w, y3)
        };

        ds.DrawGeometry(geom, glowBrush, glowWidth, s_ribbonStrokeStyle);
        ds.DrawGeometry(geom, coreBrush, strokeCore, s_ribbonStrokeStyle);
    }

    private void DisposeTextures()
    {
        _bgSceneTarget?.Dispose();
        _cachedWallpaperBitmap?.Dispose();
        _bgSceneTarget = null;
        _cachedWallpaperBitmap = null;
        _cachedWallpaperPath = string.Empty;
        _lastStaticBgSignature = string.Empty;
        _visibleAuroraImageSource = null;
        _bgW = 0;
        _bgH = 0;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }
        _isDisposed = true;
        DisposeTextures();
        _wallpaperLoadLock.Dispose();
    }
}
