using System.Text.Json;
using TaskSchedulerPlus.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace TaskSchedulerPlus.Web.Services;

// 初期設定の保存先をファイルで管理する実装です。
// DB 接続文字列は DataProtection で保護し、参照用の rds.json は Parameter フォルダへ同期します。
public sealed class FileSetupConfigurationStore : ISetupConfigurationStore
{
    private const string ProtectionPurpose = "TaskSchedulerPlus.DatabaseConnection.v1";
    private readonly string _settingsPath;
    private readonly ParameterFileStore _parameterFileStore;
    private readonly IDataProtector _protector;
    private readonly object _sync = new();
    private SetupConfiguration? _current;

    public FileSetupConfigurationStore(
        IHostEnvironment environment,
        IConfiguration configuration,
        ParameterFileStore parameterFileStore,
        IDataProtectionProvider protectionProvider)
    {
        _settingsPath = Path.Combine(ResolveAppDataPath(environment, configuration), "setup.json");
        _parameterFileStore = parameterFileStore;
        _protector = protectionProvider.CreateProtector(ProtectionPurpose);
        _current = ReadConfiguration() ?? RecoverConfigurationFromRds();

        if (_current is not null)
        {
            // Web 画面から参照できるよう、保護済み接続文字列を復号して rds.json へ同期します。
            _parameterFileStore.SaveRds(_current, _protector.Unprotect(_current.ProtectedConnectionString));
        }
    }

    public bool IsConfigured => Current is not null;

    public SetupConfiguration? Current
    {
        get
        {
            lock (_sync)
            {
                _current ??= ReadConfiguration();
                _current ??= RecoverConfigurationFromRds();
                return _current;
            }
        }
    }

    public static string ResolveAppDataPath(IHostEnvironment environment, IConfiguration configuration)
    {
        var configuredPath = configuration["Setup:AppDataPath"];
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            // 未指定時はプロジェクト配下の App_Data を使用します。
            return Path.Combine(environment.ContentRootPath, "App_Data");
        }

        return Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath));
    }

    public string GetRequiredConnectionString()
    {
        var configuration = Current
            ?? throw new InvalidOperationException("初期設定が完了していません。");

        // setup.json には保護済み文字列を保存しているため、利用時に復号します。
        return _protector.Unprotect(configuration.ProtectedConnectionString);
    }

    public async Task SaveAsync(
        SetupConfiguration configuration,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        // 接続文字列は setup.json に平文保存しないよう暗号化します。
        configuration.ProtectedConnectionString = _protector.Protect(connectionString);

        // 一時ファイルへ書き出してから置き換え、書き込み途中の破損を避けます。
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_settingsPath}.tmp";
        var json = JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
        File.Move(temporaryPath, _settingsPath, overwrite: true);
        await _parameterFileStore.SaveRdsAsync(configuration, connectionString, cancellationToken);

        lock (_sync)
        {
            _current = configuration;
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_settingsPath))
        {
            File.Delete(_settingsPath);
        }

        // 初期設定を消す場合は、参照用の rds.json も合わせて削除します。
        _parameterFileStore.DeleteRds();

        lock (_sync)
        {
            _current = null;
        }

        return Task.CompletedTask;
    }

    private SetupConfiguration? ReadConfiguration()
    {
        if (!File.Exists(_settingsPath))
        {
            return null;
        }

        try
        {
            // 復号まで成功した場合だけ、有効な設定として扱います。
            var json = File.ReadAllText(_settingsPath);
            var configuration = JsonSerializer.Deserialize<SetupConfiguration>(json);
            if (configuration is null || string.IsNullOrWhiteSpace(configuration.ProtectedConnectionString))
            {
                return null;
            }

            _protector.Unprotect(configuration.ProtectedConnectionString);
            return configuration;
        }
        catch
        {
            // 設定ファイル破損や復号失敗時は未設定扱いにし、初期設定画面へ誘導します。
            return null;
        }
    }

    private SetupConfiguration? RecoverConfigurationFromRds()
    {
        var rds = _parameterFileStore.ReadRdsParameter();
        if (string.IsNullOrWhiteSpace(rds.ConnectionString))
        {
            return null;
        }

        try
        {
            var builder = new SqlConnectionStringBuilder(rds.ConnectionString);
            var configuration = new SetupConfiguration
            {
                ApplicationName = "タスクスケジューラープラス",
                ServerName = builder.DataSource,
                DatabaseName = builder.InitialCatalog,
                AuthenticationMode = builder.IntegratedSecurity
                    ? DatabaseAuthenticationMode.Windows
                    : DatabaseAuthenticationMode.SqlServer,
                TimeZoneId = TimeZoneInfo.Local.Id,
                ConfiguredAt = DateTimeOffset.Now,
                ProtectedConnectionString = _protector.Protect(rds.ConnectionString)
            };

            WriteConfiguration(configuration);
            return configuration;
        }
        catch
        {
            // rds.json からの復旧にも失敗した場合は未設定扱いにします。
            return null;
        }
    }

    private void WriteConfiguration(SetupConfiguration configuration)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_settingsPath}.tmp";
        var json = JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }
}
