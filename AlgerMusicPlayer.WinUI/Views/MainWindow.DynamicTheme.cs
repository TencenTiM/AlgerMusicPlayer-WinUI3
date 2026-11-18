using System.Diagnostics;
using System.Numerics;
using LiquidGlass.WinUI.Materials;
using LiquidGlass.WinUI.Rendering;
using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.System;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
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

    // 底栏音频律动渐变画刷与合成器句柄
    private LinearGradientBrush? _rhythmGlowBrush;
    private Microsoft.UI.Composition.Visual? _bottomCoverVisual;
    private Microsoft.UI.Composition.Visual? _rhythmGlowVisual;
    private Microsoft.UI.Composition.Visual? _wallpaperVisual;

    // 实时音频频谱可视化与指针物理交互节点池（帧内 0 GC 分配）
    private string _builtAudioVizSignature = string.Empty;
    private double _audioVizBuiltWidth;
    private double _audioVizBuiltHeight;
    private double _idleBreathPhase;

    private readonly List<(FrameworkElement Element, Microsoft.UI.Composition.Visual Visual, double MaxHeight)> _specBarVisuals = [];
    private readonly List<(FrameworkElement Element, Microsoft.UI.Composition.Visual Visual)> _specPeakVisuals = [];
    private readonly float[] _specPeakHeights = new float[48];
    private readonly float[] _specPeakHold = new float[48];

    private readonly List<(FrameworkElement Element, Microsoft.UI.Composition.Visual Visual)> _pulseRingVisuals = [];
    private readonly List<(FrameworkElement Element, Microsoft.UI.Composition.Visual Visual)> _pulseRayVisuals = [];
    private Microsoft.UI.Composition.Visual? _pulseRayRingHostVisual;
    private float _pulseRaysRotationDeg;

    private readonly List<(FrameworkElement Element, Microsoft.UI.Composition.Visual Visual, int LayerIndex, int ColIndex)> _silkWaveVisuals = [];

    private Ellipse? _pointerGlowOrb;
    private Microsoft.UI.Composition.Visual? _pointerGlowVisual;
    private double _pointerTargetX;
    private double _pointerTargetY;
    private double _curPointerX;
    private double _curPointerY;
    private bool _pointerCoordsInitialized;
    private bool _isPointerInside;
    private float _pointerEnergy;

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
        public float AngleDegrees;
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
            if (GlobalAudioVisualizerCanvas.Visibility == Visibility.Visible)
            {
                bool isL = IsCurrentUiLightMode;
                var adaptedP = ThemeService.ResolveModeAdaptedProfile(ViewModel.Theme.GetEffectiveTheme(), isL);
                var ac = ThemeService.ParseColorOrDefault(adaptedP.AccentColorHex, Color.FromArgb(255, 16, 185, 129));
                RebuildAudioVisualizerPoolIfNeeded(adaptedP, ac, isL);
            }
            UpdateDynamicEnginePowerState();
        };

        RootLayout.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnRootPointerMovedForTheme), true);
        RootLayout.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(OnRootPointerExitedForTheme), true);
    }

    private void OnRootPointerMovedForTheme(object sender, PointerRoutedEventArgs e)
    {
        var profile = ViewModel.Theme.GetEffectiveTheme();
        var pt = e.GetCurrentPoint(RootLayout).Position;
        if (IsLiquidGlassMode(profile))
        {
            UpdateAllLiquidGlassPointerHighlights(pt, true);
        }

        if (!profile.EnablePointerInteraction) return;

        double dx = pt.X - _pointerTargetX;
        double dy = pt.Y - _pointerTargetY;
        _pointerTargetX = pt.X;
        _pointerTargetY = pt.Y;
        if (!_pointerCoordsInitialized)
        {
            _curPointerX = pt.X;
            _curPointerY = pt.Y;
            _pointerCoordsInitialized = true;
        }
        _isPointerInside = true;
        float speedBoost = (float)Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / 28.0, 0.15, 1.0);
        _pointerEnergy = Math.Max(_pointerEnergy, speedBoost);
    }

    private void OnRootPointerExitedForTheme(object sender, PointerRoutedEventArgs e)
    {
        _isPointerInside = false;
        var profile = ViewModel.Theme.GetEffectiveTheme();
        if (IsLiquidGlassMode(profile))
        {
            UpdateAllLiquidGlassPointerHighlights(default, false);
        }
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

        // 2. 按需挂载/卸载原生 VSync 帧回调与音频分析器（自动匹配 60Hz / 120Hz / 144Hz / 165Hz / 240Hz+ 高刷显示器）
        bool gpuEnabled = GlassHub.IsGpuAccelerationEnabled;
        bool needAurora = gpuEnabled && (profile.BackdropType is "Aurora" or "CoverFlow");
        bool needParticles = gpuEnabled && profile.EnableParticleOverlay;
        bool needVinylSpin = gpuEnabled && profile.CoverArtShape == "VinylSpin" && ViewModel.IsPlaying;
        bool needRhythm = gpuEnabled && profile.EnablePlayBarRhythmGlow && ViewModel.IsPlaying;
        bool needAudioReactive = gpuEnabled && profile.EnableAudioReactive;
        bool needPointerInteract = gpuEnabled && profile.EnablePointerInteraction;

        ViewModel.Player.ReactiveAnalyzer.SetMonitoringActive(visible && needAudioReactive, ViewModel.IsPlaying);

        if (!needAudioReactive)
        {
            if (_wallpaperVisual != null) _wallpaperVisual.Scale = Vector3.One;
            if (_auroraVisual1 != null) _auroraVisual1.Scale = Vector3.One;
            if (_auroraVisual2 != null) _auroraVisual2.Scale = Vector3.One;
            if (_auroraVisual3 != null) _auroraVisual3.Scale = Vector3.One;
        }

        bool shouldRunVsync = visible && (needAurora || needParticles || needVinylSpin || needRhythm || needAudioReactive || needPointerInteract);
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

            // 步骤 6：恢复主题中心原生极光三光斑与粒子层控制，严禁篡改背景层颜色与可见性
            if (_auroraOrb1 != null) _auroraOrb1.Opacity = 1.0;
            if (_auroraOrb2 != null) _auroraOrb2.Opacity = 1.0;
            if (_auroraOrb3 != null) _auroraOrb3.Opacity = 1.0;
            _liquidSceneProvider.SetScreenAuroraVisibility(false);
            ApplyAuroraOrbColors(profile.AuroraIntensity, isLight);
            UpdateAuroraOrbPositions(0);
        }
        else
        {
            _liquidSceneProvider.SetScreenAuroraVisibility(false);
            GlobalAuroraCanvas.Visibility = Visibility.Collapsed;
        }

        // 3. 全屏独立氛围粒子层（统一由主题中心在 GlassBackdropSource 内正常控制，供 GlassPanel 同源采样）
        if (GlassHub.IsGpuAccelerationEnabled && profile.EnableParticleOverlay)
        {
            RebuildParticlePoolIfNeeded(profile, accent, isLight);
            GlobalParticleOverlayCanvas.Visibility = Visibility.Visible;
        }
        else
        {
            GlobalParticleOverlayCanvas.Visibility = Visibility.Collapsed;
        }

        // 4. 实时音频律动可视化与指针互动追光层（非拦截点击）
        if (GlassHub.IsGpuAccelerationEnabled && (profile.EnableAudioReactive || profile.EnablePointerInteraction))
        {
            RebuildAudioVisualizerPoolIfNeeded(profile, accent, isLight);
            GlobalAudioVisualizerCanvas.Visibility = Visibility.Visible;
        }
        else
        {
            GlobalAudioVisualizerCanvas.Visibility = Visibility.Collapsed;
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
        visual.CenterPoint = new Vector3((float)(diameter * 0.5), (float)(diameter * 0.5), 0f);
        return (orb, brush, visual);
    }

    private void ApplyAuroraOrbColors(double intensityPercent, bool isLight)
    {
        if (_auroraBrush1 == null || _auroraBrush2 == null || _auroraBrush3 == null) return;

        double factor = Math.Clamp(intensityPercent, 20, 100) / 100.0;
        byte centerAlpha = (byte)Math.Round((isLight ? 142 : 205) * factor);
        byte midAlpha = (byte)Math.Round((isLight ? 62 : 95) * factor);

        SetRadialStops(_auroraBrush1, _curAuroraC1, centerAlpha, midAlpha);
        SetRadialStops(_auroraBrush2, _curAuroraC2, centerAlpha, midAlpha);
        SetRadialStops(_auroraBrush3, _curAuroraC3, (byte)(centerAlpha * 0.88), (byte)(midAlpha * 0.85));
    }

    private void SetUnderlyingXamlAuroraOrbsSuppressed(bool suppressed)
    {
        if (_auroraOrb1 == null || _auroraOrb2 == null || _auroraOrb3 == null)
            return;

        if (suppressed)
        {
            if (_auroraOrb1.Visibility != Visibility.Collapsed) _auroraOrb1.Visibility = Visibility.Collapsed;
            if (_auroraOrb2.Visibility != Visibility.Collapsed) _auroraOrb2.Visibility = Visibility.Collapsed;
            if (_auroraOrb3.Visibility != Visibility.Collapsed) _auroraOrb3.Visibility = Visibility.Collapsed;
        }
        else if (GlobalAuroraCanvas.Visibility == Visibility.Visible)
        {
            if (_auroraOrb1.Visibility != Visibility.Visible) _auroraOrb1.Visibility = Visibility.Visible;
            if (_auroraOrb2.Visibility != Visibility.Visible) _auroraOrb2.Visibility = Visibility.Visible;
            if (_auroraOrb3.Visibility != Visibility.Visible) _auroraOrb3.Visibility = Visibility.Visible;
        }
    }

    private static void SetRadialStops(RadialGradientBrush brush, Color c, byte centerAlpha, byte midAlpha)
    {
        brush.GradientStops[0].Color = Color.FromArgb(centerAlpha, c.R, c.G, c.B);
        brush.GradientStops[1].Color = Color.FromArgb(midAlpha, c.R, c.G, c.B);
        brush.GradientStops[2].Color = Color.FromArgb(0, c.R, c.G, c.B);
    }

    private void UpdateAuroraOrbPositions(
        double dtSeconds,
        float bass = 0f,
        float mid = 0f,
        float treble = 0f,
        float overall = 0f,
        float beat = 0f)
    {
        if (_auroraOrb1 == null || _auroraOrb2 == null || _auroraOrb3 == null ||
            _auroraVisual1 == null || _auroraVisual2 == null || _auroraVisual3 == null) return;

        var profile = ViewModel.Theme.GetEffectiveTheme();
        double speed = Math.Clamp(profile.AuroraSpeed, 0.2, 3.0);
        double sens = profile.EnableAudioReactive ? Math.Clamp(profile.AudioSensitivity, 0.4, 2.2) : 0.0;
        double audioSpeedBoost = 1.0 + Math.Min(1.6, (overall * 1.15 + beat * 0.65) * sens);
        _auroraPhase += dtSeconds * 0.42 * speed * audioSpeedBoost;

        if (_auroraOrb1.Visibility == Visibility.Collapsed)
        {
            return;
        }

        double w = Math.Max(960, RootLayout.ActualWidth);
        double h = Math.Max(640, RootLayout.ActualHeight);

        double pShiftX = 0;
        double pShiftY = 0;
        if (profile.EnablePointerInteraction && _pointerCoordsInitialized)
        {
            pShiftX = Math.Clamp((_curPointerX - w * 0.5) / w, -0.5, 0.5) * 44.0;
            pShiftY = Math.Clamp((_curPointerY - h * 0.5) / h, -0.5, 0.5) * 32.0;
        }

        float x1 = (float)(w * (0.24 + 0.22 * Math.Sin(_auroraPhase * 0.9)) + pShiftX * 0.9 - _auroraOrb1.Width * 0.5);
        float y1 = (float)(h * (0.28 + 0.24 * Math.Cos(_auroraPhase * 0.7)) + pShiftY * 0.9 - _auroraOrb1.Height * 0.5);

        float x2 = (float)(w * (0.74 + 0.20 * Math.Cos(_auroraPhase * 0.8 + 1.4)) - pShiftX * 0.75 - _auroraOrb2.Width * 0.5);
        float y2 = (float)(h * (0.62 + 0.22 * Math.Sin(_auroraPhase * 1.05 + 0.8)) + pShiftY * 0.7 - _auroraOrb2.Height * 0.5);

        float x3 = (float)(w * (0.48 + 0.25 * Math.Sin(_auroraPhase * 0.65 + 2.8)) + pShiftX * 0.5 - _auroraOrb3.Width * 0.5);
        float y3 = (float)(h * (0.76 + 0.20 * Math.Cos(_auroraPhase * 0.95 + 2.1)) - pShiftY * 0.6 - _auroraOrb3.Height * 0.5);

        _auroraVisual1.Properties.InsertVector3("Translation", new Vector3(x1, y1, 0f));
        _auroraVisual2.Properties.InsertVector3("Translation", new Vector3(x2, y2, 0f));
        _auroraVisual3.Properties.InsertVector3("Translation", new Vector3(x3, y3, 0f));

        if (profile.EnableAudioReactive)
        {
            float s1 = 1f + (float)Math.Clamp((bass * 0.22 + beat * 0.14) * sens, 0, 0.36);
            float s2 = 1f + (float)Math.Clamp((mid * 0.20 + overall * 0.10) * sens, 0, 0.32);
            float s3 = 1f + (float)Math.Clamp((treble * 0.24 + beat * 0.12) * sens, 0, 0.38);
            _auroraVisual1.Scale = new Vector3(s1, s1, 1f);
            _auroraVisual2.Scale = new Vector3(s2, s2, 1f);
            _auroraVisual3.Scale = new Vector3(s3, s3, 1f);
        }
        else
        {
            if (_auroraVisual1.Scale != Vector3.One) _auroraVisual1.Scale = Vector3.One;
            if (_auroraVisual2.Scale != Vector3.One) _auroraVisual2.Scale = Vector3.One;
            if (_auroraVisual3.Scale != Vector3.One) _auroraVisual3.Scale = Vector3.One;
        }
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
        float initAngle = 0f;
        double vx = 0, vy = 0, size = 6, baseOp = 0.65, spin = 0;

        switch (type)
        {
            case "Sakura":
                size = 7 + _particleRng.NextDouble() * 7;
                initAngle = (float)(_particleRng.NextDouble() * 360.0);
                el = new Border
                {
                    Width = size * 1.35,
                    Height = size * 0.82,
                    CornerRadius = new CornerRadius(size, 2, size, 2),
                    Background = new SolidColorBrush(color)
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
        visual.CenterPoint = new Vector3((float)(el.Width * 0.5), (float)(el.Height * 0.5), 0f);
        visual.Properties.InsertVector3("Translation", new Vector3((float)x, (float)y, 0f));
        if (Math.Abs(spin) > 0.01)
        {
            visual.RotationAngleInDegrees = initAngle;
        }

        return new ThemeParticleNode
        {
            Element = el,
            Visual = visual,
            AngleDegrees = initAngle,
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

        // 0. 实时音频 FFT 频谱采样与指针动能平滑追踪（全部使用 stackalloc / 字段缓冲，帧内 0 GC 分配）
        bool wantAudio = profile.EnableAudioReactive;
        float bass = 0f, mid = 0f, treble = 0f, overall = 0f, beat = 0f;
        Span<float> bands = stackalloc float[32];
        if (wantAudio)
        {
            ViewModel.Player.ReactiveAnalyzer.ComputeFrame(
                dt,
                ViewModel.IsPlaying,
                profile.AudioSensitivity,
                bands,
                out bass,
                out mid,
                out treble,
                out overall,
                out beat);

            // 暂停或切歌间隙保持温和微呼吸，避免可视化完全静止僵硬；有真实音频输入后立即交还频谱全权驱动
            if (!ViewModel.IsPlaying || overall < 0.015f)
            {
                _idleBreathPhase = (_idleBreathPhase + dt * 1.45) % (Math.PI * 2);
                float breathBase = ViewModel.IsPlaying ? 0.055f : 0.032f;
                float breathAmp = ViewModel.IsPlaying ? 0.035f : 0.020f;
                for (int i = 0; i < 32; i++)
                {
                    float wave = breathBase + breathAmp * MathF.Sin((float)_idleBreathPhase + i * 0.24f);
                    if (wave > bands[i]) bands[i] = wave;
                }
                bass = MathF.Max(bass, breathBase + breathAmp * 0.6f);
                mid = MathF.Max(mid, breathBase + breathAmp * 0.4f);
                treble = MathF.Max(treble, breathBase * 0.8f);
                overall = MathF.Max(overall, breathBase);
            }
        }

        if (profile.EnablePointerInteraction)
        {
            float pAlpha = (float)Math.Clamp(1.0 - Math.Exp(-14.0 * dt), 0.02, 0.45);
            _curPointerX += (_pointerTargetX - _curPointerX) * pAlpha;
            _curPointerY += (_pointerTargetY - _curPointerY) * pAlpha;
            _pointerEnergy *= (float)Math.Exp(-4.2 * dt);
            if (_pointerEnergy < 0.004f) _pointerEnergy = 0f;
        }

        // 0.5 壁纸图像低音瞬态微呼吸（仅 Image 模式且开启音频律动时生效）
        if (wantAudio && string.Equals(profile.BackdropType, "Image", StringComparison.OrdinalIgnoreCase) &&
            GlobalWallpaperImage.Visibility == Visibility.Visible)
        {
            _wallpaperVisual ??= ElementCompositionPreview.GetElementVisual(GlobalWallpaperImage);
            float wpCx = (float)(RootLayout.ActualWidth > 0 ? RootLayout.ActualWidth * 0.5 : 640);
            float wpCy = (float)(RootLayout.ActualHeight > 0 ? RootLayout.ActualHeight * 0.5 : 380);
            _wallpaperVisual.CenterPoint = new Vector3(wpCx, wpCy, 0f);
            float wpScale = 1f + Math.Clamp(bass * 0.022f + beat * 0.016f, 0f, 0.042f);
            _wallpaperVisual.Scale = new Vector3(wpScale, wpScale, 1f);
        }
        else if (_wallpaperVisual != null && _wallpaperVisual.Scale != Vector3.One)
        {
            _wallpaperVisual.Scale = Vector3.One;
        }

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
                var nextC1 = ThemeService.LerpColor(_curAuroraC1, goalC1, lerpT);
                var nextC2 = ThemeService.LerpColor(_curAuroraC2, goalC2, lerpT);
                var nextC3 = ThemeService.LerpColor(_curAuroraC3, goalC3, lerpT);
                if (nextC1 != _curAuroraC1 || nextC2 != _curAuroraC2 || nextC3 != _curAuroraC3)
                {
                    _curAuroraC1 = nextC1;
                    _curAuroraC2 = nextC2;
                    _curAuroraC3 = nextC3;
                    double effIntensity = isLight ? Math.Clamp(profile.AuroraIntensity, 38, 56) : profile.AuroraIntensity;
                    ApplyAuroraOrbColors(effIntensity, isLight);
                }
            }
            UpdateAuroraOrbPositions(dt, bass, mid, treble, overall, beat);
            if (IsLiquidGlassMode(profile))
            {
                var effAccent = ThemeService.ParseColorOrDefault(profile.AccentColorHex, Color.FromArgb(255, 56, 189, 248));
                TimeSpan renderTime = args is RenderingEventArgs rea ? rea.RenderingTime : TimeSpan.Zero;
                RefreshDynamicLiquidGlassRibbonsFrame(profile, IsCurrentUiLightMode, effAccent, renderTime);
            }
        }

        // 2. 推进全屏粒子运动与呼吸闪烁（100% 直接更新合成器 Visual，结合音频能量加速与指针排斥物理场）
        if (profile.EnableParticleOverlay && GlobalParticleOverlayCanvas.Visibility == Visibility.Visible && _particlePool.Count > 0)
        {
            double w = Math.Max(800, RootLayout.ActualWidth);
            double h = Math.Max(550, RootLayout.ActualHeight);
            double speedMul = Math.Clamp(profile.ParticleSpeed, 0.3, 2.5);
            double audioSpeedMul = wantAudio ? (1.0 + bass * 0.85 + beat * 1.15) : 1.0;
            string pType = profile.ParticleType;
            bool ptrField = profile.EnablePointerInteraction && _isPointerInside && _pointerCoordsInitialized;

            foreach (var p in _particlePool)
            {
                p.Phase += dt * (pType == "Starlight" ? 2.4 : 1.5);
                double sway = Math.Sin(p.Phase) * (pType is "Sakura" or "Snow" ? 16.0 : 8.0);

                p.X += (p.Vx + sway) * speedMul * audioSpeedMul * dt;
                p.Y += p.Vy * speedMul * audioSpeedMul * dt;

                if (ptrField)
                {
                    double dx = p.X - _curPointerX;
                    double dy = p.Y - _curPointerY;
                    double distSq = dx * dx + dy * dy;
                    if (distSq > 4.0 && distSq < 25600.0)
                    {
                        double dist = Math.Sqrt(distSq);
                        double force = (1.0 - dist / 160.0) * (135.0 + _pointerEnergy * 160.0) * dt;
                        p.X += (dx / dist) * force;
                        p.Y += (dy / dist) * force * 0.72;
                    }
                }

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

                if (wantAudio)
                {
                    float pScale = 1f + Math.Clamp(treble * 0.35f + beat * 0.25f, 0f, 0.55f);
                    p.Visual.Scale = new Vector3(pScale, pScale, 1f);
                }
                else if (p.Visual.Scale != Vector3.One)
                {
                    p.Visual.Scale = Vector3.One;
                }

                if (Math.Abs(p.SpinSpeed) > 0.01)
                {
                    p.AngleDegrees = (float)((p.AngleDegrees + p.SpinSpeed * speedMul * audioSpeedMul * dt) % 360.0);
                    p.Visual.RotationAngleInDegrees = p.AngleDegrees;
                }

                if (pType is "Starlight" or "Firefly")
                {
                    double pulse = wantAudio ? treble * 0.35 + beat * 0.22 : 0.0;
                    p.Visual.Opacity = (float)Math.Clamp(p.BaseOpacity * (0.45 + 0.55 * (0.5 + 0.5 * Math.Sin(p.Phase * 1.4))) + pulse, 0.08, 0.98);
                }
            }
        }

        // 3. 推进左下角黑胶唱片旋转动画（纯合成器 Visual 旋转，避免 UI 线程改写 XAML RotateTransform）
        if (profile.CoverArtShape == "VinylSpin" && ViewModel.IsPlaying)
        {
            double spinBoost = wantAudio ? (overall * 22.0 + beat * 16.0) : 0.0;
            _vinylRotationAngle = (_vinylRotationAngle + dt * (32.0 + spinBoost)) % 360.0;
            _bottomCoverVisual ??= ElementCompositionPreview.GetElementVisual(BottomCoverClipBorder);
            float cx = (float)(BottomCoverClipBorder.ActualWidth > 0 ? BottomCoverClipBorder.ActualWidth * 0.5 : 24.0);
            float cy = (float)(BottomCoverClipBorder.ActualHeight > 0 ? BottomCoverClipBorder.ActualHeight * 0.5 : 24.0);
            _bottomCoverVisual.CenterPoint = new Vector3(cx, cy, 0f);
            _bottomCoverVisual.RotationAngleInDegrees = (float)_vinylRotationAngle;
        }

        // 4. 推进播放栏顶沿流光律动呼吸灯带（音频开启时实时随低频鼓点脉冲，否则平稳正弦呼吸）
        if (profile.EnablePlayBarRhythmGlow && ViewModel.IsPlaying && PlayBarRhythmGlowBar.Visibility == Visibility.Visible)
        {
            _rhythmGlowVisual ??= ElementCompositionPreview.GetElementVisual(PlayBarRhythmGlowBar);
            if (wantAudio)
            {
                _rhythmGlowVisual.Opacity = (float)Math.Clamp(0.35f + bass * 0.48f + beat * 0.36f, 0.25f, 1.0f);
            }
            else
            {
                _rhythmPhase = (_rhythmPhase + dt * 2.4) % (Math.PI * 2);
                _rhythmGlowVisual.Opacity = (float)(0.52 + 0.48 * (0.5 + 0.5 * Math.Sin(_rhythmPhase)));
            }
        }

        // 5. 推进实时音频频谱可视化对象池与指针交互追光场
        if ((wantAudio || profile.EnablePointerInteraction) && GlobalAudioVisualizerCanvas.Visibility == Visibility.Visible)
        {
            UpdateAudioVisualizerFrame(profile, bands, bass, mid, treble, overall, beat, dt);
        }
    }

    // ==================== 界面形态、卡片质感风格、字体与黑胶封面样式 ====================

    private void ApplyMorphologyTypographyAndCoverDetails(CustomThemeProfile profile, Color accent, bool isLight)
    {
        bool isLiquidGlass = IsLiquidGlassMode(profile);

        // 1. 穿底实时折射控制：非降级模式开启 RowSpan=2 让主内容区穿过悬浮播放条，展现原生 DWM 实时弯曲与色散；
        //    左侧栏同步底部让位防遮挡，降级模式恢复 RowSpan=1 与贴边布局。
        bool enablePassUnder = !GlassHub.IsDegradedMode;
        if (MainBodyHostGrid != null)
        {
            Grid.SetRowSpan(MainBodyHostGrid, enablePassUnder ? 2 : 1);
        }
        if (NavPaneGlassPanel != null)
        {
            NavPaneGlassPanel.Margin = enablePassUnder
                ? new Thickness(-24, 0, 0, 102)
                : new Thickness(-24, 0, 0, 0);
        }
        if (NavFooterSettingsItem != null)
        {
            NavFooterSettingsItem.Margin = enablePassUnder
                ? new Thickness(0, 0, 0, 106)
                : new Thickness(0);
        }
        Canvas.SetZIndex(BottomPlayBarHostGrid, 10);
        Canvas.SetZIndex(AppTitleBar, 10);
        SyncScrollViewerPassUnderBottomPadding(enablePassUnder);

        RootLayout.RowDefinitions[2].Height = new GridLength(98);
        BottomPlayBarBorder.Margin = new Thickness(0);
        BottomPlayBarBorder.CornerRadius = new CornerRadius(0);
        BottomPlayBarBorder.BorderThickness = new Thickness(0);
        BottomPlayBarBorder.Background = new SolidColorBrush(Colors.Transparent);
        BottomPlayBarBorder.ClearValue(Border.BorderBrushProperty);
        BottomPlayBarBorder.Padding = new Thickness(18, 2, 18, 8);

        SyncPlayBarLiquidGlassButtonAnimations(isLiquidGlass);

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
            PlayBarRhythmGlowBar.Opacity = 1.0;
            _rhythmGlowVisual ??= ElementCompositionPreview.GetElementVisual(PlayBarRhythmGlowBar);
            _rhythmGlowVisual.Opacity = ViewModel.IsPlaying ? 0.9f : 0.45f;
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
            if (_bottomCoverVisual != null) _bottomCoverVisual.RotationAngleInDegrees = 0f;
        }
        else
        {
            BottomCoverWrapperBtn.CornerRadius = new CornerRadius(8);
            BottomCoverClipBorder.CornerRadius = new CornerRadius(8);
            BottomCoverClipBorder.BorderThickness = new Thickness(0);
            BottomCoverHoverArrowOverlay.CornerRadius = new CornerRadius(8);
            BottomCoverVinylCenterRing.Visibility = Visibility.Collapsed;
            BottomCoverRotateTransform.Angle = 0;
            if (_bottomCoverVisual != null) _bottomCoverVisual.RotationAngleInDegrees = 0f;
        }

        // 4. 卡片质感预设覆盖（当未手动指定详细卡片颜色或叠加风格时赋予鲜明视觉辨识度）
        ApplyCardVisualStyleOverlay(profile, accent, isLight);
        ApplyLiquidGlassSlidersTheme(isLiquidGlass, accent, isLight);

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
                    ThemeService.SetOrUpdateSolidBrush(rootRes, "CardBackgroundFillColorDefaultBrush", isLight
                        ? Color.FromArgb(136, 255, 255, 255)
                        : Color.FromArgb(98, tintedSurface.R, tintedSurface.G, tintedSurface.B));
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
                    ThemeService.SetOrUpdateSolidBrush(rootRes, "CardBackgroundFillColorDefaultBrush", isLight
                        ? Color.FromArgb(146, 255, 255, 255)
                        : Color.FromArgb(112, tintedSurface.R, tintedSurface.G, tintedSurface.B));
                }
                ThemeService.SetOrUpdateSolidBrush(rootRes, "CardStrokeColorDefaultBrush", Color.FromArgb(180, accent.R, accent.G, accent.B));
                ThemeService.SetOrUpdateSolidBrush(rootRes, "DividerStrokeColorDefaultBrush", Color.FromArgb(80, accent.R, accent.G, accent.B));
                break;

            case "FlatMinimal":
                if (!profile.EnableCustomContent)
                {
                    ThemeService.SetOrUpdateSolidBrush(rootRes, "CardBackgroundFillColorDefaultBrush", isLight
                        ? Color.FromArgb(152, 255, 255, 255)
                        : Color.FromArgb(104, tintedSurface.R, tintedSurface.G, tintedSurface.B));
                }
                ThemeService.SetOrUpdateSolidBrush(rootRes, "CardStrokeColorDefaultBrush", Colors.Transparent);
                break;

            case "ElevatedCard":
                if (!profile.EnableCustomContent)
                {
                    ThemeService.SetOrUpdateSolidBrush(rootRes, "CardBackgroundFillColorDefaultBrush", isLight
                        ? Color.FromArgb(172, 255, 255, 255)
                        : Color.FromArgb(142, tintedSurface.R, tintedSurface.G, tintedSurface.B));
                    ThemeService.SetOrUpdateSolidBrush(rootRes, "CardStrokeColorDefaultBrush", isLight
                        ? Color.FromArgb(205, borderMix.R, borderMix.G, borderMix.B)
                        : Color.FromArgb(145, accent.R, accent.G, accent.B));
                }
                break;
        }
    }

    private readonly HashSet<Slider> _hookedGlassSliders = [];
    private bool _glassSlidersEnabled;
    private Color _glassSliderAccent;
    private bool _glassSliderIsLight;

    private static readonly string[] s_glassSliderResKeys =
    [
        "SliderTrackFill",
        "SliderTrackFillPointerOver",
        "SliderTrackFillPressed",
        "SliderTrackValueFill",
        "SliderTrackValueFillPointerOver",
        "SliderTrackValueFillPressed",
        "SliderThumbBackground",
        "SliderThumbBackgroundPointerOver",
        "SliderThumbBackgroundPressed"
    ];

    /// <summary>
    /// 液态玻璃进度条与音量条改造（对照 Alger 文档第 2 / 5.6 节）：
    /// 6px 凹槽轨道 + 1px 微亮包边 + 青色流光渐变已播放段 + 18px 晶透白玉滑块球。
    /// 切回普通主题时 100% 干净还原。
    /// </summary>
    private void ApplyLiquidGlassSlidersTheme(bool enable, Color accent, bool isLight)
    {
        _glassSlidersEnabled = enable;
        _glassSliderAccent = accent;
        _glassSliderIsLight = isLight;

        Slider?[] targets = [SeekSlider, VolumeSlider, SimplePlayBarSeekSlider, SimplePlayBarVolumeSlider];
        foreach (var slider in targets)
        {
            if (slider == null)
            {
                continue;
            }

            if (_hookedGlassSliders.Add(slider))
            {
                slider.Loaded += (_, _) =>
                {
                    ApplySingleLiquidGlassSlider(slider, _glassSlidersEnabled, _glassSliderAccent, _glassSliderIsLight);
                };
            }

            ApplySingleLiquidGlassSlider(slider, enable, accent, isLight);
        }
    }

    private static void ApplySingleLiquidGlassSlider(Slider slider, bool enable, Color accent, bool isLight)
    {
        var res = slider.Resources;
        if (!enable)
        {
            foreach (var key in s_glassSliderResKeys)
            {
                res.Remove(key);
            }

            if (FindDescendantByName<Rectangle>(slider, "HorizontalTrackRect") is { } tr)
            {
                tr.ClearValue(FrameworkElement.HeightProperty);
                tr.ClearValue(Rectangle.RadiusXProperty);
                tr.ClearValue(Rectangle.RadiusYProperty);
                tr.ClearValue(Shape.StrokeProperty);
                tr.ClearValue(Shape.StrokeThicknessProperty);
            }
            if (FindDescendantByName<Rectangle>(slider, "HorizontalDecreaseRect") is { } dr)
            {
                dr.ClearValue(FrameworkElement.HeightProperty);
                dr.ClearValue(Rectangle.RadiusXProperty);
                dr.ClearValue(Rectangle.RadiusYProperty);
            }
            if (FindDescendantByName<Thumb>(slider, "HorizontalThumb") is { } th)
            {
                th.ClearValue(FrameworkElement.WidthProperty);
                th.ClearValue(FrameworkElement.HeightProperty);
                th.ClearValue(Control.CornerRadiusProperty);
                th.ClearValue(Control.BorderThicknessProperty);
                th.ClearValue(Control.BorderBrushProperty);
            }
            return;
        }

        Color grooveColor = isLight
            ? Color.FromArgb(0x28, 0x0F, 0x17, 0x2A)
            : Color.FromArgb(0x38, 0, 0, 0);
        Color grooveHover = isLight
            ? Color.FromArgb(0x36, 0x0F, 0x17, 0x2A)
            : Color.FromArgb(0x46, 0, 0, 0);
        res["SliderTrackFill"] = new SolidColorBrush(grooveColor);
        res["SliderTrackFillPointerOver"] = new SolidColorBrush(grooveHover);
        res["SliderTrackFillPressed"] = new SolidColorBrush(grooveHover);

        Color cyanStart = ThemeService.LerpColor(Color.FromArgb(255, 0x3D, 0xCB, 0xF5), accent, 0.22);
        Color cyanEnd = ThemeService.LerpColor(Color.FromArgb(255, 0x7B, 0xE3, 0xFA), accent, 0.14);
        var valBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            GradientStops =
            {
                new GradientStop { Offset = 0.0, Color = cyanStart },
                new GradientStop { Offset = 1.0, Color = cyanEnd }
            }
        };
        res["SliderTrackValueFill"] = valBrush;
        res["SliderTrackValueFillPointerOver"] = valBrush;
        res["SliderTrackValueFillPressed"] = valBrush;

        var thumbBg = new SolidColorBrush(Color.FromArgb(0xF0, 255, 255, 255));
        var thumbActive = new SolidColorBrush(Colors.White);
        res["SliderThumbBackground"] = thumbBg;
        res["SliderThumbBackgroundPointerOver"] = thumbActive;
        res["SliderThumbBackgroundPressed"] = thumbActive;

        if (FindDescendantByName<Rectangle>(slider, "HorizontalTrackRect") is { } trackRect)
        {
            trackRect.Height = 6;
            trackRect.RadiusX = 3;
            trackRect.RadiusY = 3;
            trackRect.Stroke = new SolidColorBrush(isLight
                ? Color.FromArgb(0x50, 255, 255, 255)
                : Color.FromArgb(0x2A, 255, 255, 255));
            trackRect.StrokeThickness = 1;
        }
        if (FindDescendantByName<Rectangle>(slider, "HorizontalDecreaseRect") is { } decRect)
        {
            decRect.Height = 6;
            decRect.RadiusX = 3;
            decRect.RadiusY = 3;
        }
        if (FindDescendantByName<Thumb>(slider, "HorizontalThumb") is { } thumb)
        {
            thumb.Width = 18;
            thumb.Height = 18;
            thumb.CornerRadius = new CornerRadius(9);
            thumb.BorderThickness = new Thickness(1.2);
            thumb.BorderBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Offset = 0.0, Color = Color.FromArgb(0xE8, 255, 255, 255) },
                    new GradientStop { Offset = 1.0, Color = Color.FromArgb(0x90, 0x3D, 0xCB, 0xF5) }
                }
            };
        }
    }

    // ==================== 实时音频频谱可视化对象池与指针追光场（全合成器零分配） ====================

    private void RebuildAudioVisualizerPoolIfNeeded(CustomThemeProfile profile, Color accent, bool isLightUi)
    {
        double w = Math.Max(900, RootLayout.ActualWidth);
        double h = Math.Max(580, RootLayout.ActualHeight);
        string style = string.IsNullOrWhiteSpace(profile.AudioReactiveStyle) ? "SpectrumBars" : profile.AudioReactiveStyle;
        Color accent2 = profile.EnableAccentGradient && ThemeService.TryParseColor(profile.AccentGradientEndHex, out var gradEnd)
            ? gradEnd
            : ThemeService.LerpColor(accent, Color.FromArgb(255, 99, 102, 241), 0.45);

        string sig = $"{profile.EnableAudioReactive}|{style}|{profile.EnablePointerInteraction}|{ThemeService.ToHex(accent)}|{ThemeService.ToHex(accent2)}|{(isLightUi ? "L" : "D")}|{(int)Math.Round(w)}x{(int)Math.Round(h)}";
        if (_builtAudioVizSignature == sig &&
            Math.Abs(_audioVizBuiltWidth - w) < 2 &&
            Math.Abs(_audioVizBuiltHeight - h) < 2)
            return;

        _builtAudioVizSignature = sig;
        _audioVizBuiltWidth = w;
        _audioVizBuiltHeight = h;

        _specBarVisuals.Clear();
        _specPeakVisuals.Clear();
        Array.Clear(_specPeakHeights, 0, _specPeakHeights.Length);
        Array.Clear(_specPeakHold, 0, _specPeakHold.Length);
        _pulseRingVisuals.Clear();
        _pulseRayVisuals.Clear();
        _pulseRayRingHostVisual = null;
        _silkWaveVisuals.Clear();
        _pointerGlowOrb = null;
        _pointerGlowVisual = null;
        GlobalAudioVisualizerCanvas.Children.Clear();

        // 1. 指针跟随环境柔光球（不阻塞任何点击事件）
        if (profile.EnablePointerInteraction)
        {
            const double orbDia = 340;
            var pBrush = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            byte glowCenterAlpha = (byte)(isLightUi ? 44 : 68);
            byte glowMidAlpha = (byte)(isLightUi ? 16 : 26);
            pBrush.GradientStops.Add(new GradientStop { Offset = 0.0, Color = Color.FromArgb(glowCenterAlpha, accent.R, accent.G, accent.B) });
            pBrush.GradientStops.Add(new GradientStop { Offset = 0.45, Color = Color.FromArgb(glowMidAlpha, accent2.R, accent2.G, accent2.B) });
            pBrush.GradientStops.Add(new GradientStop { Offset = 1.0, Color = Color.FromArgb(0, accent2.R, accent2.G, accent2.B) });

            var orb = new Ellipse
            {
                Width = orbDia,
                Height = orbDia,
                Fill = pBrush,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(orb, 0);
            Canvas.SetTop(orb, 0);
            ElementCompositionPreview.SetIsTranslationEnabled(orb, true);
            var orbVisual = ElementCompositionPreview.GetElementVisual(orb);
            orbVisual.CenterPoint = new Vector3((float)(orbDia * 0.5), (float)(orbDia * 0.5), 0f);
            orbVisual.Opacity = _isPointerInside ? 1f : 0f;
            if (_pointerCoordsInitialized)
            {
                orbVisual.Properties.InsertVector3("Translation", new Vector3((float)(_curPointerX - orbDia * 0.5), (float)(_curPointerY - orbDia * 0.5), 0f));
            }
            GlobalAudioVisualizerCanvas.Children.Add(orb);
            _pointerGlowOrb = orb;
            _pointerGlowVisual = orbVisual;
        }

        if (!profile.EnableAudioReactive)
            return;

        // 2. 构建选中的背景实时音频频谱可视化层
        byte baseAlpha = (byte)(isLightUi ? 168 : 215);
        byte midAlpha = (byte)(isLightUi ? 108 : 148);

        if (string.Equals(style, "PulseRings", StringComparison.OrdinalIgnoreCase))
        {
            // 同心声波脉冲环（避开左侧导航栏与底部播放栏，精准居中主视野）
            double cx = w > 540 ? (w + 180) * 0.5 : w * 0.5;
            double cy = (h - 90) * 0.48;

            double[] diameters = [220, 330, 450];
            double[] strokes = [2.2, 1.6, 1.1];
            byte[] strokeAlphas = [(byte)(isLightUi ? 125 : 168), (byte)(isLightUi ? 92 : 128), (byte)(isLightUi ? 64 : 90)];

            for (int r = 0; r < diameters.Length; r++)
            {
                double d = diameters[r];
                Color ringCol = r == 0 ? accent : (r == 1 ? ThemeService.LerpColor(accent, accent2, 0.5) : accent2);
                var ring = new Ellipse
                {
                    Width = d,
                    Height = d,
                    Stroke = new SolidColorBrush(Color.FromArgb(strokeAlphas[r], ringCol.R, ringCol.G, ringCol.B)),
                    StrokeThickness = strokes[r],
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(ring, cx - d * 0.5);
                Canvas.SetTop(ring, cy - d * 0.5);
                var rv = ElementCompositionPreview.GetElementVisual(ring);
                rv.CenterPoint = new Vector3((float)(d * 0.5), (float)(d * 0.5), 0f);
                GlobalAudioVisualizerCanvas.Children.Add(ring);
                _pulseRingVisuals.Add((ring, rv));
            }

            // 32 根 360° 环绕放射音轨，容纳于可平稳旋转的居中宿主画布
            const double hostSize = 620;
            const double ringRadius = 156;
            const double rayW = 5.0;
            const double rayMaxH = 115;
            var rayRingHost = new Canvas
            {
                Width = hostSize,
                Height = hostSize,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(rayRingHost, cx - hostSize * 0.5);
            Canvas.SetTop(rayRingHost, cy - hostSize * 0.5);
            var hostVisual = ElementCompositionPreview.GetElementVisual(rayRingHost);
            hostVisual.CenterPoint = new Vector3((float)(hostSize * 0.5), (float)(hostSize * 0.5), 0f);
            hostVisual.RotationAngleInDegrees = _pulseRaysRotationDeg;
            _pulseRayRingHostVisual = hostVisual;

            for (int i = 0; i < 32; i++)
            {
                double thetaDeg = i * (360.0 / 32.0);
                double thetaRad = thetaDeg * Math.PI / 180.0;
                Color rayTopCol = ThemeService.LerpColor(accent, accent2, i / 31.0);
                var rayBrush = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, 0),
                    EndPoint = new Point(0.5, 1)
                };
                rayBrush.GradientStops.Add(new GradientStop { Offset = 0.0, Color = Color.FromArgb((byte)(baseAlpha + 30), rayTopCol.R, rayTopCol.G, rayTopCol.B) });
                rayBrush.GradientStops.Add(new GradientStop { Offset = 1.0, Color = Color.FromArgb(42, rayTopCol.R, rayTopCol.G, rayTopCol.B) });

                var ray = new Border
                {
                    Width = rayW,
                    Height = rayMaxH,
                    CornerRadius = new CornerRadius(rayW * 0.5),
                    Background = rayBrush,
                    IsHitTestVisible = false
                };
                double anchorX = hostSize * 0.5 + ringRadius * Math.Sin(thetaRad);
                double anchorY = hostSize * 0.5 - ringRadius * Math.Cos(thetaRad);
                Canvas.SetLeft(ray, anchorX - rayW * 0.5);
                Canvas.SetTop(ray, anchorY - rayMaxH);

                var rayVis = ElementCompositionPreview.GetElementVisual(ray);
                rayVis.CenterPoint = new Vector3((float)(rayW * 0.5), (float)rayMaxH, 0f);
                rayVis.RotationAngleInDegrees = (float)thetaDeg;
                rayVis.Scale = new Vector3(1f, 0.16f, 1f);
                rayRingHost.Children.Add(ray);
                _pulseRayVisuals.Add((ray, rayVis));
            }
            GlobalAudioVisualizerCanvas.Children.Add(rayRingHost);
        }
        else if (string.Equals(style, "SilkWave", StringComparison.OrdinalIgnoreCase))
        {
            // 三层丝绸极光波带（3 * 36 = 108 根渐变柔和波柱交织覆盖全宽底栏之上）
            const int colsPerLayer = 36;
            double marginX = 16;
            double availW = Math.Max(480, w - marginX * 2);
            double colStep = availW / colsPerLayer;
            double colW = Math.Max(14, colStep * 1.24);
            double baselineY = Math.Max(340, h - 92);

            Color[] layerColors =
            [
                accent,
                accent2,
                ThemeService.LerpColor(accent, Colors.White, isLightUi ? 0.18 : 0.42)
            ];
            double[] layerMaxHeights =
            [
                Math.Clamp(h * 0.40, 180, 380),
                Math.Clamp(h * 0.33, 150, 315),
                Math.Clamp(h * 0.26, 120, 250)
            ];
            byte[] layerAlphas =
            [
                (byte)(isLightUi ? 105 : 142),
                (byte)(isLightUi ? 88 : 120),
                (byte)(isLightUi ? 72 : 98)
            ];

            for (int layer = 0; layer < 3; layer++)
            {
                Color c = layerColors[layer];
                double maxH = layerMaxHeights[layer];
                byte topA = layerAlphas[layer];

                var waveBrush = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, 0),
                    EndPoint = new Point(0.5, 1)
                };
                waveBrush.GradientStops.Add(new GradientStop { Offset = 0.0, Color = Color.FromArgb(topA, c.R, c.G, c.B) });
                waveBrush.GradientStops.Add(new GradientStop { Offset = 0.55, Color = Color.FromArgb((byte)(topA * 0.55), c.R, c.G, c.B) });
                waveBrush.GradientStops.Add(new GradientStop { Offset = 1.0, Color = Color.FromArgb(0, c.R, c.G, c.B) });

                double stagger = (layer - 1) * (colStep * 0.34);
                for (int col = 0; col < colsPerLayer; col++)
                {
                    var stripe = new Border
                    {
                        Width = colW,
                        Height = maxH,
                        CornerRadius = new CornerRadius(colW * 0.5, colW * 0.5, 0, 0),
                        Background = waveBrush,
                        IsHitTestVisible = false
                    };
                    double x = marginX + col * colStep + stagger - (colW - colStep) * 0.5;
                    Canvas.SetLeft(stripe, x);
                    Canvas.SetTop(stripe, baselineY - maxH);

                    var vis = ElementCompositionPreview.GetElementVisual(stripe);
                    vis.CenterPoint = new Vector3((float)(colW * 0.5), (float)maxH, 0f);
                    vis.Scale = new Vector3(1f, 0.14f, 1f);
                    GlobalAudioVisualizerCanvas.Children.Add(stripe);
                    _silkWaveVisuals.Add((stripe, vis, layer, col));
                }
            }
        }
        else
        {
            // 默认：SpectrumBars（48 根全宽中央镜像频谱柱 + 48 颗峰值悬停珠）
            const int barCount = 48;
            double marginX = 42;
            double availW = Math.Max(420, w - marginX * 2);
            double step = availW / barCount;
            double barW = Math.Clamp(step * 0.60, 4.5, 15.0);
            double baselineY = Math.Max(340, h - 98);
            double maxBarH = Math.Clamp(h * 0.44, 210, 460);

            for (int i = 0; i < barCount; i++)
            {
                double x = marginX + i * step + (step - barW) * 0.5;
                double mirrorT = Math.Abs(i - 23.5) / 23.5;
                Color colTop = ThemeService.LerpColor(accent, accent2, mirrorT);
                var barBrush = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, 0),
                    EndPoint = new Point(0.5, 1)
                };
                barBrush.GradientStops.Add(new GradientStop { Offset = 0.0, Color = Color.FromArgb(baseAlpha, colTop.R, colTop.G, colTop.B) });
                barBrush.GradientStops.Add(new GradientStop { Offset = 0.65, Color = Color.FromArgb(midAlpha, colTop.R, colTop.G, colTop.B) });
                barBrush.GradientStops.Add(new GradientStop { Offset = 1.0, Color = Color.FromArgb(20, colTop.R, colTop.G, colTop.B) });

                var bar = new Border
                {
                    Width = barW,
                    Height = maxBarH,
                    CornerRadius = new CornerRadius(barW * 0.5, barW * 0.5, 2, 2),
                    Background = barBrush,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(bar, x);
                Canvas.SetTop(bar, baselineY - maxBarH);

                var barVis = ElementCompositionPreview.GetElementVisual(bar);
                barVis.CenterPoint = new Vector3((float)(barW * 0.5), (float)maxBarH, 0f);
                barVis.Scale = new Vector3(1f, 0.04f, 1f);
                GlobalAudioVisualizerCanvas.Children.Add(bar);
                _specBarVisuals.Add((bar, barVis, maxBarH));

                var peakCap = new Border
                {
                    Width = barW,
                    Height = 3.8,
                    CornerRadius = new CornerRadius(1.9),
                    Background = new SolidColorBrush(Color.FromArgb((byte)(isLightUi ? 190 : 238), colTop.R, colTop.G, colTop.B)),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(peakCap, x);
                Canvas.SetTop(peakCap, baselineY - 4);
                ElementCompositionPreview.SetIsTranslationEnabled(peakCap, true);
                var peakVis = ElementCompositionPreview.GetElementVisual(peakCap);
                peakVis.Opacity = 0.25f;
                GlobalAudioVisualizerCanvas.Children.Add(peakCap);
                _specPeakVisuals.Add((peakCap, peakVis));
            }
        }
    }

    private void UpdateAudioVisualizerFrame(
        CustomThemeProfile profile,
        ReadOnlySpan<float> bands,
        float bass,
        float mid,
        float treble,
        float overall,
        float beat,
        double dt)
    {
        // 1. 指针跟随柔光球平滑更新
        if (_pointerGlowVisual != null && _pointerGlowOrb != null)
        {
            float targetOp = _isPointerInside ? Math.Clamp(0.55f + _pointerEnergy * 0.35f + bass * 0.25f, 0f, 1f) : 0f;
            float opLerp = (float)Math.Clamp(1.0 - Math.Exp(-8.0 * dt), 0.02, 0.35);
            _pointerGlowVisual.Opacity += (targetOp - _pointerGlowVisual.Opacity) * opLerp;

            if (_pointerCoordsInitialized)
            {
                float halfW = (float)(_pointerGlowOrb.Width * 0.5);
                _pointerGlowVisual.Properties.InsertVector3("Translation", new Vector3((float)_curPointerX - halfW, (float)_curPointerY - halfW, 0f));
                float orbScale = 1f + Math.Clamp(_pointerEnergy * 0.28f + bass * 0.18f + beat * 0.15f, 0f, 0.45f);
                _pointerGlowVisual.Scale = new Vector3(orbScale, orbScale, 1f);
            }
        }

        if (!profile.EnableAudioReactive)
            return;

        string style = string.IsNullOrWhiteSpace(profile.AudioReactiveStyle) ? "SpectrumBars" : profile.AudioReactiveStyle;

        if (string.Equals(style, "PulseRings", StringComparison.OrdinalIgnoreCase))
        {
            if (_pulseRingVisuals.Count >= 3)
            {
                float s0 = 1f + Math.Clamp(bass * 0.42f + beat * 0.28f, 0f, 0.68f);
                float s1 = 1f + Math.Clamp(mid * 0.34f + overall * 0.18f, 0f, 0.54f);
                float s2 = 1f + Math.Clamp(treble * 0.38f + beat * 0.20f, 0f, 0.58f);
                _pulseRingVisuals[0].Visual.Scale = new Vector3(s0, s0, 1f);
                _pulseRingVisuals[1].Visual.Scale = new Vector3(s1, s1, 1f);
                _pulseRingVisuals[2].Visual.Scale = new Vector3(s2, s2, 1f);

                _pulseRingVisuals[0].Visual.Opacity = Math.Clamp(0.38f + bass * 0.42f + beat * 0.25f, 0.28f, 1.0f);
                _pulseRingVisuals[1].Visual.Opacity = Math.Clamp(0.32f + mid * 0.40f, 0.24f, 0.92f);
                _pulseRingVisuals[2].Visual.Opacity = Math.Clamp(0.26f + treble * 0.45f, 0.20f, 0.88f);
            }

            if (_pulseRayRingHostVisual != null)
            {
                _pulseRaysRotationDeg = (_pulseRaysRotationDeg + (float)(dt * (7.5 + overall * 20.0 + beat * 12.0))) % 360f;
                _pulseRayRingHostVisual.RotationAngleInDegrees = _pulseRaysRotationDeg;
            }

            for (int i = 0; i < _pulseRayVisuals.Count; i++)
            {
                float b = i < bands.Length ? bands[i] : overall;
                float scaleY = 0.14f + b * 1.06f;
                var vis = _pulseRayVisuals[i].Visual;
                vis.Scale = new Vector3(1f, scaleY, 1f);
                vis.Opacity = Math.Clamp(0.36f + b * 0.62f, 0.28f, 0.99f);
            }
        }
        else if (string.Equals(style, "SilkWave", StringComparison.OrdinalIgnoreCase))
        {
            float phaseNow = (float)(_idleBreathPhase * 1.45 + _auroraPhase * 1.6);

            foreach (var (_, vis, layer, col) in _silkWaveVisuals)
            {
                float normX = col / 35.0f;
                int bIdx = Math.Clamp((int)(normX * 28f), 0, Math.Max(0, bands.Length - 1));
                float bandVal = bIdx < bands.Length ? bands[bIdx] : overall;
                float layerEnergy = layer == 0 ? bass : (layer == 1 ? mid : treble);
                float waveCarrier = 0.5f + 0.5f * MathF.Sin(phaseNow * (1.1f + layer * 0.38f) + col * (0.36f + layer * 0.14f));
                float amp = Math.Clamp(0.10f + bandVal * 0.72f + layerEnergy * 0.34f * waveCarrier + beat * (layer == 0 ? 0.22f : 0.10f), 0.08f, 1.05f);

                vis.Scale = new Vector3(1f, amp, 1f);
                vis.Opacity = Math.Clamp(0.50f + amp * 0.45f, 0.38f, 0.97f);
            }
        }
        else
        {
            float fdt = (float)dt;

            for (int i = 0; i < _specBarVisuals.Count && i < _specPeakVisuals.Count; i++)
            {
                float tDist = MathF.Abs(i - 23.5f) / 23.5f;
                float pos = tDist * 28.0f;
                int b0 = Math.Clamp((int)pos, 0, Math.Max(0, bands.Length - 1));
                int b1 = Math.Clamp(b0 + 1, 0, Math.Max(0, bands.Length - 1));
                float frac = pos - b0;
                float val = bands.Length > 0 ? bands[b0] * (1f - frac) + bands[b1] * frac : overall;
                val = Math.Clamp(val + beat * (1f - tDist * 0.65f) * 0.16f, 0f, 1f);

                float barScaleY = Math.Clamp(0.035f + val * 0.965f, 0.035f, 1.0f);
                var (_, barVis, maxBarH) = _specBarVisuals[i];
                barVis.Scale = new Vector3(1f, barScaleY, 1f);
                barVis.Opacity = Math.Clamp(0.62f + val * 0.36f, 0.50f, 0.99f);

                if (val >= _specPeakHeights[i])
                {
                    _specPeakHeights[i] = val;
                    _specPeakHold[i] = 0.16f;
                }
                else
                {
                    if (_specPeakHold[i] > 0f)
                    {
                        _specPeakHold[i] -= fdt;
                    }
                    else
                    {
                        _specPeakHeights[i] = MathF.Max(0.02f, _specPeakHeights[i] - 0.52f * fdt);
                    }
                }

                float peakOffsetY = -(float)(_specPeakHeights[i] * maxBarH);
                var peakVis = _specPeakVisuals[i].Visual;
                peakVis.Properties.InsertVector3("Translation", new Vector3(0f, peakOffsetY, 0f));
                peakVis.Opacity = Math.Clamp(0.28f + _specPeakHeights[i] * 0.72f, 0.22f, 0.98f);
            }
        }
    }

    private void SyncPlayBarLiquidGlassButtonAnimations(bool enable)
    {
        if (BottomPlayBarBorder?.Child == null)
        {
            return;
        }

        var queue = new global::System.Collections.Generic.Queue<DependencyObject>();
        queue.Enqueue(BottomPlayBarBorder.Child);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (cur is global::Microsoft.UI.Xaml.Controls.Primitives.ButtonBase btn)
            {
                if (enable)
                {
                    GlassAnimationController.AttachInteractions(btn, LiquidGlassMaterialKind.Button);
                }
                else
                {
                    GlassAnimationController.DetachInteractions(btn);
                }
            }

            int count = VisualTreeHelper.GetChildrenCount(cur);
            for (int i = 0; i < count; i++)
            {
                queue.Enqueue(VisualTreeHelper.GetChild(cur, i));
            }
        }
    }

    private readonly global::System.Collections.Generic.Dictionary<FrameworkElement, Thickness> _scrollContentBottomClearanceSaved = new();

    private void SyncScrollViewerPassUnderBottomPadding(bool enablePassUnderPlayBar)
    {
        if (MainContentWorkspaceGrid == null)
        {
            return;
        }

        if (!enablePassUnderPlayBar)
        {
            foreach (var (contentEl, origMargin) in _scrollContentBottomClearanceSaved)
            {
                try
                {
                    contentEl.Margin = origMargin;
                }
                catch
                {
                }
            }
            _scrollContentBottomClearanceSaved.Clear();
            return;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(MainContentWorkspaceGrid);
        for (int i = 0; i < childCount; i++)
        {
            if (VisualTreeHelper.GetChild(MainContentWorkspaceGrid, i) is ScrollViewer sv &&
                sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled &&
                sv.Content is FrameworkElement contentEl)
            {
                if (!_scrollContentBottomClearanceSaved.ContainsKey(contentEl))
                {
                    _scrollContentBottomClearanceSaved[contentEl] = contentEl.Margin;
                }
                var baseMargin = _scrollContentBottomClearanceSaved[contentEl];
                contentEl.Margin = new Thickness(
                    baseMargin.Left,
                    baseMargin.Top,
                    baseMargin.Right,
                    Math.Max(baseMargin.Bottom, 98));
            }
        }
    }
}
