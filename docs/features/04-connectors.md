# Connectors

Route: `/connectors`

![Connectors](screenshots/connectors.png)

GitHub and NuGet credentials used to fetch repositories, packages, and GitHub Actions.

## Header

- Title: **Connectors**
- Subtitle: `N connector(s)` or `N connector(s) found`.
- [Filter search](shared.md#filter-search-shared) - placeholder "Search connectors…". Disabled when the list is empty.
- **Test Connectors** - tests every connector in parallel. Disabled while a test-all is running or when there are no connectors. Each row status becomes **Testing** then **OK** or **Error**.
- **Add Connector** (blue) - create modal.

## Search fields

Plain terms match **name**, **API base URL**, **user name**, **type** (GitHub/NuGet), and **status**.

Field prefixes:

- `type:` - GitHub or NuGet
- `status:` - Testing, Ok, Error, etc.

Empty match: "No connectors match your search."

Empty list: "No connectors found. Add your first connector to get started."

## Grid columns

| Column | What it shows | Interaction |
| --- | --- | --- |
| Name | Type icon + connector name | GitHub = octocat, NuGet = box |
| API Base URL | Endpoint | Display |
| User Name | Account associated with the token | Display (may be empty) |
| Active | Switch | On = used; turning on runs a test and turns back off if the test fails. Disabled while that row is busy |
| Status | Badge | Tooltip is the last error or "Connection OK." / "Testing connection..." / "Not tested yet." |
| Actions | **Test**, **Edit**, **Delete** | Disabled while that connector is testing or toggling |

### Status badges

| Status | Label | Meaning |
| --- | --- | --- |
| Testing | Testing | Connection check in progress |
| Ok | OK | Last test succeeded (green) |
| Error | Error | Last test failed (red). Tooltip = `LastError`. A connector-fault failure also deactivates the switch |
| anything else | Unknown | Not tested yet |

A used connector in Error is what turns the Home **Manage Connectors** button red.

## Add / Edit modal

Title **Add Connector** or **Edit Connector**. Large dialog.

- **Connector Name** * required
- **Connector Type** * dropdown: GitHub / NuGet (with icons). Changing type fills a default API URL.
- **API Base URL** * with suggestions: `https://api.github.com/`, `https://api.nuget.org/v3/index.json`, `https://nuget.pkg.github.com/`, plus URLs already used by other connectors.
- **User Name** - shown when the type/URL needs it.
- **User Token** - password field. Required except for public nuget.org. For GitHub, a **Create** button opens GitHub's "new classic PAT" page with scopes `repo,workflow,read:packages`.
- Validation errors inline. Submit shows **Adding...** / **Updating...** with a spinner.
- After save, the connector is tested automatically (status goes Testing then OK/Error).
- Escape cancels.

## Delete modal

"Are you sure you want to delete **{name}**?" Cancel / **Delete** (red). Enter confirms delete. Escape cancels.
