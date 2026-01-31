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
builder.Services.AddScoped<GitHubRepositoryService>();
builder.Services.AddScoped<GitHubActionsService>();

// GitHub API service
builder.Services.AddHttpClient<GitHubService>();


var app = builder.Build();

// Ensure the local SQLite database is created
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    dbContext.Database.EnsureCreated();
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
