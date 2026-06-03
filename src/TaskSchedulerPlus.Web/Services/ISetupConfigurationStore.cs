using TaskSchedulerPlus.Web.Data;

namespace TaskSchedulerPlus.Web.Services;

// 初期設定の読み書きと、DB 接続文字列の取得を抽象化します。
public interface ISetupConfigurationStore
{
    // 初期設定が完了しているかを表します。
    bool IsConfigured { get; }

    // 現在保存されている初期設定です。
    SetupConfiguration? Current { get; }

    // DB 接続文字列を取得します。未設定の場合は例外にします。
    string GetRequiredConnectionString();

    // 初期設定を保存します。接続文字列は実装側で保護して保存します。
    Task SaveAsync(SetupConfiguration configuration, string connectionString, CancellationToken cancellationToken = default);

    // 初期設定を削除し、未設定状態へ戻します。
    Task ClearAsync(CancellationToken cancellationToken = default);
}
