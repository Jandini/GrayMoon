using GrayMoon.App.Models;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Components.Pages;

/// <summary>
/// What a repository row shows for a GitHub SSO refusal. Labels are plain text. The authorization URL is
/// validated again here and is not part of <see cref="VisibleText"/>.
/// </summary>
public sealed record RepositorySsoPresentation(string? OAuthApplication, string? Organization, string? AuthorizationUrl)
{
    public string Heading => GitHubSsoAuthorization.Heading;

    public string Explanation => GitHubSsoAuthorization.BuildSsoExplanation(OAuthApplication, Organization);

    /// <summary>Every word rendered as text. Does not include <see cref="AuthorizationUrl"/>.</summary>
    public string VisibleText => Heading + " " + Explanation;

    public bool ShowsAuthorize => AuthorizationUrl != null;

    public static RepositorySsoPresentation? TryCreate(bool ssoRequired, string? application, string? organization, string? authorizationUrl)
    {
        if (!ssoRequired)
            return null;

        return new RepositorySsoPresentation(
            GitHubSsoAuthorization.CleanLabel(application),
            GitHubSsoAuthorization.CleanLabel(organization),
            GitHubSsoAuthorization.TryValidateAuthorizationUrl(authorizationUrl));
    }
}

/// <summary>
/// Replaces one repository's sync error instead of appending, and drops it when the attempt succeeded with no message.
/// </summary>
public static class RepositorySyncAlert
{
    public static bool ShouldApply(int jobGeneration, int currentGeneration)
        => jobGeneration == currentGeneration;

    public static void Apply(
        Dictionary<int, string> errors,
        Dictionary<int, RepositorySsoPresentation> sso,
        int repositoryId,
        RepoGitVersionInfo info)
    {
        var presentation = RepositorySsoPresentation.TryCreate(
            info.SsoAuthorizationRequired,
            info.SsoOAuthApplication,
            info.SsoOrganization,
            info.SsoAuthorizationUrl);

        if (!info.FatalSyncFailure && string.IsNullOrWhiteSpace(info.ErrorMessage) && presentation == null)
        {
            errors.Remove(repositoryId);
            sso.Remove(repositoryId);
            return;
        }

        if (string.IsNullOrWhiteSpace(info.ErrorMessage))
            errors.Remove(repositoryId);
        else
            errors[repositoryId] = info.ErrorMessage.Trim();

        if (presentation == null)
            sso.Remove(repositoryId);
        else
            sso[repositoryId] = presentation;
    }
}
