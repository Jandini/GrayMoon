using GrayMoon.Common.Git;

namespace GrayMoon.App.Components.GitChanges;

public sealed record GitChangesStageEventArgs(int WorkspaceRepositoryId, GitChangeOperationScope Scope, IReadOnlyList<string> Paths);

public sealed record GitChangesCommitEventArgs(int WorkspaceRepositoryId, bool StageAllFirst);
