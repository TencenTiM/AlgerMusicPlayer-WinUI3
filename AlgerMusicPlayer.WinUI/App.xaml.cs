using AlgerMusicPlayer.WinUI.Helpers;
using AlgerMusicPlayer.WinUI.Services.Api;
using AlgerMusicPlayer.WinUI.Services.Audio;
using AlgerMusicPlayer.WinUI.Services.Download;
using AlgerMusicPlayer.WinUI.Services.Local;
using AlgerMusicPlayer.WinUI.Services.Lyric;
using AlgerMusicPlayer.WinUI.Services.System;
using AlgerMusicPlayer.WinUI.Services.Unblock;
using AlgerMusicPlayer.WinUI.ViewModels;
using AlgerMusicPlayer.WinUI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace AlgerMusicPlayer.WinUI;

/// <summary>
/// WinUI 3 应用入口与依赖注入容器配置
/// </summary>
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static string WebView2DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AlgerMusicPlayer.WinUI",
        "WebView2");

    private static CoreWebView2Environment? _sharedWebViewEnv;
    private static readonly SemaphoreSlim _webViewEnvLock = new(1, 1);
    private Window? _mainWindow;

    public App()
    {
        try
        {
            Environment.CurrentDirectory = AppContext.BaseDirectory;
            Directory.CreateDirectory(WebView2DataDirectory);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", WebView2DataDirectory);
        }
        catch
        {
        }

        SingleInstanceGuard.AcquireOrRedirectAndExit();
        InitializeComponent();
        Services = ConfigureServices();
    }

    /// <summary>
    /// 使用位于用户 AppData 下的共享环境初始化 WebView2，避免在 Program Files 下因无写权限报错
    /// </summary>
    public static async Task EnsureWebView2Async(WebView2 view)
    {
        if (view.CoreWebView2 != null) return;

        await _webViewEnvLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(WebView2DataDirectory);
            _sharedWebViewEnv ??= await CoreWebView2Environment.CreateWithOptionsAsync(
                string.Empty,
                WebView2DataDirectory,
                new CoreWebView2EnvironmentOptions());
        }
        finally
        {
            _webViewEnvLock.Release();
        }

        await view.EnsureCoreWebView2Async(_sharedWebViewEnv);
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // 核心服务单例注册
        services.AddSingleton<ConfigService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<I18nService>();
        services.AddSingleton<NeteaseApiClient>();
        services.AddSingleton<QqMusicService>();
        services.AddSingleton<LxScriptHostService>();
        services.AddSingleton<UnblockMusicService>();
        services.AddSingleton<BilibiliService>();
        services.AddSingleton<LyricService>();
        services.AddSingleton<MultichannelManagerService>();
        services.AddSingleton<AudioPlayerService>();
        services.AddSingleton<DownloadManagerService>();
        services.AddSingleton<LocalMusicScannerService>();
        services.AddSingleton<RemoteControlService>();
        services.AddSingleton<GlobalHotkeyService>();

        // 视图模型与主窗口
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = Services.GetRequiredService<MainWindow>();
        _mainWindow.Activate();
    }
}
