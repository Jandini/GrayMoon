using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<GitHubConnector> GitHubConnectors => Set<GitHubConnector>();
    public DbSet<GitHubRepository> GitHubRepositories => Set<GitHubRepository>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceRepositoryLink> WorkspaceRepositories => Set<WorkspaceRepositoryLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<GitHubConnector>(entity =>
        {
            entity.HasIndex(connector => connector.ConnectorName)
                .IsUnique();

            entity.Property(connector => connector.ConnectorName)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(connector => connector.ApiBaseUrl)
                .IsRequired()
                .HasMaxLength(300);

            entity.Property(connector => connector.UserName)
                .HasMaxLength(100);

            entity.Property(connector => connector.UserToken)
                .IsRequired()
                .HasMaxLength(500);

            entity.Property(connector => connector.Status)
                .IsRequired()
                .HasMaxLength(20);

            entity.Property(connector => connector.IsActive)
                .HasDefaultValue(true);

            entity.Property(connector => connector.LastError)
                .HasMaxLength(1000);
        });

        modelBuilder.Entity<GitHubRepository>(entity =>
        {
            entity.HasIndex(repository => new { repository.GitHubConnectorId, repository.RepositoryName, repository.OrgName })
                .IsUnique();

            entity.Property(repository => repository.RepositoryName)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(repository => repository.OrgName)
                .HasMaxLength(200);

            entity.Property(repository => repository.Visibility)
                .IsRequired()
                .HasMaxLength(20);

            entity.Property(repository => repository.CloneUrl)
                .IsRequired()
                .HasMaxLength(500);

            entity.HasOne(repository => repository.GitHubConnector)
                .WithMany()
                .HasForeignKey(repository => repository.GitHubConnectorId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Workspace>(entity =>
        {
            entity.HasIndex(workspace => workspace.Name)
                .IsUnique();

            entity.Property(workspace => workspace.Name)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(workspace => workspace.IsInSync)
                .HasDefaultValue(false);
        });

        modelBuilder.Entity<WorkspaceRepositoryLink>(entity =>
        {
            entity.HasIndex(wr => new { wr.WorkspaceId, wr.GitHubRepositoryId })
                .IsUnique();

            entity.Property(wr => wr.GitVersion)
                .HasMaxLength(100);

            entity.Property(wr => wr.BranchName)
                .HasMaxLength(200);

            entity.Property(wr => wr.SyncStatus)
                .HasDefaultValue(RepoSyncStatus.NeedsSync)
                .HasSentinel(RepoSyncStatus.InSync);

            entity.HasOne(wr => wr.Workspace)
                .WithMany(workspace => workspace.Repositories)
                .HasForeignKey(wr => wr.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(wr => wr.GitHubRepository)
                .WithMany()
                .HasForeignKey(wr => wr.GitHubRepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
