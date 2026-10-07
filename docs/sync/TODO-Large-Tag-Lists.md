# TODO: Large tag lists

Status: **TODO**. Out of scope for the sync read-overlap change. Keep the full tag list. Do not drop, cap, or delay tags to make sync faster.

## 1. Saving the list can reject a real tag

`RepositoryBranches` has a unique index on workspace, name, and remote flag (`AppDbContext`: `WorkspaceRepositoryId`, `BranchName`, `IsRemote`). The index does not include `IsTag`.

Git allows a tag and a local branch to share a name. Sync sends both with the remote flag false. `RepositoryBranchWriter.PersistAsync` inserts both rows, and SQLite rejects the second one.

`PersistAsync` does not catch that. `PersistVersionsAsync` then saves repositories one after another, so one collision stops the rest of the workspace. The version row for that repository is already committed by then, and its tags are not.

## 2. The tag tab renders every row

`SwitchBranchModal` builds a DOM node per tag with no windowing. A few thousand tags can stall the Blazor circuit while the data itself is fine.

Window the list in the dialog. Do not omit stored tags.
