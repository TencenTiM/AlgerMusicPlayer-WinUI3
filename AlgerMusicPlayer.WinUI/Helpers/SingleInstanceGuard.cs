namespace AlgerMusicPlayer.WinUI.Helpers;

/// <summary>
/// 进程全局唯一单实例保护与二次启动窗口唤醒守卫
/// </summary>
public static class SingleInstanceGuard
{
    private const string SingleInstanceMutexName = @"Local\AlgerMusicPlayer.WinUI.SingleInstance.Mutex";
    private const string ActivateInstanceEventName = @"Local\AlgerMusicPlayer.WinUI.ActivateInstance.Event";

    private static Mutex? _instanceMutex;
    private static bool _ownsMutex;
    private static EventWaitHandle? _activateEvent;
    private static RegisteredWaitHandle? _registeredWaitHandle;

    public static event Action? ExternalActivationRequested;

    /// <summary>
    /// 尝试获取单实例锁；若已有实例运行，则唤醒已有窗口并立即退出当前进程
    /// </summary>
    public static void AcquireOrRedirectAndExit()
    {
        var args = Environment.GetCommandLineArgs();
        bool isRestarting = args.Any(a => string.Equals(a, "--restarting", StringComparison.OrdinalIgnoreCase));

        _instanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName);
        try
        {
            _ownsMutex = _instanceMutex.WaitOne(isRestarting ? TimeSpan.FromSeconds(5) : TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        if (!_ownsMutex)
        {
            SignalExistingInstanceToForeground();
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Environment.Exit(0);
            return;
        }

        StartListeningForExternalActivation();
    }

    private static void SignalExistingInstanceToForeground()
    {
        try
        {
            _ = Win32Interop.AllowSetForegroundWindow(Win32Interop.ASFW_ANY);
            using var evt = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateInstanceEventName);
            _ = evt.Set();
        }
        catch
        {
        }
    }

    private static void StartListeningForExternalActivation()
    {
        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateInstanceEventName);
            _registeredWaitHandle = ThreadPool.RegisterWaitForSingleObject(
                _activateEvent,
                static (_, timedOut) =>
                {
                    if (!timedOut)
                    {
                        ExternalActivationRequested?.Invoke();
                    }
                },
                state: null,
                millisecondsTimeOutInterval: -1,
                executeOnlyOnce: false);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 释放单实例互斥锁与唤醒事件句柄（供退出或应用内重启调用）
    /// </summary>
    public static void Release()
    {
        try
        {
            _registeredWaitHandle?.Unregister(null);
            _registeredWaitHandle = null;
        }
        catch
        {
        }

        try
        {
            _activateEvent?.Dispose();
            _activateEvent = null;
        }
        catch
        {
        }

        if (_instanceMutex != null)
        {
            try
            {
                if (_ownsMutex)
                {
                    _instanceMutex.ReleaseMutex();
                    _ownsMutex = false;
                }
            }
            catch
            {
            }

            try
            {
                _instanceMutex.Dispose();
            }
            catch
            {
            }

            _instanceMutex = null;
        }
    }
}
