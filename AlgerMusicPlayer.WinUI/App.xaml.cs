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

namespace AlgerMusicPlayer.WinUI;

/// <summary>
/// WinUI 3 应用入口与依赖注入容器配置
/// </summary>
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private Window? _mainWindow;

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // 核心服务单例注册
        services.AddSingleton<ConfigService>();
        services.AddSingleton<I18nService>();
        services.AddSingleton<NeteaseApiClient>();
        services.AddSingleton<QqMusicService>();
        services.AddSingleton<UnblockMusicService>();
        services.AddSingleton<BilibiliService>();
        services.AddSingleton<LyricService>();
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
