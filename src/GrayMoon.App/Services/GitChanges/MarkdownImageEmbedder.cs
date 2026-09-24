using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Services;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Rewrites relative (and remote) markdown image <c>src</c> values to data URIs so Preview can render
/// them inside the App (no repository filesystem access; WebView2 also has no GitHub session cookies).
/// </summary>
public sealed class MarkdownImageEmbedder(IAgentBridge agentBridge, IHttpClientFactory httpClientFactory, ILogger<MarkdownImageEmbedder> logger)
{
    private const int MaxImages = 24;
    private const long MaxRemoteBytes = 2 * 1024 * 1024;
    private const long MaxSvgBytes = 256 * 1024;

    private static readonly Regex ImgSrc = new(
        @"<img\b[^>]*?\bsrc\s*=\s*(?:""([^""]+)""|'([^']+)')",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SvgDangerous = new(
        @"<script[\s>]|javascript:|\bon[a-z]+\s*=",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<string> EmbedAsync(
        string html,
        string workspaceRoot,
        string workspaceName,
        string repositoryName,
        string markdownRelativePath,
        string? githubBearerToken = null,
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
                dataUri = await TryDownloadRemoteAsync(src, githubBearerToken, cancellationToken);
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

        var result = html;
        foreach (var (from, to) in replacements)
        {
            result = result.Replace($"src=\"{from}\"", $"src=\"{to}\"", StringComparison.Ordinal);
            result = result.Replace($"src='{from}'", $"src='{to}'", StringComparison.Ordinal);
        }

        // Private badges / failed downloads leave a broken <img> (oversized browser placeholder).
        // Swap those for a compact alt-text chip; outer <a> (if any) stays clickable.
        return RewriteUnembeddedRemoteImages(result);
    }

    private static readonly Regex RemoteImgTag = new(
        @"<img\b(?<attrs>[^>]*?)\bsrc\s*=\s*(?:""(?<src>https?://[^""]+)""|'(?<src>https?://[^']+)')(?<attrs2>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AltAttr = new(
        @"\balt\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Replaces remote <c>img</c> tags that were not embedded as data URIs with a small text chip
    /// (uses <c>alt</c>, e.g. Build for Actions badges) so Preview never shows a broken-image icon.
    /// </summary>
    internal static string RewriteUnembeddedRemoteImages(string html)
    {
        if (string.IsNullOrEmpty(html) || html.IndexOf("<img", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return html;
        }

        return RemoteImgTag.Replace(html, static match =>
        {
            var src = match.Groups["src"].Value;
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return match.Value;
            }

            var attrs = match.Groups["attrs"].Value + match.Groups["attrs2"].Value;
            var altMatch = AltAttr.Match(attrs);
            var label = altMatch.Success && !string.IsNullOrWhiteSpace(altMatch.Groups["v"].Value)
                ? altMatch.Groups["v"].Value.Trim()
                : "image";

            return $"<span class=\"markdown-img-fallback\" title=\"{HtmlAttributeEncode(src)}\">{HtmlEncode(label)}</span>";
        });
    }

    private static string HtmlEncode(string value) =>
        System.Net.WebUtility.HtmlEncode(value);

    private static string HtmlAttributeEncode(string value) =>
        System.Net.WebUtility.HtmlEncode(value).Replace("\"", "&quot;", StringComparison.Ordinal);

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

            var bytes = Convert.FromBase64String(result.ContentBase64);
            var contentType = string.IsNullOrWhiteSpace(result.ContentType)
                ? GuessContentTypeFromPath(repoRelativePath)
                : result.ContentType;
            return TryBuildDataUri(repoRelativePath, contentType, bytes);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to embed markdown image {Path}", repoRelativePath);
            return null;
        }
    }

    private async Task<string?> TryDownloadRemoteAsync(string url, string? githubBearerToken, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(nameof(MarkdownImageEmbedder));
            var bytes = await DownloadBytesAsync(client, url, bearerToken: null, cancellationToken);
            if (bytes is null && !string.IsNullOrWhiteSpace(githubBearerToken) && IsGitHubWebUrl(url))
            {
                // Private-repo badges (actions/workflows/*.svg) 404 anonymously; retry with connector token.
                bytes = await DownloadBytesAsync(client, url, githubBearerToken, cancellationToken);
            }

            if (bytes is null || bytes.Length == 0)
            {
                return null;
            }

            var contentType = GuessContentTypeFromPath(url);
            return TryBuildDataUri(url, contentType, bytes);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to download markdown image {Url}", url);
            return null;
        }
    }

    private static async Task<byte[]?> DownloadBytesAsync(
        HttpClient client,
        string url,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("image/svg+xml,image/*,*/*;q=0.8");
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var length = response.Content.Headers.ContentLength;
        if (length is > MaxRemoteBytes)
        {
            return null;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0 || bytes.Length > MaxRemoteBytes)
        {
            return null;
        }

        // Prefer server content-type when present; caller still validates via TryBuildDataUri.
        _ = mediaType;
        return bytes;
    }

    private static string? TryBuildDataUri(string sourceHint, string? contentType, byte[] bytes)
    {
        var resolvedType = ResolveImageContentType(sourceHint, contentType, bytes);
        if (resolvedType is null)
        {
            return null;
        }

        if (resolvedType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            if (bytes.Length > MaxSvgBytes || !IsSafeSvg(bytes))
            {
                return null;
            }
        }

        return $"data:{resolvedType};base64,{Convert.ToBase64String(bytes)}";
    }

    private static string? ResolveImageContentType(string sourceHint, string? contentType, byte[] bytes)
    {
        var type = contentType?.Split(';')[0].Trim();
        if (!string.IsNullOrWhiteSpace(type)
            && type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && !type.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            return type;
        }

        if ((!string.IsNullOrWhiteSpace(type) && type.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase))
            || sourceHint.Contains(".svg", StringComparison.OrdinalIgnoreCase)
            || LooksLikeSvg(bytes))
        {
            return "image/svg+xml";
        }

        if (!string.IsNullOrWhiteSpace(type) && type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return type;
        }

        return GuessContentTypeFromPath(sourceHint) is { } guessed && guessed.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? guessed
            : null;
    }

    private static bool LooksLikeSvg(byte[] bytes)
    {
        var probeLen = Math.Min(bytes.Length, 256);
        var probe = Encoding.UTF8.GetString(bytes, 0, probeLen);
        return probe.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeSvg(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        if (!text.Contains("<svg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !SvgDangerous.IsMatch(text);
    }

    private static string GuessContentTypeFromPath(string pathOrUrl)
    {
        var path = pathOrUrl;
        var q = path.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0)
        {
            path = path[..q];
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream",
        };
    }

    private static string? ResolveRepoRelativePath(string markdownDir, string src)
    {
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

    private static bool IsGitHubWebUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("camo.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeScheme(string src)
    {
        var colon = src.IndexOf(':');
        return colon > 0 && src.AsSpan(0, colon).ToString().All(static c => char.IsLetter(c));
    }
}
