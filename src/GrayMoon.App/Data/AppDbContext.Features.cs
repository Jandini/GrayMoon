using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Data;

public partial class AppDbContext
{
    private static void ConfigureFeatureEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkspaceFeature>(entity =>
        {
            entity.ToTable("WorkspaceFeatures");
            entity.HasKey(f => f.WorkspaceFeatureId);
            entity.Property(f => f.WorkspaceFeatureId).ValueGeneratedOnAdd();
            entity.HasIndex(f => new { f.WorkspaceId, f.Name }).IsUnique();
            entity.Property(f => f.Name).IsRequired().HasMaxLength(200);
            entity.Property(f => f.LastError).HasMaxLength(2000);
            entity.Property(f => f.LifecycleState).HasConversion<int>();
            entity.Property(f => f.BaseKind).HasConversion<int>();
            entity.HasOne(f => f.Workspace)
                .WithMany()
                .HasForeignKey(f => f.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(f => f.BaseWorkspaceFeature)
                .WithMany()
                .HasForeignKey(f => f.BaseWorkspaceFeatureId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkspaceFeatureContext>(entity =>
        {
            entity.ToTable("WorkspaceFeatureContexts");
            entity.HasKey(c => c.WorkspaceFeatureContextId);
            entity.Property(c => c.WorkspaceFeatureContextId).ValueGeneratedOnAdd();
            entity.Property(c => c.Kind).HasConversion<int>();
            entity.HasIndex(c => c.WorkspaceId)
                .IsUnique()
                .HasFilter("\"Kind\" = 0")
                .HasDatabaseName("IX_WorkspaceFeatureContexts_Workspace_KindWorkspace");
            entity.HasIndex(c => c.WorkspaceFeatureId)
                .IsUnique()
                .HasFilter("\"WorkspaceFeatureId\" IS NOT NULL")
                .HasDatabaseName("IX_WorkspaceFeatureContexts_FeatureId");
            entity.HasOne(c => c.Workspace)
                .WithMany()
                .HasForeignKey(c => c.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(c => c.WorkspaceFeature)
                .WithOne(f => f.Context)
                .HasForeignKey<WorkspaceFeatureContext>(c => c.WorkspaceFeatureId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkspaceFeatureRepository>(entity =>
        {
            entity.ToTable("WorkspaceFeatureRepositories");
            entity.HasKey(r => r.WorkspaceFeatureRepositoryId);
            entity.Property(r => r.WorkspaceFeatureRepositoryId).ValueGeneratedOnAdd();
            entity.HasIndex(r => new { r.WorkspaceFeatureContextId, r.WorkspaceRepositoryId }).IsUnique();
            entity.Property(r => r.WorktreePath).IsRequired().HasMaxLength(2000);
            entity.Property(r => r.BaseCommitSha).IsRequired().HasMaxLength(64);
            entity.Property(r => r.LastError).HasMaxLength(2000);
            entity.Property(r => r.State).HasConversion<int>();
            entity.HasOne(r => r.WorkspaceFeatureContext)
                .WithMany(c => c.FeatureRepositories)
                .HasForeignKey(r => r.WorkspaceFeatureContextId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(r => r.WorkspaceRepository)
                .WithMany()
                .HasForeignKey(r => r.WorkspaceRepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkspaceRepositoryContextState>(entity =>
        {
            entity.ToTable("WorkspaceRepositoryContextStates");
            entity.HasKey(s => s.WorkspaceRepositoryContextStateId);
            entity.Property(s => s.WorkspaceRepositoryContextStateId).ValueGeneratedOnAdd();
            entity.HasIndex(s => new { s.WorkspaceFeatureContextId, s.WorkspaceRepositoryId }).IsUnique();
            entity.Property(s => s.BranchName).HasMaxLength(200);
            entity.Property(s => s.CheckedOutTag).HasMaxLength(200);
            entity.Property(s => s.HeadCommit).HasMaxLength(64);
            entity.Property(s => s.GitVersion).HasMaxLength(100);
            entity.Property(s => s.SyncStatus)
                .HasDefaultValue(RepoSyncStatus.NeedsSync)
                .HasSentinel(RepoSyncStatus.InSync);
            entity.HasOne(s => s.WorkspaceFeatureContext)
                .WithMany(c => c.RepositoryStates)
                .HasForeignKey(s => s.WorkspaceFeatureContextId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(s => s.WorkspaceRepository)
                .WithMany()
                .HasForeignKey(s => s.WorkspaceRepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkspaceSelectedFeatureContext>(entity =>
        {
            entity.ToTable("WorkspaceSelectedFeatureContexts");
            entity.HasKey(s => s.WorkspaceId);
            entity.HasOne(s => s.Workspace)
                .WithOne()
                .HasForeignKey<WorkspaceSelectedFeatureContext>(s => s.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(s => s.WorkspaceFeatureContext)
                .WithMany()
                .HasForeignKey(s => s.WorkspaceFeatureContextId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkspaceRepositoryContextPullRequest>(entity =>
        {
            entity.ToTable("WorkspaceRepositoryContextPullRequests");
            entity.HasKey(pr => pr.WorkspaceRepositoryContextPullRequestId);
            entity.Property(pr => pr.WorkspaceRepositoryContextPullRequestId).ValueGeneratedOnAdd();
            entity.HasIndex(pr => new { pr.WorkspaceFeatureContextId, pr.WorkspaceRepositoryId }).IsUnique();
            entity.Property(pr => pr.State).HasMaxLength(20);
            entity.Property(pr => pr.MergeableState).HasMaxLength(50);
            entity.Property(pr => pr.HtmlUrl).HasMaxLength(500);
            entity.HasOne(pr => pr.WorkspaceFeatureContext)
                .WithMany()
                .HasForeignKey(pr => pr.WorkspaceFeatureContextId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(pr => pr.WorkspaceRepository)
                .WithMany()
                .HasForeignKey(pr => pr.WorkspaceRepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkspaceRepositoryContextAction>(entity =>
        {
            entity.ToTable("WorkspaceRepositoryContextActions");
            entity.HasKey(a => a.WorkspaceRepositoryContextActionId);
            entity.Property(a => a.WorkspaceRepositoryContextActionId).ValueGeneratedOnAdd();
            entity.HasIndex(a => new { a.WorkspaceFeatureContextId, a.WorkspaceRepositoryId }).IsUnique();
            entity.Property(a => a.Status).HasMaxLength(20);
            entity.Property(a => a.HtmlUrl).HasMaxLength(500);
            entity.Property(a => a.BranchName).HasMaxLength(200);
            entity.Property(a => a.WorkflowName).HasMaxLength(200);
            entity.HasOne(a => a.WorkspaceFeatureContext)
                .WithMany()
                .HasForeignKey(a => a.WorkspaceFeatureContextId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(a => a.WorkspaceRepository)
                .WithMany()
                .HasForeignKey(a => a.WorkspaceRepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkspaceGitContextRepositoryStatus>(entity =>
        {
            entity.ToTable("WorkspaceGitContextRepositoryStatuses");
            entity.HasKey(s => s.WorkspaceGitContextRepositoryStatusId);
            entity.Property(s => s.WorkspaceGitContextRepositoryStatusId).ValueGeneratedOnAdd();
            entity.HasIndex(s => new { s.WorkspaceFeatureContextId, s.WorkspaceRepositoryId }).IsUnique();
            entity.Property(s => s.BranchName).HasMaxLength(200);
            entity.Property(s => s.HeadCommit).HasMaxLength(64);
            entity.Property(s => s.LastErrorCode).HasMaxLength(100);
            entity.Property(s => s.LastErrorMessage).HasMaxLength(2000);
            entity.HasOne(s => s.WorkspaceFeatureContext)
                .WithMany()
                .HasForeignKey(s => s.WorkspaceFeatureContextId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(s => s.WorkspaceRepository)
                .WithMany()
                .HasForeignKey(s => s.WorkspaceRepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkspaceGitContextChangeEntry>(entity =>
        {
            entity.ToTable("WorkspaceGitContextChangeEntries");
            entity.HasKey(e => e.WorkspaceGitContextChangeEntryId);
            entity.Property(e => e.WorkspaceGitContextChangeEntryId).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.WorkspaceFeatureContextId, e.WorkspaceRepositoryId })
                .HasDatabaseName("IX_WorkspaceGitContextChangeEntries_Context_Repo");
            entity.Property(e => e.Path).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.OriginalPath).HasMaxLength(2000);
            entity.Property(e => e.IndexChange).HasConversion<int>();
            entity.Property(e => e.WorktreeChange).HasConversion<int>();
            entity.HasOne(e => e.WorkspaceFeatureContext)
                .WithMany()
                .HasForeignKey(e => e.WorkspaceFeatureContextId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.WorkspaceRepository)
                .WithMany()
                .HasForeignKey(e => e.WorkspaceRepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkspaceFileContextState>(entity =>
        {
            entity.ToTable("WorkspaceFileContextStates");
            entity.HasKey(s => s.WorkspaceFileContextStateId);
            entity.Property(s => s.WorkspaceFileContextStateId).ValueGeneratedOnAdd();
            entity.HasIndex(s => new { s.WorkspaceFeatureContextId, s.FileId }).IsUnique();
            entity.HasOne(s => s.WorkspaceFeatureContext)
                .WithMany()
                .HasForeignKey(s => s.WorkspaceFeatureContextId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(s => s.File)
                .WithMany()
                .HasForeignKey(s => s.FileId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
