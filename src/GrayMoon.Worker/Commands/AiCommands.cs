using System.Diagnostics;
using GrayMoon.Common.Ai;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services.Ai;

namespace GrayMoon.Worker.Commands;

public sealed class GetAiProvidersCommand(AiCliRunner aiCli) : ICommandHandler<GetAiProvidersRequest, GetAiProvidersResponse>
{
    public async Task<GetAiProvidersResponse> ExecuteAsync(GetAiProvidersRequest request, CancellationToken cancellationToken = default)
    {
        var providers = await aiCli.ProbeAsync(request.Refresh, cancellationToken);
        return new GetAiProvidersResponse
        {
            Providers = providers.Select(p => new AiProviderAvailabilityDto
            {
                ProviderId = p.ProviderId,
                IsInstalled = p.IsInstalled,
                Version = p.Version,
                HasNoTools = p.HasNoTools,
                Problem = p.Problem,
            }).ToList(),
        };
    }
}

/// <summary>Runs exactly one provider call for one prepared prompt. Never retries; a bad answer is reported, not repaired.</summary>
public sealed class GenerateAiTextCommand(AiCliRunner aiCli) : ICommandHandler<GenerateAiTextRequest, GenerateAiTextResponse>
{
    public async Task<GenerateAiTextResponse> ExecuteAsync(GenerateAiTextRequest request, CancellationToken cancellationToken = default)
    {
        var providerId = request.ProviderId ?? throw new ArgumentException("providerId required");
        var systemPrompt = request.SystemPrompt ?? throw new ArgumentException("systemPrompt required");
        var userPrompt = request.UserPrompt ?? throw new ArgumentException("userPrompt required");
        var schema = request.JsonSchema ?? throw new ArgumentException("jsonSchema required");
        var model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim();

        var started = Stopwatch.GetTimestamp();
        var outcome = await aiCli.GenerateAsync(
            providerId,
            new AiCliRequest(systemPrompt, userPrompt, schema, model),
            TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 10, 600)),
            cancellationToken);

        return new GenerateAiTextResponse
        {
            Success = outcome.Error == AiGenerationErrorKind.None && outcome.Json is not null,
            Json = outcome.Json,
            ErrorKind = outcome.Error,
            ErrorMessage = outcome.Message,
            ElapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            Warning = AiGenerationWarningDto.From(outcome.Warning),
        };
    }
}
