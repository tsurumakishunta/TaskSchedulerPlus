using TaskSchedulerPlus.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace TaskSchedulerPlus.Web.Services;

// 初期設定で保存された接続文字列を使って ApplicationDbContext を生成します。
public sealed class ConfiguredApplicationDbContextFactory(ISetupConfigurationStore setupConfiguration)
    : IDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext()
    {
        // 初期設定が未完了の場合は GetRequiredConnectionString が例外を投げます。
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(setupConfiguration.GetRequiredConnectionString())
            .Options;

        return new ApplicationDbContext(options);
    }

    public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateDbContext());
    }
}
