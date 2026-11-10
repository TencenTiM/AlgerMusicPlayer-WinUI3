using AlgerMusicPlayer.WinUI.Helpers;
using AlgerMusicPlayer.WinUI.Models;

namespace AlgerMusicPlayer.WinUI.Services.System;

/// <summary>
/// Win32 全局快捷键注册与监听服务（对应原 modules/shortcuts.ts）
/// </summary>
public class GlobalHotkeyService : IDisposable
{
    private readonly ConfigService _configService;
    private IntPtr _hWnd;
    private Win32Interop.SubclassProc? _subclassDelegate;
    private readonly Dictionary<int, string> _registeredHotkeys = new();
    private const nuint SubclassId = 9001;

    public event Action<string>? HotkeyTriggered;

    public bool IsSuspended { get; set; }

    public GlobalHotkeyService(ConfigService configService)
    {
        _configService = configService;
    }

    public void Initialize(IntPtr hWnd)
    {
        _hWnd = hWnd;
        _subclassDelegate = WndProcCallback;
        Win32Interop.SetWindowSubclass(_hWnd, _subclassDelegate, SubclassId, 0);
        ReloadHotkeys();
    }

    public List<(string ActionId, string DisplayName, string Key)> ReloadHotkeys()
    {
        var failures = new List<(string ActionId, string DisplayName, string Key)>();
        if (_hWnd == IntPtr.Zero) return failures;

        foreach (var id in _registeredHotkeys.Keys)
        {
            Win32Interop.UnregisterHotKey(_hWnd, id);
        }
        _registeredHotkeys.Clear();

        _configService.Settings.Shortcuts = AppSettings.NormalizeShortcuts(_configService.Settings.Shortcuts);

        int nextId = 100;
        foreach (var item in _configService.Settings.Shortcuts)
        {
            if (!item.Enabled || !string.Equals(item.Scope, "global", StringComparison.OrdinalIgnoreCase)) continue;
            var keyGesture = item.Key;
            if (string.IsNullOrWhiteSpace(keyGesture)) continue;

            if (TryParseGesture(keyGesture, out uint modifiers, out uint vk))
            {
                int id = nextId++;
                if (Win32Interop.RegisterHotKey(_hWnd, id, modifiers | Win32Interop.MOD_NOREPEAT, vk))
                {
                    _registeredHotkeys[id] = item.ActionId;
                }
                else
                {
                    failures.Add((item.ActionId, item.DisplayName, item.FormattedKey));
                }
            }
            else
            {
                failures.Add((item.ActionId, item.DisplayName, item.FormattedKey));
            }
        }
        return failures;
    }

    private IntPtr WndProcCallback(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == Win32Interop.WM_HOTKEY && !IsSuspended)
        {
            int hotkeyId = wParam.ToInt32();
            if (_registeredHotkeys.TryGetValue(hotkeyId, out var actionId))
            {
                HotkeyTriggered?.Invoke(actionId);
                return IntPtr.Zero;
            }
        }
        return Win32Interop.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public static bool TryParseGesture(string gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control" or "commandorcontrol" or "cmdorctrl":
                    modifiers |= Win32Interop.MOD_CONTROL;
                    break;
                case "alt" or "option":
                    modifiers |= Win32Interop.MOD_ALT;
                    break;
                case "shift":
                    modifiers |= Win32Interop.MOD_SHIFT;
                    break;
                case "win" or "meta" or "super" or "windows":
                    modifiers |= Win32Interop.MOD_WIN;
                    break;
            }
        }

        var keyToken = parts[^1].ToUpperInvariant();
        if (keyToken.Length == 1 && char.IsLetterOrDigit(keyToken[0]))
        {
            vk = keyToken[0];
            return true;
        }

        if (keyToken.StartsWith('F') && int.TryParse(keyToken[1..], out int fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }

        vk = keyToken switch
        {
            "SPACE" => 0x20,
            "LEFT" or "ARROWLEFT" => 0x25,
            "UP" or "ARROWUP" => 0x26,
            "RIGHT" or "ARROWRIGHT" => 0x27,
            "DOWN" or "ARROWDOWN" => 0x28,
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            "ESCAPE" or "ESC" => 0x1B,
            "BACKSPACE" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "INSERT" or "INS" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "PLUS" or "=" => 0xBB,
            "MINUS" or "-" => 0xBD,
            "MEDIAPLAYPAUSE" => 0xB3,
            "MEDIANEXTTRACK" => 0xB0,
            "MEDIAPREVIOUSTRACK" => 0xB1,
            "MEDIASTOP" => 0xB2,
            _ => 0
        };
        return vk != 0;
    }

    public void Dispose()
    {
        if (_hWnd != IntPtr.Zero)
        {
            foreach (var id in _registeredHotkeys.Keys)
            {
                Win32Interop.UnregisterHotKey(_hWnd, id);
            }
            _registeredHotkeys.Clear();

            if (_subclassDelegate != null)
            {
                Win32Interop.RemoveWindowSubclass(_hWnd, _subclassDelegate, SubclassId);
            }
        }
        GC.SuppressFinalize(this);
    }
}
