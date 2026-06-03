using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NLog;
using NLog.Extensions.Logging;
using TaskSchedulerPlus.Web.Components;
using TaskSchedulerPlus.Web.Components.Account;
using TaskSchedulerPlus.Web.Data;
using TaskSchedulerPlus.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Web アプリ起動直後に NLog を初期化します。
// ASP.NET Core 標準ロガーより先に設定することで、起動処理中のログも同じ形式で出力できます。
NLogSetup.Configure(builder.Environment.ContentRootPath);
builder.Logging.ClearProviders();
builder.Logging.AddNLog();

// Blazor Server の画面表示に必要な Razor Components と対話型レンダリングを登録します。
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// インストール後は Web 画面も Windows Service として常駐できるようにします。
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "TaskSchedulerPlusWeb";
});

// Identity が Blazor コンポーネント内で現在ユーザー情報を扱えるようにするための登録です。
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityUserAccessor>();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

// 認証 Cookie の既定スキーマを Identity に合わせます。
// 外部ログインは使わない方針ですが、Identity の内部処理で必要な既定値は設定しておきます。
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

// 画面や操作ごとの権限をポリシーとして定義します。
// 各 Razor 画面ではロール名を直接書かず、このポリシー名を参照します。
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AppPolicies.ManageUsers, policy =>
        policy.RequireRole(AppRoles.Administrator));
    options.AddPolicy(AppPolicies.ManageWorkflows, policy =>
        policy.RequireRole(AppRoles.Administrator, AppRoles.GeneralUser));
    options.AddPolicy(AppPolicies.ViewExecutionResults, policy =>
        policy.RequireRole(AppRoles.Administrator, AppRoles.GeneralUser, AppRoles.Viewer));
});

// Data Protection の鍵は App_Data 配下に永続化します。
// これによりアプリ再起動後もログイン Cookie やトークンを復号できます。
var dataDirectory = FileSetupConfigurationStore.ResolveAppDataPath(builder.Environment, builder.Configuration);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")))
    .SetApplicationName("TaskSchedulerPlus.Web");

// Parameter フォルダ、初期設定、DbContext など、設定ファイルを起点に動くサービスを登録します。
builder.Services.AddSingleton<ParameterFileStore>();
builder.Services.AddSingleton<ISetupConfigurationStore, FileSetupConfigurationStore>();
builder.Services.AddSingleton<InitialSetupState>();
builder.Services.AddSingleton<IDbContextFactory<ApplicationDbContext>, ConfiguredApplicationDbContextFactory>();
builder.Services.AddScoped(provider => provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
builder.Services.AddScoped<InitialSetupService>();
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// Identity のユーザー、ロール、パスワード、ロックアウト条件を定義します。
// エラーメッセージは JapaneseIdentityErrorDescriber で日本語化します。
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 10;
        options.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddRoles<IdentityRole>()
    .AddErrorDescriber<JapaneseIdentityErrorDescriber>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

// メール送信は mail.json の設定を読む実装に差し替えます。
// ワークフロー実行キューは画面やスケジューラから共通で呼び出されます。
builder.Services.AddSingleton<ParameterEmailSender>();
builder.Services.AddSingleton<IEmailSender<ApplicationUser>>(provider => provider.GetRequiredService<ParameterEmailSender>());
builder.Services.AddScoped<WorkflowJsonTransferService>();
builder.Services.AddScoped<IFlowExecutionQueue, FlowExecutionQueue>();

var app = builder.Build();

// 初期設定が完了している場合のみ DB マイグレーションとロール作成を実行します。
// 未設定状態では接続文字列がないため、DB に触らず setup 画面へ誘導します。
var setupStore = app.Services.GetRequiredService<ISetupConfigurationStore>();
var initialSetupState = app.Services.GetRequiredService<InitialSetupState>();
var setupRequired = true;
if (setupStore.IsConfigured)
{
    try
    {
        using var migrationScope = app.Services.CreateScope();
        var database = migrationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        database.Database.Migrate();
        await IdentityRoleInitializer.EnsureRolesAsync(migrationScope.ServiceProvider);

        setupRequired = !await database.Users.AnyAsync();
        if (setupRequired)
        {
            app.Logger.LogWarning("初期設定ファイルは存在しますが、ログインユーザーが存在しないため初期設定画面へ誘導します。");
        }
    }
    catch (Exception exception)
    {
        setupRequired = true;
        app.Logger.LogWarning(exception, "初期設定状態の確認に失敗したため初期設定画面へ誘導します。");
    }
}
initialSetupState.SetRequired(setupRequired);

// 開発環境では EF Core の詳細なエラー画面を有効化し、本番環境では共通エラー画面と HSTS を使います。
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

// 初期設定が未完了の場合は、通常画面へアクセスさせず /setup へリダイレクトします。
// Blazor のフレームワークファイルや CSS まで止めると setup 画面が表示できないため、必要な静的リソースは除外します。
app.Use(async (context, next) =>
{
    if (initialSetupState.IsRequired && !IsSetupResource(context.Request.Path))
    {
        context.Response.Redirect("/setup");
        return;
    }

    await next();
});

// 認証、認可、CSRF 対策を有効化します。
// MapRazorComponents より前に置くことで、画面単位の Authorize が正しく機能します。
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// 静的ファイルと Blazor コンポーネントのエンドポイントを登録します。
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Identity のログイン、ログアウト、パスワードリセットなどに必要な追加エンドポイントを登録します。
app.MapAdditionalIdentityEndpoints();

try
{
    app.Run();
}
finally
{
    // アプリ終了時に NLog のバッファを明示的にフラッシュします。
    LogManager.Shutdown();
}

// 初期設定前でも読み込ませる必要があるパスを判定します。
// ここに含めない画面や API は、初期設定完了まで /setup へ誘導されます。
static bool IsSetupResource(PathString path)
{
    var value = path.Value ?? string.Empty;

    return path.StartsWithSegments("/setup") ||
        path.StartsWithSegments("/_framework") ||
        path.StartsWithSegments("/_blazor") ||
        path.StartsWithSegments("/lib") ||
        path.StartsWithSegments("/app.js") ||
        path.StartsWithSegments("/app.css") ||
        value.StartsWith("/app.", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/TaskSchedulerPlus.Web.styles.css") ||
        value.StartsWith("/TaskSchedulerPlus.Web.styles.", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("/TaskSchedulerPlus.Web.", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/task-scheduler-plus-icon.svg") ||
        path.StartsWithSegments("/favicon.png");
}
