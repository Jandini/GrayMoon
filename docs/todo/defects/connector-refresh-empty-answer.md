# Connector refresh: an empty answer deletes, and an all-empty refresh never purges

Status: not built. Both parts are traced in the code, not reproduced; confirm each with a failing test before fixing.

## Symptom

1. A connector that answers 200 with zero repositories while the database holds repositories for it counts as "answered". The merge then removes that connector's repositories with their links, branches and pull request rows. A token that lost its organization SSO grant or a scope answers exactly like this.
2. When every answering connector returns zero repositories, the merge is skipped, so repositories deleted on GitHub stay in the grid. This shows on single-connector setups.

## Cause

`Services/GitHub/GitHubRepositoryService.cs` runs `MergeRepositoriesAsync(allFetched, failedConnectorIds)` only when `allFetched.Count > 0`, and it treats a connector as failed only when its fetch returned an error message. "Answered with nothing" is not told apart from "answered with everything gone".

## Fix plan

1. Treat "0 returned, more than 0 stored for that connector" as skipped, with the message "Connector X returned no repositories; its repositories were left unchanged", unless the user confirms the removal. Add the connector to `failedConnectorIds` for that run.
2. After step 1 is in, run the merge for the connectors that answered even when the total is zero.

Do step 1 first: step 2 alone turns the quiet stale-rows bug into data loss.

## Tests

- Fan-out test with a connector that has stored repositories, links, branches and pull request rows and returns `repoCount: 0`; assert the rows survive and the result names the connector.
- A connector with no stored rows that returns zero stays a normal, silent success.
- After a confirmed removal, the stored rows are purged.

## Rule this falls under

`AGENTS.md`, "Destructive and merge operations", rule 1.
