namespace TaskSchedulerPlus.Web.Data;

// アプリ全体で使うロール名を一箇所に集約します。
public static class AppRoles
{
    public const string Administrator = "Administrator";
    public const string GeneralUser = "GeneralUser";
    public const string Viewer = "Viewer";

    public static readonly IReadOnlyList<string> All =
    [
        Administrator,
        GeneralUser,
        Viewer
    ];

    // 画面表示用の日本語名です。DB に保存する値は英語のロール名のままにします。
    public static string DisplayName(string role) => role switch
    {
        Administrator => "管理者",
        GeneralUser => "一般ユーザー",
        Viewer => "参照者",
        _ => role
    };
}

// Authorize 属性から参照するポリシー名です。
public static class AppPolicies
{
    public const string ManageUsers = "ManageUsers";
    public const string ManageWorkflows = "ManageWorkflows";
    public const string ViewExecutionResults = "ViewExecutionResults";
}
