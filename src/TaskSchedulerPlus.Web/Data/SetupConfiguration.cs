using System.ComponentModel.DataAnnotations;

namespace TaskSchedulerPlus.Web.Data;

// 初期設定画面で選択する SQL Server の認証方式です。
public enum DatabaseAuthenticationMode
{
    Windows,
    SqlServer
}

// 初期設定画面の入力モデルです。
public sealed class InitialSetupRequest
{
    [Required(ErrorMessage = "システム名を入力してください。")]
    [StringLength(100, ErrorMessage = "システム名は {1} 文字以内で入力してください。")]
    public string ApplicationName { get; set; } = "ワークフロー運用管理";

    [Required(ErrorMessage = "SQL Serverのサーバー名を入力してください。")]
    [StringLength(300, ErrorMessage = "サーバー名は {1} 文字以内で入力してください。")]
    public string ServerName { get; set; } = @"(localdb)\MSSQLLocalDB";

    [Required(ErrorMessage = "データベース名を入力してください。")]
    [StringLength(128, ErrorMessage = "データベース名は {1} 文字以内で入力してください。")]
    public string DatabaseName { get; set; } = "TaskSchedulerPlus";

    public DatabaseAuthenticationMode AuthenticationMode { get; set; } = DatabaseAuthenticationMode.Windows;

    [StringLength(128, ErrorMessage = "DBユーザー名は {1} 文字以内で入力してください。")]
    public string? DatabaseUserName { get; set; }

    [StringLength(300, ErrorMessage = "DBパスワードは {1} 文字以内で入力してください。")]
    public string? DatabasePassword { get; set; }

    public bool EncryptConnection { get; set; } = true;

    public bool TrustServerCertificate { get; set; } = true;

    [Required(ErrorMessage = "タイムゾーンを選択してください。")]
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;

    [Required(ErrorMessage = "初期管理者のメールアドレスを入力してください。")]
    [EmailAddress(ErrorMessage = "メールアドレスの形式で入力してください。")]
    [StringLength(256, ErrorMessage = "メールアドレスは {1} 文字以内で入力してください。")]
    public string AdministratorEmail { get; set; } = string.Empty;

    [Required(ErrorMessage = "初期管理者のパスワードを入力してください。")]
    [StringLength(100, ErrorMessage = "パスワードは {2} 文字以上 {1} 文字以内で入力してください。", MinimumLength = 10)]
    public string AdministratorPassword { get; set; } = string.Empty;

    [Compare(nameof(AdministratorPassword), ErrorMessage = "パスワードと確認用パスワードが一致しません。")]
    public string ConfirmAdministratorPassword { get; set; } = string.Empty;
}

// 初期設定完了後に App_Data/setup.json へ保存する設定です。
public sealed class SetupConfiguration
{
    public string ApplicationName { get; set; } = "ワークフロー運用管理";
    public string ServerName { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public DatabaseAuthenticationMode AuthenticationMode { get; set; }
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public string AdministratorEmail { get; set; } = string.Empty;
    public DateTimeOffset ConfiguredAt { get; set; }
    public string ProtectedConnectionString { get; set; } = string.Empty;
}

// 初期設定処理の成否と画面表示メッセージを返します。
public sealed record SetupResult(bool Succeeded, string Message);
