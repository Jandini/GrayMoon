using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GrayMoon.Abstractions.Models;
using GrayMoon.App.Models;
using Polly;
using Polly.Retry;

namespace GrayMoon.App.Services.GitHub;

/// <summary>Shared HTTP transport for GitHub REST calls: retry pipelines, request builders, and the generic GET/POST/PUT/PATCH helpers used by every other partial.</summary>
public sealed partial class GitHubService
{
    // 429 (rate limit) is intentionally NOT retried here: retrying just burns more quota against a limit
    // that is already exhausted. The rate-limit pause gate (IGitHubRateLimitTracker) handles backoff instead.
    private static readonly ResiliencePipeline<HttpResponseMessage> GitHubGetRetryPipeline =
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(r => r.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable)
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(),
                // 3 quick retries with short backoff, then fail
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = static args =>
                {
                    if (args.Outcome.Result is HttpResponseMessage prev)
                        prev.Dispose();
                    return ValueTask.CompletedTask;
                }
            })
            .Build();

    /// <summary>
    /// Retries transient failures for GitHub REST <strong>mutations</strong> (POST). Read-only calls use <see cref="GitHubGetRetryPipeline"/>.
    /// 429 is intentionally NOT retried; see the comment on <see cref="GitHubGetRetryPipeline"/>.
    /// </summary>
    private static readonly ResiliencePipeline<HttpResponseMessage> GitHubMutationRetryPipeline =
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(r => r.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable)
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = static args =>
                {
                    if (args.Outcome.Result is HttpResponseMessage prev)
                        prev.Dispose();
                    return ValueTask.CompletedTask;
                }
            })
            .Build();

    /// <summary>
    /// Retry pipeline for live-feed polls: retries 502/503 but NOT 429 -
    /// the polling loop owns rate-limit backoff so retrying here just wastes quota.
    /// </summary>
    private static readonly ResiliencePipeline<HttpResponseMessage> GitHubLiveFeedRetryPipeline =
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(r => r.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable)
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(),
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = static args =>
                {
                    if (args.Outcome.Result is HttpResponseMessage prev)
                        prev.Dispose();
                    return ValueTask.CompletedTask;
                }
            })
            .Build();

    private async Task<T?> PostAsync<T>(Connector connector, string requestUri, string? payload, CancellationToken cancellationToken = default)
    {
        var baseUrl = string.IsNullOrWhiteSpace(connector.ApiBaseUrl)
            ? "https://api.github.com/"
            : connector.ApiBaseUrl.TrimEnd('/') + "/";

        using var response = await GitHubMutationRetryPipeline.ExecuteAsync(async ct =>
        {
            using var request = CreatePostRequest(connector, requestUri, payload);
            return await _httpClient.SendAsync(request, ct);
        }, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("GitHub API POST failed. Status: {StatusCode}, URL: {Url}, Response: {Response}",
                response.StatusCode,
                new Uri(new Uri(baseUrl), requestUri),
                errorContent);
            throw GitHubApiErrorHelper.CreateHttpRequestException(response.StatusCode, errorContent, response);
        }

        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, cancellationToken);
    }

    /// <summary>PUT with a JSON body and a deserialized response - used for the merge endpoint. Mirrors <see cref="PostAsync{T}"/> but with HttpMethod.Put.</summary>
    private async Task<T?> PutAsync<T>(Connector connector, string requestUri, string? payload, CancellationToken cancellationToken = default)
    {
        var baseUrl = string.IsNullOrWhiteSpace(connector.ApiBaseUrl)
            ? "https://api.github.com/"
            : connector.ApiBaseUrl.TrimEnd('/') + "/";

        using var response = await GitHubMutationRetryPipeline.ExecuteAsync(async ct =>
        {
            using var request = CreatePutRequest(connector, requestUri, payload);
            return await _httpClient.SendAsync(request, ct);
        }, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("GitHub API PUT failed. Status: {StatusCode}, URL: {Url}, Response: {Response}",
                response.StatusCode,
                new Uri(new Uri(baseUrl), requestUri),
                errorContent);
            throw GitHubApiErrorHelper.CreateHttpRequestException(response.StatusCode, errorContent, response);
        }

        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, cancellationToken);
    }

    private HttpRequestMessage CreatePutRequest(Connector connector, string requestUri, string? payload)
    {
        var baseUrl = string.IsNullOrWhiteSpace(connector.ApiBaseUrl)
            ? "https://api.github.com/"
            : connector.ApiBaseUrl.TrimEnd('/') + "/";

        var request = new HttpRequestMessage(HttpMethod.Put, new Uri(new Uri(baseUrl), requestUri));
        request.Content = new StringContent(payload ?? "{}", Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("GrayMoon");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var token = ConnectorHelpers.UnprotectToken(connector.UserToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Connector token is not configured.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage CreateGetRequest(Connector connector, string requestUri)
    {
        var baseUrl = string.IsNullOrWhiteSpace(connector.ApiBaseUrl)
            ? "https://api.github.com/"
            : connector.ApiBaseUrl.TrimEnd('/') + "/";
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl), requestUri));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("GrayMoon");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var token = ConnectorHelpers.UnprotectToken(connector.UserToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Connector token is not configured.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<HttpResponseMessage> GetResponseAsync(Connector connector, string requestUri, CancellationToken cancellationToken, bool skipRateLimitRetry = false)
    {
        var pipeline = skipRateLimitRetry ? GitHubLiveFeedRetryPipeline : GitHubGetRetryPipeline;
        var response = await pipeline.ExecuteAsync(async (ct) =>
        {
            using var request = CreateGetRequest(connector, requestUri);
            var result = await _httpClient.SendAsync(request, ct);
            RecordRateLimit(connector, result);
            _usageRecorder.Record(connector.ConnectorId, connector.ConnectorName, requestUri, isNotModified: false, isError: !result.IsSuccessStatusCode);
            return result;
        }, cancellationToken);

        return response;
    }

    /// <summary>Records rate-limit headers from every response, not only failures, so remaining quota is visible before a call ever fails.</summary>
    private void RecordRateLimit(Connector connector, HttpResponseMessage response)
    {
        var snapshot = GitHubApiErrorHelper.TryParseRateLimitHeaders(response);
        if (snapshot.HasValue)
            _rateLimitTracker.Record(connector.ConnectorName, snapshot.Value);
    }

    private async Task<T?> GetAsync<T>(string requestUri)
    {
        var response = await _httpClient.GetAsync(requestUri);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("GitHub API call failed. Status: {StatusCode}, URL: {Url}, Response: {Response}",
                response.StatusCode,
                new Uri(_httpClient.BaseAddress ?? new Uri("https://api.github.com/"), requestUri),
                errorContent);
            ThrowGitHubApiFailure(response, errorContent);
        }

        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions);
    }

    private async Task<T?> GetAsync<T>(Connector connector, string requestUri, CancellationToken cancellationToken = default, bool skipRateLimitRetry = false)
    {
        using var response = await GetResponseAsync(connector, requestUri, cancellationToken, skipRateLimitRetry);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("GitHub API call failed. Status: {StatusCode}, URL: {Url}, Response: {Response}",
                response.StatusCode,
                response.RequestMessage?.RequestUri,
                errorContent);
            ThrowGitHubApiFailure(response, errorContent);
        }

        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, cancellationToken);
    }

    /// <summary>
    /// Like <see cref="GetAsync{T}(Connector, string, CancellationToken, bool)"/> but sends the last known
    /// ETag as <c>If-None-Match</c>. A 304 response (unchanged since last poll) is answered from the cached
    /// body instead of a fresh download - free against the primary limit and only 1 point against the
    /// secondary/abuse limit, which matters for the frequently-repolled jobs/branch-runs endpoints.
    /// </summary>
    private async Task<T?> GetETaggedAsync<T>(Connector connector, string requestUri, CancellationToken cancellationToken = default, bool skipRateLimitRetry = false)
    {
        var cacheKey = $"{connector.ConnectorId}|{requestUri}";
        var cached = _eTagCache.TryGet(cacheKey);

        var pipeline = skipRateLimitRetry ? GitHubLiveFeedRetryPipeline : GitHubGetRetryPipeline;
        using var response = await pipeline.ExecuteAsync(async ct =>
        {
            using var request = CreateGetRequest(connector, requestUri);
            if (cached is { } entry)
                request.Headers.TryAddWithoutValidation("If-None-Match", entry.ETag);
            var result = await _httpClient.SendAsync(request, ct);
            RecordRateLimit(connector, result);
            _usageRecorder.Record(
                connector.ConnectorId,
                connector.ConnectorName,
                requestUri,
                isNotModified: result.StatusCode == HttpStatusCode.NotModified,
                isError: !result.IsSuccessStatusCode && result.StatusCode != HttpStatusCode.NotModified);
            return result;
        }, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotModified && cached is { } cachedEntry)
        {
            _logger.LogTrace("GitHub ETag cache hit (304) for {Url}", requestUri);
            return JsonSerializer.Deserialize<T>(cachedEntry.Body, _jsonOptions);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("GitHub API call failed. Status: {StatusCode}, URL: {Url}, Response: {Response}",
                response.StatusCode,
                response.RequestMessage?.RequestUri,
                errorContent);
            ThrowGitHubApiFailure(response, errorContent);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var etag = response.Headers.ETag?.Tag;
        if (!string.IsNullOrWhiteSpace(etag))
            _eTagCache.Set(cacheKey, etag, body);

        return string.IsNullOrEmpty(body) ? default : JsonSerializer.Deserialize<T>(body, _jsonOptions);
    }

    private HttpRequestMessage CreatePostRequest(Connector connector, string requestUri, string? payload)
    {
        var baseUrl = string.IsNullOrWhiteSpace(connector.ApiBaseUrl)
            ? "https://api.github.com/"
            : connector.ApiBaseUrl.TrimEnd('/') + "/";

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), requestUri));
        if (payload != null)
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        else
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("GrayMoon");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var token = ConnectorHelpers.UnprotectToken(connector.UserToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Connector token is not configured.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task PatchAsync(Connector connector, string requestUri, string? payload = null, CancellationToken cancellationToken = default)
    {
        var baseUrl = string.IsNullOrWhiteSpace(connector.ApiBaseUrl)
            ? "https://api.github.com/"
            : connector.ApiBaseUrl.TrimEnd('/') + "/";

        using var response = await GitHubMutationRetryPipeline.ExecuteAsync(async ct =>
        {
            var request = new HttpRequestMessage(HttpMethod.Patch, new Uri(new Uri(baseUrl), requestUri));
            if (payload != null)
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            else
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd("GrayMoon");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            var token = ConnectorHelpers.UnprotectToken(connector.UserToken);
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Connector token is not configured.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }, cancellationToken);

        if (response.IsSuccessStatusCode)
            return;

        var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogError("GitHub API PATCH failed. Status: {StatusCode}, URL: {Url}, Response: {Response}",
            response.StatusCode,
            new Uri(new Uri(baseUrl), requestUri),
            errorContent);

        throw GitHubApiErrorHelper.CreateHttpRequestException(response.StatusCode, errorContent, response);
    }

    private async Task PostAsync(Connector connector, string requestUri, string? payload = null, CancellationToken cancellationToken = default)
    {
        var baseUrl = string.IsNullOrWhiteSpace(connector.ApiBaseUrl)
            ? "https://api.github.com/"
            : connector.ApiBaseUrl.TrimEnd('/') + "/";

        using var response = await GitHubMutationRetryPipeline.ExecuteAsync(async ct =>
        {
            using var request = CreatePostRequest(connector, requestUri, payload);
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }, cancellationToken);

        if (response.IsSuccessStatusCode)
            return;

        var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogError("GitHub API POST failed. Status: {StatusCode}, URL: {Url}, Response: {Response}",
            response.StatusCode,
            new Uri(new Uri(baseUrl), requestUri),
            errorContent);

        throw GitHubApiErrorHelper.CreateHttpRequestException(response.StatusCode, errorContent, response);
    }

    private static void ThrowGitHubApiFailure(HttpResponseMessage response, string errorContent)
    {
        if (GitHubApiErrorHelper.IsRateLimitExhausted(response)
            && !GitHubApiErrorHelper.LooksLikeRateLimit(response.StatusCode, GitHubApiErrorHelper.TryParseGitHubApiUserMessage(errorContent)))
        {
            throw GitHubApiErrorHelper.CreateHttpRequestException(
                response.StatusCode,
                "API rate limit exceeded. X-RateLimit-Remaining is 0.",
                response);
        }

        throw GitHubApiErrorHelper.CreateHttpRequestException(response.StatusCode, errorContent, response);
    }
}
