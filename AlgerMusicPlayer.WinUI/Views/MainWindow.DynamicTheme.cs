using System.Diagnostics;
using System.Numerics;
using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.System;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.UI;

namespace AlgerMusicPlayer.WinUI.Views;

/// <summary>
/// 主窗口分部类：高性能动态背景引擎（流体极光 / 封面流光 / 动态视频壁纸）、五类氛围粒子画布、悬浮灵动岛布局、卡片质感、全局字体与黑胶旋转唱盘
/// </summary>
public sealed partial class MainWindow
{
    private readonly Stopwatch _dynamicVsyncStopwatch = new();
    private long _lastDynamicFrameTicks;
    private bool _isDynamicVsyncAttached;
    private double _auroraPhase;
    private double _rhythmPhase;
    private double _vinylRotationAngle;
    private string _loadedBgVideoPath = string.Empty;

    // 极光 / 封面流光三光斑节点与合成器 Visual
    private Microsoft.UI.Xaml.Shapes.Ellipse? _auroraOrb1;
    private Microsoft.UI.Xaml.Shapes.Ellipse? _auroraOrb2;
    private Microsoft.UI.Xaml.Shapes.Ellipse? _auroraOrb3;
    private Microsoft.UI.Composition.Visual? _auroraVisual1;
    private Microsoft.UI.Composition.Visual? _auroraVisual2;
    private Microsoft.UI.Composition.Visual? _auroraVisual3;
    private RadialGradientBrush? _auroraBrush1;
    private RadialGradientBrush? _auroraBrush2;
    private RadialGradientBrush? _auroraBrush3;

    // 封面流光当前与目标插值色彩
    private Color _curAuroraC1 = Color.FromArgb(255, 16, 185, 129);
    private Color _curAuroraC2 = Color.FromArgb(255, 99, 102, 241);
    private Color _curAuroraC3 = Color.FromArgb(255, 236, 72, 153);
    private Color _targetCoverC1 = Color.FromArgb(255, 16, 185, 129);
    private Color _targetCoverC2 = Color.FromArgb(255, 59, 130, 246);
    private Color _targetCoverC3 = Color.FromArgb(255, 168, 85, 247);
    private string _lastCoverFlowUrl = string.Empty;

    // 底栏音频律动渐变画刷
    private LinearGradientBrush? _rhythmGlowBrush;

    // 粒子对象池（零帧内 GC 分配，支持密度增减与原地改色）
    private readonly List<ThemeParticleNode> _particlePool = [];
    private string _builtParticleSignature = string.Empty;
    private string _builtParticleType = string.Empty;
    private Color _builtParticleColor;
    private readonly Random _particleRng = new();

    private sealed class ThemeParticleNode
    {
        public required FrameworkElement Element { get; init; }
        public required Microsoft.UI.Composition.Visual Visual { get; init; }
        public RotateTransform? Rotation { get; init; }
        public double X;
        public double Y;
        public double Vx;
        public double Vy;
        public double Size;
        public double BaseOpacity;
        public double Phase;
        public double SpinSpeed;
    }

