using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace AlgerMusicPlayer.Installer;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!IsAdministrator())
        {
            try
            {
                var exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
                var args = Environment.GetCommandLineArgs().Skip(1);
                var argString = string.Join(" ", args.Select(a => $"\"{a}\""));

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = argString,
                    UseShellExecute = true,
                    Verb = "runas"
                };

                Process.Start(startInfo);
                Shutdown();
                return;
            }
            catch
            {
                // 用户拒绝 UAC 提权或系统不支持时，继续尝试运行
            }
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
