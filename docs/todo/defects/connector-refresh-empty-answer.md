# Connector removal deletes the repositories of its Workspaces

Status: partly built. The repository refresh part shipped on 2026-11-11; removing a connector is still open.

## Built

A repository refresh no longer removes a repository that a Workspace links. A connector that is inactive, failed, or answered with an empty list keeps every stored row, and an empty answer is reported. See `docs/architecture/02-system-architecture.md` (repository refresh) and `Repositories.MissingFromProviderSinceUtc` in `03-data-model-and-persistence.md`.

## Symptom (open)

Removing a connector deletes its repositories and, with them, their Workspace links, branches, pull request rows and context rows. It is refused only when a Feature uses one of the repositories ("Remove the Features that use this connector's repositories first.").

## Cause

`ConnectorRepository.DeleteAsync` removes the connector row, and the `Repositories.ConnectorId` foreign key is `OnDelete(DeleteBehavior.Cascade)` (`Data/AppDbContext.cs`).

## Owner decision (2026-11-11)

Connector state must never wipe a Workspace. Removing a connector is a soft delete, as long as the Workspace's repositories can be reassigned to a good connector. Repositories no Workspace links may still be deleted.

## Fix plan

Change 1b in [outstanding-defects-fix-plan](../outstanding-defects-fix-plan.md): nullable `Connectors.RemovedAtUtc`, a removed connector hidden from every list and never fetched, reassignment through the clone URL match of the merge, "Repository X has no valid connector" for work that needs a token, and "No connector" / "Not found on provider" states in the grid.

## Rule this falls under

`AGENTS.md`, "Destructive and merge operations", rules 1 and 2.
