using System.Text.Encodings.Web;
using System.Text.Json;
using TaskSchedulerPlus.Web.Data;

namespace TaskSchedulerPlus.Web.Services;

// .sln と同じ階層の Parameter フォルダにある rds.json / mail.json を読み書きします。
public sealed class ParameterFileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    public ParameterFileStore(IHostEnvironment environment, IConfiguration configuration)
    {
        ParameterDirectoryPath = ResolveParameterPath(environment, configuration);
        RdsJsonPath = Path.Combine(ParameterDirectoryPath, "rds.json");
        MailJsonPath = Path.Combine(ParameterDirectoryPath, "mail.json");

        Directory.CreateDirectory(ParameterDirectoryPath);
        EnsureDefaultMailFile();
    }

    public string ParameterDirectoryPath { get; }
    public string RdsJsonPath { get; }
    public string MailJsonPath { get; }

    public static string ResolveParameterPath(IHostEnvironment environment, IConfiguration configuration)
    {
        var configuredPath = configuration["Setup:ParameterPath"];
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            // 未指定時は .sln と同じ階層の Parameter フォルダを使います。
            configuredPath = "Parameter";
        }

        if (Path.IsPathRooted(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        return Path.GetFullPath(Path.Combine(
            SolutionPathResolver.ResolveSolutionRoot(environment.ContentRootPath),
            configuredPath));
    }

    public ParameterFileSnapshot ReadRdsSnapshot() =>
        ReadSnapshot("SQL Server 接続文字列", "rds.json", RdsJsonPath);

    public ParameterFileSnapshot ReadMailSnapshot() =>
        ReadSnapshot("メール送信設定", "mail.json", MailJsonPath);

    public RdsParameterFile ReadRdsParameter() =>
        ReadParameter<RdsParameterFile>(RdsJsonPath) ?? new RdsParameterFile();

    public MailParameterFile ReadMailParameter()
    {
        // メール設定は未作成でも既定値ファイルを作ってから読み込みます。
        EnsureDefaultMailFile();
        return ReadParameter<MailParameterFile>(MailJsonPath) ?? new MailParameterFile();
    }

    public async Task SaveRdsAsync(
        SetupConfiguration configuration,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        // SetupConfiguration は互換性のため受け取っていますが、rds.json には接続文字列だけを保存します。
        _ = configuration;
        var parameter = CreateRdsParameter(connectionString);
        await WriteJsonAsync(RdsJsonPath, parameter, cancellationToken);
    }

    public void SaveRds(SetupConfiguration configuration, string connectionString)
    {
        _ = configuration;
        var parameter = CreateRdsParameter(connectionString);
        WriteJson(RdsJsonPath, parameter);
    }

    public void DeleteRds()
    {
        if (File.Exists(RdsJsonPath))
        {
            File.Delete(RdsJsonPath);
        }
    }

    private void EnsureDefaultMailFile()
    {
        if (File.Exists(MailJsonPath))
        {
            return;
        }

        // メール未使用でも画面から設定ファイルの存在を確認できるようにします。
        WriteJson(MailJsonPath, new MailParameterFile());
    }

    // 接続文字列から取得できる派生情報は保存せず、ConnectionString を正とします。
    private static RdsParameterFile CreateRdsParameter(string connectionString) => new()
    {
        Provider = "SqlServer",
        ConnectionString = connectionString
    };

    private static ParameterFileSnapshot ReadSnapshot(string displayName, string fileName, string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return new ParameterFileSnapshot(displayName, fileName, fullPath, false, "ファイルがありません。");
        }

        var json = File.ReadAllText(fullPath);
        return new ParameterFileSnapshot(displayName, fileName, fullPath, true, json);
    }

    private static T? ReadParameter<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch
        {
            // 設定ファイルが壊れていても画面全体を落とさず、既定値で表示します。
            return default;
        }
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 一時ファイル経由で置き換え、書き込み途中のファイル破損を避けます。
        var temporaryPath = $"{path}.tmp";
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 同期版でも非同期版と同じく、一時ファイル経由で保存します。
        var temporaryPath = $"{path}.tmp";
        var json = JsonSerializer.Serialize(value, JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, path, overwrite: true);
    }
}

public sealed record ParameterFileSnapshot(
    string DisplayName,
    string FileName,
    string FullPath,
    bool Exists,
    string Json);

// rds.json は接続先の正である接続文字列だけを持ちます。
public sealed class RdsParameterFile
{
    public string Provider { get; set; } = "SqlServer";
    public string ConnectionString { get; set; } = string.Empty;
}

// mail.json のメール送信設定です。
public sealed class MailParameterFile
{
    public bool Enabled { get; set; }
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string SenderAddress { get; set; } = string.Empty;
    public string SenderName { get; set; } = "ワークフロー運用管理";
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;

    // パスワードリセットメール等を送るために最低限必要な設定が揃っているか判定します。
    public bool IsConfigured() =>
        Enabled &&
        !string.IsNullOrWhiteSpace(SmtpHost) &&
        SmtpPort is > 0 and <= 65535 &&
        !string.IsNullOrWhiteSpace(SenderAddress) &&
        TimeoutSeconds > 0;
}
