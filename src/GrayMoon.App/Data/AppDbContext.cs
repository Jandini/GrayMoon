using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Connector> Connectors => Set<Connector>();
    public DbSet<Repository> Repositories => Set<Repository>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceRepositoryLink> WorkspaceRepositories => Set<WorkspaceRepositoryLink>();
    public DbSet<RepositoryProject> RepositoryProjects => Set<RepositoryProject>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Connector>(entity =>
        {
            entity.ToTable("Connectors");
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

        modelBuilder.Entity<Repository>(entity =>
        {
            entity.ToTable("Repositories");
            entity.HasIndex(repository => new { repository.ConnectorId, repository.RepositoryName, repository.OrgName })
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

            entity.HasOne(repository => repository.Connector)
                .WithMany()
                .HasForeignKey(repository => repository.ConnectorId)
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
            entity.ToTable("WorkspaceRepositories");
            entity.HasKey(wr => wr.RepositoryId);
            entity.Property(wr => wr.RepositoryId).ValueGeneratedOnAdd();

            entity.HasIndex(wr => new { wr.WorkspaceId, wr.LinkedRepositoryId })
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

            entity.HasOne(wr => wr.Repository)
                .WithMany()
                .HasForeignKey(wr => wr.LinkedRepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RepositoryProject>(entity =>
        {
            entity.HasKey(p => p.ProjectId);
            entity.HasIndex(p => new { p.RepositoryId, p.ProjectName })
                .IsUnique();

            entity.Property(p => p.ProjectName)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(p => p.ProjectFilePath)
                .IsRequired()
                .HasMaxLength(500);

            entity.Property(p => p.TargetFramework)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(p => p.PackageId)
                .HasMaxLength(200);

            entity.HasOne(p => p.Repository)
                .WithMany()
                .HasForeignKey(p => p.RepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
