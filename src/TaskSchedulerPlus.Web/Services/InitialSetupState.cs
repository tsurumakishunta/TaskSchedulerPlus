namespace TaskSchedulerPlus.Web.Services;

// 初期設定が必要かどうかを、アプリケーション起動中に共有する状態です。
public sealed class InitialSetupState
{
    private volatile bool _isRequired = true;

    public bool IsRequired => _isRequired;

    public void SetRequired(bool isRequired) => _isRequired = isRequired;

    public void MarkRequired() => _isRequired = true;

    public void MarkCompleted() => _isRequired = false;
}
