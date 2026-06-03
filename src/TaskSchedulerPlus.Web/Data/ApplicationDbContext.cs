using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace TaskSchedulerPlus.Web.Data;

// Identity のユーザー管理テーブルと、業務用テーブルをまとめて扱う DbContext です。
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<JobDefinition> JobDefinitions => Set<JobDefinition>();
    public DbSet<JobFlow> JobFlows => Set<JobFlow>();
    public DbSet<JobNode> JobNodes => Set<JobNode>();
    public DbSet<FlowRun> FlowRuns => Set<FlowRun>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // 作成日・更新日は主要テーブルで共通管理します。
        ConfigureAudit<JobDefinition>(builder);
        ConfigureAudit<JobFlow>(builder);
        ConfigureAudit<JobNode>(builder);
        ConfigureAudit<FlowRun>(builder);
        ConfigureAudit<JobRun>(builder);

        // ワークフローを削除した場合は、紐づくノードも一緒に削除します。
        builder.Entity<JobNode>()
            .HasOne(node => node.JobFlow)
            .WithMany(flow => flow.Nodes)
            .HasForeignKey(node => node.JobFlowId)
            .OnDelete(DeleteBehavior.Cascade);

        // タスク定義は複数ノードから参照されるため、誤削除を防ぐ目的で Restrict にしています。
        builder.Entity<JobNode>()
            .HasOne(node => node.JobDefinition)
            .WithMany(job => job.Nodes)
            .HasForeignKey(node => node.JobDefinitionId)
            .OnDelete(DeleteBehavior.Restrict);

        // 同じワークフロー内で同名タスクが重複しないようにします。
        builder.Entity<JobNode>()
            .HasIndex(node => new { node.JobFlowId, node.Name })
            .IsUnique();

        // ワークフロー名は利用者が識別する名前なので、全体で一意にします。
        builder.Entity<JobFlow>()
            .HasIndex(flow => flow.Name)
            .IsUnique();

        // スケジュール監視で NextRunAt を検索するためのインデックスです。
        builder.Entity<JobFlow>()
            .HasIndex(flow => new { flow.ScheduleEnabled, flow.NextRunAt });

        // 実行履歴削除や期間抽出で CreatedAt を使うためのインデックスです。
        builder.Entity<FlowRun>()
            .HasIndex(run => run.CreatedAt);

        builder.Entity<FlowRun>()
            .HasOne(run => run.JobFlow)
            .WithMany(flow => flow.Runs)
            .HasForeignKey(run => run.JobFlowId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<JobRun>()
            .HasOne(run => run.FlowRun)
            .WithMany(flowRun => flowRun.JobRuns)
            .HasForeignKey(run => run.FlowRunId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<JobRun>()
            .HasOne(run => run.JobNode)
            .WithMany(node => node.Runs)
            .HasForeignKey(run => run.JobNodeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<JobRun>()
            .HasIndex(run => new { run.FlowRunId, run.JobNodeId })
            .IsUnique();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        // 同期保存でも監査日時を必ず補完します。
        ApplyAuditTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override int SaveChanges()
    {
        ApplyAuditTimestamps();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ApplyAuditTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyAuditTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    private static void ConfigureAudit<TEntity>(ModelBuilder builder)
        where TEntity : class, IAuditableEntity
    {
        // DB 直投入時でも値が入るよう、SQL Server 側にも既定値を持たせます。
        builder.Entity<TEntity>()
            .Property(entity => entity.CreatedAt)
            .HasDefaultValueSql("SYSUTCDATETIME()");

        builder.Entity<TEntity>()
            .Property(entity => entity.UpdatedAt)
            .HasDefaultValueSql("SYSUTCDATETIME()");
    }

    private void ApplyAuditTimestamps()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<IAuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                // 新規作成時は作成日と更新日を同じ時刻に揃えます。
                if (entry.Entity.CreatedAt == default)
                {
                    entry.Entity.CreatedAt = now;
                }

                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                // 更新時に作成日が上書きされないよう、CreatedAt は変更対象から外します。
                entry.Property(entity => entity.CreatedAt).IsModified = false;
                entry.Entity.UpdatedAt = now;
            }
        }
    }
}
