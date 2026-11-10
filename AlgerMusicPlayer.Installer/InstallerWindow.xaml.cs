using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace AlgerMusicPlayer.Installer;

/// <summary>
/// AlgerMusicPlayer 自研安装与卸载器主窗口（支持中文路径、进度动画、桌面/开始菜单快捷方式及系统卸载注册）
/// </summary>
public partial class InstallerWindow : Window
{
    private const string AppId = "AlgerMusicPlayer";
    private const string AppDisplayName = "Alger Music Player";
    private const string AppVersion = "5.1.0";
    private const string MainExeName = "AlgerMusicPlayer.WinUI.exe";
    private const string UninstallExeName = "Uninstall.exe";
    private const string UninstallRegKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AlgerMusicPlayer";

    private bool _isBusy;
    private bool _isUninstallMode;
    private bool _uninstalledDone;
    private string _installedTargetExe = string.Empty;

    public InstallerWindow()
    {
        InitializeComponent();

        var defaultDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "AlgerMusicPlayer");

        // 若之前已安装过，优先读取上次安装的目录方便覆盖升级
        try
        {
            using var regKey = Registry.CurrentUser.OpenSubKey(UninstallRegKeyPath, false);
            var previousDir = regKey?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrWhiteSpace(previousDir))
            {
                defaultDir = previousDir;
            }
        }
        catch
        {
        }

        InstallPathTextBox.Text = defaultDir;

        var args = Environment.GetCommandLineArgs();
        var currentExeName = Path.GetFileName(Environment.ProcessPath ?? string.Empty);
        if (args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(currentExeName, UninstallExeName, StringComparison.OrdinalIgnoreCase))
        {
            EnterUninstallMode();
        }
    }

    private void EnterUninstallMode()
    {
        _isUninstallMode = true;
        Title = "AlgerMusicPlayer 卸载向导";
        WindowHeaderTitleText.Text = "AlgerMusicPlayer 卸载向导";
        SetupConfigPage.Visibility = Visibility.Collapsed;
        UninstallConfirmPage.Visibility = Visibility.Visible;
        FooterStatusText.Text = "卸载向导将清理程序文件、桌面与开始菜单快捷方式及注册表项";
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnMinimizeWindowClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnCloseWindowClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        Close();
    }

    private void OnBrowseInstallPathClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择 Alger Music Player 安装目录（支持中文路径）",
            UseDescriptionForTitle = true,
            SelectedPath = InstallPathTextBox.Text
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK &&
            !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            var chosen = dialog.SelectedPath.Trim();
            if (!chosen.EndsWith("AlgerMusicPlayer", StringComparison.OrdinalIgnoreCase))
            {
                chosen = Path.Combine(chosen, "AlgerMusicPlayer");
            }
            InstallPathTextBox.Text = chosen;
        }
    }

    private async void OnStartInstallClick(object sender, RoutedEventArgs e)
    {
        var targetDir = InstallPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            System.Windows.MessageBox.Show(this, "请选择有效的安装路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool createDesktop = DesktopShortcutCheck.IsChecked == true;
        bool createStartMenu = StartMenuShortcutCheck.IsChecked == true;

        _isBusy = true;
        CloseWindowBtn.IsEnabled = false;
        SetupConfigPage.Visibility = Visibility.Collapsed;
        SetupProgressPage.Visibility = Visibility.Visible;

        try
        {
            await RunInstallCoreAsync(targetDir, createDesktop, createStartMenu);

            _installedTargetExe = Path.Combine(targetDir, MainExeName);
            SetupProgressPage.Visibility = Visibility.Collapsed;
            SetupCompletePage.Visibility = Visibility.Visible;
            FooterStatusText.Text = $"已成功安装至：{targetDir}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                $"安装过程中发生错误：\n{ex.Message}",
                "安装失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            SetupProgressPage.Visibility = Visibility.Collapsed;
            SetupConfigPage.Visibility = Visibility.Visible;
        }
        finally
        {
            _isBusy = false;
            CloseWindowBtn.IsEnabled = true;
        }
    }

    private async Task RunInstallCoreAsync(string targetDir, bool createDesktop, bool createStartMenu)
    {
        UpdateProgressUi(3, "正在检查并关闭运行中的 AlgerMusicPlayer 进程...");
        await Task.Run(StopRunningAppProcesses);

        UpdateProgressUi(8, $"正在创建安装目录：{targetDir}");
        Directory.CreateDirectory(targetDir);

        using var payloadStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AlgerMusicPlayer.Payload.zip");
        if (payloadStream == null)
        {
            throw new InvalidOperationException("安装包未包含内嵌的应用资源包 (AlgerMusicPlayer.Payload.zip)，请使用 build-installer.ps1 构建。");
        }

        using var archive = new ZipArchive(payloadStream, ZipArchiveMode.Read);
        var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        int total = Math.Max(1, entries.Count);
        int current = 0;

        foreach (var entry in entries)
        {
            current++;
            var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var destPath = Path.Combine(targetDir, relativePath);
            var destFolder = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destFolder))
            {
                Directory.CreateDirectory(destFolder);
            }

            await Task.Run(() => ExtractEntrySafe(entry, destPath));

            int pct = 10 + (int)Math.Round(current * 80.0 / total);
            if (current % 4 == 0 || current == total)
            {
                UpdateProgressUi(pct, $"正在释放文件 ({current}/{total})：{entry.Name}");
                await Task.Delay(1);
            }
        }

        UpdateProgressUi(93, "正在生成卸载程序与系统注册表信息...");
        var currentInstallerExe = Environment.ProcessPath;
        var uninstallPath = Path.Combine(targetDir, UninstallExeName);
        if (!string.IsNullOrWhiteSpace(currentInstallerExe) && File.Exists(currentInstallerExe))
        {
            try
            {
                File.Copy(currentInstallerExe, uninstallPath, overwrite: true);
            }
            catch
            {
            }
        }

        var mainExePath = Path.Combine(targetDir, MainExeName);
        var iconPath = Path.Combine(targetDir, "Assets", "icon.ico");
        if (!File.Exists(iconPath))
        {
            iconPath = mainExePath;
        }

        RegisterUninstallerInRegistry(targetDir, mainExePath, uninstallPath, iconPath);

        UpdateProgressUi(97, "正在创建快捷方式...");
        if (createDesktop)
        {
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            CreateShortcutSafe(Path.Combine(desktopDir, $"{AppDisplayName}.lnk"), mainExePath, targetDir, iconPath, "Alger Music Player 音乐播放器");
        }

        if (createStartMenu)
        {
            var startMenuDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                AppDisplayName);
            Directory.CreateDirectory(startMenuDir);
            CreateShortcutSafe(Path.Combine(startMenuDir, $"{AppDisplayName}.lnk"), mainExePath, targetDir, iconPath, "Alger Music Player 音乐播放器");
            CreateShortcutSafe(Path.Combine(startMenuDir, $"卸载 {AppDisplayName}.lnk"), uninstallPath, targetDir, iconPath, "卸载 Alger Music Player", "--uninstall");
        }

        UpdateProgressUi(100, "安装完成！");
        await Task.Delay(180);
    }

    private async void OnConfirmUninstallClick(object sender, RoutedEventArgs e)
    {
        bool removeUserData = RemoveUserDataCheck.IsChecked == true;
        _isBusy = true;
        CloseWindowBtn.IsEnabled = false;
        UninstallConfirmPage.Visibility = Visibility.Collapsed;
        SetupProgressPage.Visibility = Visibility.Visible;
        ProgressTitleText.Text = "正在卸载 Alger Music Player...";

        try
        {
            await RunUninstallCoreAsync(removeUserData);
            _uninstalledDone = true;
            SetupProgressPage.Visibility = Visibility.Collapsed;
            SetupCompletePage.Visibility = Visibility.Visible;
            CompleteTitleText.Text = "Alger Music Player 已成功卸载";
            CompleteSubtitleText.Text = "感谢您的使用，所有程序快捷方式与注册项已清理完毕";
            FinishActionBtn.Content = "完成并退出";
            FooterStatusText.Text = "卸载完成";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"卸载过程中出现异常：\n{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
        finally
        {
            _isBusy = false;
            CloseWindowBtn.IsEnabled = true;
        }
    }

    private async Task RunUninstallCoreAsync(bool removeUserData)
    {
        UpdateProgressUi(15, "正在停止运行中的 AlgerMusicPlayer 进程...");
        await Task.Run(StopRunningAppProcesses);

        string installDir = string.Empty;
        try
        {
            using var regKey = Registry.CurrentUser.OpenSubKey(UninstallRegKeyPath, false);
            installDir = regKey?.GetValue("InstallLocation") as string ?? string.Empty;
        }
        catch
        {
        }

        if (string.IsNullOrWhiteSpace(installDir))
        {
            var selfDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrWhiteSpace(selfDir) && File.Exists(Path.Combine(selfDir, MainExeName)))
            {
                installDir = selfDir;
            }
        }

        UpdateProgressUi(45, "正在清理桌面与开始菜单快捷方式...");
        try
        {
            var desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppDisplayName}.lnk");
            if (File.Exists(desktopShortcut)) File.Delete(desktopShortcut);

            var startMenuFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppDisplayName);
            if (Directory.Exists(startMenuFolder)) Directory.Delete(startMenuFolder, recursive: true);
        }
        catch
        {
        }

        UpdateProgressUi(70, "正在移除程序文件...");
        if (!string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir))
        {
            var currentExe = Environment.ProcessPath ?? string.Empty;
            await Task.Run(() =>
            {
                foreach (var file in Directory.GetFiles(installDir, "*", SearchOption.AllDirectories))
                {
                    if (string.Equals(file, currentExe, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(file); } catch { }
                }
            });

            // 退出后通过 cmd 延迟清理残留的空目录与自身 Uninstall.exe
            ScheduleSelfDeleteFolder(installDir);
        }

        if (removeUserData)
        {
            UpdateProgressUi(88, "正在清理用户配置与缓存数据...");
            var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlgerMusicPlayer.WinUI");
            if (Directory.Exists(appDataDir))
            {
                try { Directory.Delete(appDataDir, recursive: true); } catch { }
            }
        }

        UpdateProgressUi(95, "正在清理系统卸载注册表项...");
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegKeyPath, throwOnMissingSubKey: false);
        }
        catch
        {
        }

        UpdateProgressUi(100, "卸载已完成！");
        await Task.Delay(150);
    }

    private void OnFinishButtonClick(object sender, RoutedEventArgs e)
    {
        if (!_isUninstallMode && !_uninstalledDone && AutoLaunchCheck.IsChecked == true)
        {
            if (!string.IsNullOrWhiteSpace(_installedTargetExe) && File.Exists(_installedTargetExe))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _installedTargetExe,
                        WorkingDirectory = Path.GetDirectoryName(_installedTargetExe) ?? string.Empty,
                        UseShellExecute = true
                    });
                }
                catch
                {
                }
            }
        }

        Close();
    }

    private void UpdateProgressUi(int percent, string detail)
    {
        percent = Math.Clamp(percent, 0, 100);
        ProgressPercentText.Text = $"{percent}%";
        ProgressFillBar.Width = 480.0 * percent / 100.0;
        ProgressDetailText.Text = detail;
    }

    /// <summary>
    /// 安全解压单个文件条目（使用 FileShare.ReadWrite 规避 Windows Defender / 杀毒软件对 117MB mpv.exe 实时扫描时的句柄冲突，并自带重试与旧文件重命名兜底）
    /// </summary>
    private static void ExtractEntrySafe(ZipArchiveEntry entry, string destPath)
    {
        const int maxRetries = 6;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (File.Exists(destPath))
                {
                    try
                    {
                        File.SetAttributes(destPath, FileAttributes.Normal);
                    }
                    catch
                    {
                    }
                }

                using (var input = entry.Open())
                using (var output = new FileStream(
                    destPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 131072,
                    useAsync: false))
                {
                    input.CopyTo(output, 131072);
                    output.Flush();
                }

                // 尝试还原时间戳，若此时杀毒软件正在扫描该 exe 则忽略时间戳异常（不影响文件内容完整性）
                try
                {
                    File.SetLastWriteTime(destPath, entry.LastWriteTime.DateTime);
                }
                catch
                {
                }

                return;
            }
            catch (IOException) when (attempt < maxRetries)
            {
                // 若目标目录下存在被占用的旧版文件，尝试将其重命名为 .old 后再写入新文件
                try
                {
                    if (File.Exists(destPath))
                    {
                        var oldBackup = destPath + $".old.{Environment.TickCount64}";
                        File.Move(destPath, oldBackup, overwrite: true);
                        try { File.Delete(oldBackup); } catch { }
                    }
                }
                catch
                {
                }

                Thread.Sleep(150 * attempt);
            }
        }
    }

    private static void StopRunningAppProcesses()
    {
        string[] processNames = ["AlgerMusicPlayer.WinUI", "AlgerMusicPlayer", "mpv"];
        foreach (var name in processNames)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(3000);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch
            {
            }
        }
    }

    private static void RegisterUninstallerInRegistry(string installDir, string mainExePath, string uninstallExePath, string iconPath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallRegKeyPath);
            if (key == null) return;

            key.SetValue("DisplayName", AppDisplayName);
            key.SetValue("DisplayVersion", AppVersion);
            key.SetValue("Publisher", "TencenTiM");
            key.SetValue("URLInfoAbout", "https://github.com/TencenTiM/AlgerMusicPlayer-WinUI3");
            key.SetValue("DisplayIcon", iconPath);
            key.SetValue("InstallLocation", installDir);
            key.SetValue("UninstallString", $"\"{uninstallExePath}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        catch
        {
        }
    }

    private static void CreateShortcutSafe(string shortcutPath, string targetPath, string workingDir, string iconPath, string description, string arguments = "")
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return;

            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDir;
            shortcut.IconLocation = $"{iconPath},0";
            shortcut.Description = description;
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                shortcut.Arguments = arguments;
            }
            shortcut.Save();
        }
        catch
        {
        }
    }

    private static void ScheduleSelfDeleteFolder(string targetDir)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/C ping 127.0.0.1 -n 2 > nul & rmdir /s /q \"{targetDir}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch
        {
        }
    }
}
