# What happens when a token is not SSO authorized

This describes the behavior built for failed clones and GitHub SAML SSO on Workspace Repositories. Sample names below are fictional (`acme-labs`).

## Short answer

One connector token that GitHub has not authorized for SSO does **not** open 30 browser pages for 30 repositories.

GrayMoon still tries every repository. Each repository GitHub refuses gets its own row on Workspace Repositories: status **not cloned** or **error**, the sentence **GitHub SSO authorization required**, and **Retry**. **Authorize SSO** is a link on that row. Nothing opens a browser until the user clicks that link. One click opens one GitHub tab. Authorizing that OAuth application for the organization once covers the token. The user then retries the rows. They do not authorize 30 times.

## What was built

Before this change, a failed `git clone` could leave the row looking in sync and show no reason. The Worker logged the raw git output, including the sensitive `authorization_request` value.

Now a failed clone stays failed. The row shows a short reason. A GitHub SAML SSO refusal is recognized as its own case, separate from a generic HTTP 403, a bad token, a missing repository, a network failure, or a certificate problem. The authorization secret is removed from the sentence, the logs, and saved state.

The App and the Worker ship together. Desktop itself did not change.

## One token and 30 repositories

Sync sends the same connector token with every repository. Repositories run together, up to the usual parallel limit. One SSO refusal does not cancel the others.

| Situation | What the user sees |
|---|---|
| All 30 are not cloned yet, and all 30 are in the same SSO organization | 30 rows, each **not cloned**, each with the same SSO sentence and its own **Authorize SSO** and **Retry**. No browser window opens by itself. |
| The user clicks **Authorize SSO** on one row | One new browser tab, the exact `https://github.com` link GitHub returned for that attempt. |
| The user clicks **Authorize SSO** on every row | One new tab per click. GitHub still treats those as the same application and organization. One completed authorization is enough. |
| The user finishes authorization in GitHub, then clicks **Retry** or runs Sync again | Each retried repository clones with the same token. A successful retry clears that row's sentence. |
| Some repositories are already cloned | Those go through fetch, not clone. See below. |
| Some repositories are outside the SSO organization | Those can succeed while the protected ones stay failed. The Workspace is not in sync while any row failed. |

There is no single Workspace dialog that stands in for every row. The sentence is repeated once per failed repository so each row stays honest about its own clone.

## What the user sees on a failed clone

On Workspace Repositories, under the repository that failed:

1. The status badge is **not cloned**.
2. The heading is **GitHub SSO authorization required**.
3. The sentence names the OAuth application and organization GitHub reported, for example: reauthorize Visual Studio to access acme-labs. It tells the user to finish authorization in the browser, then retry cloning.
4. **Authorize SSO** appears only when GitHub supplied a link that is exactly `https://github.com`, on the default port, with a path of `/orgs/{slug}/sso` or `/enterprises/{slug}/sso`. The link opens in a new tab and does not send a referrer.
5. **Retry** runs the existing per-repository Sync.

The `authorization_request` value is not written in the row.

A link that is not that GitHub SSO shape is dropped. The sentence still appears. **Authorize SSO** does not.

## What GrayMoon does, step by step

1. Workspace Repositories Sync asks the Worker to sync each repository, and includes that repository's connector token.
2. If the folder is not already a git repository, the Worker runs `git clone`.
3. GitHub answers that the organization has enforced SAML SSO and that the OAuth application must be reauthorized. Git exits with a failure. The output often also says HTTP 403.
4. The Worker does not retry that failure. An SSO refusal is final even when the same output also looks like a temporary network error.
5. The Worker classifies the output. SSO wins over the generic 403 message. A 403 that does not mention SAML SSO stays "Git could not access this repository (HTTP 403)."
6. The safe GitHub URL is kept beside the message, not inside it. Logs and the command overlay see `authorization_request=redacted`.
7. The Worker tells the App the clone failed, the repository is not cloned, the failure kind is SSO, and the application, organization, and URL when they are safe.
8. The App does not mark the row in sync. The previous branch and version stay. The Workspace is not in sync.
9. The page shows the sentence and the buttons for that repository only.
10. An empty folder or a folder that contains only `.git`, created by this clone, is removed so Retry can clone again. A folder that already had other files is left in place, and the sentence says so.

## After the user authorizes

GitHub authorization is for the OAuth application and the organization, not for one repository. Completing it once lets the same token clone the other repositories in that organization.

GrayMoon does not watch the browser tab and does not start the clones by itself. The user clicks **Retry** on a row, or runs Sync again. Each repository is tried on its own. A later failure replaces that row's sentence. A success clears it. Other rows keep their own sentences until they succeed.

## Repositories that are already cloned

A later fetch can also be refused for SSO. The row then shows **error** (the checkout is already there) and the SSO heading, plus **Retry**.

**Authorize SSO** is only attached on clone and clone-into. Fetch returns the explanation as text and does not carry the GitHub link, so those rows have no **Authorize SSO** button. Use a row that failed during clone, or authorize from GitHub directly, then retry.

Pull, push, and other remote git commands use the same token. Their failure text is classified the same way when it is shown, and the secret is redacted. The button on Workspace Repositories is the clone path.

## What is remembered

| Kept | Dropped |
|---|---|
| **not cloned** or **error** on the row, including after a full page reload | The GitHub URL |
| The failure sentence for the rest of this page session | The `authorization_request` secret |
| | The sentence and **Authorize SSO** after a full reload, a context switch, a successful retry, or a newer error on that row |

The URL is checked again in the page before it becomes a link. It is not written to the database.

A reload still shows the failed status. The sentence and the button come back the next time that repository's sync fails.

## What this does not do

- It does not open GitHub on its own.
- It does not collapse 30 rows into one prompt.
- It does not stop the rest of the Sync when the first repository fails.
- It does not call the GitHub API to preflight SSO before clone.
- It does not change how the token is stored or how git is authenticated.
- It does not treat a plain 403, 401, 404, network error, or certificate error as SSO.
