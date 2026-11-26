using System.Text.Json;
using Com.AndroidSync.App.Models;
using Microsoft.Extensions.Logging;

namespace Com.AndroidSync.App.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger<SettingsService> _logger;
    private readonly string _settingsFilePath;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(appData, "AndroidSync");
        _settingsFilePath = Path.Combine(folder, "settings.json");
    }

    public AppSettings Current { get; private set; } = new();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                Current = new AppSettings();
                return;
            }

            await using var stream = File.OpenRead(_settingsFilePath);
            var loaded = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            Current = loaded ?? new AppSettings();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "讀取設定檔失敗，將使用預設設定: {Path}", _settingsFilePath);
            Current = new AppSettings();
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = _settingsFilePath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, Current, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(tempPath, _settingsFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "儲存設定檔失敗: {Path}", _settingsFilePath);
        }
        finally
        {
            _syncLock.Release();
        }
    }
}
