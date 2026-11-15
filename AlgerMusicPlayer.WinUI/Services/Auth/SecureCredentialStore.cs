using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.System;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage.Streams;

namespace AlgerMusicPlayer.WinUI.Services.Auth;

/// <summary>
/// Windows DPAPI 当前用户加密凭证存储服务（accounts.dat + 内存热缓存）
/// </summary>
public sealed class SecureCredentialStore
{
    private readonly string _storeFilePath;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private readonly ConcurrentDictionary<AuthMusicPlatform, AccountSession> _cache = new();
    private bool _initialized;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public SecureCredentialStore(ConfigService config)
    {
        string dir = Path.Combine(config.AppDataDirectory, "Credentials");
        Directory.CreateDirectory(dir);
        _storeFilePath = Path.Combine(dir, "accounts.dat");
    }

    public string StoreFilePath => _storeFilePath;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        await _ioLock.WaitAsync();
        try
        {
            if (_initialized) return;
            var all = await LoadAllInternalAsync();
            _cache.Clear();
            foreach (var kv in all)
            {
                _cache[kv.Key] = kv.Value;
            }
            _initialized = true;
        }
        catch
        {
            _initialized = true;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public AccountSession? TryGetCachedSession(AuthMusicPlatform platform)
    {
        return _cache.TryGetValue(platform, out var session) ? session : null;
    }

    public IReadOnlyDictionary<AuthMusicPlatform, AccountSession> GetCachedSnapshot() => _cache;

    public async Task SaveSessionAsync(AccountSession session)
    {
        await _ioLock.WaitAsync();
        try
        {
            var all = await LoadAllInternalAsync();
            session.UpdatedAt = DateTimeOffset.UtcNow;
            all[session.Platform] = session;
            await SaveAllInternalAsync(all);
            _cache[session.Platform] = session;
            _initialized = true;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<AccountSession?> GetSessionAsync(AuthMusicPlatform platform)
    {
        if (!_initialized)
        {
            await InitializeAsync();
        }

        if (_cache.TryGetValue(platform, out var cached))
        {
            return cached;
        }

        await _ioLock.WaitAsync();
        try
        {
            var all = await LoadAllInternalAsync();
            return all.TryGetValue(platform, out var session) ? session : null;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<Dictionary<AuthMusicPlatform, AccountSession>> GetAllSessionsAsync()
    {
        if (!_initialized)
        {
            await InitializeAsync();
        }

        await _ioLock.WaitAsync();
        try
        {
            var all = await LoadAllInternalAsync();
            _cache.Clear();
            foreach (var kv in all)
            {
                _cache[kv.Key] = kv.Value;
            }
            return all;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task RemoveSessionAsync(AuthMusicPlatform platform)
    {
        await _ioLock.WaitAsync();
        try
        {
            _cache.TryRemove(platform, out _);
            var all = await LoadAllInternalAsync();
            if (all.Remove(platform))
            {
                await SaveAllInternalAsync(all);
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task ClearAllAsync()
    {
        await _ioLock.WaitAsync();
        try
        {
            _cache.Clear();
            if (File.Exists(_storeFilePath))
            {
                File.Delete(_storeFilePath);
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task<Dictionary<AuthMusicPlatform, AccountSession>> LoadAllInternalAsync()
    {
        if (!File.Exists(_storeFilePath))
        {
            return new Dictionary<AuthMusicPlatform, AccountSession>();
        }

        try
        {
            byte[] encryptedBytes = await File.ReadAllBytesAsync(_storeFilePath);
            if (encryptedBytes.Length == 0)
            {
                return new Dictionary<AuthMusicPlatform, AccountSession>();
            }

            DataProtectionProvider provider = new();
            IBuffer unprotectedBuffer = await provider.UnprotectAsync(encryptedBytes.AsBuffer());
            byte[] plainBytes = unprotectedBuffer.ToArray();
            string json = Encoding.UTF8.GetString(plainBytes);

            return JsonSerializer.Deserialize<Dictionary<AuthMusicPlatform, AccountSession>>(json, JsonOptions)
                   ?? new Dictionary<AuthMusicPlatform, AccountSession>();
        }
        catch
        {
            return new Dictionary<AuthMusicPlatform, AccountSession>();
        }
    }

    private async Task SaveAllInternalAsync(Dictionary<AuthMusicPlatform, AccountSession> sessions)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storeFilePath)!);
        string json = JsonSerializer.Serialize(sessions, JsonOptions);
        byte[] plainBytes = Encoding.UTF8.GetBytes(json);

        DataProtectionProvider provider = new("LOCAL=user");
        IBuffer protectedBuffer = await provider.ProtectAsync(plainBytes.AsBuffer());
        byte[] encryptedBytes = protectedBuffer.ToArray();

        await File.WriteAllBytesAsync(_storeFilePath, encryptedBytes);
    }
}
