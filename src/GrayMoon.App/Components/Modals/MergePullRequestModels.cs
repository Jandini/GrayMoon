using GrayMoon.App.Models;

namespace GrayMoon.App.Components.Modals;

/// <summary>Result emitted by the merge pull request modal when the user clicks the merge button.</summary>
public sealed record MergePullRequestChoice(MergeMethod Method, bool SyncToDefault);
