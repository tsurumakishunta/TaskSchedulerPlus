using TaskSchedulerPlus.Web.Data;
using TaskSchedulerPlus.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NLog;
using NLog.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Worker 側のログも Web と同じ NLog 設定で出力します。
// ログは Worker の実行直下に作成し、Web 側のログと自然に分離します。
NLogSetup.Configure(builder.Environment.ContentRootPath);
builder.Logging.ClearProviders();
builder.Logging.AddNLog();

// Web と同じ Data Protection 鍵を使い、Identity 関連の暗号化設定を共有します。
var dataDirectory = FileSetupConfigurationStore.ResolveAppDataPath(builder.Environment, builder.Configuration);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")))
    .SetApplicationName("TaskSchedulerPlus.Web");

// Windows Service として登録された場合に、サービス名を明示します。
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "TaskSchedulerPlusWorker";
});

// Worker は画面を持たず、Parameter と DB を共有してスケジュール監視と実行処理だけを担当します。
builder.Services.AddSingleton<ParameterFileStore>();
builder.Services.AddSingleton<ISetupConfigurationStore, FileSetupConfigurationStore>();
builder.Services.AddSingleton<IDbContextFactory<ApplicationDbContext>, ConfiguredApplicationDbContextFactory>();
builder.Services.AddSingleton<IFlowExecutionQueue, FlowExecutionQueue>();

// FlowScheduleDispatcher が実行予定をキューへ登録し、QueuedFlowRunDispatcher がキューを実行します。
builder.Services.AddHostedService<FlowScheduleDispatcher>();
builder.Services.AddHostedService<QueuedFlowRunDispatcher>();

var host = builder.Build();

// 接続設定が存在する場合のみ DB 初期化を行います。
// 未設定の環境では Worker が DB に接続しようとして失敗し続けないようにします。
var setupStore = host.Services.GetRequiredService<ISetupConfigurationStore>();
if (setupStore.IsConfigured)
{
    using var migrationScope = host.Services.CreateScope();
    var database = migrationScope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();
    database.Database.Migrate();
}

try
{
    host.Run();
}
finally
{
    // 常駐プロセス終了時にログを確実に書き切ります。
    LogManager.Shutdown();
}