    private void InitializeDynamicThemeVisualEngine()
    {
        // 注意：WinUI 3 最小化/任务栏还原改变的是 OverlappedPresenter.State，触发的是 DidSizeChange 而非 DidPresenterChange
        _appWindow.Changed += (_, args) =>
        {
            if (args.DidVisibilityChange || args.DidPresenterChange || args.DidSizeChange)
            {
                ScheduleDynamicPowerStateCheck();
            }
        };

        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                ScheduleDynamicPowerStateCheck();
            }
        };

        VisibilityChanged += (_, _) =>
        {
            ScheduleDynamicPowerStateCheck();
        };

        RootLayout.SizeChanged += (_, _) =>
        {
            if (GlobalAuroraCanvas.Visibility == Visibility.Visible)
            {
                UpdateAuroraOrbPositions(0);
            }
            UpdateDynamicEnginePowerState();
        };
    }

    private void ScheduleDynamicPowerStateCheck()
    {
        DispatcherQueue.TryEnqueue(UpdateDynamicEnginePowerState);
        // 兼容任务栏窗口还原动画过渡瞬间 IsIconic 尚未清除的时序，延迟二次确认唤醒
        _ = Task.Run(async () =>
        {
            await Task.Delay(120);
            DispatcherQueue.TryEnqueue(UpdateDynamicEnginePowerState);
        });
    }

    private bool IsMainWindowActivelyVisible()
    {
        if (!_appWindow.IsVisible) return false;
        if (_hWnd != IntPtr.Zero && Helpers.Win32Interop.IsIconic(_hWnd)) return false;
        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            return false;
        return true;
    }

    /// <summary>
    /// 智能休眠电源管理：仅在窗口可见且开启了至少一项动态视觉时才挂载 VSync 垂直同步渲染，休眠时 0% 空转功耗
    /// </summary>
    private void UpdateDynamicEnginePowerState()
    {
        var profile = ViewModel.Theme.GetEffectiveTheme();
        bool visible = IsMainWindowActivelyVisible();

        // 1. 视频壁纸智能播放/暂停
        bool wantVideo = visible &&
                         string.Equals(profile.BackdropType, "Video", StringComparison.OrdinalIgnoreCase) &&
                         !string.IsNullOrWhiteSpace(_loadedBgVideoPath);
        try
        {
            if (GlobalWallpaperVideoPlayer.CoreWebView2 != null)
            {
                if (wantVideo)
                {
                    _ = GlobalWallpaperVideoPlayer.ExecuteScriptAsync("document.getElementById('v')?.play();");
                }
                else
                {
                    _ = GlobalWallpaperVideoPlayer.ExecuteScriptAsync("document.getElementById('v')?.pause();");
                }
            }
        }
        catch
        {
        }

        // 2. 按需挂载/卸载原生 VSync 帧回调（自动匹配 60Hz / 120Hz / 144Hz / 165Hz / 240Hz+ 高刷显示器）
        bool needAurora = profile.BackdropType is "Aurora" or "CoverFlow";
        bool needParticles = profile.EnableParticleOverlay;
        bool needVinylSpin = profile.CoverArtShape == "VinylSpin" && ViewModel.IsPlaying;
        bool needRhythm = profile.EnablePlayBarRhythmGlow && ViewModel.IsPlaying;

        bool shouldRunVsync = visible && (needAurora || needParticles || needVinylSpin || needRhythm);
        if (shouldRunVsync && !_isDynamicVsyncAttached)
        {
            _dynamicVsyncStopwatch.Restart();
            _lastDynamicFrameTicks = 0;
            CompositionTarget.Rendering += OnDynamicThemeVsyncRendering;
            _isDynamicVsyncAttached = true;
        }
        else if (!shouldRunVsync && _isDynamicVsyncAttached)
        {
            CompositionTarget.Rendering -= OnDynamicThemeVsyncRendering;
            _isDynamicVsyncAttached = false;
            _dynamicVsyncStopwatch.Stop();
        }
    }

    // ==================== 动态背景栈（极光 / 封面流光 / 动态视频 / 粒子层） ====================

    private async Task ApplyDynamicBackgroundAndParticlesAsync(CustomThemeProfile profile, Color accent, bool isLight)
    {
        string kind = profile.BackdropType ?? "Mica";

        // 1. 动态视频壁纸层 (.mp4 / .webm)
        if (string.Equals(kind, "Video", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(profile.BackgroundVideoPath) &&
            File.Exists(profile.BackgroundVideoPath))
        {
            GlobalWallpaperVideoPlayer.Visibility = Visibility.Visible;
            if (!string.Equals(_loadedBgVideoPath, profile.BackgroundVideoPath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _loadedBgVideoPath = profile.BackgroundVideoPath;
                    await App.EnsureWebView2Async(GlobalWallpaperVideoPlayer);
                    string videoUri = new Uri(profile.BackgroundVideoPath).AbsoluteUri;
                    string html = $"<!DOCTYPE html><html><head><meta charset='utf-8'><style>html,body{{margin:0;padding:0;overflow:hidden;background:transparent;width:100%;height:100%;}}video{{position:fixed;top:0;left:0;width:100vw;height:100vh;object-fit:cover;pointer-events:none;}}</style></head><body><video id='v' src='{videoUri}' autoplay loop muted playsinline></video></body></html>";
                    GlobalWallpaperVideoPlayer.NavigateToString(html);
                }
                catch
                {
                    _loadedBgVideoPath = string.Empty;
                    GlobalWallpaperVideoPlayer.Visibility = Visibility.Collapsed;
                }
            }

            var maskBase = ThemeService.ParseColorOrDefault(profile.BackgroundMaskColorHex, Color.FromArgb(255, 9, 13, 22));
            GlobalWallpaperMaskBorder.Background = new SolidColorBrush(ThemeService.WithOpacity(maskBase, profile.BackgroundMaskOpacity));
            GlobalWallpaperMaskBorder.Visibility = Visibility.Visible;
        }
        else
        {
            if (!string.IsNullOrEmpty(_loadedBgVideoPath))
            {
                try
                {
                    if (GlobalWallpaperVideoPlayer.CoreWebView2 != null)
                    {
                        GlobalWallpaperVideoPlayer.NavigateToString("<!DOCTYPE html><html><body style='background:transparent;'></body></html>");
                    }
                }
                catch
                {
                }
                _loadedBgVideoPath = string.Empty;
            }
            GlobalWallpaperVideoPlayer.Visibility = Visibility.Collapsed;
        }

        // 2. 动态流体极光 / 专辑封面多色流光呼吸层
        if (kind is "Aurora" or "CoverFlow")
        {
            EnsureAuroraOrbsCreated();
            GlobalAuroraCanvas.Visibility = Visibility.Visible;

            if (kind == "Aurora")
            {
                _curAuroraC1 = ThemeService.ParseColorOrDefault(profile.AuroraColor1Hex, accent);
                _curAuroraC2 = ThemeService.ParseColorOrDefault(profile.AuroraColor2Hex, Color.FromArgb(255, 99, 102, 241));
                _curAuroraC3 = ThemeService.ParseColorOrDefault(profile.AuroraColor3Hex, Color.FromArgb(255, 236, 72, 153));
            }
            else
            {
                string coverUrl = ViewModel.Player.CurrentSong?.PicUrl ?? ViewModel.CurrentCoverUrl;
                if (!string.IsNullOrWhiteSpace(coverUrl))
                {
                    await RefreshCoverFlowColorsFromSongAsync(coverUrl);
                }

                _curAuroraC1 = ThemeService.AdaptAuroraColorForMode(_targetCoverC1, isLight);
                _curAuroraC2 = ThemeService.AdaptAuroraColorForMode(_targetCoverC2, isLight);
                _curAuroraC3 = ThemeService.AdaptAuroraColorForMode(_targetCoverC3, isLight);
            }

            ApplyAuroraOrbColors(profile.AuroraIntensity, isLight);
            UpdateAuroraOrbPositions(0);
        }
        else
        {
            GlobalAuroraCanvas.Visibility = Visibility.Collapsed;
        }

        // 3. 全屏独立氛围粒子层（可叠加在任何背景之上）
        if (profile.EnableParticleOverlay)
        {
            RebuildParticlePoolIfNeeded(profile, accent, isLight);
            GlobalParticleOverlayCanvas.Visibility = Visibility.Visible;
        }
        else
        {
            GlobalParticleOverlayCanvas.Visibility = Visibility.Collapsed;
        }

        UpdateDynamicEnginePowerState();
    }

    private async Task RefreshCoverFlowColorsFromSongAsync(string? coverUrl)
    {
        if (string.IsNullOrWhiteSpace(coverUrl)) return;
        if (string.Equals(_lastCoverFlowUrl, coverUrl, StringComparison.OrdinalIgnoreCase)) return;

        var triad = await ViewModel.Theme.ExtractCoverFlowTriadAsync(coverUrl);
        if (triad == null) return;

        _lastCoverFlowUrl = coverUrl;
        _targetCoverC1 = triad.Value.C1;
        _targetCoverC2 = triad.Value.C2;
        _targetCoverC3 = triad.Value.C3;
    }

    private void EnsureAuroraOrbsCreated()
    {
        if (_auroraOrb1 != null) return;

        (_auroraOrb1, _auroraBrush1, _auroraVisual1) = CreateSingleAuroraOrb(860);
        (_auroraOrb2, _auroraBrush2, _auroraVisual2) = CreateSingleAuroraOrb(780);
        (_auroraOrb3, _auroraBrush3, _auroraVisual3) = CreateSingleAuroraOrb(720);

        GlobalAuroraCanvas.Children.Clear();
        GlobalAuroraCanvas.Children.Add(_auroraOrb1);
        GlobalAuroraCanvas.Children.Add(_auroraOrb2);
        GlobalAuroraCanvas.Children.Add(_auroraOrb3);
    }

    private static (Microsoft.UI.Xaml.Shapes.Ellipse Orb, RadialGradientBrush Brush, Microsoft.UI.Composition.Visual Visual) CreateSingleAuroraOrb(double diameter)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        brush.GradientStops.Add(new GradientStop { Offset = 0.0, Color = Color.FromArgb(150, 16, 185, 129) });
        brush.GradientStops.Add(new GradientStop { Offset = 0.52, Color = Color.FromArgb(68, 16, 185, 129) });
        brush.GradientStops.Add(new GradientStop { Offset = 1.0, Color = Color.FromArgb(0, 16, 185, 129) });

        var orb = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = diameter,
            Height = diameter,
            Fill = brush,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(orb, 0);
        Canvas.SetTop(orb, 0);
        ElementCompositionPreview.SetIsTranslationEnabled(orb, true);
        var visual = ElementCompositionPreview.GetElementVisual(orb);
        return (orb, brush, visual);
    }

    private void ApplyAuroraOrbColors(double intensityPercent, bool isLight)
    {
        if (_auroraBrush1 == null || _auroraBrush2 == null || _auroraBrush3 == null) return;

        double factor = Math.Clamp(intensityPercent, 20, 100) / 100.0;
        byte centerAlpha = (byte)Math.Round((isLight ? 128 : 185) * factor);
        byte midAlpha = (byte)Math.Round((isLight ? 54 : 82) * factor);

        SetRadialStops(_auroraBrush1, _curAuroraC1, centerAlpha, midAlpha);
        SetRadialStops(_auroraBrush2, _curAuroraC2, centerAlpha, midAlpha);
        SetRadialStops(_auroraBrush3, _curAuroraC3, (byte)(centerAlpha * 0.88), (byte)(midAlpha * 0.85));
    }

    private static void SetRadialStops(RadialGradientBrush brush, Color c, byte centerAlpha, byte midAlpha)
    {
        brush.GradientStops[0].Color = Color.FromArgb(centerAlpha, c.R, c.G, c.B);
        brush.GradientStops[1].Color = Color.FromArgb(midAlpha, c.R, c.G, c.B);
        brush.GradientStops[2].Color = Color.FromArgb(0, c.R, c.G, c.B);
    }

    private void UpdateAuroraOrbPositions(double dtSeconds)
    {
        if (_auroraOrb1 == null || _auroraOrb2 == null || _auroraOrb3 == null ||
            _auroraVisual1 == null || _auroraVisual2 == null || _auroraVisual3 == null) return;

        var profile = ViewModel.Theme.GetEffectiveTheme();
        double speed = Math.Clamp(profile.AuroraSpeed, 0.2, 3.0);
        _auroraPhase += dtSeconds * 0.42 * speed;

        double w = Math.Max(960, RootLayout.ActualWidth);
        double h = Math.Max(640, RootLayout.ActualHeight);

        float x1 = (float)(w * (0.24 + 0.22 * Math.Sin(_auroraPhase * 0.9)) - _auroraOrb1.Width * 0.5);
        float y1 = (float)(h * (0.28 + 0.24 * Math.Cos(_auroraPhase * 0.7)) - _auroraOrb1.Height * 0.5);

        float x2 = (float)(w * (0.74 + 0.20 * Math.Cos(_auroraPhase * 0.8 + 1.4)) - _auroraOrb2.Width * 0.5);
        float y2 = (float)(h * (0.62 + 0.22 * Math.Sin(_auroraPhase * 1.05 + 0.8)) - _auroraOrb2.Height * 0.5);

        float x3 = (float)(w * (0.48 + 0.25 * Math.Sin(_auroraPhase * 0.65 + 2.8)) - _auroraOrb3.Width * 0.5);
        float y3 = (float)(h * (0.76 + 0.20 * Math.Cos(_auroraPhase * 0.95 + 2.1)) - _auroraOrb3.Height * 0.5);

        _auroraVisual1.Properties.InsertVector3("Translation", new Vector3(x1, y1, 0f));
        _auroraVisual2.Properties.InsertVector3("Translation", new Vector3(x2, y2, 0f));
        _auroraVisual3.Properties.InsertVector3("Translation", new Vector3(x3, y3, 0f));
    }

    // ==================== 五类氛围粒子对象池构建与物理演进 ====================

    private void RebuildParticlePoolIfNeeded(CustomThemeProfile profile, Color accent, bool isLightUi)
    {
        int count = Math.Clamp(profile.ParticleDensity, 10, 90);
        string type = string.IsNullOrWhiteSpace(profile.ParticleType) ? "Starlight" : profile.ParticleType;
        Color baseColor = ResolveParticleColor(type, profile.ParticleColorHex, accent, isLightUi);

        string sig = $"{type}|{count}|{ThemeService.ToHex(baseColor)}|{(isLightUi ? "L" : "D")}";
        if (_builtParticleSignature == sig && _particlePool.Count == count)
            return;

        double w = Math.Max(1200, RootLayout.ActualWidth);
        double h = Math.Max(760, RootLayout.ActualHeight);

        // 同类型粒子：增量增减密度并原地更新颜色，避免拖动滑块/调色盘时全屏粒子闪烁重建
        if (string.Equals(_builtParticleType, type, StringComparison.Ordinal) && _particlePool.Count > 0)
        {
            while (_particlePool.Count > count)
            {
                int last = _particlePool.Count - 1;
                GlobalParticleOverlayCanvas.Children.Remove(_particlePool[last].Element);
                _particlePool.RemoveAt(last);
            }

            if (_builtParticleColor != baseColor)
            {
                foreach (var node in _particlePool)
                {
                    UpdateParticleNodeColor(node.Element, type, baseColor);
                }
            }

            while (_particlePool.Count < count)
            {
                var node = CreateParticleNode(type, baseColor, w, h);
                _particlePool.Add(node);
                GlobalParticleOverlayCanvas.Children.Add(node.Element);
            }

            _builtParticleSignature = sig;
            _builtParticleColor = baseColor;
            return;
        }

        _builtParticleSignature = sig;
        _builtParticleType = type;
        _builtParticleColor = baseColor;
        _particlePool.Clear();
        GlobalParticleOverlayCanvas.Children.Clear();

        for (int i = 0; i < count; i++)
        {
            var node = CreateParticleNode(type, baseColor, w, h);
            _particlePool.Add(node);
            GlobalParticleOverlayCanvas.Children.Add(node.Element);
        }
    }

    private static void UpdateParticleNodeColor(FrameworkElement el, string type, Color color)
    {
        if (type == "Bubble" && el is Border bubble)
        {
            if (bubble.Background is SolidColorBrush bg) bg.Color = Color.FromArgb(36, color.R, color.G, color.B);
            if (bubble.BorderBrush is SolidColorBrush bd) bd.Color = Color.FromArgb(145, color.R, color.G, color.B);
        }
        else if (el is Border petal && petal.Background is SolidColorBrush pbg)
        {
            pbg.Color = color;
        }
        else if (el is Microsoft.UI.Xaml.Shapes.Ellipse dot && dot.Fill is SolidColorBrush fill)
        {
            fill.Color = color;
        }
    }

    private static Color ResolveParticleColor(string type, string customHex, Color accent, bool isLightUi)
    {
        if (ThemeService.TryParseColor(customHex, out var custom))
            return custom;

        return type switch
        {
            "Sakura" => isLightUi ? Color.FromArgb(255, 244, 114, 182) : Color.FromArgb(255, 251, 182, 206),
            "Firefly" => Color.FromArgb(255, 253, 224, 71),
            "Snow" => isLightUi ? Color.FromArgb(255, 148, 163, 184) : Color.FromArgb(255, 241, 245, 249),
            "Bubble" => accent,
            _ => isLightUi ? accent : Color.FromArgb(255, 226, 232, 240)
        };
    }

    private ThemeParticleNode CreateParticleNode(string type, Color color, double areaW, double areaH)
    {
        double x = _particleRng.NextDouble() * areaW;
        double y = _particleRng.NextDouble() * areaH;
        double phase = _particleRng.NextDouble() * Math.PI * 2;

        FrameworkElement el;
        RotateTransform? rot = null;
        double vx = 0, vy = 0, size = 6, baseOp = 0.65, spin = 0;

        switch (type)
        {
            case "Sakura":
                size = 7 + _particleRng.NextDouble() * 7;
                rot = new RotateTransform { Angle = _particleRng.NextDouble() * 360 };
                el = new Border
                {
                    Width = size * 1.35,
                    Height = size * 0.82,
                    CornerRadius = new CornerRadius(size, 2, size, 2),
                    Background = new SolidColorBrush(color),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = rot
                };
                vx = 14 + _particleRng.NextDouble() * 18;
                vy = 26 + _particleRng.NextDouble() * 26;
                spin = (_particleRng.NextDouble() - 0.5) * 95;
                baseOp = 0.42 + _particleRng.NextDouble() * 0.38;
                break;

            case "Firefly":
                size = 4 + _particleRng.NextDouble() * 5.5;
                el = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = size,
                    Height = size,
                    Fill = new SolidColorBrush(color)
                };
                vx = (_particleRng.NextDouble() - 0.5) * 22;
                vy = -8 - _particleRng.NextDouble() * 16;
                baseOp = 0.35 + _particleRng.NextDouble() * 0.55;
                break;

            case "Snow":
                size = 3 + _particleRng.NextDouble() * 5;
                el = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = size,
                    Height = size,
                    Fill = new SolidColorBrush(color)
                };
                vx = (_particleRng.NextDouble() - 0.5) * 14;
                vy = 22 + _particleRng.NextDouble() * 28;
                baseOp = 0.35 + _particleRng.NextDouble() * 0.48;
                break;

            case "Bubble":
                size = 8 + _particleRng.NextDouble() * 14;
                el = new Border
                {
                    Width = size,
                    Height = size,
                    CornerRadius = new CornerRadius(size * 0.5),
                    Background = new SolidColorBrush(Color.FromArgb(36, color.R, color.G, color.B)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(145, color.R, color.G, color.B)),
                    BorderThickness = new Thickness(1)
                };
                vx = (_particleRng.NextDouble() - 0.5) * 12;
                vy = -18 - _particleRng.NextDouble() * 24;
                baseOp = 0.32 + _particleRng.NextDouble() * 0.45;
                break;

            default: // Starlight
                size = 2.2 + _particleRng.NextDouble() * 3.8;
                el = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = size,
                    Height = size,
                    Fill = new SolidColorBrush(color)
                };
                vx = (_particleRng.NextDouble() - 0.5) * 6;
                vy = -4 - _particleRng.NextDouble() * 8;
                baseOp = 0.25 + _particleRng.NextDouble() * 0.65;
                break;
        }

        Canvas.SetLeft(el, 0);
        Canvas.SetTop(el, 0);
        ElementCompositionPreview.SetIsTranslationEnabled(el, true);
        var visual = ElementCompositionPreview.GetElementVisual(el);
        visual.Opacity = (float)baseOp;
        visual.Properties.InsertVector3("Translation", new Vector3((float)x, (float)y, 0f));

        return new ThemeParticleNode
        {
            Element = el,
            Visual = visual,
            Rotation = rot,
            X = x,
            Y = y,
            Vx = vx,
            Vy = vy,
            Size = size,
            BaseOpacity = baseOp,
            Phase = phase,
            SpinSpeed = spin
        };
    }

    private void OnDynamicThemeVsyncRendering(object? sender, object args)
    {
        if (!IsMainWindowActivelyVisible())
        {
            UpdateDynamicEnginePowerState();
            return;
        }

        long nowTicks = _dynamicVsyncStopwatch.ElapsedTicks;
        double rawDt = _lastDynamicFrameTicks == 0
            ? (1.0 / 120.0)
            : (nowTicks - _lastDynamicFrameTicks) / (double)Stopwatch.Frequency;
        _lastDynamicFrameTicks = nowTicks;

        if (rawDt <= 0.0004) return;
        double dt = Math.Clamp(rawDt, 0.001, 0.10);

        var profile = ViewModel.Theme.GetEffectiveTheme();

        // 1. 推进极光或封面流光色彩渐变与坐标（帧率无关指数插值，60Hz~240Hz+ 速率严格一致）
        if (profile.BackdropType is "Aurora" or "CoverFlow" && GlobalAuroraCanvas.Visibility == Visibility.Visible)
        {
            if (profile.BackdropType == "CoverFlow")
            {
                bool isLight = IsCurrentUiLightMode;
                var goalC1 = ThemeService.AdaptAuroraColorForMode(_targetCoverC1, isLight);
                var goalC2 = ThemeService.AdaptAuroraColorForMode(_targetCoverC2, isLight);
                var goalC3 = ThemeService.AdaptAuroraColorForMode(_targetCoverC3, isLight);

                double lerpT = Math.Clamp(1.0 - Math.Exp(-1.8 * dt), 0.002, 0.25);
                _curAuroraC1 = ThemeService.LerpColor(_curAuroraC1, goalC1, lerpT);
                _curAuroraC2 = ThemeService.LerpColor(_curAuroraC2, goalC2, lerpT);
                _curAuroraC3 = ThemeService.LerpColor(_curAuroraC3, goalC3, lerpT);
                double effIntensity = isLight ? Math.Clamp(profile.AuroraIntensity, 38, 56) : profile.AuroraIntensity;
                ApplyAuroraOrbColors(effIntensity, isLight);
            }
            UpdateAuroraOrbPositions(dt);
        }

        // 2. 推进全屏粒子运动与呼吸闪烁（直接更新合成器 Visual.Translation 与 Opacity，零 XAML 布局重排）
        if (profile.EnableParticleOverlay && GlobalParticleOverlayCanvas.Visibility == Visibility.Visible && _particlePool.Count > 0)
        {
            double w = Math.Max(800, RootLayout.ActualWidth);
            double h = Math.Max(550, RootLayout.ActualHeight);
            double speedMul = Math.Clamp(profile.ParticleSpeed, 0.3, 2.5);
            string pType = profile.ParticleType;

            foreach (var p in _particlePool)
            {
                p.Phase += dt * (pType == "Starlight" ? 2.4 : 1.5);
                double sway = Math.Sin(p.Phase) * (pType is "Sakura" or "Snow" ? 16.0 : 8.0);

                p.X += (p.Vx + sway) * speedMul * dt;
                p.Y += p.Vy * speedMul * dt;

                if (p.X < -24) p.X = w + 18;
                else if (p.X > w + 24) p.X = -18;

                if (p.Vy > 0 && p.Y > h + 24)
                {
                    p.Y = -18;
                    p.X = _particleRng.NextDouble() * w;
                }
                else if (p.Vy < 0 && p.Y < -24)
                {
                    p.Y = h + 18;
                    p.X = _particleRng.NextDouble() * w;
                }

                p.Visual.Properties.InsertVector3("Translation", new Vector3((float)p.X, (float)p.Y, 0f));

                if (p.Rotation != null)
                {
                    p.Rotation.Angle = (p.Rotation.Angle + p.SpinSpeed * speedMul * dt) % 360.0;
                }

                if (pType is "Starlight" or "Firefly")
                {
                    p.Visual.Opacity = (float)Math.Clamp(p.BaseOpacity * (0.45 + 0.55 * (0.5 + 0.5 * Math.Sin(p.Phase * 1.4))), 0.08, 0.95);
                }
            }
        }

        // 3. 推进左下角黑胶唱片旋转动画
        if (profile.CoverArtShape == "VinylSpin" && ViewModel.IsPlaying)
        {
            _vinylRotationAngle = (_vinylRotationAngle + dt * 32.0) % 360.0;
            BottomCoverRotateTransform.Angle = _vinylRotationAngle;
        }

        // 4. 推进播放栏顶沿流光律动呼吸灯带
        if (profile.EnablePlayBarRhythmGlow && ViewModel.IsPlaying && _rhythmGlowBrush != null && _rhythmGlowBrush.GradientStops.Count >= 3)
        {
            _rhythmPhase = (_rhythmPhase + dt * 1.6) % (Math.PI * 2);
            double center = 0.5 + 0.38 * Math.Sin(_rhythmPhase);
            _rhythmGlowBrush.GradientStops[1].Offset = Math.Clamp(center, 0.12, 0.88);
            PlayBarRhythmGlowBar.Opacity = 0.65 + 0.35 * (0.5 + 0.5 * Math.Cos(_rhythmPhase * 2.0));
        }
    }

    // ==================== 界面形态、卡片质感风格、字体与黑胶封面样式 ====================

    private void ApplyMorphologyTypographyAndCoverDetails(CustomThemeProfile profile, Color accent, bool isLight)
    {
        bool isCustomLayer = profile.BackdropType is "Image" or "Solid" or "Aurora" or "CoverFlow" or "Video";
        bool isLiquidGlass = IsLiquidGlassMode(profile);
        bool isCrystalClear = IsCrystalClearMode(profile);
        Color tintedBarSurface = ThemeService.CreateThemeTintedSurfaceColor(profile, accent, isLight, isLight ? 0.14 : 0.24);
        Color borderMix = ThemeService.LerpColor(Colors.White, accent, isLight ? 0.26 : 0.48);

        // 1. 底部播放栏形态切换（Docked | FloatingIsland | MinimalGlass）
        string pbMode = profile.PlayBarLayoutMode ?? "Docked";
        if (pbMode == "FloatingIsland")
        {
            RootLayout.RowDefinitions[2].Height = new GridLength(98);
            BottomPlayBarBorder.Margin = new Thickness(22, 0, 22, 10);
            BottomPlayBarBorder.CornerRadius = new CornerRadius(24);
            BottomPlayBarBorder.BorderThickness = new Thickness(1);
            BottomPlayBarBorder.BackgroundSizing = BackgroundSizing.OuterBorderEdge;
            BottomPlayBarBorder.Padding = new Thickness(20, 2, 20, 6);

            if (!profile.EnableCustomPlayBar)
            {
                if (isLiquidGlass)
                {
                    BottomPlayBarBorder.Background = ThemeService.CreateLiquidGlassSurfaceBrush(accent, isLight, intensity: 1.08, blurAmount: 0.85f);
                    BottomPlayBarBorder.BorderBrush = ThemeService.CreateLiquidGlassBorderBrush(accent, intensity: 1.06);
                }
                else if (isCrystalClear)
                {
                    BottomPlayBarBorder.Background = new SolidColorBrush(Colors.Transparent);
                    BottomPlayBarBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(76, 148, 163, 184));
                }
                else
                {
                    BottomPlayBarBorder.Background = isLight
                        ? ThemeService.CreateFrostedGlassBrush(tintedBarSurface, tintOpacity: 0.18, luminosityOpacity: 0.28, fallbackAlpha: 62)
                        : ThemeService.CreateFrostedGlassBrush(tintedBarSurface, tintOpacity: 0.45, luminosityOpacity: 0.58, fallbackAlpha: 132);
                    BottomPlayBarBorder.BorderBrush = new SolidColorBrush(isLight
                        ? Color.FromArgb(190, borderMix.R, borderMix.G, borderMix.B)
                        : Color.FromArgb(105, accent.R, accent.G, accent.B));
                }
            }
        }
        else if (pbMode == "MinimalGlass")
        {
            RootLayout.RowDefinitions[2].Height = new GridLength(86);
            BottomPlayBarBorder.Margin = new Thickness(0);
            BottomPlayBarBorder.CornerRadius = new CornerRadius(0);
            BottomPlayBarBorder.BorderThickness = new Thickness(0);
            BottomPlayBarBorder.Padding = new Thickness(18, 2, 18, 8);

            if (!profile.EnableCustomPlayBar)
            {
                if (isLiquidGlass)
                {
                    BottomPlayBarBorder.Background = ThemeService.CreateLiquidGlassSurfaceBrush(accent, isLight, intensity: 0.94, blurAmount: 0.85f);
                }
                else if (isCrystalClear)
                {
                    BottomPlayBarBorder.Background = new SolidColorBrush(Colors.Transparent);
                }
                else
                {
                    BottomPlayBarBorder.Background = isLight
                        ? ThemeService.CreateFrostedGlassBrush(tintedBarSurface, tintOpacity: 0.12, luminosityOpacity: 0.20, fallbackAlpha: 44)
                        : ThemeService.CreateFrostedGlassBrush(tintedBarSurface, tintOpacity: 0.30, luminosityOpacity: 0.44, fallbackAlpha: 92);
                }
            }
        }
        else
        {
            RootLayout.RowDefinitions[2].Height = new GridLength(88);
            BottomPlayBarBorder.Margin = new Thickness(0);
            BottomPlayBarBorder.CornerRadius = new CornerRadius(0);
            BottomPlayBarBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            BottomPlayBarBorder.Padding = new Thickness(18, 2, 18, 8);

            if (!profile.EnableCustomPlayBar && isCustomLayer)
            {
                if (isLiquidGlass)
                {
                    BottomPlayBarBorder.Background = ThemeService.CreateLiquidGlassSurfaceBrush(accent, isLight, intensity: 1.05, blurAmount: 0.85f);
                    BottomPlayBarBorder.BorderBrush = ThemeService.CreateLiquidGlassBorderBrush(accent, intensity: 1.0);
                }
                else if (isCrystalClear)
                {
                    BottomPlayBarBorder.Background = new SolidColorBrush(Colors.Transparent);
                    BottomPlayBarBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(68, 148, 163, 184));
                }
                else
                {
                    BottomPlayBarBorder.Background = isLight
                        ? ThemeService.CreateFrostedGlassBrush(tintedBarSurface, tintOpacity: 0.18, luminosityOpacity: 0.28, fallbackAlpha: 62)
                        : ThemeService.CreateFrostedGlassBrush(tintedBarSurface, tintOpacity: 0.44, luminosityOpacity: 0.56, fallbackAlpha: 128);
                    BottomPlayBarBorder.BorderBrush = new SolidColorBrush(isLight
                        ? Color.FromArgb(180, borderMix.R, borderMix.G, borderMix.B)
                        : Color.FromArgb(68, borderMix.R, borderMix.G, borderMix.B));
                }
            }
        }

        // 2. 底栏音频律动流光呼吸灯带
        if (profile.EnablePlayBarRhythmGlow)
        {
            var endCol = profile.EnableAccentGradient
                ? ThemeService.ParseColorOrDefault(profile.AccentGradientEndHex, ThemeService.AdjustLightness(accent, 0.15))
                : ThemeService.HslToColor((ThemeService.RgbToHsl(accent.R, accent.G, accent.B).H + 45) % 360, 0.85, 0.58);

            _rhythmGlowBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };
            _rhythmGlowBrush.GradientStops.Add(new GradientStop { Offset = 0.0, Color = Color.FromArgb(30, accent.R, accent.G, accent.B) });
            _rhythmGlowBrush.GradientStops.Add(new GradientStop { Offset = 0.5, Color = endCol });
            _rhythmGlowBrush.GradientStops.Add(new GradientStop { Offset = 1.0, Color = Color.FromArgb(30, accent.R, accent.G, accent.B) });

            PlayBarRhythmGlowBar.Background = _rhythmGlowBrush;
            PlayBarRhythmGlowBar.Opacity = ViewModel.IsPlaying ? 0.9 : 0.45;
            PlayBarRhythmGlowBar.Visibility = Visibility.Visible;
        }
        else
        {
            PlayBarRhythmGlowBar.Visibility = Visibility.Collapsed;
        }

        // 3. 左下角歌曲封面形态（圆角方形 / 正圆 / 黑胶唱片旋转）
        string coverShape = profile.CoverArtShape ?? "RoundedSquare";
        if (coverShape == "VinylSpin")
        {
            BottomCoverWrapperBtn.CornerRadius = new CornerRadius(24);
            BottomCoverClipBorder.CornerRadius = new CornerRadius(24);
            BottomCoverClipBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(225, 24, 24, 27));
            BottomCoverClipBorder.BorderThickness = new Thickness(3.5);
            BottomCoverHoverArrowOverlay.CornerRadius = new CornerRadius(24);
            BottomCoverVinylCenterRing.Visibility = Visibility.Visible;
        }
        else if (coverShape == "Circle")
        {
            BottomCoverWrapperBtn.CornerRadius = new CornerRadius(24);
            BottomCoverClipBorder.CornerRadius = new CornerRadius(24);
            BottomCoverClipBorder.BorderThickness = new Thickness(0);
            BottomCoverHoverArrowOverlay.CornerRadius = new CornerRadius(24);
            BottomCoverVinylCenterRing.Visibility = Visibility.Collapsed;
            BottomCoverRotateTransform.Angle = 0;
        }
        else
        {
            BottomCoverWrapperBtn.CornerRadius = new CornerRadius(8);
            BottomCoverClipBorder.CornerRadius = new CornerRadius(8);
            BottomCoverClipBorder.BorderThickness = new Thickness(0);
            BottomCoverHoverArrowOverlay.CornerRadius = new CornerRadius(8);
            BottomCoverVinylCenterRing.Visibility = Visibility.Collapsed;
            BottomCoverRotateTransform.Angle = 0;
        }

        // 4. 卡片质感预设覆盖（当未手动指定详细卡片颜色或叠加风格时赋予鲜明视觉辨识度）
        ApplyCardVisualStyleOverlay(profile, accent, isLight);

        // 5. 全局卡片圆角（OverlayCornerRadius）与按钮胶囊圆角（ControlCornerRadius）全主题同步生效（液态玻璃自动呈现饱满 24px 水滴大圆角）
        double rawCardR = isLiquidGlass ? Math.Max(profile.CardCornerRadius, 24.0) : profile.CardCornerRadius;
        double cardRadius = Math.Clamp(rawCardR, 4, 24);
        double btnRadius = Math.Clamp(profile.ButtonCornerRadius, 4, 22);
        RootLayout.Resources["OverlayCornerRadius"] = new CornerRadius(cardRadius);
        RootLayout.Resources["ControlCornerRadius"] = new CornerRadius(btnRadius);
        ApplyCustomCornerRadiusToVisibleCards(MainContentWorkspaceGrid, new CornerRadius(cardRadius));

        if (!string.IsNullOrWhiteSpace(profile.CustomFontFamily))
        {
            var ff = new FontFamily(profile.CustomFontFamily);
            MainNavView.FontFamily = ff;
            BrandTitleText.FontFamily = ff;
            BottomSongTitleText.FontFamily = ff;
            BottomArtistText.FontFamily = ff;
            PlayModeText.FontFamily = ff;
            ImmersiveLyricListView.FontFamily = ff;
        }
        else
        {
            MainNavView.ClearValue(Control.FontFamilyProperty);
            BrandTitleText.ClearValue(TextBlock.FontFamilyProperty);
            BottomSongTitleText.ClearValue(TextBlock.FontFamilyProperty);
            BottomArtistText.ClearValue(TextBlock.FontFamilyProperty);
            PlayModeText.ClearValue(TextBlock.FontFamilyProperty);
            ImmersiveLyricListView.ClearValue(Control.FontFamilyProperty);
        }
    }

    private void ApplyCardVisualStyleOverlay(CustomThemeProfile profile, Color accent, bool isLight)
    {
        // LiquidGlass 与 CrystalClear 已在 ApplyContentAndCardsSectionTheme 统一注入，无需重复覆盖创建画刷实例
        if (IsLiquidGlassMode(profile) || IsCrystalClearMode(profile))
            return;

        var rootRes = RootLayout.Resources;
        string style = profile.CardVisualStyle ?? "Default";
        if (style == "Default") return;

        Color tintedSurface = ThemeService.CreateThemeTintedSurfaceColor(profile, accent, isLight, isLight ? 0.11 : 0.20);
        Color borderMix = ThemeService.LerpColor(Colors.White, accent, isLight ? 0.22 : 0.42);

        switch (style)
        {

            case "FrostedGlass":
                if (!profile.EnableCustomContent)
                {
                    rootRes["CardBackgroundFillColorDefaultBrush"] = isLight
                        ? ThemeService.CreateFrostedGlassBrush(tintedSurface, tintOpacity: 0.08, luminosityOpacity: 0.14, fallbackAlpha: 38)
                        : ThemeService.CreateFrostedGlassBrush(tintedSurface, tintOpacity: 0.24, luminosityOpacity: 0.36, fallbackAlpha: 72);
                    ThemeService.SetOrUpdateSolidBrush(rootRes, "CardBackgroundFillColorSecondaryBrush", isLight
                        ? Color.FromArgb(26, 255, 255, 255)
                        : Color.FromArgb(54, tintedSurface.R, tintedSurface.G, tintedSurface.B));
                    ThemeService.SetOrUpdateSolidBrush(rootRes, "CardStrokeColorDefaultBrush", isLight
                        ? Color.FromArgb(195, borderMix.R, borderMix.G, borderMix.B)
                        : Color.FromArgb(62, borderMix.R, borderMix.G, borderMix.B));
                }
                break;

            case "NeonBorder":
                if (!profile.EnableCustomContent)
                {
                    rootRes["CardBackgroundFillColorDefaultBrush"] = isLight
                        ? ThemeService.CreateFrostedGlassBrush(tintedSurface, tintOpacity: 0.11, luminosityOpacity: 0.18, fallbackAlpha: 45)
                        : ThemeService.CreateFrostedGlassBrush(tintedSurface, tintOpacity: 0.32, luminosityOpacity: 0.45, fallbackAlpha: 85);
                }
                ThemeService.SetOrUpdateSolidBrush(rootRes, "CardStrokeColorDefaultBrush", Color.FromArgb(180, accent.R, accent.G, accent.B));
                ThemeService.SetOrUpdateSolidBrush(rootRes, "DividerStrokeColorDefaultBrush", Color.FromArgb(80, accent.R, accent.G, accent.B));
                break;

            case "FlatMinimal":
                if (!profile.EnableCustomContent)
                {
                    rootRes["CardBackgroundFillColorDefaultBrush"] = isLight
                        ? ThemeService.CreateFrostedGlassBrush(tintedSurface, tintOpacity: 0.12, luminosityOpacity: 0.20, fallbackAlpha: 48)
                        : ThemeService.CreateFrostedGlassBrush(tintedSurface, tintOpacity: 0.28, luminosityOpacity: 0.40, fallbackAlpha: 78);
                }
                ThemeService.SetOrUpdateSolidBrush(rootRes, "CardStrokeColorDefaultBrush", Colors.Transparent);
                break;

            case "ElevatedCard":
                if (!profile.EnableCustomContent)
                {
                    rootRes["CardBackgroundFillColorDefaultBrush"] = isLight
                        ? ThemeService.CreateFrostedGlassBrush(tintedSurface, tintOpacity: 0.18, luminosityOpacity: 0.28, fallbackAlpha: 64)
                        : ThemeService.CreateFrostedGlassBrush(tintedSurface, tintOpacity: 0.42, luminosityOpacity: 0.54, fallbackAlpha: 120);
                    ThemeService.SetOrUpdateSolidBrush(rootRes, "CardStrokeColorDefaultBrush", isLight
                        ? Color.FromArgb(205, borderMix.R, borderMix.G, borderMix.B)
                        : Color.FromArgb(145, accent.R, accent.G, accent.B));
                }
                break;
        }
    }
}
