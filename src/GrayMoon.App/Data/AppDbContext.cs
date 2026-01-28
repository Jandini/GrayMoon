using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<GitHubConnector> GitHubConnectors => Set<GitHubConnector>();
    public DbSet<GitHubRepository> GitHubRepositories => Set<GitHubRepository>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceRepositoryLink> WorkspaceRepositoryLinks => Set<WorkspaceRepositoryLink>();

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

            entity.Property(connector => connector.UserToken)
                .IsRequired()
                .HasMaxLength(500);

            entity.Property(connector => connector.Status)
                .IsRequired()
                .HasMaxLength(20);

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
        });

        modelBuilder.Entity<WorkspaceRepositoryLink>(entity =>
        {
            entity.HasIndex(link => new { link.WorkspaceId, link.GitHubRepositoryId })
                .IsUnique();

            entity.HasOne(link => link.Workspace)
                .WithMany(workspace => workspace.Repositories)
                .HasForeignKey(link => link.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(link => link.GitHubRepository)
                .WithMany()
                .HasForeignKey(link => link.GitHubRepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
