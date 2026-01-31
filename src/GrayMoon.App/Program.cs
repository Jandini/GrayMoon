using GrayMoon.App.Components;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

builder.Services.Configure<WorkspaceOptions>(builder.Configuration.GetSection("Workspace"));

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Database (SQLite) for persisted data
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=graymoon.db";
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddScoped<GitHubConnectorRepository>();
builder.Services.AddScoped<GitHubRepositoryRepository>();
builder.Services.AddScoped<WorkspaceRepository>();
builder.Services.AddScoped<WorkspaceService>();
builder.Services.AddScoped<GitCommandService>();
builder.Services.AddScoped<GitVersionCommandService>();
builder.Services.AddScoped<WorkspaceGitService>();
builder.Services.AddScoped<GitHubRepositoryService>();
builder.Services.AddScoped<GitHubActionsService>();

// GitHub API service
builder.Services.AddHttpClient<GitHubService>();


var app = builder.Build();

// Ensure the local SQLite database is created and migrate schema if needed
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    dbContext.Database.EnsureCreated();
    await MigrateWorkspaceSyncMetadataAsync(dbContext);
}

static async Task MigrateWorkspaceSyncMetadataAsync(AppDbContext dbContext)
{
    try
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Workspaces') WHERE name='LastSyncedAt'";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count == 0)
            {
                cmd.CommandText = "ALTER TABLE Workspaces ADD COLUMN LastSyncedAt TEXT";
                await cmd.ExecuteNonQueryAsync();
                cmd.CommandText = "ALTER TABLE Workspaces ADD COLUMN IsInSync INTEGER NOT NULL DEFAULT 0";
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
    catch
    {
        // Migration may already be applied or table doesn't exist yet
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
