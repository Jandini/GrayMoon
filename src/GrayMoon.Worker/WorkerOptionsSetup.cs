using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.Worker;

/// <summary>
/// Binds the Worker's options classes from configuration. In one place so that <c>WorkerOptionsBindingTests</c>
/// runs the same calls: in the Native AOT exe the binding code is generated at build time, and a property the
/// generator skips keeps its default without any error.
/// </summary>
internal static class WorkerOptionsSetup
{
    public static IServiceCollection AddWorkerOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorkerOptions>(configuration.GetSection(WorkerOptions.SectionName));
        services.Configure<GitChangesOptions>(configuration.GetSection($"{WorkerOptions.SectionName}:GitChanges"));
        services.Configure<GitProcessOptions>(configuration.GetSection($"{WorkerOptions.SectionName}:GitProcess"));
        // ProcessExecutionOptions.DefaultTimeoutSeconds mirrors GitProcessOptions.DefaultTimeoutSeconds -
        // same "GitProcess" section, so CommandLineService's own fallback timeout stays in sync with the
        // tier GitProcessRunner resolves for local/fast git operations.
        services.Configure<ProcessExecutionOptions>(configuration.GetSection($"{WorkerOptions.SectionName}:GitProcess"));
        return services;
    }

    /// <summary>The options the <c>run</c> verb starts from: the settings files, before the command line is applied.</summary>
    public static WorkerOptions ReadWorkerOptions(IConfiguration configuration)
    {
        var options = new WorkerOptions();
        configuration.GetSection(WorkerOptions.SectionName).Bind(options);
        return options;
    }
}
