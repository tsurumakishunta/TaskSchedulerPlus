using TaskSchedulerPlus.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace TaskSchedulerPlus.Web.Services;

// 初回起動時に DB 接続確認、マイグレーション、初期管理者作成をまとめて行うサービスです。
public sealed class InitialSetupService(
    ISetupConfigurationStore setupConfiguration,
    InitialSetupState initialSetupState,
    IServiceScopeFactory scopeFactory,
    ILogger<InitialSetupService> logger)
{
    public async Task<SetupResult> CompleteAsync(InitialSetupRequest request, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "初期設定を開始します。ServerName={ServerName}, DatabaseName={DatabaseName}, AuthenticationMode={AuthenticationMode}, TimeZoneId={TimeZoneId}",
            request.ServerName,
            request.DatabaseName,
            request.AuthenticationMode,
            request.TimeZoneId);

        if (setupConfiguration.IsConfigured && !initialSetupState.IsRequired)
        {
            logger.LogWarning("初期設定は既に完了しているため中断しました。");
            return new SetupResult(false, "初期設定はすでに完了しています。");
        }

        // 入力内容から SQL Server 接続文字列を組み立てます。
        var connectionResult = BuildConnectionString(request);
        if (!connectionResult.Succeeded)
        {
            logger.LogWarning(
                "初期設定の接続文字列作成に失敗しました。AuthenticationMode={AuthenticationMode}",
                request.AuthenticationMode);
            return new SetupResult(false, connectionResult.Message);
        }

        try
        {
            // 指定された DB に接続し、Migration を適用できるか検証します。
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlServer(connectionResult.ConnectionString)
                .Options;
            await using var validationDb = new ApplicationDbContext(options);

            logger.LogInformation(
                "初期設定用データベースの検証を開始します。ServerName={ServerName}, DatabaseName={DatabaseName}",
                request.ServerName,
                request.DatabaseName);
            await validationDb.Database.MigrateAsync(cancellationToken);

            if (await validationDb.Users.AnyAsync(cancellationToken))
            {
                logger.LogWarning(
                    "初期設定対象データベースに既存ユーザーが存在するため中断しました。ServerName={ServerName}, DatabaseName={DatabaseName}",
                    request.ServerName,
                    request.DatabaseName);
                return new SetupResult(
                    false,
                    "指定したデータベースには既にユーザーが存在します。初回設定には空のデータベースを指定してください。");
            }
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            logger.LogWarning(exception, "データベースの初期設定に失敗しました。");
            return new SetupResult(false, "データベース接続または初期化に失敗しました。SQL Server名、データベース名、認証情報、接続権限を確認してください。");
        }

        var configuration = new SetupConfiguration
        {
            ApplicationName = request.ApplicationName.Trim(),
            ServerName = request.ServerName.Trim(),
            DatabaseName = request.DatabaseName.Trim(),
            AuthenticationMode = request.AuthenticationMode,
            TimeZoneId = request.TimeZoneId,
            AdministratorEmail = request.AdministratorEmail.Trim(),
            ConfiguredAt = DateTimeOffset.UtcNow
        };

        // 接続文字列は保護して setup.json に保存し、参照用の rds.json も更新します。
        await setupConfiguration.SaveAsync(configuration, connectionResult.ConnectionString!, cancellationToken);
        logger.LogInformation(
            "初期設定ファイルを保存しました。ServerName={ServerName}, DatabaseName={DatabaseName}, AuthenticationMode={AuthenticationMode}",
            configuration.ServerName,
            configuration.DatabaseName,
            configuration.AuthenticationMode);

        try
        {
            logger.LogInformation("初期管理者アカウントの作成を開始します。");

            // 管理者作成とロール付与をトランザクション内で行います。
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            await IdentityRoleInitializer.EnsureRolesAsync(scope.ServiceProvider);

            var administrator = new ApplicationUser
            {
                UserName = request.AdministratorEmail.Trim(),
                Email = request.AdministratorEmail.Trim(),
                EmailConfirmed = true
            };
            var userResult = await userManager.CreateAsync(administrator, request.AdministratorPassword);
            if (!userResult.Succeeded)
            {
                throw new InvalidOperationException(JoinIdentityErrors(userResult));
            }

            var assignmentResult = await userManager.AddToRoleAsync(administrator, AppRoles.Administrator);
            if (!assignmentResult.Succeeded)
            {
                throw new InvalidOperationException(JoinIdentityErrors(assignmentResult));
            }

            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(
                "初期管理者アカウントを作成しました。AdministratorUserId={AdministratorUserId}",
                administrator.Id);
        }
        catch (Exception exception)
        {
            // 管理者作成に失敗した場合は、途中で保存した初期設定を戻します。
            await setupConfiguration.ClearAsync(cancellationToken);
            initialSetupState.MarkRequired();
            logger.LogWarning(exception, "初期管理者アカウントの作成に失敗しました。");
            return new SetupResult(false, $"初期管理者の作成に失敗しました。{exception.Message}");
        }

        logger.LogInformation(
            "初期設定が完了しました。ServerName={ServerName}, DatabaseName={DatabaseName}, AuthenticationMode={AuthenticationMode}",
            configuration.ServerName,
            configuration.DatabaseName,
            configuration.AuthenticationMode);

        initialSetupState.MarkCompleted();

        return new SetupResult(true, "初期設定が完了しました。初期管理者でログインしてください。");
    }

    private static ConnectionStringResult BuildConnectionString(InitialSetupRequest request)
    {
        if (request.AuthenticationMode == DatabaseAuthenticationMode.SqlServer &&
            (string.IsNullOrWhiteSpace(request.DatabaseUserName) || string.IsNullOrWhiteSpace(request.DatabasePassword)))
        {
            // SQL Server 認証では DB ユーザー名とパスワードが必須です。
            return new ConnectionStringResult(false, "SQL Server 認証ではデータベースのユーザー名とパスワードが必要です。", null);
        }

        try
        {
            // SqlConnectionStringBuilder を使い、接続文字列の組み立てミスを避けます。
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = request.ServerName.Trim(),
                InitialCatalog = request.DatabaseName.Trim(),
                IntegratedSecurity = request.AuthenticationMode == DatabaseAuthenticationMode.Windows,
                MultipleActiveResultSets = true,
                Encrypt = request.EncryptConnection,
                TrustServerCertificate = request.TrustServerCertificate,
                ConnectTimeout = 15
            };

            if (request.AuthenticationMode == DatabaseAuthenticationMode.SqlServer)
            {
                builder.UserID = request.DatabaseUserName!.Trim();
                builder.Password = request.DatabasePassword!;
            }

            return new ConnectionStringResult(true, string.Empty, builder.ConnectionString);
        }
        catch (ArgumentException)
        {
            return new ConnectionStringResult(false, "接続先の設定が正しくありません。サーバー名、データベース名、認証情報を確認してください。", null);
        }
    }

    private static string JoinIdentityErrors(IdentityResult result) =>
        string.Join(" / ", result.Errors.Select(error => error.Description));

    private sealed record ConnectionStringResult(bool Succeeded, string Message, string? ConnectionString);
}
