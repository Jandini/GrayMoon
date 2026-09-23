using System.Net.Http;
using System.Text.RegularExpressions;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Services;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Rewrites relative (and optionally remote) markdown image <c>src</c> values to data URIs so Preview
/// can render them inside the App (which has no repository filesystem access).
/// </summary>
public sealed class MarkdownImageEmbedder(IAgentBridge agentBridge, IHttpClientFactory httpClientFactory, ILogger<MarkdownImageEmbedder> logger)
{
    private const int MaxImages = 24;
    private const long MaxRemoteBytes = 2 * 1024 * 1024;

    private static readonly Regex ImgSrc = new(
        @"<img\b[^>]*?\bsrc\s*=\s*(?:""([^""]+)""|'([^']+)')",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<string> EmbedAsync(
        string html,
        string workspaceRoot,
        string workspaceName,
        string repositoryName,
        string markdownRelativePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(html))
        {
            return html;
        }

        var matches = ImgSrc.Matches(html);
        if (matches.Count == 0)
        {
            return html;
        }

        var markdownDir = Path.GetDirectoryName(markdownRelativePath.Replace('\\', '/'))?.Replace('\\', '/') ?? string.Empty;
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var embedded = 0;

        foreach (Match match in matches)
        {
            if (embedded >= MaxImages)
            {
                break;
            }

            var src = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (string.IsNullOrWhiteSpace(src) || replacements.ContainsKey(src) || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? dataUri = null;
            if (IsHttpUrl(src))
            {
                dataUri = await TryDownloadRemoteAsync(src, cancellationToken);
            }
            else if (!src.StartsWith('#') && !LooksLikeScheme(src))
            {
                dataUri = await TryLoadRepoFileAsync(
                    workspaceRoot,
                    workspaceName,
                    repositoryName,
                    ResolveRepoRelativePath(markdownDir, src),
                    cancellationToken);
            }

            if (dataUri != null)
            {
                replacements[src] = dataUri;
                embedded++;
            }
        }

        if (replacements.Count == 0)
        {
            return html;
        }

        var result = html;
        foreach (var (from, to) in replacements)
        {
            result = result.Replace($"src=\"{from}\"", $"src=\"{to}\"", StringComparison.Ordinal);
            result = result.Replace($"src='{from}'", $"src='{to}'", StringComparison.Ordinal);
        }

        return result;
    }

    private async Task<string?> TryLoadRepoFileAsync(
        string workspaceRoot,
        string workspaceName,
        string repositoryName,
        string? repoRelativePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repoRelativePath))
        {
            return null;
        }

        try
        {
            var resp = await agentBridge.SendCommandAsync(
                "GetFileContents",
                new
                {
                    workspaceName,
                    workspaceRoot,
                    repositoryName,
                    filePath = repoRelativePath,
                    asBase64 = true,
                },
                cancellationToken);

            if (!resp.Success || resp.Data is null)
            {
                return null;
            }

            var result = AgentResponseJson.DeserializeAgentResponse<AgentGetFileContentsResponse>(resp.Data);
            if (result?.ErrorMessage != null || string.IsNullOrEmpty(result?.ContentBase64))
            {
                return null;
            }

            var contentType = string.IsNullOrWhiteSpace(result.ContentType)
                ? "application/octet-stream"
                : result.ContentType;
            if (!IsSafeImageContentType(contentType))
            {
                return null;
            }

            return $"data:{contentType};base64,{result.ContentBase64}";
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to embed markdown image {Path}", repoRelativePath);
            return null;
        }
    }

    private async Task<string?> TryDownloadRemoteAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(nameof(MarkdownImageEmbedder));
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is null || !IsSafeImageContentType(contentType))
            {
                return null;
            }

            var length = response.Content.Headers.ContentLength;
            if (length is > MaxRemoteBytes)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0 || bytes.Length > MaxRemoteBytes)
            {
                return null;
            }

            return $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to download markdown image {Url}", url);
            return null;
        }
    }

    private static bool IsSafeImageContentType(string contentType) =>
        contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        && !contentType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveRepoRelativePath(string markdownDir, string src)
    {
        // Strip optional title / angle-bracket wrapping leftovers Markdig might leave.
        var cleaned = src.Trim();
        var hash = cleaned.IndexOf('#');
        if (hash >= 0)
        {
            cleaned = cleaned[..hash];
        }

        var query = cleaned.IndexOf('?');
        if (query >= 0)
        {
            cleaned = cleaned[..query];
        }

        cleaned = cleaned.Replace('\\', '/').Trim();
        if (string.IsNullOrEmpty(cleaned) || cleaned.StartsWith('/'))
        {
            return null;
        }

        var combined = string.IsNullOrEmpty(markdownDir)
            ? cleaned
            : $"{markdownDir.TrimEnd('/')}/{cleaned}";

        var segments = new List<string>();
        foreach (var part in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (segments.Count == 0)
                {
                    return null;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(part);
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    private static bool IsHttpUrl(string src) =>
        src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeScheme(string src)
    {
        var colon = src.IndexOf(':');
        return colon > 0 && src.AsSpan(0, colon).ToString().All(static c => char.IsLetter(c));
    }
}
